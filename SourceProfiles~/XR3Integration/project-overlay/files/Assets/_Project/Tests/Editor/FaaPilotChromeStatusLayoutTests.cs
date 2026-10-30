using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public sealed class FaaPilotChromeStatusLayoutTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private Component chrome;
        private Type type;
        private readonly string[] controls = { "brief", "cues", "commands", "help", "hands", "weather", "forward", "traffic", "settings" };

        private object Call(string name, params object[] args) => type.GetMethods(Any)
            .First(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(chrome, args);
        private object Prop(string name) => type.GetProperty(name, Any).GetValue(chrome);
        private object Enum(string name, string value) => System.Enum.Parse(type.Assembly.GetType("FAA.Customization." + name, true), value);
        private RectTransform RectOf(string id) => (RectTransform)Call("ButtonRect", id);
        private void Report(string severity, string caption) => Call("ReportStatus", "data", Enum("FaaChromeSeverity", severity), caption, "Source details", 0, true);
        private void Add(string id, string cluster, float order, string caption, string key = null, string shortCaption = null) =>
            Call("AddButton", id, Enum("FaaChromeCluster", cluster), order, caption, key, null, shortCaption);

        [SetUp]
        public void Create()
        {
            type = Type.GetType("FAA.Customization.FaaPilotChrome, Assembly-CSharp", true);
            chrome = (Component)type.GetMethod("CreateForTests").Invoke(null, null);
            Add("brief", "Left", 20, "BRIEF");
            Add("cues", "Left", 30, "CUES");
            Call("SetButtonBadge", "cues", "ON", true);
            Add("commands", "Left", 40, "COMMANDS", "Tab", "CMD");
            Add("hands", "Right", -90, "‹ HAND STUDIO", null, "‹ HANDS");
            Add("weather", "Right", -55, "‹ WEATHER", null, "‹ WX");
            Add("forward", "Right", 0, "FORWARD", "R", "FWD");
            Add("traffic", "Right", 55, "TRAFFIC ›", null, "TFC ›");
            Add("settings", "Right", 90, "SETTINGS ›", "F9", "SET ›");
        }

        [TearDown]
        public void Destroy() { if (chrome != null) UnityEngine.Object.DestroyImmediate(chrome.gameObject); }

        private void Layout(float width)
        {
            var canvas = (Canvas)Prop("Canvas");
            canvas.renderMode = RenderMode.WorldSpace;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(width, 1080);
            Call("Relayout", width);
        }

        private Rect Bounds(RectTransform rect)
        {
            var bar = (RectTransform)Prop("Bar");
            var corners = new Vector3[4]; rect.GetWorldCorners(corners);
            var a = bar.InverseTransformPoint(corners[0]); var b = bar.InverseTransformPoint(corners[2]);
            return Rect.MinMaxRect(a.x, a.y, b.x, b.y);
        }

        [TestCase(1920f)]
        [TestCase(1280f)]
        [TestCase(960f)]
        [TestCase(600f)]
        public void StatusTextSeverityAndVisibilityNeverMoveToolbarTargets(float width)
        {
            Layout(width);
            var before = controls.ToDictionary(id => id, id => Bounds(RectOf(id)));
            foreach (var state in new[] { ("Nominal", "DATA LIVE"), ("Warning", "NO FLIGHT DATA"),
                ("Caution", "TERRAIN LOADING 1/106"), ("Caution", "TERRAIN LOADING 106/106"), ("Nominal", "DATA LIVE") })
            {
                Report(state.Item1, state.Item2); Layout(width);
                foreach (var id in controls) Assert.That(Bounds(RectOf(id)), Is.EqualTo(before[id]), id + " moved when status became " + state.Item2);
            }
            Call("ClearStatus", "data"); Layout(width);
            foreach (var id in controls) Assert.That(Bounds(RectOf(id)), Is.EqualTo(before[id]), id + " moved when status disappeared");
        }

        [TestCase(1920f)]
        [TestCase(960f)]
        [TestCase(600f)]
        [TestCase(480f)]
        public void FullStatusAndControlsRemainLegibleAndNonOverlapping(float width)
        {
            Layout(width);
            Report("Warning", "NO FLIGHT DATA");
            Call("ReportStatus", "terrain", Enum("FaaChromeSeverity", "Caution"), "TERRAIN LOADING 106/106", "Terrain details", 1, true);
            Layout(width);
            var bar = (RectTransform)Prop("Bar");
            var ids = new[] { "status" }.Concat(controls).ToArray();
            for (int i = 0; i < ids.Length; i++)
            {
                Rect bounds = Bounds(RectOf(ids[i]));
                Assert.That(bounds.xMin, Is.GreaterThanOrEqualTo(bar.rect.xMin - .01f), ids[i] + " left edge");
                Assert.That(bounds.xMax, Is.LessThanOrEqualTo(bar.rect.xMax + .01f), ids[i] + " right edge");
                Assert.That(bounds.yMin, Is.GreaterThanOrEqualTo(bar.rect.yMin - .01f), ids[i] + " bottom edge");
                Assert.That(bounds.yMax, Is.LessThanOrEqualTo(bar.rect.yMax + .01f), ids[i] + " top edge");
                for (int j = 0; j < i; j++) Assert.That(bounds.Overlaps(Bounds(RectOf(ids[j]))), Is.False, ids[i] + " overlaps " + ids[j]);
            }
            var text = RectOf("status").GetComponentsInChildren<TMP_Text>().Single();
            text.ForceMeshUpdate();
            Assert.That(text.text, Does.Contain("NO FLIGHT DATA"));
            Assert.That(text.overflowMode, Is.Not.EqualTo(TextOverflowModes.Ellipsis), "Primary alerts must remain fully readable");
            Assert.That(text.isTextOverflowing, Is.False, "The full status caption must fit without clipping");
            Assert.That(text.preferredHeight, Is.LessThanOrEqualTo(text.rectTransform.rect.height + .1f));
        }

        [Test]
        public void NarrowBarMovesExistingHelpAndSeparateBriefFlyoutsAboveItsTop()
        {
            Layout(600);
            Report("Caution", "TERRAIN LOADING 106/106"); Layout(600);
            Call("SetHelpVisible", true); Layout(600);
            var bar = (RectTransform)Prop("Bar");
            var help = (RectTransform)Prop("HelpRect");
            Assert.That(bar.rect.height, Is.GreaterThan(50f), "Fixture exercises the responsive multi-row bar");
            Assert.That(Bounds(help).yMin, Is.GreaterThanOrEqualTo(bar.rect.yMax + 8f - .01f), "Shared flyouts clear the expanded bar");

            var owner = new GameObject("Separate Brief canvas responsive layout fixture");
            try
            {
                var panelType = type.Assembly.GetType("FAA.Explanations.ExplanationAssistantPanel", true);
                var panel = owner.AddComponent(panelType);
                if (panelType.GetProperty("DockRect", Any).GetValue(panel) == null)
                    panelType.GetMethod("RebuildView", Any).Invoke(panel, null);
                var panelCanvas = owner.GetComponentInChildren<Canvas>(true);
                panelCanvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)panelCanvas.transform).sizeDelta = new Vector2(600,1080);
                panelType.GetMethod("ApplyDockedLayout", Any).Invoke(panel, null);
                var dock = (RectTransform)panelType.GetProperty("DockRect", Any).GetValue(panel);
                Assert.That(dock.anchoredPosition.y, Is.EqualTo((float)Prop("CurrentFlyoutBottom")).Within(.01f), "The existing separate Brief canvas uses the responsive slot too");
            }
            finally { UnityEngine.Object.DestroyImmediate(owner); }
        }
    }
}
