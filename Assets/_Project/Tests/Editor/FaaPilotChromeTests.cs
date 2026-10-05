using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;

namespace FAA.Customization.Tests
{
    /// <summary>Screen-chrome unit: one bar (zone Z6), merged status chip, flyouts, key list, view dock and docked cue/brief flyouts.</summary>
    public sealed class FaaPilotChromeTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        private static Type ChromeType => T("FAA.Customization.FaaPilotChrome");
        private static object Enum(string type, string value) => System.Enum.Parse(T(type), value);
        private static object Call(object target, string method, params object[] args)
        {
            var type = target as Type ?? target.GetType();
            var m = type.GetMethods(Any).First(x => x.Name == method && x.GetParameters().Length == args.Length);
            return m.Invoke(target is Type ? null : target, args);
        }
        private static object Prop(object target, string name) => target.GetType().GetProperty(name, Any).GetValue(target);
        private static object Field(object target, string name) => target.GetType().GetField(name, Any).GetValue(target);
        private static object Static(string type, string name) => T(type).GetField(name, Any).GetValue(null);
        private static float Style(string name) => (float)Static("FAA.Customization.FaaHudStyle", name);
        private static Color StyleColor(string name) => (Color)Static("FAA.Customization.FaaHudStyle", name);
        private static float ChromeConst(string name) => (float)Static("FAA.Customization.FaaPilotChrome", name);
        private static string KeepOutKind(Component host)
        {
            var region = host.GetComponent(T("FAA.Customization.FaaHudKeepOutRegion"));
            return region == null ? "none" : Prop(region, "Kind").ToString();
        }

        private Component chrome;
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();

        [SetUp]
        public void CreateChrome()
        {
            chrome = (Component)ChromeType.GetMethod("CreateForTests").Invoke(null, null);
            owned.Add(chrome.gameObject);
        }
        [TearDown]
        public void DestroyChrome()
        {
            ChromeType.GetMethod("SetDeveloperMode").Invoke(null, new object[] { false });
            foreach (var o in owned) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            owned.Clear();
        }

        private void Add(string id, string cluster, float order, string caption, string key = null, string shortCaption = null) =>
            Call(chrome, "AddButton", id, Enum("FAA.Customization.FaaChromeCluster", cluster), order, caption, key, null, shortCaption);
        private void Report(string source, string severity, string text, int rank = 0, bool immediate = false) =>
            Call(chrome, "ReportStatus", source, Enum("FAA.Customization.FaaChromeSeverity", severity), text, "detail " + source, rank, immediate);

        [Test]
        public void Bar_IsOneSlimBandInZoneZ6_WithChromeKeepOutAndSortAboveFlightHud()
        {
            var bar = (RectTransform)Prop(chrome, "Bar");
            Assert.That(bar.anchorMin, Is.EqualTo(Vector2.zero));
            Assert.That(bar.anchorMax, Is.EqualTo(new Vector2(1, 0)), "Full-width band");
            Assert.That(1080 - bar.offsetMax.y, Is.EqualTo(1018).Within(.01), "Top of bar at reference y 1018 from the top");
            Assert.That(1080 - bar.offsetMin.y, Is.EqualTo(1068).Within(.01), "Bottom of bar at reference y 1068 from the top");
            Assert.That(KeepOutKind(bar), Is.EqualTo("Chrome"));
            var canvas = (Canvas)Prop(chrome, "Canvas");
            Assert.That(canvas.sortingOrder, Is.GreaterThan(5000), "Flight HUD never draws over chrome");
            Assert.That(ChromeConst("FlyoutBottom"), Is.GreaterThanOrEqualTo(1080 - 1018 + 8 - 0.01f), "Flyouts open above the bar");
        }

