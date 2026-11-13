using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public class FaaRadarSettingsTests
    {
        private static Type Overlay => Type.GetType("FAA.Customization.FaaRadarControlsOverlay, Assembly-CSharp");
        private GameObject _host;
        private Component _overlay;
        private RectTransform _strip;

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("Readable Settings Test", typeof(RectTransform));
            _host.SetActive(false);
            _overlay = _host.AddComponent(Overlay);
            var stripObject = new GameObject("Test Settings Strip", typeof(RectTransform));
            stripObject.transform.SetParent(_host.transform, false);
            _strip = stripObject.GetComponent<RectTransform>();
            _strip.sizeDelta = new Vector2(476, 276);
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_host);

        [TestCase("WX", "Weather")]
        [TestCase("WX_T", "Weather + turbulence")]
        [TestCase("TURB", "Turbulence")]
        [TestCase("MAP", "Ground map")]
        [TestCase("STBY", "Standby")]
        public void WeatherModes_UseCompleteNames(string mode, string expected)
        {
            var method = Overlay.GetMethod("ReadableWeatherMode");
            var value = Enum.Parse(method.GetParameters()[0].ParameterType, mode);
            Assert.That(method.Invoke(null, new[] { value }), Is.EqualTo(expected));
        }

        [TestCase("Sectional", "Sectional chart")]
        [TestCase("Terminal Area", "Terminal area chart")]
        [TestCase("World Aeronautical", "World aeronautical chart")]
        [TestCase("Street", "Street map")]
        [TestCase("Custom", "Custom map")]
        [TestCase(null, "No map source")]
        public void MapSources_AreNotCrypticCodes(string source, string expected) =>
            Assert.That(Overlay.GetMethod("ReadableMapSource").Invoke(null, new object[] { source }), Is.EqualTo(expected));

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public void SettingsPages_HaveFourNamedCardsAndBoundedReadableControls(bool traffic, int page)
        {
            Build(traffic, page);
            var root = _strip.Find("Readable Settings");
            var cards = root.Cast<Transform>().Where(t => t.gameObject.activeSelf && t.Find("Setting Name") != null).ToArray();
            Assert.That(cards.Length, Is.EqualTo(4));
            foreach (var card in cards)
            {
                Assert.That(card.Find("Setting Name").GetComponent<TMP_Text>().text.Length, Is.GreaterThan(8));
                foreach (var rect in card.GetComponentsInChildren<RectTransform>(true))
                {
                    if (rect == card || !rect.gameObject.activeSelf) continue;
                    var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(card, rect);
                    var cardRect = ((RectTransform)card).rect;
                    Assert.That(bounds.min.x, Is.GreaterThanOrEqualTo(cardRect.xMin - .1f), rect.name);
                    Assert.That(bounds.max.x, Is.LessThanOrEqualTo(cardRect.xMax + .1f), rect.name);
                }
            }
            foreach (var button in _strip.GetComponentsInChildren<Button>(true).Where(b => b.gameObject.activeSelf))
            {
                Assert.That(button.GetComponent<RectTransform>().rect.height, Is.GreaterThanOrEqualTo(32));
                var text = button.GetComponentInChildren<TMP_Text>(true).text;
                Assert.That(new[] { "T-", "G+", "M-", "R+", "BKG", "REF", "CHT", "MORE", "LESS", "REST" }, Does.Not.Contain(text));
                Assert.That(button.GetComponent(Type.GetType("FAA.Customization.FaaRadarControlHint, Assembly-CSharp")), Is.Not.Null);
            }
        }

        [Test]
        public void SettingFocus_ExplainsTheControlWithoutOpeningAnotherPopup()
        {
            Build(false, 0);
            var button = _strip.GetComponentsInChildren<Button>(true).First(b => b.name == "WXTiltUp");
            var hintType = Type.GetType("FAA.Customization.FaaRadarControlHint, Assembly-CSharp");
            var hint = button.GetComponent(hintType);
            hintType.GetMethod("OnPointerDown").Invoke(hint, new object[] { null });
            var help = _strip.Find("Readable Settings/Help").GetComponent<TMP_Text>();
            Assert.That(help.text, Does.Contain("0.5°"));
            Assert.That(help.text, Does.Contain("above the horizon"));
            hintType.GetMethod("OnPointerExit").Invoke(hint, new object[] { null });
            Assert.That(help.text, Does.Contain("Changes apply immediately"));
        }

        [Test]
        public void FullMapToolbar_FitsCompleteSourceAndActionNames()
        {
            _strip.sizeDelta = new Vector2(796, 110);
            Overlay.GetMethod("EnsureReadableFocus", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_overlay, new object[] { _strip });
            foreach (var button in _strip.GetComponentsInChildren<Button>(true))
            {
                var label = button.GetComponentInChildren<TMP_Text>();
                label.font = TMP_Settings.defaultFontAsset;
                label.fontSharedMaterial = label.font.material;
                if (button.name == "TCASFocusSource") label.text = "World aeronautical chart";
                float needed = label.GetPreferredValues(label.text).x;
                Assert.That(needed, Is.LessThanOrEqualTo(label.rectTransform.rect.width), label.text);
                var bounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_strip, button.transform);
                Assert.That(bounds.max.x, Is.LessThanOrEqualTo(_strip.rect.xMax + .1f), button.name);
            }
        }

        [Test]
        public void Labels_TolerateRetiredUnityReferencesDuringEditorReload()
        {
            var retired = new GameObject("Retired weather root", typeof(RectTransform));
            Set("_weatherRoot", retired.transform);
            UnityEngine.Object.DestroyImmediate(retired);
            Assert.DoesNotThrow(() => Overlay.GetMethod("RefreshReadableValues", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_overlay, null));
        }

        [TestCase("TrafficRangeDown")]
        [TestCase("TrafficRangeUp")]
        public void ManualRangeSelection_DisablesAutoRange(string action)
        {
            var type = Type.GetType("TrafficRadar.Core.TrafficRadarController, TrafficRadar");
            var controller = _host.AddComponent(type);
            Set("_trafficController", controller);
            type.GetProperty("AutoRangeEnabled").SetValue(controller, true);
            Overlay.GetMethod(action).Invoke(_overlay, null);
            Assert.That(type.GetProperty("AutoRangeEnabled").GetValue(controller), Is.False);
        }

        [Test]
        public void TiltAndGain_UseTheDisplayedUnitsAndKeepThePanelOpen()
        {
            var type = Type.GetType("WeatherRadar.WeatherRadarDataProvider, WeatherRadar");
            var provider = _host.AddComponent(type);
            Set("_weatherDataProvider", provider);
            Set("_weatherConfigurationVisible", true);
            Build(false, 0);
            _strip.GetComponentsInChildren<Button>(true).First(b => b.name == "WXTiltUp").onClick.Invoke();
            _strip.GetComponentsInChildren<Button>(true).First(b => b.name == "WXGainDown").onClick.Invoke();
            var texts = _strip.GetComponentsInChildren<TMP_Text>(true);
            Assert.That(texts.First(t => t.name == "WXTiltValue").text, Is.EqualTo("+0.5°"));
            Assert.That(texts.First(t => t.name == "WXGainValue").text, Is.EqualTo("-1 dB"));
            Assert.That(Overlay.GetField("_weatherConfigurationVisible", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(_overlay), Is.True);
        }

        [TestCase(220f)]
        [TestCase(360f)]
        [TestCase(560f)]
        public void TrafficSettings_DockBelowHudAndClearOfTheRadar(float radarWidth)
        {
            // One tap opens only this drawer (no co-opened quick menu), so it docks directly beside the scope.
            var position = (Vector2)Overlay.GetMethod("CalculateTrafficSettingsDock").Invoke(null, new object[] { new Vector2(-28, 28), radarWidth });
            float height = (float)Overlay.GetField("SettingsPanelHeight").GetRawConstantValue();
            float width = (float)Overlay.GetField("SettingsPanelWidth").GetRawConstantValue();
            Assert.That(position.x, Is.LessThanOrEqualTo(-28 - radarWidth - 12), "The drawer (right-edge pivot) must clear the radar.");
            Assert.That(position.x, Is.GreaterThanOrEqualTo(-28 - radarWidth - 40), "Controls stay next to the display they affect.");
            Assert.That(position.y + height, Is.LessThan(320), "Keep below the heading tape and primary instruments.");
            Assert.That(position.x + 1920 - width, Is.GreaterThan(300), "Leave the weather scope clear at the 16:9 reference layout.");
        }

        [TestCase(false, 0)]
        [TestCase(false, 1)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        [TestCase(true, 2)]
        public void SettingsText_IsAtLeastTheFaaMinimumAndHasAnExplicitClose(bool traffic, int page)
        {
            Build(traffic, page);
            foreach (var text in _strip.GetComponentsInChildren<TMP_Text>(true).Where(t => Shown(t.transform)))
                Assert.That(text.fontSize, Is.GreaterThanOrEqualTo(15f), text.name + " is below the 15-unit (16 arcmin) floor");
            var close = _strip.GetComponentsInChildren<Button>(true).FirstOrDefault(b => Shown(b.transform) && b.name.EndsWith("SettingsClose"));
            Assert.That(close, Is.Not.Null, "Closing must not depend on an instruction to tap the radar again.");
            Assert.That(_strip.Find("Readable Settings/Close Hint") == null || !_strip.Find("Readable Settings/Close Hint").gameObject.activeSelf);
        }

        [Test]
        public void WeatherDisplayPage_ShowsTheSharedColorKeyOffTheScope()
        {
            Build(false, 1);
            var key = _strip.Find("Readable Settings/WXColorKey");
            Assert.That(key, Is.Not.Null);
            var palette = Type.GetType("WeatherRadar.WeatherRadarPalette, WeatherRadar", true);
            for (int level = 1; level <= 4; level++)
            {
                var swatch = key.Find("Swatch " + level).GetComponent<Image>();
                Color32 expected = (Color32)palette.GetMethod("ForLevel").Invoke(null, new object[] { level });
                Assert.That((Color32)swatch.color, Is.EqualTo(expected));
            }
        }

        [Test]
        public void TrafficRadarPage_OffersTheTcasAltitudeBandAndCyclesIt()
        {
            var type = Type.GetType("TrafficRadar.Core.TrafficRadarController, TrafficRadar");
            var controller = _host.AddComponent(type);
            Set("_trafficController", controller);
            Build(true, 0);
            var band = _strip.GetComponentsInChildren<Button>(true).First(b => b.name == "TCASBandCycle");
            Assert.That(band.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("Normal ±2700"));
            band.onClick.Invoke();
            Assert.That(type.GetProperty("AltitudeBand").GetValue(controller).ToString(), Is.EqualTo("Above"));
            Assert.That(band.GetComponentInChildren<TMP_Text>().text, Is.EqualTo("Above +9900"));
            Assert.That(_strip.Find("Readable Settings/Help").GetComponent<TMP_Text>().text, Does.Contain("hundreds of feet"),
                "The tag legend lives in the settings help, not as a box on the scope.");
        }

        private bool Shown(Transform t)
        {
            for (; t != null && t != _strip; t = t.parent) if (!t.gameObject.activeSelf) return false;
            return true;
        }

        private void Build(bool traffic, int page)
        {
            Set(traffic ? "_trafficSettingsPage" : "_weatherSettingsPage", page);
            Overlay.GetMethod(traffic ? "EnsureReadableTrafficControls" : "EnsureReadableWeatherControls", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(_overlay, new object[] { _strip });
            Overlay.GetMethod("RefreshReadableValues", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(_overlay, null);
        }

        private void Set(string field, object value) => Overlay.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_overlay, value);
    }
}
