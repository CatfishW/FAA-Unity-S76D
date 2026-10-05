using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WeatherRadar
{
    /// <summary>Crisp vector references. Weather returns remain on their own unmodified layer.</summary>
    [ExecuteAlways, DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class XPlaneWeatherRadarFace : MaskableGraphic
    {
        [SerializeField] private WeatherRadarDataProvider dataProvider;
        /// <summary>FAA minimum legible size on the radar canvas (1 unit is about 1 reference px at the audited panel scale).</summary>
        public const float MinimumFontSize = 15f;
        /// <summary>Only the half-range and full-range arcs are labelled (rings 2 and 4 of 4).</summary>
        public static readonly int[] LabelledRings = { 2, 4 };
        private readonly TMP_Text[] _rangeLabels = new TMP_Text[2];
        private Vector2 _lastSize;
        private float _lastRange = -1f;
        private float _textScale = 1f, _lastScale = -1f;
        private static readonly Color Ink = new Color(.64f, .79f, .83f, .54f);
        /// <summary>Near-black knockout plate behind each range label (WeatherRadarPalette.NoReturn tone), so a white numeral never
        /// sits directly on a green/yellow/red return.</summary>
        public static readonly Color PlateColor = new Color(.016f, .04f, .055f, .86f);
        private readonly Rect[] _plates = new Rect[2];

        /// <summary>Knockout plate (face-local rect) behind range label <paramref name="index"/>; empty before the first layout.</summary>
        public Rect LabelPlate(int index) => index >= 0 && index < _plates.Length ? _plates[index] : default;

        /// <summary>Plate size for a range numeral of <paramref name="digits"/> characters at <paramref name="fontSize"/>.</summary>
        public static Vector2 PlateSize(int digits, float fontSize) =>
            new Vector2(Mathf.Max(fontSize * 1.4f, Mathf.Max(1, digits) * fontSize * .64f + 10f), fontSize + 6f);

        /// <summary>Readability multiplier driven by the host so world-space panels keep the FAA minimum.</summary>
        public float TextScale
        {
            get => _textScale;
            set => _textScale = Mathf.Clamp(value, 1f, 2f);
        }

        /// <summary>Label font for a sector of the given width (never below the FAA minimum).</summary>
        public static float LabelFontSize(float sectorWidth, float scale) =>
            Mathf.Clamp(sectorWidth / 22f, MinimumFontSize, 17f) * Mathf.Max(1f, scale);

        public void Configure(WeatherRadarDataProvider provider)
        {
            if (GetComponent<CanvasRenderer>() == null) gameObject.AddComponent<CanvasRenderer>();
            dataProvider = provider;
            raycastTarget = false;
            RefreshFace();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            raycastTarget = false;
        }

        private void Update()
        {
            float range = dataProvider != null ? dataProvider.RadarData.currentRange : 160f;
            if (_rangeLabels[0] == null || _lastSize != rectTransform.rect.size || !Mathf.Approximately(range, _lastRange) ||
                !Mathf.Approximately(_textScale, _lastScale))
                RefreshFace();
        }

        private void RefreshFace()
        {
            _lastSize = rectTransform.rect.size;
            _lastScale = _textScale;
            _lastRange = dataProvider != null ? dataProvider.RadarData.currentRange : 160f;
            Rect bounds = rectTransform.rect;
            Vector2 origin = new Vector2(bounds.center.x, bounds.yMin + bounds.height * XPlaneWeatherRadarGeometry.OriginHeight);
            float radius = bounds.height * XPlaneWeatherRadarGeometry.Radius;
            // Retire the old 1/4 and 3/4 labels that sat on the boresight returns.
            foreach (string retired in new[] { "Range 1", "Range 3" })
            {
                Transform old = transform.Find(retired);
                if (old != null && old.gameObject.activeSelf) old.gameObject.SetActive(false);
            }
            float fontSize = LabelFontSize(bounds.width, _textScale);
            for (int i = 0; i < _rangeLabels.Length; i++)
            {
                int ring = LabelledRings[i];
                if (_rangeLabels[i] == null)
                {
                    Transform child = transform.Find("Range " + ring);
                    var go = child != null ? child.gameObject : new GameObject("Range " + ring, typeof(RectTransform));
                    go.transform.SetParent(transform, false);
                    _rangeLabels[i] = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
                }
                TMP_Text label = _rangeLabels[i];
                label.gameObject.SetActive(true);
                label.text = XPlaneWeatherRadarGeometry.RangeAtRing(_lastRange, ring).ToString("0.##");
                label.fontSize = fontSize;
                label.enableAutoSizing = false;
                label.fontStyle = FontStyles.Bold;
                label.alignment = TextAlignmentOptions.Center;
                // Light text with a dark halo stays legible on the near-black face and on any return colour.
                label.color = new Color(.84f, .94f, .96f, 1f);
                label.faceColor = Color.white;
                label.extraPadding = true;
                label.outlineWidth = .22f;
                label.outlineColor = new Color32(0, 8, 10, 230);
                label.canvasRenderer.SetColor(Color.white);
                label.raycastTarget = false;
                label.textWrappingMode = TextWrappingModes.NoWrap;
                var rect = label.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
                rect.sizeDelta = new Vector2(48f, 22f) * Mathf.Max(1f, _textScale);
                // Just inside each labelled arc beside the left radial: clear of the boresight returns and
                // inside the sector picture.
                rect.anchoredPosition = XPlaneWeatherRadarGeometry.Point(origin, radius * ring / 4f - fontSize * .7f,
                    -XPlaneWeatherRadarGeometry.HalfAngle + 9f) + new Vector2(4f, 0f);
                Vector2 plate = PlateSize(label.text.Length, fontSize);
                // The label is centre-anchored, so its face-local centre is the rect centre plus its anchored position.
                _plates[i] = new Rect(bounds.center + rect.anchoredPosition - plate * .5f, plate);
            }
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            Vector2 origin = new Vector2(r.center.x, r.yMin + r.height * XPlaneWeatherRadarGeometry.OriginHeight);
            float radius = r.height * XPlaneWeatherRadarGeometry.Radius;
            float halfAngle = XPlaneWeatherRadarGeometry.HalfAngle;
            for (int ring = 1; ring <= 4; ring++)
            {
                Color ink = Ink;
                ink.a = ring == 4 ? .70f : ring == 2 ? .46f : .28f;
                float rr = radius * ring / 4f;
                Vector2 previous = XPlaneWeatherRadarGeometry.Point(origin, rr, -halfAngle);
                for (int i = 1; i <= 100; i++)
                {
                    Vector2 point = XPlaneWeatherRadarGeometry.Point(origin, rr, Mathf.Lerp(-halfAngle, halfAngle, i / 100f));
                    Line(vh, previous, point, ring == 4 ? 1.25f : .85f, ink);
                    previous = point;
                }
            }
            Color dim = new Color(Ink.r, Ink.g, Ink.b, .25f);
            Line(vh, XPlaneWeatherRadarGeometry.Point(origin, radius * .08f, -halfAngle), XPlaneWeatherRadarGeometry.Point(origin, radius, -halfAngle), .8f, dim);
            Line(vh, XPlaneWeatherRadarGeometry.Point(origin, radius * .08f, halfAngle), XPlaneWeatherRadarGeometry.Point(origin, radius, halfAngle), .8f, dim);
            // The forward reference is deliberately neutral: not an invented navigation course.
            Line(vh, origin + Vector2.up * 14f, origin + Vector2.up * radius, .8f, dim);
            for (int angle = -50; angle <= 50; angle += 10)
                Line(vh, XPlaneWeatherRadarGeometry.Point(origin, radius - (angle % 30 == 0 ? 7f : 4f), angle), XPlaneWeatherRadarGeometry.Point(origin, radius, angle), 1f, Ink);
            Color aircraft = new Color(.82f, .96f, .94f, 1f);
            Line(vh, origin + Vector2.up * 8f, origin - Vector2.up * 7f, 1.6f, aircraft);
            Line(vh, origin + new Vector2(-8f, -1f), origin + Vector2.up * 2f, 1.6f, aircraft);
            Line(vh, origin + Vector2.up * 2f, origin + new Vector2(8f, -1f), 1.6f, aircraft);
            Line(vh, origin + new Vector2(-4f, -6f), origin + new Vector2(4f, -6f), 1.4f, aircraft);
            // Knockout plates last, so neither returns nor arcs run through the labels drawn on top (children of this graphic).
            for (int i = 0; i < _plates.Length; i++)
                if (_plates[i].width > 0f && _rangeLabels[i] != null && _rangeLabels[i].gameObject.activeSelf) Box(vh, _plates[i], PlateColor);
        }

        private static void Box(VertexHelper vh, Rect r, Color tint)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector2(r.xMin, r.yMin), tint, Vector2.zero);
            vh.AddVert(new Vector2(r.xMin, r.yMax), tint, Vector2.zero);
            vh.AddVert(new Vector2(r.xMax, r.yMax), tint, Vector2.zero);
            vh.AddVert(new Vector2(r.xMax, r.yMin), tint, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void Line(VertexHelper vh, Vector2 a, Vector2 b, float width, Color tint)
        {
            Vector2 normal = new Vector2(-(b - a).y, (b - a).x).normalized * width * .5f;
            int start = vh.currentVertCount;
            vh.AddVert(a - normal, tint, Vector2.zero);
            vh.AddVert(a + normal, tint, Vector2.zero);
            vh.AddVert(b + normal, tint, Vector2.zero);
            vh.AddVert(b - normal, tint, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
