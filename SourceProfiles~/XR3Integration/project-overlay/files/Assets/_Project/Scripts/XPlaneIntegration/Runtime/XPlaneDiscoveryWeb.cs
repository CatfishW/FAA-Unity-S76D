using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FAA.XPlaneIntegration.Runtime
{
    public static class XPlaneDiscoveryWeb
    {
        public static async Task Run(string endpoint,Action<XPlaneDiscoveredFrame> publish,Action<string> diagnostic,CancellationToken token)
        {
            var uri=new Uri(endpoint);bool local=XPlaneSourceConfig.IsLoopback(uri.Host);
            string lastStatus=null;
            void Status(string stage,string detail)
            {
                string message="Native Web API "+uri.Authority+" ["+stage+"] "+detail;
                if(message==lastStatus)return;lastStatus=message;diagnostic(message);
            }
            while(!token.IsCancellationRequested)
            {
                string stage="identity";
                try
                {
                    Status(stage,"probing X-Plane identity");
                    using(var handler=new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false})
                    using(var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(2),MaxResponseContentBufferSize=131072})
                    {
                        http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                        string[] names=XPlaneDiscoveryData.Subscriptions.Select(n=>n.Split('[')[0]).Distinct().ToArray();
                        // Fail one cheap identity lookup before issuing the bounded catalog requests.
                        using(var probe=await http.GetAsync(endpoint+"/api/v1/datarefs?filter%5Bname%5D=sim%2Fversion%2Fxplane_internal_version&limit=1",token))
                        {
                            if(!probe.IsSuccessStatusCode)stage="identity HTTP "+(int)probe.StatusCode;
                            probe.EnsureSuccessStatusCode();
                            string identity=await probe.Content.ReadAsStringAsync();
                            if(!identity.Contains("sim/version/xplane_internal_version"))throw new InvalidDataException("Not an X-Plane dataref API.");
                        }
                        stage="catalog";Status(stage,"resolving required flight datarefs");
                        var ids=new Dictionary<string,string>(StringComparer.Ordinal);var known=new HashSet<string>(names,StringComparer.Ordinal);
                        // Exact filters discover session-specific IDs; never reuse IDs across simulator restarts.
                        // Native API 1 is supported by every version exposing the Web API.
                        using(var lookupTimeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                        using(var gate=new SemaphoreSlim(4))
                        {
                            lookupTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                            var tasks=names.Select(async name=>
                            {
                                await gate.WaitAsync(lookupTimeout.Token);
                                try
                                {
                                    using(var response=await http.GetAsync(endpoint+"/api/v1/datarefs?filter%5Bname%5D="+Uri.EscapeDataString(name)+"&limit=4",lookupTimeout.Token))
                                    {
                                        if(response.StatusCode==HttpStatusCode.NotFound)return;
                                        if(!response.IsSuccessStatusCode)stage="catalog HTTP "+(int)response.StatusCode;
                                        response.EnsureSuccessStatusCode();
                                        string text=await response.Content.ReadAsStringAsync();
                                        using(var reader=new JsonTextReader(new StringReader(text)){MaxDepth=6})
                                        {
                                            var root=JObject.Load(reader);if(!(root["data"] is JArray rows))return;
                                            foreach(JObject row in rows.OfType<JObject>().Take(4))
                                                if(row.Value<string>("name")==name&&row["id"]?.Type==JTokenType.Integer)
                                                    lock(ids)ids[row["id"].ToString()]=name;
                                        }
                                    }
                                }
                                finally{gate.Release();}
                            }).ToArray();
                            await Task.WhenAll(tasks);
                        }
                        if(!XPlaneDiscoveryData.Core.All(n=>ids.Values.Contains(n))||!ids.Values.Contains(XPlaneDiscoveryData.Clock)||!ids.Values.Contains(XPlaneDiscoveryData.Version))
                            throw new InvalidDataException("Native API lacks required flight identity/datarefs.");
                        stage="websocket connect";Status(stage,"resolved "+ids.Count+" datarefs; connecting");
                        using(var socket=new ClientWebSocket())
                        using(var lifecycle=CancellationTokenSource.CreateLinkedTokenSource(token))
                        {
                            socket.Options.Proxy=null;socket.Options.KeepAliveInterval=TimeSpan.FromSeconds(5);
                            lifecycle.CancelAfter(TimeSpan.FromSeconds(4));
                            var ws=new UriBuilder(uri){Scheme=uri.Scheme=="https"?"wss":"ws",Path=uri.AbsolutePath.TrimEnd('/')+"/api/v1"};
                            await socket.ConnectAsync(ws.Uri,lifecycle.Token);lifecycle.CancelAfter(Timeout.Infinite);
                            stage="subscribe";
                            var request=new JObject{["req_id"]=1,["type"]="dataref_subscribe_values",["params"]=new JObject{["datarefs"]=new JArray(ids.Keys.Select(id=>(JToken)new JObject{["id"]=long.Parse(id,CultureInfo.InvariantCulture)}))}};
                            byte[] bytes=Encoding.UTF8.GetBytes(request.ToString(Formatting.None));await socket.SendAsync(new ArraySegment<byte>(bytes),WebSocketMessageType.Text,true,token);
                            stage="stream read";Status(stage,"waiting for validated flight data");
                            var values=new Dictionary<string,double>(StringComparer.Ordinal);var timestamps=new Dictionary<string,double>(StringComparer.Ordinal);double lastClock=-1;byte[] buffer=new byte[131072];
                            try
                            {
                            while(!token.IsCancellationRequested&&socket.State==WebSocketState.Open)
                            {
                                int length=0;WebSocketReceiveResult result;
                                using(var readTimeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                                {
                                    readTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                                    do
                                    {
                                        if(length==buffer.Length)throw new InvalidDataException("Oversized native stream frame.");
                                        result=await socket.ReceiveAsync(new ArraySegment<byte>(buffer,length,buffer.Length-length),readTimeout.Token);length+=result.Count;
                                        if(result.MessageType!=WebSocketMessageType.Text)throw new InvalidDataException("Native stream closed/non-text.");
                                    }while(!result.EndOfMessage);
                                }
                                JObject update;using(var reader=new JsonTextReader(new StringReader(Encoding.UTF8.GetString(buffer,0,length))){MaxDepth=8})update=JObject.Load(reader);
                                if(update.Value<string>("type")=="result"&&update["success"]?.Value<bool>()==false)throw new InvalidDataException("Subscription rejected.");
                                if(update.Value<string>("type")!="dataref_update_values"||!(update["data"] is JObject data))continue;
                                var delta=new Dictionary<string,double>(StringComparer.Ordinal);var invalid=new List<string>();
                                foreach(var item in data.Properties().Take(XPlaneDiscoveryData.MaxValues))
                                    if(ids.TryGetValue(item.Name,out string path))
                                    {
                                        var reported=new Dictionary<string,double>(StringComparer.Ordinal);
                                        XPlaneDiscoveryData.Flatten(path,item.Value,reported);
                                        if(item.Value is JArray array)
                                        {
                                            int limit=path=="sim/cockpit2/tcas/targets/icao_type"?160:20;
                                            for(int i=0;i<Math.Min(array.Count,limit);i++)if(!reported.ContainsKey(path+"["+i+"]"))invalid.Add(path+"["+i+"]");
                                        }
                                        else if(reported.Count==0)invalid.Add(path);
                                        foreach(var pair in reported)delta[pair.Key]=pair.Value;
                                    }
                                XPlaneDiscoveryData.MergeNativeTrafficDelta(values,timestamps,delta,invalid,XPlaneDiscoveryData.Now);
                                // Unchanged datarefs are valid until this websocket session ends; native API sends deltas.
                                bool paused=values.TryGetValue("sim/time/paused",out double pause)&&pause==1;
                                if(!values.TryGetValue(XPlaneDiscoveryData.Clock,out double clock)||!paused&&clock<=lastClock||!values.TryGetValue(XPlaneDiscoveryData.Version,out double version)||version<120000||version>=130000||!XPlaneDiscoveryData.CoreValid(values))
                                {Status("flight validation",WaitingReason(values,lastClock));continue;}
                                lastClock=clock;
                                double now=XPlaneDiscoveryData.Now;
                                Status("live",paused?"validated paused flight data":"validated advancing flight data");
                                publish(new XPlaneDiscoveredFrame{Id="native-web:"+endpoint,Label=(local?"LOCAL X-PLANE WEB ":"CONFIGURED X-PLANE WEB ")+uri.Authority+(paused?" [PAUSED]":""),Transport="Native Web API",Priority=local?110:85,Received=now,Values=XPlaneDiscoveryData.FreshTrafficPose(values,timestamps,now),Signature=paused?"paused:"+now.ToString("R",CultureInfo.InvariantCulture):"clock:"+clock.ToString("R",CultureInfo.InvariantCulture),LocalProcessVerified=true});
                            }
                            }
                            finally{XPlaneDiscoveryData.ClearNativeTrafficSession(values,timestamps);}
                        }
                    }
                }
                catch(Exception e) when(e is HttpRequestException||e is WebSocketException||e is IOException||e is JsonException||e is OperationCanceledException||e is ObjectDisposedException||e is FormatException)
                {if(token.IsCancellationRequested)break;Status(stage,e.GetType().Name+"; retry in 12s");}
                try{await Task.Delay(12000,token);}catch(OperationCanceledException){break;}
            }
        }
        internal static string WaitingReason(Dictionary<string,double> values,double lastClock)
        {
            if(!values.TryGetValue(XPlaneDiscoveryData.Clock,out double clock))return "awaiting simulator clock";
            bool paused=values.TryGetValue("sim/time/paused",out double pause)&&pause==1;
            if(!paused&&clock<=lastClock)return "awaiting advancing simulator clock";
            if(!values.TryGetValue(XPlaneDiscoveryData.Version,out double version)||version<120000||version>=130000)return "awaiting valid X-Plane 12 identity";
            return "awaiting complete valid flight core";
        }
    }
}
