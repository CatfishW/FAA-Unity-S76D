using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public sealed class TrafficCueKeepOutTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        private static Type T(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var type = assembly.GetType(name); if (type != null) return type; }
            return null;
        }
        private static Type Controller => T("IndicatorSystem.Controller.IndicatorSystemController");
        private static object Get(object data, string field) => data.GetType().GetField(field).GetValue(data);
        private static void Set(object data, string field, object value) => data.GetType().GetField(field).SetValue(data, value);
        private static object Data(Vector2 position, bool onScreen = false)
        {
            object data = Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));
            Set(data, "Type", Enum.Parse(T("IndicatorSystem.Core.IndicatorType"), "Traffic"));
            Set(data, "Visibility", Enum.Parse(T("IndicatorSystem.Core.IndicatorVisibility"), onScreen ? "OnScreen" : "OffScreen"));
            Set(data, "ScreenPosition", position); Set(data, "ProjectionRect", new Rect(0, 0, 1920, 1080));
            Set(data, "ArrowRotation", 90f); Set(data, "ViewRelativeRotation", Quaternion.Euler(-12, 35, 20));
            Set(data, "Heading", 35f); Set(data, "PitchDegrees", 12f); Set(data, "RollDegrees", -20f);
            return data;
        }
        private static bool Place(ref object data, List<Rect> occupied, out Rect bounds)
        {
            object[] args = { data, 1f, null, occupied, null };
            bool result = (bool)Controller.GetMethod("TryPlaceOffscreenCue").Invoke(null, args);
            data = args[0]; bounds = (Rect)args[4]; return result;
        }
        private static void Inside(Rect bounds)
        {
            Assert.That(bounds.xMin, Is.GreaterThanOrEqualTo(-.01f)); Assert.That(bounds.xMax, Is.LessThanOrEqualTo(1920.01f));
            Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(-.01f)); Assert.That(bounds.yMax, Is.LessThanOrEqualTo(1080.01f));
        }

        [Test]
        public void RightEdgeReadoutMovesToNearestClearVsiSlotWithoutChangingBearingOrBodyPose()
        {
            object data = Data(new Vector2(1848, 500)); Quaternion rotation = (Quaternion)Get(data, "ViewRelativeRotation");
            var blocked = new List<Rect> { new Rect(1600, 430, 160, 150) };
            Assert.That(Place(ref data, blocked, out Rect bounds), Is.True);
            Assert.That((Vector2)Get(data, "ScreenPosition"), Is.EqualTo(new Vector2(1848, 368)));
            Assert.That(bounds.Overlaps(blocked[0]), Is.False); Inside(bounds);
            Assert.That(Get(data, "ArrowRotation"), Is.EqualTo(90f)); Assert.That(Get(data, "ViewRelativeRotation"), Is.EqualTo(rotation));
            Assert.That(Get(data, "Heading"), Is.EqualTo(35f)); Assert.That(Get(data, "PitchDegrees"), Is.EqualTo(12f)); Assert.That(Get(data, "RollDegrees"), Is.EqualTo(-20f));
        }

        [Test]
        public void TallUpperKeyChoosesTheLowerClearSlotInsteadOfEscapingTheHudAboveIt()
        {
            object data = Data(new Vector2(72, 900));
            var blocked = new List<Rect> { new Rect(0, 200, 440, 870), new Rect(0, 0, 1920, 70) };
            Assert.That(Place(ref data, blocked, out Rect bounds), Is.True);
            Assert.That((Vector2)Get(data, "ScreenPosition"), Is.EqualTo(new Vector2(72, 138)));
            foreach (Rect obstacle in blocked) Assert.That(bounds.Overlaps(obstacle), Is.False);
            Inside(bounds);
        }

        [Test]
        public void FullyBlockedEdgeIsSuppressedAndInViewAnchorNeverMoves()
        {
            var blocked = new List<Rect> { new Rect(0, 0, 1920, 1080) };
            object edge = Data(new Vector2(1848, 500));
            Assert.That(Place(ref edge, blocked, out _), Is.False); Assert.That(Get(edge, "ScreenPosition"), Is.EqualTo(new Vector2(1848, 500)));
            object inView = Data(new Vector2(960, 500), true);
            Assert.That(Place(ref inView, blocked, out _), Is.False); Assert.That(Get(inView, "ScreenPosition"), Is.EqualTo(new Vector2(960, 500)));
        }

        [Test]
        public void VisibleInstrumentAndChromeRegionsAreCollectedButAttitudeWindowAndHiddenRegionsAreNot()
        {
            var collector = (Delegate)Controller.GetField("KeepOutCollector", Any).GetValue(null);
            Type regionType = T("FAA.Customization.FaaHudKeepOutRegion");
            if (regionType == null) { Assert.That(collector, Is.Null, "Standard profile has no optional full HUD registry."); return; }
            Assert.That(collector, Is.Not.Null);
            var host = new GameObject("Keepout canvas", typeof(RectTransform), typeof(Canvas));
            var regions = new List<Component>();
            try
            {
                host.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
                var expected = new List<Rect>();
                string[] kinds = { "Symbology", "Chrome", "AttitudeWindow" };
                for (int i = 0; i < kinds.Length; i++)
                {
                    var child = new GameObject(kinds[i], typeof(RectTransform)); child.transform.SetParent(host.transform, false);
                    var rect = (RectTransform)child.transform; rect.anchoredPosition = new Vector2(100 * i, 100 * i); rect.sizeDelta = new Vector2(42, 42);
                    Component region = child.AddComponent(regionType); regions.Add(region);
                    regionType.GetMethod("Configure").Invoke(region, new object[] { "test:" + kinds[i], Enum.Parse(T("FAA.Customization.FaaKeepOutKind"), kinds[i]), 0f, rect });
                    T("FAA.Customization.FaaHudKeepOut").GetMethod("Register").Invoke(null, new object[] { region }); // Simulate the play-mode OnEnable registration in this EditMode fixture.
                    object[] args = { null }; Assert.That((bool)regionType.GetMethod("TryGetScreenRect").Invoke(region, args), Is.True); expected.Add((Rect)args[0]);
                }
                var collected = new List<Rect>(); collector.DynamicInvoke(collected, true);
                Assert.That(collected, Has.Member(expected[0])); Assert.That(collected, Has.Member(expected[1])); Assert.That(collected, Has.No.Member(expected[2]));
                regions[0].gameObject.SetActive(false); collected.Clear(); collector.DynamicInvoke(collected, true);
                Assert.That(collected, Has.No.Member(expected[0])); Assert.That(collected, Has.Member(expected[1]));
            }
            finally
            {
                foreach (var region in regions) T("FAA.Customization.FaaHudKeepOut").GetMethod("Unregister").Invoke(null, new object[] { region });
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
