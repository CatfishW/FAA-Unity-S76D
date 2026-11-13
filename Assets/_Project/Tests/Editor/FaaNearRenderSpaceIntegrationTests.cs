using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaNearRenderSpaceIntegrationTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name, string assembly = "Assembly-CSharp") => Type.GetType(name + ", " + assembly, true);
        private static object Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Any).Invoke(owner, args);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Any).SetValue(owner, value);
        private static V Get<V>(object owner, string name) => (V)owner.GetType().GetField(name, Any).GetValue(owner);
        private sealed class Fixture : IDisposable
        {
            public readonly GameObject Aircraft = new GameObject("Data-world aircraft");
            public readonly GameObject Head = new GameObject("Render-space head", typeof(Camera));
            public readonly Component Rig;
            public readonly object Origin;
            public Camera View => Head.GetComponent<Camera>();
            public Fixture()
            {
                Aircraft.transform.SetPositionAndRotation(new Vector3(12466.1f, 318.2f, -4424.2f), Quaternion.identity);
                Head.tag = "MainCamera"; Head.transform.localPosition = new Vector3(.17f, 1.62f, -.23f);
                Head.AddComponent(T("UnityEngine.InputSystem.XR.TrackedPoseDriver", "Unity.InputSystem"));
                Rig = Head.AddComponent(T("AircraftControl.Camera.AircraftCameraController", "AircraftControl"));
                Set(Rig, "aircraftTransform", Aircraft.transform); Origin = Get<object>(Rig, "_nativeXrOrigin");
                Call(Origin, "Follow", Head.transform, Aircraft.transform.position, Aircraft.transform.rotation, new Vector3(0, 1.5f, 2), true, true, true);
                T("AircraftControl.Camera.AircraftViewRenderSpace", "AircraftControl").GetMethod("Bind", Any).Invoke(null, new object[] { View, Rig });
                View.pixelRect = new Rect(0, 0, 1600, 900); View.aspect = 16f / 9f; View.fieldOfView = 60; View.farClipPlane = 20000;
            }
            public void Near(bool active) => Call(Rig, "SetNearRenderSpaceRequested", active);
            public Vector3 ToData(Vector3 point) => (Vector3)T("AircraftControl.Camera.AircraftViewRenderSpace", "AircraftControl")
                .GetMethod("RenderToDataPoint", Any, null, new[] { typeof(Camera), typeof(Vector3) }, null).Invoke(null, new object[] { View, point });
            public void Dispose() { Near(false); Call(Origin, "Release"); Object.DestroyImmediate(Head); Object.DestroyImmediate(Aircraft); }
        }
        [SetUp] public void EmptyScene() => EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        private static object Target(Vector3 point, float heading)
        {
            object target = Activator.CreateInstance(T("IndicatorSystem.Integration.TrafficIndicatorTarget"));
            Set(target, "id", "SPACE TEST"); Set(target, "worldPosition", point); Set(target, "distanceNM", 1f);
            Set(target, "hasHeading", true); Set(target, "heading", heading); Set(target, "displayColor", Color.cyan);
            return target;
        }
        private static object Calculate(object target, Camera view)
        {
            var config = T("IndicatorSystem.Core.IndicatorEdgeConfig").GetProperty("Default", Any).GetValue(null);
            return T("IndicatorSystem.Core.ScreenIndicatorCalculator").GetMethod("CalculateIndicator", Any).Invoke(null, new[] { target, view, config });
        }

        [TestCase(0f, 400f, 1000f)] [TestCase(90f, -400f, 1000f)] [TestCase(180f, 0f, 1000f)] [TestCase(270f, 200f, -1000f)]
        public void TrafficAndLegacyNavigationLightsKeepTheirDataWorldBearingAndState(float heading, float x, float z)
        {
            using (var f = new Fixture())
            {
                var host = new GameObject("Legacy traffic renderer", typeof(RectTransform));
                var settings = ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings"));
                try
                {
                    Set(settings, "showNavigationLights", true); Set(settings, "blinkNavLights", false);
                    Component renderer = host.AddComponent(T("IndicatorSystem.Display.IndicatorElement"));
                    object target = Target(f.Aircraft.transform.position + new Vector3(x, 50f, z), heading);
                    object before = Calculate(target, f.View);
                    float angleBefore = (float)Call(renderer, "CalculateScreenRotation", before, settings);
                    Vector3 tiltBefore = (Vector3)Call(renderer, "Calculate3DRotation", before, settings, angleBefore);
                    Call(renderer, "UpdateNavigationLights", before, settings, false); string lightsBefore = Lights(renderer);
                    f.Near(true); object after = Calculate(target, f.View);
                    float angleAfter = (float)Call(renderer, "CalculateScreenRotation", after, settings);
                    Vector3 tiltAfter = (Vector3)Call(renderer, "Calculate3DRotation", after, settings, angleAfter);
                    Call(renderer, "UpdateNavigationLights", after, settings, false);
                    Assert.That(Get<Vector3>(after, "WorldPosition"), Is.EqualTo(Get<Vector3>(before, "WorldPosition")));
                    Assert.That(Get<object>(after, "Visibility"), Is.EqualTo(Get<object>(before, "Visibility")));
                    Assert.That(Vector2.Distance(Get<Vector2>(after, "ScreenPosition"), Get<Vector2>(before, "ScreenPosition")), Is.LessThan(1.5f));
                    Assert.That(Mathf.Abs(Mathf.DeltaAngle(angleBefore, angleAfter)), Is.LessThan(.2f));
                    Assert.That(Vector3.Distance(tiltBefore, tiltAfter), Is.LessThan(.25f));
                    Assert.That(Lights(renderer), Is.EqualTo(lightsBefore));
                    f.Near(false); object restored = Calculate(target, f.View);
                    Assert.That(Vector2.Distance(Get<Vector2>(restored, "ScreenPosition"), Get<Vector2>(before, "ScreenPosition")), Is.LessThan(.01f));
                }
                finally { Object.DestroyImmediate(host); Object.DestroyImmediate(settings); }
            }
        }
        private static string Lights(Component renderer)
        {
            string value = "";
            foreach (string field in new[] { "portLightImage", "starboardLightImage", "tailLightImage" })
                value += Get<Component>(renderer, field).gameObject.activeSelf ? "1" : "0";
            return value;
        }

        [TestCase(true)] [TestCase(false)]
        public void WeatherCachedCameraReferenceRemainsDataWorldAndIsOffsetExactlyOnce(bool radarProjection)
        {
            using (var f = new Fixture())
            {
                var host = new GameObject("Weather data reference");
                try
                {
                    Component weather = host.AddComponent(T("IndicatorSystem.Integration.WeatherIndicatorBridge"));
                    Set(weather, "positionReference", f.Head.transform); Set(weather, "useRadarRelativeScreenProjection", radarProjection);
                    Vector3 baseline = (Vector3)Call(weather, "BuildWorldPosition", 25f, 1852f);
                    f.Near(true);
                    Vector3 nearData = (Vector3)Call(weather, "BuildWorldPosition", 25f, 1852f);
                    Assert.That(Vector3.Distance(nearData, baseline), Is.LessThan(.004f));
                    Assert.That(nearData.magnitude, Is.GreaterThan(10000), "Targets retain geographic data-world coordinates.");
                    object before = Calculate(Target(baseline, 0), f.View), after = Calculate(Target(nearData, 0), f.View);
                    Assert.That(Vector2.Distance(Get<Vector2>(before, "ScreenPosition"), Get<Vector2>(after, "ScreenPosition")), Is.LessThan(.01f));
                    Set(weather, "positionReference", f.Aircraft.transform);
                    Vector3 aircraftOrigin = (Vector3)Call(weather, "BuildWorldPosition", 25f, 1852f);
                    Assert.That(Vector3.Distance(aircraftOrigin, baseline), Is.LessThan(5f), "Actual aircraft references must not acquire a second offset.");
                }
                finally { Object.DestroyImmediate(host); }
            }
        }

        [Test]
        public void GeographicSceneReferenceAndWireKeepDirectionAndPhysicsLandingRayHitsDataWorld()
        {
            if (Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp") == null)
                Assert.Ignore("Full XR integration scene layer is not imported by the standard profile.");
            using (var f = new Fixture())
            {
                var host = new GameObject("Conformal coordinate owner");
                var anchorHost = new GameObject("Authored geographic obstacle");
                var lineEnd = new GameObject("Authored geographic wire endpoint");
                GameObject surface = null, selected = null;
                Component layer = host.AddComponent(T("FAA.Customization.FaaRotorcraftConformalLayer"));
                try
                {
                    Set(layer, "view", f.View); Set(layer, "aircraft", f.Aircraft.transform);
                    Component anchor = anchorHost.AddComponent(T("FAA.Customization.FaaRotorcraftCueAnchor"));
                    anchorHost.transform.position = f.Aircraft.transform.position + new Vector3(40, 30, 500);
                    lineEnd.transform.position = anchorHost.transform.position + new Vector3(30, 10, 40);
                    Set(anchor, "kind", Enum.Parse(T("FAA.Customization.FaaRotorcraftCueAnchor+CueType"), "Obstacle")); Set(anchor, "lineEnd", lineEnd.transform);
                    Call(anchor, "OnEnable"); Call(layer, "CacheView"); Set(layer, "eyePosition", f.View.transform.position);
                    Call(layer, "DrawSceneReferences", false);
                    Vector3 before = Get<Vector3[]>(layer, "sceneCentre")[0];
                    int beforeVertices = ((IList)Get<object>(layer, "vertices")).Count;
                    Assert.That(before.magnitude, Is.GreaterThan(100)); Assert.That(beforeVertices, Is.GreaterThan(0));
                    ((IList)Get<object>(layer, "vertices")).Clear(); f.Near(true);
                    Call(layer, "CacheView"); Set(layer, "eyePosition", f.View.transform.position); Call(layer, "DrawSceneReferences", false);
                    Vector3 after = Get<Vector3[]>(layer, "sceneCentre")[0];
                    Assert.That(Vector3.Distance(after, before), Is.LessThan(.004f));
                    Assert.That(((IList)Get<object>(layer, "vertices")).Count, Is.EqualTo(beforeVertices));

                    f.Head.transform.localRotation = Quaternion.Euler(40, 0, 0);
                    Vector3 dataEye = f.ToData(f.View.transform.position), hit = dataEye + f.View.transform.forward * 50f;
                    surface = GameObject.CreatePrimitive(PrimitiveType.Cube); surface.name = "Actual data-world terrain collider";
                    surface.transform.position = new Vector3(hit.x, hit.y - .5f, hit.z); surface.transform.localScale = new Vector3(100, 1, 100);
                    Physics.SyncTransforms(); Set(layer, "<HasLiveAttitude>k__BackingField", true);
                    Call(layer, "MarkLandingReference");
                    var reference = Get<Component>(layer, "userLandingReference"); Assert.That(reference, Is.Not.Null);
                    selected = reference.gameObject;
                    Assert.That(reference.transform.parent, Is.EqualTo(surface.transform));
                    Assert.That(Vector3.Distance(reference.transform.position, hit + Vector3.up * .05f), Is.LessThan(.02f));
                    Assert.That(reference.transform.position.magnitude, Is.GreaterThan(10000));
                }
                finally
                {
                    Set(layer, "userLandingReference", null);
                    if (selected != null) Object.DestroyImmediate(selected);
                    if (surface != null) Object.DestroyImmediate(surface);
                    Object.DestroyImmediate(lineEnd); Object.DestroyImmediate(anchorHost); Object.DestroyImmediate(host);
                }
            }
        }

        [Test]
        public void BeforeRenderRefreshesFixedGeographicSceneCueAfterPureAppliedOffsetTranslation()
        {
            if (Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp") == null)
                Assert.Ignore("Full XR integration scene layer is not imported by the standard profile.");
            if (Resources.Load<Shader>("Shaders/FaaRotorcraftConformal") == null) Assert.Ignore("Full integration shader required.");
            using (var f = new Fixture())
            {
                var host = new GameObject("Fixed geographic cue render-phase owner");
                var bridgeHost = new GameObject("Inactive render-phase flight fixture"); bridgeHost.SetActive(false);
                var canvasHost = new GameObject("Render-phase source canvas", typeof(RectTransform), typeof(Canvas));
                var anchorHost = new GameObject("Fixed geographic cue");
                Component layer = host.AddComponent(T("FAA.Customization.FaaRotorcraftConformalLayer"));
                try
                {
                    f.Near(true);
                    Component bridge = bridgeHost.AddComponent(T("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge"));
                    object data = Activator.CreateInstance(T("AviationUI.AviationFlightData"));
                    Set(data, "attitudeValid", true); Set(data, "heading", 0f); Set(data, "pitch", 0f); Set(data, "roll", 0f);
                    Set(bridge, "_latestFlightData", data);
                    bridge.GetType().GetProperty("IsFeedHealthy").SetValue(bridge, true);
                    bridge.GetType().GetProperty("LastPacketAgeSeconds").SetValue(bridge, .05f);
                    Set(layer, "flightData", bridge);
                    Call(layer, "Bind", f.View, f.Aircraft.transform, canvasHost.GetComponent<Canvas>());
                    Component anchor = anchorHost.AddComponent(T("FAA.Customization.FaaRotorcraftCueAnchor"));
                    anchorHost.transform.position = f.Aircraft.transform.position + new Vector3(150, 10, 800);
                    Call(anchor, "OnEnable"); Call(layer, "RefreshPresentation");
                    Assert.That((int)layer.GetType().GetProperty("VisibleSceneCueCount").GetValue(layer), Is.GreaterThan(0));
                    Vector3 before = Get<Vector3[]>(layer, "sceneCentre")[0], eye = f.View.transform.position;
                    Vector3 delta = new Vector3(8, 0, 13);
                    f.Aircraft.transform.position += delta; Call(f.Rig, "UpdateNativeXrOrigin");
                    Assert.That(f.View.transform.position, Is.EqualTo(eye), "Only the applied data-world offset changed within the same frame.");
                    Call(layer, "RefreshBeforeRender");
                    Vector3 after = Get<Vector3[]>(layer, "sceneCentre")[0];
                    Assert.That(Vector3.Distance(after, before - delta), Is.LessThan(.004f), "A render-eye-only cache key must not skip geographic cue reprojection.");
                }
                finally
                {
                    foreach (string field in new[] { "mesh", "material", "labelMaterial" })
                        if (Get<Object>(layer, field) is Object value && value != null) Object.DestroyImmediate(value);
                    Object.DestroyImmediate(anchorHost); Object.DestroyImmediate(canvasHost); Object.DestroyImmediate(host); Object.DestroyImmediate(bridgeHost);
                }
            }
        }

        [Test]
        public void PassthroughOwnerActivatesReleasesAndTransfersOnlyItsViewAndSpectatorSharesIt()
        {
            using (var f = new Fixture())
            {
                var host = new GameObject("MR lifecycle owner"); var previewHost = new GameObject("Owned desktop preview", typeof(Camera));
                Component mr = host.AddComponent(T("FAA.Headset.XR3MixedRealityPresentation"));
                try
                {
                    Set(mr, "view", f.View); Set(mr, "<PassthroughActive>k__BackingField", true);
                    Call(mr, "ApplyPresentation");
                    Assert.That((bool)f.Rig.GetType().GetProperty("NearRenderSpaceActive").GetValue(f.Rig), Is.True);
                    T("FAA.Headset.XR3DesktopHudPreview").GetMethod("ConfigureCamera", Any).Invoke(null, new object[] { previewHost.GetComponent<Camera>(), f.View, new Vector2(1600, 900) });
                    object previewSpace = T("AircraftControl.Camera.AircraftViewRenderSpace", "AircraftControl").GetMethod("ForCamera").Invoke(null, new object[] { previewHost.GetComponent<Camera>() });
                    Assert.That(previewSpace.GetType().GetProperty("Owner").GetValue(previewSpace), Is.SameAs(f.Rig));
                    Assert.That(previewHost.transform.position, Is.EqualTo(f.View.transform.position));
                    Set(mr, "<PassthroughActive>k__BackingField", false); Call(mr, "BeforeRender");
                    Assert.That((bool)f.Rig.GetType().GetProperty("NearRenderSpaceActive").GetValue(f.Rig), Is.False);
                    Set(mr, "<PassthroughActive>k__BackingField", true); Call(mr, "ApplyPresentation");
                    Call(mr, "OnDisable");
                    Assert.That((bool)f.Rig.GetType().GetProperty("NearRenderSpaceActive").GetValue(f.Rig), Is.False);
                    Assert.That(f.Head.transform.position.magnitude, Is.GreaterThan(10000));
                    Set(mr, "<PassthroughActive>k__BackingField", true); Call(mr, "ApplyPresentation");
                    using (var replacement = new Fixture())
                    {
                        Set(mr, "view", replacement.View); Call(mr, "ApplyPresentation");
                        Assert.That((bool)f.Rig.GetType().GetProperty("NearRenderSpaceActive").GetValue(f.Rig), Is.False);
                        Assert.That((bool)replacement.Rig.GetType().GetProperty("NearRenderSpaceActive").GetValue(replacement.Rig), Is.True);
                        Call(mr, "OnSceneLoaded", default(UnityEngine.SceneManagement.Scene), UnityEngine.SceneManagement.LoadSceneMode.Single);
                        Assert.That((bool)replacement.Rig.GetType().GetProperty("NearRenderSpaceActive").GetValue(replacement.Rig), Is.False);
                    }
                }
                finally { Set(mr, "<PassthroughActive>k__BackingField", false); Object.DestroyImmediate(host); Object.DestroyImmediate(previewHost); }
            }
        }
    }
}
