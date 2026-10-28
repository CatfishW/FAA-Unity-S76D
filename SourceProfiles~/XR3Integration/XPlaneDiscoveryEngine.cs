using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FAA.XPlaneIntegration.Runtime
{
    public sealed class XPlaneDiscoveryEngine:IDisposable
    {
        internal const int RetryMilliseconds=12000;
        internal const int InventoryMilliseconds=30000;
        private readonly CancellationTokenSource cancel=new();
        private readonly Dictionary<string,XPlaneDiscoveredFrame> pending=new(StringComparer.Ordinal);
        // Supervisors retain endpoint ownership during backoff; started contains active attempts only.
        private readonly HashSet<string> workers=new(StringComparer.Ordinal);
        private readonly HashSet<string> started=new(StringComparer.Ordinal);
        private readonly Dictionary<string,string> diagnostics=new(StringComparer.Ordinal);
        private readonly object sync=new();
        private readonly XPlaneSourceConfig config;
        private readonly HashSet<string> localAddresses=new(){"127.0.0.1","::1"};
        public string InventoryStatus{get;private set;}="Checking local simulator...";
        public string[] EndpointDiagnostics
        {
            get{lock(sync)return diagnostics.OrderBy(p=>DiagnosticPriority(p.Key)).ThenBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Value).ToArray();}
        }
        // Native Web diagnostics remain visible when a lower-priority UDP/MQTT probe also fails.
        public string LastDiagnostic=>string.Join(" | ",EndpointDiagnostics.Take(3));
        public XPlaneLocalInventory Inventory{get;private set;}
        public Exception BackgroundFailure{get;private set;}
        private bool disposed;
        public XPlaneDiscoveryEngine(XPlaneSourceConfig configuration,string home,string localAppData):this(configuration,home,localAppData,true){ }
        internal XPlaneDiscoveryEngine(XPlaneSourceConfig configuration,string home,string localAppData,bool startDiscovery)
        {
            config=configuration;
            if(!startDiscovery)return;
            try{foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces())foreach(var address in adapter.GetIPProperties().UnicastAddresses)localAddresses.Add(address.Address.ToString());}catch(NetworkInformationException){ }
            ObserveBackgroundTask(Task.Run(()=>RunInventoryLoop(
                ()=>ScanAndStart(home,localAppData),StartLocalDefaults,
                message=>{InventoryStatus=message;Diagnostic("inventory",message);},Task.Delay,cancel.Token)),"inventory");
            foreach(string target in config.udpEndpoints)if(XPlaneSourceConfig.TryUdpEndpoint(target,out var endpoint))StartUdp(endpoint);
            foreach(string endpoint in config.webEndpoints)StartWeb(endpoint.TrimEnd('/'));
            foreach(var broker in config.mqttBrokers)StartMqtt(broker);
            if(config.observeLocalBeacons)Launch("beacon",ObserveBeacons);
        }
        private void StartLocalDefaults()
        {
            // Process/installation metadata must never gate the standard loopback probes.
            if(config.discoverLocalWeb)StartWeb("http://127.0.0.1:8086");
            if(config.discoverLocalUdp)StartUdp(new IPEndPoint(IPAddress.Loopback,49000));
        }
        private void ScanAndStart(string home,string localAppData)
        {
            var inventory=XPlaneLocalInventory.Scan(home,localAppData,config.installPaths);Inventory=inventory;InventoryStatus=inventory.Summary;
            lock(sync)diagnostics.Remove("inventory");
            if(config.discoverLocalUdp)foreach(int port in inventory.UdpPorts)StartUdp(new IPEndPoint(IPAddress.Loopback,port));
            if(config.discoverLocalWeb)foreach(int port in inventory.WebPorts)StartWeb("http://127.0.0.1:"+port);
            if(config.mqttBrokers.Any(b=>XPlaneSourceConfig.IsLoopback(b.host)))foreach(int port in inventory.MqttPorts)
                StartMqtt(new XPlaneMqttDiscoveryConfig{host="127.0.0.1",port=port});
            foreach(int port in inventory.OutputPorts.Concat(config.udpListenPorts).Distinct())
                Launch("data:"+port,diagnostic=>XPlaneDiscoveryUdp.RunData(port,localAddresses,Publish,diagnostic,cancel.Token));
        }
        internal static async Task RunInventoryLoop(Action scanAndStart,Action startDefaults,Action<string> diagnostic,Func<int,CancellationToken,Task> delay,CancellationToken token)
        {
            while(!token.IsCancellationRequested)
            {
                int wait=InventoryMilliseconds;
                try
                {
                    startDefaults();
                    token.ThrowIfCancellationRequested();
                    scanAndStart();
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                catch(Exception e) when(IsRecoverable(e))
                {
                    wait=RetryMilliseconds;
                    diagnostic("Local inventory [scan] "+e.GetType().Name+"; default probes continue; retry in 12s.");
                }
                if(token.IsCancellationRequested)break;
                try{await delay(wait,token).ConfigureAwait(false);}catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            }
        }
        private void Launch(string key,Func<Action<string>,Task> run)
        {
            lock(sync){if(disposed||cancel.IsCancellationRequested||workers.Count>=24||!workers.Add(key))return;}
            ObserveBackgroundTask(Task.Run(()=>RunWorkerLoop(key,()=>run(message=>Diagnostic(key,message)),active=>
            {lock(sync){if(active)started.Add(key);else started.Remove(key);}},message=>Diagnostic(key,message),Task.Delay,cancel.Token)),key);
        }
        internal static async Task RunWorkerLoop(string key,Func<Task> run,Action<bool> active,Action<string> diagnostic,Func<int,CancellationToken,Task> delay,CancellationToken token)
        {
            while(!token.IsCancellationRequested)
            {
                active(true);
                try
                {
                    token.ThrowIfCancellationRequested();
                    await run().ConfigureAwait(false);
                    if(!token.IsCancellationRequested)diagnostic(key+" [worker] exited unexpectedly; retry in 12s.");
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
                catch(Exception e) when(IsRecoverable(e))
                {if(!token.IsCancellationRequested)diagnostic(key+" [worker] "+e.GetType().Name+"; retry in 12s.");}
                finally{active(false);}
                if(token.IsCancellationRequested)break;
                try{await delay(RetryMilliseconds,token).ConfigureAwait(false);}catch(OperationCanceledException) when(token.IsCancellationRequested){break;}
            }
        }
        internal static bool IsRecoverable(Exception e)
        {
            if(e is OutOfMemoryException||e is StackOverflowException||e is AccessViolationException||e is System.Runtime.InteropServices.SEHException||e is ThreadAbortException||e is AppDomainUnloadedException||e is CannotUnloadAppDomainException)return false;
            if(e is AggregateException aggregate)return aggregate.InnerExceptions.All(IsRecoverable);
            return e.InnerException==null||IsRecoverable(e.InnerException);
        }
        private void ObserveBackgroundTask(Task task,string key)
        {
            // Observe faults without converting fatal/corrupted-state exceptions into retry loops.
            // The registered supervisor remains stopped after a fatal fault until explicit rescan.
            _=task.ContinueWith(failed=>
            {
                var failure=failed.Exception.GetBaseException();BackgroundFailure=failure;
                Diagnostic(key,key+" [worker stopped] "+failure.GetType().Name+"; explicit rescan required.");
            },CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        }
        private void StartMqtt(XPlaneMqttDiscoveryConfig broker)=>Launch("mqtt:"+broker.host+":"+broker.port,diagnostic=>XPlaneDiscoveryMqtt.Run(broker,Publish,diagnostic,cancel.Token));
        private void StartUdp(IPEndPoint endpoint)=>Launch("rref:"+endpoint,diagnostic=>XPlaneDiscoveryUdp.RunRref(endpoint,Publish,diagnostic,cancel.Token));
        private void StartWeb(string endpoint)=>Launch("web:"+endpoint,diagnostic=>XPlaneDiscoveryWeb.Run(endpoint,Publish,diagnostic,cancel.Token));
        private void Publish(XPlaneDiscoveredFrame frame)
        {lock(sync){if(!disposed&&(pending.Count<32||pending.ContainsKey(frame.Id)))pending[frame.Id]=frame;}}
        private static int DiagnosticPriority(string key)=>key.StartsWith("web:",StringComparison.Ordinal)?0:key=="inventory"?1:2;
        private void Diagnostic(string key,string message)
        {lock(sync){if(!disposed&&(diagnostics.Count<26||diagnostics.ContainsKey(key)))diagnostics[key]=XPlaneDiscoveryData.SafeLabel(message,220);}}
        public XPlaneDiscoveredFrame[] Drain()
        {lock(sync){var frames=pending.Values.ToArray();pending.Clear();return frames;}}
        private async Task ObserveBeacons(Action<string> diagnostic)
        {
            while(!cancel.IsCancellationRequested)
            {
                try
                {
                    using(var socket=new UdpClient())
                    using(cancel.Token.Register(()=>socket.Close()))
                    {
                        socket.ExclusiveAddressUse=false;socket.Client.SetSocketOption(SocketOptionLevel.Socket,SocketOptionName.ReuseAddress,true);
                        socket.Client.Bind(new IPEndPoint(IPAddress.Any,49707));socket.JoinMulticastGroup(IPAddress.Parse("239.255.1.1"));
                        while(!cancel.IsCancellationRequested)
                        {
                            var packet=await socket.ReceiveAsync();
                            // Multicast reception is passive. Unknown LAN hosts are never selected automatically.
                            if(localAddresses.Contains(packet.RemoteEndPoint.Address.ToString())&&XPlaneDiscoveryUdp.ParseBeacon(packet.Buffer,out int port,out _))
                                StartUdp(new IPEndPoint(IPAddress.Loopback,port));
                        }
                    }
                }
                catch(Exception e) when(e is SocketException||e is ObjectDisposedException||e is OperationCanceledException)
                {if(cancel.IsCancellationRequested)break;diagnostic("Local beacon listener unavailable; direct loopback probes continue.");}
                try{await Task.Delay(15000,cancel.Token);}catch(OperationCanceledException){break;}
            }
        }
        public void Dispose()
        {
            lock(sync){if(disposed)return;disposed=true;pending.Clear();}
            cancel.Cancel();
            // Workers own disposal of sockets/clients; do not block the Unity render thread.
        }
    }
}
