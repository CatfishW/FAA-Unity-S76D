using System.Globalization;
using FAA.Customization;
using IndicatorSystem.Core;
using UnityEngine;
using UnityEngine.UI;

namespace IndicatorSystem.Display
{
    /// <summary>Quiet, scale-independent screen cues. These are simulator aids, not TCAS/RA guidance.</summary>
    public sealed class PilotIndicatorCue : MonoBehaviour
    {
        private PilotCueSymbol _symbol;
        private TrafficAircraftSymbolGraphic _aircraft;
        private int _verticalStateKey = int.MinValue;
        private RectTransform _plate;
        private Text _title, _detail, _state;
        // Legibility floor (FaaHudStyle.MinLabel = 15 ref, ~16 arcmin): title 16, detail/state 15.
        private const float PlateWidth = 262f, PlateHeight = 70f;
        // Last rendered inputs: Present runs every frame per cue, so text is rebuilt only when what it shows changes.
        private int _titleKey = int.MinValue, _detailA = int.MinValue, _detailB = int.MinValue, _stateKey = -1;
        private string _titleLabel;
        private Color _titleTint;

        public static PilotIndicatorCue Create(Transform parent)
        {
            var root = new GameObject("Pilot cue", typeof(RectTransform));
            root.transform.SetParent(parent, false); root.layer = parent.gameObject.layer;
            var cue = root.AddComponent<PilotIndicatorCue>();
            var symbol = new GameObject("Vector symbol", typeof(RectTransform), typeof(PilotCueSymbol));
            symbol.transform.SetParent(root.transform, false); symbol.layer = root.layer;
            cue._symbol = symbol.GetComponent<PilotCueSymbol>();
            cue._symbol.rectTransform.sizeDelta = new Vector2(42, 42);
            cue._symbol.raycastTarget = false;
            var aircraft = new GameObject("Reference aircraft symbol", typeof(RectTransform), typeof(TrafficAircraftSymbolGraphic));
            aircraft.transform.SetParent(root.transform, false); aircraft.layer = root.layer;
            cue._aircraft = aircraft.GetComponent<TrafficAircraftSymbolGraphic>();
            cue._aircraft.rectTransform.sizeDelta = new Vector2(42, 42); cue._aircraft.raycastTarget = false;
            aircraft.SetActive(false);
            var plate = new GameObject("Readout", typeof(RectTransform), typeof(Image));
            plate.transform.SetParent(root.transform, false); plate.layer = root.layer;
            cue._plate = plate.GetComponent<RectTransform>();
            cue._plate.sizeDelta = new Vector2(PlateWidth, PlateHeight);
            var background = plate.GetComponent<Image>();
            background.color = new Color(0.015f, 0.055f, 0.08f, 0.83f);
            background.raycastTarget = false;
            cue._title = MakeText(plate.transform, "Identity", 16, FontStyle.Bold);
            cue._detail = MakeText(plate.transform, "Range and relative altitude", 15, FontStyle.Normal);
            cue._state = MakeText(plate.transform, "View and source", 15, FontStyle.Normal);
            LayoutText(cue._title, new Vector2(8, -4), new Vector2(PlateWidth - 16, 20));
            LayoutText(cue._detail, new Vector2(8, -26), new Vector2(PlateWidth - 16, 19));
            LayoutText(cue._state, new Vector2(8, -47), new Vector2(PlateWidth - 16, 19));
            cue._detail.color = new Color(0.83f, 0.92f, 0.96f);
            cue._state.color = new Color(0.62f, 0.76f, 0.82f);
            return cue;
        }

        public void Present(IndicatorData data) => PresentWithSettings(data, null);

