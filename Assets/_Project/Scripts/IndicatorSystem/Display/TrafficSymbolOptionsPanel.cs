using System;
using FAA.Customization;
using IndicatorSystem.Core;
using TMPro;
using TrafficRadar;
using UnityEngine;
using UnityEngine.UI;

namespace IndicatorSystem.Display
{
    public enum TrafficSymbolStyleOption { Outline, HeadingTip, NavigationMarks, TailLight, BodyStripe, Adaptive }

    /// <summary>Illustrated key and explicit display-style choices. Examples never enter the live target pool.</summary>
    public sealed class TrafficSymbolOptionsPanel : MonoBehaviour
    {
        public const float Width = 440f, Height = 560f;
        public const string PreferencePrefix = "FAA.Cues.TrafficStyle.";
        private const float FontSize = 16f;
        private static readonly Color White = new Color(.94f, .98f, 1f);
        private static readonly Color Quiet = new Color(.70f, .80f, .86f);
        private static readonly Color Cyan = new Color(.35f, .90f, 1f);
        private static readonly Color Plate = new Color(.02f, .05f, .07f, .98f);
        private static readonly Color ButtonColor = new Color(.07f, .12f, .16f, .96f);
        private static readonly Color SelectedColor = new Color(.10f, .33f, .37f, 1f);
        private static readonly TrafficSymbolStyleOption[] Options = (TrafficSymbolStyleOption[])Enum.GetValues(typeof(TrafficSymbolStyleOption));

        private sealed class Choice { public Image[] plates, marks; public int selected = -1; }
        private readonly Choice[] choices = new Choice[6];
        private readonly TrafficAircraftSymbolGraphic[] examples = new TrafficAircraftSymbolGraphic[8];
        private readonly IndicatorData[] exampleData = new IndicatorData[8];
        private IndicatorSettings settings, exampleSettings;
        private Action changed;
        private TMP_Text farCaption, closeCaption;
        private int lastStyle = -1;
        private float lastClose = float.NaN, lastFar = float.NaN;

        public static TrafficSymbolOptionsPanel Create(IndicatorSettings settings, Transform parent, Action changed = null)
        {
            var host = new GameObject("Screen cue symbol key", typeof(RectTransform), typeof(Image));
            host.transform.SetParent(parent, false);
            if (parent != null) host.layer = parent.gameObject.layer;
            var rect = (RectTransform)host.transform; rect.sizeDelta = new Vector2(Width, Height);
            host.GetComponent<Image>().color = Plate;
            var panel = host.AddComponent<TrafficSymbolOptionsPanel>();
            panel.settings = settings; panel.changed = changed;
            panel.Build(); panel.RefreshState();
            return panel;
        }

        public static string PreferenceKey(TrafficSymbolStyleOption option) => PreferencePrefix + option;

        /// <summary>Only preferences that were explicitly selected override the authored style defaults.</summary>
        public static void RestoreSavedStyle(IndicatorSettings settings)
        {
            if (settings == null) return;
            foreach (var option in Options)
            {
                string key = PreferenceKey(option);
                if (PlayerPrefs.HasKey(key)) WriteOption(settings, option, PlayerPrefs.GetInt(key) != 0);
            }
        }

        public bool OptionEnabled(TrafficSymbolStyleOption option) => settings != null && ReadOption(settings, option);

        /// <summary>Explicit user selection; opening, drawing and restoring the key never write preferences.</summary>
        public void SelectOption(TrafficSymbolStyleOption option, bool enabled)
        {
            if (settings == null || (int)option < 0 || (int)option >= choices.Length) return;
            WriteOption(settings, option, enabled);
            PlayerPrefs.SetInt(PreferenceKey(option), enabled ? 1 : 0);
            PlayerPrefs.Save();
            changed?.Invoke();
            RefreshState();
        }

        private static bool ReadOption(IndicatorSettings s, TrafficSymbolStyleOption option)
        {
            switch (option)
            {
                case TrafficSymbolStyleOption.Outline: return s.trafficSymbolsOutline;
                case TrafficSymbolStyleOption.HeadingTip: return s.showTrafficHeadingTip;
                case TrafficSymbolStyleOption.NavigationMarks: return s.showNavigationLights;
                case TrafficSymbolStyleOption.TailLight: return s.showTrafficTailLight;
                case TrafficSymbolStyleOption.BodyStripe: return s.showTrafficBodyStripe;
                default: return s.useAdaptiveTrafficSymbols;
            }
        }