        [Test]
        public void Buttons_ShowKeyBindings_MeetTargetSize_AndKeepEightRefGaps()
        {
            Add("a", "Right", 10, "‹ HAND STUDIO");
            Add("b", "Right", 20, "FORWARD", "R");
            Add("c", "Right", 30, "SETTINGS ›", "F9");
            Call(chrome, "Relayout", 1920f);
            var rects = new[] { "a", "b", "c" }.Select(id => (RectTransform)Call(chrome, "ButtonRect", id)).ToArray();
            foreach (var r in rects)
            {
                Assert.That(r.sizeDelta.y, Is.GreaterThanOrEqualTo(26f), "Pointer target at least 8 mm");
                foreach (var t in r.GetComponentsInChildren<TMP_Text>(true))
                    Assert.That(t.fontSize, Is.GreaterThanOrEqualTo(Style("Chrome")), t.name);
            }
            Assert.That(Call(chrome, "ButtonKeycap", "b"), Is.EqualTo("R"));
            Assert.That(rects[1].GetComponentsInChildren<TMP_Text>(true).Any(t => t.text == "R"), Is.True, "Key binding is drawn on the button");
            // Right cluster (pivot 1): -anchoredPosition.x is the distance of a button's right edge from the bar's right end.
            for (int i = 0; i < 2; i++)
            {
                float rightEdgeOfLeftButton = -rects[i].anchoredPosition.x;
                float leftEdgeOfRightButton = -rects[i + 1].anchoredPosition.x + rects[i + 1].sizeDelta.x;
                Assert.That(rightEdgeOfLeftButton - leftEdgeOfRightButton, Is.EqualTo(8f).Within(.01f), "8 ref gap between neighbours");
            }
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, (List<string>)Call(chrome, "VisibleOrder", Enum("FAA.Customization.FaaChromeCluster", "Right")));
        }

        [Test]
        public void Buttons_ActiveStateHighlightsAndOrderFollowsYaw()
        {
            Add("weather", "Right", -55, "‹ WEATHER");
            Add("traffic", "Right", 55, "TRAFFIC ›");
            Call(chrome, "SetButtonState", "traffic", true, true, null, null);
            Assert.That((bool)Call(chrome, "IsButtonActive", "traffic"), Is.True);
            var plate = ((RectTransform)Call(chrome, "ButtonRect", "traffic")).GetComponent<UnityEngine.UI.Image>();
            Assert.That(plate.color, Is.EqualTo(StyleColor("ChromeButtonActive")));
            Call(chrome, "SetButtonOrder", "weather", 120f); // pilot dragged the weather radar to the right side
            CollectionAssert.AreEqual(new[] { "traffic", "weather" }, (List<string>)Call(chrome, "VisibleOrder", Enum("FAA.Customization.FaaChromeCluster", "Right")));
            Call(chrome, "SetButtonState", "weather", false, false, null, null);
            CollectionAssert.AreEqual(new[] { "traffic" }, (List<string>)Call(chrome, "VisibleOrder", Enum("FAA.Customization.FaaChromeCluster", "Right")));
        }

        [Test]
        public void Bar_UsesShortCaptionsInsteadOfLettingClustersMeet()
        {
            Add("l1", "Left", 10, "A VERY LONG LEFT CLUSTER CAPTION", null, "L1");
            Add("r1", "Right", 10, "A VERY LONG RIGHT CLUSTER CAPTION", "F9", "R1");
            Call(chrome, "Relayout", 1920f);
            Assert.That((bool)Prop(chrome, "Compact"), Is.False);
            Call(chrome, "Relayout", 600f);
            Assert.That((bool)Prop(chrome, "Compact"), Is.True);
            Assert.That(Call(chrome, "ButtonCaption", "r1"), Is.EqualTo("R1"));
        }

        [Test]
        public void StatusChip_HiddenUntilReported_MostSevereFirst_SecondaryQuiet()
        {
            Assert.That((bool)Prop(chrome, "StatusVisible"), Is.False, "No report, no chip");
            Report("data", "Caution", "FALLBACK DATA");
            Report("terrain", "Caution", "TERRAIN LOADING 22/106", 1);
            Assert.That(Prop(chrome, "StatusSeverity").ToString(), Is.EqualTo("Caution"));
            var text = (string)Prop(chrome, "StatusText");
            Assert.That(text, Does.StartWith("FALLBACK DATA"));
            Assert.That(text, Does.Contain("TERRAIN LOADING 22/106"));
            Report("data", "Warning", "NO FLIGHT DATA");
            Assert.That(Prop(chrome, "StatusSeverity").ToString(), Is.EqualTo("Warning"));
            Assert.That((string)Prop(chrome, "StatusText"), Does.StartWith("NO FLIGHT DATA"));
            Assert.That(Prop(chrome, "StatusDetail"), Is.EqualTo("detail data"));
            Assert.That(FaaPilotChromeColor("Warning"), Is.EqualTo(StyleColor("Red")));
            Assert.That(FaaPilotChromeColor("Caution"), Is.EqualTo(StyleColor("Amber")));
        }
        private static Color FaaPilotChromeColor(string severity) =>
            (Color)ChromeType.GetMethod("ColorFor").Invoke(null, new[] { Enum("FAA.Customization.FaaChromeSeverity", severity) });

        [Test]
        public void StatusChip_FallsBackToNominalOnlyAfterClearDelay_AndNominalIsShortAndQuiet()
        {
            Report("data", "Warning", "DATA STALE");
            Report("data", "Nominal", "DATA LIVE");
            Assert.That(Prop(chrome, "StatusSeverity").ToString(), Is.EqualTo("Warning"), "A flapping source must not flicker the chip");
            Report("data", "Nominal", "DATA LIVE", 0, true);
            Report("terrain", "Nominal", "", 1);
            Assert.That(Prop(chrome, "StatusSeverity").ToString(), Is.EqualTo("Nominal"));
            Assert.That(Prop(chrome, "StatusText"), Is.EqualTo("DATA LIVE"), "Silent nominal terrain adds nothing");
            Call(chrome, "ClearStatus", "data");
            Assert.That((bool)Prop(chrome, "StatusVisible"), Is.False, "Removed sources leave no stale text");
        }

        [Test]
        public void Flyouts_OnlyOneIsOpenAtATime()
        {
            int closedA = 0;
            Call(chrome, "RegisterFlyout", "a", (Action)(() => closedA++));
            Call(chrome, "RegisterFlyout", "b", (Action)(() => { }));
            Call(chrome, "NotifyFlyoutOpened", "a");
            Call(chrome, "NotifyFlyoutOpened", "b");
            Assert.That(closedA, Is.EqualTo(1));
            Assert.That(Prop(chrome, "OpenFlyoutId"), Is.EqualTo("b"));
            Call(chrome, "NotifyFlyoutClosed", "a");
            Assert.That(Prop(chrome, "OpenFlyoutId"), Is.EqualTo("b"), "Closing a flyout that is not open changes nothing");
        }

        [Test]
        public void KeyList_IsOffByDefault_ListsPilotBindingsOnly_DeveloperKeysOnlyInDeveloperMode()
        {
            Assert.That((bool)Prop(chrome, "HelpVisible"), Is.False);
            Assert.That((bool)Call(chrome, "HasButton", "help"), Is.True);
            Assert.That(Call(chrome, "ButtonKeycap", "help"), Is.EqualTo("F1"));
            Call(chrome, "SetHelpEntry", "F9", "Settings", 20, false);
            Call(chrome, "SetHelpEntry", "\\", "XR simulator: cycle devices", 920, true);
            Call(chrome, "SetHelpEntry", "CTRL+ALT+T", "Test developer tool", 950, true);
            Call(chrome, "SetHelpVisible", true);
            Assert.That((bool)Prop(chrome, "HelpVisible"), Is.True);
            Assert.That((bool)Call(chrome, "IsButtonActive", "help"), Is.True);
            var text = (string)Prop(chrome, "HelpText");
            Assert.That(text, Does.Contain("F9  Settings"));
            Assert.That(text, Does.Not.Contain("[DEV]"), "The pilot list never shows developer bindings");
            Assert.That(text, Does.Not.Contain("CTRL+ALT+T").And.Not.Contain("Test developer tool"));
            Assert.That(VisibleHelpTexts().Any(t => t.Contains("Test developer tool") || t == "DEVELOPER"), Is.False, "Nothing developer-only is drawn");
            ChromeType.GetMethod("SetDeveloperMode").Invoke(null, new object[] { true });
            text = (string)Prop(chrome, "HelpText");
            Assert.That(text.IndexOf("[DEV] CTRL+ALT+T", StringComparison.Ordinal), Is.GreaterThan(text.IndexOf("F9", StringComparison.Ordinal)), "Developer keys are grouped last");
            Assert.That(text, Does.Contain("[DEV] CTRL+SHIFT+D"), "Developer mode lists how to leave it");
            Assert.That(VisibleHelpTexts(), Does.Contain("DEVELOPER"));
            Assert.That((string)Prop(chrome, "StatusText"), Does.Contain("DEVELOPER MODE"), "Developer mode is annunciated as a quiet status");
            ChromeType.GetMethod("SetDeveloperMode").Invoke(null, new object[] { false });
            Assert.That((string)Prop(chrome, "HelpText"), Does.Not.Contain("[DEV]"));
            Call(chrome, "ToggleHelp");
            Assert.That((bool)Prop(chrome, "HelpVisible"), Is.False);
        }
        private IEnumerable<string> VisibleHelpTexts() =>
            ((RectTransform)Prop(chrome, "HelpRect")).GetComponentsInChildren<TMP_Text>(false).Select(t => t.text);

        [Test]
        public void KeyList_StaysClearOfTheTqColumn_WrapsLongMeanings_AndClosesLikeEveryFlyout()
        {
            Assert.That(ChromeConst("FlyoutLeft") + ChromeConst("FlyoutMaxWidth"), Is.LessThanOrEqualTo(500f - 16f), "At default size the slot ends 16 ref left of the IAS box (~500) and TQ column (~520)");
            Call(chrome, "SetHelpEntry", "F9", "Settings panel: open and turn to it, then close it and return to the forward view", 20, false);
            Call(chrome, "SetHelpVisible", true);
            var help = (RectTransform)Prop(chrome, "HelpRect");
            Assert.That(ChromeConst("FlyoutLeft") + help.sizeDelta.x, Is.LessThanOrEqualTo(ChromeConst("FlyoutLeft") + ChromeConst("FlyoutMaxWidth") + .01f));
            var meaning = help.GetComponentsInChildren<TMP_Text>(false).First(t => t.text.StartsWith("Settings panel", StringComparison.Ordinal));
            Assert.That(meaning.textWrappingMode, Is.EqualTo(TextWrappingModes.Normal), "Long meanings wrap instead of running into the TQ column");
            Assert.That(meaning.rectTransform.anchoredPosition.x + meaning.rectTransform.sizeDelta.x, Is.LessThanOrEqualTo(help.sizeDelta.x + .01f));
            foreach (var t in help.GetComponentsInChildren<TMP_Text>(false))
                Assert.That(t.fontSize, Is.GreaterThanOrEqualTo(Style("Chrome")), t.name);
            Assert.That(help.GetComponentsInChildren<TMP_Text>(false).Any(t => (t.text ?? "") == "CLOSE"), Is.True, "Same CLOSE [Esc] control as every flyout");
            Assert.That(KeepOutKind(help), Is.EqualTo("Chrome"));
            // A click outside the list (and outside its bar button) closes it.
            Call(chrome, "CloseOnClickOutside", new Vector2(-5000f, -5000f));
            Assert.That((bool)Prop(chrome, "HelpVisible"), Is.False);
        }

        [TestCase(true)]   // TQ column keep-out at 520 ref (693 px at 2560x1440): the flyout ends 16 ref short of it
        [TestCase(false)]  // a rectangle on the right half never limits the left slot
        public void FlyoutWidth_FollowsTheLeftFlightColumnKeepOut(bool leftColumn)
        {
            const float scale = 1.333f;
            float x = leftColumn ? 520f * scale : 1700f;
            var rects = new List<Rect> { new Rect(x, 280f, 150f, 200f) };
            float width = (float)ChromeType.GetMethod("FlyoutAvailableWidth").Invoke(null, new object[] { rects, scale, 2560f, 400f });
            if (!leftColumn) Assert.That(width, Is.EqualTo(float.MaxValue));
            else Assert.That(width, Is.EqualTo(520f - 16f - 16f).Within(.01f), "left edge of the keep-out minus the 16 ref gap, from the 16 ref slot origin");
            var above = new List<Rect> { new Rect(520f * scale, 1300f, 150f, 100f) }; // above the flyout band (FMA row etc.)
            Assert.That((float)ChromeType.GetMethod("FlyoutAvailableWidth").Invoke(null, new object[] { above, scale, 2560f, 400f }), Is.EqualTo(float.MaxValue));
        }

        // ---------------------------------------------------------------- status sources

        [TestCase(false, "Automatic", false, true, true, false, "Nominal", "DATA LIVE")]
        [TestCase(false, "Automatic", false, false, true, false, "Warning", "DATA STALE")]
        [TestCase(false, "Automatic", true, true, false, false, "Caution", "FALLBACK DATA")]
        [TestCase(false, "Automatic", true, false, false, false, "Warning", "NO FLIGHT DATA")]
        [TestCase(false, "Automatic", false, false, false, true, "Status", "DATA SEARCHING")]
        [TestCase(false, "LocalOnly", false, false, false, false, "Warning", "NO LOCAL SOURCE")]
        [TestCase(false, "Stopped", false, false, false, false, "Warning", "DATA STOPPED")]
        [TestCase(true, "Stopped", false, false, false, false, "Warning", "DATA CONFIG ERROR")]
        public void DataSource_SeverityEscalatesWithTheFault(bool configError, string mode, bool fallback, bool healthy, bool selected, bool searching, string severity, string text)
        {
            var type = T("FAA.XPlaneIntegration.Runtime.XPlaneSourceDiscovery");
            var m = System.Enum.Parse(type.GetNestedType("SelectionMode"), mode);
            var args = new object[] { configError, m, fallback, healthy, selected, searching };
            Assert.That(type.GetMethod("ClassifySource").Invoke(null, args).ToString(), Is.EqualTo(severity));
            Assert.That(type.GetMethod("ChipText").Invoke(null, args), Is.EqualTo(text));
        }

        [TestCase("READY", 60.0, "Nominal", "")]
        [TestCase("POSITION STALE", 60.0, "Nominal", "")]
        [TestCase("LOADING", 1.0, "Nominal", "TERRAIN LOADING 22/106")]
        [TestCase("LOADING", 10.0, "Caution", "TERRAIN LOADING 22/106")]
        [TestCase("NO COVERAGE", 10.0, "Caution", "NO TERRAIN COVERAGE")]
        [TestCase("CONNECTION REQUIRED", 10.0, "Caution", "TERRAIN OFFLINE")]
        [TestCase("CONFIGURATION ERROR", 0.0, "Caution", "TERRAIN CONFIG ERROR")]
        public void Terrain_TransientAndCascadingStatesStaySilent(string state, double seconds, string severity, string text)
        {
            var type = T("FAA.XPlaneIntegration.Runtime.XPlaneTerrainStreamer");
            Assert.That(type.GetMethod("ClassifyTerrain").Invoke(null, new object[] { state, seconds }).ToString(), Is.EqualTo(severity));
            Assert.That(type.GetMethod("TerrainChipText").Invoke(null, new object[] { state, 22, 106 }), Is.EqualTo(text));
        }

        // ---------------------------------------------------------------- XR simulator developer UI

        [Test]
        public void XrSimulatorUi_HiddenOnPilotViewByDefault_AndPilotKeysWin()
        {
            var type = T("FAA.Headset.XR3HeadsetCompatibility");
            Assert.That(type.GetMethod("SimulatorUiShouldShow").Invoke(null, new object[] { true, false }), Is.False);
            Assert.That(type.GetMethod("SimulatorUiShouldShow").Invoke(null, new object[] { true, true }), Is.True, "Developer toggle shows it");
            Assert.That(type.GetMethod("SimulatorUiShouldShow").Invoke(null, new object[] { false, false }), Is.True);
            var bind = type.GetMethod("PilotSafeSimulatorBinding");
            Assert.That(bind.Invoke(null, new object[] { "Cycle Devices", "<Keyboard>/tab", false }), Is.EqualTo("<Keyboard>/backslash"), "Tab belongs to COMMANDS");
            Assert.That(bind.Invoke(null, new object[] { "Reset", "<Keyboard>/r", true }), Is.EqualTo(string.Empty), "R belongs to FORWARD");
            Assert.That(bind.Invoke(null, new object[] { "X Constraint", "<Keyboard>/v", true }), Is.EqualTo(string.Empty));
            Assert.That(bind.Invoke(null, new object[] { "Reset", "<Keyboard>/r", false }), Is.Null, "With the simulated HMD driving the view, XRI reset IS the forward reset");
            var keys = (string)type.GetField("SimulatorUiToggleKeys").GetValue(null);
            Assert.That(keys, Does.Not.Contain("TAB").And.Not.EqualTo("R"));
        }

        // ---------------------------------------------------------------- view dock

        [Test]
        public void ViewDock_FollowsPanelYaw_AndHidesCameraTurnsInNativeXr()
        {
            var type = T("FAA.Customization.FaaSpatialWorkspace");
            var order = (string[])type.GetMethod("ChromeViewOrder").Invoke(null, new object[] { -90f, -55f, 55f, 90f });
            CollectionAssert.AreEqual(new[] { "view.hands", "view.weather", "view.forward", "view.traffic", "settings" }, order,
                "HAND STUDIO, WEATHER, FORWARD, TRAFFIC, SETTINGS from left to right");
            order = (string[])type.GetMethod("ChromeViewOrder").Invoke(null, new object[] { -90f, -120f, 123f, 90f });
            CollectionAssert.AreEqual(new[] { "view.weather", "view.hands", "view.forward", "settings", "view.traffic" }, order);
            var visible = type.GetMethod("ChromeViewButtonVisible");
            foreach (var id in new[] { "view.hands", "view.weather", "view.forward", "view.traffic" })
            {
                Assert.That(visible.Invoke(null, new object[] { id, false }), Is.True, id);
                Assert.That(visible.Invoke(null, new object[] { id, true }), Is.False, id + " cannot turn a tracked HMD");
            }
            Assert.That(visible.Invoke(null, new object[] { "settings", true }), Is.True);
            Assert.That(visible.Invoke(null, new object[] { "view.recenter", true }), Is.True);
            Assert.That(visible.Invoke(null, new object[] { "view.recenter", false }), Is.False);
            Assert.That(type.GetMethod("ToggleSettingsInspection"), Is.Not.Null, "F9 entry point that opens and shows Settings");
        }

        [Test]
        public void InspectionHooks_ForwardHudDimmingEndsWhenTheViewReturnsForward()
        {
            var type = T("FAA.Customization.FaaSpatialWorkspace");
            var inspection = T("FAA.Customization.FaaHudInspection");
            var go = new GameObject("Workspace inspection hook test"); owned.Add(go);
            var workspace = go.AddComponent(type); // edit mode: no Awake, no binding, Current untouched
            var setInspected = type.GetProperty("InspectedPanelId").GetSetMethod(true);
            try
            {
                // R / right-drag look: the camera is no longer inspecting, so the sync ends the dimming.
                inspection.GetMethod("Begin").Invoke(null, new object[] { "weather" });
                setInspected.Invoke(workspace, new object[] { "weather" });
                Assert.That((bool)inspection.GetProperty("Active").GetValue(null), Is.True);
                type.GetMethod("SyncInspectionState", Any).Invoke(workspace, null);
                Assert.That(type.GetProperty("InspectedPanelId").GetValue(workspace), Is.Null);
                Assert.That((bool)inspection.GetProperty("Active").GetValue(null), Is.False);
                // FORWARD button / panel close path.
                inspection.GetMethod("Begin").Invoke(null, new object[] { "settings" });
                setInspected.Invoke(workspace, new object[] { "settings" });
                type.GetMethod("ReturnToForwardView").Invoke(workspace, null);
                Assert.That((bool)inspection.GetProperty("Active").GetValue(null), Is.False);
                Assert.That(inspection.GetProperty("TargetId").GetValue(null), Is.EqualTo(""));
            }
            finally { inspection.GetMethod("ResetImmediate").Invoke(null, null); }
        }

        // ---------------------------------------------------------------- docked flyouts

        [Test]
        public void CueControls_DockAsChromeFlyout_StartCollapsed_KeyStacksAbove()
        {
            var root = new GameObject("Cue controls canvas", typeof(RectTransform), typeof(Canvas)); owned.Add(root);
            root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
            var source = new GameObject("Cue controls test source"); source.SetActive(false); owned.Add(source);
            var settings = ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings")); owned.Add(settings);
            var controller = source.AddComponent(T("IndicatorSystem.Controller.IndicatorSystemController"));
            controller.GetType().GetField("settings", Any).SetValue(controller, settings);
            var panelType = T("IndicatorSystem.Display.IndicatorControlsPanel");
            var panel = (Component)panelType.GetMethod("Create").Invoke(null, new object[] { controller, root.transform });
            var rt = (RectTransform)panel.transform;
            Assert.That((bool)Prop(panel, "IsDocked"), Is.True);
            Assert.That((bool)Prop(panel, "IsExpanded"), Is.False, "Always starts collapsed");
            Assert.That(rt.sizeDelta, Is.EqualTo(Vector2.zero), "A collapsed flyout occupies nothing");
            Assert.That(rt.parent, Is.EqualTo(Prop(chrome, "FlyoutRoot")));
            Assert.That(rt.anchoredPosition, Is.EqualTo(new Vector2(ChromeConst("FlyoutLeft"), ChromeConst("FlyoutBottom"))));
            Assert.That((bool)Call(chrome, "HasButton", "cues"), Is.True);
            Call(panel, "ToggleExpanded");
            Assert.That(rt.sizeDelta, Is.EqualTo((Vector2)panelType.GetField("DockedSize").GetValue(null)));
            Assert.That(Prop(chrome, "OpenFlyoutId"), Is.EqualTo("cues"));
            Assert.That(ChromeConst("FlyoutLeft") + rt.sizeDelta.x, Is.LessThanOrEqualTo(ChromeConst("FlyoutLeft") + ChromeConst("FlyoutMaxWidth")), "Left of the IAS/TQ column");
            Assert.That(rt.GetComponentsInChildren<TMP_Text>(true).Any(t => (t.text ?? "") == "CLOSE"), Is.True, "Same CLOSE [Esc] control as every flyout");
            Assert.That(rt.GetComponentsInChildren<TMP_Text>(true).Any(t => (t.text ?? "") == "TRAFFIC CUES"), Is.True);
            Assert.That(rt.GetComponentsInChildren<TMP_Text>(true).Any(t => (t.text ?? "").Contains("TAP")), Is.False, "No 'tap' wording on a mouse desktop");
            Assert.That(rt.GetComponentsInChildren<TMP_Text>(true).Any(t => (t.text ?? "").Contains("MARKERS   ON") || (t.text ?? "").Contains("MARKERS   OFF")), Is.False, "State lives in the ON/OFF segments, not in the row title");
            Call(panel, "ToggleLegend");
            var legend = ((GameObject)Prop(panel, "Legend")).GetComponent<RectTransform>();
            Assert.That(legend.pivot.y, Is.EqualTo(0f));
            Assert.That(legend.anchoredPosition.y, Is.GreaterThan(0f), "Symbol key stacks above the panel, never over the VSI");
            Assert.That(legend.sizeDelta.x, Is.LessThanOrEqualTo(rt.sizeDelta.x));
            Call(chrome, "NotifyFlyoutOpened", "brief");
            Assert.That((bool)Prop(panel, "IsExpanded"), Is.False, "Opening another flyout closes the cue controls");
            // The bar names the action; the state is a separate badge (never 'CUES OFF' as a caption).
            Assert.That(panelType.GetMethod("ChromeCaption").Invoke(null, new object[] { false, true }), Is.EqualTo("CUES"));
            Assert.That(panelType.GetMethod("ChromeCaption").Invoke(null, new object[] { false, false }), Is.EqualTo("CUES"));
            Assert.That(panelType.GetMethod("ChromeBadge").Invoke(null, new object[] { false, false }), Is.EqualTo("OFF"));
            Assert.That(panelType.GetMethod("ChromeBadge").Invoke(null, new object[] { true, false }), Is.EqualTo("TFC ONLY"));
            Assert.That(panelType.GetMethod("ChromeBadge").Invoke(null, new object[] { false, true }), Is.EqualTo("WX ONLY"));
            Assert.That(panelType.GetMethod("ChromeBadge").Invoke(null, new object[] { true, true }), Is.EqualTo("ON"));
            Assert.That(panelType.GetMethod("RangeIndex").Invoke(null, new object[] { 40f }), Is.EqualTo(2));
            Assert.That(panelType.GetMethod("RangeIndex").Invoke(null, new object[] { 33f }), Is.EqualTo(-1), "An off-preset range lights no segment");
            foreach (var t in panel.GetComponentsInChildren<TMP_Text>(true))
                Assert.That(t.fontSize, Is.GreaterThanOrEqualTo(Style("Chrome")), t.name + " uses the chrome type floor");
        }

        [Test]
        public void PilotBrief_StartsCollapsed_AndOpensInTheFixedSlotAboveTheBar()
        {
            var owner = new GameObject("Pilot brief chrome fixture"); owned.Add(owner);
            var panelType = T("FAA.Explanations.ExplanationAssistantPanel");
            var panel = owner.AddComponent(panelType);
            if (Prop(panel, "DockRect") == null) Call(panel, "RebuildView");
            Assert.That((bool)Prop(panel, "IsDocked"), Is.True);
            Assert.That((bool)Prop(panel, "IsOpen"), Is.False, "The brief starts collapsed");
            Assert.That((bool)Call(chrome, "HasButton", "brief"), Is.True);
            Assert.That(((RectTransform)Prop(panel, "LauncherRect")).gameObject.activeSelf, Is.False, "No second launcher in the boresight column");
            Call(chrome, "Press", "brief");
            Assert.That((bool)Prop(panel, "IsOpen"), Is.True);
            Assert.That((bool)Call(chrome, "IsButtonActive", "brief"), Is.True);
            var dock = (RectTransform)Prop(panel, "DockRect");
            var canvasRect = (RectTransform)owner.GetComponentInChildren<Canvas>(true).transform;
            float width = canvasRect.rect.width > 1 ? canvasRect.rect.width : 1920f;
            float left = dock.anchoredPosition.x - dock.sizeDelta.x * .5f + width * .5f;
            Assert.That(left, Is.EqualTo(ChromeConst("FlyoutLeft")).Within(.01));
            Assert.That(dock.anchoredPosition.y, Is.EqualTo(ChromeConst("FlyoutBottom")).Within(.01), "Opens above the bar");
            Assert.That(left + dock.sizeDelta.x, Is.LessThan(540f), "Never under the IAS/TQ column or the boresight column");
            var result = (RectTransform)Prop(panel, "ResultRect");
            float dockedHeight = (float)panelType.GetField("DockedHeight").GetValue(null);
            Assert.That(dock.sizeDelta.y, Is.EqualTo(dockedHeight).Within(.01));
            Assert.That(result.anchoredPosition.y, Is.EqualTo(ChromeConst("FlyoutBottom") + dockedHeight + 8).Within(.01));
            Assert.That(KeepOutKind(dock), Is.EqualTo("Chrome"));
            var captions = dock.GetComponentsInChildren<TMP_Text>(true).Select(t => t.text).ToArray();
            CollectionAssert.IsSubsetOf(new[] { "TFC BRIEF", "WX BRIEF", "CHART BRIEF", "STATUS BRIEF", "CLOSE", "OPTIONS" }, captions,
                "Brief actions say they request a brief, so they never read like the bar's TRAFFIC / WEATHER view buttons");
            Assert.That(captions.Any(c => c.Contains("Tap")), Is.False);
            Assert.That(Call(chrome, "ButtonCaption", "brief"), Is.EqualTo("BRIEF"), "State is a badge, never part of the caption");
            // Unusual attitude: the brief disappears with the declutter and returns unchanged.
            Call(panel, "ApplyDeclutter", true);
            Assert.That((bool)Prop(panel, "Decluttered"), Is.True);
            Assert.That(owner.GetComponentInChildren<CanvasGroup>(true).alpha, Is.EqualTo(0f));
            Call(panel, "ApplyDeclutter", false);
            Assert.That((bool)Prop(panel, "IsOpen"), Is.True, "Recovery restores the brief as it was");
            Call(chrome, "NotifyFlyoutOpened", "cues");
            Assert.That((bool)Prop(panel, "IsOpen"), Is.False, "Only one flyout at a time");
            foreach (var t in owner.GetComponentsInChildren<TMP_Text>(true))
                Assert.That(t.fontSize, Is.GreaterThanOrEqualTo(Style("Chrome")), t.name + " uses the chrome type floor");
        }

        // ---------------------------------------------------------------- COMMANDS (C1 / M13)

        [Test]
        public void Commands_OpenAsChromeFlyoutAndNeverHideFlightSymbology()
        {
            var roots = new[] { "Second Interation GUI", "FAA UI Toolkit HUD", "FAASymbologyCanvasWorldSpace", "FAAHeadingTapeCanvas" }
                .Select(n => { var g = new GameObject(n); owned.Add(g); return g; }).ToArray();
            var type = T("VoiceControl.UI.UIToolkitRadialMenuAdvanced");
            var go = new GameObject("Commands flyout test"); owned.Add(go);
            var menu = go.AddComponent(type);
            type.GetField("hideHudWhileOpen", Any).SetValue(menu, true); // what ExperimentScene serializes
            type.GetMethod("ApplyAviationHudPreset").Invoke(menu, new object[] { false });
            Assert.That((bool)type.GetProperty("HideHudWhileOpen").GetValue(menu), Is.False, "The preset can never turn HUD hiding back on");
            Assert.That(type.GetMethod("EnforceHudSuppression", Any), Is.Null, "No SetActive suppression path remains");
            Assert.That((bool)type.GetField("useBackdrop", Any).GetValue(menu), Is.False, "No full-screen backdrop over the outside view");

            type.GetMethod("SetChromeFlyoutOpen").Invoke(menu, new object[] { chrome, true });
            Assert.That((bool)Prop(chrome, "CommandsVisible"), Is.True);
            Assert.That((bool)type.GetProperty("IsOpen").GetValue(menu), Is.True);
            Assert.That(Prop(chrome, "OpenFlyoutId"), Is.EqualTo("commands"));
            foreach (var root in roots) Assert.That(root.activeInHierarchy, Is.True, root.name + " stays active with COMMANDS open");

            var rect = (RectTransform)Prop(chrome, "CommandsRect");
            Assert.That(rect.parent, Is.EqualTo(Prop(chrome, "FlyoutRoot")));
            Assert.That(rect.anchoredPosition, Is.EqualTo(new Vector2(ChromeConst("FlyoutLeft"), ChromeConst("FlyoutBottom"))), "Lower-left flyout slot above the bar");
            Assert.That(ChromeConst("FlyoutLeft") + rect.sizeDelta.x, Is.LessThanOrEqualTo(ChromeConst("FlyoutLeft") + ChromeConst("FlyoutMaxWidth") + .01f), "Outside the IAS/TQ column");
            Assert.That(ChromeConst("FlyoutBottom") + rect.sizeDelta.y, Is.LessThan(1080f * .63f), "Below the attitude field's lower edge band");
            Assert.That(KeepOutKind(rect), Is.EqualTo("Chrome"));
            Assert.That(rect.GetComponentsInChildren<TMP_Text>(true).Any(t => (t.text ?? "") == "CLOSE"), Is.True);

            // Esc / CLOSE path, then a click outside.
            Assert.That((bool)Call(chrome, "CloseOpenFlyout"), Is.True);
            Assert.That((bool)Prop(chrome, "CommandsVisible"), Is.False);
            type.GetMethod("SetChromeFlyoutOpen").Invoke(menu, new object[] { chrome, true });
            Call(chrome, "CloseOnClickOutside", new Vector2(-5000f, -5000f));
            Assert.That((bool)Prop(chrome, "CommandsVisible"), Is.False);
            type.GetMethod("SyncDockedState", Any).Invoke(menu, new object[] { chrome });
            Assert.That((bool)type.GetProperty("IsOpen").GetValue(menu), Is.False);
            foreach (var root in roots) Assert.That(root.activeInHierarchy, Is.True);
            Assert.That(type.GetField("HelpMeaning").GetValue(null), Is.EqualTo("Commands: radars, screen cues, HUD"));
        }

        [Test]
        public void Commands_RowsShowStateAsSegments_UnknownStateLightsNothing()
        {
            var rowType = T("FAA.Customization.FaaChromeCommandRow");
            bool radarOn = false; int lastPick = -1;
            var toggle = rowType.GetMethod("Toggle").Invoke(null, new object[] { "RADARS", "WEATHER RADAR",
                (Func<bool?>)(() => radarOn), (Action<bool>)(on => radarOn = on) });
            var unknown = rowType.GetMethod("Toggle").Invoke(null, new object[] { "HUD", "FLIGHT HUD", (Func<bool?>)(() => null), (Action<bool>)(_ => { }) });
            var selector = Activator.CreateInstance(rowType, "HUD", "BRIGHTNESS %", new[] { "40", "60", "80", "100" },
                (Func<int>)(() => 2), (Action<int>)(i => lastPick = i), false);
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
            list.Add(toggle); list.Add(unknown); list.Add(selector);
            Call(chrome, "SetCommandRows", list);
            Call(chrome, "SetCommandsVisible", true);
            Assert.That((int)Prop(chrome, "CommandRowCount"), Is.EqualTo(3));
            Assert.That(Call(chrome, "CommandRowLabel", 0), Is.EqualTo("WEATHER RADAR"), "One row per function: no separate Show / Hide items");
            Assert.That((int)Call(chrome, "CommandRowState", 0), Is.EqualTo(1), "OFF lit");
            Assert.That((int)Call(chrome, "CommandRowState", 1), Is.EqualTo(-1), "Unknown state is never guessed");
            Assert.That((int)Call(chrome, "CommandRowState", 2), Is.EqualTo(2));
            Call(chrome, "PressCommand", 0, 0);
            Assert.That(radarOn, Is.True);
            Assert.That((int)Call(chrome, "CommandRowState", 0), Is.EqualTo(0), "The new state shows immediately");
            Assert.That((bool)Prop(chrome, "CommandsVisible"), Is.True, "Stays open so the change can be seen");
            Call(chrome, "PressCommand", 2, 3);
            Assert.That(lastPick, Is.EqualTo(3));
            var rect = (RectTransform)Prop(chrome, "CommandsRect");
            var texts = rect.GetComponentsInChildren<TMP_Text>(true);
            foreach (var t in texts) Assert.That(t.fontSize, Is.GreaterThanOrEqualTo(Style("Chrome")), t.name + " meets the chrome type floor");
            CollectionAssert.IsSubsetOf(new[] { "COMMANDS", "RADARS", "HUD", "ON", "OFF" }, texts.Select(t => t.text).ToArray());
            Assert.That(texts.Where(t => t.text.Length > 1 && t.text != "Esc").All(t => t.text == t.text.ToUpperInvariant()), Is.True, "ALL CAPS like the chrome bar (keycaps keep their key name)");
            var radial = T("VoiceControl.UI.UIToolkitRadialMenuAdvanced");
            Assert.That(radial.GetField("SectionRadars").GetValue(null), Is.EqualTo("RADARS"));
            Assert.That(radial.GetField("SectionCues").GetValue(null), Is.EqualTo("SCREEN CUES"), "Same name as the CUES flyout");
            Assert.That(radial.GetMethod("BrightnessIndex").Invoke(null, new object[] { 79 }), Is.EqualTo(2));
            Assert.That(radial.GetMethod("BrightnessIndex").Invoke(null, new object[] { 70 }), Is.EqualTo(-1));
        }

        [Test]
        public void UnusualAttitude_HidesOpenFlyoutsAndReleasesTheirKeepOuts()
        {
            Call(chrome, "SetHelpVisible", true);
            var help = (RectTransform)Prop(chrome, "HelpRect");
            var region = help.GetComponent(T("FAA.Customization.FaaHudKeepOutRegion"));
            Call(chrome, "ApplyFlyoutDeclutter", true);
            Assert.That((bool)Prop(chrome, "FlyoutsDecluttered"), Is.True);
            var args = new object[] { default(Rect) };
            Assert.That((bool)region.GetType().GetMethod("TryGetScreenRect").Invoke(region, args), Is.False, "A hidden flyout reserves nothing");
            Assert.That(((RectTransform)Prop(chrome, "Bar")).gameObject.activeInHierarchy, Is.True, "The bar (FORWARD) stays");
            Call(chrome, "ApplyFlyoutDeclutter", false);
            Assert.That((bool)Prop(chrome, "HelpVisible"), Is.True, "Recovery restores the flyout as it was");
        }

        [Test]
        public void StatusChip_IsAnAnnunciatorNotAButton_AndRendererSwapNeedsDeveloperMode()
        {
            Report("data", "Caution", "FALLBACK DATA");
            Assert.That((string)Call(chrome, "ButtonCaption", "status"), Does.Not.Contain("›"), "Chevrons are reserved for the view buttons' turn direction");
            var plate = ((RectTransform)Call(chrome, "ButtonRect", "status")).GetComponent<UnityEngine.UI.Image>();
            Assert.That(plate.color.a, Is.EqualTo(0f), "No button plate behind a status");
            var allowed = T("FAA.HUDToolkit.FaaHudModeSwitcher").GetMethod("HotkeyAllowed");
            Assert.That(allowed.Invoke(null, new object[] { false, true, true }), Is.False, "Bare F8 never swaps the renderer");
            Assert.That(allowed.Invoke(null, new object[] { true, true, false }), Is.False, "Ctrl+F8 needs developer mode when the pilot chrome exists");
            Assert.That(allowed.Invoke(null, new object[] { true, true, true }), Is.True);
            Assert.That(allowed.Invoke(null, new object[] { true, false, false }), Is.True, "Scenes without the pilot chrome keep Ctrl+F8");
            Call(chrome, "AddButton", "cues", Enum("FAA.Customization.FaaChromeCluster", "Left"), 30f, "CUES", null, null, null);
            Call(chrome, "SetButtonBadge", "cues", "OFF", false);
            Assert.That(Call(chrome, "ButtonCaption", "cues"), Is.EqualTo("CUES"));
            Assert.That(Call(chrome, "ButtonBadge", "cues"), Is.EqualTo("OFF"));
            Assert.That((bool)Call(chrome, "ButtonBadgeOn", "cues"), Is.False);
        }

        [Test]
        public void RadialLauncher_IsDockedAsLabelledCommandsButtonByDefault()
        {
            var type = T("VoiceControl.UI.UIToolkitRadialMenuAdvanced");
            var go = new GameObject("Radial launcher chrome test"); owned.Add(go);
            var menu = go.AddComponent(type);
            Assert.That((bool)type.GetProperty("DockLauncherInPilotChrome").GetValue(menu), Is.True);
            Assert.That((bool)type.GetProperty("LauncherDocked").GetValue(menu), Is.False, "Edit-time preview keeps the floating launcher");
            Assert.That(type.GetField("ChromeCommandsId").GetValue(null), Is.EqualTo("commands"));
        }
    }
}