        public void PresentWithSettings(IndicatorData data, IndicatorSettings settings)
        {
            bool weather = data.Type == IndicatorType.Weather;
            bool traffic = data.Type == IndicatorType.Traffic;
            bool offscreen = data.Visibility != IndicatorVisibility.OnScreen;
            bool leftLabel = LabelOnLeft(data);
            float half = SymbolHalfExtent(data, settings);
            float plateOffset = Mathf.Max(30f, half + 8f);
            _plate.pivot = new Vector2(leftLabel ? 1 : 0, .5f);
            Canvas canvas = GetComponentInParent<Canvas>();
            float pixelScale = IndicatorSystem.Controller.IndicatorSystemController.PixelsPerReferenceUnit(canvas, canvas != null ? canvas.worldCamera : null) *
                (settings != null ? settings.globalScale : 1f);
            _plate.anchoredPosition = new Vector2(leftLabel ? -plateOffset : plateOffset, ReadoutVerticalOffset(data, pixelScale));
            Color tint = weather ? new Color(.64f, .84f, 1f) : data.Color;
            int detail = 0;
            _aircraft.gameObject.SetActive(traffic);
            if (traffic)
            {
                var visual = data; visual.Color = tint;
                _aircraft.Configure(visual, settings);
                detail = (int)_aircraft.CurrentDetail;
                // Edge direction and target orientation are separate signals.
                _symbol.rectTransform.sizeDelta = new Vector2(108, 108);
                _symbol.ConfigureArrowOnly(offscreen, data.ArrowRotation, tint, 52f);
            }
            else
            {
                _symbol.rectTransform.sizeDelta = new Vector2(42, 42);
                _symbol.Configure(IconFor(data), offscreen, data.ArrowRotation, tint);
            }
            int titleKey = weather ? (int)data.WeatherKind : 1000 + (int)data.AircraftType + detail * 100;
            if (titleKey != _titleKey || !ReferenceEquals(data.Label, _titleLabel))
            {
                _titleKey = titleKey; _titleLabel = data.Label;
                _title.text = weather ? WeatherLabel(data.WeatherKind) : AircraftLabel(data.AircraftType) + " · " + CleanLabel(data.Label, "NO ID");
            }
            if (tint != _titleTint) { _titleTint = tint; _title.color = tint; }
            bool simulated = data.IsIllustrativeWeather || data.Label == "SIM WX";
            float distance = data.DistanceNM, rel = data.RelativeAltitudeFeet;
            int a = weather ? Mathf.RoundToInt(distance) * 2 + (simulated ? 1 : 0) : Mathf.RoundToInt(distance * 10f);
            int b = weather ? int.MinValue + 1 : float.IsNaN(rel) || float.IsInfinity(rel) ? int.MaxValue : Mathf.RoundToInt(rel / 100f);
            if (a != _detailA || b != _detailB)
            {
                _detailA = a; _detailB = b;
                _detail.text = weather
                    ? distance.ToString("F0", CultureInfo.InvariantCulture) + " NM · " + (simulated ? "SIMULATED RETURN" : "RADAR RETURN")
                    : distance.ToString("F1", CultureInfo.InvariantCulture) + " NM  " + FormatRelativeAltitude(rel);
            }
            int stateKey = data.Visibility == IndicatorVisibility.Behind ? 2 : offscreen ? 1 : 0;
            int vertical = traffic && data.HasVerticalSpeed && !float.IsNaN(data.VerticalSpeedFpm) && !float.IsInfinity(data.VerticalSpeedFpm)
                ? Mathf.RoundToInt(data.VerticalSpeedFpm / 100f) : int.MinValue;
            int presentationKey = stateKey + detail * 10;
            if (presentationKey != _stateKey || vertical != _verticalStateKey)
            {
                _stateKey = presentationKey; _verticalStateKey = vertical;
                _state.text = stateKey == 2 ? "BEHIND YOU · FOLLOW ARROW" : stateKey == 1 ? "OUTSIDE VIEW · FOLLOW ARROW" :
                    traffic ? TrafficState(data, vertical, _aircraft.CurrentDetail) : "IN VIEW · TARGET AT ICON";
            }
        }

        private static string TrafficState(IndicatorData data, int rate, TrafficGlyphDetail detail)
        {
            if (rate != int.MinValue && Mathf.Abs(rate) >= 3)
                return (rate > 0 ? "CLIMB ↑ " : "DESCENT ↓ ") + (Mathf.Abs(rate) * 100).ToString("#,0", CultureInfo.InvariantCulture) + " FPM";
            if (detail == TrafficGlyphDetail.CloseAttitude) return "IN VIEW · REPORTED ATTITUDE";
            if (detail == TrafficGlyphDetail.FarDoubleDiamond) return "IN VIEW · DISTANT TRAFFIC";
            return "IN VIEW · TYPE SYMBOL";
        }

        public static bool LabelOnLeft(IndicatorData data) => data.HasReadoutSide ? data.ReadoutOnLeft : data.ScreenPosition.x >
            (data.ProjectionRect.width > 0 ? data.ProjectionRect.center.x : Screen.width * .5f);

        public static float SymbolHalfExtent(IndicatorData data, IndicatorSettings settings)
            => data.Type == IndicatorType.Traffic ? data.Visibility == IndicatorVisibility.OnScreen ? 42f : 54f : 21f;

