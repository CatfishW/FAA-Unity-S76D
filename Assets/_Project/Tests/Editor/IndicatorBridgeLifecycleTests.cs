using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class IndicatorBridgeLifecycleTests
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Runtime(string name,string assembly = "Assembly-CSharp") => Type.GetType(name+", "+assembly,true);
        private static FieldInfo Field(Type type,string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name,Fields|BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName,name);
        }
        private static void Set(object target,string name,object value) => Field(target.GetType(),name).SetValue(target,value);
        private static object Get(object target,string name) => Field(target.GetType(),name).GetValue(target);
        private static object Call(object target,string method,params object[] arguments) => target.GetType()
            .GetMethod(method,Fields).Invoke(target,arguments);

        [TestCase(true)]
        [TestCase(false)]
        public void UnavailableWeatherProviderClearsStoredCuesAndSourceStatusOnThePublicRefreshPath(bool destroy)
        {
            using var fixture = new WeatherFixture();
            Component provider = fixture.NewProvider("Weather provider A");
            fixture.Bind(provider);
            fixture.Publish(provider,fixture.Texture(Color.green)); fixture.Scan();
            Assert.That(fixture.CueCount,Is.GreaterThan(0));
            Assert.That(fixture.StoredCount,Is.GreaterThan(0));
            Assert.That(fixture.Status,Is.EqualTo("RADAR RETURNS"));
            if (destroy) fixture.DestroyProvider(provider);
            else ((Behaviour)provider).enabled = false;
            fixture.Scan();
            Assert.That(fixture.CueCount,Is.Zero,"The destroyed provider must not leave plausible frozen weather cells.");
            Assert.That(fixture.StoredCount,Is.Zero);
            Assert.That(fixture.Status,Is.EqualTo("WAITING FOR DATA"));
        }

        [Test]
        public void WeatherProviderReplacementUnsubscribesOldSourceAndReceivesTheNewSourcesFramesExactlyOnce()
        {
            using var fixture = new WeatherFixture();
            Component first = fixture.NewProvider("Weather provider A"), second = fixture.NewProvider("Weather provider B");
            Texture2D rain = fixture.Texture(Color.green), dry = fixture.Texture(Color.black);
            fixture.Bind(first); fixture.Publish(first,rain); fixture.Scan();
            Assert.That(fixture.CueCount,Is.GreaterThan(0));
            Set(fixture.Bridge,"weatherProvider",second);
            Call(fixture.Bridge,"Update");
            Assert.That(fixture.SubscriberCount(first),Is.Zero);
            Assert.That(fixture.SubscriberCount(second),Is.EqualTo(1));
            Assert.That(fixture.CueCount,Is.Zero);
            Assert.That(fixture.Status,Is.EqualTo("WAITING FOR DATA"));
            fixture.Publish(first,rain,false);
            Assert.That(Get(fixture.Bridge,"_lastRadarTexture"),Is.Null,"The detached source cannot refill the bridge cache.");
            Call(fixture.Bridge,"Connect"); Call(fixture.Bridge,"Connect");
            Assert.That(fixture.SubscriberCount(second),Is.EqualTo(1));
            fixture.Publish(second,dry); fixture.Scan();
            Assert.That(fixture.CueCount,Is.Zero);
            Assert.That(fixture.Status,Is.EqualTo("NO RAIN RETURNS"));
            fixture.Publish(second,rain); fixture.Scan();
            Assert.That(fixture.CueCount,Is.GreaterThan(0));
            Assert.That(fixture.Status,Is.EqualTo("RADAR RETURNS"));
        }

        [Test]
        public void ReplacementCannotReuseThePreviousDisplaysTextureOrPowerState()
        {
            using var fixture = new WeatherFixture();
            Component first = fixture.NewProvider("Weather provider A"), second = fixture.NewProvider("Weather provider B");
            var displayHost = new GameObject("Old weather display"); displayHost.SetActive(false);
            try
            {
                Component display = displayHost.AddComponent(Runtime("WeatherRadar.XPlaneOriginalWeatherRadarDisplay","WeatherRadar"));
                Texture2D oldRain = fixture.Texture(Color.green);
                Set(display,"_currentTexture",oldRain);
                Set(display,"_lastTextureRealtime",Time.realtimeSinceStartup);
                Set(fixture.Bridge,"_originalDisplay",display);
                fixture.Bind(first); fixture.Scan();
                Assert.That(fixture.CueCount,Is.GreaterThan(0),"A late subscriber can initially use the existing fresh display picture.");
                Set(fixture.Bridge,"weatherProvider",second);
                fixture.Scan();
                Assert.That(fixture.CueCount,Is.Zero);
                Assert.That(fixture.Status,Is.EqualTo("WAITING FOR DATA"),"An old display picture is not a frame from the replacement provider.");
                Set(display,"_hasRadarPowerState",true); Set(display,"_isRadarPowered",false);
                fixture.Publish(second,fixture.Texture(Color.black)); fixture.Scan();
                Assert.That(fixture.Status,Is.EqualTo("NO RAIN RETURNS"),"An unrelated old display's power state cannot override the new source.");
            }
            finally { UnityEngine.Object.DestroyImmediate(displayHost); }
        }

        [Test]
        public void FreshDryPoweredOffAndStaleWeatherRemainDistinctAndCanRecover()
        {
            using var fixture = new WeatherFixture();
            Component provider = fixture.NewProvider("Weather states"); fixture.Bind(provider);
            Texture2D rain = fixture.Texture(Color.green);
            fixture.Publish(provider,rain); fixture.Scan(); Assert.That(fixture.Status,Is.EqualTo("RADAR RETURNS"));
            fixture.SetProviderStatus(provider,"Inactive"); fixture.Scan();
            Assert.That(fixture.CueCount,Is.Zero); Assert.That(fixture.Status,Is.EqualTo("RADAR POWER OFF"));
            fixture.SetProviderStatus(provider,"Active"); fixture.Publish(provider,fixture.Texture(Color.black)); fixture.Scan();
            Assert.That(fixture.CueCount,Is.Zero); Assert.That(fixture.Status,Is.EqualTo("NO RAIN RETURNS"));
            Set(fixture.Bridge,"_lastTextureTime",Time.unscaledTime-6f); fixture.Scan();
            Assert.That(fixture.CueCount,Is.Zero); Assert.That(fixture.Status,Is.EqualTo("WAITING FOR DATA"));
            fixture.Publish(provider,rain); fixture.Scan();
            Assert.That(fixture.CueCount,Is.GreaterThan(0)); Assert.That(fixture.Status,Is.EqualTo("RADAR RETURNS"));
        }

        [TestCase(true,false,true,"TRAFFIC DATA STALE",0)]
        [TestCase(false,false,true,"NO TRAFFIC IN RANGE",0)]
        [TestCase(true,true,true,"LIVE TRAFFIC",1)]
        [TestCase(false,true,true,"LIVE TRAFFIC",1)]
        [TestCase(false,true,false,"WAITING FOR DATA",0)]
        public void TrafficStatusDistinguishesStaleSamplesFromEmptyRangeWhileOwnshipHealthIsIndependent(bool stale,bool fresh,bool healthy,string status,int count)
        {
            using var fixture = new WeatherFixture();
            var radarHost = new GameObject("Traffic source"); radarHost.SetActive(false);
            var bridgeHost = new GameObject("Traffic bridge"); bridgeHost.SetActive(false);
            var feedHost = new GameObject("Ownship transport health"); feedHost.SetActive(false);
            try
            {
                Type radarType = Runtime("TrafficRadar.Core.TrafficRadarController","TrafficRadar");
                Component radar = radarHost.AddComponent(radarType);
                radarHost.SetActive(true);
                Call(radar,"SetOwnPosition",47d,-122d,1000f,0f);
                Type stateType = Runtime("TrafficRadar.Core.AircraftState","TrafficRadar");
                var states = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(stateType));
                if (stale) states.Add(TrafficState(stateType,"OLD",47.01,DateTime.UtcNow.AddSeconds(-11)));
                if (fresh) states.Add(TrafficState(stateType,"NEW",47.02,DateTime.UtcNow));
                Set(radar,"_cachedAircraftStates",states);
                Component feed = feedHost.AddComponent(Runtime("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge"));
                Set(feed,"<IsFeedHealthy>k__BackingField",healthy);
                Component bridge = bridgeHost.AddComponent(Runtime("IndicatorSystem.Integration.TrafficIndicatorBridge"));
                Set(bridge,"trafficRadarController",radar); Set(bridge,"indicatorController",fixture.Controller);
                Set(bridge,"positionReference",bridgeHost.transform); Set(bridge,"_feed",feed);
                Call(bridge,"RefreshTargets");
                Assert.That(feed.GetType().GetProperty("IsFeedHealthy").GetValue(feed),Is.EqualTo(healthy));
                Assert.That(bridge.GetType().GetProperty("SourceStatus").GetValue(bridge),Is.EqualTo(status));
                Assert.That(bridge.GetType().GetProperty("TargetCount").GetValue(bridge),Is.EqualTo(count));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bridgeHost); UnityEngine.Object.DestroyImmediate(radarHost); UnityEngine.Object.DestroyImmediate(feedHost);
            }
        }

        private static object TrafficState(Type type,string id,double latitude,DateTime timestamp)
        {
            object value = Activator.CreateInstance(type);
            Set(value,"Icao24",id); Set(value,"Callsign",id); Set(value,"Latitude",latitude); Set(value,"Longitude",-122.01d);
            Set(value,"AltitudeMeters",1000f); Set(value,"LastUpdate",timestamp);
            return value;
        }

        private sealed class WeatherFixture : IDisposable
        {
            public readonly Component Bridge,Controller;
            private readonly ScriptableObject settings;
            private readonly List<GameObject> objects = new List<GameObject>();
            private readonly List<Component> providers = new List<Component>();
            private readonly List<Texture2D> textures = new List<Texture2D>();
            public int CueCount => (int)Bridge.GetType().GetProperty("TargetCount").GetValue(Bridge);
            public string Status => (string)Bridge.GetType().GetProperty("SourceStatus").GetValue(Bridge);
            public int StoredCount => ((IDictionary)Get(Controller,"_targets")).Count;

            public WeatherFixture()
            {
                GameObject owner = Host("Indicator controller"), bridge = Host("Weather bridge");
                settings = ScriptableObject.CreateInstance(Runtime("IndicatorSystem.Core.IndicatorSettings"));
                Controller = owner.AddComponent(Runtime("IndicatorSystem.Controller.IndicatorSystemController"));
                Set(Controller,"settings",settings);
                Bridge = bridge.AddComponent(Runtime("IndicatorSystem.Integration.WeatherIndicatorBridge"));
                Set(Bridge,"indicatorController",Controller); Set(Bridge,"positionReference",bridge.transform);
                Set(Bridge,"_feed",null); Set(Bridge,"_originalDisplay",null); Set(Bridge,"showPoweredRadarFallback",false);
            }
            private GameObject Host(string name)
            {
                var host = new GameObject(name); host.SetActive(false); objects.Add(host); return host;
            }
            public Component NewProvider(string name)
            {
                GameObject host = Host(name);
                Component provider = host.AddComponent(Runtime("WeatherRadar.SimulatedWeatherProvider","WeatherRadar"));
                Set(provider,"textureSize",16); Set(provider,"activateOnStart",false); Set(provider,"generateStormCells",false);
                host.SetActive(true); SetProviderStatus(provider,"Active"); providers.Add(provider); return provider;
            }
            public void SetProviderStatus(Component provider,string status) => Set(provider,"status",Enum.Parse(Field(provider.GetType(),"status").FieldType,status));
            public void Bind(Component provider) { Set(Bridge,"weatherProvider",provider); Call(Bridge,"Connect"); }
            public void Scan() => Bridge.GetType().GetMethod("ForceUpdate").Invoke(Bridge,null);
            public Texture2D Texture(Color color)
            {
                var texture = new Texture2D(16,16,TextureFormat.RGBA32,false) { name = "Weather bridge lifecycle sample" };
                var pixels = new Color[256]; for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
                texture.SetPixels(pixels); texture.Apply(); textures.Add(texture); return texture;
            }
            public void Publish(Component provider,Texture2D texture,bool requireSubscriber = true)
            {
                Delegate callback = (Delegate)Get(provider,"OnRadarDataUpdated");
                if (requireSubscriber) Assert.That(callback,Is.Not.Null);
                callback?.DynamicInvoke(texture);
            }
            public int SubscriberCount(Component provider)
            {
                Delegate callback = (Delegate)Get(provider,"OnRadarDataUpdated"); int count = 0;
                if (callback != null) foreach (Delegate subscriber in callback.GetInvocationList())
                    if (ReferenceEquals(subscriber.Target,Bridge) && subscriber.Method.Name == "OnWeatherDataUpdated") count++;
                return count;
            }
            public void DestroyProvider(Component provider)
            {
                if (provider == null) return;
                var allocated = Get(provider,"radarTexture") as Texture2D;
                Set(provider,"radarTexture",null);
                if (allocated != null) UnityEngine.Object.DestroyImmediate(allocated);
                UnityEngine.Object.DestroyImmediate(provider.gameObject);
            }
            public void Dispose()
            {
                if (Bridge != null) Call(Bridge,"Disconnect");
                foreach (Component provider in providers) DestroyProvider(provider);
                foreach (GameObject host in objects) if (host != null) UnityEngine.Object.DestroyImmediate(host);
                foreach (Texture2D texture in textures) if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
