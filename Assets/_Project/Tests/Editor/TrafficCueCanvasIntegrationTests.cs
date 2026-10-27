using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public sealed class TrafficCueCanvasIntegrationTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private const string ExpandedKey = "FAA.Cues.ControlsExpanded";
        private bool hadExpanded; private int expanded;
        private static Type T(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name); if (type != null) return type; }
            throw new InvalidOperationException(name);
        }
        private static void Set(object value, string name, object field) => value.GetType().GetField(name, Any).SetValue(value, field);
        private static object Get(object value, string name) => value.GetType().GetField(name, Any).GetValue(value);
        private static object Call(object value, string name, params object[] args)
        {
            foreach (MethodInfo method in value.GetType().GetMethods(Any))
                if (method.Name == name && method.GetParameters().Length == args.Length) return method.Invoke(value, args);
            throw new MissingMethodException(name);
        }
        private ScriptableObject Settings()
        {
            var value = ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings")); owned.Add(value);
            Set(value, "usePilotCueStyle", true); Set(value, "useCustomPrefabs", false); Set(value, "globalScale", 1f);
            Set(value, "edgePadding", 50f); return value;
        }
        private GameObject Host(string name, params Type[] components)
        { var value = new GameObject(name, components); owned.Add(value); return value; }
        private Component Pool(Canvas canvas, ScriptableObject settings) =>
            (Component)T("IndicatorSystem.Display.IndicatorPool").GetMethod("CreateOnCanvas").Invoke(null, new object[] { canvas, settings });
        private object Data()
        {
            var data = Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));
            Set(data, "Id", "LAYER-TEST"); Set(data, "Label", "TEST"); Set(data, "IsActive", true);
            Set(data, "Type", Enum.Parse(T("IndicatorSystem.Core.IndicatorType"), "Traffic"));
            Set(data, "AircraftType", Enum.Parse(T("TrafficRadar.TrafficRadarDataManager+AircraftType"), "Commercial"));
            Set(data, "Visibility", Enum.Parse(T("IndicatorSystem.Core.IndicatorVisibility"), "OffScreen"));
            Set(data, "Color", Color.cyan); Set(data, "DistanceNM", 12f); Set(data, "ArrowRotation", 90f);
            Set(data, "ScreenPosition", new Vector2(1400, 540)); Set(data, "ProjectionRect", new Rect(0, 0, 1920, 1080));
            return data;
        }
        [SetUp] public void PreserveExpansionPreference() { hadExpanded = PlayerPrefs.HasKey(ExpandedKey); expanded = PlayerPrefs.GetInt(ExpandedKey); }
        [TearDown]
        public void Cleanup()
        {
            foreach (var item in owned) if (item != null) UnityEngine.Object.DestroyImmediate(item); owned.Clear();
            if (hadExpanded) PlayerPrefs.SetInt(ExpandedKey, expanded); else PlayerPrefs.DeleteKey(ExpandedKey);
            PlayerPrefs.Save();
        }

        [Test]
        public void RealPoolAndLazyPilotCueInheritHudLayerBeforeAnyPresentationSweep()
        {
            var host = Host("HUD canvas", typeof(RectTransform), typeof(Canvas)); host.layer = 30;
            var settings = Settings(); Component pool = Pool(host.GetComponent<Canvas>(), settings);
            Assert.That(pool.gameObject.layer, Is.EqualTo(30));
            object data = Data(); var element = (Component)Call(pool, "GetIndicator", "LAYER-TEST", data);
            Call(element, "UpdateIndicator", data, settings);
            foreach (Transform child in element.GetComponentsInChildren<Transform>(true))
                Assert.That(child.gameObject.layer, Is.EqualTo(30), child.name);
            foreach (Graphic graphic in element.GetComponentsInChildren<Graphic>(true))
                Assert.That(graphic.raycastTarget, Is.False, graphic.name);
            Assert.That(element.GetComponent<CanvasGroup>().blocksRaycasts, Is.False);
            Assert.That(element.transform.Find("Pilot cue/Readout"), Is.Not.Null, "Check the lazy real cue, not only prewarmed roots.");
        }

        [Test]
        public void CueControlsAndLateKeyChildrenInheritTheirCanvasLayerImmediately()
        {
            var canvas = Host("HUD control canvas", typeof(RectTransform), typeof(Canvas)); canvas.layer = 30;
            var source = Host("Inactive controller"); source.SetActive(false);
            Component controller = source.AddComponent(T("IndicatorSystem.Controller.IndicatorSystemController")); Set(controller, "settings", Settings());
            Component panel = (Component)T("IndicatorSystem.Display.IndicatorControlsPanel").GetMethod("Create")
                .Invoke(null, new object[] { controller, canvas.transform });
            owned.Add(panel.gameObject); Call(panel, "ToggleLegend");
            foreach (Transform child in panel.GetComponentsInChildren<Transform>(true))
                Assert.That(child.gameObject.layer, Is.EqualTo(30), child.name);
            Assert.That(panel.GetComponentsInChildren<Button>(true).Length, Is.GreaterThanOrEqualTo(12));
        }

        private void WorldHud(out Camera camera, out Canvas canvas)
        {
            var view = Host("Wide headset view", typeof(Camera)); camera = view.GetComponent<Camera>();
            var texture = new RenderTexture(1920, 1080, 0); owned.Add(texture); camera.targetTexture = texture;
            camera.fieldOfView = 80f; camera.aspect = 1920f / 1080f;
            var host = Host("60 degree HUD", typeof(RectTransform), typeof(Canvas)); host.layer = 30; canvas = host.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
            var rect = (RectTransform)host.transform; rect.sizeDelta = new Vector2(1920, 1080);
            float metersPerUnit = 2f * 1.5f * Mathf.Tan(30f * Mathf.Deg2Rad) / 1920f;
            rect.localScale = Vector3.one * metersPerUnit; rect.position = new Vector3(0, 0, 1.5f);
        }
        private static Rect Region(Canvas canvas, Camera camera) => (Rect)T("IndicatorSystem.Controller.IndicatorSystemController")
            .GetMethod("GetProjectionRect").Invoke(null, new object[] { canvas, camera });

        [Test]
        public void ProjectedHudRegionUsesThePhysicalCanvasRatherThanTheWiderEyeFrustum()
        {
            WorldHud(out Camera camera, out Canvas canvas);
            Rect actual = Region(canvas, camera); var corners = new Vector3[4]; ((RectTransform)canvas.transform).GetWorldCorners(corners);
            Vector3 lower = camera.WorldToScreenPoint(corners[0]), upper = camera.WorldToScreenPoint(corners[2]);
            Assert.That(actual.xMin, Is.EqualTo(lower.x).Within(.01f)); Assert.That(actual.yMin, Is.EqualTo(lower.y).Within(.01f));
            Assert.That(actual.xMax, Is.EqualTo(upper.x).Within(.01f)); Assert.That(actual.yMax, Is.EqualTo(upper.y).Within(.01f));
            Assert.That(actual.width, Is.LessThan(camera.pixelRect.width * .6f));
            Assert.That(Vector2.Distance(actual.center, camera.pixelRect.center), Is.LessThan(.01f));
            canvas.transform.position += Vector3.right * 1.7f;
            Rect clipped = Region(canvas, camera);
            Assert.That(clipped.xMax, Is.EqualTo(camera.pixelRect.xMax).Within(.01f));
            Assert.That(clipped.xMin, Is.GreaterThan(camera.pixelRect.xMin));
        }

        [TestCase("behind")]
        [TestCase("outside")]
        [TestCase("degenerate")]
        public void InvalidProjectedHudRegionsFallBackToCameraViewport(string state)
        {
            WorldHud(out Camera camera, out Canvas canvas);
            if (state == "behind") canvas.transform.position = new Vector3(0, 0, -1.5f);
            else if (state == "outside") canvas.transform.position = new Vector3(100, 0, 1.5f);
            else canvas.transform.localScale = Vector3.zero;
            Assert.That(Region(canvas, camera), Is.EqualTo(camera.pixelRect));
            Assert.That(Region(null, camera), Is.EqualTo(camera.pixelRect));
        }

        [Test]
        public void UnassignedTargetCanvasUsesRealPoolCanvasForDensityAndPhysicalEdgeClamping()
        {
            WorldHud(out Camera camera, out Canvas canvas); var settings = Settings(); Component pool = Pool(canvas, settings);
            var host = Host("Inactive canvas fallback controller"); host.SetActive(false);
            Component controller = host.AddComponent(T("IndicatorSystem.Controller.IndicatorSystemController"));
            Set(controller, "settings", settings); Set(controller, "_pool", pool); Set(controller, "targetCamera", camera);
            var target = Activator.CreateInstance(T("IndicatorSystem.Integration.TrafficIndicatorTarget"));
            Set(target, "id", "HUD-EDGE"); Set(target, "distanceNM", 12f);
            Set(target, "worldPosition", camera.ViewportToWorldPoint(new Vector3(.9f, .5f, 100)));
            Call(controller, "AddOrUpdateTarget", target); Call(controller, "UpdateIndicators");
            object data = Get(Call(pool, "GetIndicator", "HUD-EDGE"), "_currentData");
            Rect region = Region(canvas, camera);
            Assert.That(Get(data, "Visibility").ToString(), Is.EqualTo("OffScreen"), "Visible in the eye frustum but beyond the physical HUD is a HUD edge cue.");
            Assert.That((Rect)Get(data, "ProjectionRect"), Is.EqualTo(region));
            float density = (float)T("IndicatorSystem.Controller.IndicatorSystemController").GetMethod("PixelsPerReferenceUnit")
                .Invoke(null, new object[] { canvas, camera });
            float padding = (float)Get(Get(controller, "_edgeConfig"), "EdgePadding");
            Assert.That(padding, Is.EqualTo(72f * density).Within(.01f), "Use projected canvas density even though serialized targetCanvas is null.");
            Vector2 anchor = (Vector2)Get(data, "ScreenPosition");
            Assert.That(anchor.x, Is.EqualTo(region.xMax - padding).Within(.02f));
            var element = (Component)Call(pool, "GetIndicator", "HUD-EDGE");
            Assert.That(((RectTransform)element.transform).anchoredPosition.x, Is.InRange(-960f, 960f));
            Rect bounds = (Rect)T("IndicatorSystem.Display.PilotIndicatorCue").GetMethod("ScreenBoundsForSettings")
                .Invoke(null, new object[] { data, density, settings });
            Assert.That(bounds.xMin, Is.GreaterThanOrEqualTo(region.xMin)); Assert.That(bounds.xMax, Is.LessThanOrEqualTo(region.xMax));
            Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(region.yMin)); Assert.That(bounds.yMax, Is.LessThanOrEqualTo(region.yMax));
        }
    }
}
