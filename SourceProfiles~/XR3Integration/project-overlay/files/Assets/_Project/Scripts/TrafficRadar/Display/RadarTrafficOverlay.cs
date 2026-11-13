using System;
using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TrafficRadar
{
    /// <summary>
    /// Vector traffic, relative altitude tags, labelled range rings and the own-ship symbol (always the top layer).
    /// The explanation of tags/arrows lives in the settings help, never as a permanent legend on the scope.
    /// </summary>
    [ExecuteAlways, DisallowMultipleComponent, RequireComponent(typeof(CanvasRenderer))]
    public sealed class RadarTrafficOverlay : MaskableGraphic
    {
        public const float TrendThresholdFpm = 500;
        public const float StaleAfterSeconds = 15;
        public const float PositionSettleSeconds = .1f;
        /// <summary>FAA minimum legible size on the radar canvas (1 unit is about 1 reference px at the audited panel scale).</summary>
        public const float MinimumFontSize = 15f;
        /// <summary>TCAS 2 NM reference ring, drawn while the selected range makes it distinguishable.</summary>
        public const float InnerRingNM = 2f;
        [SerializeField] private TrafficRadarDisplay display;
        private sealed class Track
        {
            public RadarTrafficTarget data;
            public Vector2 position, labelOffset;
            public float born, alpha;
            public bool seen, labelVisible;
            public Rect tag;
            public TMP_Text label;
            public int tagKey = int.MaxValue;
            public string tagText = "—";
        }
        private readonly Dictionary<string, Track> tracks = new();
        private readonly List<Track> visible = new();
        private readonly List<Rect> occupied = new();
        private readonly List<string> retired = new();
        private TMP_Text ringHalf, ringOuter;
        private Rect ringHalfRect, ringOuterRect;
        private float ringHalfValue = -1f, ringOuterValue = -1f;
        private bool ringLabels;
        private float radius, iconRadius = 5, symbolScale = 1, textScale = 1, lastRadius = -1;
        private bool draw, animating;
        private int lastSignature;
        private static readonly Color Knockout = new(.005f, .026f, .037f, .90f);
        private static readonly Color RingInk = new(.78f, .96f, .94f, .85f);
        private static readonly Comparison<Track> ThreatThenRange = (a, b) => a.data.threatLevel != b.data.threatLevel
            ? b.data.threatLevel.CompareTo(a.data.threatLevel) : a.data.distanceNM.CompareTo(b.data.distanceNM);

        /// <summary>Number of displayed aircraft whose altitude tag could not be placed without overlap.</summary>
        public int HiddenTagCount { get; private set; }

        /// <summary>Readability multiplier set by the host (world-space panels render one unit smaller than a reference pixel).</summary>
        public float TextScale
        {
            get => textScale;
            set => textScale = Mathf.Clamp(value, 1f, 2f);
        }

        public void Configure(TrafficRadarDisplay owner)
        {
            if (GetComponent<CanvasRenderer>() == null) gameObject.AddComponent<CanvasRenderer>();
            display = owner; raycastTarget = false;
        }

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        public static string FormatRelativeAltitude(float feet)
        {
            if (!Finite(feet)) return "—";
            // Preserve a negative sign even for traffic within 50 ft below us.
            float hundreds = Mathf.Round(Mathf.Abs(feet) / 100);
            return (feet < 0 ? "−" : "+") + hundreds.ToString("00", CultureInfo.InvariantCulture);
        }
        public static int VerticalTrend(float feetPerMinute) => !Finite(feetPerMinute) || Mathf.Abs(feetPerMinute) < TrendThresholdFpm
            ? 0 : feetPerMinute > 0 ? 1 : -1;
        public static bool IsFresh(float sampleAge, float sinceUpdate) => Finite(sampleAge) && Finite(sinceUpdate) &&
            Mathf.Max(0, sampleAge) + Mathf.Max(0, sinceUpdate) <= StaleAfterSeconds;
        public static bool InsideScope(Rect bounds, float scopeRadius)
        {
            float x = Mathf.Max(Mathf.Abs(bounds.xMin), Mathf.Abs(bounds.xMax));
            float y = Mathf.Max(Mathf.Abs(bounds.yMin), Mathf.Abs(bounds.yMax));
            return x * x + y * y <= scopeRadius * scopeRadius;
        }
        public static Vector2 SmoothPosition(Vector2 current, Vector2 next, float deltaTime) =>
            Vector2.Lerp(current, next, 1 - Mathf.Exp(-Mathf.Max(0, deltaTime) / PositionSettleSeconds));

        /// <summary>Tag font: never below the FAA minimum, larger in the full map.</summary>
        public static float TagFontSize(bool fullMap, float scale) => (fullMap ? 17f : MinimumFontSize) * Mathf.Max(1f, scale);

        /// <summary>Fraction of the selected range at the emphasised half-range ring (matches the texture's major ring).</summary>
        public static float HalfRingFraction(int ringCount)
        {
            int count = Mathf.Clamp(ringCount, 1, 8);
            return Mathf.Max(1, Mathf.CeilToInt(count * .5f)) / (float)count;
        }

        /// <summary>The 2 NM ring is shown only while it is a distinct interior reference.</summary>
        public static bool ShowInnerRing(float rangeNM) => rangeNM > InnerRingNM + .5f && rangeNM <= 20.01f;

        private void LateUpdate()
        {
            draw = display != null && display.isActiveAndEnabled && !display.UsesXPlaneTrafficTexture;
            radius = Mathf.Min(rectTransform.rect.width, rectTransform.rect.height) * .5f;
            draw &= radius > 5;
            bool full = display != null && display.IsFullscreen;
            symbolScale = (full ? 1.25f : 1f) * textScale;
            iconRadius = (full ? 6.2f : 4.7f) * textScale;
            visible.Clear(); retired.Clear();
            foreach (var track in tracks.Values) track.seen = false;
            animating = false;
            if (draw && display.DisplayTargets != null)
            {
                for (int i = 0; i < display.DisplayTargets.Count; i++)
                {
                    var data = display.DisplayTargets[i];
                    if (data == null || !Finite(data.radarPosition.x) || !Finite(data.radarPosition.y) ||
                        !IsFresh(data.sampleAgeSeconds, display.SecondsSinceTrafficUpdate)) continue;
                    string id = !string.IsNullOrWhiteSpace(data.icao24) ? data.icao24 :
                        !string.IsNullOrWhiteSpace(data.callsign) ? data.callsign : "slot-" + i;
                    // Same geographic scale as the chart, rings and navigation cue: the outer ring is the selected range.
                    Vector2 point = display.GetTargetDisplayPosition(data) * radius;
                    if (point.sqrMagnitude > (radius - 10) * (radius - 10)) continue;
                    if (!tracks.TryGetValue(id, out var track))
                    {
                        track = new Track { born = Time.unscaledTime, position = point,
                            label = CreateLabel("Altitude " + id), labelOffset = new(0, data.relativeAltitudeFt < 0 ? -22 : 22) };
                        tracks[id] = track;
                    }
                    track.seen = true; track.data = data;
                    // Range/format changes snap to the correct reference. No extrapolated positions.
                    track.position = point;
                    track.alpha = Application.isPlaying ? Mathf.Clamp01((Time.unscaledTime - track.born) / .25f) : 1;
                    if (data.threatLevel >= ThreatLevel.TrafficAdvisory) track.alpha = 1;
                    if (Application.isPlaying && Time.unscaledTime - track.born < .9f) animating = true;
                    visible.Add(track);
                }
            }
            foreach (var pair in tracks)
            {
                if (pair.Value.seen) continue;
                // Removed/stale tracks disappear immediately; never animate a stale target as live.
                if (pair.Value.label != null)
                {
                    pair.Value.label.gameObject.SetActive(false);
                    if (Application.isPlaying) Destroy(pair.Value.label.gameObject); else DestroyImmediate(pair.Value.label.gameObject);
                }
                retired.Add(pair.Key);
            }
            for (int i = 0; i < retired.Count; i++) tracks.Remove(retired[i]);
            visible.Sort(ThreatThenRange);
            PlaceRingLabels(full);
            PlaceLabels(full);
            // Rebuild the mesh only when something visible changed (or a one-shot acquisition fade runs).
            int signature = Signature(full);
            if (animating || signature != lastSignature || !Mathf.Approximately(radius, lastRadius))
            {
                lastSignature = signature; lastRadius = radius;
                SetVerticesDirty();
            }
        }

        private int Signature(bool full)
        {
            unchecked
            {
                int h = draw ? 17 : 3;
                if (!draw) return h;
                h = h * 31 + (full ? 1 : 0);
                h = h * 31 + (display.IsMapDragActive ? 1 : 0);
                h = h * 31 + Mathf.RoundToInt(display.RangeNM * 100f);
                h = h * 31 + Mathf.RoundToInt(display.ReferenceLineworkVisualAlpha * 50f);
                h = h * 31 + Mathf.RoundToInt(textScale * 100f);
                h = h * 31 + (display.ShowAltitudeLabels ? 1 : 0);
                h = h * 31 + display.OwnAircraftColor.GetHashCode();
                h = h * 31 + Mathf.RoundToInt(ringHalfRect.x * 4f) + Mathf.RoundToInt(ringOuterRect.width * 4f) * 13;
                foreach (var track in visible)
                {
                    h = h * 31 + Mathf.RoundToInt(track.position.x * 20f);
                    h = h * 31 + Mathf.RoundToInt(track.position.y * 20f);
                    h = h * 31 + Mathf.RoundToInt(track.alpha * 50f);
                    h = h * 31 + (int)track.data.threatLevel;
                    h = h * 31 + VerticalTrend(track.data.verticalRateFpm);
                    h = h * 31 + (track.labelVisible ? Mathf.RoundToInt(track.tag.x * 4f) * 7 + Mathf.RoundToInt(track.tag.y * 4f) : 0);
                }
                return h;
            }
        }

        private void PlaceRingLabels(bool full)
        {
            if (ringHalf == null) ringHalf = CreateLabel("Ring Half");
            if (ringOuter == null) ringOuter = CreateLabel("Ring Outer");
            ringLabels = draw && display.ReferenceLineworkVisible && display.ReferenceLineworkVisualAlpha > .05f;
            if (!ringLabels) { SetActive(ringHalf, false); SetActive(ringOuter, false); return; }
            float fraction = HalfRingFraction(display.RangeRingCount);
            float fontSize = (full ? 17f : MinimumFontSize) * textScale;
            ringHalfRect = PlaceRingLabel(ringHalf, ref ringHalfValue, display.RangeNM * fraction, radius * fraction, fontSize);
            ringOuterRect = PlaceRingLabel(ringOuter, ref ringOuterValue, display.RangeNM, radius - 12f * textScale, fontSize);
            // Traffic outranks reference labels: a ring label yields when a symbol sits beneath it.
            if (SymbolUnder(ringHalfRect)) ringHalfRect = Rect.zero;
            if (SymbolUnder(ringOuterRect)) ringOuterRect = Rect.zero;
            SetActive(ringHalf, ringHalfRect.width > 0); SetActive(ringOuter, ringOuterRect.width > 0);
        }

        private bool SymbolUnder(Rect rect)
        {
            float pad = iconRadius + 2f;
            Rect padded = new Rect(rect.xMin - pad, rect.yMin - pad, rect.width + pad * 2f, rect.height + pad * 2f);
            foreach (var track in visible) if (padded.Contains(track.position)) return true;
            return false;
        }

        private Rect PlaceRingLabel(TMP_Text label, ref float shown, float valueNM, float ringRadius, float fontSize)
        {
            float rounded = Mathf.Round(valueNM * 10f) / 10f;
            if (!Mathf.Approximately(rounded, shown))
            {
                shown = rounded;
                label.text = rounded.ToString("0.#", CultureInfo.InvariantCulture);
            }
            int digits = rounded >= 10f ? 2 : 1;
            if (!Mathf.Approximately(rounded, Mathf.Round(rounded))) digits += 2;
            Vector2 size = new Vector2(Mathf.Max(24f, digits * fontSize * .62f + 10f), fontSize + 5f);
            // Lower-left (225°): the aft-left sector carries the least traffic interest in track-up.
            Vector2 centre = new Vector2(-.7071f, -.7071f) * ringRadius;
            Rect rect = new Rect(centre - size * .5f, size);
            label.fontStyle = FontStyles.Bold;
            if (!Mathf.Approximately(label.fontSize, fontSize)) label.fontSize = fontSize;
            var tint = new Color(.80f, .96f, .95f, display.ReferenceLineworkVisualAlpha);
            if (label.color != tint) label.color = tint;
            if (label.rectTransform.anchoredPosition != centre) label.rectTransform.anchoredPosition = centre;
            if (label.rectTransform.sizeDelta != size) label.rectTransform.sizeDelta = size;
            return rect;
        }

        private void PlaceLabels(bool full)
        {
            // The permanent on-scope legend is retired; its text lives in the settings help.
            var legacyLegend = transform.Find("Altitude Legend");
            if (legacyLegend != null && legacyLegend.gameObject.activeSelf) legacyLegend.gameObject.SetActive(false);
            bool tags = draw && display.ShowAltitudeLabels && visible.Count > 0;
            occupied.Clear();
            float ownship = 20f * symbolScale;
            occupied.Add(new Rect(-ownship, -ownship, ownship * 2f, ownship * 2f)); // Own-ship must remain unobscured.
            if (ringLabels)
            {
                if (ringHalfRect.width > 0) occupied.Add(ringHalfRect);
                if (ringOuterRect.width > 0) occupied.Add(ringOuterRect);
            }
            float mark = 8f * textScale;
            foreach (var track in visible) occupied.Add(new Rect(track.position - Vector2.one * mark, Vector2.one * mark * 2f));
            int hidden = 0;
            float fontSize = TagFontSize(full, textScale);
            foreach (var track in visible)
            {
                bool validAltitude = Finite(track.data.relativeAltitudeFt) && Finite(track.data.altitudeFt);
                // Rebuild the tag string only when the displayed hundreds actually change.
                int key = !validAltitude ? int.MinValue : (track.data.relativeAltitudeFt < 0 ? -1 : 1) *
                    (Mathf.RoundToInt(Mathf.Abs(track.data.relativeAltitudeFt) / 100f) + 1);
                if (key != track.tagKey)
                {
                    track.tagKey = key;
                    track.tagText = validAltitude ? FormatRelativeAltitude(track.data.relativeAltitudeFt) : "—";
                }
                string text = track.tagText;
                float width = Mathf.Max(fontSize * 3.2f, text.Length * fontSize * .6f + 12f);
                Vector2 size = new(width, fontSize + 7f);
                track.labelVisible = tags && validAltitude && TryPlace(track, size, out track.tag);
                SetActive(track.label, track.labelVisible);
                if (!track.labelVisible) { if (tags && validAltitude) hidden++; continue; }
                occupied.Add(track.tag);
                track.labelOffset = track.tag.center - track.position;
                if (track.label.rectTransform.anchoredPosition != track.tag.center) track.label.rectTransform.anchoredPosition = track.tag.center;
                if (track.label.rectTransform.sizeDelta != size) track.label.rectTransform.sizeDelta = size;
                if (!Mathf.Approximately(track.label.fontSize, fontSize)) track.label.fontSize = fontSize;
                track.label.text = text;
                Color ink = TrafficInk(track);
                if (track.label.color != ink) track.label.color = ink;
            }
            HiddenTagCount = hidden;
        }

        private bool TryPlace(Track track, Vector2 size, out Rect tag)
        {
            float sign = track.data.relativeAltitudeFt < 0 ? -1 : 1;
            // Prefer above/below the symbol. Retain a displaced tag's offset to
            // avoid jitter when nearby aircraft move by fractions of a pixel.
            float maximumOffset = (display.IsFullscreen ? 62 : 46) * textScale;
            float minimumSeparation = iconRadius + size.y * .5f + 6;
            if (track.labelOffset.magnitude <= maximumOffset && Candidate(track.position + track.labelOffset, size, out tag)) return true;
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 7; col++)
                {
                    int step = (col + 1) / 2 * (col % 2 == 0 ? -1 : 1);
                    Vector2 offset = new(step * (size.x + 4) * .65f, sign * (minimumSeparation + row * (size.y + 5)));
                    if (offset.magnitude > maximumOffset) continue;
                    if (Candidate(track.position + offset, size, out tag)) return true;
                }
            tag = default;
            return false;
        }

        private bool Candidate(Vector2 center, Vector2 size, out Rect r)
        {
            r = new Rect(center - size * .5f, size);
            if (!InsideScope(r, radius - 12)) return false;
            Rect padded = new(r.xMin - 2, r.yMin - 2, r.width + 4, r.height + 4);
            foreach (var other in occupied) if (padded.Overlaps(other)) return false;
            return true;
        }

        private static void SetActive(TMP_Text label, bool active)
        {
            if (label != null && label.gameObject.activeSelf != active) label.gameObject.SetActive(active);
        }

        private TMP_Text CreateLabel(string labelName)
        {
            var existing = transform.Find(labelName);
            var go = existing != null ? existing.gameObject : new GameObject(labelName, typeof(RectTransform));
            go.transform.SetParent(transform, false);
            var label = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
            label.font = TMP_Settings.defaultFontAsset; label.raycastTarget = false;
            label.alignment = TextAlignmentOptions.Center; label.textWrappingMode = TextWrappingModes.NoWrap;
            label.fontStyle = FontStyles.Bold; label.overflowMode = TextOverflowModes.Overflow;
            label.enableAutoSizing = false; label.extraPadding = true;
            label.rectTransform.anchorMin = label.rectTransform.anchorMax = label.rectTransform.pivot = Vector2.one * .5f;
            return label;
        }

        private static Color TrafficInk(Track track)
        {
            // Non-advisory traffic is cyan (AC 20-172B); amber/red are reserved for genuine advisories.
            var c = ThreatLevelConfig.GetColor(track.data.threatLevel);
            if (track.data.threatLevel <= ThreatLevel.Proximate) c = new(.34f, .96f, .94f, 1);
            c.a = track.alpha;
            return c;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (!draw) return;
            if (ringLabels)
            {
                Color plate = Knockout; plate.a *= .85f * display.ReferenceLineworkVisualAlpha;
                if (ringHalfRect.width > 0) Box(vh, ringHalfRect, plate);
                if (ringOuterRect.width > 0) Box(vh, ringOuterRect, plate);
                if (ShowInnerRing(display.RangeNM))
                {
                    Color dots = RingInk; dots.a *= display.ReferenceLineworkVisualAlpha;
                    float inner = radius * InnerRingNM / Mathf.Max(1f, display.RangeNM);
                    for (int i = 0; i < 12; i++)
                    {
                        float a = i * 30f * Mathf.Deg2Rad;
                        Disc(vh, new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * inner, 1.6f * textScale, dots);
                    }
                }
            }
            foreach (var track in visible)
            {
                Color c = TrafficInk(track), dark = Knockout; dark.a *= track.alpha;
                if (track.labelVisible)
                {
                    Vector2 end = track.tag.center;
                    if (Vector2.Distance(track.position, end) > 26 * textScale)
                        Stroke(vh, track.position, end, .65f, c * new Color(1, 1, 1, .45f));
                    Box(vh, track.tag, dark);
                }
                SymbolType shape = ThreatLevelConfig.GetSymbolType(track.data.threatLevel);
                DrawSymbol(vh, track.position, shape, iconRadius, c, dark);
                int trend = Finite(track.data.relativeAltitudeFt) ? VerticalTrend(track.data.verticalRateFpm) : 0;
                if (display.ShowAltitudeLabels && trend != 0)
                {
                    float k = textScale;
                    Vector2 a = track.position + new Vector2(iconRadius + 4 * k, -5 * trend * k), b = a + new Vector2(0, 10 * trend * k);
                    Stroke(vh, a, b, 3, dark); Stroke(vh, a, b, 1.3f, c);
                    Stroke(vh, b, b + new Vector2(-2.5f * k, -3 * trend * k), 1.3f, c);
                    Stroke(vh, b, b + new Vector2(2.5f * k, -3 * trend * k), 1.3f, c);
                }
                float age = Time.unscaledTime - track.born;
                if (Application.isPlaying && age < .9f && track.data.threatLevel < ThreatLevel.TrafficAdvisory)
                {
                    // Fixed-size acquisition brackets fade once; no flashing or growing halo.
                    c.a *= .4f * (1 - Mathf.Clamp01(age / .9f));
                    float d = iconRadius + 4;
                    Stroke(vh, track.position + new Vector2(-d, d - 3), track.position + new Vector2(-d, d), .8f, c);
                    Stroke(vh, track.position + new Vector2(-d, d), track.position + new Vector2(-d + 3, d), .8f, c);
                    Stroke(vh, track.position + new Vector2(d, -d + 3), track.position + new Vector2(d, -d), .8f, c);
                    Stroke(vh, track.position + new Vector2(d, -d), track.position + new Vector2(d - 3, -d), .8f, c);
                }
            }
            // Own-ship is the top layer: traffic, tags and leaders can never hide the reference symbol.
            if (!display.IsMapDragActive) DrawOwnship(vh, symbolScale, display.OwnAircraftColor);
        }

        private static void DrawOwnship(VertexHelper vh, float k, Color ink)
        {
            Color dark = new Color(.002f, .020f, .026f, .92f);
            ink.a = 1f;
            for (int pass = 0; pass < 2; pass++)
            {
                Color c = pass == 0 ? dark : ink;
                float w = pass == 0 ? 4.6f : 2.0f;
                Stroke(vh, new Vector2(0, 11) * k, new Vector2(0, -10) * k, w, c);
                Stroke(vh, new Vector2(-12, -2) * k, new Vector2(0, 2) * k, w, c);
                Stroke(vh, new Vector2(0, 2) * k, new Vector2(12, -2) * k, w, c);
                Stroke(vh, new Vector2(-6, -8) * k, new Vector2(0, -6) * k, w, c);
                Stroke(vh, new Vector2(0, -6) * k, new Vector2(6, -8) * k, w, c);
            }
        }

        private static void DrawSymbol(VertexHelper vh, Vector2 p, SymbolType shape, float size, Color ink, Color dark)
        {
            int corners = shape == SymbolType.FilledCircle ? 24 : 4;
            float rotation = shape == SymbolType.FilledSquare ? 45 : 0;
            for (int i = 0; i < corners; i++)
            {
                float a = (rotation + i * 360f / corners) * Mathf.Deg2Rad;
                float b = (rotation + (i + 1) * 360f / corners) * Mathf.Deg2Rad;
                Vector2 v = p + new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * size;
                Vector2 w = p + new Vector2(Mathf.Sin(b), Mathf.Cos(b)) * size;
                if (shape != SymbolType.UnfilledDiamond)
                {
                    int n = vh.currentVertCount;
                    vh.AddVert(p, ink, Vector2.zero); vh.AddVert(v, ink, Vector2.zero); vh.AddVert(w, ink, Vector2.zero);
                    vh.AddTriangle(n, n + 1, n + 2);
                }
                Stroke(vh, v, w, 3.2f, dark); Stroke(vh, v, w, 1.25f, ink);
            }
        }
        private static void Disc(VertexHelper vh, Vector2 p, float r, Color c)
        {
            int n = vh.currentVertCount;
            vh.AddVert(p, c, Vector2.zero);
            for (int i = 0; i <= 10; i++)
            {
                float a = i * 36f * Mathf.Deg2Rad;
                vh.AddVert(p + new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * r, c, Vector2.zero);
                if (i > 0) vh.AddTriangle(n, n + i, n + i + 1);
            }
        }
        private static void Box(VertexHelper vh, Rect r, Color c)
        {
            int n = vh.currentVertCount;
            vh.AddVert(new Vector2(r.xMin, r.yMin), c, Vector2.zero); vh.AddVert(new Vector2(r.xMin, r.yMax), c, Vector2.zero);
            vh.AddVert(new Vector2(r.xMax, r.yMax), c, Vector2.zero); vh.AddVert(new Vector2(r.xMax, r.yMin), c, Vector2.zero);
            vh.AddTriangle(n, n + 1, n + 2); vh.AddTriangle(n, n + 2, n + 3);
        }
        private static void Stroke(VertexHelper vh, Vector2 a, Vector2 b, float width, Color c)
        {
            if ((b - a).sqrMagnitude < .0001f) return;
            Vector2 d = (b - a).normalized, n = new(-d.y, d.x);
            // Coverage fringe around a crisp solid core, independent of texture resolution (no per-call allocation).
            float half = width * .5f;
            int start = vh.currentVertCount;
            for (int j = 0; j < 4; j++)
            {
                float offset = j == 0 ? -half - .35f : j == 1 ? -half : j == 2 ? half : half + .35f;
                Color tint = c; if (j == 0 || j == 3) tint.a = 0;
                vh.AddVert(a + n * offset, tint, Vector2.zero); vh.AddVert(b + n * offset, tint, Vector2.zero);
            }
            for (int j = 0; j < 3; j++)
            { int k = start + j * 2; vh.AddTriangle(k, k + 2, k + 3); vh.AddTriangle(k, k + 3, k + 1); }
        }
    }
}