        private static void WriteOption(IndicatorSettings s, TrafficSymbolStyleOption option, bool enabled)
        {
            switch (option)
            {
                case TrafficSymbolStyleOption.Outline: s.trafficSymbolsOutline = enabled; break;
                case TrafficSymbolStyleOption.HeadingTip: s.showTrafficHeadingTip = enabled; break;
                case TrafficSymbolStyleOption.NavigationMarks: s.showNavigationLights = enabled; break;
                case TrafficSymbolStyleOption.TailLight: s.showTrafficTailLight = enabled; break;
                case TrafficSymbolStyleOption.BodyStripe: s.showTrafficBodyStripe = enabled; break;
                case TrafficSymbolStyleOption.Adaptive: s.useAdaptiveTrafficSymbols = enabled; break;
            }
        }

        private void Build()
        {
            Label("Key title", "KEY / STYLE  ·  EXAMPLES", 12, 8, 416, 24, White, true);
            Label("Color and arrow meaning", "Tint = threat, never closeness.\nArrow = off-screen direction, not heading.", 12, 34, 416, 38, Quiet);
            farCaption = Label("Far range", "FAR", 66, 80, 362, 22, White, true);
            Label("Far meaning", "DOUBLE DIAMOND · DISTANT TRAFFIC", 66, 102, 362, 22, Quiet);
            AddExample(0, "KEY far double diamond", 34, 102, TrafficRadarDataManager.AircraftType.Commercial, false, Quaternion.identity);

            Label("Medium range", "MID RANGE · REPORTED CATEGORY", 12, 128, 416, 20, White, true);
            var types = new[] { TrafficRadarDataManager.AircraftType.Commercial, TrafficRadarDataManager.AircraftType.General,
                TrafficRadarDataManager.AircraftType.Helicopter, TrafficRadarDataManager.AircraftType.Military };
            string[] names = { "AIRLINER", "LIGHT", "HELICOPTER", "MILITARY" };
            for (int i = 0; i < types.Length; i++)
            {
                AddExample(i + 1, "KEY category " + names[i], 55 + i * 110, 174, types[i], false, Quaternion.identity);
                Label("Category meaning", names[i], i * 110, 202, 110, 22, White, false, true);
            }

            closeCaption = Label("Close range", "NEAR", 12, 224, 416, 20, White, true);
            AddExample(5, "KEY reported level pose", 74, 270, TrafficRadarDataManager.AircraftType.Commercial, true, Quaternion.identity);
            AddExample(6, "KEY reported pitch and bank", 220, 270, TrafficRadarDataManager.AircraftType.Commercial, true, Quaternion.Euler(-25, 35, 10));
            AddExample(7, "KEY pose unavailable", 366, 270, TrafficRadarDataManager.AircraftType.Commercial, false, Quaternion.identity);
            Label("Level pose meaning", "LEVEL", 4, 298, 140, 22, White, false, true);
            Label("Bank pose meaning", "PITCH / BANK", 150, 298, 140, 22, White, false, true);
            Label("Missing pose meaning", "NO POSE", 296, 298, 140, 22, White, false, true);
            Label("Pose caveat", "No pose: type only. Unknown type stays UNKNOWN.", 12, 322, 416, 22, Quiet);

            FaaRadarIcon[] rain = { FaaRadarIcon.WeatherRainLight, FaaRadarIcon.WeatherRainModerate, FaaRadarIcon.WeatherRainHeavy };
            string[] weather = { "LIGHT RAIN", "MOD RAIN", "HEAVY RAIN" };
            for (int i = 0; i < rain.Length; i++)
            {
                var symbol = new GameObject("Weather key " + weather[i], typeof(RectTransform), typeof(PilotCueSymbol)).GetComponent<PilotCueSymbol>();
                symbol.transform.SetParent(transform, false);
                symbol.gameObject.layer = gameObject.layer;
                symbol.rectTransform.anchorMin = symbol.rectTransform.anchorMax = new Vector2(0, 1);
                symbol.rectTransform.anchoredPosition = new Vector2(22 + 146 * i, -360);
                symbol.rectTransform.sizeDelta = new Vector2(24, 24); symbol.raycastTarget = false;
                symbol.Configure(rain[i], false, 0, Cyan);
                Label("Weather meaning", weather[i], 40 + 146 * i, 348, 106, 24, White);
            }
            Label("Weather source caveat", "SIMULATED = illustrative, not measured weather.", 12, 376, 416, 22, Quiet);

            MakeChoice(TrafficSymbolStyleOption.Outline, "STYLE", 12, 408, true);
            MakeChoice(TrafficSymbolStyleOption.Adaptive, "ADAPTIVE", 226, 408);
            MakeChoice(TrafficSymbolStyleOption.HeadingTip, "NOSE TIP", 12, 444);
            MakeChoice(TrafficSymbolStyleOption.NavigationMarks, "NAV MARKS", 226, 444);
            MakeChoice(TrafficSymbolStyleOption.TailLight, "TAIL", 12, 480);
            MakeChoice(TrafficSymbolStyleOption.BodyStripe, "STRIPE", 226, 480);
            Label("Navigation mark caveat", "Nav / tail / stripe = display marks.\nnot sensed aircraft lighting.", 12, 514, 416, 42, Quiet);
        }

