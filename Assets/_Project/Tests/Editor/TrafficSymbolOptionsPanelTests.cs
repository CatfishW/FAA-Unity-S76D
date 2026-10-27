using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public sealed class TrafficSymbolOptionsPanelTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly string[] Options = { "Outline", "HeadingTip", "NavigationMarks", "TailLight", "BodyStripe", "Adaptive" };
        private const string Prefix = "FAA.Cues.TrafficStyle.";
        private readonly Dictionary<string, int?> saved = new Dictionary<string, int?>();
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private static Type Runtime(string name) => Type.GetType("IndicatorSystem." + name + ", Assembly-CSharp", true);
        private static Type Panel => Runtime("Display.TrafficSymbolOptionsPanel");
        private static object Option(string name) => Enum.Parse(Runtime("Display.TrafficSymbolStyleOption"), name);
        private static object Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Any).Invoke(instance, args);
        private static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Any).SetValue(instance, value);
        private static T Get<T>(object instance, string field) => (T)instance.GetType().GetField(field, Any).GetValue(instance);

        [SetUp]
        public void IsolateStylePreferences()
        {
            foreach (string option in Options)
            {
                string key = Prefix + option;
                saved[key] = PlayerPrefs.HasKey(key) ? (int?)PlayerPrefs.GetInt(key) : null;
                PlayerPrefs.DeleteKey(key);
            }
        }

        [TearDown]
        public void RestorePreferencesAndObjects()
        {
            foreach (var item in owned) if (item != null) UnityEngine.Object.DestroyImmediate(item);
            owned.Clear();
            foreach (var item in saved)
                if (item.Value.HasValue) PlayerPrefs.SetInt(item.Key, item.Value.Value); else PlayerPrefs.DeleteKey(item.Key);
            saved.Clear();
            PlayerPrefs.Save();
        }

        private ScriptableObject Settings()
        {
            var settings = ScriptableObject.CreateInstance(Runtime("Core.IndicatorSettings")); owned.Add(settings);
            Set(settings, "enabled", false); Set(settings, "showTrafficIndicators", false);
            Set(settings, "showWeatherIndicators", true); Set(settings, "globalOpacity", .43f);
            Set(settings, "maxDisplayDistance", 37f);
            return settings;
        }

        private Component Create(ScriptableObject settings)
        {
            var canvas = new GameObject("Traffic key test canvas", typeof(RectTransform), typeof(Canvas)); owned.Add(canvas);
            return (Component)Panel.GetMethod("Create").Invoke(null, new object[] { settings, canvas.transform, null });
        }

        private static void AssertCueStateUnchanged(ScriptableObject settings)
        {
            Assert.That(Get<bool>(settings, "enabled"), Is.False);
            Assert.That(Get<bool>(settings, "showTrafficIndicators"), Is.False);
            Assert.That(Get<bool>(settings, "showWeatherIndicators"), Is.True);
            Assert.That(Get<float>(settings, "globalOpacity"), Is.EqualTo(.43f));
            Assert.That(Get<float>(settings, "maxDisplayDistance"), Is.EqualTo(37f));
        }

        [Test]
        public void OpeningAndRefreshingTheKeyNeverPersistStyleDefaultsOrSwitchCuesOn()
        {
            var settings = Settings(); var panel = Create(settings);
            Call(panel, "RefreshState"); panel.gameObject.SetActive(false); panel.gameObject.SetActive(true); Call(panel, "RefreshState");
            foreach (string option in Options) Assert.That(PlayerPrefs.HasKey(Prefix + option), Is.False, option);
            AssertCueStateUnchanged(settings);
        }

        [TestCase("Outline")]
        [TestCase("HeadingTip")]
        [TestCase("NavigationMarks")]
        [TestCase("TailLight")]
        [TestCase("BodyStripe")]
        [TestCase("Adaptive")]
        public void ExplicitSelectionChangesAndPersistsOnlyThatStyleOption(string selected)
        {
            var settings = Settings(); var panel = Create(settings);
            var before = Options.ToDictionary(x => x, x => (bool)Call(panel, "OptionEnabled", Option(x)));
            bool desired = !before[selected];
            Call(panel, "SelectOption", Option(selected), desired);
            foreach (string option in Options)
            {
                Assert.That((bool)Call(panel, "OptionEnabled", Option(option)), Is.EqualTo(option == selected ? desired : before[option]), option);
                Assert.That(PlayerPrefs.HasKey(Prefix + option), Is.EqualTo(option == selected), option);
            }
            Assert.That(PlayerPrefs.GetInt(Prefix + selected), Is.EqualTo(desired ? 1 : 0));
            AssertCueStateUnchanged(settings);
        }

        [Test]
        public void RestoringAnExplicitSelectionLeavesUnselectedAuthoredDefaultsAlone()
        {
            var settings = Settings(); Set(settings, "showTrafficHeadingTip", true); Set(settings, "trafficSymbolsOutline", true);
            PlayerPrefs.SetInt(Prefix + "HeadingTip", 0);
            Panel.GetMethod("RestoreSavedStyle").Invoke(null, new object[] { settings });
            Assert.That(Get<bool>(settings, "showTrafficHeadingTip"), Is.False);
            Assert.That(Get<bool>(settings, "trafficSymbolsOutline"), Is.True);
            foreach (string option in Options.Where(x => x != "HeadingTip")) Assert.That(PlayerPrefs.HasKey(Prefix + option), Is.False, option);
            AssertCueStateUnchanged(settings);
        }

        [Test]
        public void SegmentButtonsSelectTheNamedStyleAndMoveTheHighlightedMarker()
        {
            var panel = Create(Settings());
            Transform row = panel.transform.Find("STYLE style choice");
            var filled = row.Find("FILLED").GetComponent<Button>();
            var outline = row.Find("OUTLINE").GetComponent<Button>();
            outline.onClick.Invoke();
            Assert.That((bool)Call(panel, "OptionEnabled", Option("Outline")), Is.True);
            Assert.That(outline.transform.Find("Active marker").GetComponent<Image>().enabled, Is.True);
            Assert.That(filled.transform.Find("Active marker").GetComponent<Image>().enabled, Is.False);
            filled.onClick.Invoke();
            Assert.That((bool)Call(panel, "OptionEnabled", Option("Outline")), Is.False);
            Assert.That(filled.transform.Find("Active marker").GetComponent<Image>().enabled, Is.True);
            Assert.That(outline.transform.Find("Active marker").GetComponent<Image>().enabled, Is.False);
            Assert.That(PlayerPrefs.GetInt(Prefix + "Outline"), Is.Zero);
        }

        [Test]
        public void RangeKeyTracksAuthoredThresholdsWithoutWritingStylePreferences()
        {
            var settings = Settings(); Set(settings, "closeDistanceNM", 3f); Set(settings, "farDistanceNM", 22f);
            var panel = Create(settings);
            string Caption(string name) => panel.transform.Find(name).GetComponent<TMP_Text>().text;
            Assert.That(Caption("Far range"), Does.Contain("> 22 NM"));
            Assert.That(Caption("Close range"), Does.Contain("≤ 3 NM"));
            Set(settings, "closeDistanceNM", 8f); Set(settings, "farDistanceNM", 60f); Call(panel, "RefreshState");
            Assert.That(Caption("Far range"), Does.Contain("> 60 NM"));
            Assert.That(Caption("Close range"), Does.Contain("≤ 8 NM"));
            foreach (string option in Options) Assert.That(PlayerPrefs.HasKey(Prefix + option), Is.False);
            AssertCueStateUnchanged(settings);
        }

        [Test]
        public void AdaptiveKeyUsesTheRealRendererAndUnavailablePoseStaysAtCategoryDetail()
        {
            var settings = Settings(); Set(settings, "useAdaptiveTrafficSymbols", false);
            var panel = Create(settings);
            var graphicType = Runtime("Display.TrafficAircraftSymbolGraphic");
            var examples = panel.GetComponentsInChildren(graphicType, true);
            Assert.That(examples.Length, Is.EqualTo(8));
            foreach (var example in examples)
            {
                Assert.That(example.name, Does.StartWith("KEY "));
                Assert.That(((RectTransform)example.transform).sizeDelta, Is.EqualTo(new Vector2(42, 42)));
                Assert.That(((Graphic)example).raycastTarget, Is.False);
            }
            string Detail(string name) => graphicType.GetProperty("CurrentDetail").GetValue(examples.Single(x => x.name == name)).ToString();
            Assert.That(Detail("KEY far double diamond"), Is.EqualTo("FarDoubleDiamond"));
            foreach (var example in examples.Where(x => x.name.StartsWith("KEY category ", StringComparison.Ordinal)))
                Assert.That(graphicType.GetProperty("CurrentDetail").GetValue(example).ToString(), Is.EqualTo("MediumType"));
            Assert.That(Detail("KEY reported level pose"), Is.EqualTo("CloseAttitude"));
            Assert.That(Detail("KEY reported pitch and bank"), Is.EqualTo("CloseAttitude"));
            Assert.That(Detail("KEY pose unavailable"), Is.EqualTo("MediumType"));
            Assert.That(Get<bool>(settings, "useAdaptiveTrafficSymbols"), Is.False, "Illustrating adaptive tiers must not enable the live option.");
        }

        [Test]
        public void KeyFitsTheLeftSlotKeepsLegibleTextAndExplainsThreatArrowPoseAndWeather()
        {
            var panel = Create(Settings()); var root = (RectTransform)panel.transform;
            Assert.That(root.sizeDelta.x, Is.LessThanOrEqualTo(440)); Assert.That(root.sizeDelta.y, Is.LessThanOrEqualTo(560));
            string words = string.Join("\n", panel.GetComponentsInChildren<TMP_Text>(true).Select(x => x.text));
            Assert.That(words, Does.Contain("Tint = threat, never closeness"));
            Assert.That(words, Does.Contain("off-screen direction, not heading"));
            Assert.That(words, Does.Contain("No pose: type only"));
            Assert.That(words, Does.Contain("SIMULATED = illustrative, not measured weather"));
            Assert.That(words, Does.Contain("not sensed aircraft lighting"));
            Assert.That(panel.GetComponentsInChildren(Runtime("Display.PilotCueSymbol"), true).Length, Is.EqualTo(3), "Weather return key is retained.");
            foreach (var label in panel.GetComponentsInChildren<TMP_Text>(true))
            {
                Assert.That(label.fontSize, Is.GreaterThanOrEqualTo(16), label.name);
                var corners = new Vector3[4]; label.rectTransform.GetWorldCorners(corners);
                foreach (Vector3 corner in corners)
                {
                    Vector3 point = root.InverseTransformPoint(corner);
                    Assert.That(point.x, Is.InRange(root.rect.xMin - .01f, root.rect.xMax + .01f), label.text);
                    Assert.That(point.y, Is.InRange(root.rect.yMin - .01f, root.rect.yMax + .01f), label.text);
                }
            }
            var buttons = panel.GetComponentsInChildren<Button>(true);
            Assert.That(buttons.Length, Is.EqualTo(12));
            foreach (var button in buttons) Assert.That(((RectTransform)button.transform).sizeDelta.y, Is.GreaterThanOrEqualTo(30));
        }

        [Test]
        public void ActionCaptionsAndTheTwoLineFooterFitWithoutTruncationAtReadableSize()
        {
            var panel = Create(Settings());
            foreach (var button in panel.GetComponentsInChildren<Button>(true))
            {
                TMP_Text caption = button.GetComponentInChildren<TMP_Text>();
                Assert.That(caption.GetPreferredValues(caption.text).x, Is.LessThanOrEqualTo(caption.rectTransform.rect.width + .5f), caption.text);
            }
            var labels = panel.GetComponentsInChildren<TMP_Text>(true);
            foreach (string title in new[] { "NOSE TIP", "NAV MARKS", "TAIL", "STRIPE", "ADAPTIVE" })
            {
                TMP_Text caption = labels.Single(x => x.text == title);
                Assert.That(caption.GetPreferredValues(caption.text).x, Is.LessThanOrEqualTo(caption.rectTransform.rect.width + .5f), title);
            }
            TMP_Text footer = labels.Single(x => x.name == "Navigation mark caveat");
            Assert.That(footer.text, Does.Contain("\nnot sensed aircraft lighting."));
            Assert.That(footer.GetPreferredValues(footer.text, footer.rectTransform.rect.width, 1000).y,
                Is.LessThanOrEqualTo(footer.rectTransform.rect.height + .5f), "Both footer lines remain visible.");
        }
    }
}
