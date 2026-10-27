using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public sealed class TrafficReadoutInsetTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType("IndicatorSystem." + name + ", Assembly-CSharp", true);
        private static void Set(object data, string field, object value) => data.GetType().GetField(field).SetValue(data, value);
        private static object Data(float y, Rect region)
        {
            var data = Activator.CreateInstance(T("Core.IndicatorData"));
            Set(data, "Type", Enum.Parse(T("Core.IndicatorType"), "Traffic"));
            Set(data, "Visibility", Enum.Parse(T("Core.IndicatorVisibility"), "OnScreen"));
            Set(data, "Id", "INSET"); Set(data, "Label", "TEST"); Set(data, "Color", Color.cyan);
            Set(data, "DistanceNM", 12f); Set(data, "IsActive", true);
            Set(data, "ScreenPosition", new Vector2(region.center.x, y)); Set(data, "ProjectionRect", region);
            return data;
        }
        private static float Offset(object data, float scale) => (float)T("Display.PilotIndicatorCue")
            .GetMethod("ReadoutVerticalOffset").Invoke(null, new[] { data, (object)scale });

        [TestCase(false, 1f)]
        [TestCase(true, 1f)]
        [TestCase(false, 2f)]
        [TestCase(true, 2f)]
        public void ReadoutStaysInsideTopAndBottomInsetsWhileGlyphAnchorRemainsExact(bool top, float canvasDensity)
        {
            var host = new GameObject("Readout canvas", typeof(RectTransform), typeof(Canvas));
            var settings = ScriptableObject.CreateInstance(T("Core.IndicatorSettings"));
            try
            {
                Canvas canvas = host.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.scaleFactor = canvasDensity;
                Set(settings, "globalScale", 1.25f);
                Component cue = (Component)T("Display.PilotIndicatorCue").GetMethod("Create").Invoke(null, new object[] { host.transform });
                var root = (RectTransform)cue.transform; root.anchoredPosition = new Vector2(123, 456); root.localScale = Vector3.one * 1.25f;
                var region = new Rect(100, 100, 1600, 800); float y = top ? region.yMax - 1 : region.yMin + 1;
                object data = Data(y, region); Vector2 original = root.anchoredPosition;
                T("Display.PilotIndicatorCue").GetMethod("PresentWithSettings").Invoke(cue, new object[] { data, settings });
                var plate = (RectTransform)T("Display.PilotIndicatorCue").GetField("_plate", Any).GetValue(cue);
                float scale = canvasDensity * 1.25f, center = y + plate.anchoredPosition.y * scale;
                Assert.That(root.anchoredPosition, Is.EqualTo(original), "Only the label moves; the real target anchor is untouched.");
                Assert.That(cue.transform.Find("Vector symbol").localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(cue.transform.Find("Reference aircraft symbol").localPosition, Is.EqualTo(Vector3.zero));
                Assert.That(plate.anchoredPosition.y, Is.EqualTo(Offset(data, scale)).Within(.001f));
                Assert.That(center - plate.rect.height * .5f * scale, Is.GreaterThanOrEqualTo(region.yMin + 6f * scale - .01f));
                Assert.That(center + plate.rect.height * .5f * scale, Is.LessThanOrEqualTo(region.yMax - 6f * scale + .01f));
                Rect bounds = (Rect)T("Display.PilotIndicatorCue").GetMethod("ScreenBoundsForSettings")
                    .Invoke(null, new object[] { data, scale, settings });
                Assert.That(bounds.yMin, Is.LessThanOrEqualTo(y - 48f * scale + .01f));
                Assert.That(bounds.yMax, Is.GreaterThanOrEqualTo(y + 48f * scale - .01f));
                Assert.That(bounds.yMin, Is.LessThanOrEqualTo(center - 41f * scale + .01f));
                Assert.That(bounds.yMax, Is.GreaterThanOrEqualTo(center + 41f * scale - .01f));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void TinyRegionCentresTheReadoutInsteadOfUsingInvertedClampLimits()
        {
            var region = new Rect(0, 100, 100, 20); object data = Data(119, region);
            Assert.That(119 + Offset(data, 2f) * 2f, Is.EqualTo(region.center.y).Within(.001f));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(0f)]
        [TestCase(-1f)]
        public void InvalidPixelScalesUseAFiniteFallback(float scale)
        {
            float offset = Offset(Data(101, new Rect(0, 100, 1920, 800)), scale);
            Assert.That(offset, Is.EqualTo(40f).Within(.001f));
        }

        [Test]
        public void MissingOrNonfiniteProjectionRegionDoesNotMoveTheReadout()
        {
            Assert.That(Offset(Data(101, default), 1f), Is.Zero);
            Assert.That(Offset(Data(101, new Rect(0, float.NaN, 100, 100)), 1f), Is.Zero);
        }
    }
}
