using FAA.XPlaneIntegration.Runtime;
using TMPro;
using TrafficRadar;
using TrafficRadar.Core;
using UnityEngine;
using UnityEngine.UI;
using WeatherRadar;

namespace FAA.Customization
{
    /// <summary>
    /// Shared instrument chrome. The DISPLAY [ON|OFF] selector is local visibility, never a command to the simulator or a claim
    /// about TCAS/radar transmitter power. State and action are separate: the caption names the function, each option is its own
    /// button and the active one is filled (as in Settings), so a label never has to be read as either a state or a command.
    /// Every text is at least the FAA minimum (FaaRadarVisualStyle.MinimumFont) at its rendered size: the chrome is scaled by
    /// FaaRadarVisualStyle.ReadabilityScale on small world-space panels, footers sit on an opaque plate, numeric fields are sized
    /// from their widest value (never ellipsis-truncated), and low-priority footer parts are dropped instead of truncated.
    /// The weather footer is one row (range | mode · tilt); the traffic footer is two (range | orientation, then the
    /// always-annunciated altitude band | traffic count).
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class FaaRadarPresentation : MonoBehaviour
    {
        /// <summary>Space reserved above the radar root for the header at scale 1 (see <see cref="HeaderExtent"/>).</summary>
        public const float HeaderClearance = 64f;
        public const float HeaderHeight = 56f;
        /// <summary>Single-row footer height (weather).</summary>
        public const float FooterHeight = 28f;
        /// <summary>Two-row footer height (traffic).</summary>
        public const float TrafficFooterHeight = 50f;
        /// <summary>Header/footer plates are never narrower than this (reference units), so the title, the DISPLAY selector and a
        /// unit-bearing range fit at the FAA minimum size without truncation.</summary>
        public const float MinChromeWidth = 256f, MaxChromeWidth = 420f;
        public const float TitleFont = 15f, SourceFont = 15f, ToggleFont = 15f, RangeFont = 16f, DetailFont = 15f;
        public const float MessageFont = 17f, HintFont = 15f;
        /// <summary>DISPLAY selector geometry (reference units): caption, ON segment, OFF segment.</summary>
        public const float DisplayCaptionWidth = 66f, DisplayOnWidth = 36f, DisplayOffWidth = 40f;
        private const float CaptionGap = 4f, SegmentGap = 2f, FooterPad = 8f, FooterGap = 8f;
        public const float DisplaySwitchWidth = DisplayCaptionWidth + CaptionGap + DisplayOnWidth + SegmentGap + DisplayOffWidth;
        /// <summary>Widest value each range readout can show; the range field is sized from it so it never truncates or jumps.</summary>
        public const string WidestWeatherRange = "320 NM", WidestTrafficRange = "80 NM";
        public enum DataState { Preview, Live, Waiting, Stale, Standby, RadarOff, DisplayOff }

        [SerializeField] private FaaRadarKind radarKind;
        [SerializeField] private bool displayEnabled = true;
        [SerializeField] private XPlane12ApiHudBridge bridge;
        [SerializeField] private XPlaneOriginalWeatherRadarDisplay weatherDisplay;
        [SerializeField] private WeatherRadarDataProvider weatherData;
        [SerializeField] private TrafficRadarDisplay trafficDisplay;
        [SerializeField] private TrafficRadarController trafficController;
        private RectTransform _header, _footer, _messageRoot, _switch;
        private TMP_Text _title, _source, _switchCaption, _onLabel, _offLabel, _range, _detail, _band, _count, _message, _messageHint;
        private Image _onSegment, _offSegment, _footerPlate;
        private CanvasGroup _content;
        private RadarTrafficOverlay _trafficOverlay;
        private XPlaneWeatherRadarFace _weatherFace;
        private float _nextRefresh, _nextLegacySweep;
        private float _visualAlpha = 1f;
        private float _scale = 1f;
        private float _rangeWidth = 70f, _rangeWidthScale = -1f, _countField = 60f;
        private string _rangeWidthText;
        private readonly Vector3[] _corners = new Vector3[4];
        private readonly string[] _parts = new string[4];
        // Cached readout strings: rebuilt only when the underlying value changes (no per-refresh string building).
        private float _shownRange = float.NaN, _shownTilt = float.NaN;
        private string _rangeText = string.Empty, _tiltText = string.Empty, _modeText = "WX";
        private RadarMode _shownMode = (RadarMode)(-1);
        private string _lastDetail = string.Empty, _lastCount = string.Empty;
        private readonly string[] _lastDetailParts = new string[4], _lastCountParts = new string[4];
        private float _lastDetailWidth = -1f, _lastCountWidth = -1f;
        private static string[] _trafficCountTexts, _noTagTexts;

        public bool IsDisplayOn => displayEnabled;
        public FaaRadarKind RadarKind => radarKind;
        public DataState CurrentState { get; private set; }
        public string StatusText => _source != null ? _source.text : string.Empty;
        /// <summary>Readability scale currently applied to this radar's text (1 on reference-scale canvases).</summary>
        public float ChromeScale => _scale;
        /// <summary>Height the header occupies above the radar root, in root units.</summary>
        public float HeaderExtent => (HeaderHeight + 8f) * _scale;
        /// <summary>How far (root units) the header/footer plates extend past each side of the root; neighbours dock beyond it.</summary>
        public float ChromeOverhang
        {
            get
            {
                float rootWidth = transform is RectTransform rect ? rect.rect.width : 0f;
                return Mathf.Max(0f, (ChromeWidth(rootWidth, _scale) * _scale - rootWidth) * .5f);
            }
        }
        /// <summary>Height of this radar's footer plate in reference units (one row for weather, two for traffic).</summary>
        public float FooterPlateHeight => radarKind == FaaRadarKind.Traffic ? TrafficFooterHeight : FooterHeight;

        private static readonly Color Text = FaaRadarVisualStyle.TextPrimary;
        private static readonly Color Muted = FaaRadarVisualStyle.TextSecondary;
        private static readonly Color Accent = new Color(.40f, .86f, .78f, 1f);
        private static readonly Color Caution = new Color(.98f, .73f, .34f, 1f);
        private static readonly Color Plate = new Color(.025f, .06f, .08f, .94f);
        /// <summary>Fill of the selected DISPLAY segment (FaaWorkspaceUi.Selected, the Settings convention).</summary>
        private static readonly Color SegmentSelected = FaaWorkspaceUi.Selected;
        private static readonly Color SegmentIdle = new Color(.06f, .085f, .10f, 1f);

        public void Configure(FaaRadarKind kind, XPlane12ApiHudBridge source)
        {
            radarKind = kind;
            bridge = source;
            ResolveReferences();
            EnsureChrome();
            RefreshPresentation();
        }

        private void OnEnable()
        {
            ResolveReferences();
            EnsureChrome();
            RefreshPresentation();
        }

        private void OnDisable()
        {
            if (_content != null) _content.alpha = 1f;
            if (trafficDisplay != null) trafficDisplay.InstrumentDisplayEnabled = true;
        }

        private void Update()
        {
            if (_header == null) EnsureChrome();
            if (!Application.isPlaying || Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + .25f;
                RefreshPresentation();
            }
            float alpha = CurrentState == DataState.DisplayOff ? 0f :
                CurrentState == DataState.Live || CurrentState == DataState.Preview ? 1f : .12f;
            // Fade within FaaHudStyle.FadeSeconds; data that is not live is removed, not frozen.
            _visualAlpha = Application.isPlaying
                ? Mathf.MoveTowards(_visualAlpha, alpha, Time.unscaledDeltaTime / FaaHudStyle.FadeSeconds) : alpha;
            if (_content != null && !Mathf.Approximately(_content.alpha, _visualAlpha)) _content.alpha = _visualAlpha;
        }

        public void ToggleDisplay() => SetDisplayEnabled(!displayEnabled);

        public void SetDisplayEnabled(bool visible)
        {
            displayEnabled = visible;
            if (!visible && trafficDisplay != null) trafficDisplay.ClearNavigationPreview();
            RefreshPresentation();
        }

        private void SelectDisplayOn() => SetDisplayEnabled(true);
        private void SelectDisplayOff() => SetDisplayEnabled(false);

        public static DataState ResolveState(bool displayOn, bool playing, bool freshData,
            bool hasPreviousData, bool standby, bool knownRadarOff)
        {
            if (!displayOn) return DataState.DisplayOff;
            if (!playing) return DataState.Preview;
            // Stale aircraft power/mode must not masquerade as authoritative OFF/STBY.
            if (!freshData) return hasPreviousData ? DataState.Stale : DataState.Waiting;
            if (knownRadarOff) return DataState.RadarOff;
            if (standby) return DataState.Standby;
            return DataState.Live;
        }

        /// <summary>
        /// Provenance that matches the actual source (RDR-08/17): synthetic training cells and the network fallback feed are named
        /// as such, with the same words as the status chip ("FALLBACK DATA"). "LIVE" is reserved for the local X-Plane simulator;
        /// neither the fallback feed nor synthetic training cells are ever called live.
        /// </summary>
        public static string SourceText(bool weather, bool fallbackFeed, bool trainingWeather, bool proceduralWeather,
            bool playing, bool fresh, bool previous)
        {
            string provenance = weather && trainingWeather ? "TRAINING WX"
                : fallbackFeed ? "FALLBACK DATA"
                : weather ? (proceduralWeather ? "X-PLANE WX" : "X-PLANE") : "X-PLANE";
            bool notSimulator = (weather && trainingWeather) || fallbackFeed;
            if (notSimulator && playing && fresh) return provenance;
            string freshness = !playing ? "PREVIEW" : fresh ? "LIVE" : previous ? "STALE" : "WAITING";
            return provenance + " · " + freshness;
        }

        public static bool SourceIsCaution(bool fallbackFeed, bool trainingWeather, bool playing, bool fresh) =>
            fallbackFeed || trainingWeather || (playing && !fresh);

        /// <summary>Range readout with its unit, e.g. "160 NM". Never abbreviated or truncated.</summary>
        public static string FormatRange(float range) => range.ToString("0.#") + " NM";

        /// <summary>Traffic count wording: "NO TFC" when nothing is displayed, otherwise "n TFC" (replaces the non-standard "n TGT").</summary>
        public static string TrafficCountText(int count)
        {
            count = Mathf.Max(0, count);
            if (count >= 100) return count + " TFC";
            _trafficCountTexts ??= new string[100];
            return _trafficCountTexts[count] ??= count == 0 ? "NO TFC" : count + " TFC";
        }

        private static string NoTagText(int count)
        {
            if (count <= 0) return null;
            if (count >= 100) return count + " NO TAG";
            _noTagTexts ??= new string[100];
            return _noTagTexts[count] ??= count + " NO TAG";
        }

        /// <summary>Conservative width of LiberationSans text, used to drop footer parts instead of truncating.</summary>
        public static float EstimateTextWidth(string text, float fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            float em = 0f;
            foreach (char c in text)
                em += c == ' ' || c == '·' || c == '.' || c == ':' ? .32f : char.IsDigit(c) ? .56f
                    : c == 'W' || c == 'M' ? .95f : char.IsUpper(c) ? .72f : c == '°' ? .42f : .58f;
            return em * fontSize;
        }

        /// <summary>Joins parts in priority order and stops before the text would exceed the width.
        /// The first part is always kept; nothing is ever ellipsis-truncated.</summary>
        public static string ComposeFooter(string[] parts, int count, float width, float fontSize)
        {
            string result = string.Empty;
            for (int i = 0; i < count; i++)
            {
                if (string.IsNullOrEmpty(parts[i])) continue;
                string next = result.Length == 0 ? parts[i] : result + " · " + parts[i];
                if (result.Length > 0 && EstimateTextWidth(next, fontSize) > width) continue;
                result = next;
            }
            return result;
        }

        /// <summary>
        /// Width (reference units) of a footer range field: the measured width of the widest value it can show (and of the current
        /// value, if wider), plus a small margin. <paramref name="measured"/> is the TMP preferred width when available (0 otherwise);
        /// the conservative estimate (bold adds TMP's 7% bold spacing) is the floor.
        /// </summary>
        public static float RangeFieldWidth(string widest, string current, float measuredWidest, float measuredCurrent, float fontSize)
        {
            float widestWidth = Mathf.Max(measuredWidest, EstimateTextWidth(widest, fontSize) * 1.07f);
            float currentWidth = Mathf.Max(measuredCurrent, EstimateTextWidth(current, fontSize) * 1.07f);
            return Mathf.Ceil(Mathf.Max(widestWidth, currentWidth)) + 6f;
        }

        /// <summary>Chrome plate width in reference units for a root of the given size at the given readability scale.</summary>
        public static float ChromeWidth(float rootWidth, float scale) =>
            Mathf.Clamp(rootWidth / Mathf.Max(.01f, scale), MinChromeWidth, MaxChromeWidth);

        private void ResolveReferences()
        {
            if (radarKind == FaaRadarKind.Weather)
            {
                if (weatherDisplay == null) weatherDisplay = GetComponentInChildren<XPlaneOriginalWeatherRadarDisplay>(true);
                if (weatherData == null) weatherData = GetComponentInChildren<WeatherRadarDataProvider>(true);
                if (_content == null && weatherDisplay != null)
                    _content = EnsureContentGroup(weatherDisplay.gameObject);
            }
            else
            {
                if (trafficDisplay == null) trafficDisplay = GetComponentInChildren<TrafficRadarDisplay>(true);
                if (trafficController == null) trafficController = GetComponentInChildren<TrafficRadarController>(true);
                if (_content == null && trafficDisplay != null)
                    _content = EnsureContentGroup(trafficDisplay.gameObject);
            }
        }

        public static CanvasGroup EnsureContentGroup(GameObject content)
        {
            // Native Unity components can return a non-CLR-null missing-object
            // sentinel. Do not use ?? here: it can leave no actual CanvasGroup.
            if (!content.TryGetComponent<CanvasGroup>(out var group)) group = content.AddComponent<CanvasGroup>();
            return group;
        }

        private void EnsureChrome()
        {
            if (!(transform is RectTransform)) return;
            _header = Child(transform, "Radar Status Header");
            Image plate = _header.GetComponent<Image>() ?? _header.gameObject.AddComponent<Image>();
            FaaRadarVisualStyle.ApplyRounded(plate, Plate, 12);
            plate.raycastTarget = false;
            _title = Label(_header, "Instrument", TitleFont, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            _source = Label(_header, "Data Status", SourceFont, TextAlignmentOptions.MidlineLeft);
            RetireLegacyToggle(_header);
            _switch = Child(_header, "Display Switch");
            _switchCaption = Label(_switch, "Caption", ToggleFont, TextAlignmentOptions.MidlineRight);
            SetText(_switchCaption, "DISPLAY");
            SetColor(_switchCaption, Muted);
            _onSegment = Segment(_switch, "On", SelectDisplayOn, out _onLabel);
            _offSegment = Segment(_switch, "Off", SelectDisplayOff, out _offLabel);
            SetText(_onLabel, "ON");
            SetText(_offLabel, "OFF");
            _footer = Child(transform, "Radar Status Footer");
            _footerPlate = _footer.GetComponent<Image>() ?? _footer.gameObject.AddComponent<Image>();
            FaaRadarVisualStyle.ApplyRounded(_footerPlate, new Color(.025f, .06f, .08f, .90f), 10);
            _range = Label(_footer, "Range", RangeFont, TextAlignmentOptions.MidlineLeft, FontStyles.Bold);
            _detail = Label(_footer, "Mode", DetailFont, TextAlignmentOptions.MidlineRight);
            _band = Label(_footer, "Altitude Band", DetailFont, TextAlignmentOptions.MidlineLeft);
            _count = Label(_footer, "Traffic Count", DetailFont, TextAlignmentOptions.MidlineRight);
            bool traffic = radarKind == FaaRadarKind.Traffic;
            if (_band.gameObject.activeSelf != traffic) _band.gameObject.SetActive(traffic);
            if (_count.gameObject.activeSelf != traffic) _count.gameObject.SetActive(traffic);
            _messageRoot = Child(transform, "Radar Availability");
            var background = _messageRoot.GetComponent<Image>() ?? _messageRoot.gameObject.AddComponent<Image>();
            FaaRadarVisualStyle.ApplyRounded(background, Plate, 10);
            background.raycastTarget = false;
            _message = Label(_messageRoot, "Message", MessageFont, TextAlignmentOptions.Center, FontStyles.Bold, false, true);
            _messageHint = Label(_messageRoot, "Hint", HintFont, TextAlignmentOptions.Center, FontStyles.Normal, true);
            // Chrome must not intercept radar taps. Only the DISPLAY segments are interactive.
            foreach (var graphic in _footer.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            LayoutChrome();
        }

        /// <summary>The pre-segment single "DISPLAY ON/OFF" button (serialized in older scenes) is retired, never left clickable.</summary>
        private void RetireLegacyToggle(Transform header)
        {
            Transform legacy = header.Find("Display Toggle");
            if (legacy == null) return;
            if (legacy.TryGetComponent<Button>(out var button))
            {
                button.onClick.RemoveListener(ToggleDisplay);
                button.interactable = false;
            }
            foreach (var graphic in legacy.GetComponentsInChildren<Graphic>(true)) graphic.raycastTarget = false;
            if (legacy.gameObject.activeSelf) legacy.gameObject.SetActive(false);
        }

        private Image Segment(Transform parent, string name, UnityEngine.Events.UnityAction action, out TMP_Text label)
        {
            RectTransform rect = Child(parent, name);
            bool created = !rect.TryGetComponent<Image>(out var image);
            if (created) image = rect.gameObject.AddComponent<Image>();
            // The fill is the state indicator and belongs to the refresh; only a new segment starts idle.
            FaaRadarVisualStyle.ApplyRounded(image, created ? SegmentIdle : image.color, 6);
            image.raycastTarget = true;
            // A slightly larger hit area than the drawn segment (left, bottom, right, top) for mouse and XR rays.
            var padding = new Vector4(-1f, -6f, -1f, -3f);
            if (image.raycastPadding != padding) image.raycastPadding = padding;
            Button button = rect.GetComponent<Button>() ?? rect.gameObject.AddComponent<Button>();
            button.onClick.RemoveListener(action);
            button.onClick.AddListener(action);
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            FaaRadarVisualStyle.ConfigureButton(button, image);
            label = Label(rect, "Label", ToggleFont, TextAlignmentOptions.Center);
            return image;
        }

        private void LayoutChrome()
        {
            var root = (RectTransform)transform;
            float s = _scale;
            // Lay out in reference units (width / scale) and scale the plates, so text keeps its
            // physical size on any root size or panel distance.
            float width = ChromeWidth(root.rect.width, s);
            Dock(_header, new Vector2(.5f, 1f), new Vector2(0, (HeaderHeight * .5f + 6f) * s), new Vector2(width, HeaderHeight), null, s);
            Dock(_title.rectTransform, new Vector2(0, .5f), new Vector2(10f, 12f), new Vector2(width - DisplaySwitchWidth - 22f, 24f), new Vector2(0, .5f));
            Dock(_source.rectTransform, new Vector2(0, .5f), new Vector2(10f, -14f), new Vector2(width - 20f, 22f), new Vector2(0, .5f));
            Dock(_switch, new Vector2(1f, .5f), new Vector2(-6f, 12f), new Vector2(DisplaySwitchWidth, 28f), new Vector2(1f, .5f));
            Dock(_switchCaption.rectTransform, new Vector2(0f, .5f), Vector2.zero, new Vector2(DisplayCaptionWidth, 26f), new Vector2(0f, .5f));
            Dock(_onSegment.rectTransform, new Vector2(0f, .5f), new Vector2(DisplayCaptionWidth + CaptionGap, 0f), new Vector2(DisplayOnWidth, 26f), new Vector2(0f, .5f));
            Dock(_offSegment.rectTransform, new Vector2(1f, .5f), Vector2.zero, new Vector2(DisplayOffWidth, 26f), new Vector2(1f, .5f));
            Dock(_onLabel.rectTransform, new Vector2(.5f, .5f), Vector2.zero, new Vector2(DisplayOnWidth - 2f, 24f));
            Dock(_offLabel.rectTransform, new Vector2(.5f, .5f), Vector2.zero, new Vector2(DisplayOffWidth - 2f, 24f));
            bool traffic = radarKind == FaaRadarKind.Traffic;
            float footerHeight = traffic ? TrafficFooterHeight : FooterHeight;
            float footerY = -(footerHeight * .5f + 6f) * s;
            Vector2 footerAnchor = new Vector2(.5f, 0f);
            if (!traffic && TryGetPictureBottom(root, out float pictureBottom))
            {
                // Dock under the actual sector picture, not under the square root's empty bottom band.
                footerAnchor = new Vector2(.5f, .5f);
                footerY = pictureBottom - root.rect.center.y - (footerHeight * .5f + 6f) * s;
            }
            Dock(_footer, footerAnchor, new Vector2(0f, footerY), new Vector2(width, footerHeight), null, s);
            float rowY = traffic ? 12f : 0f, rowHeight = traffic ? 24f : FooterHeight;
            Dock(_range.rectTransform, new Vector2(0, .5f), new Vector2(FooterPad, rowY), new Vector2(_rangeWidth, rowHeight), new Vector2(0, .5f));
            Dock(_detail.rectTransform, new Vector2(1f, .5f), new Vector2(-FooterPad, rowY), new Vector2(DetailWidth(width), rowHeight), new Vector2(1f, .5f));
            if (traffic) DockTrafficRow(width);
            float messageWidth = Mathf.Min(width - 20f, 236f);
            Dock(_messageRoot, new Vector2(.5f, .5f), Vector2.zero, new Vector2(messageWidth, 86f), null, s);
            Dock(_message.rectTransform, new Vector2(.5f, .5f), new Vector2(0, 22f), new Vector2(messageWidth - 12f, 26f));
            Dock(_messageHint.rectTransform, new Vector2(.5f, .5f), new Vector2(0, -14f), new Vector2(messageWidth - 16f, 44f));
        }

        /// <summary>Second traffic footer row: the count field is sized from its text and the band gets the rest, so neither is cut off.</summary>
        private void DockTrafficRow(float chromeWidth)
        {
            float rowWidth = chromeWidth - 2f * FooterPad - FooterGap;
            float countField = Mathf.Clamp(_countField, 40f, Mathf.Max(40f, rowWidth - 40f));
            Dock(_count.rectTransform, new Vector2(1f, .5f), new Vector2(-FooterPad, -12f), new Vector2(countField, 22f), new Vector2(1f, .5f));
            Dock(_band.rectTransform, new Vector2(0, .5f), new Vector2(FooterPad, -12f), new Vector2(rowWidth - countField, 22f), new Vector2(0, .5f));
        }

        /// <summary>Width left for the right-hand footer field once the range field (sized from its widest value) is placed.</summary>
        private float DetailWidth(float chromeWidth) => Mathf.Max(40f, chromeWidth - 2f * FooterPad - FooterGap - _rangeWidth);

        private bool TryGetPictureBottom(RectTransform root, out float bottom)
        {
            bottom = 0f;
            RectTransform picture = weatherDisplay != null ? weatherDisplay.PictureRect : null;
            if (picture == null || !picture.gameObject.activeInHierarchy || picture.rect.height < 8f) return false;
            picture.GetWorldCorners(_corners);
            bottom = float.PositiveInfinity;
            for (int i = 0; i < 4; i++) bottom = Mathf.Min(bottom, root.InverseTransformPoint(_corners[i]).y);
            return !float.IsInfinity(bottom) && !float.IsNaN(bottom);
        }

        /// <summary>Re-measures the range field only when its text or the readability scale changes.</summary>
        private void UpdateRangeWidth(string current)
        {
            if (_range == null || (ReferenceEquals(current, _rangeWidthText) && Mathf.Approximately(_rangeWidthScale, _scale))) return;
            _rangeWidthText = current;
            _rangeWidthScale = _scale;
            string widest = radarKind == FaaRadarKind.Weather ? WidestWeatherRange : WidestTrafficRange;
            _rangeWidth = RangeFieldWidth(widest, current, Measure(_range, widest), Measure(_range, current), RangeFont);
        }

        private static float Measure(TMP_Text label, string text)
        {
            if (label == null || label.font == null || string.IsNullOrEmpty(text)) return 0f;
            float width = label.GetPreferredValues(text).x;
            return float.IsNaN(width) || float.IsInfinity(width) ? 0f : width;
        }

        public void RefreshPresentation()
        {
            if (_header == null) return;
            ResolveReferences();
            UpdateReadabilityScale();
            bool weather = radarKind == FaaRadarKind.Weather;
            float range = weather ? (weatherData != null ? weatherData.RadarData.currentRange : 160f)
                : (trafficDisplay != null ? trafficDisplay.SelectedRangeNM : 40f);
            if (!Mathf.Approximately(range, _shownRange)) { _shownRange = range; _rangeText = FormatRange(range); }
            UpdateRangeWidth(_rangeText);
            LayoutChrome();
            bool feed = bridge != null && bridge.IsFeedHealthy && bridge.LastPacketAgeSeconds <= 6f;
            bool previous = weather ? weatherDisplay != null && weatherDisplay.HasUsableTexture : bridge != null && bridge.LatestFlightData != null;
            bool standby = weather && weatherData != null && weatherData.RadarData.currentMode == RadarMode.STBY;
            bool poweredOff = weather && weatherDisplay != null && weatherDisplay.HasRadarPowerState && !weatherDisplay.IsRadarPowered;
            bool training = weather && weatherDisplay != null && weatherDisplay.IsTrainingTexture;
            // Training cells are synthetic: they are fresh only as a picture, never as X-Plane data.
            bool fresh = feed && (!weather || standby || poweredOff || (weatherDisplay != null && weatherDisplay.HasFreshTexture));
            if (training && !feed) fresh = weatherDisplay.HasFreshTexture;
            CurrentState = ResolveState(displayEnabled, Application.isPlaying, fresh, previous, standby, poweredOff);
            if (trafficDisplay != null)
            {
                trafficDisplay.InstrumentDisplayEnabled = displayEnabled;
                trafficDisplay.UseExternalRangeReadout = true;
            }
            SetText(_title, weather ? "WEATHER" : "TRAFFIC");
            bool fallbackFeed = XPlaneSourceDiscovery.Active != null && XPlaneSourceDiscovery.Active.UsingFallback;
            bool procedural = weather && (weatherDisplay == null || weatherDisplay.IsProceduralTexture || !Application.isPlaying);
            string source = SourceText(weather, fallbackFeed, training, procedural, Application.isPlaying, fresh, previous);
            Color sourceColor = SourceIsCaution(fallbackFeed, training, Application.isPlaying, fresh) ? Caution : Muted;
            ApplyDisplaySelector();
            SetText(_range, _rangeText);
            SetColor(_range, Text);
            float chromeWidth = ChromeWidth(((RectTransform)transform).rect.width, _scale);
            float detailWidth = DetailWidth(chromeWidth);
            string detail;
            if (weather)
            {
                float tilt = weatherData != null ? Mathf.Round(weatherData.RadarData.tiltAngle * 10f) / 10f : 0f;
                if (!Mathf.Approximately(tilt, _shownTilt)) { _shownTilt = tilt; _tiltText = "TILT " + tilt.ToString("+0.0;-0.0;0.0") + "°"; }
                RadarMode mode = weatherData != null ? weatherData.RadarData.currentMode : RadarMode.WX;
                if (mode != _shownMode) { _shownMode = mode; _modeText = mode.ToString().Replace("_", "+"); }
                _parts[0] = _modeText;
                _parts[1] = _tiltText;
                // Power is annunciated only when X-Plane reports it OFF; unknown power is not shown as a value.
                _parts[2] = feed && weatherDisplay != null && weatherDisplay.HasRadarPowerState && !weatherDisplay.IsRadarPowered ? "PWR OFF" : null;
                _parts[3] = null;
                detail = Compose(_parts, _lastDetailParts, ref _lastDetail, ref _lastDetailWidth, detailWidth);
            }
            else
            {
                // Row 1: orientation. Row 2: the altitude band is ALWAYS annunciated, so a band-filtered scope is never mistaken
                // for an empty sky, next to the displayed-traffic count ("NO TFC" / "n TFC").
                detail = trafficDisplay != null && trafficDisplay.TrackUpModeEnabled ? "TRK UP" : "N UP";
                TrafficAltitudeBand band = trafficController != null ? trafficController.AltitudeBand : TrafficAltitudeBand.Normal;
                int hidden = fresh && _trafficOverlay != null ? _trafficOverlay.HiddenTagCount : 0;
                _parts[0] = fresh && trafficController != null ? TrafficCountText(trafficController.TargetCount) : null;
                _parts[1] = NoTagText(hidden);
                _parts[2] = _parts[3] = null;
                float half = (chromeWidth - 2f * FooterPad - FooterGap) * .5f;
                string count = _parts[0] == null ? string.Empty : Compose(_parts, _lastCountParts, ref _lastCount, ref _lastCountWidth, half);
                string bandText = TrafficAltitudeBands.FooterLabel(band);
                // The full band wording yields to the TCAS short form only when band and count would not both fit.
                float countWidth = EstimateTextWidth(count, DetailFont);
                if (EstimateTextWidth(bandText, DetailFont) + countWidth > chromeWidth - 2f * FooterPad - FooterGap)
                    bandText = TrafficAltitudeBands.ShortLabel(band);
                SetText(_band, bandText);
                SetColor(_band, band == TrafficAltitudeBand.Normal ? Muted : Text);
                SetText(_count, count);
                SetColor(_count, Muted);
                _countField = Mathf.Ceil(countWidth) + 4f;
                DockTrafficRow(chromeWidth);
            }
            SetColor(_detail, Muted);
            bool message = CurrentState != DataState.Live && CurrentState != DataState.Preview;
            bool turbulenceMode = weather && weatherData != null && TurbulenceEvidence.IsTurbulenceMode(weatherData.RadarData.currentMode);
            bool turbulenceUnavailable = turbulenceMode && CurrentState == DataState.Live;
            if (turbulenceUnavailable)
            {
                // A fresh rain texture must not claim the turbulence channel is live.
                source = training ? "TRAINING WX · NO TURB SCAN" : "X-PLANE WX · NO TURB SCAN";
                sourceColor = Caution;
                if (weatherData.RadarData.currentMode == RadarMode.TURB) message = true;
                else detail = "WX+T · RAIN ONLY";
            }
            SetText(_source, source);
            SetColor(_source, sourceColor);
            SetText(_detail, detail);
            if (_messageRoot.gameObject.activeSelf != message) _messageRoot.gameObject.SetActive(message);
            string headline = CurrentState switch
            {
                DataState.DisplayOff => "DISPLAY OFF", DataState.RadarOff => "RADAR POWER OFF",
                DataState.Standby => "STANDBY", DataState.Stale => "DATA STALE", _ => "WAITING FOR DATA"
            };
            string hint = CurrentState switch
            {
                DataState.DisplayOff => "Select DISPLAY ON above to restore",
                DataState.RadarOff => "X-Plane reports transmitter off",
                DataState.Standby => "Select a weather mode to resume",
                _ => training ? "Synthetic cells, not X-Plane weather" : "Do not use for guidance"
            };
            Color headlineColor = CurrentState == DataState.Stale || CurrentState == DataState.Waiting ? Caution : Text;
            if (turbulenceUnavailable && weatherData.RadarData.currentMode == RadarMode.TURB)
            {
                headline = "TURB SCAN UNAVAILABLE";
                headlineColor = Caution;
                hint = TurbulenceEvidence.Explanation(bridge != null ? bridge.LatestSnapshot?.Weather : null, feed);
            }
            SetText(_message, headline);
            SetColor(_message, headlineColor);
            // Never shrink below the FAA minimum to fit: the hint wraps instead.
            float headlineFont = EstimateTextWidth(headline, MessageFont) > _message.rectTransform.rect.width ? FaaRadarVisualStyle.MinimumFont : MessageFont;
            if (!Mathf.Approximately(_message.fontSize, headlineFont)) _message.fontSize = headlineFont;
            SetText(_messageHint, hint);
            SuppressLegacyReadouts();
        }

        /// <summary>Composes footer parts only when one of them (cached strings, compared by reference) or the width changed.</summary>
        private static string Compose(string[] parts, string[] lastParts, ref string last, ref float lastWidth, float width)
        {
            bool same = Mathf.Approximately(width, lastWidth);
            for (int i = 0; i < parts.Length && same; i++) same = ReferenceEquals(parts[i], lastParts[i]);
            if (same) return last;
            for (int i = 0; i < parts.Length; i++) lastParts[i] = parts[i];
            lastWidth = width;
            last = ComposeFooter(parts, parts.Length, width, DetailFont);
            return last;
        }

        /// <summary>Selected segment is filled with accent text; the other is a quiet, clickable option.</summary>
        private void ApplyDisplaySelector()
        {
            SetSegment(_onSegment, _onLabel, displayEnabled);
            SetSegment(_offSegment, _offLabel, !displayEnabled);
        }

        private static void SetSegment(Image segment, TMP_Text label, bool selected)
        {
            if (segment == null) return;
            Color fill = selected ? SegmentSelected : SegmentIdle;
            if (segment.color != fill) segment.color = fill;
            SetColor(label, selected ? Accent : Muted);
            FontStyles style = selected ? FontStyles.Bold : FontStyles.Normal;
            if (label != null && label.fontStyle != style) label.fontStyle = style;
        }

        private void UpdateReadabilityScale()
        {
            float measured = FaaRadarVisualStyle.ReadabilityScale((RectTransform)transform);
            // While a panel is inspected the camera may zoom in or the panel may be framed closer. Never shrink the text then:
            // the inspection magnifies it instead of cancelling out, and text sizes never pump during the camera move.
            if (Application.isPlaying && FaaHudInspection.Active && measured < _scale) measured = _scale;
            _scale = FaaRadarVisualStyle.StableScale(_scale, measured);
            if (radarKind == FaaRadarKind.Traffic && trafficDisplay != null)
            {
                if (_trafficOverlay == null) _trafficOverlay = trafficDisplay.GetComponentInChildren<RadarTrafficOverlay>(true);
                if (_trafficOverlay != null) _trafficOverlay.TextScale = _scale;
                trafficDisplay.LabelScale = _scale;
            }
            else if (weatherDisplay != null)
            {
                if (_weatherFace == null) _weatherFace = weatherDisplay.GetComponentInChildren<XPlaneWeatherRadarFace>(true);
                if (_weatherFace != null) _weatherFace.TextScale = _scale;
            }
        }

        private static void SetText(TMP_Text label, string text)
        {
            if (label != null && label.text != text) label.text = text;
        }

        private static void SetColor(TMP_Text label, Color color)
        {
            if (label != null && label.color != color) label.color = color;
        }

        private void SuppressLegacyReadouts()
        {
            // Hierarchy sweep is not a per-tick job: legacy labels only appear on (re)load.
            if (Application.isPlaying && Time.unscaledTime < _nextLegacySweep) return;
            _nextLegacySweep = Time.unscaledTime + 2f;
            foreach (var label in GetComponentsInChildren<TMP_Text>(true))
            {
                string key = label.name.Replace(" ", "");
                if (key == "RangeLabel" || key == "TiltLabel" || key == "ModeLabel" || key == "TextureStatusLabel" || key == "SourceLabel" || key == "TextureAgeLabel")
                    label.gameObject.SetActive(false);
            }
            Transform power = weatherDisplay != null ? weatherDisplay.transform.parent.Find("WeatherPowerBadge") : null;
            if (power != null) power.gameObject.SetActive(false);
        }

        private static RectTransform Child(Transform parent, string name)
        {
            Transform found = parent.Find(name);
            GameObject go = found != null ? found.gameObject : new GameObject(name, typeof(RectTransform));
            if (found == null) go.transform.SetParent(parent, false);
            return go.GetComponent<RectTransform>();
        }

        /// <summary>
        /// Finds or creates a chrome label and enforces the legibility rules (size floor, no auto-size, no ellipsis). Style and
        /// colour are initialised only on creation: the refresh owns them afterwards, so the 1 Hz re-configure never toggles them
        /// (and never dirties the canvas) on existing labels.
        /// </summary>
        private static TMP_Text Label(Transform parent, string name, float size, TextAlignmentOptions align,
            FontStyles style = FontStyles.Normal, bool wrap = false, bool sizeOwnedByRefresh = false)
        {
            var rect = Child(parent, name);
            bool created = !rect.TryGetComponent<TextMeshProUGUI>(out var label);
            if (created) label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (label.font == null && TMP_Settings.defaultFontAsset != null) label.font = TMP_Settings.defaultFontAsset;
            float fontSize = Mathf.Max(size, FaaRadarVisualStyle.MinimumFont);
            if (sizeOwnedByRefresh && !created) fontSize = Mathf.Max(label.fontSize, FaaRadarVisualStyle.MinimumFont);
            if (!Mathf.Approximately(label.fontSize, fontSize)) label.fontSize = fontSize;
            if (label.enableAutoSizing) label.enableAutoSizing = false;
            TextWrappingModes wrapping = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            if (label.textWrappingMode != wrapping) label.textWrappingMode = wrapping;
            // Fields are sized from their widest content and footers drop parts by priority: nothing is ever ellipsis-cut.
            if (label.overflowMode != TextOverflowModes.Overflow) label.overflowMode = TextOverflowModes.Overflow;
            if (label.alignment != align) label.alignment = align;
            if (!label.extraPadding) label.extraPadding = true;
            if (label.raycastTarget) label.raycastTarget = false;
            if (created)
            {
                label.fontStyle = style;
                label.color = Text;
                label.faceColor = Color.white;
                label.canvasRenderer.SetColor(Color.white);
            }
            return label;
        }

        private static void Dock(RectTransform rect, Vector2 anchor, Vector2 position, Vector2 size, Vector2? pivot = null, float scale = 1f)
        {
            if (rect.anchorMin != anchor) rect.anchorMin = anchor;
            if (rect.anchorMax != anchor) rect.anchorMax = anchor;
            Vector2 p = pivot ?? new Vector2(.5f, .5f);
            if (rect.pivot != p) rect.pivot = p;
            if (rect.anchoredPosition != position) rect.anchoredPosition = position;
            if (rect.sizeDelta != size) rect.sizeDelta = size;
            Vector3 local = new Vector3(scale, scale, 1f);
            if (rect.localScale != local) rect.localScale = local;
        }
    }
}
