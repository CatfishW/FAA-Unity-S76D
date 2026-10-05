using HUDControl.Elements;
using TMPro;
using TrafficRadar;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// LOC (lateral) or G/S (vertical) deviation scale, drawn ONLY while real ILS/LOC/GPS deviation data is valid.
    /// Without guidance it draws nothing, shows no labels, reports no layout bounds, registers no keep-out and is no obstacle for the
    /// Pilot Brief (AC 25-11B: automatic declutter of no-data states; never a plausible on-course cue).
    /// Calibrated: dots at +/-1 and +/-2 dots, full scale 2.5 dots; at full scale the diamond is pegged as a half-diamond.
    /// One source label ("LOC" or "G/S"), monochrome in the pilot's HUD colour, halo, >= 18 ref on screen.
    /// Map targets are not shown here: the selected target lives only on the heading tape.
    /// The lateral scale pins itself directly above the heading tape (zone Z5), outside the boresight field.
    /// </summary>
    [ExecuteAlways, RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaNavigationScaleGraphic : MaskableGraphic
    {
        public const float FullScaleDots = 2.5f;
        /// <summary>Deviation at or beyond this is shown pegged (the bridge clamps to 2.5 dots).</summary>
        public const float PegDots = 2.45f;
        /// <summary>Gap between the LOC scale and the top of the heading tape, reference pixels.</summary>
        public const float LocGapAboveHeadingTape = 6f;
        public const float LabelSize = FaaHudStyle.Secondary;
        private const float HorizontalEndMargin = 60f, VerticalEndMargin = 28f;
        private const float DotRadius = 4f, DotStroke = 1.6f, DiamondHalf = 8f, DiamondStroke = 2f, IndexHalf = 7f;
        private const float ContentHalfHeight = 12f;
        private static readonly Color Halo = new Color(.01f, .025f, .03f, .75f);

        [SerializeField] private bool vertical;
        [SerializeField] private LocalizerElement localizer;
        [SerializeField] private GlidescopeElement glideslope;
        [Tooltip("Retained for serialized compatibility. Map targets are shown on the heading tape, never on a deviation scale.")]
        [SerializeField] private TrafficRadarDisplay navigationDisplay;
        [Tooltip("Play mode: pin the lateral (LOC) scale directly above the heading tape.")]
        [SerializeField] private bool pinLateralAboveHeadingTape = true;
        private TMP_Text title;
        private TMP_Text detail;
        private TMP_Text positiveLabel;
        private TMP_Text negativeLabel;
        private bool hasGuidance;
        private bool clamped;
        private float position;
        private bool stateApplied;
        private bool shownGuidance;
        private bool shownClamped;
        private float shownPosition = float.NaN;
        private float shownScale = -1f;
        private Color shownTint = new Color(-1f, -1f, -1f, -1f);
        private CanvasGroup visibility;
        private Canvas cachedCanvas;
        private bool keepOutRegistered;

        /// <summary>True only while real deviation guidance is valid and drawn.</summary>
        public bool HasGuidance => hasGuidance;
        public bool IsVertical => vertical;
        /// <summary>Normalized diamond position, -1..1 = full scale (2.5 dots).</summary>
        public float Position => position;
        public bool Clamped => clamped;

        public void Configure(bool isVertical, LocalizerElement lateral, GlidescopeElement glide, TrafficRadarDisplay radar)
        {
            vertical = isVertical;
            localizer = lateral;
            glideslope = glide;
            navigationDisplay = radar;
            raycastTarget = false;
            stateApplied = false;
            Refresh();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            raycastTarget = false;
            stateApplied = false;
        }

        protected override void OnTransformParentChanged()
        {
            base.OnTransformParentChanged();
            cachedCanvas = null;
        }

        private void Update()
        {
            Refresh();
            if (!Application.isPlaying)
            {
                return;
            }

            EnsureRuntimeComponents();
            PlaceLateralAboveHeadingTape();
            float alpha = hasGuidance ? FaaHudInspection.ForwardHudIntensity : 0f;
            if (visibility != null && Mathf.Abs(visibility.alpha - alpha) > .002f)
            {
                visibility.alpha = alpha;
            }
        }

        private void Refresh()
        {
            bool available = vertical ? glideslope != null && glideslope.HasDeviationData :
                localizer != null && localizer.HasDeviationData;
            float dots = 0f;
            if (available)
            {
                dots = vertical ? -glideslope.GetDisplayedDeviation() : localizer.GetDisplayedDeviation();
                available = !float.IsNaN(dots) && !float.IsInfinity(dots);
            }

            hasGuidance = available;
            position = available ? Mathf.Clamp(dots / FullScaleDots, -1f, 1f) : 0f;
            clamped = available && Mathf.Abs(dots) >= PegDots;
            float scale = EffectiveScale();
            Color tint = FaaHudStyle.WithAlpha(FaaHudPilotColor.Resolve(FaaHudStyle.Green), 1f);
            bool changed = !stateApplied || hasGuidance != shownGuidance || clamped != shownClamped ||
                           Mathf.Abs(scale - shownScale) > .01f || tint != shownTint;
            if (changed)
            {
                ApplyState(scale, tint);
            }

            if (changed || float.IsNaN(shownPosition) || Mathf.Abs(position - shownPosition) > .0005f)
            {
                shownPosition = position;
                SetVerticesDirty();
            }
        }

        private void ApplyState(float scale, Color tint)
        {
            stateApplied = true;
            shownGuidance = hasGuidance;
            shownClamped = clamped;
            shownScale = scale;
            shownTint = tint;
            // Graphic colour alpha carries visibility for layout/keep-out consumers; the mesh uses explicit vertex colours.
            Color visible = new Color(1f, 1f, 1f, hasGuidance ? 1f : 0f);
            if (color != visible)
            {
                color = visible;
            }

            float end = End();
            float labelSize = FaaHudStyle.Legible(LabelSize, LabelSize / scale);
            title = Label("Mode", title, vertical ? new Vector2(0f, end + 8f + 11f) : new Vector2(-end - 8f - 26f, 0f), labelSize,
                vertical ? TextAlignmentOptions.Midline : TextAlignmentOptions.MidlineRight);
            detail = Label("Detail", detail, Vector2.zero, labelSize, TextAlignmentOptions.Midline);
            positiveLabel = Label("Positive", positiveLabel, Vector2.zero, labelSize, TextAlignmentOptions.Midline);
            negativeLabel = Label("Negative", negativeLabel, Vector2.zero, labelSize, TextAlignmentOptions.Midline);
            // One source label only: no L/R, UP/DN or "x.x DOT" text, and nothing at all without guidance.
            SetText(title, hasGuidance ? (vertical ? "G/S" : "LOC") : "");
            SetText(detail, "");
            SetText(positiveLabel, "");
            SetText(negativeLabel, "");
            SetActive(title, hasGuidance);
            SetActive(detail, false);
            SetActive(positiveLabel, false);
            SetActive(negativeLabel, false);
            StyleLabel(title, tint);
        }

        private float End()
        {
            Rect rect = rectTransform.rect;
            return vertical ? Mathf.Max(20f, rect.height * .5f - VerticalEndMargin) : Mathf.Max(30f, rect.width * .5f - HorizontalEndMargin);
        }

        /// <summary>Reference pixels per local unit, so symbol and text sizes have an on-screen floor at reduced module scales.</summary>
        private float EffectiveScale()
        {
            if (cachedCanvas == null)
            {
                cachedCanvas = GetComponentInParent<Canvas>();
            }

            if (cachedCanvas == null)
            {
                return 1f;
            }

            float root = Mathf.Abs(cachedCanvas.rootCanvas.transform.lossyScale.y);
            float own = Mathf.Abs(transform.lossyScale.y);
            float scale = root > 1e-6f ? own / root : 1f;
            return float.IsNaN(scale) || scale <= .05f ? 1f : scale;
        }

        private void EnsureRuntimeComponents()
        {
            if (visibility == null)
            {
                visibility = GetComponent<CanvasGroup>();
                if (visibility == null)
                {
                    visibility = gameObject.AddComponent<CanvasGroup>();
                }

                visibility.interactable = false;
                visibility.blocksRaycasts = false;
                visibility.alpha = hasGuidance ? 1f : 0f;
            }

            if (!keepOutRegistered)
            {
                // Registered only while visible (CanvasGroup alpha > 0.05): a hidden scale leaves no hole in the horizon.
                FaaHudKeepOutRegion.Ensure(gameObject, vertical ? "gs-scale" : "loc-scale", FaaKeepOutKind.Symbology, 4f);
                keepOutRegistered = true;
            }
        }

        /// <summary>Lateral scale only: its bottom edge sits 6 ref above the heading tape's top edge (zone Z5), centred.</summary>
        private void PlaceLateralAboveHeadingTape()
        {
            if (vertical || !pinLateralAboveHeadingTape || !hasGuidance || cachedCanvas == null)
            {
                return;
            }

            RectTransform root = cachedCanvas.rootCanvas.transform as RectTransform;
            if (root == null || root.rect.height < 200f)
            {
                return;
            }

            float scale = EffectiveScale();
            float halfHeight = ContentHalfHeight * Mathf.Max(1f, 1f / scale) * scale;
            float centreFromTop = FaaHeadingTapeOverlay.LaneTopFromScreenTop - LocGapAboveHeadingTape - halfHeight;
            Vector3 targetLocal = new Vector3(root.rect.center.x, root.rect.yMax - centreFromTop, 0f);
            Vector3 currentLocal = root.InverseTransformPoint(rectTransform.TransformPoint(rectTransform.rect.center));
            Vector2 delta = new Vector2(targetLocal.x - currentLocal.x, targetLocal.y - currentLocal.y);
            if (delta.sqrMagnitude <= .0001f)
            {
                return;
            }

            Vector3 worldDelta = root.TransformVector(new Vector3(delta.x, delta.y, 0f));
            rectTransform.position += worldDelta;
        }

        private TMP_Text Label(string name, TMP_Text text, Vector2 at, float fontSize, TextAlignmentOptions alignment)
        {
            if (text == null)
            {
                Transform child = transform.Find(name);
                GameObject go = child != null ? child.gameObject : new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(transform, false);
                text = go.GetComponent<TextMeshProUGUI>();
                if (text == null)
                {
                    text = go.AddComponent<TextMeshProUGUI>();
                }

                text.font = TMP_Settings.defaultFontAsset;
                text.enableAutoSizing = false;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.overflowMode = TextOverflowModes.Overflow;
                text.raycastTarget = false;
                text.richText = false;
            }

            text.fontStyle = FontStyles.Bold;
            text.alignment = alignment;
            text.fontSize = fontSize;
            text.rectTransform.anchorMin = text.rectTransform.anchorMax = text.rectTransform.pivot = new Vector2(.5f, .5f);
            text.rectTransform.anchoredPosition = at;
            text.rectTransform.sizeDelta = new Vector2(52f, Mathf.Max(22f, fontSize + 4f));
            text.rectTransform.localScale = Vector3.one;
            return text;
        }

        private static void SetText(TMP_Text text, string value)
        {
            if (text != null && text.text != value)
            {
                text.text = value;
            }
        }

        private static void SetActive(TMP_Text text, bool active)
        {
            if (text != null && text.gameObject.activeSelf != active)
            {
                text.gameObject.SetActive(active);
            }
        }

        private static void StyleLabel(TMP_Text text, Color tint)
        {
            if (text == null)
            {
                return;
            }

            text.color = FaaHudStyle.WithAlpha(tint, Mathf.Max(tint.a, FaaHudStyle.MinTextAlpha));
            // TMP already bakes its tint into the mesh. A retained renderer
            // fade would multiply that alpha and make the scale unreadable.
            text.canvasRenderer.SetColor(Color.white);
            if (Application.isPlaying)
            {
                FaaHudStyle.ApplyHalo(text);
            }
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (!hasGuidance)
            {
                return;
            }

            float scale = EffectiveScale();
            float f = Mathf.Max(1f, 1f / scale);
            Color tint = shownTint.a >= 0f ? shownTint : FaaHudStyle.Green;
            Color scaleTint = FaaHudStyle.WithAlpha(tint, .85f);
            Vector2 axis = vertical ? Vector2.up : Vector2.right;
            Vector2 cross = vertical ? Vector2.right : Vector2.up;
            float end = End();

            // Centre index and calibrated dots at +/-1 and +/-2 dots (full scale = 2.5 dots = end).
            Line(mesh, -cross * IndexHalf * f, cross * IndexHalf * f, DiamondStroke * 2.2f * f, Halo);
            Line(mesh, -cross * IndexHalf * f, cross * IndexHalf * f, DiamondStroke * f, scaleTint);
            for (int k = -2; k <= 2; k++)
            {
                if (k == 0)
                {
                    continue;
                }

                Vector2 centre = axis * (end * k / FullScaleDots);
                Ring(mesh, centre, DotRadius * f, DotStroke * 2.4f * f, Halo);
                Ring(mesh, centre, DotRadius * f, DotStroke * f, scaleTint);
            }

            Vector2 target = axis * (position * end);
            Vector2[] diamond = { cross * DiamondHalf * f, axis * DiamondHalf * f, -cross * DiamondHalf * f, -axis * DiamondHalf * f };
            if (!clamped)
            {
                for (int i = 0; i < 4; i++)
                {
                    Line(mesh, target + diamond[i], target + diamond[(i + 1) % 4], DiamondStroke * 2.2f * f, Halo);
                }

                for (int i = 0; i < 4; i++)
                {
                    Line(mesh, target + diamond[i], target + diamond[(i + 1) % 4], DiamondStroke * f, tint);
                }

                return;
            }

            // Pegged at full scale: only the inner half of the diamond remains, parked at the scale end.
            Vector2 inner = -axis * Mathf.Sign(position) * DiamondHalf * f;
            Vector2 a = target + diamond[0], b = target + inner, c = target + diamond[2];
            Line(mesh, a, b, DiamondStroke * 2.2f * f, Halo);
            Line(mesh, b, c, DiamondStroke * 2.2f * f, Halo);
            Line(mesh, a, b, DiamondStroke * f, tint);
            Line(mesh, b, c, DiamondStroke * f, tint);
        }

        private static void Ring(VertexHelper mesh, Vector2 centre, float radius, float width, Color tint)
        {
            const int segments = 20;
            for (int j = 0; j < segments; j++)
            {
                float a = 2 * Mathf.PI * j / segments;
                float b = 2 * Mathf.PI * (j + 1) / segments;
                Line(mesh, centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius,
                    centre + new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * radius, width, tint);
            }
        }

        private static void Line(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color tint)
        {
            Vector2 direction = b - a;
            if (direction.sqrMagnitude < .0001f) return;
            Vector2 n = new Vector2(-direction.y, direction.x).normalized * width * .5f;
            int start = mesh.currentVertCount;
            Vertex(mesh, a - n, tint);
            Vertex(mesh, a + n, tint);
            Vertex(mesh, b + n, tint);
            Vertex(mesh, b - n, tint);
            mesh.AddTriangle(start, start + 1, start + 2);
            mesh.AddTriangle(start, start + 2, start + 3);
        }

        private static void Vertex(VertexHelper mesh, Vector2 point, Color tint)
        {
            UIVertex vertex = UIVertex.simpleVert;
            vertex.position = point;
            vertex.color = tint;
            mesh.AddVert(vertex);
        }
    }
}