        public static string AircraftLabel(TrafficRadar.TrafficRadarDataManager.AircraftType type)
        {
            switch (type)
            {
                case TrafficRadar.TrafficRadarDataManager.AircraftType.Commercial: return "AIRLINER";
                case TrafficRadar.TrafficRadarDataManager.AircraftType.General: return "LIGHT AIRCRAFT";
                case TrafficRadar.TrafficRadarDataManager.AircraftType.Military: return "MILITARY";
                case TrafficRadar.TrafficRadarDataManager.AircraftType.Helicopter: return "HELICOPTER";
                default: return "TYPE UNKNOWN";
            }
        }

        public static string WeatherLabel(WeatherCueKind kind) => kind == WeatherCueKind.RainLight ? "LIGHT RAIN" :
            kind == WeatherCueKind.RainModerate ? "MODERATE RAIN" : kind == WeatherCueKind.RainHeavy ? "HEAVY RAIN" : "WEATHER RETURN";

        public static FaaRadarIcon IconFor(IndicatorData data)
        {
            if (data.Type == IndicatorType.Weather)
                return data.WeatherKind == WeatherCueKind.RainLight ? FaaRadarIcon.WeatherRainLight :
                    data.WeatherKind == WeatherCueKind.RainModerate ? FaaRadarIcon.WeatherRainModerate :
                    data.WeatherKind == WeatherCueKind.RainHeavy ? FaaRadarIcon.WeatherRainHeavy : FaaRadarIcon.WeatherReturn;
            switch (data.AircraftType)
            {
                case TrafficRadar.TrafficRadarDataManager.AircraftType.Commercial: return FaaRadarIcon.AircraftAirliner;
                case TrafficRadar.TrafficRadarDataManager.AircraftType.General: return FaaRadarIcon.AircraftGeneral;
                case TrafficRadar.TrafficRadarDataManager.AircraftType.Military: return FaaRadarIcon.AircraftMilitary;
                case TrafficRadar.TrafficRadarDataManager.AircraftType.Helicopter: return FaaRadarIcon.AircraftHelicopter;
                default: return FaaRadarIcon.AircraftUnknown;
            }
        }

        public static string FormatRelativeAltitude(float feet)
        {
            if (float.IsNaN(feet) || float.IsInfinity(feet)) return "REL — FT";
            // Explicit feet, rounded to the nearest hundred; no ambiguous thousands/hundreds suffix.
            int rounded = Mathf.RoundToInt(feet / 100f) * 100;
            return "REL " + rounded.ToString("+#,0;-#,0;0", CultureInfo.InvariantCulture) + " FT";
        }

        private static string CleanLabel(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            value = value.Replace("\n", " ").Replace("\r", " ").Replace("\t", " ").Trim();
            return value.Length > 8 ? value.Substring(0, 8) : value;
        }

        public static Rect ScreenBounds(IndicatorData data, float scale) => ScreenBoundsForSettings(data, scale, null);

        private static float SafePixelScale(float scale) => TrafficGlyphPolicy.Finite(scale) && scale > .0001f &&
            TrafficGlyphPolicy.Finite(scale * 1024f) ? scale : 1f;

        /// <summary>Keep the readout inside the HUD window without moving the real target glyph.</summary>
        public static float ReadoutVerticalOffset(IndicatorData data, float pixelScale)
        {
            Rect region = data.ProjectionRect;
            if (!TrafficGlyphPolicy.Finite(data.ScreenPosition.y) || !TrafficGlyphPolicy.Finite(region.yMin) ||
                !TrafficGlyphPolicy.Finite(region.yMax) || !TrafficGlyphPolicy.Finite(region.height) || region.height <= 0) return 0f;
            float scale = SafePixelScale(pixelScale);
            float inset = (PlateHeight * .5f + 6f) * scale;
            float low = region.yMin + inset, high = region.yMax - inset;
            // A tiny region cannot contain the whole plate. Centre it rather than clamp against inverted limits.
            float center = low <= high ? Mathf.Clamp(data.ScreenPosition.y, low, high) : region.center.y;
            float offset = (center - data.ScreenPosition.y) / scale;
            return TrafficGlyphPolicy.Finite(offset) ? offset : 0f;
        }

