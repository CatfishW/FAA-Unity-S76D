using IndicatorSystem.Controller;
using IndicatorSystem.Core;
using IndicatorSystem.Integration;
using FAA.Customization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace IndicatorSystem.Display
{
    /// <summary>
    /// Marker controls, separate from radar display power. Status stays visible when cues are hidden.
    /// With the pilot chrome present (Play Mode) the panel is a flyout of the bar's CUES button: it opens above the bar in
    /// the left flyout slot, its symbol key stacks above it (never over the VSI or the flight columns), and it always starts
    /// collapsed. Without the chrome (edit-time hosts) it keeps its own header button.
    /// State is never mixed into an action label: the bar shows "CUES" with a separate state badge (ON / OFF / TFC ONLY /
    /// WX ONLY), each marker type is a "TRAFFIC CUES [ON|OFF]" row with the live state highlighted, and the marker range is a
    /// segmented [10|20|40|80] NM selector (same pattern as Settings). Dismissal: the CLOSE [Esc] button shared by every flyout.
    /// </summary>
    public sealed class IndicatorControlsPanel : MonoBehaviour
    {
        public const string ChromeId = "cues";
        public const float Width = 360f, DockedWidth = 440f, ContentHeight = 214f, LegendHeight = 350f, LegendGap = 8f;
        public static readonly Vector2 CollapsedSize = new Vector2(190, 42), ExpandedSize = new Vector2(Width, ContentHeight);
        /// <summary>Size of the docked CUES flyout (fits the left flyout slot, FaaPilotChrome.FlyoutMaxWidth).</summary>
        public static readonly Vector2 DockedSize = new Vector2(DockedWidth, ContentHeight);
        public static readonly float[] RangePresetsNm = { 10f, 20f, 40f, 80f };
        private static readonly string[] RangeLabels = { "10", "20", "40", "80" };
        private const string TrafficKey = "FAA.Cues.TrafficVisible";
        private const string WeatherKey = "FAA.Cues.WeatherVisible";
        private const string ExpandedKey = "FAA.Cues.ControlsExpanded";
        private const float TitleSize = FaaHudStyle.Chrome, BodySize = FaaHudStyle.Chrome;
        private const float SegmentWidth = 52f, SegmentHeight = 30f, SegmentGap = 4f;
        private static readonly Color Active = new Color(0.08f, 0.23f, 0.26f, 0.96f);
        private static readonly Color Inactive = FaaHudStyle.ChromeButton;
        private static readonly Color TitleColor = FaaHudStyle.White;
        private static readonly Color QuietColor = FaaHudStyle.ChromeQuiet;

        private sealed class Segments { public Image[] plates, marks; public int shown = -2; }

        private IndicatorSystemController _controller;
        private TrafficIndicatorBridge _traffic;
        private WeatherIndicatorBridge _weather;
        private TMP_Text _trafficTitle, _trafficStatus, _weatherTitle, _weatherStatus, _summary, _headerLabel;
        private Image _trafficBackground, _weatherBackground, _background;
        private Segments _trafficSegments, _weatherSegments, _rangeSegments;
        private GameObject _content, _header;
        private GameObject _legend;
        private float _width = Width;
        private bool _expanded, _docked;
        private readonly Vector3[] _boundsCorners = new Vector3[4];
        private float _nextRefresh;
        // Last rendered values: text is rebuilt only when one of these changes (no per-refresh allocation).
        private int _lastTrafficCount = -1, _lastWeatherCount = -1, _lastOn = -1, _lastOff = -1, _lastSuppressed = -1;
        private bool _lastTrafficOn, _lastWeatherOn, _lastAnyOn = true, _rowsInitialised;
        private string _lastTrafficSource, _lastWeatherSource;
        // The panel that currently owns the CUES button. A replacement panel (SetTargetCanvas) registers before the old
        // one's deferred OnDestroy runs, so only the owner may remove the button.
        private static IndicatorControlsPanel _chromeOwner;

        public bool IsExpanded => _expanded;
        /// <summary>True when the panel is a flyout of the pilot chrome bar.</summary>
        public bool IsDocked => _docked;
        public GameObject Legend => _legend;
        public bool SummaryVisible => _summary != null && _summary.gameObject.activeSelf;

        public static IndicatorControlsPanel Create(IndicatorSystemController controller, Transform parent)
        {
            var root = new GameObject("Screen cue controls", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(Image));
            root.transform.SetParent(parent, false);
            var rt = root.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-62, -62);
            rt.sizeDelta = ExpandedSize;
            var canvas = root.GetComponent<Canvas>();
            canvas.overrideSorting = true;
            canvas.sortingOrder = 5026;
            var background = root.GetComponent<Image>();
            background.color = FaaHudStyle.ChromePlate;
            background.raycastTarget = false;
            var panel = root.AddComponent<IndicatorControlsPanel>();
            panel._background = background;
            panel._controller = controller;
            panel._traffic = controller.GetComponent<TrafficIndicatorBridge>();
            panel._weather = controller.GetComponent<WeatherIndicatorBridge>();
            var chrome = FaaPilotChrome.Ensure();
            panel._width = chrome != null ? DockedWidth : Width;
            Button header = MakeButton(root.transform, "Show or hide cue controls", new Vector2(6, -4), new Vector2(CollapsedSize.x - 12, CollapsedSize.y - 8));
            panel._header = header.gameObject;
            panel._headerLabel = Label(header.transform, "Title", TitleSize, TitleColor, new Vector2(10, 0), new Vector2(CollapsedSize.x - 28, CollapsedSize.y - 8));
            panel._headerLabel.fontStyle = FontStyles.Bold;
            header.onClick.AddListener(panel.ToggleExpanded);
            panel._content = new GameObject("Expandable cue controls", typeof(RectTransform));
            panel._content.transform.SetParent(root.transform, false);
            var contentRect = panel._content.GetComponent<RectTransform>();
            contentRect.anchorMin = Vector2.zero; contentRect.anchorMax = Vector2.one;
            contentRect.offsetMin = contentRect.offsetMax = Vector2.zero;
            float w = panel._width;
            // KEY sits left of the CLOSE button when docked; in the legacy layout it sits right of the header button.
            float keyX = chrome != null ? w - 8 - 128 - 8 - 60 : 196;
            Button keyButton = MakeButton(panel._content.transform, "Symbol key", new Vector2(keyX, -6), new Vector2(60, 30));
            var keyLabel = Label(keyButton.transform, "Label", TitleSize, TitleColor, Vector2.zero, new Vector2(60, 30));
            keyLabel.alignment = TextAlignmentOptions.Center; keyLabel.text = "KEY"; keyLabel.fontStyle = FontStyles.Bold;
            keyButton.onClick.AddListener(panel.ToggleLegend);
            panel.MakeRow(IndicatorType.Traffic, -44, out panel._trafficTitle, out panel._trafficStatus, out panel._trafficBackground, out panel._trafficSegments);
            panel.MakeRow(IndicatorType.Weather, -98, out panel._weatherTitle, out panel._weatherStatus, out panel._weatherBackground, out panel._weatherSegments);
            var rangeLabel = Label(panel._content.transform, "Marker range", TitleSize, TitleColor, new Vector2(16, -152), new Vector2(110, 30));
            rangeLabel.text = "RANGE NM"; rangeLabel.fontStyle = FontStyles.Bold;
            panel._rangeSegments = panel.MakeSegments(panel._content.transform, RangeLabels, new Vector2(w - 8, -152), panel.SelectRange);
            panel._summary = Label(panel._content.transform, "Visibility and declutter", BodySize, QuietColor, new Vector2(16, -188), new Vector2(w - 28, 22));
            FaaHudKeepOutRegion.Ensure(panel._content, "chrome:cue-controls", FaaKeepOutKind.Chrome, 4f);
            if (PlayerPrefs.HasKey(TrafficKey)) controller.SetTypeVisible(IndicatorType.Traffic, PlayerPrefs.GetInt(TrafficKey) != 0);
            if (PlayerPrefs.HasKey(WeatherKey)) controller.SetTypeVisible(IndicatorType.Weather, PlayerPrefs.GetInt(WeatherKey) != 0);
            panel.Dock(chrome);
            // Non-essential controls always start collapsed; the persisted expanded state is no longer restored.
            panel.ApplyExpandedState(false);
            panel.Refresh();
            return panel;
        }

        /// <summary>Turns the panel into the CUES flyout of the chrome bar (idempotent). Exposed for edit-mode tests.</summary>
        public void Dock(FaaPilotChrome chrome)
        {
            if (chrome == null || _docked) return;
            _docked = true;
            _header.SetActive(false);
            chrome.AttachFlyout((RectTransform)transform);
            GetComponent<Canvas>().sortingOrder = FaaPilotChrome.FlyoutOrder;
            var title = Label(_content.transform, "Title", TitleSize, TitleColor, new Vector2(14, -6), new Vector2(150, 30));
            title.text = "SCREEN CUES"; title.fontStyle = FontStyles.Bold;
            FaaPilotChrome.CreateCloseButton((RectTransform)_content.transform, () => SetExpanded(false), 6, 8);
            chrome.AddButton(ChromeId, FaaChromeCluster.Left, 30, "CUES", null, ToggleExpanded);
            _chromeOwner = this;
            chrome.RegisterFlyout(ChromeId, () => SetExpanded(false));
            chrome.SetHelpEntry("CUES", "Screen cue markers, range, symbol key", 70);
            ApplyExpandedState(_expanded);
        }

        private void OnDestroy()
        {
            if (!_docked || _chromeOwner != this) return;
            _chromeOwner = null;
            var chrome = FaaPilotChrome.Current;
            if (chrome == null) return;
            chrome.RemoveButton(ChromeId); chrome.UnregisterFlyout(ChromeId); chrome.RemoveHelpEntry("CUES");
        }

        public void ToggleExpanded() => SetExpanded(!_expanded);

        public void SetExpanded(bool expanded)
        {
            ApplyExpandedState(expanded);
            PlayerPrefs.SetInt(ExpandedKey, expanded ? 1 : 0);
            PlayerPrefs.Save();
        }

        private void ApplyExpandedState(bool expanded)
        {
            _expanded = expanded;
            _content.SetActive(expanded);
            // Hiding controls never switches traffic or weather markers off.
            // The next expansion starts with controls, not a large open legend.
            if (!expanded && _legend != null) _legend.SetActive(false);
            if (_docked)
            {
                // Collapsed flyout occupies nothing: no plate, zero size, keep-outs released.
                _background.enabled = expanded;
                ((RectTransform)transform).sizeDelta = expanded ? DockedSize : Vector2.zero;
                var chrome = FaaPilotChrome.Current;
                if (chrome != null)
                {
                    if (expanded) chrome.NotifyFlyoutOpened(ChromeId); else chrome.NotifyFlyoutClosed(ChromeId);
                    chrome.SetButtonState(ChromeId, chrome.IsButtonVisible(ChromeId), expanded);
                }
                if (expanded) Refresh(); // rows are current the moment the flyout opens
                return;
            }
            ((RectTransform)transform).sizeDelta = expanded ? ExpandedSize : CollapsedSize;
            _headerLabel.text = expanded ? "SCREEN CUES  –" : "SCREEN CUES  +";
        }

        public void ToggleLegend()
        {
            if (!_expanded) SetExpanded(true);
            if (_legend == null)
            {
                _legend = new GameObject("Screen cue symbol key", typeof(RectTransform), typeof(Image));
                _legend.transform.SetParent(_content.transform, false);
                var rt = _legend.GetComponent<RectTransform>();
                if (_docked)
                {
                    // Stacked above the panel inside the left flyout slot (clear of the IAS/TQ column and the VSI).
                    rt.anchorMin = rt.anchorMax = new Vector2(0, 1); rt.pivot = new Vector2(0, 0);
                    rt.anchoredPosition = new Vector2(0, LegendGap);
                }
                else
                {
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
                    rt.anchoredPosition = new Vector2(0, -(ContentHeight + LegendGap));
                }
                rt.sizeDelta = new Vector2(_width, LegendHeight);
                _legend.GetComponent<Image>().color = FaaHudStyle.ChromePlate;
                var how = Label(_legend.transform, "Arrow explanation", BodySize, TitleColor, new Vector2(12, -8), new Vector2(_width - 24, 22));
                how.text = "ARROW = OFF SCREEN  ·  ICON = TYPE"; how.fontStyle = FontStyles.Bold;
                FaaRadarIcon[] icons = { FaaRadarIcon.AircraftAirliner, FaaRadarIcon.AircraftGeneral, FaaRadarIcon.AircraftHelicopter,
                    FaaRadarIcon.AircraftMilitary, FaaRadarIcon.AircraftUnknown, FaaRadarIcon.WeatherRainLight,
                    FaaRadarIcon.WeatherRainModerate, FaaRadarIcon.WeatherRainHeavy };
                string[] labels = { "AIRLINER", "LIGHT AIRCRAFT", "HELICOPTER", "MILITARY CATEGORY", "AIRCRAFT TYPE UNREPORTED", "LIGHT RAIN RETURN", "MODERATE RAIN RETURN", "HEAVY RAIN RETURN" };
                for (int i = 0; i < icons.Length; i++)
                {
                    var symbol = new GameObject(labels[i], typeof(RectTransform), typeof(PilotCueSymbol)).GetComponent<PilotCueSymbol>();
                    symbol.transform.SetParent(_legend.transform, false);
                    symbol.rectTransform.anchorMin = symbol.rectTransform.anchorMax = new Vector2(0, 1);
                    symbol.rectTransform.anchoredPosition = new Vector2(26, -50 - i * 32);
                    symbol.rectTransform.sizeDelta = new Vector2(30, 30);
                    symbol.raycastTarget = false;
                    symbol.Configure(icons[i], false, 0, new Color(.55f, .91f, .96f));
                    var label = Label(_legend.transform, "Meaning", TitleSize, TitleColor, new Vector2(50, -37 - i * 32), new Vector2(_width - 62, 26));
                    label.text = labels[i];
                }
                var note = Label(_legend.transform, "Source caveat", BodySize, QuietColor, new Vector2(12, -296), new Vector2(_width - 24, 46));
                note.textWrappingMode = TextWrappingModes.Normal;
                note.text = "SIMULATED = illustrative, not measured.\nUnknown type is never guessed from speed.";
                FaaHudKeepOutRegion.Ensure(_legend, "chrome:cue-key", FaaKeepOutKind.Chrome, 4f);
            }
            else _legend.SetActive(!_legend.activeSelf);
        }

        public Rect OccupiedScreenBounds()
        {
            var corners = _boundsCorners;
            ((RectTransform)transform).GetWorldCorners(corners);
            var canvas = GetComponentInParent<Canvas>().rootCanvas;
            var camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            Vector2 min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
            Vector2 max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);
            if (_docked && !_expanded) return new Rect(min, Vector2.zero); // a collapsed flyout reserves no area
            if (_legend != null && _legend.activeInHierarchy)
            {
                _legend.GetComponent<RectTransform>().GetWorldCorners(corners);
                min = Vector2.Min(min, RectTransformUtility.WorldToScreenPoint(camera, corners[0]));
                max = Vector2.Max(max, RectTransformUtility.WorldToScreenPoint(camera, corners[2]));
            }
            return Rect.MinMaxRect(min.x - 8, min.y - 8, max.x + 8, max.y + 8);
        }

        private void MakeRow(IndicatorType type, float y, out TMP_Text title, out TMP_Text status, out Image background, out Segments segments)
        {
            var row = new GameObject(type + " marker row", typeof(RectTransform), typeof(Image));
            row.transform.SetParent(_content.transform, false);
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(8, y); rt.sizeDelta = new Vector2(_width - 16, 50);
            background = row.GetComponent<Image>(); background.color = Inactive; background.raycastTarget = false;
            row.AddComponent<Outline>().effectColor = FaaHudStyle.ChromeOutline;
            var iconObject = new GameObject("Vector icon", typeof(RectTransform), typeof(PilotCueSymbol));
            iconObject.transform.SetParent(row.transform, false);
            var icon = iconObject.GetComponent<PilotCueSymbol>();
            icon.rectTransform.anchorMin = icon.rectTransform.anchorMax = new Vector2(0, 0.5f);
            icon.rectTransform.anchoredPosition = new Vector2(22, 0);
            icon.rectTransform.sizeDelta = new Vector2(32, 32);
            icon.raycastTarget = false;
            icon.Configure(type == IndicatorType.Weather, false, 0, new Color(0.52f, 0.91f, 0.95f));
            float textWidth = _width - 16 - 44 - (2 * SegmentWidth + SegmentGap) - 16;
            title = Label(row.transform, "Marker state", TitleSize, TitleColor, new Vector2(44, -4), new Vector2(textWidth, 22));
            title.fontStyle = FontStyles.Bold;
            title.text = type == IndicatorType.Traffic ? "TRAFFIC CUES" : "WEATHER CUES";
            status = Label(row.transform, "Data status", BodySize, QuietColor, new Vector2(44, -26), new Vector2(textWidth, 20));
            segments = MakeSegments(row.transform, FaaChromeCommandRow.OnOff, new Vector2(_width - 16 - 8, -10), i => SetVisible(type, i == 0));
        }

        /// <summary>Right-aligned square segment buttons ending at <paramref name="rightTop"/>.x; the live state is highlighted.</summary>
        private Segments MakeSegments(Transform parent, string[] labels, Vector2 rightTop, System.Action<int> select)
        {
            var result = new Segments { plates = new Image[labels.Length], marks = new Image[labels.Length] };
            float x = rightTop.x - labels.Length * SegmentWidth - (labels.Length - 1) * SegmentGap;
            for (int i = 0; i < labels.Length; i++)
            {
                int index = i;
                Button button = MakeButton(parent, "Segment " + labels[i], new Vector2(x, rightTop.y), new Vector2(SegmentWidth, SegmentHeight));
                var image = button.GetComponent<Image>(); image.color = Inactive;
                var caption = Label(button.transform, "Label", TitleSize, TitleColor, Vector2.zero, new Vector2(SegmentWidth, SegmentHeight));
                caption.alignment = TextAlignmentOptions.Center; caption.text = labels[i]; caption.fontStyle = FontStyles.Bold;
                var mark = new GameObject("Active marker", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
                mark.transform.SetParent(button.transform, false);
                var mr = mark.rectTransform; mr.anchorMin = new Vector2(0, 0); mr.anchorMax = new Vector2(1, 0); mr.pivot = new Vector2(.5f, 0);
                mr.offsetMin = new Vector2(4, 2); mr.offsetMax = new Vector2(-4, 5); mark.color = FaaHudStyle.Cyan; mark.raycastTarget = false; mark.enabled = false;
                button.onClick.AddListener(() => select(index));
                result.plates[i] = image; result.marks[i] = mark;
                x += SegmentWidth + SegmentGap;
            }
            return result;
        }

        private static void ShowSegment(Segments segments, int state)
        {
            if (segments == null || segments.shown == state) return;
            segments.shown = state;
            for (int i = 0; i < segments.plates.Length; i++)
            {
                bool on = i == state;
                segments.plates[i].color = on ? FaaHudStyle.ChromeButtonActive : Inactive;
                segments.marks[i].enabled = on;
            }
        }

        private static TMP_Text Label(Transform parent, string name, float size, Color color, Vector2 position, Vector2 extent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.font = TMP_Settings.defaultFontAsset; text.richText = false; text.raycastTarget = false;
            text.textWrappingMode = TextWrappingModes.NoWrap; text.overflowMode = TextOverflowModes.Ellipsis;
            text.alignment = TextAlignmentOptions.MidlineLeft;
            FaaHudStyle.StyleText(text, size, FaaHudStyle.MinLabel, color, FaaHudStyle.MinQuietAlpha, false);
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = position; rt.sizeDelta = extent;
            return text;
        }

        private static Button MakeButton(Transform parent, string name, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = position; rt.sizeDelta = size;
            var image = go.GetComponent<Image>(); image.color = Active;
            go.AddComponent<Outline>().effectColor = FaaHudStyle.ChromeOutline;
            var button = go.GetComponent<Button>();
            button.targetGraphic = image;
            var colors = button.colors;
            colors.highlightedColor = new Color(1.4f, 1.4f, 1.4f);
            colors.pressedColor = new Color(1.9f, 1.9f, 1.9f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.05f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            return button;
        }

        /// <summary>Shows or hides one marker type and remembers the choice (also used by the COMMANDS flyout).</summary>
        public static void SetCueVisible(IndicatorSystemController controller, IndicatorType type, bool visible)
        {
            if (controller == null) return;
            controller.SetTypeVisible(type, visible);
            PlayerPrefs.SetInt(type == IndicatorType.Weather ? WeatherKey : TrafficKey, visible ? 1 : 0);
            PlayerPrefs.Save();
        }

        public void SetVisible(IndicatorType type, bool visible)
        {
            SetCueVisible(_controller, type, visible);
            Refresh();
        }

        public void Toggle(IndicatorType type) => SetVisible(type, !_controller.IsTypeVisible(type));

        /// <summary>Index of the range preset matching <paramref name="rangeNm"/> (-1 for an off-preset value: nothing is lit).</summary>
        public static int RangeIndex(float rangeNm)
        {
            for (int i = 0; i < RangePresetsNm.Length; i++) if (Mathf.Abs(RangePresetsNm[i] - rangeNm) < .5f) return i;
            return -1;
        }

        public void SelectRange(int index)
        {
            if (_controller == null || _controller.Settings == null || index < 0 || index >= RangePresetsNm.Length) return;
            _controller.Settings.maxDisplayDistance = RangePresetsNm[index];
            _controller.RefreshSettings();
            Refresh();
        }

        public void CycleRange()
        {
            float value = _controller.Settings.maxDisplayDistance;
            int index = RangeIndex(value);
            SelectRange(index < 0 ? 0 : (index + 1) % RangePresetsNm.Length);
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh || _controller == null) return;
            _nextRefresh = Time.unscaledTime + 0.2f;
            Refresh();
        }

        /// <summary>Bar caption for the CUES button: the action name only. The state is the separate <see cref="ChromeBadge"/>.</summary>
        public static string ChromeCaption(bool trafficOn, bool weatherOn) => "CUES";

        /// <summary>State badge drawn inside the CUES button: ON, OFF, TFC ONLY or WX ONLY.</summary>
        public static string ChromeBadge(bool trafficOn, bool weatherOn) =>
            trafficOn && weatherOn ? "ON" : !trafficOn && !weatherOn ? "OFF" : trafficOn ? "TFC ONLY" : "WX ONLY";

        private void Refresh()
        {
            if (_controller == null || _controller.Settings == null) return;
            if (_traffic == null) _traffic = _controller.GetComponent<TrafficIndicatorBridge>();
            if (_weather == null) _weather = _controller.GetComponent<WeatherIndicatorBridge>();
            bool trafficOn = _controller.IsTypeVisible(IndicatorType.Traffic), weatherOn = _controller.IsTypeVisible(IndicatorType.Weather);
            if (_docked)
            {
                var chrome = FaaPilotChrome.Current;
                // The CUES button is offered only while the cue system itself is running.
                if (chrome != null)
                {
                    chrome.SetButtonState(ChromeId, _controller.isActiveAndEnabled, _expanded, ChromeCaption(trafficOn, weatherOn));
                    chrome.SetButtonBadge(ChromeId, ChromeBadge(trafficOn, weatherOn), trafficOn || weatherOn);
                }
                if (!_expanded) return; // collapsed: nothing else on screen to update
            }
            UpdateRow(_trafficStatus, _trafficBackground, _trafficSegments, trafficOn,
                _controller.TrafficVisibleCount, _traffic != null ? _traffic.SourceStatus : "SOURCE NOT CONNECTED",
                ref _lastTrafficOn, ref _lastTrafficCount, ref _lastTrafficSource);
            UpdateRow(_weatherStatus, _weatherBackground, _weatherSegments, weatherOn,
                _controller.WeatherVisibleCount, _weather != null ? _weather.SourceStatus : "SOURCE NOT CONNECTED",
                ref _lastWeatherOn, ref _lastWeatherCount, ref _lastWeatherSource);
            _rowsInitialised = true;
            ShowSegment(_rangeSegments, RangeIndex(_controller.Settings.maxDisplayDistance));
            // The count line describes markers on screen; with both types off it would only restate OFF.
            bool anyOn = trafficOn || weatherOn;
            if (anyOn != _lastAnyOn) { _lastAnyOn = anyOn; _summary.gameObject.SetActive(anyOn); }
            int on = _controller.OnScreenCount, off = _controller.OffScreenCount, suppressed = _controller.SuppressedCount;
            if (anyOn && (on != _lastOn || off != _lastOff || suppressed != _lastSuppressed))
            {
                _lastOn = on; _lastOff = off; _lastSuppressed = suppressed;
                _summary.text = on + " IN VIEW  ·  " + off + " OFF SCREEN" + (suppressed > 0 ? "  ·  " + suppressed + " DECLUTTERED" : "");
            }
        }

        private void UpdateRow(TMP_Text status, Image background, Segments segments, bool enabled, int count, string source,
            ref bool lastEnabled, ref int lastCount, ref string lastSource)
        {
            ShowSegment(segments, enabled ? 0 : 1);
            if (_rowsInitialised && enabled == lastEnabled && count == lastCount && source == lastSource) return;
            lastEnabled = enabled; lastCount = count; lastSource = source;
            // The subline is the data source (and, when shown, how many markers are drawn); the ON/OFF state lives in the segments only.
            status.text = enabled ? source + "  ·  " + count + " SHOWN" : source;
            background.color = enabled ? Active : Inactive;
        }
    }
}
