using System.Collections.Generic;
using AircraftControl.Core;
using AviationUI;
using TMPro;
using TrafficRadar;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Digital heading scale, shared by both styles (zone Z5, centred below the attitude field; top edge 790 ref from the screen top, so it
    /// clears the wandering conformal waterline above and the bottom chrome below). Layout, local units (1 unit = 1 reference pixel at
    /// module scale 1), measured from the overlay centre:
    ///   +31      bug row: selected-heading bug (cyan) and map-target diamond (green) with its caption, at least 8 ref above the numerals;
    ///   +8       numerals every 10 deg (24 ref, bold, alpha 1, halo);
    ///   -14..-2  ticks standing on the baseline;
    ///   -14..+22 boxed current-heading readout (34 ref digits) with the heading reference annunciated once, beside the digits inside the
    ///            box, as its own 22 ref label ("TRU": the card, the radar and the map bearings are TRUE);
    ///   -24..-14 index tick under the box.
    /// Off scale, the selected heading shows as a filled cyan arrowhead just outside the tape end on the numeral row (14 ref tall), and a
    /// cyan-boxed "HDG 084" field under that end of the tape (below the index tick), so it never merges with the bug or the numerals and
    /// stays clear of the Classic small dials outboard of the tape.
    /// Invalid or stale heading removes the card and shows an amber "HDG" flag in the box (never a frozen value).
    /// Colour: the pilot symbology colour, or the Classic reference green while the Classic style is active.
    /// Intensity: the readout (digits, box, reference) is an awareness readout and keeps the awareness floor while a side panel is
    /// inspected; the rest of the tape follows the forward-HUD intensity.
    /// Text is assigned only when it changes; markers are fixed 5-degree slots, so scrolling only moves them.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(9950)]
    [RequireComponent(typeof(RectTransform))]
    [AddComponentMenu("FAA/Customization/FAA Heading Tape Overlay")]
    public class FaaHeadingTapeOverlay : MonoBehaviour
    {
        private const int MarkerCount = 73;
        private const int SlotCount = 72;
        private const float MarkerSpacingDegrees = 5f;
        private const float DefaultPixelsPerDegree = 5f;
        private static readonly Vector2 DefaultClipAnchoredPosition = new Vector2(0f, -5f);

        /// <summary>Zone Z5: top edge of the drawn tape, reference pixels from the top of the screen.</summary>
        public const float LaneTopFromScreenTop = 790f;
        /// <summary>Drawn extent above the overlay pivot (bug row), local units.</summary>
        public const float TopExtent = 36f;
        /// <summary>Drawn extent below the overlay pivot (index tick), local units.</summary>
        public const float BottomExtent = 24f;
        public const float BugRowY = 31f;
        public const float LabelY = 8f;
        public const float BaselineY = -14f;
        public const float ReadoutBoxWidth = 128f;
        public const float ReadoutBoxHeight = 36f;
        public const float ReadoutBoxCenterY = 4f;
        public const float LabelWidth = 40f;
        public const float LabelHeight = 28f;
        public const string InvalidHeadingFlag = "HDG";
        /// <summary>Heading reference annunciation (TRUE: card, radar and map bearings are true). Shown once, beside the digits.</summary>
        public const string TrueReferenceText = "TRU";
        public const float ReferenceLabelSize = 22f;
        /// <summary>Digits occupy the left of the box (right-aligned to <see cref="DigitsRightX"/>); the reference label starts at <see cref="ReferenceLeftX"/>.</summary>
        public const float DigitsRightX = 6f, ReferenceLeftX = 10f;
        /// <summary>Off-scale selected-heading cue: arrowhead just outside the tape end (on the numeral row), boxed value field under that end.</summary>
        public const float OffScaleArrowInset = 4f, OffScaleArrowLength = 12f, OffScaleArrowHalfHeight = 7f;
        /// <summary>The field's top edge sits this far below the index tick (-<see cref="BottomExtent"/>); it is right-/left-aligned to the tape end.</summary>
        public const float OffScaleFieldGap = 6f, OffScaleFieldWidth = 96f, OffScaleFieldHeight = 28f, OffScaleFieldSize = 20f;
        public const float OffScaleFieldCenterY = -(BottomExtent + OffScaleFieldGap + OffScaleFieldHeight * 0.5f);
        public const string SelectedHeadingPrefix = "HDG ";

        [Header("Data Sources")]
        [SerializeField] private AviationFlightDataProvider flightDataProvider;
        [SerializeField] private AircraftController aircraftController;
        [SerializeField] private global::HeadingHUD headingHud;
        [SerializeField] private Transform headingTarget;
        [SerializeField] private TrafficRadarDisplay navigationDisplay;
        [SerializeField] private bool autoFindSources = true;

        [Header("Layout")]
        [SerializeField, HideInInspector] private Vector2 anchoredPosition = new Vector2(0f, 540f - LaneTopFromScreenTop - TopExtent);
        [SerializeField, HideInInspector] private Vector2 size = new Vector2(520f, 64f);
        [SerializeField, HideInInspector] private Vector2 clipAnchoredPosition = new Vector2(0f, -5f);
        [SerializeField] private float pixelsPerDegree = DefaultPixelsPerDegree;
        [SerializeField] private float smoothing = 0.18f;
        [Tooltip("In play mode, pin the tape's top edge to zone Z5 (790 ref from the top) at any module scale.")]
        [SerializeField] private bool useCentreColumnLane = true;

        [Header("Style")]
        [SerializeField] private Color hudColor = new Color(0.2f, 1f, 0.2f, 1f);
        [SerializeField] private Color hudDimColor = new Color(0.2f, 1f, 0.2f, 0.92f);

        private static readonly string[] MarkerLabels = BuildMarkerLabels();
        private static string[] _readoutStrings;
        private static string[] _degreeStrings;
        private static string[] _selectedHeadingStrings;

        private readonly List<CompassMarker> _markers = new List<CompassMarker>(MarkerCount);
        private readonly List<TMP_Text> _fixedCardinalLabels = new List<TMP_Text>(4);
        private readonly Image[] _boxLines = new Image[4];
        private readonly List<CanvasGroup> _intensityGroups = new List<CanvasGroup>(6);
        private readonly List<CanvasGroup> _awarenessGroups = new List<CanvasGroup>(3);
        private readonly Image[] _fieldLines = new Image[4];
        private RectTransform _rectTransform;
        private Canvas _canvas;
        private RectTransform _clipRect;
        private Image _baseline;
        private Image _topRule;
        private Image _centerTick;
        private TMP_Text _headingReadout;
        private RectTransform _readoutBox;
        private RectTransform _navigationTargetCue;
        private Image _navigationTargetStem;
        private Image _navigationTargetArrow;
        private Image _navigationTargetDiamond;
        private TMP_Text _navigationTargetLabel;
        private RectTransform _selectedHeadingBug;
        private Image _bugLeft;
        private Image _bugRight;
        private Image _bugTop;
        private TMP_Text _bugValue;
        private TMP_Text _referenceLabel;
        private RectTransform _offScaleCue;
        private FaaHeadingBugArrowGraphic _offScaleArrow;
        private TMP_Text _offScaleValue;
        private float _offScaleSide;
        private bool _bugOffScale;
        private float _appliedAwareness = -1f;
        private float _displayedHeading;
        private int _shownReadout = int.MinValue;
        private bool _shownValid = true;
        private Color _styledColor = new Color(-1f, -1f, -1f, -1f);
        private float _styledScale = -1f;
        private float _appliedIntensity = -1f;
        private Material _halo;
        private string _targetIdentifier;
        private int _targetBearing = int.MinValue;
        private int _targetDistanceTenths = int.MinValue;
        private float _targetLabelHalfWidth = 60f;
        private bool _bugShown;
        private float _bugX;

        public void Configure(Vector2 overlayAnchoredPosition, Vector2 overlaySize, Color primaryColor, Color dimColor)
        {
            anchoredPosition = overlayAnchoredPosition;
            size = overlaySize;
            hudColor = primaryColor;
            hudDimColor = dimColor;
            pixelsPerDegree = DefaultPixelsPerDegree;
            EnsureBuilt();
            ApplyLayoutAndStyle();
            UpdateTape(true);
        }

        public void SetDataSources(
            AviationFlightDataProvider provider,
            AircraftController controller,
            Transform fallbackHeadingTarget)
        {
            flightDataProvider = provider;
            aircraftController = controller;
            if (fallbackHeadingTarget != null)
            {
                headingTarget = fallbackHeadingTarget;
            }
        }

        public void SetNavigationDisplay(TrafficRadarDisplay display)
        {
            navigationDisplay = display;
        }

        /// <summary>True while the card shows a valid heading (false: card removed, "HDG" flag shown).</summary>
        public bool HeadingValid => _shownValid;
        /// <summary>True while the selected-heading cue (the bug, or its off-scale arrowhead and field) is drawn.</summary>
        public bool SelectedHeadingBugVisible => _bugShown;
        /// <summary>True while the selected heading is off the tape and shown as the edge arrowhead plus the boxed value field.</summary>
        public bool SelectedHeadingOffScale => _bugShown && _bugOffScale;

        private void Awake()
        {
            CaptureEditorLayout();
            EnsureBuilt();
            RefreshDataSources();
            _displayedHeading = ReadHeading();
            UpdateTape(true);
        }

        private void OnEnable()
        {
            CaptureEditorLayout();
            EnsureBuilt();
            RefreshDataSources();
            _displayedHeading = ReadHeading();
            UpdateTape(true);
        }

        private void OnValidate()
        {
            CaptureEditorLayout();
            size.x = Mathf.Max(260f, size.x);
            size.y = Mathf.Max(34f, size.y);
            pixelsPerDegree = Mathf.Clamp(pixelsPerDegree, 1.1f, GetMaximumPixelsPerDegree(size.x));
            smoothing = Mathf.Clamp01(smoothing);
            EnsureBuilt();
            ApplyLayoutAndStyle();
            UpdateTape(true);
        }

        private void Update()
        {
            CaptureEditorLayout();

            if (autoFindSources && (flightDataProvider == null || aircraftController == null || headingTarget == null))
            {
                RefreshDataSources();
            }

            if (Application.isPlaying)
            {
                PlaceInLane();
            }

            UpdateTape(false);

            if (Application.isPlaying)
            {
                ApplyForwardIntensity();
            }
        }

        private void OnTransformParentChanged()
        {
            _canvas = null;
        }

        private void CaptureEditorLayout()
        {
            if (Application.isPlaying)
            {
                return;
            }

            // Once the overlay has been generated, its RectTransforms are the source of
            // truth in edit mode. This keeps ExecuteAlways from undoing Scene view and
            // Inspector edits on every update.
            Transform existingClip = transform.Find("Heading Tape Clip");
            if (existingClip == null)
            {
                return;
            }

            _rectTransform = GetComponent<RectTransform>();
            _clipRect = existingClip as RectTransform;
            bool changed = false;

            if (_rectTransform != null)
            {
                Vector2 currentPosition = _rectTransform.anchoredPosition;
                Vector2 currentSize = _rectTransform.sizeDelta;
                if (!Approximately(anchoredPosition, currentPosition))
                {
                    anchoredPosition = currentPosition;
                    changed = true;
                }

                if (!Approximately(size, currentSize))
                {
                    size = currentSize;
                    changed = true;
                }
            }

            if (_clipRect != null)
            {
                Vector2 currentClipPosition = _clipRect.anchoredPosition;
                if (!IsReasonableClipOffset(currentClipPosition, size))
                {
                    // The clip is an internal scrolling viewport. Moving it hundreds of
                    // pixels away from the overlay detaches the compass labels/ticks from
                    // the heading index, which is easy to do accidentally in Scene view.
                    // Keep legitimate small authoring offsets, but repair detached clips.
                    clipAnchoredPosition = DefaultClipAnchoredPosition;
                    _clipRect.anchoredPosition = DefaultClipAnchoredPosition;
                    changed = true;
                }
                else if (!Approximately(clipAnchoredPosition, currentClipPosition))
                {
                    clipAnchoredPosition = currentClipPosition;
                    changed = true;
                }
            }

#if UNITY_EDITOR
            if (changed)
            {
                ApplyLayoutAndStyle();
                UnityEditor.EditorUtility.SetDirty(this);
            }
#endif
        }

        /// <summary>
        /// Zone Z5 lane: the drawn top edge (TopExtent above the pivot, scaled with the module) sits 790 ref below the screen top.
        /// Play mode only, so edit-mode authoring and the editor tests keep the RectTransform as the source of truth.
        /// </summary>
        private void PlaceInLane()
        {
            if (!useCentreColumnLane || _rectTransform == null)
            {
                return;
            }

            if (_canvas == null)
            {
                _canvas = GetComponentInParent<Canvas>();
            }

            RectTransform root = _canvas != null ? _canvas.rootCanvas.transform as RectTransform : null;
            if (root == null || _rectTransform.parent != root || root.rect.height < 200f)
            {
                return;
            }

            float y = root.rect.height * 0.5f - LaneTopFromScreenTop - TopExtent * EffectiveScale();
            Vector2 target = new Vector2(_rectTransform.anchoredPosition.x, y);
            if ((target - _rectTransform.anchoredPosition).sqrMagnitude > 0.0001f)
            {
                _rectTransform.anchoredPosition = target;
            }

            anchoredPosition = target;
        }

        /// <summary>Reference pixels per local unit (the workspace module scale), used for legibility floors.</summary>
        private float EffectiveScale()
        {
            if (_canvas == null)
            {
                _canvas = GetComponentInParent<Canvas>();
            }

            Canvas canvas = _canvas;
            if (canvas == null)
            {
                return 1f;
            }

            float root = Mathf.Abs(canvas.rootCanvas.transform.lossyScale.y);
            float own = Mathf.Abs(transform.lossyScale.y);
            float scale = root > 1e-6f ? own / root : 1f;
            return float.IsNaN(scale) || scale <= 0.05f ? 1f : scale;
        }

        private void EnsureBuilt()
        {
            _rectTransform = GetComponent<RectTransform>();
            ApplyRootLayout();

            RectTransform clip = GetOrCreateRectChild(transform, "Heading Tape Clip");
            _clipRect = clip;
            if (clip.GetComponent<RectMask2D>() == null)
            {
                clip.gameObject.AddComponent<RectMask2D>();
            }

            _baseline = GetOrCreateImageChild(clip, "Heading Tape Baseline");
            _topRule = GetOrCreateImageChild(transform, "Heading Tape Top Rule");
            _centerTick = GetOrCreateImageChild(transform, "Current Heading Index");
            // Children added by this revision are created in play mode only, so opening the scene in the editor
            // (ExecuteAlways) never adds objects to it. Existing children are reused in both modes.
            _readoutBox = CanCreateRuntimeChild(transform, "Current Heading Box") ? GetOrCreateRectChild(transform, "Current Heading Box") : null;
            string[] boxNames = { "Top", "Bottom", "Left", "Right" };
            for (int i = 0; i < 4; i++)
            {
                _boxLines[i] = _readoutBox != null ? GetOrCreateImageChild(_readoutBox, boxNames[i]) : null;
            }

            _headingReadout = GetOrCreateTextChild(transform, "Current Heading Readout");
            _referenceLabel = CanCreateRuntimeChild(transform, "Heading Reference") ? GetOrCreateTextChild(transform, "Heading Reference") : null;
            EnsureNavigationTargetCue(clip);
            EnsureSelectedHeadingBug();
            EnsureFixedCardinalLabels();

            while (_markers.Count < MarkerCount)
            {
                int index = _markers.Count;
                RectTransform markerRoot = GetOrCreateRectChild(clip, $"Heading Tape Marker {index:00}");
                Image tick = GetOrCreateImageChild(markerRoot, "Tick");
                TMP_Text label = GetOrCreateTextChild(markerRoot, "Label");
                _markers.Add(new CompassMarker(markerRoot, tick, label));
            }

            ApplyLayoutAndStyle();
        }

        private void EnsureNavigationTargetCue(RectTransform clip)
        {
            if (clip == null)
            {
                return;
            }

            // UpdateTape runs every frame (and also in ExecuteAlways edit
            // previews). Once the cue has been built, keep its current
            // active state intact; reactivating it here would make the
            // no-target path toggle the object on and off every frame.
            if (_navigationTargetCue != null)
            {
                return;
            }

            // Keep the cue outside the scrolling/masked tick viewport so an
            // edge-clamped target and its caption stay readable.
            RectTransform cue = GetOrCreateRectChild(transform, "Navigation Target Cue");
            _navigationTargetCue = cue;
            _navigationTargetStem = GetOrCreateImageChild(cue, "Target Stem");
            _navigationTargetArrow = CanCreateRuntimeChild(cue, "Target Arrow") ? GetOrCreateImageChild(cue, "Target Arrow") : null;
            _navigationTargetDiamond = GetOrCreateImageChild(cue, "Target Diamond");
            _navigationTargetLabel = GetOrCreateTextChild(cue, "Target Label");
            _navigationTargetCue.SetAsLastSibling();
            _navigationTargetCue.gameObject.SetActive(false);
        }

        private void EnsureSelectedHeadingBug()
        {
            if (_selectedHeadingBug != null || !CanCreateRuntimeChild(transform, "Selected Heading Bug"))
            {
                return;
            }

            _selectedHeadingBug = GetOrCreateRectChild(transform, "Selected Heading Bug");
            _bugLeft = GetOrCreateImageChild(_selectedHeadingBug, "Left Post");
            _bugRight = GetOrCreateImageChild(_selectedHeadingBug, "Right Post");
            _bugTop = GetOrCreateImageChild(_selectedHeadingBug, "Top Bar");
            _bugValue = GetOrCreateTextChild(_selectedHeadingBug, "Off Scale Value");
            _selectedHeadingBug.gameObject.SetActive(false);
            EnsureOffScaleSelectedHeadingCue();
        }

        /// <summary>Off-scale cue: a filled arrowhead just outside the tape end and a cyan-boxed "HDG nnn" field beyond it.</summary>
        private void EnsureOffScaleSelectedHeadingCue()
        {
            if (_offScaleCue != null || !CanCreateRuntimeChild(transform, "Selected Heading Off Scale"))
            {
                return;
            }

            _offScaleCue = GetOrCreateRectChild(transform, "Selected Heading Off Scale");
            RectTransform arrow = GetOrCreateRectChild(_offScaleCue, "Arrow");
            if (arrow.GetComponent<CanvasRenderer>() == null)
            {
                arrow.gameObject.AddComponent<CanvasRenderer>();
            }

            _offScaleArrow = arrow.GetComponent<FaaHeadingBugArrowGraphic>();
            if (_offScaleArrow == null)
            {
                _offScaleArrow = arrow.gameObject.AddComponent<FaaHeadingBugArrowGraphic>();
            }

            string[] names = { "Field Top", "Field Bottom", "Field Left", "Field Right" };
            for (int i = 0; i < 4; i++)
            {
                _fieldLines[i] = GetOrCreateImageChild(_offScaleCue, names[i]);
            }

            _offScaleValue = GetOrCreateTextChild(_offScaleCue, "Field Value");
            _offScaleSide = 0f;
            _offScaleCue.gameObject.SetActive(false);
        }

        private void ApplyRootLayout()
        {
            if (_rectTransform == null)
            {
                return;
            }

            _rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
            _rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            _rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _rectTransform.anchoredPosition = anchoredPosition;
            _rectTransform.sizeDelta = size;
            _rectTransform.localScale = Vector3.one;
            _rectTransform.localRotation = Quaternion.identity;
            gameObject.SetActive(true);
        }

        private void ApplyLayoutAndStyle()
        {
            ApplyRootLayout();

            if (_clipRect != null)
            {
                if (!IsReasonableClipOffset(clipAnchoredPosition, size))
                {
                    clipAnchoredPosition = DefaultClipAnchoredPosition;
                }

                _clipRect.anchorMin = new Vector2(0.5f, 0.5f);
                _clipRect.anchorMax = new Vector2(0.5f, 0.5f);
                _clipRect.pivot = new Vector2(0.5f, 0.5f);
                _clipRect.anchoredPosition = clipAnchoredPosition;
                _clipRect.sizeDelta = new Vector2(size.x, Mathf.Max(34f, size.y));
                _clipRect.localScale = Vector3.one;
                _clipRect.localRotation = Quaternion.identity;
            }

            if (_topRule != null)
            {
                _topRule.gameObject.SetActive(false);
            }

            ApplyFixedCardinalLabelLayout();

            if (_headingReadout != null)
            {
                RectTransform readout = _headingReadout.rectTransform;
                readout.anchorMin = readout.anchorMax = readout.pivot = new Vector2(0.5f, 0.5f);
                readout.localScale = Vector3.one;
                LayoutReadoutText(_shownValid);
                _headingReadout.fontStyle = FontStyles.Bold;
                _headingReadout.richText = true;
                _shownReadout = int.MinValue;
            }

            if (_referenceLabel != null)
            {
                RectTransform reference = _referenceLabel.rectTransform;
                reference.anchorMin = reference.anchorMax = reference.pivot = new Vector2(0.5f, 0.5f);
                float innerRight = ReadoutBoxWidth * 0.5f - 4f;
                reference.anchoredPosition = new Vector2((ReferenceLeftX + innerRight) * 0.5f, ReadoutBoxCenterY);
                reference.sizeDelta = new Vector2(innerRight - ReferenceLeftX, ReadoutBoxHeight);
                reference.localScale = Vector3.one;
                reference.localRotation = Quaternion.identity;
                _referenceLabel.alignment = TextAlignmentOptions.MidlineLeft;
                _referenceLabel.fontStyle = FontStyles.Bold;
                _referenceLabel.richText = false;
                _referenceLabel.raycastTarget = false;
            }

            if (_readoutBox != null)
            {
                _readoutBox.anchorMin = _readoutBox.anchorMax = _readoutBox.pivot = new Vector2(0.5f, 0.5f);
                _readoutBox.anchoredPosition = new Vector2(0f, ReadoutBoxCenterY);
                _readoutBox.sizeDelta = new Vector2(ReadoutBoxWidth, ReadoutBoxHeight);
                _readoutBox.localScale = Vector3.one;
                _readoutBox.localRotation = Quaternion.identity;
            }

            ConfigureNavigationTargetCueLayout();
            ConfigureSelectedHeadingBugLayout();

            foreach (CompassMarker marker in _markers)
            {
                if (marker.Root == null)
                {
                    continue;
                }

                marker.Root.anchorMin = new Vector2(0.5f, 0.5f);
                marker.Root.anchorMax = new Vector2(0.5f, 0.5f);
                marker.Root.pivot = new Vector2(0.5f, 0.5f);
                marker.Root.sizeDelta = new Vector2(LabelWidth + 4f, 30f);
                marker.Root.localScale = Vector3.one;
                marker.Root.localRotation = Quaternion.identity;

                if (marker.Tick != null)
                {
                    marker.Tick.raycastTarget = false;
                }

                if (marker.Label != null)
                {
                    marker.Label.alignment = TextAlignmentOptions.Midline;
                    marker.Label.fontStyle = FontStyles.Bold;
                    marker.Label.raycastTarget = false;
                    marker.Label.richText = false;
                    RectTransform label = marker.Label.rectTransform;
                    label.anchorMin = label.anchorMax = label.pivot = new Vector2(0.5f, 0.5f);
                    label.sizeDelta = new Vector2(LabelWidth, LabelHeight);
                    label.localScale = Vector3.one;
                }
            }

            // Force colours, geometry and font floors to be re-applied on the next tape update.
            _styledColor = new Color(-1f, -1f, -1f, -1f);
            _styledScale = -1f;
            RestyleIfNeeded(CurrentHudColor());
        }

        /// <summary>Pilot symbology colour; while the Classic style is active, the Classic reference green so only one green is on screen.</summary>
        private Color CurrentHudColor()
        {
            Color primary = hudColor;
            if (Application.isPlaying)
            {
                FaaSpatialWorkspace workspace = FaaSpatialWorkspace.Current;
                primary = workspace != null && workspace.CurrentSymbology == FaaSymbologyVersion.ClassicAnalog && workspace.ClassicHud != null
                    ? workspace.ClassicHud.ReferenceGreen
                    : FaaHudPilotColor.Resolve(hudColor);
            }

            return new Color(primary.r, primary.g, primary.b, 1f);
        }

        /// <summary>
        /// Applies colour, geometry and font floors only when the pilot colour or the module scale changed.
        /// Inner positions are expressed relative to the clip so an authored clip offset keeps ticks on the baseline.
        /// </summary>
        private void RestyleIfNeeded(Color hud)
        {
            float scale = EffectiveScale();
            if (hud == _styledColor && Mathf.Abs(scale - _styledScale) < 0.01f)
            {
                return;
            }

            _styledColor = hud;
            _styledScale = scale;
            float clipY = _clipRect != null ? _clipRect.anchoredPosition.y : clipAnchoredPosition.y;
            Color quiet = new Color(hud.r, hud.g, hud.b, Mathf.Clamp(hudDimColor.a, 0.7f, 1f) * 0.82f);
            float numeralSize = FaaHudStyle.Legible(FaaHudStyle.Data, FaaHudStyle.MinLabel / scale);
            float readoutSize = FaaHudStyle.Legible(FaaHudStyle.Primary, FaaHudStyle.Secondary / scale);
            float captionSize = FaaHudStyle.Legible(FaaHudStyle.Secondary, FaaHudStyle.MinLabel / scale);
            if (Application.isPlaying && _headingReadout != null && _headingReadout.font != null)
            {
                _halo = FaaHudStyle.HaloMaterial(_headingReadout.font);
            }

            ConfigureLine(_baseline, new Vector2(size.x - 32f, 1.5f), new Vector2(0f, BaselineY - clipY), new Color(hud.r, hud.g, hud.b, 0.45f));
            // Index tick hangs under the readout box: BaselineY down to -BottomExtent.
            ConfigureLine(_centerTick, new Vector2(2f, BottomExtent + BaselineY), new Vector2(0f, (BaselineY - BottomExtent) * 0.5f), hud);
            Color box = _shownValid ? hud : FaaHudStyle.Amber;
            ConfigureLine(_boxLines[0], new Vector2(ReadoutBoxWidth, 2f), new Vector2(0f, ReadoutBoxHeight * 0.5f), box);
            ConfigureLine(_boxLines[1], new Vector2(ReadoutBoxWidth, 2f), new Vector2(0f, -ReadoutBoxHeight * 0.5f), box);
            ConfigureLine(_boxLines[2], new Vector2(2f, ReadoutBoxHeight), new Vector2(-ReadoutBoxWidth * 0.5f, 0f), box);
            ConfigureLine(_boxLines[3], new Vector2(2f, ReadoutBoxHeight), new Vector2(ReadoutBoxWidth * 0.5f, 0f), box);

            if (_headingReadout != null)
            {
                _headingReadout.fontSize = readoutSize;
                StyleText(_headingReadout, _shownValid ? hud : FaaHudStyle.Amber);
            }

            if (_referenceLabel != null)
            {
                _referenceLabel.fontSize = FaaHudStyle.Legible(ReferenceLabelSize, FaaHudStyle.MinLabel / scale);
                StyleText(_referenceLabel, hud);
            }

            if (_offScaleValue != null)
            {
                _offScaleValue.fontSize = FaaHudStyle.Legible(OffScaleFieldSize, FaaHudStyle.MinLabel / scale);
                StyleText(_offScaleValue, FaaHudStyle.Cyan);
            }

            if (_offScaleArrow != null)
            {
                _offScaleArrow.color = FaaHudStyle.Cyan;
            }

            for (int i = 0; i < _fieldLines.Length; i++)
            {
                if (_fieldLines[i] != null)
                {
                    _fieldLines[i].color = FaaHudStyle.Cyan;
                }
            }

            if (_navigationTargetLabel != null)
            {
                _navigationTargetLabel.fontSize = captionSize;
                StyleText(_navigationTargetLabel, hud);
            }

            if (_navigationTargetDiamond != null)
            {
                _navigationTargetDiamond.color = hud;
            }

            if (_navigationTargetStem != null)
            {
                _navigationTargetStem.color = hud;
            }

            if (_navigationTargetArrow != null)
            {
                _navigationTargetArrow.color = hud;
            }

            if (_bugValue != null)
            {
                _bugValue.fontSize = captionSize;
                StyleText(_bugValue, FaaHudStyle.Cyan);
            }

            for (int i = 0; i < _markers.Count; i++)
            {
                CompassMarker marker = _markers[i];
                if (marker.Root == null)
                {
                    continue;
                }

                bool major = i < SlotCount && (i * 5) % 10 == 0;
                float tickHeight = major ? 12f : 6f;
                ConfigureLine(marker.Tick, new Vector2(major ? 2f : 1.5f, tickHeight),
                    new Vector2(0f, BaselineY + tickHeight * 0.5f - clipY), major ? hud : quiet);
                if (marker.Label != null)
                {
                    marker.Label.rectTransform.anchoredPosition = new Vector2(0f, LabelY - clipY);
                    marker.Label.fontSize = numeralSize;
                    StyleText(marker.Label, hud);
                }
            }
        }

        private void ConfigureNavigationTargetCueLayout()
        {
            if (_navigationTargetCue == null)
            {
                return;
            }

            _navigationTargetCue.anchorMin = new Vector2(0.5f, 0.5f);
            _navigationTargetCue.anchorMax = new Vector2(0.5f, 0.5f);
            _navigationTargetCue.pivot = new Vector2(0.5f, 0.5f);
            _navigationTargetCue.sizeDelta = new Vector2(24f, 16f);
            _navigationTargetCue.localScale = Vector3.one;
            _navigationTargetCue.localRotation = Quaternion.identity;

            if (_navigationTargetDiamond != null)
            {
                RectTransform diamondRect = _navigationTargetDiamond.rectTransform;
                diamondRect.anchorMin = diamondRect.anchorMax = diamondRect.pivot = new Vector2(0.5f, 0.5f);
                diamondRect.anchoredPosition = new Vector2(0f, BugRowY);
                diamondRect.sizeDelta = new Vector2(8f, 8f);
                diamondRect.localRotation = Quaternion.Euler(0f, 0f, 45f);
                diamondRect.localScale = Vector3.one;
                _navigationTargetDiamond.raycastTarget = false;
            }

            if (_navigationTargetLabel != null)
            {
                RectTransform labelRect = _navigationTargetLabel.rectTransform;
                labelRect.anchorMin = labelRect.anchorMax = labelRect.pivot = new Vector2(0.5f, 0.5f);
                labelRect.anchoredPosition = new Vector2(0f, BugRowY);
                labelRect.sizeDelta = new Vector2(170f, 22f);
                _navigationTargetLabel.alignment = TextAlignmentOptions.Midline;
                _navigationTargetLabel.fontStyle = FontStyles.Bold;
                _navigationTargetLabel.richText = false;
                _navigationTargetLabel.raycastTarget = false;
                _targetIdentifier = null;
            }

            // Keep the current visibility while the layout is reapplied.
            // UpdateNavigationTargetCue owns the show/hide decision.
        }

        private void ConfigureSelectedHeadingBugLayout()
        {
            if (_selectedHeadingBug == null)
            {
                return;
            }

            _selectedHeadingBug.anchorMin = _selectedHeadingBug.anchorMax = _selectedHeadingBug.pivot = new Vector2(0.5f, 0.5f);
            _selectedHeadingBug.sizeDelta = new Vector2(16f, 10f);
            _selectedHeadingBug.localScale = Vector3.one;
            _selectedHeadingBug.localRotation = Quaternion.identity;
            ConfigureLine(_bugLeft, new Vector2(2f, 9f), new Vector2(-6f, -0.5f), FaaHudStyle.Cyan);
            ConfigureLine(_bugRight, new Vector2(2f, 9f), new Vector2(6f, -0.5f), FaaHudStyle.Cyan);
            ConfigureLine(_bugTop, new Vector2(14f, 2f), new Vector2(0f, 4f), FaaHudStyle.Cyan);
            if (_bugValue != null)
            {
                // Retired: an off-scale value next to a remaining post read as '1084' or '084r'. The off-scale field replaces it.
                SetActive(_bugValue.gameObject, false);
            }

            ConfigureOffScaleCueLayout();
        }

        private void ConfigureOffScaleCueLayout()
        {
            if (_offScaleCue == null)
            {
                return;
            }

            _offScaleCue.anchorMin = _offScaleCue.anchorMax = _offScaleCue.pivot = new Vector2(0.5f, 0.5f);
            _offScaleCue.anchoredPosition = Vector2.zero;
            _offScaleCue.sizeDelta = Vector2.zero;
            _offScaleCue.localScale = Vector3.one;
            _offScaleCue.localRotation = Quaternion.identity;
            if (_offScaleValue != null)
            {
                RectTransform value = _offScaleValue.rectTransform;
                value.anchorMin = value.anchorMax = value.pivot = new Vector2(0.5f, 0.5f);
                value.sizeDelta = new Vector2(OffScaleFieldWidth - 6f, OffScaleFieldHeight);
                value.localScale = Vector3.one;
                _offScaleValue.alignment = TextAlignmentOptions.Midline;
                _offScaleValue.fontStyle = FontStyles.Bold;
                _offScaleValue.richText = false;
                _offScaleValue.raycastTarget = false;
            }

            if (_offScaleArrow != null)
            {
                RectTransform arrow = _offScaleArrow.rectTransform;
                arrow.anchorMin = arrow.anchorMax = arrow.pivot = new Vector2(0.5f, 0.5f);
                arrow.sizeDelta = new Vector2(OffScaleArrowLength, OffScaleArrowHalfHeight * 2f);
                arrow.localScale = Vector3.one;
                arrow.localRotation = Quaternion.identity;
                _offScaleArrow.raycastTarget = false;
            }

            _offScaleSide = 0f;
        }

        /// <summary>Places the off-scale arrowhead and field on one side (only when the side changes).</summary>
        private void LayoutOffScaleCue(float side, float halfWidth)
        {
            if (_offScaleCue == null || side == _offScaleSide)
            {
                return;
            }

            _offScaleSide = side;
            float arrowX = side * (halfWidth + OffScaleArrowInset + OffScaleArrowLength * 0.5f);
            float fieldX = side * (halfWidth - OffScaleFieldWidth * 0.5f);
            float fieldY = OffScaleFieldCenterY;
            if (_offScaleArrow != null)
            {
                _offScaleArrow.rectTransform.anchoredPosition = new Vector2(arrowX, LabelY);
                _offScaleArrow.Direction = side;
            }

            if (_offScaleValue != null)
            {
                _offScaleValue.rectTransform.anchoredPosition = new Vector2(fieldX, fieldY);
            }

            ConfigureLine(_fieldLines[0], new Vector2(OffScaleFieldWidth, 2f), new Vector2(fieldX, fieldY + OffScaleFieldHeight * 0.5f), FaaHudStyle.Cyan);
            ConfigureLine(_fieldLines[1], new Vector2(OffScaleFieldWidth, 2f), new Vector2(fieldX, fieldY - OffScaleFieldHeight * 0.5f), FaaHudStyle.Cyan);
            ConfigureLine(_fieldLines[2], new Vector2(2f, OffScaleFieldHeight), new Vector2(fieldX - OffScaleFieldWidth * 0.5f, fieldY), FaaHudStyle.Cyan);
            ConfigureLine(_fieldLines[3], new Vector2(2f, OffScaleFieldHeight), new Vector2(fieldX + OffScaleFieldWidth * 0.5f, fieldY), FaaHudStyle.Cyan);
        }

        /// <summary>Valid: digits right-aligned in the left of the box, reference label to their right. Invalid: the amber flag centred in the box.</summary>
        private void LayoutReadoutText(bool valid)
        {
            if (_headingReadout == null)
            {
                return;
            }

            RectTransform readout = _headingReadout.rectTransform;
            float innerLeft = -ReadoutBoxWidth * 0.5f + 4f;
            Vector2 position = valid ? new Vector2((innerLeft + DigitsRightX) * 0.5f, ReadoutBoxCenterY) : new Vector2(0f, ReadoutBoxCenterY);
            Vector2 sizeDelta = valid ? new Vector2(DigitsRightX - innerLeft, ReadoutBoxHeight) : new Vector2(ReadoutBoxWidth - 4f, ReadoutBoxHeight);
            TextAlignmentOptions alignment = valid ? TextAlignmentOptions.MidlineRight : TextAlignmentOptions.Midline;
            if (readout.anchoredPosition != position) readout.anchoredPosition = position;
            if (readout.sizeDelta != sizeDelta) readout.sizeDelta = sizeDelta;
            if (_headingReadout.alignment != alignment) _headingReadout.alignment = alignment;
        }

        private void EnsureFixedCardinalLabels()
        {
            string[] names = { "Fixed Cardinal W", "Fixed Cardinal N", "Fixed Cardinal E", "Fixed Cardinal S" };
            while (_fixedCardinalLabels.Count < names.Length)
            {
                TMP_Text label = GetOrCreateTextChild(transform, names[_fixedCardinalLabels.Count]);
                _fixedCardinalLabels.Add(label);
            }
        }

        private void ApplyFixedCardinalLabelLayout()
        {
            // Retired fixed cardinal letters: one cardinal representation per heading (on the moving card).
            for (int i = 0; i < _fixedCardinalLabels.Count; i++)
            {
                TMP_Text label = _fixedCardinalLabels[i];
                if (label != null && (label.gameObject.activeSelf || label.enabled))
                {
                    DisableTextGraphic(label);
                }
            }
        }

        private void UpdateTape(bool immediate)
        {
            if (_rectTransform == null || _markers.Count != MarkerCount)
                EnsureBuilt();

            float targetHeading = ReadHeading();
            if (immediate || smoothing <= 0f)
            {
                _displayedHeading = targetHeading;
            }
            else
            {
                // Light filter only: the bridge already interpolates between packets. Time constant <= ~30 ms (AC 25-11B latency).
                float perFrame = Mathf.Max(smoothing, 0.45f);
                float lerpFactor = 1f - Mathf.Pow(1f - perFrame, Time.unscaledDeltaTime * 60f);
                _displayedHeading = Mathf.LerpAngle(_displayedHeading, targetHeading, lerpFactor);
            }

            _displayedHeading = Normalize360(_displayedHeading);
            bool valid = EvaluateHeadingValidity(out FaaAnalogFlightSample sample, out bool hasSample);
            Color hud = CurrentHudColor();
            if (valid != _shownValid)
            {
                _shownValid = valid;
                _shownReadout = int.MinValue;
                _styledColor = new Color(-1f, -1f, -1f, -1f);
            }

            if (ExternallyRecoloured(hud))
            {
                // A palette pass elsewhere (SymbologyColorManager) rewrote the tape's colours, e.g. with the pilot colour while the
                // Classic reference green applies: restyle once. RGB only, so an opacity pass never causes a per-frame restyle.
                _styledColor = new Color(-1f, -1f, -1f, -1f);
            }

            RestyleIfNeeded(hud);
            ApplyFixedCardinalLabelLayout();
            UpdateReadout(valid, hud);

            float halfWidth = size.x * 0.5f;
            // Keep the opposite cardinal outside the mask: roughly 90 degrees, never the whole compass compressed into one strip.
            float effectivePixelsPerDegree = Mathf.Min(pixelsPerDegree, GetMaximumPixelsPerDegree(size.x));
            float boxHalf = ReadoutBoxWidth * 0.5f;

            for (int i = 0; i < _markers.Count; i++)
            {
                CompassMarker marker = _markers[i];
                if (marker.Root == null)
                {
                    continue;
                }

                if (!valid || i >= SlotCount)
                {
                    SetActive(marker.Root.gameObject, false);
                    continue;
                }

                // Fixed slots: marker i always shows i*5 degrees, so its text never changes while the card scrolls.
                int markerDegrees = i * 5;
                float x = Mathf.DeltaAngle(_displayedHeading, markerDegrees) * effectivePixelsPerDegree;
                bool visible = Mathf.Abs(x) <= halfWidth - 22f;
                SetActive(marker.Root.gameObject, visible);
                if (!visible)
                {
                    continue;
                }

                Vector2 position = new Vector2(x, 0f);
                if ((marker.Root.anchoredPosition - position).sqrMagnitude > 0.000001f)
                {
                    marker.Root.anchoredPosition = position;
                }

                if (marker.Tick != null)
                {
                    // Ticks never run through the readout box.
                    bool tickVisible = Mathf.Abs(x) >= boxHalf + 1f;
                    if (marker.Tick.enabled != tickVisible)
                    {
                        marker.Tick.enabled = tickVisible;
                    }
                }

                if (marker.Label != null)
                {
                    bool labeled = markerDegrees % 10 == 0 && Mathf.Abs(x) >= boxHalf + LabelWidth * 0.5f + 2f;
                    if (!labeled)
                    {
                        SetActive(marker.Label.gameObject, false);
                        continue;
                    }

                    EnsureTextVisible(marker.Label);
                    if (_halo != null && marker.Label.fontSharedMaterial != _halo)
                    {
                        // A palette refresh elsewhere may swap in a material instance; keep the shared halo.
                        marker.Label.fontSharedMaterial = _halo;
                        marker.Label.UpdateMeshPadding();
                    }

                    string label = MarkerLabels[markerDegrees / 10];
                    if (marker.Label.text != label)
                    {
                        marker.Label.text = label;
                    }
                }
            }

            UpdateSelectedHeadingBug(sample, hasSample && valid, effectivePixelsPerDegree, halfWidth);
            UpdateNavigationTargetCue(effectivePixelsPerDegree, halfWidth, valid);
        }

        /// <summary>
        /// With an X-Plane bridge present, heading follows the bridge validity (fresh feed and valid attitude/heading).
        /// Without a bridge (local data source or tests) the configured local sources are used as before.
        /// </summary>
        private bool EvaluateHeadingValidity(out FaaAnalogFlightSample sample, out bool hasSample)
        {
            sample = default;
            hasSample = false;
            if (!Application.isPlaying || !autoFindSources || !FaaHudLiveSample.TryGet(out sample))
            {
                return true;
            }

            hasSample = true;
            return sample.Fresh && sample.AttitudeValid;
        }

        private bool ExternallyRecoloured(Color hud)
        {
            if (_headingReadout == null || _shownReadout == int.MinValue)
            {
                return false;
            }

            Color expected = _shownValid ? hud : FaaHudStyle.Amber;
            Color actual = _headingReadout.color;
            return Mathf.Abs(actual.r - expected.r) > 0.004f || Mathf.Abs(actual.g - expected.g) > 0.004f || Mathf.Abs(actual.b - expected.b) > 0.004f;
        }

        private void UpdateReadout(bool valid, Color hud)
        {
            if (_headingReadout == null)
            {
                return;
            }

            EnsureTextVisible(_headingReadout);
            if (_referenceLabel != null)
            {
                // The reference belongs to a valid heading; the invalid flag stands alone.
                if (valid)
                {
                    EnsureTextVisible(_referenceLabel);
                    if (_referenceLabel.text != TrueReferenceText)
                    {
                        _referenceLabel.text = TrueReferenceText;
                    }
                }
                else
                {
                    SetActive(_referenceLabel.gameObject, false);
                }
            }

            int rounded = Mathf.RoundToInt(_displayedHeading) % 360;
            int shown = valid ? (rounded == 0 ? 360 : rounded) : -1;
            if (shown != _shownReadout || string.IsNullOrEmpty(_headingReadout.text))
            {
                _shownReadout = shown;
                LayoutReadoutText(valid);
                _headingReadout.text = valid ? ReadoutString(shown) : InvalidHeadingFlag;
                Color c = valid ? hud : FaaHudStyle.Amber;
                StyleText(_headingReadout, c);
                for (int i = 0; i < _boxLines.Length; i++)
                {
                    if (_boxLines[i] != null)
                    {
                        _boxLines[i].color = c;
                    }
                }
            }
        }

        /// <summary>
        /// Selected-heading bug (AFCS heading select, degrees magnetic) converted to the TRUE card, drawn only while the active lateral
        /// mode is HDG and the magnetic variation is measured.
        /// </summary>
        private void UpdateSelectedHeadingBug(FaaAnalogFlightSample sample, bool hasSample, float pixelsPerDeg, float halfWidth)
        {
            bool show = hasSample && sample.Fresh && sample.SelectedHeadingValid && FaaHudLiveSample.MagneticVariationValid;
            if (show)
            {
                FaaFma fma = FaaFlightModeAnnunciation.Compose(sample);
                show = fma.Engaged && fma.Lateral == "HDG";
            }

            PresentSelectedHeading(show, show ? Normalize360(sample.SelectedHeadingMag + sample.MagneticVariation) : 0f, pixelsPerDeg, halfWidth);
        }

        /// <summary>
        /// Draws the selected-heading cue for a selected heading on the card's (TRUE) reference. On scale: the cyan bug in the bug row above
        /// the numerals. Off scale (AC 25-11B: show it as off-scale with its value): the bug is replaced by a filled cyan arrowhead just
        /// outside the tape end, pointing toward the selected heading, and a cyan-boxed "HDG nnn" field under that end of the tape, more than
        /// 1 em from the arrowhead and 16 ref below the baseline, so it can never fuse with the bug or the numerals. Public for deterministic tests.
        /// </summary>
        public void PresentSelectedHeading(bool show, float selectedHeadingTrue, float pixelsPerDeg, float halfWidth)
        {
            _bugShown = show && _selectedHeadingBug != null;
            if (!_bugShown)
            {
                _bugOffScale = false;
                if (_selectedHeadingBug != null) SetActive(_selectedHeadingBug.gameObject, false);
                if (_offScaleCue != null) SetActive(_offScaleCue.gameObject, false);
                return;
            }

            float bugTrue = Normalize360(selectedHeadingTrue);
            float x = Mathf.DeltaAngle(_displayedHeading, bugTrue) * pixelsPerDeg;
            float edge = halfWidth - 8f;
            bool offScale = Mathf.Abs(x) > edge;
            float side = x < 0f ? -1f : 1f;
            _bugOffScale = offScale;
            _bugX = Mathf.Clamp(x, -edge, edge);
            SetActive(_selectedHeadingBug.gameObject, !offScale);
            if (_bugValue != null) SetActive(_bugValue.gameObject, false);
            if (!offScale)
            {
                Vector2 position = new Vector2(_bugX, BugRowY);
                if ((_selectedHeadingBug.anchoredPosition - position).sqrMagnitude > 0.000001f)
                {
                    _selectedHeadingBug.anchoredPosition = position;
                }

                SetEnabled(_bugLeft, true);
                SetEnabled(_bugRight, true);
            }

            if (_offScaleCue == null)
            {
                return;
            }

            SetActive(_offScaleCue.gameObject, offScale);
            if (!offScale)
            {
                return;
            }

            LayoutOffScaleCue(side, halfWidth);
            for (int i = 0; i < _fieldLines.Length; i++)
            {
                // Selected values stay cyan even if a palette pass recoloured the tape's images.
                if (_fieldLines[i] != null && _fieldLines[i].color != FaaHudStyle.Cyan)
                {
                    _fieldLines[i].color = FaaHudStyle.Cyan;
                }
            }

            if (_offScaleValue != null)
            {
                EnsureTextVisible(_offScaleValue);
                if (_offScaleValue.color != FaaHudStyle.Cyan)
                {
                    StyleText(_offScaleValue, FaaHudStyle.Cyan);
                }

                int value = Mathf.RoundToInt(bugTrue) % 360;
                string text = SelectedHeadingString(value == 0 ? 360 : value);
                if (_offScaleValue.text != text)
                {
                    _offScaleValue.text = text;
                }
            }
        }

        private void UpdateNavigationTargetCue(float effectivePixelsPerDegree, float halfWidth, bool headingValid)
        {
            if (_navigationTargetCue == null)
            {
                return;
            }

            if (navigationDisplay == null && autoFindSources)
            {
                navigationDisplay = FindNavigationDisplay();
            }

            if (!headingValid ||
                navigationDisplay == null ||
                !navigationDisplay.HasNavigationTarget ||
                !navigationDisplay.ShowNavigationTarget)
            {
                SetActive(_navigationTargetCue.gameObject, false);
                return;
            }

            RadarNavigationTarget target = navigationDisplay.CurrentNavigationTarget;
            float targetDelta = Mathf.DeltaAngle(_displayedHeading, target.BearingDegrees);
            float targetX = targetDelta * effectivePixelsPerDegree;
            float edgePadding = 13f;
            bool clamped = Mathf.Abs(targetX) > halfWidth - edgePadding;
            float cueX = Mathf.Clamp(targetX, -halfWidth + edgePadding, halfWidth - edgePadding);
            float side = targetX < 0f ? -1f : 1f;
            SetActive(_navigationTargetCue.gameObject, true);
            Vector2 cuePosition = new Vector2(cueX, 0f);
            if ((_navigationTargetCue.anchoredPosition - cuePosition).sqrMagnitude > 0.000001f)
            {
                _navigationTargetCue.anchoredPosition = cuePosition;
            }

            // On scale: a filled diamond in the bug row. Off scale: an arrowhead at the tape edge pointing toward the
            // target (changed appearance, same HUD colour; amber stays reserved for cautions).
            SetEnabled(_navigationTargetDiamond, !clamped);
            SetEnabled(_navigationTargetStem, clamped);
            SetEnabled(_navigationTargetArrow, clamped);
            if (clamped)
            {
                ConfigureStroke(_navigationTargetStem, new Vector2(side * 5f, BugRowY), new Vector2(-side * 3f, BugRowY + 5f));
                ConfigureStroke(_navigationTargetArrow, new Vector2(side * 5f, BugRowY), new Vector2(-side * 3f, BugRowY - 5f));
            }

            if (_navigationTargetLabel != null)
            {
                EnsureTextVisible(_navigationTargetLabel);
                string identifier = string.IsNullOrWhiteSpace(target.Identifier) ? "TGT" : target.Identifier;
                int bearing = Mathf.RoundToInt(Normalize360(target.BearingDegrees)) % 360;
                int tenths = Mathf.RoundToInt(Mathf.Max(0f, target.DistanceNM) * 10f);
                if (identifier != _targetIdentifier || bearing != _targetBearing || tenths != _targetDistanceTenths)
                {
                    _targetIdentifier = identifier;
                    _targetBearing = bearing;
                    _targetDistanceTenths = tenths;
                    string distance = tenths >= 100 ? (tenths / 10).ToString() : (tenths / 10f).ToString("0.0");
                    _navigationTargetLabel.text = identifier + "  " + DegreeString(bearing == 0 ? 360 : bearing) + "°  " + distance + "NM";
                    _targetLabelHalfWidth = Mathf.Min(_navigationTargetLabel.GetPreferredValues(_navigationTargetLabel.text).x * 0.5f + 2f,
                        _navigationTargetLabel.rectTransform.rect.width * 0.5f);
                }

                // Caption beside the cue (never covering it), on the side toward the tape centre, and away from the selected-heading bug.
                float labelHalfWidth = _targetLabelHalfWidth;
                float toward = cueX > 0f ? -1f : 1f;
                float centre = cueX + toward * (9f + labelHalfWidth);
                if (_bugShown && !_bugOffScale && Mathf.Abs(_bugX - centre) < labelHalfWidth + 10f)
                {
                    centre = cueX - toward * (9f + labelHalfWidth);
                }

                float labelX = centre + GetNavigationLabelOffset(centre, halfWidth, labelHalfWidth) - cueX;
                Vector2 labelPosition = new Vector2(labelX, BugRowY);
                if ((_navigationTargetLabel.rectTransform.anchoredPosition - labelPosition).sqrMagnitude > 0.000001f)
                {
                    _navigationTargetLabel.rectTransform.anchoredPosition = labelPosition;
                }
            }
        }

        public static float GetNavigationLabelOffset(float cueX, float halfWidth, float labelHalfWidth)
        {
            float limit = Mathf.Max(0f, halfWidth - labelHalfWidth);
            return Mathf.Clamp(cueX, -limit, limit) - cueX;
        }

        /// <summary>
        /// Dims the tape while a side panel is inspected (full intensity for the instrument previewed from Settings). The current-heading
        /// readout, its box and its reference label are awareness readouts and keep the awareness floor. The overlay root's CanvasGroup
        /// belongs to the Digital style gate.
        /// </summary>
        private void ApplyForwardIntensity()
        {
            if (_intensityGroups.Count == 0)
            {
                AddIntensityGroup(_intensityGroups, _clipRect);
                AddIntensityGroup(_intensityGroups, _centerTick != null ? _centerTick.rectTransform : null);
                AddIntensityGroup(_intensityGroups, _selectedHeadingBug);
                AddIntensityGroup(_intensityGroups, _navigationTargetCue);
                AddIntensityGroup(_intensityGroups, _offScaleCue);
                AddIntensityGroup(_awarenessGroups, _headingReadout != null ? _headingReadout.rectTransform : null);
                AddIntensityGroup(_awarenessGroups, _readoutBox);
                AddIntensityGroup(_awarenessGroups, _referenceLabel != null ? _referenceLabel.rectTransform : null);
                _appliedIntensity = -1f;
                _appliedAwareness = -1f;
            }

            float k = FaaSpatialWorkspace.ForwardIntensityFor(transform);
            float awareness = Mathf.Max(k, FaaHudInspection.AwarenessHudIntensity);
            if (Mathf.Abs(k - _appliedIntensity) > 0.002f)
            {
                _appliedIntensity = k;
                SetGroupAlpha(_intensityGroups, k);
            }

            if (Mathf.Abs(awareness - _appliedAwareness) > 0.002f)
            {
                _appliedAwareness = awareness;
                SetGroupAlpha(_awarenessGroups, awareness);
            }
        }

        private static void SetGroupAlpha(List<CanvasGroup> groups, float alpha)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] != null)
                {
                    groups[i].alpha = alpha;
                }
            }
        }

        private static void AddIntensityGroup(List<CanvasGroup> groups, RectTransform target)
        {
            if (target == null)
            {
                return;
            }

            CanvasGroup group = target.GetComponent<CanvasGroup>();
            if (group == null)
            {
                group = target.gameObject.AddComponent<CanvasGroup>();
            }

            group.interactable = false;
            group.blocksRaycasts = false;
            groups.Add(group);
        }

        private void RefreshDataSources()
        {
            if (!autoFindSources)
            {
                return;
            }

            if (flightDataProvider == null)
            {
                flightDataProvider = FindAnyObjectByType<AviationFlightDataProvider>(FindObjectsInactive.Include);
            }

            if (aircraftController == null)
            {
                aircraftController = FindAnyObjectByType<AircraftController>(FindObjectsInactive.Include);
            }

            if (headingHud == null)
            {
                headingHud = FindAnyObjectByType<global::HeadingHUD>(FindObjectsInactive.Include);
            }

            if (navigationDisplay == null)
            {
                navigationDisplay = FindNavigationDisplay();
            }

            if (headingTarget == null && aircraftController != null)
            {
                headingTarget = aircraftController.transform;
            }

            if (headingTarget == null && Camera.main != null)
            {
                headingTarget = Camera.main.transform;
            }
        }

        private float ReadHeading()
        {
            if (aircraftController != null && aircraftController.State != null)
            {
                return Normalize360(aircraftController.State.Heading);
            }

            if (headingHud != null)
            {
                return Normalize360(headingHud.GetCurrentHeading());
            }

            if (flightDataProvider != null && flightDataProvider.FlightData != null)
            {
                return Normalize360(flightDataProvider.FlightData.heading);
            }

            if (headingTarget != null)
            {
                Vector3 forward = headingTarget.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 0.0001f)
                {
                    return Normalize360(Quaternion.LookRotation(forward).eulerAngles.y);
                }
            }

            return Normalize360(_displayedHeading);
        }

        /// <summary>
        /// ExperimentScene contains a disabled legacy radar beside the active
        /// XR-3 radar. Prefer the active instance so the heading cue follows
        /// the display the pilot can actually see.
        /// </summary>
        private static TrafficRadarDisplay FindNavigationDisplay()
        {
            TrafficRadarDisplay active = FindAnyObjectByType<TrafficRadarDisplay>();
            return active != null
                ? active
                : FindAnyObjectByType<TrafficRadarDisplay>(FindObjectsInactive.Include);
        }

        private static bool CanCreateRuntimeChild(Transform parent, string childName)
        {
            return Application.isPlaying || parent.Find(childName) != null;
        }

        private static RectTransform GetOrCreateRectChild(Transform parent, string childName)
        {
            Transform existing = parent.Find(childName);
            GameObject child = existing != null ? existing.gameObject : new GameObject(childName, typeof(RectTransform));
            child.transform.SetParent(parent, false);
            child.SetActive(true);

            RectTransform rect = child.GetComponent<RectTransform>();
            if (rect == null)
            {
                rect = child.AddComponent<RectTransform>();
            }

            return rect;
        }

        private static Image GetOrCreateImageChild(Transform parent, string childName)
        {
            RectTransform rect = GetOrCreateRectChild(parent, childName);
            Image image = rect.GetComponent<Image>();
            if (image == null)
            {
                image = rect.gameObject.AddComponent<Image>();
            }

            image.sprite = null;
            image.type = Image.Type.Simple;
            image.raycastTarget = false;
            image.enabled = true;
            return image;
        }

        private static TMP_Text GetOrCreateTextChild(Transform parent, string childName)
        {
            RectTransform rect = GetOrCreateRectChild(parent, childName);
            TextMeshProUGUI text = rect.GetComponent<TextMeshProUGUI>();
            if (text == null)
            {
                text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            }

            text.raycastTarget = false;
            text.enabled = true;
            if (text.font == null && TMP_Settings.defaultFontAsset != null)
            {
                text.font = TMP_Settings.defaultFontAsset;
            }

            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            text.enableAutoSizing = false;
            return text;
        }

        private static void DisableTextGraphic(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            text.text = string.Empty;
            text.enabled = false;
            text.raycastTarget = false;
            text.canvasRenderer.SetAlpha(0f);
            text.canvasRenderer.Clear();
            text.gameObject.SetActive(false);
        }

        /// <summary>Cheap per-frame guarantee that a shown label is active, enabled, unculled and opaque.</summary>
        private static void EnsureTextVisible(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            if (!text.gameObject.activeSelf)
            {
                text.gameObject.SetActive(true);
            }

            if (!text.enabled)
            {
                text.enabled = true;
            }

            CanvasRenderer renderer = text.canvasRenderer;
            if (renderer.cull)
            {
                renderer.cull = false;
            }

            Color tint = renderer.GetColor();
            if (tint.r < 0.999f || tint.g < 0.999f || tint.b < 0.999f || tint.a < 0.999f)
            {
                // TMP bakes its colour into the mesh; a retained renderer tint would multiply it.
                renderer.SetColor(Color.white);
            }
        }

        /// <summary>Colour (alpha floor 0.85) and, in play mode, the shared halo material. Assigned only when different.</summary>
        private void StyleText(TMP_Text text, Color color)
        {
            if (text == null)
            {
                return;
            }

            if (text.font == null && TMP_Settings.defaultFontAsset != null)
            {
                text.font = TMP_Settings.defaultFontAsset;
            }

            Color target = FaaHudStyle.WithAlpha(color, Mathf.Max(color.a, FaaHudStyle.MinTextAlpha));
            if (text.color != target)
            {
                text.color = target;
            }

            if (text.enableVertexGradient)
            {
                text.enableVertexGradient = false;
            }

            if (_halo != null && text.fontSharedMaterial != _halo)
            {
                text.fontSharedMaterial = _halo;
                text.UpdateMeshPadding();
            }
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }

        private static void SetEnabled(Behaviour target, bool enabled)
        {
            if (target != null && target.enabled != enabled)
            {
                target.enabled = enabled;
            }
        }

        private static void ConfigureLine(Image image, Vector2 lineSize, Vector2 anchored, Color color)
        {
            if (image == null)
            {
                return;
            }

            RectTransform rect = image.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = lineSize;
            rect.anchoredPosition = anchored;
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            image.color = color;
            image.raycastTarget = false;
        }

        /// <summary>Positions a thin Image as a 2-unit stroke from a to b (parent-local).</summary>
        private static void ConfigureStroke(Image image, Vector2 a, Vector2 b)
        {
            if (image == null)
            {
                return;
            }

            Vector2 d = b - a;
            RectTransform rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            Vector2 sizeDelta = new Vector2(d.magnitude, 2f);
            Vector2 centre = (a + b) * 0.5f;
            Quaternion rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            if (rect.sizeDelta != sizeDelta) rect.sizeDelta = sizeDelta;
            if (rect.anchoredPosition != centre) rect.anchoredPosition = centre;
            if (rect.localRotation != rotation) rect.localRotation = rotation;
        }

        private static string[] BuildMarkerLabels()
        {
            var labels = new string[36];
            for (int i = 0; i < 36; i++)
            {
                labels[i] = i == 0 ? "N" : i == 9 ? "E" : i == 18 ? "S" : i == 27 ? "W" : i.ToString("00");
            }

            return labels;
        }

        /// <summary>Readout digits only; the reference is its own legible label (<see cref="TrueReferenceText"/>). North reads 360 (AC 25-11B).</summary>
        private static string ReadoutString(int degrees)
        {
            if (_readoutStrings == null)
            {
                _readoutStrings = new string[361];
            }

            degrees = Mathf.Clamp(degrees, 0, 360);
            return _readoutStrings[degrees] ??= "<mspace=0.6em>" + degrees.ToString("000") + "</mspace>";
        }

        /// <summary>Off-scale selected-heading field text, e.g. "HDG 084" (same reference as the card, annunciated once at the readout).</summary>
        public static string SelectedHeadingString(int degrees)
        {
            if (_selectedHeadingStrings == null)
            {
                _selectedHeadingStrings = new string[361];
            }

            degrees = Mathf.Clamp(degrees, 0, 360);
            return _selectedHeadingStrings[degrees] ??= SelectedHeadingPrefix + degrees.ToString("000");
        }

        private static string DegreeString(int degrees)
        {
            if (_degreeStrings == null)
            {
                _degreeStrings = new string[361];
            }

            degrees = Mathf.Clamp(degrees, 0, 360);
            return _degreeStrings[degrees] ??= degrees.ToString("000");
        }

        private static float Normalize360(float degrees)
        {
            degrees %= 360f;
            if (degrees < 0f)
            {
                degrees += 360f;
            }

            return degrees;
        }

        private static bool Approximately(Vector2 left, Vector2 right)
        {
            return (left - right).sqrMagnitude < 0.0001f;
        }

        private static bool IsReasonableClipOffset(Vector2 offset, Vector2 overlaySize)
        {
            float maxHorizontalOffset = Mathf.Max(32f, Mathf.Abs(overlaySize.x) * 0.25f);
            float maxVerticalOffset = Mathf.Max(24f, Mathf.Abs(overlaySize.y));
            return Mathf.Abs(offset.x) <= maxHorizontalOffset && Mathf.Abs(offset.y) <= maxVerticalOffset;
        }

        private static float GetMaximumPixelsPerDegree(float overlayWidth)
        {
            // Show roughly 100 degrees, not the entire compass compressed into
            // one strip. A narrower sweep preserves meaningful tick spacing.
            return Mathf.Max(1.1f, (Mathf.Abs(overlayWidth) - 32f) / 90f);
        }

        private readonly struct CompassMarker
        {
            public CompassMarker(RectTransform root, Image tick, TMP_Text label)
            {
                Root = root;
                Tick = tick;
                Label = label;
            }

            public RectTransform Root { get; }
            public Image Tick { get; }
            public TMP_Text Label { get; }
        }
    }

    /// <summary>Filled arrowhead with a dark halo, pointing along +x (Direction 1) or -x (Direction -1). Off-scale selected-heading cue.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaHeadingBugArrowGraphic : MaskableGraphic
    {
        private static readonly Color Halo = new Color(0f, 0f, 0f, 0.5f);
        private float _direction = 1f;

        public float Direction
        {
            get => _direction;
            set
            {
                float direction = value < 0f ? -1f : 1f;
                if (direction == _direction)
                {
                    return;
                }

                _direction = direction;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            Vector2 c = r.center;
            float hx = r.width * 0.5f, hy = r.height * 0.5f;
            Vector2 tip = c + new Vector2(_direction * hx, 0f);
            Vector2 upper = c + new Vector2(-_direction * hx, hy);
            Vector2 lower = c + new Vector2(-_direction * hx, -hy);
            FaaAnalogVector.HollowTriangle(vh, tip, upper, lower, 2.4f, new Color(Halo.r, Halo.g, Halo.b, Halo.a * color.a));
            FaaAnalogVector.Triangle(vh, tip, upper, lower, color);
        }
    }
}