        private void AddExample(int index, string name, float x, float y, TrafficRadarDataManager.AircraftType type,
            bool pose, Quaternion rotation)
        {
            var graphic = new GameObject(name, typeof(RectTransform), typeof(TrafficAircraftSymbolGraphic)).GetComponent<TrafficAircraftSymbolGraphic>();
            graphic.transform.SetParent(transform, false);
            graphic.gameObject.layer = gameObject.layer;
            graphic.rectTransform.anchorMin = graphic.rectTransform.anchorMax = new Vector2(0, 1);
            graphic.rectTransform.anchoredPosition = new Vector2(x, -y);
            graphic.rectTransform.sizeDelta = new Vector2(42, 42); graphic.raycastTarget = false;
            examples[index] = graphic;
            exampleData[index] = new IndicatorData
            {
                Id = name, Label = "KEY EXAMPLE", Type = IndicatorType.Traffic, IsActive = true,
                Color = Cyan, AircraftType = type, Visibility = IndicatorVisibility.OnScreen,
                HasHeading = pose || index > 0 && index < 5, HasPitch = pose, HasRoll = pose, HasVerticalSpeed = pose,
                PitchDegrees = index == 6 ? 25f : 0f, RollDegrees = index == 6 ? -10f : 0f,
                RelativeHeadingDegrees = index == 6 ? 35f : 0f, ViewRelativeRotation = rotation,
                ProjectionRect = new Rect(0, 0, 1920, 1080)
            };
        }

        private void MakeChoice(TrafficSymbolStyleOption option, string title, float x, float y, bool fill = false)
        {
            var row = new GameObject(title + " style choice", typeof(RectTransform)); row.transform.SetParent(transform, false);
            row.layer = gameObject.layer;
            var rect = (RectTransform)row.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(202, 30);
            if (!fill) Label("Action", title, 0, 0, 104, 30, White, true, false, row.transform);
            string[] labels = fill ? new[] { "FILLED", "OUTLINE" } : new[] { "ON", "OFF" };
            float width = fill ? 99 : 44, left = 202 - width * 2 - 4;
            var choice = new Choice { plates = new Image[2], marks = new Image[2] }; choices[(int)option] = choice;
            for (int i = 0; i < 2; i++)
            {
                bool enabled = fill ? i == 1 : i == 0;
                var host = new GameObject(labels[i], typeof(RectTransform), typeof(Image), typeof(Button)); host.transform.SetParent(row.transform, false);
                host.layer = gameObject.layer;
                var buttonRect = (RectTransform)host.transform;
                buttonRect.anchorMin = buttonRect.anchorMax = buttonRect.pivot = new Vector2(0, 1);
                buttonRect.anchoredPosition = new Vector2(left + i * (width + 4), 0); buttonRect.sizeDelta = new Vector2(width, 30);
                var plate = host.GetComponent<Image>(); plate.color = ButtonColor;
                var outline = host.AddComponent<Outline>(); outline.effectColor = new Color(.22f, .40f, .47f, .72f);
                var button = host.GetComponent<Button>(); button.targetGraphic = plate;
                button.navigation = new Navigation { mode = Navigation.Mode.None };
                button.onClick.AddListener(() => SelectOption(option, enabled));
                Label("Label", labels[i], 0, 0, width, 30, White, true, true, host.transform);
                var mark = new GameObject("Active marker", typeof(RectTransform), typeof(Image)).GetComponent<Image>(); mark.transform.SetParent(host.transform, false);
                mark.gameObject.layer = gameObject.layer;
                mark.rectTransform.anchorMin = new Vector2(0, 0); mark.rectTransform.anchorMax = new Vector2(1, 0);
                mark.rectTransform.offsetMin = new Vector2(4, 2); mark.rectTransform.offsetMax = new Vector2(-4, 5);
                mark.color = Cyan; mark.raycastTarget = false;
                choice.plates[i] = plate; choice.marks[i] = mark;
            }
        }