        public static Rect ScreenBoundsForSettings(IndicatorData data, float scale, IndicatorSettings settings)
        {
            scale = SafePixelScale(scale);
            bool left = LabelOnLeft(data);
            float half = SymbolHalfExtent(data, settings);
            float side = Mathf.Max(30f, half + 8f) + PlateWidth + 6f;
            float other = half + 6f;
            float plateY = data.ScreenPosition.y + ReadoutVerticalOffset(data, scale) * scale;
            float minY = Mathf.Min(data.ScreenPosition.y - other * scale, plateY - (PlateHeight * .5f + 6f) * scale);
            float maxY = Mathf.Max(data.ScreenPosition.y + other * scale, plateY + (PlateHeight * .5f + 6f) * scale);
            return Rect.MinMaxRect(data.ScreenPosition.x - (left ? side : other) * scale, minY,
                data.ScreenPosition.x + (left ? other : side) * scale, maxY);
        }

        internal static Text MakeText(Transform parent, string name, int size, FontStyle style)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false); go.layer = parent.gameObject.layer;
            var text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.fontSize = size;
            text.fontStyle = style;
            text.supportRichText = false;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.alignment = TextAnchor.MiddleLeft;
            return text;
        }

        internal static void LayoutText(Text text, Vector2 position, Vector2 size)
        {
            var rt = text.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = position;
            rt.sizeDelta = size;
        }
    }

    /// <summary>SVG aircraft/weather pictogram with a separate edge-direction arrow. No blinking or spinning.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class PilotCueSymbol : MaskableGraphic
    {
        private FaaRadarIcon _icon;
        private bool _offscreen;
        private float _angle;
        private bool _arrowOnly;
        private float _arrowRadius = 21f;
        private static FaaSvgIconLibrary _library;
        public void Configure(bool weather, bool offscreen, float angle, Color tint)
            => Configure(weather ? FaaRadarIcon.WeatherRainModerate : FaaRadarIcon.AircraftAirliner, offscreen, angle, tint);

        public void Configure(FaaRadarIcon icon, bool offscreen, float angle, Color tint)
        {
            if (!_arrowOnly && _icon == icon && _offscreen == offscreen && Mathf.Abs(Mathf.DeltaAngle(_angle, angle)) < 0.1f && color == tint) return;
            _arrowOnly = false; _arrowRadius = 21f;
            _icon = icon; _offscreen = offscreen; _angle = angle; color = tint;
            SetVerticesDirty();
        }

        public void ConfigureArrowOnly(bool offscreen, float angle, Color tint, float radius = 52f)
        {
            if (_arrowOnly && _offscreen == offscreen && Mathf.Abs(Mathf.DeltaAngle(_angle, angle)) < .1f && color == tint && Mathf.Approximately(_arrowRadius, radius)) return;
            _arrowOnly = true; _offscreen = offscreen; _angle = angle; _arrowRadius = radius; color = tint; SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_library == null) _library = Resources.Load<FaaSvgIconLibrary>(FaaSvgIconLibrary.ResourcePath);
            var entry = _library != null ? _library.Find(_icon) : null;
            float scale = _arrowOnly ? 1f : Mathf.Min(rectTransform.rect.width, rectTransform.rect.height) / 42f;
            if (!_arrowOnly && entry != null)
            {
                foreach (var point in entry.vertices) vh.AddVert(rectTransform.rect.center + point * (1.18f * scale), color, Vector2.zero);
                for (int i = 0; i < entry.triangles.Length; i += 3) vh.AddTriangle(entry.triangles[i], entry.triangles[i+1], entry.triangles[i+2]);
            }
            if (_offscreen)
            {
                Quaternion rotation = Quaternion.Euler(0, 0, -_angle);
                Stroke(vh, rotation * (new Vector2(-5, _arrowRadius - 6) * scale), rotation * (new Vector2(0, _arrowRadius) * scale), color);
                Stroke(vh, rotation * (new Vector2(0, _arrowRadius) * scale), rotation * (new Vector2(5, _arrowRadius - 6) * scale), color);
            }
        }

        private static void Stroke(VertexHelper vh, Vector2 a, Vector2 b, Color tint)
        {
            Vector2 n = new Vector2(-(b-a).y, (b-a).x).normalized * 0.9f;
            int start = vh.currentVertCount;
            vh.AddVert(a - n, tint, Vector2.zero); vh.AddVert(a + n, tint, Vector2.zero);
            vh.AddVert(b + n, tint, Vector2.zero); vh.AddVert(b - n, tint, Vector2.zero);
            vh.AddTriangle(start, start+1, start+2); vh.AddTriangle(start, start+2, start+3);
        }
    }
}
