using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class FaaTrafficKinematicsTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type Runtime(string name, string assembly = "Assembly-CSharp") => Type.GetType(name + ", " + assembly, true);
        // Runtime JSON types stay behind reflection so this test assembly does
        // not acquire an unrelated Newtonsoft package/assembly reference.
        private static object ParseJson(string json) => Runtime("Newtonsoft.Json.Linq.JToken", "Newtonsoft.Json")
            .GetMethod("Parse", new[] { typeof(string) }).Invoke(null, new object[] { json });
        private static object DeserializeJson(string json, Type type) => Runtime("Newtonsoft.Json.JsonConvert", "Newtonsoft.Json")
            .GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) }).Invoke(null, new object[] { json, type });
        private static void Set(object value, string name, object data) => value.GetType().GetField(name, Any).SetValue(value, data);
        private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, Any).GetValue(value);
        private static object Call(Type type, object instance, string method, params object[] args) => type.GetMethod(method, Any).Invoke(instance, args);
        private static GameObject Inactive(string name) { var go = new GameObject(name); go.SetActive(false); return go; }
        private static Dictionary<string, float> Position() => new Dictionary<string, float>
        {
            ["sim/multiplayer/position/plane1_lat"] = 47.01f,
            ["sim/multiplayer/position/plane1_lon"] = -122.01f,
            ["sim/multiplayer/position/plane1_el"] = 1100f
        };
        private static IList Rows(GameObject go, Dictionary<string, float> source)
        {
            var bridgeType = Runtime("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge");
            var bridge = go.GetComponent(bridgeType) ?? go.AddComponent(bridgeType);
            var rowType = Runtime("TrafficRadar.TrafficRadarDataManager+AircraftData", "TrafficRadar");
            var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
            Call(bridgeType, bridge, "BuildTrafficRows", source, 47d, -122d, rows);
            return rows;
        }

        [Test]
        public void NativeRows_PreserveReportedZeroAndClearMissingPoseOnNextPosition()
        {
            var go = Inactive("Traffic metadata parser");
            try
            {
                var source = Position();
                foreach (string field in new[] { "psi", "the", "phi", "v_y" }) source["sim/multiplayer/position/plane1_" + field] = 0f;
                object first = Rows(go, source)[0];
                foreach (string flag in new[] { "hasHeading", "hasPitch", "hasRoll", "hasVerticalSpeed" })
                    Assert.That(Get<bool>(first, flag), Is.True, flag);
                object next = Rows(go, Position())[0];
                foreach (string flag in new[] { "hasHeading", "hasPitch", "hasRoll", "hasVerticalSpeed" })
                    Assert.That(Get<bool>(next, flag), Is.False, "A new position must not inherit " + flag);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void NativeRows_RequireActualFinitePositionButPreserveEquatorZero()
        {
            var go = Inactive("Traffic position validation");
            try
            {
                var source = Position(); source.Remove("sim/multiplayer/position/plane1_lon");
                Assert.That(Rows(go, source), Is.Empty, "Missing longitude cannot become a fabricated zero");
                source = Position(); source["sim/multiplayer/position/plane1_el"] = float.PositiveInfinity;
                Assert.That(Rows(go, source), Is.Empty);
                source = Position(); source["sim/multiplayer/position/plane1_lat"] = 91;
                Assert.That(Rows(go, source), Is.Empty);
                source = Position(); source["sim/multiplayer/position/plane1_lat"] = 0;
                Assert.That(Rows(go, source).Count, Is.EqualTo(1), "Actually supplied latitude zero is valid");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void NativeRows_RejectNonfiniteAttitudeAndNeverBorrowOwnship()
        {
            var go = Inactive("Traffic metadata invalid values");
            try
            {
                var source = Position();
                source["sim/flightmodel/position/theta"] = 15f;
                source["sim/flightmodel/position/phi"] = 20f;
                source["sim/multiplayer/position/plane1_psi"] = float.NaN;
                source["sim/multiplayer/position/plane1_the"] = 91f;
                source["sim/multiplayer/position/plane1_phi"] = float.PositiveInfinity;
                source["sim/multiplayer/position/plane1_v_y"] = float.NaN;
                object row = Rows(go, source)[0];
                foreach (string flag in new[] { "hasHeading", "hasPitch", "hasRoll", "hasVerticalSpeed" }) Assert.That(Get<bool>(row, flag), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(0, true)]
        [TestCase(-11, false)]
        [TestCase(60, false)]
        public void RadarAndIndicatorAdapters_PreservePoseUnitsAndExpireValidity(int ageOffset, bool valid)
        {
            var go = Inactive("Traffic pipeline");
            var cueGo = Inactive("Traffic cue bridge");
            try
            {
                var controllerType = Runtime("TrafficRadar.Core.TrafficRadarController", "TrafficRadar");
                object controller = go.AddComponent(controllerType);
                Call(controllerType, controller, "SetOwnPosition", 47d, -122d, 1000f, 0f);
                var source = Position();
                source["sim/multiplayer/position/plane1_psi"] = 0f;
                source["sim/multiplayer/position/plane1_the"] = 5f;
                source["sim/multiplayer/position/plane1_phi"] = -20f;
                source["sim/multiplayer/position/plane1_v_y"] = 1.5f;
                IList rows = Rows(cueGo, source);
                Set(rows[0], "lastUpdateTime", DateTime.UtcNow.AddSeconds(ageOffset));
                Call(controllerType, controller, "OnDataManagerUpdated", rows);
                IList targets = (IList)Call(controllerType, controller, "GetIndicatorTargets", 80f);
                Assert.That(targets.Count, Is.EqualTo(1));
                object radar = targets[0];
                foreach (string flag in new[] { "HasHeading", "HasPitch", "HasRoll", "HasVerticalSpeed" }) Assert.That(Get<bool>(radar, flag), Is.EqualTo(valid), flag);
                Assert.That(Get<float>(radar, "VerticalRateFpm"), Is.EqualTo(1.5f * 196.85f).Within(.01));
                var bridgeType = Runtime("IndicatorSystem.Integration.TrafficIndicatorBridge");
                object bridge = cueGo.AddComponent(bridgeType);
                Set(bridge, "useRadarRelativeScreenProjection", false);
                object target = Call(bridgeType, bridge, "ConvertToIndicatorTarget", radar);
                Assert.That(Get<bool>(target, "hasPitch"), Is.EqualTo(valid));
                Assert.That(Get<float>(target, "pitchDegrees"), Is.EqualTo(5f));
                Assert.That(Get<float>(target, "rollDegrees"), Is.EqualTo(-20f));
                Assert.That(Get<float>(target, "verticalSpeedFpm"), Is.EqualTo(295.275f).Within(.01));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); UnityEngine.Object.DestroyImmediate(cueGo); }
        }

        [Test]
        public void AdsbTrack_IsNotBodyHeading_AndZeroRateIsReported()
        {
            var go = Inactive("ADSB metadata");
            try
            {
                var type = Runtime("TrafficRadar.TrafficRadarDataManager", "TrafficRadar");
                object manager = go.AddComponent(type);
                Call(type, manager, "ProcessResponse", "{\"ac\":[{\"hex\":\"ABCDEF\",\"lat\":47,\"lon\":-122,\"alt_baro\":5000,\"track\":90,\"roll\":0,\"baro_rate\":0}]}");
                object row = Get<IList>(manager, "aircraftList")[0];
                Assert.That(Get<bool>(row, "hasHeading"), Is.False);
                Assert.That(Get<float>(row, "heading"), Is.EqualTo(90));
                Assert.That(Get<bool>(row, "hasRoll"), Is.True);
                Assert.That(Get<bool>(row, "hasVerticalSpeed"), Is.True);
                Assert.That(Get<bool>(row, "hasPitch"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void RelayJson_ExplicitZeroHasPresence_AbsentFieldsRemainUnknown()
        {
            var type = Runtime("FAA.XPlaneIntegration.Runtime.XPlaneRemoteTrafficTarget");
            object absent = DeserializeJson("{}", type);
            object zero = DeserializeJson("{\"heading_deg\":0,\"pitch_deg\":0,\"roll_deg\":0,\"vertical_rate_mps\":0}", type);
            foreach (string flag in new[] { "HeadingDegSpecified", "PitchDegSpecified", "RollDegSpecified", "VerticalRateMpsSpecified" })
            {
                Assert.That(Get<bool>(absent, flag), Is.False, flag);
                Assert.That(Get<bool>(zero, flag), Is.True, flag);
            }
        }

        [TestCase(0f, 0f, 100f, 0f)]
        [TestCase(100f, 0f, 100f, -45f)]
        public void Projection_UsesSightBasisAndActualCameraRect(float x, float y, float z, float relativeHeading)
        {
            var go = new GameObject("Kinematics projection", typeof(Camera));
            try
            {
                var camera = go.GetComponent<Camera>();
                camera.pixelRect = new Rect(110, 70, 820, 540);
                camera.transform.rotation = Quaternion.Euler(0, 12, 8);
                var target = Activator.CreateInstance(Runtime("IndicatorSystem.Integration.TrafficIndicatorTarget"));
                Set(target, "worldPosition", new Vector3(x, y, z)); Set(target, "distanceNM", 1f);
                Set(target, "hasHeading", true); Set(target, "hasPitch", true); Set(target, "hasRoll", true);
                Set(target, "heading", 0f); Set(target, "pitchDegrees", 10f); Set(target, "rollDegrees", -20f);
                var config = Activator.CreateInstance(Runtime("IndicatorSystem.Core.IndicatorEdgeConfig"));
                Set(config, "MaxDisplayDistance", 80f);
                object data = Call(Runtime("IndicatorSystem.Core.ScreenIndicatorCalculator"), null, "CalculateIndicator", target, camera, config);
                Assert.That(Get<Rect>(data, "ProjectionRect"), Is.EqualTo(camera.pixelRect));
                Assert.That(Get<float>(data, "RelativeHeadingDegrees"), Is.EqualTo(relativeHeading).Within(.01));
                Quaternion expected = Quaternion.Inverse(Quaternion.LookRotation(new Vector3(x, y, z), camera.transform.up)) * Quaternion.Euler(-10, 0, 20);
                Assert.That(Quaternion.Angle(Get<Quaternion>(data, "ViewRelativeRotation"), expected), Is.LessThan(.01));
                Set(target, "hasPitch", false);
                data = Call(Runtime("IndicatorSystem.Core.ScreenIndicatorCalculator"), null, "CalculateIndicator", target, camera, config);
                Assert.That(Get<bool>(data, "HasPitch"), Is.False);
                Assert.That(Get<Quaternion>(data, "ViewRelativeRotation"), Is.EqualTo(Quaternion.identity));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(0f, 100f, "OnScreen")]
        [TestCase(40f, 100f, "OffScreen")]
        [TestCase(0f, -100f, "Behind")]
        public void HudRegion_ClipsInAbsolutePixelsWithoutRescalingCameraProjection(float x, float z, string visibility)
        {
            var go = new GameObject("HUD region projection", typeof(Camera));
            var image = new RenderTexture(1920, 1080, 0);
            try
            {
                var camera = go.GetComponent<Camera>();
                camera.targetTexture = image;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.pixelRect = new Rect(100, 200, 1000, 600);
                camera.aspect = 1000f / 600f;
                camera.ResetProjectionMatrix();
                Rect region = new Rect(300, 350, 400, 200);
                var target = Activator.CreateInstance(Runtime("IndicatorSystem.Integration.TrafficIndicatorTarget"));
                Vector3 world = new Vector3(x, 0, z);
                Set(target, "worldPosition", world); Set(target, "distanceNM", 1f);
                var config = Activator.CreateInstance(Runtime("IndicatorSystem.Core.IndicatorEdgeConfig"));
                Set(config, "MaxDisplayDistance", 80f); Set(config, "EdgePadding", 30f);
                var calculator = Runtime("IndicatorSystem.Core.ScreenIndicatorCalculator");
                Assert.That(calculator.GetMethods(Any).Count(m => m.Name == "CalculateIndicator"), Is.EqualTo(1), "Existing reflection API remains unambiguous");
                object data = Call(calculator, null, "CalculateIndicatorInRect", target, camera, config, region);
                Assert.That(Get<Rect>(data, "ProjectionRect"), Is.EqualTo(region));
                Assert.That(Get<object>(data, "Visibility").ToString(), Is.EqualTo(visibility));
                Vector2 position = Get<Vector2>(data, "ScreenPosition");
                if (visibility == "OnScreen")
                {
                    Vector3 exact = camera.WorldToScreenPoint(world);
                    Assert.That(position.x, Is.EqualTo(exact.x).Within(.01));
                    Assert.That(position.y, Is.EqualTo(exact.y).Within(.01));
                    Assert.That(position, Is.Not.EqualTo(region.center), "Projection must not be recentered into the HUD");
                }
                else if (visibility == "OffScreen")
                {
                    Vector3 viewport = camera.WorldToViewportPoint(world);
                    Assert.That(viewport.x, Is.InRange(0f, 1f), "Target remains visible in the full camera");
                    Assert.That(position.x, Is.EqualTo(region.xMax - 30f).Within(.01));
                    Assert.That(position.y, Is.InRange(region.yMin + 30f, region.yMax - 30f));
                }
                else
                {
                    Assert.That(position.x, Is.EqualTo(region.center.x).Within(.01));
                    Assert.That(position.y, Is.EqualTo(region.yMin + 30f).Within(.01));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); image.Release(); UnityEngine.Object.DestroyImmediate(image); }
        }

        [Test]
        public void AsymmetricEyeProjection_TrueAftDirectionUsesHudBottomCentre()
        {
            var go = new GameObject("Asymmetric eye region", typeof(Camera));
            var image = new RenderTexture(1920, 1080, 0);
            try
            {
                var camera = go.GetComponent<Camera>();
                camera.targetTexture = image;
                camera.stereoTargetEye = StereoTargetEyeMask.None;
                camera.pixelRect = new Rect(100, 200, 1000, 600);
                camera.aspect = 1000f / 600f;
                camera.ResetProjectionMatrix();
                camera.transform.rotation = Quaternion.Euler(5, 20, 8);
                Matrix4x4 projection = Matrix4x4.Perspective(60, 1000f / 600f, .1f, 1000f);
                projection[0, 2] = .25f; projection[1, 2] = -.15f;
                camera.projectionMatrix = projection;
                Rect region = new Rect(300, 350, 400, 200);
                var target = Activator.CreateInstance(Runtime("IndicatorSystem.Integration.TrafficIndicatorTarget"));
                Vector3 aft = camera.transform.TransformPoint(Vector3.back * 100f);
                Vector3 aftViewport = camera.WorldToViewportPoint(aft);
                Assert.That(new Vector2(aftViewport.x - .5f, aftViewport.y - .5f).sqrMagnitude, Is.GreaterThan(.001f), "Physical aft axis is off-centre in this calibrated projection");
                Set(target, "worldPosition", aft); Set(target, "distanceNM", 1f);
                var config = Activator.CreateInstance(Runtime("IndicatorSystem.Core.IndicatorEdgeConfig"));
                Set(config, "MaxDisplayDistance", 80f); Set(config, "EdgePadding", 30f);
                var calculator = Runtime("IndicatorSystem.Core.ScreenIndicatorCalculator");
                object data = Call(calculator, null, "CalculateIndicatorInRect", target, camera, config, region);
                Assert.That(Get<object>(data, "Visibility").ToString(), Is.EqualTo("Behind"));
                Assert.That(Get<Vector2>(data, "ScreenPosition").x, Is.EqualTo(region.center.x).Within(.01));
                Assert.That(Get<Vector2>(data, "ScreenPosition").y, Is.EqualTo(region.yMin + 30f).Within(.01));
                // An ordinary visible mark still uses the exact asymmetric
                // camera projection, rather than being recentered into the HUD.
                Vector3 visible = camera.ViewportToWorldPoint(new Vector3(.32f, .4f, 100f));
                Set(target, "worldPosition", visible);
                data = Call(calculator, null, "CalculateIndicatorInRect", target, camera, config, region);
                Vector3 exact = camera.WorldToScreenPoint(visible);
                Assert.That(Get<object>(data, "Visibility").ToString(), Is.EqualTo("OnScreen"));
                Assert.That(Get<Vector2>(data, "ScreenPosition").x, Is.EqualTo(exact.x).Within(.01));
                Assert.That(Get<Vector2>(data, "ScreenPosition").y, Is.EqualTo(exact.y).Within(.01));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); image.Release(); UnityEngine.Object.DestroyImmediate(image); }
        }

        [Test]
        public void CoincidentTarget_DoesNotInventAValidObserverRelativePose()
        {
            var go = new GameObject("Coincident pose", typeof(Camera));
            try
            {
                var target = Activator.CreateInstance(Runtime("IndicatorSystem.Integration.TrafficIndicatorTarget"));
                Set(target, "worldPosition", go.transform.position); Set(target, "distanceNM", 0f);
                Set(target, "hasHeading", true); Set(target, "hasPitch", true); Set(target, "hasRoll", true);
                Set(target, "heading", 0f); Set(target, "pitchDegrees", 10f); Set(target, "rollDegrees", 20f);
                var config = Activator.CreateInstance(Runtime("IndicatorSystem.Core.IndicatorEdgeConfig")); Set(config, "MaxDisplayDistance", 80f);
                object data = Call(Runtime("IndicatorSystem.Core.ScreenIndicatorCalculator"), null, "CalculateIndicator", target, go.GetComponent<Camera>(), config);
                Assert.That(Get<bool>(data, "HasPitch"), Is.True, "Source field was actually reported");
                Quaternion view = Get<Quaternion>(data, "ViewRelativeRotation");
                Assert.That(Quaternion.Dot(view, view), Is.Zero, "No sight basis exists for a valid view quaternion");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void ReportedPose_UsesNoseUpAndRightBankAircraftAxes()
        {
            var go = new GameObject("Known pose axes", typeof(Camera));
            try
            {
                var target = Activator.CreateInstance(Runtime("IndicatorSystem.Integration.TrafficIndicatorTarget"));
                Set(target, "worldPosition", new Vector3(0, 0, 100)); Set(target, "distanceNM", 1f);
                Set(target, "hasHeading", true); Set(target, "hasPitch", true); Set(target, "hasRoll", true);
                Set(target, "heading", 0f); Set(target, "pitchDegrees", 10f); Set(target, "rollDegrees", 20f);
                var config = Activator.CreateInstance(Runtime("IndicatorSystem.Core.IndicatorEdgeConfig")); Set(config, "MaxDisplayDistance", 80f);
                object data = Call(Runtime("IndicatorSystem.Core.ScreenIndicatorCalculator"), null, "CalculateIndicator", target, go.GetComponent<Camera>(), config);
                Quaternion view = Get<Quaternion>(data, "ViewRelativeRotation");
                Assert.That((view * Vector3.forward).y, Is.GreaterThan(0), "Positive reported pitch raises the nose");
                Assert.That((view * Vector3.right).y, Is.LessThan(0), "Positive reported bank lowers the right wing");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void Discovery_ContainsLastTrafficSlotAndDoesNotTruncateIcaoBytes()
        {
            var type = Runtime("FAA.XPlaneIntegration.Runtime.XPlaneDiscoveryData");
            var subscriptions = (string[])type.GetField("Subscriptions").GetValue(null);
            foreach (string path in new[] { "sim/multiplayer/position/plane19_the", "sim/multiplayer/position/plane19_phi", "sim/cockpit2/tcas/targets/icao_type[159]", "sim/operation/override/override_TCAS" }) Assert.That(subscriptions, Does.Contain(path));
            var values = new Dictionary<string, double>();
            for (int i = 0; i < 256; i++) values["sim/test/" + i] = i;
            int[] bytes = new int[160]; bytes[152] = 'S'; bytes[153] = '7'; bytes[154] = '6';
            Call(type, null, "Flatten", "sim/cockpit2/tcas/targets/icao_type", ParseJson("[" + string.Join(",", bytes) + "]"), values);
            Assert.That(values["sim/cockpit2/tcas/targets/icao_type[154]"], Is.EqualTo((int)'6'));
            Assert.That(values.ContainsKey("sim/cockpit2/tcas/targets/icao_type[159]"), Is.True);
            var nativeBytes = bytes.Select(value => (byte)value).ToArray();
            values.Clear();
            Call(type, null, "Flatten", "sim/cockpit2/tcas/targets/icao_type", ParseJson("\"" + Convert.ToBase64String(nativeBytes) + "\""), values);
            Assert.That(values["sim/cockpit2/tcas/targets/icao_type[152]"], Is.EqualTo((int)'S'));
            Assert.That(values.ContainsKey("sim/cockpit2/tcas/targets/icao_type[159]"), Is.True);
        }

        [Test]
        public void NativeDelta_HealthyUnchangedIdentityKeepsLevelPose_IdentityAndDisconnectClearIt()
        {
            var type = Runtime("FAA.XPlaneIntegration.Runtime.XPlaneDiscoveryData");
            const string prefix = "sim/multiplayer/position/plane1";
            const string identity = "sim/cockpit2/tcas/targets/modeS_id[1]";
            const string clock = "sim/time/total_running_time_sec";
            var values = new Dictionary<string, double>();
            var times = new Dictionary<string, double>();
            var baseline = new Dictionary<string, double>
            {
                [prefix + "_lat"] = 47, [prefix + "_psi"] = 0, [prefix + "_the"] = 0,
                [prefix + "_phi"] = 0, [prefix + "_v_y"] = 0, [identity] = 123,
                ["sim/operation/override/override_TCAS"] = 0, [clock] = 0
            };
            Call(type, null, "MergeNativeTrafficDelta", values, times, baseline, Array.Empty<string>(), 0d);
            // Delta-only straight/level flight: only native clock/position change.
            Call(type, null, "MergeNativeTrafficDelta", values, times,
                new Dictionary<string,double> { [prefix + "_lat"] = 47.1, [clock] = 120 }, Array.Empty<string>(), 120d);
            var fresh = (Dictionary<string,double>)Call(type, null, "FreshTrafficPose", values, times, 120d);
            Assert.That(fresh[prefix + "_the"], Is.EqualTo(0));
            Assert.That(fresh[prefix + "_phi"], Is.EqualTo(0));
            Assert.That(fresh[clock], Is.EqualTo(120));
            // A new aircraft in the same slot must not inherit that baseline.
            Call(type, null, "MergeNativeTrafficDelta", values, times,
                new Dictionary<string,double> { [identity] = 456, [prefix + "_lat"] = 47.2, [clock] = 121 }, Array.Empty<string>(), 121d);
            Assert.That(values.ContainsKey(prefix + "_the"), Is.False);
            Assert.That(values.ContainsKey(prefix + "_phi"), Is.False);
            // Newly reported zero belongs to the new identity and is valid.
            Call(type, null, "MergeNativeTrafficDelta", values, times,
                new Dictionary<string,double> { [prefix + "_the"] = 0, [prefix + "_phi"] = 0 }, Array.Empty<string>(), 122d);
            fresh = (Dictionary<string,double>)Call(type, null, "FreshTrafficPose", values, times, 122d);
            Assert.That(fresh.ContainsKey(prefix + "_the"), Is.True);
            // Explicit null/invalid pitch removes it; unchanged roll is retained.
            Call(type, null, "MergeNativeTrafficDelta", values, times,
                new Dictionary<string,double>(), new[] { prefix + "_the" }, 123d);
            Assert.That(values.ContainsKey(prefix + "_the"), Is.False);
            Assert.That(values.ContainsKey(prefix + "_phi"), Is.True);
            Call(type, null, "ClearNativeTrafficSession", values, times);
            Assert.That(values, Is.Empty);
            Assert.That(times, Is.Empty);
        }

        [Test]
        public void NativeDelta_NewPositionWithoutTrustedIdentityHasBoundedPoseFreshness()
        {
            var type = Runtime("FAA.XPlaneIntegration.Runtime.XPlaneDiscoveryData");
            const string prefix = "sim/multiplayer/position/plane1";
            var values = new Dictionary<string, double> { [prefix + "_lat"] = 47, [prefix + "_psi"] = 0, [prefix + "_the"] = 0, [prefix + "_phi"] = 0, [prefix + "_v_y"] = 0 };
            var times = values.Keys.ToDictionary(k => k, k => 0d); times[prefix + "_lat"] = 20;
            var fresh = (Dictionary<string, double>)Call(type, null, "FreshTrafficPose", values, times, 20d);
            Assert.That(fresh.ContainsKey(prefix + "_lat"), Is.True);
            Assert.That(fresh.ContainsKey(prefix + "_the"), Is.False);
            Assert.That(fresh.ContainsKey(prefix + "_phi"), Is.False);
            times[prefix + "_phi"] = 20;
            fresh = (Dictionary<string, double>)Call(type, null, "FreshTrafficPose", values, times, 20d);
            Assert.That(fresh[prefix + "_phi"], Is.EqualTo(0));
            Assert.That(fresh.ContainsKey(prefix + "_the"), Is.False);
        }
    }
}