        public void RefreshState()
        {
            if (settings == null) return;
            int style = 0;
            foreach (var option in Options)
            {
                bool enabled = ReadOption(settings, option);
                if (enabled) style |= 1 << (int)option;
                Choice choice = choices[(int)option];
                int selected = option == TrafficSymbolStyleOption.Outline ? (enabled ? 1 : 0) : (enabled ? 0 : 1);
                if (choice == null || choice.selected == selected) continue;
                choice.selected = selected;
                for (int i = 0; i < 2; i++) { choice.plates[i].color = i == selected ? SelectedColor : ButtonColor; choice.marks[i].enabled = i == selected; }
            }
            if (style == lastStyle && lastClose == settings.closeDistanceNM && lastFar == settings.farDistanceNM) return;
            lastStyle = style; lastClose = settings.closeDistanceNM; lastFar = settings.farDistanceNM;
            if (exampleSettings == null)
            {
                exampleSettings = Instantiate(settings); exampleSettings.name = "Traffic key example style";
                exampleSettings.hideFlags = HideFlags.HideAndDontSave;
            }
            foreach (var option in Options) WriteOption(exampleSettings, option, ReadOption(settings, option));
            // The key illustrates all three adaptive tiers even when the live adaptive option is off.
            // Normalize example size so the key never inherits a large live marker scale.
            exampleSettings.useAdaptiveTrafficSymbols = true; exampleSettings.enableDistanceScaling = false;
            exampleSettings.enabled = true; exampleSettings.showTrafficIndicators = true;
            exampleSettings.closeDistanceNM = settings.closeDistanceNM; exampleSettings.farDistanceNM = settings.farDistanceNM;
            exampleSettings.globalOpacity = 1f;
            farCaption.text = "FAR > " + settings.farDistanceNM.ToString("0.#") + " NM · ADAPTIVE KEY";
            closeCaption.text = "NEAR ≤ " + settings.closeDistanceNM.ToString("0.#") + " NM · REPORTED POSE";
            for (int i = 0; i < examples.Length; i++)
            {
                var data = exampleData[i];
                data.DistanceNM = i == 0 ? settings.farDistanceNM + 10f : i < 5 ? Mathf.Lerp(settings.closeDistanceNM, settings.farDistanceNM, .5f) : Mathf.Max(0, settings.closeDistanceNM - .5f);
                exampleData[i] = data; examples[i].Configure(data, exampleSettings);
            }
        }

        private TMP_Text Label(string name, string text, float x, float y, float width, float height, Color color,
            bool bold = false, bool centered = false, Transform parent = null)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)); host.transform.SetParent(parent != null ? parent : transform, false);
            host.layer = gameObject.layer;
            var label = host.GetComponent<TextMeshProUGUI>(); label.font = TMP_Settings.defaultFontAsset;
            label.text = text; label.fontSize = FontSize; label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            label.color = color; label.richText = false; label.raycastTarget = false;
            label.alignment = centered ? TextAlignmentOptions.Center : TextAlignmentOptions.MidlineLeft;
            label.textWrappingMode = TextWrappingModes.Normal; label.overflowMode = TextOverflowModes.Ellipsis;
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = new Vector2(0, 1);
            label.rectTransform.anchoredPosition = new Vector2(x, -y); label.rectTransform.sizeDelta = new Vector2(width, height);
            return label;
        }

        private void OnDestroy()
        {
            if (exampleSettings == null) return;
            if (Application.isPlaying) Destroy(exampleSettings); else DestroyImmediate(exampleSettings);
        }
    }
}
