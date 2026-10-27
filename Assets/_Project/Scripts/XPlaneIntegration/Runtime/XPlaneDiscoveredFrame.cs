using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FAA.XPlaneIntegration.Runtime
{
    public sealed class XPlaneDiscoveredFrame
    {
        public string Id,Label,Transport,Topic;
        public int Priority;
        public double Received;
        public Dictionary<string,double> Values;
        public string Signature;
        public bool LocalProcessVerified;
    }
    public static class XPlaneDiscoveryData
    {
        public const string Prefix="sim/flightmodel/position/";
        public const string Clock="sim/time/total_running_time_sec";
        public const int MaxValues = 768;
        public const string Version="sim/version/xplane_internal_version";
        public static readonly string[] Core={Prefix+"latitude",Prefix+"longitude",Prefix+"elevation",Prefix+"theta",Prefix+"phi",Prefix+"psi",Prefix+"indicated_airspeed",Prefix+"groundspeed",Prefix+"vh_ind"};
        public static readonly string[] Subscriptions=Core.Concat(new[]{
            Clock,Version,"sim/time/paused",
            "sim/flightmodel/engine/ENGN_TRQ[0]","sim/flightmodel/engine/ENGN_TRQ[1]",
            "sim/aircraft/controls/acf_trq_max_eng",Prefix+"hpath",Prefix+"y_agl",Prefix+"true_airspeed",Prefix+"mag_psi","sim/flightmodel/forces/g_side",
            "sim/aircraft/engine/acf_num_engines","sim/flightmodel/engine/ENGN_driv_TRQ[0]","sim/flightmodel/engine/ENGN_driv_TRQ[1]",
            "sim/flightmodel/engine/POINT_max_TRQ[0]","sim/flightmodel/engine/POINT_max_TRQ[1]",
            "sim/cockpit2/engine/indicators/prop_speed_rpm[0]","sim/cockpit2/engine/indicators/prop_speed_rpm[1]",
            "sim/aircraft/controls/acf_RSC_redline_prp","sim/cockpit2/engine/indicators/N1_percent[0]","sim/cockpit2/engine/indicators/N2_percent[0]",
            "sim/cockpit2/engine/indicators/N1_percent[1]","sim/cockpit2/engine/indicators/N2_percent[1]",
            "sim/cockpit/autopilot/autopilot_state","sim/cockpit2/autopilot/flight_director_mode","sim/cockpit/autopilot/autopilot_mode",
            // Selected heading for the heading-tape bug (degrees magnetic; the second is the legacy fallback) and the baro altimeter
            // (read-only, offset-applied to the geometric altitude by XPlane12ApiHudBridge.TryGetIndicatedAltitudeFeet).
            "sim/cockpit2/autopilot/heading_dial_deg_mag_pilot","sim/cockpit/autopilot/heading_mag","sim/cockpit2/gauges/indicators/altitude_ft_pilot",
            "sim/cockpit2/radios/nav1_has_glideslope","sim/cockpit2/radios/indicators/nav1_hdef_dots_pilot","sim/cockpit2/radios/indicators/nav1_vdef_dots_pilot",
            "sim/weather/aircraft/wind_speed_kt","sim/weather/aircraft/wind_direction_deg","sim/weather/aircraft/barometer_sealevel_inhg",
            "sim/weather/aircraft/ambient_temperature_c","sim/weather/visibility_reported_m","sim/weather/aircraft/precipitation_on_aircraft_ratio"
        }).Concat(Enumerable.Range(1,19).SelectMany(i => new[]{"lat","lon","el","psi","the","phi","v_x","v_y","v_z"}
            .Select(field => "sim/multiplayer/position/plane"+i+"_"+field)))
            .Concat(Enumerable.Range(0,160).Select(i => "sim/cockpit2/tcas/targets/icao_type["+i+"]"))
            .Concat(Enumerable.Range(0,20).Select(i => "sim/cockpit2/tcas/targets/modeS_id["+i+"]"))
            .Concat(new[]{"sim/operation/override/override_TCAS"}).Distinct().ToArray();
        public static double Now=>System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;
        public static bool Finite(double v)=>!double.IsNaN(v)&&!double.IsInfinity(v);
        public static bool CoreValid(IDictionary<string,double> values)
        {
            if(values==null||Core.Any(k=>!values.TryGetValue(k,out double v)||!Finite(v)))return false;
            return Math.Abs(values[Core[0]])<=90&&Math.Abs(values[Core[1]])<=180&&values[Core[2]]>=-1500&&values[Core[2]]<=100000&&
                Math.Abs(values[Core[3]])<=90&&Math.Abs(values[Core[4]])<=360&&values[Core[5]]>=-360&&values[Core[5]]<=720&&
                Math.Abs(values[Core[6]])<=2000&&values[Core[7]]>=0&&values[Core[7]]<=1200&&Math.Abs(values[Core[8]])<=1000;
        }
        public static bool Number(JToken value,out double number)
        {
            number=0;if(value==null)return false;
            if(value.Type==JTokenType.Integer||value.Type==JTokenType.Float)number=value.Value<double>();
            else if(value.Type==JTokenType.String&&!double.TryParse(value.Value<string>(),NumberStyles.Float,CultureInfo.InvariantCulture,out number))return false;
            else if(value.Type!=JTokenType.String)return false;
            return Finite(number);
        }
        public static void Flatten(string path,JToken token,Dictionary<string,double> destination)
        {
            if(!path.StartsWith("sim/",StringComparison.Ordinal)||path.Length>240||destination.Count>=MaxValues)return;
            if(path=="sim/cockpit2/tcas/targets/icao_type"&&token?.Type==JTokenType.String)
            {
                string encoded=token.Value<string>();if(encoded.Length>1024)return;
                try
                {
                    byte[] bytes=Convert.FromBase64String(encoded);
                    if(bytes.Length>512)return;
                    for(int i=0;i<Math.Min(bytes.Length,160)&&destination.Count<MaxValues;i++)destination[path+"["+i+"]"]=bytes[i];
                }
                catch(FormatException){ }
                return;
            }
            if(token is JArray array)
            {
                for(int i=0;i<Math.Min(array.Count,path=="sim/cockpit2/tcas/targets/icao_type"?160:20)&&destination.Count<MaxValues;i++)if(Number(array[i],out double value))destination[path+"["+i+"]"]=value;
            }
            else if(Number(token,out double scalar))destination[path]=scalar;
        }
        public static bool DecodeJson(string json,out Dictionary<string,double> values,out string signature)
        {
            values=null;signature=null;
            if(string.IsNullOrWhiteSpace(json)||json.Length>131072)return false;
            try
            {
                JObject root;
                using(var reader=new JsonTextReader(new StringReader(json)){MaxDepth=8,DateParseHandling=DateParseHandling.None})root=JObject.Load(reader);
                var health=root["health"] as JObject;
                if(health!=null&&(health.Value<string>("status")!="ok"||health["last_packet_age_sec"]!=null&&(!Number(health["last_packet_age_sec"],out double reportedAge)||reportedAge<0||reportedAge>1)))return false;
                JToken stamp=root["timestamp_utc"]??root["timestamp"]??root["snapshot_utc"]??health?["last_update_utc"];
                if(stamp!=null)
                {
                    if(stamp.Type==JTokenType.String&&DateTimeOffset.TryParse(stamp.Value<string>(),CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var time))
                    {double age=(DateTimeOffset.UtcNow-time).TotalSeconds;if(age>3||age< -30)return false;signature=stamp.ToString();}
                    else if(Number(stamp,out double timestamp)&&timestamp>1000000000)
                    {double seconds=timestamp>100000000000?timestamp/1000:timestamp;double age=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()/1000.0-seconds;if(age>3||age< -30)return false;signature=stamp.ToString();}
                }
                values=new Dictionary<string,double>(StringComparer.Ordinal);
                JObject raw=root["raw"] as JObject??root["datarefs"] as JObject??root;
                foreach(var property in raw.Properties().Take(MaxValues))Flatten(property.Name,property.Value,values);
                JObject own=root["ownship"] as JObject;
                if(own!=null)
                {
                    Add(own,"latitude",Core[0],1,values);Add(own,"longitude",Core[1],1,values);Add(own,"altitude_m",Core[2],1,values);
                    Add(own,"pitch_deg",Core[3],1,values);Add(own,"roll_deg",Core[4],1,values);Add(own,"heading_deg",Core[5],1,values);
                    Add(own,"indicated_airspeed_kt",Core[6],1,values);Add(own,"ground_speed_kt",Core[7],.5144444444,values);
                    Add(own,"vertical_speed_fpm",Core[8],.00508,values);Add(own,"altitude_agl_m",Prefix+"y_agl",1,values);Add(own,"track_deg",Prefix+"hpath",1,values);
                }
                if(values.TryGetValue(Clock,out double clock))signature="clock:"+clock.ToString("R",CultureInfo.InvariantCulture);
                if(!CoreValid(values)){values=null;return false;}return true;
            }
            catch(Exception e) when(e is JsonException||e is FormatException||e is InvalidCastException||e is OverflowException){values=null;return false;}
        }
        private static void Add(JObject source,string key,string path,double scale,Dictionary<string,double> values)
        {if(Number(source[key],out double value))values[path]=value*scale;}
        public static JObject Envelope(XPlaneDiscoveredFrame frame,double age)
        {
            return new JObject{["raw"]=JObject.FromObject(frame.Values),["source_mode"]=frame.Label,
                ["health"]=new JObject{["status"]="ok",["last_packet_age_sec"]=Math.Max(0,age),["last_error"]=""}};
        }
        private const string NativeIcao = "sim/cockpit2/tcas/targets/icao_type";
        private const string NativeModeS = "sim/cockpit2/tcas/targets/modeS_id";
        private static int NativeTrafficSlot(string path)
        {
            const string prefix="sim/multiplayer/position/plane";
            if(!path.StartsWith(prefix,StringComparison.Ordinal))return -1;
            int end=path.IndexOf('_',prefix.Length);
            return end>prefix.Length&&int.TryParse(path.Substring(prefix.Length,end-prefix.Length),out int slot)&&slot>=1&&slot<=19?slot:-1;
        }
        private static bool TrafficPosePath(string path) => NativeTrafficSlot(path)>0 &&
            (path.EndsWith("_psi",StringComparison.Ordinal)||path.EndsWith("_the",StringComparison.Ordinal)||
             path.EndsWith("_phi",StringComparison.Ordinal)||path.EndsWith("_v_y",StringComparison.Ordinal));
        private static int NativeArrayIndex(string path,string prefix)
        {
            if(!path.StartsWith(prefix+"[",StringComparison.Ordinal)||!path.EndsWith("]",StringComparison.Ordinal))return -1;
            return int.TryParse(path.Substring(prefix.Length+1,path.Length-prefix.Length-2),out int index)?index:-1;
        }
        private static void ClearNativeSlotPose(IDictionary<string,double> values,IDictionary<string,double> timestamps,int slot)
        {
            foreach(string suffix in new[]{"_psi","_the","_phi","_v_y"})
            {
                string key="sim/multiplayer/position/plane"+slot+suffix;
                values.Remove(key);timestamps.Remove(key);
            }
        }
        public static void ClearNativeTrafficSession(IDictionary<string,double> values,IDictionary<string,double> timestamps)
        {values.Clear();timestamps.Clear();}
        public static void MergeNativeTrafficDelta(IDictionary<string,double> values,IDictionary<string,double> timestamps,
            IDictionary<string,double> reported,IEnumerable<string> invalid,double now)
        {
            var changed=new HashSet<int>();
            foreach(var pair in reported)
            {
                int slot=NativeArrayIndex(pair.Key,NativeModeS);
                if(slot<0){int index=NativeArrayIndex(pair.Key,NativeIcao);slot=index>=0?index/8:-1;}
                if(slot>=1&&slot<=19&&(!values.TryGetValue(pair.Key,out double old)||old!=pair.Value))changed.Add(slot);
            }
            foreach(string key in invalid??Array.Empty<string>())
            {
                int slot=NativeArrayIndex(key,NativeModeS);
                if(slot<0){int index=NativeArrayIndex(key,NativeIcao);slot=index>=0?index/8:-1;}
                if(slot>=1&&slot<=19)changed.Add(slot);
                if(key==NativeModeS||key==NativeIcao)
                {
                    for(int i=1;i<=19;i++)changed.Add(i);
                    foreach(string old in values.Keys.Where(k=>k.StartsWith(key+"[",StringComparison.Ordinal)).ToArray())
                    {values.Remove(old);timestamps.Remove(old);}
                }
                values.Remove(key);timestamps.Remove(key);
            }
            foreach(int slot in changed)ClearNativeSlotPose(values,timestamps,slot);
            // Reapply only explicitly reported values after invalidating changed
            // slot identities; zero is a valid newly reported baseline.
            foreach(var pair in reported){values[pair.Key]=pair.Value;timestamps[pair.Key]=now;}
        }
        public static Dictionary<string,double> FreshTrafficPose(IDictionary<string,double> values,IDictionary<string,double> timestamps,double now)
        {
            var fresh=new Dictionary<string,double>(StringComparer.Ordinal);
            foreach(var pair in values)
            {
                if(TrafficPosePath(pair.Key))
                {
                    int slot=NativeTrafficSlot(pair.Key);
                    bool trustedIdentity=values.TryGetValue("sim/operation/override/override_TCAS",out double overridden)&&overridden==0&&
                        values.TryGetValue(NativeModeS+"["+slot+"]",out double identity)&&Finite(identity)&&identity>0&&identity<=16777215&&identity==Math.Floor(identity);
                    // Native Web API is delta-only. An unchanged baseline remains
                    // valid within this healthy clock-validated session while its
                    // trusted slot identity is unchanged. Without identity proof,
                    // retain the conservative bounded freshness policy.
                    if(!trustedIdentity&&(!timestamps.TryGetValue(pair.Key,out double at)||now-at<0||now-at>10))continue;
                }
                fresh[pair.Key]=pair.Value;
            }
            return fresh;
        }
        public static string SafeLabel(string value,int length=140)=>string.IsNullOrEmpty(value)?"":new string(value.Where(c=>!char.IsControl(c)&&c!='<'&&c!='>').Take(length).ToArray());
    }
}
