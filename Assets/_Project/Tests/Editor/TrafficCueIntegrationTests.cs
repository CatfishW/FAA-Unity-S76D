using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public class TrafficCueIntegrationTests
    {
        private static Type T(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name); if (type != null) return type; }
            throw new InvalidOperationException(name);
        }
        [Test]
        public void LabelSideUsesTheHeadsetViewportRatherThanDesktopDimensions()
        {
            var data = Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));
            data.GetType().GetField("ScreenPosition").SetValue(data, new Vector2(1100, 600));
            data.GetType().GetField("ProjectionRect").SetValue(data, new Rect(100, 50, 3000, 2000));
            Assert.That((bool)T("IndicatorSystem.Display.PilotIndicatorCue").GetMethod("LabelOnLeft").Invoke(null, new[] { data }), Is.False);
            data.GetType().GetField("ScreenPosition").SetValue(data, new Vector2(2200, 600));
            Assert.That((bool)T("IndicatorSystem.Display.PilotIndicatorCue").GetMethod("LabelOnLeft").Invoke(null, new[] { data }), Is.True);
        }
        [Test]
        public void OffscreenDirectionSurvivesTargetHeadingChangesAndTheReadoutClearsTheGlyph()
        {
            var parent = new GameObject("Layered cue parent", typeof(RectTransform)); parent.layer = 30;
            UnityEngine.Object settings = null;
            try
            {
                Type cueType = T("IndicatorSystem.Display.PilotIndicatorCue");
                Component cue = (Component)cueType.GetMethod("Create").Invoke(null, new object[] { parent.transform });
                settings = ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings"));
                var data = Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));
                Type dt = data.GetType();
                void Set(string n, object v) => dt.GetField(n).SetValue(data, v);
                Set("Id", "REFERENCE"); Set("Label", "TEST"); Set("Type", Enum.Parse(T("IndicatorSystem.Core.IndicatorType"), "Traffic"));
                Set("AircraftType", Enum.Parse(T("TrafficRadar.TrafficRadarDataManager+AircraftType"), "Commercial"));
                Set("Visibility", Enum.Parse(T("IndicatorSystem.Core.IndicatorVisibility"), "OffScreen"));
                Set("ScreenPosition", new Vector2(500, 400)); Set("ProjectionRect", new Rect(0, 0, 1920, 1080));
                Set("ArrowRotation", 90f); Set("Heading", 0f); Set("HasHeading", true); Set("DistanceNM", 2f); Set("Color", Color.cyan);
                cueType.GetMethod("PresentWithSettings").Invoke(cue, new[] { data, settings });
                var arrow = cueType.GetField("_symbol", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cue);
                Type at = arrow.GetType();
                Assert.That((float)at.GetField("_angle", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(arrow), Is.EqualTo(90));
                Set("Heading", 270f); Set("RelativeHeadingDegrees", 270f);
                cueType.GetMethod("PresentWithSettings").Invoke(cue, new[] { data, settings });
                Assert.That((float)at.GetField("_angle", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(arrow), Is.EqualTo(90));
                var plate = (RectTransform)cueType.GetField("_plate", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(cue);
                Assert.That(Mathf.Abs(plate.anchoredPosition.x), Is.GreaterThan(54f));
                foreach (Graphic g in cue.GetComponentsInChildren<Graphic>(true))
                { Assert.That(g.raycastTarget, Is.False); Assert.That(g.gameObject.layer, Is.EqualTo(30)); }
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); if (settings != null) UnityEngine.Object.DestroyImmediate(settings); }
        }
        [Test]
        public void WorldHudCueBoundsUseProjectedPixelsPerReferenceUnit()
        {
            var view = new GameObject("View", typeof(Camera)); var host = new GameObject("World HUD", typeof(RectTransform), typeof(Canvas));
            var texture = new RenderTexture(1920, 1080, 0);
            try
            {
                Camera camera = view.GetComponent<Camera>(); camera.targetTexture = texture; camera.fieldOfView = 80; camera.aspect = 1920f / 1080;
                Canvas canvas = host.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
                host.transform.position = new Vector3(0, 0, 1.5f); host.transform.localScale = Vector3.one * .001f;
                float pixels = (float)T("IndicatorSystem.Controller.IndicatorSystemController").GetMethod("PixelsPerReferenceUnit")
                    .Invoke(null, new object[] { canvas, camera });
                float expected = camera.WorldToScreenPoint(host.transform.TransformPoint(Vector3.right)).x - camera.WorldToScreenPoint(host.transform.position).x;
                Assert.That(pixels, Is.EqualTo(expected).Within(.001f));
                Assert.That(pixels, Is.Not.EqualTo(canvas.scaleFactor).Within(.01f));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(view); UnityEngine.Object.DestroyImmediate(texture); }
        }
    }
}
