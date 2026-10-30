using System.Collections.Generic;
using System.Globalization;
using FAA.XPlaneIntegration.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    public enum FaaEngineInstrument { Torque, EngineSpeed, VerticalSpeed }

    /// <summary>
    /// Digital column instruments, value first and compact: "TQ %" (engine 1 | 2 digits over twin rails), "NR %" (rotor NR as the
    /// headline, engine N2 rails with an NR reference line; N2 digits only when they differ from NR) and "VS FPM".
    /// Each instrument sits on one translucent card and every number on a darker plate with a halo, so captions, ids, numerals and
    /// digits stay legible over haze and terrain and where the horizon crosses the column.
    /// Limit marking comes from <see cref="FaaRotorcraftLimits"/>, whose thresholds are DEMONSTRATOR ASSUMPTIONS for an S-76D-class twin
    /// and NOT Rotorcraft Flight Manual data (<see cref="FaaRotorcraftLimits.Configured"/> is false). TQ rails carry a green normal band,
    /// an amber time-limited band and a haloed red limit line. The NR block uses an expanded 60-120 % scale with one limit strip between
    /// the N2 rails, drawn like the Classic NR dial: red at 91 and 110, amber 91-95 and 107-110, green 95-107 (the high marks are the N2
    /// limits too). Caution = amber digits, bar and box; warning = red digits and bar with a red box that flashes for
    /// <see cref="FaaHudStyle.BlinkSeconds"/> and then stays steady. Beyond the scale the bar pegs with an arrowhead while the digits keep
    /// the true value. Invalid or stale data shows "---" and removes the bar or pointer; nothing is clamped into a plausible value.
    /// While a side panel is inspected the digits, their plates and alert boxes keep the awareness intensity; captions, scales, rails and
    /// the card dim with the forward HUD. In an unusual attitude the VS scale numerals and minor ticks and the N2 split digits declutter.
    /// The mesh is rebuilt only when what is drawn changes.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaEngineInstrumentGraphic : MaskableGraphic
    {
        // Module-local layout (1 unit = 1 reference px at module scale 1).
        public const float RailX = 40f, RailBottom = -80f, RailHeight = 120f, RailTop = RailBottom + RailHeight, ScaleMax = 120f;
        /// <summary>Torque rail units per percent (0..120 % over <see cref="RailHeight"/>).</summary>
        public const float RailK = RailHeight / ScaleMax;
        /// <summary>The NR block rails use an expanded scale (<see cref="RotorScaleMin"/>..120 %) so the rotor limit zones are legible.</summary>
        public const float RotorScaleMin = 60f, RotorRailK = RailHeight / (ScaleMax - RotorScaleMin);
        public const float TitleY = 112f, ValueY = 74f, LabelY = -104f, PegArrow = 8f;
        /// <summary>Limit band lane outboard of each rail, and the red limit line (thickness and reach past the rail and band).</summary>
        public const float BandInner = 10f, BandOuter = 16f, LimitLineWidth = 3.5f, LimitLineReach = 19f, StripHalfWidth = 5f;
        /// <summary>Scale bands are subdued and stand clear of the rail, so with no live value they never read as a filled bar.</summary>
        public const float BandAlpha = .5f;
        public const float VsScaleX = -40f, VsBoxX = -16f, VsBoxWidth = 96f, VsNumeralX = -62f, VsTitleY = 114f, VsTravel = 84f, VsFullScale = 2000f;
        /// <summary>Filled VS pointer pointing at the scale (22 x 18 local, about 22 px tall at the module floor on a 2560-wide view).</summary>
        public const float VsPointerHeight = 22f, VsPointerDepth = 18f;
        /// <summary>N2 digits hide when every N2 is within this band of NR, and appear again beyond <see cref="N2SplitBand"/> (hysteresis).</summary>
        public const float N2MatchBand = .5f, N2SplitBand = 1f;
        /// <summary>First-order settle between data packets (monotonic, no overshoot; within the 30-50 ms budget of AC 25-11B 5.9).</summary>
        public const float PointerTau = .04f;
        private static readonly Vector2 EngineRect = new(176f, 256f), VsRect = new(176f, 260f);
        private static readonly Rect EngineCard = new(-86f, -126f, 172f, 252f), VsCard = new(-86f, -128f, 172f, 256f);
        private static readonly string[] LegacyEngineChildren = { "Units", "Scale 0", "Scale 1", "Scale 2", "Footer" };
        private static readonly string[] LegacyRotorChildren = { "Units", "Scale 0", "Scale 1", "Scale 2", "Footer", "Left Value", "Right Value" };
        private static readonly string[] LegacyVsChildren = { "Units", "Zero", "Direction", "Footer" };

        [SerializeField] private FaaEngineInstrument instrument;
        [SerializeField] private XPlane12ApiHudBridge source;
        [SerializeField] private Graphic[] replacedGraphics;
        private readonly Dictionary<string, TMP_Text> labels = new();
        private float left, right, rotor, displayedLeft, displayedRight, displayedRotor, nextSourceSearch;
        private bool leftValid, rightValid, rotorValid, wasLeftValid, wasRightValid, wasRotorValid, initialized, airborne, legacyHidden;
        private bool leftSplit, rightSplit, unusualAttitude;
        private int engineCount = 2;
        private FaaExceedance leftClass = FaaExceedance.Invalid, rightClass = FaaExceedance.Invalid, rotorClass = FaaExceedance.Invalid;
        private float leftOnset = -100f, rightOnset = -100f, rotorOnset = -100f;
        private Color normal = FaaHudStyle.Green, drawnNormal;
        private float detail = 1f, drawnDetail = -1f;
        private float drawnLeft = float.NaN, drawnRight = float.NaN, drawnRotor = float.NaN;
        private int drawnState = -1;
        private int leftKey = int.MaxValue, rightKey = int.MaxValue, rotorKey = int.MaxValue;
        private string leftText, rightText, rotorText;

        public FaaEngineInstrument Instrument => instrument;
        public FaaExceedance LeftState => leftClass;
        public FaaExceedance RightState => rightClass;
        public FaaExceedance RotorState => rotorClass;
        /// <summary>True while the N2 digits are shown under the NR rails (split from NR, exceedance, or NR unavailable).</summary>
        public bool ShowsN2Digits => instrument == FaaEngineInstrument.EngineSpeed && (LeftDigits || (engineCount > 1 && RightDigits));
        /// <summary>
        /// Unusual-attitude declutter (AC 25-11B 5.10.3.2). Read from <see cref="FaaRotorcraftConformalLayer.UnusualAttitudeActive"/> every
        /// frame in play; settable for tests and previews. Removes the VS scale numerals and minor ticks and the N2 split digits
        /// (exceedance digits and N2 digits standing in for an invalid NR always stay).
        /// </summary>
        public bool UnusualAttitudeDeclutter { get => unusualAttitude; set => unusualAttitude = value; }

        private bool LeftDigits => ShowDigits(leftSplit, leftClass);
        private bool RightDigits => ShowDigits(rightSplit, rightClass);
        private bool ShowDigits(bool split, FaaExceedance state) => split &&
            (!unusualAttitude || state == FaaExceedance.Caution || state == FaaExceedance.Warning || !rotorValid);

        public void Configure(FaaEngineInstrument kind, XPlane12ApiHudBridge bridge, Graphic[] legacy)
        {
            if (GetComponent<CanvasRenderer>() == null) gameObject.AddComponent<CanvasRenderer>();
            if (kind != instrument) { legacyHidden = false; labels.Clear(); leftKey = rightKey = rotorKey = int.MaxValue; leftText = rightText = rotorText = null; }
            instrument = kind; source = bridge; replacedGraphics = legacy; raycastTarget = false;
            RefreshPresentation();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            legacyHidden = false; drawnState = -1;
        }

        private void LateUpdate() => RefreshPresentation();

        public static bool IsUsable(bool feedHealthy, bool fieldValid, float value) =>
            feedHealthy && fieldValid && !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Integer percent (rotorcraft EICAS resolution, no leading zeros), or the column-wide invalid glyph. Missing data never becomes 0.</summary>
        public static string FormatPercent(float value, bool valid) => valid && Finite(value)
            ? Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture) : FaaDigitalColumnStyle.Invalid;

        /// <summary>
        /// The one VS format for both symbology styles: 50 fpm resolution (no flickering last digit), "+" for climb, true minus sign
        /// (U+2212) for descent, unsigned zero; the digits are never clamped. Caption <see cref="FaaDigitalColumnStyle.VerticalSpeedCaption"/>.
        /// </summary>
        public static string FormatVerticalSpeed(float value, bool valid) =>
            !valid || !Finite(value) ? FaaDigitalColumnStyle.Invalid : FormatVerticalSpeedKey(RoundVerticalSpeed(value));

        public static int RoundVerticalSpeed(float value) => Mathf.RoundToInt(value / 50f) * 50;

        private static string FormatVerticalSpeedKey(int rounded) => rounded == 0 ? "0" :
            (rounded > 0 ? "+" : "−") + Mathf.Abs(rounded).ToString(CultureInfo.InvariantCulture);

        public static float VerticalSpeedPosition(float value) => Mathf.Clamp(value / VsFullScale, -1, 1) * VsTravel;

        /// <summary>Torque rail height for a percentage; 0..120 % maps to <see cref="RailBottom"/>..<see cref="RailTop"/>, values above peg at the top.</summary>
        public static float RailY(float percent) => RailBottom + Mathf.Clamp(Finite(percent) ? percent : 0f, 0f, ScaleMax) * RailK;

        /// <summary>NR block rail height: <see cref="RotorScaleMin"/>..120 % maps to the full rail; values outside peg at the ends.</summary>
        public static float RotorRailY(float percent) =>
            RailBottom + Mathf.Clamp((Finite(percent) ? percent : RotorScaleMin) - RotorScaleMin, 0f, ScaleMax - RotorScaleMin) * RotorRailK;

        /// <summary>
        /// Whether an engine's N2 digits are shown in the NR block. NR is the headline; N2 is shown compactly (rails only) while it
        /// matches NR, and as digits when it splits, exceeds a limit or NR itself is unavailable. Hysteresis prevents flicker.
        /// </summary>
        public static bool ShowN2Value(float n2, bool n2Valid, float nr, bool nrValid, bool wasShown, FaaExceedance n2State)
        {
            if (!n2Valid || !Finite(n2)) return false;
            if (n2State == FaaExceedance.Caution || n2State == FaaExceedance.Warning || !nrValid || !Finite(nr)) return true;
            float split = Mathf.Abs(n2 - nr);
            return wasShown ? split > N2MatchBand : split > N2SplitBand;
        }

        public void RefreshPresentation()
        {
            if (replacedGraphics != null)
                foreach (var old in replacedGraphics) if (old != null && old.enabled) old.enabled = false;
            if (Application.isPlaying && source == null && Time.unscaledTime >= nextSourceSearch)
            {
                nextSourceSearch = Time.unscaledTime + 1f;
                source = FindAnyObjectByType<XPlane12ApiHudBridge>();
            }
            if (Application.isPlaying) unusualAttitude = FaaRotorcraftConformalLayer.UnusualAttitudeActive;
            var data = source != null ? source.LatestFlightData : null;
            bool healthy = Application.isPlaying && source != null && source.IsFeedHealthy && data != null;
            int engines = data != null ? Mathf.Clamp(data.engineCount, 1, 2) : 2;
            bool torque = instrument == FaaEngineInstrument.Torque, vs = instrument == FaaEngineInstrument.VerticalSpeed;
            float l = data == null ? 0 : torque ? data.engine1Torque : vs ? data.verticalSpeed : data.engine1NR;
            float r = data == null || vs ? 0 : torque ? data.engine2Torque : data.engine2NR;
            float nr = data != null ? data.rotorNR : 0;
            bool lv = IsUsable(healthy, data != null && (vs || (torque ? data.engine1TorqueValid : data.engine1NRValid)), l);
            bool rv = !vs && engines > 1 && IsUsable(healthy, data != null && (torque ? data.engine2TorqueValid : data.engine2NRValid), r);
            bool nv = IsUsable(healthy, data != null && data.rotorNRValid, nr);
            bool air = data != null && FaaRotorcraftLimits.LikelyAirborne(data.indicatedAirspeed, data.altitudeAGL, data.altitudeAGLValid);
            ApplySample(l, lv, r, rv, nr, nv, engines, air);
        }

        /// <summary>
        /// Presents one sample (also the test hook). <paramref name="leftValue"/> is engine 1 torque/N2 or the vertical speed,
        /// <paramref name="rightValue"/> engine 2, <paramref name="rotorValue"/> rotor NR (NR block only).
        /// </summary>
        public void ApplySample(float leftValue, bool leftOk, float rightValue, bool rightOk, float rotorValue, bool rotorOk, int engines, bool isAirborne)
        {
            engineCount = Mathf.Clamp(engines, 1, 2);
            left = leftValue; right = rightValue; rotor = rotorValue; airborne = isAirborne;
            leftValid = leftOk && Finite(left);
            rightValid = instrument != FaaEngineInstrument.VerticalSpeed && engineCount > 1 && rightOk && Finite(right);
            rotorValid = instrument == FaaEngineInstrument.EngineSpeed && rotorOk && Finite(rotor);
            float now = Time.unscaledTime;
            leftClass = Track(Classify(left, leftValid), leftClass, ref leftOnset, now);
            rightClass = Track(Classify(right, rightValid), rightClass, ref rightOnset, now);
            rotorClass = Track(rotorValid ? FaaRotorcraftLimits.RotorNr(rotor, true, airborne) : FaaExceedance.Invalid, rotorClass, ref rotorOnset, now);
            // Implausible values are invalid data, not exceedances (FaaRotorcraftLimits.*ImplausibleAbove).
            leftValid &= leftClass != FaaExceedance.Invalid; rightValid &= rightClass != FaaExceedance.Invalid; rotorValid &= rotorClass != FaaExceedance.Invalid;
            if (instrument == FaaEngineInstrument.EngineSpeed)
            {
                leftSplit = ShowN2Value(left, leftValid, rotor, rotorValid, leftSplit, leftClass);
                rightSplit = ShowN2Value(right, rightValid, rotor, rotorValid, rightSplit, rightClass);
            }
            else leftSplit = rightSplit = false;
            float blend = !initialized || !Application.isPlaying ? 1 : 1 - Mathf.Exp(-Time.unscaledDeltaTime / PointerTau);
            // A returning signal starts at its true value; it never rises from a fabricated zero.
            displayedLeft = !leftValid ? 0 : wasLeftValid ? Mathf.Lerp(displayedLeft, left, blend) : left;
            displayedRight = !rightValid ? 0 : wasRightValid ? Mathf.Lerp(displayedRight, right, blend) : right;
            displayedRotor = !rotorValid ? 0 : wasRotorValid ? Mathf.Lerp(displayedRotor, rotor, blend) : rotor;
            wasLeftValid = leftValid; wasRightValid = rightValid; wasRotorValid = rotorValid; initialized = true;
            normal = FaaDigitalColumnStyle.Normal;
            EnsureRect();
            if (!legacyHidden) HideLegacyChildren();
            // Awareness policy: digits, their plates and alert boxes keep max(forward, AwarenessHudIntensity); detail (captions, scales,
            // rails, card) follows the forward intensity. The mesh renderer carries the awareness alpha; detail vertices carry the ratio.
            float forward = FaaDigitalColumnStyle.DetailIntensity(transform);
            float awareness = FaaDigitalColumnStyle.AwarenessIntensity(forward);
            detail = awareness > .001f ? Mathf.Clamp01(forward / awareness) : 0f;
            if (instrument == FaaEngineInstrument.VerticalSpeed) RefreshVerticalLabels(forward, awareness);
            else RefreshEngineLabels(forward, awareness);
            FaaDigitalColumnStyle.SetRendererAlpha(canvasRenderer, awareness);
            DirtyIfChanged();
        }

        private FaaExceedance Classify(float value, bool valid)
        {
            if (!valid) return FaaExceedance.Invalid;
            switch (instrument)
            {
                case FaaEngineInstrument.Torque: return FaaRotorcraftLimits.Torque(value, true);
                case FaaEngineInstrument.EngineSpeed: return FaaRotorcraftLimits.EngineN2(value, true);
                default: return FaaExceedance.Normal;
            }
        }

        private static FaaExceedance Track(FaaExceedance next, FaaExceedance previous, ref float onset, float now)
        {
            if (next == FaaExceedance.Warning && previous != FaaExceedance.Warning) onset = now;
            return next;
        }

        private static bool BoxOn(FaaExceedance state, float onset) => state == FaaExceedance.Caution ||
            state == FaaExceedance.Warning && FaaHudStyle.BlinkVisible(onset);

        private void DirtyIfChanged()
        {
            bool vs = instrument == FaaEngineInstrument.VerticalSpeed;
            int state = (leftValid ? 1 : 0) | (rightValid ? 2 : 0) | (rotorValid ? 4 : 0) | (engineCount > 1 ? 8 : 0) |
                ((int)leftClass << 4) | ((int)rightClass << 6) | ((int)rotorClass << 8) |
                (BoxOn(leftClass, leftOnset) ? 1 << 10 : 0) | (BoxOn(rightClass, rightOnset) ? 1 << 11 : 0) |
                (BoxOn(rotorClass, rotorOnset) ? 1 << 12 : 0) | (LeftDigits ? 1 << 13 : 0) | (RightDigits ? 1 << 14 : 0) |
                (unusualAttitude ? 1 << 15 : 0);
            float lMax = vs ? VsFullScale + 1 : ScaleMax + 1, lMin = vs ? -lMax : -1;
            float l = Mathf.Clamp(displayedLeft, lMin, lMax), r = Mathf.Clamp(displayedRight, -1, ScaleMax + 1),
                n = Mathf.Clamp(displayedRotor, -1, ScaleMax + 1);
            float tolerance = vs ? 5f : .05f; // 5 fpm is 0.2 units of pointer travel
            // Detail alpha only changes during the 0.2 s inspection fade; it is quantised so a steady view never rebuilds.
            if (state == drawnState && normal == drawnNormal && Mathf.Abs(detail - drawnDetail) < .02f &&
                Near(l, drawnLeft, tolerance) && Near(r, drawnRight, .05f) && Near(n, drawnRotor, .05f)) return;
            drawnState = state; drawnNormal = normal; drawnDetail = detail; drawnLeft = l; drawnRight = r; drawnRotor = n;
            SetVerticesDirty();
        }

        private static bool Near(float a, float b, float tolerance) => !float.IsNaN(b) && Mathf.Abs(a - b) <= tolerance;
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private void EnsureRect()
        {
            Vector2 size = instrument == FaaEngineInstrument.VerticalSpeed ? VsRect : EngineRect;
            if (rectTransform.sizeDelta != size) rectTransform.sizeDelta = size;
        }

        private void HideLegacyChildren()
        {
            // These TMP children are serialized in the scene from the previous layout and would otherwise keep stale text.
            var names = instrument == FaaEngineInstrument.VerticalSpeed ? LegacyVsChildren :
                instrument == FaaEngineInstrument.EngineSpeed ? LegacyRotorChildren : LegacyEngineChildren;
            foreach (string key in names)
            {
                var found = transform.Find(key);
                if (found != null && found.gameObject.activeSelf) found.gameObject.SetActive(false);
                labels.Remove(key);
            }
            legacyHidden = true;
        }

        private static string Cached(ref int key, ref string text, float value, bool valid)
        {
            int next = valid ? Mathf.RoundToInt(value) : int.MinValue;
            if (next != key || text == null) { key = next; text = valid ? next.ToString(CultureInfo.InvariantCulture) : FaaDigitalColumnStyle.Invalid; }
            return text;
        }

        private void RefreshEngineLabels(float forward, float awareness)
        {
            bool twin = engineCount > 1, torque = instrument == FaaEngineInstrument.Torque;
            float x = twin ? RailX : 0f, size = FaaDigitalColumnStyle.LabelSize;
            Color title = FaaHudStyle.WithAlpha(normal, FaaDigitalColumnStyle.TitleAlpha), quiet = FaaHudStyle.WithAlpha(normal, FaaDigitalColumnStyle.LabelAlpha);
            Label("Title", torque ? FaaDigitalColumnStyle.TorqueCaption : FaaDigitalColumnStyle.RotorCaption, new(0, TitleY), new(140, 28), size, title, forward);
            if (torque)
            {
                Label("Left Value", Cached(ref leftKey, ref leftText, left, leftValid), new(-x, ValueY), new(68, 36),
                    FaaDigitalColumnStyle.ValueSize, FaaDigitalColumnStyle.ValueColor(leftClass, normal), awareness);
                Label("Right Value", twin ? Cached(ref rightKey, ref rightText, right, rightValid) : "", new(x, ValueY), new(68, 36),
                    FaaDigitalColumnStyle.ValueSize, FaaDigitalColumnStyle.ValueColor(rightClass, normal), awareness);
                Label("Left", twin ? "1" : "", new(-x, LabelY), new(36, 28), size, quiet, forward);
                Label("Right", twin ? "2" : "", new(x, LabelY), new(36, 28), size, quiet, forward);
                return;
            }
            Label("Center Value", Cached(ref rotorKey, ref rotorText, rotor, rotorValid), new(0, ValueY), new(96, 40),
                FaaDigitalColumnStyle.HeadlineSize, FaaDigitalColumnStyle.ValueColor(rotorClass, normal), awareness);
            bool leftDigits = LeftDigits, rightDigits = RightDigits;
            Label("Left", N2Text(leftDigits, leftValid, ref leftKey, ref leftText, left, twin ? "1" : "N2"), new(-x, LabelY), new(36, 28),
                size, N2Color(leftDigits, leftValid, leftClass, quiet), leftDigits ? awareness : forward);
            Label("Right", twin ? N2Text(rightDigits, rightValid, ref rightKey, ref rightText, right, "2") : "", new(x, LabelY), new(36, 28),
                size, N2Color(rightDigits, rightValid, rightClass, quiet), rightDigits ? awareness : forward);
            Label("Caption", twin ? "N2" : "", new(0, LabelY), new(36, 28), size, quiet, forward);
        }

        private string N2Text(bool shown, bool valid, ref int key, ref string text, float value, string id)
        {
            if (shown) return Cached(ref key, ref text, value, true);
            // A failed N2 is flagged where its digits would be, unless the whole engine-speed picture is already flagged by NR "---".
            return !valid && rotorValid ? FaaDigitalColumnStyle.Invalid : id;
        }

        private Color N2Color(bool shown, bool valid, FaaExceedance state, Color quiet) => shown
            ? FaaDigitalColumnStyle.ValueColor(state, normal) : !valid && rotorValid ? FaaHudStyle.WithAlpha(normal, FaaHudStyle.MinTextAlpha) : quiet;

        private void RefreshVerticalLabels(float forward, float awareness)
        {
            Color title = FaaHudStyle.WithAlpha(normal, FaaDigitalColumnStyle.TitleAlpha), quiet = FaaHudStyle.WithAlpha(normal, FaaDigitalColumnStyle.LabelAlpha);
            float size = FaaDigitalColumnStyle.LabelSize;
            Label("Title", FaaDigitalColumnStyle.VerticalSpeedCaption, new(0, VsTitleY), new(150, 28), size, title, forward);
            // Standard PFD VSI numerals in thousands of feet per minute; the pointer and the value sign give direction.
            // Unusual attitude: the scale numerals declutter, the pointer and the digits stay.
            bool numerals = !unusualAttitude;
            Label("Upper", numerals ? "2" : "", new(VsNumeralX, VsTravel), new(28, 28), size, quiet, forward);
            Label("Upper Mid", numerals ? "1" : "", new(VsNumeralX, VsTravel * .5f), new(28, 28), size, quiet, forward);
            Label("Lower Mid", numerals ? "1" : "", new(VsNumeralX, -VsTravel * .5f), new(28, 28), size, quiet, forward);
            Label("Lower", numerals ? "2" : "", new(VsNumeralX, -VsTravel), new(28, 28), size, quiet, forward);
            int key = leftValid ? RoundVerticalSpeed(left) : int.MinValue;
            if (key != leftKey || leftText == null) { leftKey = key; leftText = leftValid ? FormatVerticalSpeedKey(key) : FaaDigitalColumnStyle.Invalid; }
            Label("Value", leftText, new(VsBoxX + VsBoxWidth * .5f, 0), new(VsBoxWidth - 4f, 36), FaaDigitalColumnStyle.VsValueSize,
                FaaDigitalColumnStyle.ValueColor(leftValid ? FaaExceedance.Normal : FaaExceedance.Invalid, normal), awareness);
        }

        private void Label(string key, string value, Vector2 position, Vector2 size, float fontSize, Color tint, float alpha)
        {
            if (!labels.TryGetValue(key, out var label) || label == null)
            {
                var found = transform.Find(key);
                var go = found != null ? found.gameObject : new GameObject(key, typeof(RectTransform));
                // Labels added by the edit-mode preview are rebuilt on demand and never written into the scene file.
                if (found == null && !Application.isPlaying) go.hideFlags = HideFlags.DontSave;
                go.transform.SetParent(transform, false);
                if (!go.activeSelf) go.SetActive(true);
                label = go.GetComponent<TextMeshProUGUI>();
                if (label == null) label = go.AddComponent<TextMeshProUGUI>();
                if (label.font == null) label.font = TMP_Settings.defaultFontAsset;
                label.raycastTarget = false; label.enableAutoSizing = false; label.richText = false;
                label.alignment = TextAlignmentOptions.Center; label.textWrappingMode = TextWrappingModes.NoWrap;
                label.overflowMode = TextOverflowModes.Overflow; label.characterSpacing = 0f;
                var created = label.rectTransform;
                created.anchorMin = created.anchorMax = created.pivot = Vector2.one * .5f; created.localScale = Vector3.one;
                labels[key] = label;
            }
            var rt = label.rectTransform;
            if (rt.anchoredPosition != position) rt.anchoredPosition = position;
            if (rt.sizeDelta != size) rt.sizeDelta = size;
            if (!Mathf.Approximately(label.fontSize, fontSize)) label.fontSize = fontSize;
            if (label.color != tint) label.color = tint;
            if (label.text != value) label.text = value;
            // The halo material also replaces the per-text material instance the colour manager creates, so a cached
            // edit-preview alpha is never multiplied into a now-valid live reading.
            var halo = FaaHudStyle.HaloMaterial(label.font, FaaDigitalColumnStyle.HaloStrength);
            if (halo != null && label.fontSharedMaterial != halo) { label.fontSharedMaterial = halo; label.UpdateMeshPadding(); }
            FaaDigitalColumnStyle.SetRendererAlpha(label.canvasRenderer, alpha);
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            float d = detail;
            Color line = FaaHudStyle.WithAlpha(normal, FaaDigitalColumnStyle.LineAlpha * d), ink = FaaHudStyle.WithAlpha(normal, d);
            if (instrument == FaaEngineInstrument.VerticalSpeed) { PopulateVerticalSpeed(vh, d, line, ink); return; }
            bool torque = instrument == FaaEngineInstrument.Torque, twin = engineCount > 1;
            float x = twin ? RailX : 0f;
            // One card per instrument: the title, rails and ids keep contrast over haze and terrain (detail intensity).
            Quad(vh, EngineCard, FaaHudStyle.Dim(FaaDigitalColumnStyle.Card, d));
            // Twin rails carry their bands outboard; a single rail carries them on the side away from the NR strip.
            float singleOuter = torque ? 1f : -1f;
            Rail(vh, -x, displayedLeft, leftValid, leftClass, !torque, twin ? -1f : singleOuter, d, line);
            if (twin) Rail(vh, x, displayedRight, rightValid, rightClass, !torque, 1f, d, line);
            if (torque)
            {
                Plate(vh, new Rect(-x - 34, ValueY - 19, 68, 38), leftClass, leftOnset);
                if (twin) Plate(vh, new Rect(x - 34, ValueY - 19, 68, 38), rightClass, rightOnset);
                return;
            }
            float strip = twin ? 0f : 22f;
            RotorStrip(vh, strip, d);
            if (rotorValid)
            {
                // NR reference across the N2 rails and the limit strip: the NR pointer, and a needle split shows graphically too.
                float y = RotorRailY(displayedRotor), x0 = twin ? -x - 14f : -14f, x1 = twin ? x + 14f : strip + 12f;
                Color c = FaaDigitalColumnStyle.ValueColor(rotorClass, normal);
                Line(vh, new(x0, y), new(x1, y), 5.5f, FaaHudStyle.Dim(FaaDigitalColumnStyle.Halo, d));
                Line(vh, new(x0, y), new(x1, y), 2.5f, FaaHudStyle.Dim(c, d));
                Peg(vh, strip, displayedRotor, FaaHudStyle.Dim(c, d));
            }
            Plate(vh, new Rect(-46, ValueY - 21, 92, 42), rotorClass, rotorOnset);
            if (LeftDigits) Plate(vh, new Rect(-x - 22, LabelY - 14, 44, 28), leftClass, leftOnset);
            if (twin && RightDigits) Plate(vh, new Rect(x - 22, LabelY - 14, 44, 28), rightClass, rightOnset);
        }

        private void PopulateVerticalSpeed(VertexHelper vh, float d, Color line, Color ink)
        {
            Quad(vh, VsCard, FaaHudStyle.Dim(FaaDigitalColumnStyle.Card, d));
            Line(vh, new(VsScaleX, -VsTravel), new(VsScaleX, VsTravel), 1.5f, line);
            for (int n = -4; n <= 4; n++)
            {
                if (unusualAttitude && n != 0) continue; // unusual attitude: only the zero reference stays
                Line(vh, new(VsScaleX, n * VsTravel * .25f), new(VsScaleX + (n == 0 ? 12 : n % 2 == 0 ? 9 : 5), n * VsTravel * .25f),
                    n == 0 ? 2.5f : 1.5f, n == 0 ? ink : line);
            }
            Quad(vh, new Rect(VsBoxX, -19, VsBoxWidth, 38), FaaDigitalColumnStyle.Plate);
            if (!leftValid) return; // invalid: the pointer is removed and the digits read "---"
            float y = VerticalSpeedPosition(displayedLeft), h = VsPointerHeight * .5f, tipX = VsScaleX + 1.5f, baseX = tipX + VsPointerDepth;
            Line(vh, new(VsScaleX, 0), new(VsScaleX, y), 3f, ink);
            // Filled pointer on a dark halo, pointing at the scale.
            Tri(vh, new(tipX - 2.5f, y), new(baseX + 2f, y + h + 2.5f), new(baseX + 2f, y - h - 2.5f), FaaHudStyle.Dim(FaaDigitalColumnStyle.Halo, d));
            Tri(vh, new(tipX, y), new(baseX, y + h), new(baseX, y - h), ink);
            // Pegged: arrowhead beyond the scale end in the direction of travel; the digits keep the true value.
            if (Mathf.Abs(displayedLeft) > VsFullScale)
            {
                float s = Mathf.Sign(displayedLeft);
                Tri(vh, new(VsScaleX - 6, s * (VsTravel + 2)), new(VsScaleX, s * (VsTravel + 3 + PegArrow)), new(VsScaleX + 6, s * (VsTravel + 2)), ink);
            }
        }

        private void Rail(VertexHelper vh, float x, float value, bool valid, FaaExceedance state, bool rotorScale, float outer, float d, Color line)
        {
            Quad(vh, new Rect(x - 6, RailBottom, 12, RailHeight), FaaHudStyle.Dim(FaaDigitalColumnStyle.Plate, d));
            Line(vh, new(x - 6, RailBottom), new(x - 6, RailTop), 1.25f, line);
            Line(vh, new(x + 6, RailBottom), new(x + 6, RailTop), 1.25f, line);
            Line(vh, new(x - 9, RailBottom), new(x + 9, RailBottom), 1.25f, line);
            Line(vh, new(x - 9, Y(rotorScale, rotorScale ? 80f : 50f)), new(x + 9, Y(rotorScale, rotorScale ? 80f : 50f)), 1.25f, line);
            Line(vh, new(x - 9, Y(rotorScale, 100f)), new(x + 9, Y(rotorScale, 100f)), 1.25f, line);
            float warning;
            if (rotorScale) warning = FaaRotorcraftLimits.N2WarningAbove; // N2 bands are carried by the shared limit strip
            else
            {
                // DEMONSTRATOR torque marking (not RFM data): green normal band, amber time-limited band, red limit line.
                warning = FaaRotorcraftLimits.TorqueWarningAbove;
                float inner = x + outer * BandInner, far = x + outer * BandOuter;
                Band(vh, inner, far, RailY(0f), RailY(FaaRotorcraftLimits.TorqueCautionAbove), FaaHudStyle.Dim(FaaHudStyle.Green, d * BandAlpha));
                Band(vh, inner, far, RailY(FaaRotorcraftLimits.TorqueCautionAbove), RailY(warning), FaaHudStyle.Dim(FaaHudStyle.Amber, d * BandAlpha));
            }
            LimitLine(vh, x - outer * 9f, x + outer * LimitLineReach, Y(rotorScale, warning), d);
            if (!valid) return; // invalid: the bar is removed and the digits read "---"
            Color c = FaaHudStyle.Dim(FaaDigitalColumnStyle.ValueColor(state, normal), d);
            float y = Y(rotorScale, value);
            Quad(vh, new Rect(x - 3f, RailBottom, 6f, Mathf.Max(1f, y - RailBottom)), FaaHudStyle.Dim(c, FaaDigitalColumnStyle.FillAlpha));
            Line(vh, new(x - 10, y), new(x + 10, y), 2.5f, c);
            Peg(vh, x, rotorScale ? value - RotorScaleMin : value, c, rotorScale ? ScaleMax - RotorScaleMin : ScaleMax);
        }

        private static float Y(bool rotorScale, float percent) => rotorScale ? RotorRailY(percent) : RailY(percent);

        /// <summary>Arrowhead above the rail when a value is beyond the scale top, below it when under the scale bottom.</summary>
        private static void Peg(VertexHelper vh, float x, float offset, Color c, float span)
        {
            if (offset > span) Tri(vh, new(x - 6, RailTop + 1), new(x, RailTop + PegArrow), new(x + 6, RailTop + 1), c);
            else if (offset < 0f) Tri(vh, new(x - 6, RailBottom - 1), new(x, RailBottom - PegArrow + 1), new(x + 6, RailBottom - 1), c);
        }

        private static void Peg(VertexHelper vh, float x, float nr, Color c) => Peg(vh, x, nr - RotorScaleMin, c, ScaleMax - RotorScaleMin);

        /// <summary>Fixed NR limit strip on the expanded scale, matching the Classic NR dial (DEMONSTRATOR values, not RFM data).</summary>
        private static void RotorStrip(VertexHelper vh, float x, float d)
        {
            float w = StripHalfWidth;
            Band(vh, x - w, x + w, RotorRailY(FaaRotorcraftLimits.NrWarningBelow), RotorRailY(FaaRotorcraftLimits.NrCautionBelow), FaaHudStyle.Dim(FaaHudStyle.Amber, d * BandAlpha));
            Band(vh, x - w, x + w, RotorRailY(FaaRotorcraftLimits.NrCautionBelow), RotorRailY(FaaRotorcraftLimits.NrCautionAbove), FaaHudStyle.Dim(FaaHudStyle.Green, d * BandAlpha));
            Band(vh, x - w, x + w, RotorRailY(FaaRotorcraftLimits.NrCautionAbove), RotorRailY(FaaRotorcraftLimits.NrWarningAbove), FaaHudStyle.Dim(FaaHudStyle.Amber, d * BandAlpha));
            LimitLine(vh, x - 12f, x + 12f, RotorRailY(FaaRotorcraftLimits.NrWarningBelow), d);
            LimitLine(vh, x - 12f, x + 12f, RotorRailY(FaaRotorcraftLimits.NrWarningAbove), d);
        }

        private static void Band(VertexHelper vh, float x0, float x1, float y0, float y1, Color c) =>
            Quad(vh, Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(y0, y1), Mathf.Max(x0, x1), Mathf.Max(y0, y1)), c);

        /// <summary>Red limit line on a dark halo, extended past the rail.</summary>
        private static void LimitLine(VertexHelper vh, float x0, float x1, float y, float d)
        {
            float a = Mathf.Min(x0, x1), b = Mathf.Max(x0, x1);
            Line(vh, new(a - 1.5f, y), new(b + 1.5f, y), LimitLineWidth + 3f, FaaHudStyle.Dim(FaaDigitalColumnStyle.Halo, d));
            Line(vh, new(a, y), new(b, y), LimitLineWidth, FaaHudStyle.Dim(FaaHudStyle.Red, d));
        }

        private static void Plate(VertexHelper vh, Rect r, FaaExceedance state, float onset)
        {
            Quad(vh, r, FaaDigitalColumnStyle.Plate);
            // Redundant, non-colour coding: caution boxed, warning boxed heavier and flashing, then steady.
            if (state == FaaExceedance.Caution) Box(vh, Expand(r, 2f), 1.5f, FaaHudStyle.Amber);
            else if (state == FaaExceedance.Warning && FaaHudStyle.BlinkVisible(onset)) Box(vh, Expand(r, 2f), 2.5f, FaaHudStyle.Red);
        }

        private static Rect Expand(Rect r, float d) => Rect.MinMaxRect(r.xMin - d, r.yMin - d, r.xMax + d, r.yMax + d);

        private static void Box(VertexHelper vh, Rect r, float width, Color c)
        {
            float h = width * .5f;
            Line(vh, new(r.xMin - h, r.yMin), new(r.xMax + h, r.yMin), width, c);
            Line(vh, new(r.xMin - h, r.yMax), new(r.xMax + h, r.yMax), width, c);
            Line(vh, new(r.xMin, r.yMin), new(r.xMin, r.yMax), width, c);
            Line(vh, new(r.xMax, r.yMin), new(r.xMax, r.yMax), width, c);
        }

        private static void Quad(VertexHelper vh, Rect r, Color c)
        {
            int i = vh.currentVertCount;
            vh.AddVert(new Vector2(r.xMin, r.yMin), c, Vector2.zero); vh.AddVert(new Vector2(r.xMin, r.yMax), c, Vector2.zero);
            vh.AddVert(new Vector2(r.xMax, r.yMax), c, Vector2.zero); vh.AddVert(new Vector2(r.xMax, r.yMin), c, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
        }

        private static void Tri(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int i = vh.currentVertCount;
            vh.AddVert(a, color, Vector2.zero); vh.AddVert(b, color, Vector2.zero); vh.AddVert(c, color, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2);
        }

        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color c)
        {
            if ((a - b).sqrMagnitude < .0001f) return;
            Vector2 d = (b - a).normalized, n = new Vector2(-d.y, d.x) * width * .5f;
            int i = vh.currentVertCount;
            vh.AddVert(a - n, c, Vector2.zero); vh.AddVert(a + n, c, Vector2.zero);
            vh.AddVert(b + n, c, Vector2.zero); vh.AddVert(b - n, c, Vector2.zero);
            vh.AddTriangle(i, i + 1, i + 2); vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
