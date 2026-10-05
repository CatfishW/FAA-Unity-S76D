using System.Collections.Generic;
using AviationUI;
using FAA.XPlaneIntegration.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Research/simulator presentation, NOT an FAA-approved flight instrument.
    /// Angular cues are drawn on a distant world-space shell; geographic cues keep their actual
    /// scene depth. Both eyes therefore use Unity's native view/projection, not a mono overlay.
    /// The finite angular shell approximates optical collimation; XR hardware calibration is required.
    ///
    /// Human-factors rules (AC 25-11B 5.10, F.3.3.3, F.4.4-F.4.5, F.5.4.6; HF-STD-001B 5.3.3.2):
    /// - the pitch ladder, its numerals and the selected-FPA reference live only inside a screen-fixed attitude window that is built
    ///   every frame around the projected aircraft reference and cleared of the protected HUD areas (FaaHudKeepOut), so they never
    ///   cross IAS/TQ, ALT/VSI/NR, the FMA, the heading scale, the roll-scale ends or chrome; the wide horizon is gapped around them;
    /// - the waterline comes from validated pitch/roll/heading, never from the OwnAircraft transform, and is never dropped: outside the
    ///   window it is pinned just inside the nearest edge in a dashed, ghosted 'limited' style (W LIMIT);
    /// - rungs are drawn as complete pairs (both halves and both numerals) or not at all; rungs, horizon and FPA break around the
    ///   waterline and the FPV, and the waterline breaks under the FPV, so no two symbols merge;
    /// - unusual attitude declutters (with hysteresis) to horizon, ladder, waterline and red recovery chevrons; an off-screen horizon
    ///   is clamped to the window edge, dashed, with a caret toward the true horizon;
    /// - invalid attitude shows a red boxed ATT flag; a FOV-limited FPV is drawn ghosted (dashed) at the window edge;
    /// - text and strokes are sized by visual angle at the eye (28 arcmin text, about 1 mrad lines) for desktop and XR alike;
    /// - every alpha is multiplied by FaaSpatialWorkspace.ForwardIntensityFor (the shared inspection dimming); nothing allocates per
    ///   frame and the mesh is rebuilt only when an input changes.
    /// </summary>
    [DefaultExecutionOrder(12100), DisallowMultipleComponent]
    [AddComponentMenu("FAA/HUD/Rotorcraft Conformal Layer")]
    public sealed class FaaRotorcraftConformalLayer : MonoBehaviour
    {
        private enum Zone { Window, KeepOut, Flag }

        [Header("References")]
        [SerializeField] private Camera view;
        [SerializeField] private Transform aircraft;
        [SerializeField] private XPlane12ApiHudBridge flightData;
        [Tooltip("Optional local tangent frame: X east, Y up, Z true north. Defaults to Unity world axes.")]
        [SerializeField] private Transform earthFrame;
        [Header("Pilot-selected references, not flight-director commands")]
        [SerializeField, Range(-15f, 10f)] private float selectedFpaDegrees = -3f;
        [SerializeField] private bool showFpa = true;
        [SerializeField] private bool showSceneCues = true;
        [SerializeField] private bool showHover = true;
        [SerializeField, Min(1f)] private float hoverFullScaleKnots = 10f;
        [SerializeField, Min(1f)] private float angularDistanceMeters = 4000f;
        [SerializeField, Range(.1f, 2f)] private float maximumPacketAgeSeconds = 1f;
        [SerializeField] private LayerMask landingSurfaceLayers = ~0;
        [SerializeField] private Color cueColor = new Color(.2f, 1f, .2f, .9f);
        [Tooltip("Minimum rung stroke in camera degrees. The actual stroke follows about 1 mrad at the eye (HF-STD-001B 5.3.3.2.9).")]
        [SerializeField, Range(.01f, .1f)] private float strokeDegrees = .06f;
        [Header("Attitude window and declutter")]
        [Tooltip("Minimum symmetric horizon gap and rung inner end, in degrees either side of the aircraft reference. The effective value is at least 1 degree beyond the waterline wing tips.")]
        [SerializeField, Range(1.6f, 4f)] private float horizonGapDegrees = 2.8f;
        [Tooltip("The selected-FPA reference appears only at or below this HAGL (or with G/S active/armed).")]
        [SerializeField, Min(0f)] private float fpaApproachAglFeet = FaaRotorcraftCueMath.DefaultFpaApproachAglFeet;
        [Tooltip("The horizon brightens to full intensity over this fraction of the attitude window inside its edges. Rungs are never faded: they are shown as complete pairs or not at all.")]
        [SerializeField, Range(0f, .15f)] private float windowFadeFraction = .06f;
        [Tooltip("Extra clearance around protected HUD elements, in reference-canvas units (1080-high canvas).")]
        [SerializeField, Range(0f, 40f)] private float keepOutPaddingReference = 8f;
        [Tooltip("Horizon alpha outside the attitude window (a quieter wide reference).")]
        [SerializeField, Range(.5f, 1f)] private float horizonOutsideWindowAlpha = .7f;
        [SerializeField, Range(1, 24)] private int labelledSceneCueLimit = 8;

        private const int MaxSceneCues = 24, PitchSlots = 70, FpaSlot = PitchSlots, AttSlot = PitchSlots + 1, FpvSlot = PitchSlots + 2,
            HoverSlot = PitchSlots + 3, SceneSlot = PitchSlots + 4, SlotCount = SceneSlot + MaxSceneCues;
        private const float RungOuterMax = 6.1f, RungOuterMin = 3.5f, HookDegrees = .6f, LabelGapDegrees = .45f, GlyphAdvanceEm = .62f,
            CapEm = .716f, HaloAlpha = .45f, HoverScaleDegrees = 1.8f;
        // Limited (pinned) waterline and FPV: dashed at the quiet-alpha floor, so they stay legible yet never read as the conformal symbol.
        private const float GhostAlpha = FaaHudStyle.MinQuietAlpha;
        // C3 window anchor: at least +/-10 deg of pitch around the waterline, and room for a centred rung pair with its numerals.
        // The window may widen toward the waterline up to these viewport limits (the side columns' keep-outs then trim it).
        private const float AttitudeHalfDegrees = 10f, LadderHalfSpanDegrees = 8.8f, FieldViewportMin = .30f, FieldViewportMax = .70f;
        // M8 occlusion margin around the waterline and FPV; m3: horizon pieces shorter than this are left out.
        private const float OccluderPadDegrees = .3f, MinHorizonPieceDegrees = 1.5f, WaterlineHalfUnits = 3f, BoresightLogDegrees = 3f;
        private const string AttFlagText = "ATT", FpvFlagText = "FPV";
        private const string StateNoAttitude = "ATT / FPV UNAVAILABLE - NO FRESH VALID DATA", StateNoVelocity = "FPV UNAVAILABLE - GROUND TRACK / VELOCITY REQUIRED",
            StateHover = "HOVER", StateForward = "FORWARD FLIGHT", StateOutOfView = "FPV OUT OF VIEW", StateLimited = "FPV LIMIT - OUTSIDE ATTITUDE FIELD",
            WaterlineLimitText = "   |   W LIMIT - OUTSIDE ATTITUDE FIELD";
        private static readonly string[] PitchText = BuildPitchText();
        // Gull-wing waterline in units of its V depth (>= 6 mrad, SAE ARP4102/7): distinct from the circular FPV (AC 25-11B A.7.3).
        private static readonly Vector2[] WaterlineShape = { new(-3f, 0f), new(-1.5f, 0f), new(-.75f, -1f), new(0f, 0f), new(.75f, -1f), new(1.5f, 0f), new(3f, 0f) };
        // Hover plan-view candidates (viewport): outside the attitude field, first clear of every protected area wins.
        private static readonly Vector2[] HoverCandidates = { new(.63f, .31f), new(.37f, .31f), new(.5f, .16f), new(.70f, .24f), new(.30f, .24f), new(.5f, .11f) };

        private readonly List<Vector3> vertices = new(4096);
        private readonly List<Color> colors = new(4096);
        private readonly List<int> triangles = new(6144), haloTriangles = new(6144);
        private readonly List<MeshRenderer> labelRenderers = new(SlotCount);
        private readonly TextMeshPro[] slots = new TextMeshPro[SlotCount];
        private readonly bool[] slotUsed = new bool[SlotCount], slotActive = new bool[SlotCount];
        private readonly string[] slotText = new string[SlotCount];
        private readonly Color[] slotColor = new Color[SlotCount];
        private readonly List<Rect> keepOuts = new(32), placed = new(32);
        private readonly FaaRotorcraftCueAnchor[] sceneAnchor = new FaaRotorcraftCueAnchor[MaxSceneCues];
        private readonly Vector3[] sceneCentre = new Vector3[MaxSceneCues];
        private readonly float[] sceneDistance = new float[MaxSceneCues], sceneSize = new float[MaxSceneCues];
        private readonly bool[] sceneLabelled = new bool[MaxSceneCues];
        private readonly Color[] sceneTint = new Color[MaxSceneCues];
        private readonly Dictionary<FaaRotorcraftCueAnchor, SceneLabel> sceneLabels = new();
        private readonly float[] cutStart = new float[48], cutEnd = new float[48];
        private readonly float[] pieceStart = new float[32], pieceEnd = new float[32];
        // Pixel rects of the drawn aircraft reference and FPV: ladder, horizon, FPA and chevrons break around them (M8).
        // symbolCuts holds the FPV ring/wing/fin bands only; the waterline itself is cut by those (the FPV is drawn whole).
        private readonly List<Rect> occluders = new(8), symbolCuts = new(8);
        private readonly float[] signature = new float[112], lastSignature = new float[112];
        private readonly Dictionary<GameObject, bool> retiredObjects = new();
        private readonly Dictionary<Graphic, bool> retiredGraphics = new();
        private sealed class SceneLabel { public int Bucket = int.MinValue; public string Id; public FaaRotorcraftCueAnchor.CueType Kind; public string Text; }

        private Mesh mesh;
        private MeshRenderer meshRenderer;
        private Material material, labelMaterial;
        private Transform geometryRoot;
        private Transform worldLabelRoot;
        private Canvas sourceCanvas;
        private string dataStatus = "NOT INITIALIZED", flightState = StateNoAttitude;
        private Transform primaryHudRoot;
        private float radius, nextSourceSearch;
        private bool isHover, frameVisible, warnedShader;
        private Vector3 eyePosition;
        private FaaRotorcraftCueAnchor userLandingReference;
        private readonly RaycastHit[] landingHits = new RaycastHit[64];
        private CanvasGroup[] visibilityGroups;
        private int lastRefreshFrame = -1;
        private Vector3 lastViewPosition;
        private Quaternion lastViewRotation;
        private string actionFeedback;
        private float feedbackUntil;
        private float worldTextLineHeight = 1f, capToLine = .64f;

        // Per-refresh view and zone state (cached so each projection is a matrix multiply, not native property calls).
        private Matrix4x4 projection;
        private Quaternion viewRotation = Quaternion.identity, inverseViewRotation = Quaternion.identity;
        private Rect pixels, windowPx, zoneAnchor = new Rect(-2f, -2f, 0f, 0f);
        private Vector2 focal = Vector2.one, windowScreen;
        private bool windowReady, windowAnchored, isUnusual, classicLocal, nativeXr, glideslopeMode;
        private int zoneFrame = -1, signatureLength, lastSignatureLength = -1, hoverCandidate = -1;
        private float zoneTime, rungStroke, primaryStroke, minorStroke, haloDegrees, capDegrees, capPixels, fpvRadius, waterlineUnit,
            fadeX, fadeY, rungOuter = RungOuterMax, intensity = 1f, ladderInner = 2.8f, strokePixels, haloPixels, occluderPad = 4f;
        // Waterline from validated data, projected once per refresh (window anchor and symbol).
        private Quaternion waterlineBody = Quaternion.identity;
        private Vector3 waterlineDirection = Vector3.forward;
        private Vector2 waterlinePixel;
        private Rect waterlineBox;
        private bool waterlineValid, waterlineProjected;
        // FPV planned before the waterline is drawn, so the waterline can be cut under it.
        private bool fpvPlanned, fpvGhost;
        private Vector2 fpvPixel;
        private Vector3 fpvDirection;
        private float fpvPixels;
        private float fpaLabelValue = float.NaN, fpaLabelEm = -1f;
        private string fpaLabelText, hoverLabelText;
        private int hoverLabelKey = int.MinValue;
        private string statusState;
        private bool statusWaterlineLimited;
        private int statusRa = int.MaxValue, statusQ1 = int.MaxValue, statusQ2 = int.MaxValue, statusNr = int.MaxValue;
        // Boresight diagnostics (C3): which camera rig provides the aircraft reference, and rate limiting of the mismatch log.
        private Camera cameraRigView;
        private AircraftControl.Camera.AircraftCameraController cameraRig;
        private float boresightOffsetSince = -1f, nextBoresightLog;

        public float SelectedFpaDegrees => selectedFpaDegrees;
        public bool HoverMode => isHover;
        public bool HasLiveAttitude { get; private set; }
        public bool HasLiveVelocity { get; private set; }
        public int LastVertexCount => vertices.Count;
        public int VisibleSceneCueCount { get; private set; }
        public string DataStatus => dataStatus;
        /// <summary>True while any conformal layer is in unusual-attitude declutter, so other layers can declutter together (AC 25-11B 5.10.3.2).</summary>
        public static bool UnusualAttitudeActive { get; private set; }
        public bool UnusualAttitude => isUnusual;
        /// <summary>The screen-fixed attitude window (camera pixels, bottom-left origin) that confines the ladder this frame.</summary>
        public Rect AttitudeWindowPixels => windowPx;
        public bool AttitudeFlagVisible { get; private set; }
        public bool FpaReferenceVisible { get; private set; }
        public bool FpvLimited { get; private set; }
        public int LastRungCount { get; private set; }
        /// <summary>Cap height of conformal text in camera degrees (28 arcmin at the eye).</summary>
        public float LabelCapDegrees => capDegrees;
        /// <summary>World direction of the aircraft waterline built from validated flight data (zero when not drawn).</summary>
        public Vector3 LastWaterlineDirection { get; private set; }
        /// <summary>The aircraft reference was drawn this frame (always, while attitude is live outside the Classic inset; C3).</summary>
        public bool WaterlineVisible { get; private set; }
        /// <summary>The aircraft reference lies outside the attitude window and is drawn pinned and ghosted at its edge (W LIMIT).</summary>
        public bool WaterlineLimited { get; private set; }
        /// <summary>The true horizon is off screen and a dashed horizon with a caret is clamped to the window edge (standards 3.1).</summary>
        public bool HorizonOffScale { get; private set; }
        /// <summary>Camera forward azimuth minus the validated heading, degrees (includes the pilot's look offset).</summary>
        public float ViewHeadingOffsetDegrees { get; private set; }
        /// <summary>
        /// Azimuth of the camera rig's aircraft reference (the view with the look offset removed) minus the validated heading, degrees.
        /// Near zero when the view and the data agree; the ladder and waterline are always centred on the validated heading.
        /// </summary>
        public float BoresightHeadingOffsetDegrees { get; private set; }

        private static string[] BuildPitchText()
        {
            var text = new string[35];
            for (int i = 0; i < text.Length; i++) text[i] = (-85 + i * 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return text;
        }

        public void Bind(Camera camera, Transform aircraftTransform, Canvas fixedFlightCanvas)
        {
            view = camera;
            aircraft = aircraftTransform;
            if (sourceCanvas == fixedFlightCanvas) return;
            RestoreLegacy();
            sourceCanvas = fixedFlightCanvas;
            primaryHudRoot = sourceCanvas != null ? sourceCanvas.transform.Find("Second Interation GUI") : null;
            visibilityGroups = primaryHudRoot != null ? primaryHudRoot.GetComponentsInParent<CanvasGroup>(true) : null;
            lastSignatureLength = -1;
            RetireLegacy();
        }

        private void OnEnable()
        {
            Application.onBeforeRender += RefreshBeforeRender;
            Camera.onPreCull += CameraReady;
            RenderPipelineManager.beginCameraRendering += PipelineCameraReady;
            lastSignatureLength = -1;
            RetireLegacy();
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= RefreshBeforeRender;
            Camera.onPreCull -= CameraReady;
            RenderPipelineManager.beginCameraRendering -= PipelineCameraReady;
            SetRenderVisibility(false);
            isUnusual = false; UnusualAttitudeActive = false;
            RestoreLegacy();
        }

        private void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
            if (material != null) Destroy(material);
            if (labelMaterial != null) Destroy(labelMaterial);
            if (geometryRoot != null) Destroy(geometryRoot.gameObject);
            if (userLandingReference != null) Destroy(userLandingReference.gameObject);
        }

        private void RetireLegacy()
        {
            if (sourceCanvas == null) return;
            foreach (var old in sourceCanvas.GetComponentsInChildren<FaaPitchLadderGraphic>(true))
            {
                if (!retiredObjects.ContainsKey(old.gameObject)) retiredObjects.Add(old.gameObject, old.gameObject.activeSelf);
                old.gameObject.SetActive(false); // Also hides its children/labels, not just its mesh.
            }
            foreach (var old in sourceCanvas.GetComponentsInChildren<Graphic>(true))
            {
                string name = old.gameObject.name;
                if (name != "Scale" && name != "ScaleIteration2" && name != "FPV" && name != "Miniature Aircraft") continue;
                if (!retiredGraphics.ContainsKey(old)) retiredGraphics.Add(old, old.enabled);
                old.enabled = false;
            }
        }

        private void RestoreLegacy()
        {
            // At most ONE attitude presentation comes back (AC 25-11B 5.10.1): when the vector ladder returns,
            // the bitmap ladders it replaces stay off.
            bool vectorLadder = false;
            foreach (var entry in retiredObjects)
                if (entry.Key != null) { entry.Key.SetActive(entry.Value); vectorLadder |= entry.Value; }
            foreach (var entry in retiredGraphics)
            {
                if (entry.Key == null) continue;
                string name = entry.Key.gameObject.name;
                bool bitmapLadder = name == "Scale" || name == "ScaleIteration2";
                entry.Key.enabled = entry.Value && !(vectorLadder && bitmapLadder);
            }
            retiredObjects.Clear();
            retiredGraphics.Clear();
        }

        private void LateUpdate()
        {
            if (flightData == null && Time.unscaledTime >= nextSourceSearch)
            {
                nextSourceSearch = Time.unscaledTime + 1f;
                flightData = FindFirstObjectByType<XPlane12ApiHudBridge>();
            }
            RefreshPresentation();
        }

        [BeforeRenderOrder(300)]
        private void RefreshBeforeRender()
        {
            if (isActiveAndEnabled && view != null && (lastRefreshFrame != Time.frameCount ||
                lastViewPosition != view.transform.position || lastViewRotation != view.transform.rotation))
                RefreshPresentation();
        }
        private void PipelineCameraReady(ScriptableRenderContext context, Camera camera) => CameraReady(camera);
        private void CameraReady(Camera camera)
        {
            if (!isActiveAndEnabled) return;
            if (camera == view) RefreshBeforeRender(); // Final pose; avoid rebuilding the same mesh three times each frame.
            SetRenderVisibility(camera == view && frameVisible);
        }

        private void SetRenderVisibility(bool visible)
        {
            if (meshRenderer != null) meshRenderer.enabled = visible;
            // Use native mesh text. Canvas batches can be culled before a camera's
            // pre-cull callback when several radar/scene cameras share the frame.
            for (int i = 0; i < labelRenderers.Count; i++) if (labelRenderers[i] != null) labelRenderers[i].enabled = visible;
        }

        private bool EnsureBuilt()
        {
            if (mesh != null) return true;
            Shader shader = Resources.Load<Shader>("Shaders/FaaRotorcraftConformal");
            if (shader == null || !shader.isSupported)
            {
                if (!warnedShader) Debug.LogError("[FAA rotorcraft HUD] Conformal line shader unavailable; scene cues suppressed.");
                warnedShader = true;
                return false;
            }
            var go = new GameObject("FAA Rotorcraft World Cues", typeof(MeshFilter), typeof(MeshRenderer));
            geometryRoot = go.transform;
            geometryRoot.SetParent(transform, false);
            mesh = new Mesh { name = "FAA rotorcraft cue mesh", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            material = new Material(shader) { name = "FAA rotorcraft lines (runtime)", enableInstancing = true };
            meshRenderer = go.GetComponent<MeshRenderer>();
            // Submesh 0: dark halo (drawn first) keeps >= 3:1 local contrast over bright sky (AC 20-167A H.7.3); submesh 1: symbology.
            meshRenderer.sharedMaterials = new[] { material, material };
            meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.allowOcclusionWhenDynamic = false;
            meshRenderer.sortingOrder = 5040;
            var labelRoot = new GameObject("FAA World Cue Labels");
            labelRoot.transform.SetParent(geometryRoot, false);
            worldLabelRoot = labelRoot.transform;
            var font = TMP_Settings.defaultFontAsset;
            if (font != null)
            {
                labelMaterial = new Material(font.material) { name = "FAA world text (runtime)", enableInstancing = true };
                // A resource material keeps the overlay shader included in player builds.
                // The regular TMP mobile shader uses unity_GUIZTestMode, not _ZTestMode.
                var overlayTemplate = Resources.Load<Material>("Shaders/FaaRotorcraftText");
                if (overlayTemplate != null) labelMaterial.shader = overlayTemplate.shader;
                else Debug.LogError("[FAA rotorcraft HUD] Missing overlay text material; world labels may be occluded.");
                if (labelMaterial.HasProperty("_UnderlayColor"))
                {
                    // Dark underlay halo, matching FaaHudStyle.HaloMaterial (text keeps contrast over bright terrain or cloud).
                    labelMaterial.EnableKeyword("UNDERLAY_ON");
                    labelMaterial.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, .75f));
                    if (labelMaterial.HasProperty("_UnderlayDilate")) labelMaterial.SetFloat("_UnderlayDilate", .35f);
                    if (labelMaterial.HasProperty("_UnderlaySoftness")) labelMaterial.SetFloat("_UnderlaySoftness", .3f);
                }
                var face = font.faceInfo;
                float line = face.ascentLine - face.descentLine;
                capToLine = line > 0f && face.capLine > 0f ? Mathf.Clamp(face.capLine / line, .45f, .85f) : .64f;
            }
            return true;
        }

        public void RefreshPresentation()
        {
            var data = flightData != null ? flightData.LatestFlightData : null;
            bool fresh = flightData != null && FaaRotorcraftCueMath.Fresh(flightData.IsFeedHealthy,
                flightData.LastPacketAgeSeconds, maximumPacketAgeSeconds);
            HasLiveAttitude = fresh && data != null && data.attitudeValid &&
                FaaRotorcraftCueMath.Finite(data.pitch) && FaaRotorcraftCueMath.Finite(data.roll) &&
                FaaRotorcraftCueMath.Finite(data.heading);
            // Evaluated even when the layer is hidden so other layers can declutter on it; invalid attitude is never "unusual".
            isUnusual = HasLiveAttitude && FaaRotorcraftCueMath.UnusualAttitude(data.pitch, data.roll, isUnusual);
            UnusualAttitudeActive = isUnusual;

            frameVisible = view != null && !view.orthographic && aircraft != null && sourceCanvas != null &&
                sourceCanvas.isActiveAndEnabled && (primaryHudRoot == null || primaryHudRoot.gameObject.activeInHierarchy);
            if (visibilityGroups != null)
                foreach (var group in visibilityGroups)
                    if (group != null && group.alpha <= .001f) frameVisible = false;
            if (!frameVisible || !EnsureBuilt())
            {
                SetRenderVisibility(false);
                lastSignatureLength = -1;
                return;
            }
            eyePosition = view.transform.position;
            lastRefreshFrame = Time.frameCount;
            lastViewPosition = eyePosition;
            lastViewRotation = view.transform.rotation;
            geometryRoot.SetPositionAndRotation(eyePosition, Quaternion.identity);
            geometryRoot.localScale = Vector3.one;
            radius = Mathf.Min(Mathf.Max(10f, angularDistanceMeters), view.farClipPlane * .75f);
            if (radius <= view.nearClipPlane * 2f)
            {
                frameVisible = false;
                SetRenderVisibility(false);
                lastSignatureLength = -1;
                return;
            }
            Vector3 velocity = Vector3.zero;
            HasLiveVelocity = HasLiveAttitude && data.groundVelocityValid &&
                FaaRotorcraftCueMath.TryGroundVelocity(data.groundSpeed, data.track, data.verticalSpeed, out velocity);
            isHover = HasLiveVelocity && FaaRotorcraftCueMath.UseHover(data.groundSpeed, isHover);

            var workspace = FaaSpatialWorkspace.Current;
            nativeXr = workspace != null && workspace.NativeXr;
            // The classic attitude inset is explicitly non-conformal: no second (conformal) pitch scale, FPV or FPA over it (AC 25-11B F.5.4.6.2).
            classicLocal = workspace != null && workspace.UsesClassicAttitude;
            intensity = Mathf.Clamp01(FaaSpatialWorkspace.ForwardIntensityFor(transform));
            CacheView();
            ComputeAngularSizes();
            ProjectWaterline(data);
            RefreshZones();
            ComputeWindowSizes();
            UpdateBoresightDiagnostics(data);
            bool lowAgl = HasLiveAttitude && data.altitudeAGLValid && FaaRotorcraftCueMath.Finite(data.altitudeAGL) &&
                data.altitudeAGL >= 0f && data.altitudeAGL <= fpaApproachAglFeet;
            glideslopeMode = HasLiveAttitude && showFpa && !lowAgl && !classicLocal && !isHover && !isUnusual && GlideslopeMode();

            BuildSignature(data);
            if (SignatureChanged()) Rebuild(data, velocity);
            UpdateStatus(data);
            SetRenderVisibility(true);
        }

        private void Rebuild(AviationFlightData data, Vector3 velocity)
        {
            vertices.Clear(); colors.Clear(); triangles.Clear(); haloTriangles.Clear(); placed.Clear(); occluders.Clear(); symbolCuts.Clear();
            System.Array.Clear(slotUsed, 0, SlotCount);
            VisibleSceneCueCount = 0; LastRungCount = 0;
            AttitudeFlagVisible = FpaReferenceVisible = FpvLimited = WaterlineVisible = WaterlineLimited = HorizonOffScale = false;
            LastWaterlineDirection = Vector3.zero;
            string state = !HasLiveAttitude ? StateNoAttitude : !HasLiveVelocity ? StateNoVelocity : isHover ? StateHover : StateForward;

            if (HasLiveAttitude)
            {
                // Priority order (AC 25-11B F.4.5): aircraft reference and FPV first, then the horizon and ladder (broken around
                // both, M8), then references and labels. The FPV is planned first so the waterline can be cut under it.
                bool fpvWanted = !classicLocal && !isUnusual && !isHover;
                fpvPlanned = false;
                if (fpvWanted && HasLiveVelocity)
                {
                    int fpv = PlanFpv(EarthVector(velocity).normalized);
                    if (fpv == 1) { state = StateLimited; FpvLimited = true; }
                    else if (fpv == 2) state = StateOutOfView; // Never pin a normal FPV to a false direction at the edge.
                }
                if (fpvPlanned) FpvCuts(symbolCuts);
                if (!classicLocal) DrawWaterline();
                if (fpvPlanned) DrawPlannedFpv();
                else if (fpvWanted && !HasLiveVelocity)
                    Flag(FpvSlot, FpvFlagText, new Vector2(windowPx.center.x, windowPx.yMin + windowPx.height * .12f), FaaHudStyle.Amber, false);
                if (!classicLocal)
                {
                    // Standards 3.1 / 2.7: a horizon reference in every attitude; clamped and dashed when the true one is off screen.
                    if (!DrawHorizon(data.heading)) DrawOffScaleHorizon();
                    DrawLadder(data.heading);
                }
                FpaReferenceVisible = !classicLocal && FaaRotorcraftCueMath.ShowFpaReference(showFpa, isHover, isUnusual,
                    data.altitudeAGLValid, data.altitudeAGL, glideslopeMode, fpaApproachAglFeet);
                if (FpaReferenceVisible) DrawFpa(HasLiveVelocity && !isHover ? data.track : data.heading);
                if (showHover && isHover && !isUnusual) DrawHover(velocity, data.heading);
                // Unusual attitude keeps only what recognition and recovery need (standards 2.7): no waypoint, LZ or obstacle references.
                if (showSceneCues && !isUnusual) DrawSceneReferences(true);
            }
            else if (!classicLocal)
            {
                // Positive indication of attitude loss where the attitude normally appears (AC 25-11B 4.6.4, Table F-2).
                Flag(AttSlot, AttFlagText, windowPx.center, FaaHudStyle.Red, true);
                AttitudeFlagVisible = true;
            }
            flightState = state;
            for (int i = 0; i < SlotCount; i++)
                if (slotActive[i] && !slotUsed[i] && slots[i] != null) { slots[i].gameObject.SetActive(false); slotActive[i] = false; }
            mesh.Clear();
            mesh.SetVertices(vertices); mesh.SetColors(colors);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(haloTriangles, 0, false);
            mesh.SetTriangles(triangles, 1, false);
            mesh.RecalculateBounds();
        }

        private void UpdateStatus(AviationFlightData data)
        {
            bool live = HasLiveAttitude && data != null;
            // 10 ft steps above 200 ft so the diagnostic string is not rebuilt every frame in a climb or descent.
            int ra = live && data.altitudeAGLValid && FaaRotorcraftCueMath.Finite(data.altitudeAGL) ?
                (data.altitudeAGL > 200f ? Mathf.RoundToInt(data.altitudeAGL / 10f) * 10 : Mathf.RoundToInt(data.altitudeAGL)) : int.MinValue;
            int q1 = live && data.engine1TorqueValid && FaaRotorcraftCueMath.Finite(data.engine1Torque) ? Mathf.RoundToInt(data.engine1Torque) : int.MinValue;
            int q2 = live && data.engine2TorqueValid && FaaRotorcraftCueMath.Finite(data.engine2Torque) ? Mathf.RoundToInt(data.engine2Torque) : int.MinValue;
            int nr = live && data.rotorNRValid && FaaRotorcraftCueMath.Finite(data.rotorNR) ? Mathf.RoundToInt(data.rotorNR) : int.MinValue;
            bool limited = live && WaterlineLimited;
            if (ReferenceEquals(statusState, flightState) && ra == statusRa && q1 == statusQ1 && q2 == statusQ2 && nr == statusNr &&
                limited == statusWaterlineLimited) return;
            statusState = flightState; statusRa = ra; statusQ1 = q1; statusQ2 = q2; statusNr = nr; statusWaterlineLimited = limited;
            // y_agl is geometric height above ground, not proof of a radar-altimeter measurement. Rebuilt only when a value changes.
            dataStatus = flightState + (limited ? WaterlineLimitText : "") + "   |   HAGL " + (ra == int.MinValue ? "--" : ra + " FT") +
                "   Q1/Q2 " + Num(q1) + "/" + Num(q2) + "%   NR " + Num(nr) + "%";
        }
        private static string Num(int value) => value == int.MinValue ? "--" : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// G/S active or armed with the FD/AP coupled (same datarefs and decoding as FaaAnalogFlightSample/FaaFlightModeAnnunciation).
        /// Read directly so the per-frame check allocates nothing (FaaAnalogFlightSample.Capture builds params arrays).
        /// </summary>
        private bool GlideslopeMode()
        {
            var systems = flightData != null ? flightData.LatestSnapshot?.Systems : null;
            if (systems == null) return false;
            bool coupled = FaaAnalogFlightSample.Try(systems, "sim/cockpit2/autopilot/flight_director_mode", out float mode) ||
                FaaAnalogFlightSample.Try(systems, "sim/cockpit/autopilot/autopilot_mode", out mode);
            if (!coupled || mode < 1f) return false;
            if (!FaaAnalogFlightSample.Try(systems, "sim/cockpit/autopilot/autopilot_state", out float raw) || raw < 0f || raw >= 16777216f) return false;
            FaaAnalogFlightSample.DecodeModes((int)raw, out _, out string pitch, out _, out string pitchArmed);
            return pitch == "G/S" || pitchArmed == "G/S ARM";
        }

        // ---- View, zones and sizes ---------------------------------------------------------------

        private void CacheView()
        {
            projection = view.nonJitteredProjectionMatrix;
            viewRotation = view.transform.rotation;
            inverseViewRotation = Quaternion.Inverse(viewRotation);
            pixels = view.pixelRect;
            focal = new Vector2(Mathf.Abs(projection.m00) * pixels.width * .5f, Mathf.Abs(projection.m11) * pixels.height * .5f);
            if (!FaaRotorcraftCueMath.Finite(focal.x) || !FaaRotorcraftCueMath.Finite(focal.y) || focal.x < 1f || focal.y < 1f)
            {
                float f = pixels.height * .5f / Mathf.Tan(Mathf.Clamp(view.fieldOfView, 5f, 170f) * .5f * Mathf.Deg2Rad);
                focal = new Vector2(f, f);
            }
        }

        /// <summary>
        /// Screen-fixed attitude window (C3): the registered/default attitude field, extended toward the projected waterline and the
        /// +/-10 deg of pitch around it (never above its own top, which stays under the roll-scale ends), then cleared of every protected
        /// Symbology/Chrome rectangle (IAS/TQ, ALT/VSI/NR, FMA, heading, G/S when shown, bottom chrome) relative to the attitude-field
        /// centre, so it never jumps across the heading scale or a side column. Growth follows flight data and is never eased; other
        /// inward changes snap, outward changes ease, sub-pixel changes are ignored, so the window never jitters and never overlaps.
        /// </summary>
        private void RefreshZones()
        {
            Rect anchorKey = waterlineValid && waterlineProjected ? waterlineBox : new Rect(-1f, -1f, 0f, 0f);
            if (zoneFrame == Time.frameCount && windowReady && windowScreen == pixels.size && SameRect(anchorKey, zoneAnchor, .5f)) return;
            float dt = zoneFrame < 0 || zoneFrame == Time.frameCount ? 0f : Mathf.Max(0f, Time.unscaledTime - zoneTime);
            zoneFrame = Time.frameCount; zoneTime = Time.unscaledTime; zoneAnchor = anchorKey;
            float pad = keepOutPaddingReference * pixels.height / FaaRotorcraftCueMath.ReferenceCanvasHeight;
            keepOuts.Clear();
            FaaHudKeepOut.Collect(keepOuts, true);
            for (int i = 0; i < keepOuts.Count; i++)
            {
                Rect r = keepOuts[i];
                keepOuts[i] = Rect.MinMaxRect(r.xMin - pad, r.yMin - pad, r.xMax + pad, r.yMax + pad);
            }
            Rect start = FaaHudKeepOut.AttitudeWindow(pixels.width, pixels.height);
            Rect v = FaaHudKeepOut.DefaultAttitudeViewport;
            Rect fallback = new Rect(pixels.x + v.x * pixels.width, pixels.y + v.y * pixels.height, v.width * pixels.width, v.height * pixels.height);
            Rect grown = start;
            if (waterlineValid && waterlineProjected)
            {
                Vector2 c = waterlinePixel;
                float half = Mathf.Max(.5f * waterlineBox.width, focal.x * Mathf.Tan(LadderHalfSpanDegrees * Mathf.Deg2Rad));
                float margin = focal.y * Mathf.Tan(AttitudeHalfDegrees * Mathf.Deg2Rad);
                Vector3 right = viewRotation * Vector3.right;
                // +/-10 deg about the camera's horizontal axis (Unity: a positive rotation about +X turns forward downward).
                float lo = ToPixel(Quaternion.AngleAxis(AttitudeHalfDegrees, right) * waterlineDirection, out Vector2 below) ? Mathf.Min(below.y, c.y) : c.y - margin;
                float hi = ToPixel(Quaternion.AngleAxis(-AttitudeHalfDegrees, right) * waterlineDirection, out Vector2 above) ? Mathf.Max(above.y, c.y) : c.y + margin;
                Rect content = Rect.MinMaxRect(Mathf.Min(waterlineBox.xMin, c.x - half), Mathf.Min(waterlineBox.yMin, lo),
                    Mathf.Max(waterlineBox.xMax, c.x + half), Mathf.Max(waterlineBox.yMax, hi));
                Rect bounds = Rect.MinMaxRect(Mathf.Min(start.xMin, pixels.x + FieldViewportMin * pixels.width), pixels.yMin,
                    Mathf.Max(start.xMax, pixels.x + FieldViewportMax * pixels.width), Mathf.Min(start.yMax, pixels.yMax));
                grown = FaaRotorcraftCueMath.GrowToward(start, content, bounds);
            }
            Rect target = FaaRotorcraftCueMath.AttitudeWindow(grown, keepOuts, 0f, new Vector2(.10f * pixels.width, .16f * pixels.height), fallback, start.center);
            bool anchored = grown != start;
            windowPx = !windowReady || windowScreen != pixels.size || anchored || windowAnchored ? target : FaaRotorcraftCueMath.SmoothWindow(windowPx, target, dt);
            windowAnchored = anchored;
            windowReady = true; windowScreen = pixels.size;
        }
        private static bool SameRect(Rect a, Rect b, float tolerance) =>
            Mathf.Abs(a.xMin - b.xMin) <= tolerance && Mathf.Abs(a.yMin - b.yMin) <= tolerance && Mathf.Abs(a.xMax - b.xMax) <= tolerance && Mathf.Abs(a.yMax - b.yMax) <= tolerance;

        private void ComputeAngularSizes()
        {
            float fov = view.fieldOfView, h = pixels.height, mrad = FaaRotorcraftCueMath.ArcminPerMilliradian;
            rungStroke = Mathf.Max(strokeDegrees, FaaRotorcraftCueMath.AngularSize(mrad, fov, h, nativeXr));
            primaryStroke = Mathf.Max(strokeDegrees * 1.2f, FaaRotorcraftCueMath.AngularSize(1.2f * mrad, fov, h, nativeXr));
            minorStroke = Mathf.Max(strokeDegrees * .8f, FaaRotorcraftCueMath.AngularSize(.8f * mrad, fov, h, nativeXr));
            haloDegrees = FaaRotorcraftCueMath.AngularSize(.5f * mrad, fov, h, nativeXr);
            capDegrees = FaaRotorcraftCueMath.AngularSize(FaaRotorcraftCueMath.HudAlphanumericArcmin, fov, h, nativeXr);
            capPixels = focal.y * Mathf.Tan(capDegrees * Mathf.Deg2Rad);
            fpvRadius = Mathf.Max(.35f, FaaRotorcraftCueMath.AngularSize(17f, fov, h, nativeXr)); // 34 arcmin ring (HUD symbol)
            waterlineUnit = Mathf.Max(.5f, FaaRotorcraftCueMath.AngularSize(20.6f, fov, h, nativeXr)); // 6 mrad V depth
            // M8: rung inner ends and the horizon gap stay at least 1 deg clear of the waterline wing tips.
            ladderInner = FaaRotorcraftCueMath.RungInnerDegrees(horizonGapDegrees, WaterlineHalfUnits * waterlineUnit);
            strokePixels = focal.y * Mathf.Tan(primaryStroke * Mathf.Deg2Rad);
            haloPixels = focal.y * Mathf.Tan(haloDegrees * Mathf.Deg2Rad);
            occluderPad = Mathf.Max(3f, focal.y * Mathf.Tan(OccluderPadDegrees * Mathf.Deg2Rad));
        }

        private void ComputeWindowSizes()
        {
            fadeX = windowFadeFraction * windowPx.width; fadeY = windowFadeFraction * windowPx.height;
            float halfWidth = Mathf.Atan(windowPx.width * .5f / focal.x) * Mathf.Rad2Deg;
            float numeral = 3f * GlyphAdvanceEm * capDegrees / CapEm;
            // Narrow windows (Classic dials) shorten the rungs so a centred rung pair and both numerals still fit inside the window.
            rungOuter = Mathf.Clamp(halfWidth - LabelGapDegrees - numeral - .3f, Mathf.Max(RungOuterMin, ladderInner + 1.2f), RungOuterMax);
        }

        /// <summary>Waterline from validated pitch/roll/true heading (CONF-01), projected once per refresh for the window anchor and the symbol.</summary>
        private void ProjectWaterline(AviationFlightData data)
        {
            waterlineValid = HasLiveAttitude && data != null && !classicLocal;
            waterlineProjected = false;
            if (!waterlineValid) return;
            waterlineBody = (earthFrame != null ? earthFrame.rotation : Quaternion.identity) * FaaRotorcraftCueMath.BodyRotation(data.pitch, data.roll, data.heading);
            waterlineDirection = waterlineBody * Vector3.forward;
            float u = waterlineUnit, x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < WaterlineShape.Length; i++)
            {
                if (!ToPixel(FaaRotorcraftCueMath.BodyPoint(waterlineBody, WaterlineShape[i].x * u, WaterlineShape[i].y * u), out Vector2 p)) return;
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            float s = .5f * strokePixels + haloPixels;
            waterlineBox = Rect.MinMaxRect(x0 - s, y0 - s, x1 + s, y1 + s);
            waterlineProjected = ToPixel(waterlineDirection, out waterlinePixel) && FaaRotorcraftCueMath.FiniteRect(waterlineBox);
        }

        /// <summary>
        /// C3 investigation: the ladder and waterline are centred on the validated heading, which is the aircraft boresight azimuth. The
        /// camera rig's aircraft reference (its view with the pilot's look offset removed) must point the same way; the desktop rig takes
        /// its yaw from the aircraft transform, which the bridge drives from the same heading. A difference that persists for 2 s means the
        /// view and the data disagree (for example the transform is driven by another source) and is logged at most every 30 s. A look
        /// offset alone (free look, an eased FORWARD return) only moves <see cref="ViewHeadingOffsetDegrees"/>, which is expected.
        /// </summary>
        private void UpdateBoresightDiagnostics(AviationFlightData data)
        {
            if (!HasLiveAttitude || data == null) { ViewHeadingOffsetDegrees = BoresightHeadingOffsetDegrees = 0f; boresightOffsetSince = -1f; return; }
            ViewHeadingOffsetDegrees = HeadingOffset(viewRotation * Vector3.forward, data.heading);
            if (cameraRigView != view) { cameraRigView = view; cameraRig = view != null ? view.GetComponent<AircraftControl.Camera.AircraftCameraController>() : null; }
            if (cameraRig == null) { BoresightHeadingOffsetDegrees = ViewHeadingOffsetDegrees; boresightOffsetSince = -1f; return; }
            BoresightHeadingOffsetDegrees = HeadingOffset(cameraRig.AircraftReferenceRotation * Vector3.forward, data.heading);
            float now = Time.unscaledTime;
            if (Mathf.Abs(BoresightHeadingOffsetDegrees) < BoresightLogDegrees) { boresightOffsetSince = -1f; return; }
            if (boresightOffsetSince < 0f) boresightOffsetSince = now;
            if (now - boresightOffsetSince < 2f || now < nextBoresightLog) return;
            nextBoresightLog = now + 30f;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Debug.LogWarning("[FAA rotorcraft HUD] Camera boresight azimuth differs from the validated heading by " +
                BoresightHeadingOffsetDegrees.ToString("0.0", inv) + " deg (view incl. look offset: " + ViewHeadingOffsetDegrees.ToString("0.0", inv) +
                " deg). The ladder and waterline stay on the validated heading; check what drives the aircraft transform.");
        }
        private float HeadingOffset(Vector3 forward, float heading)
        {
            Vector3 local = earthFrame != null ? Quaternion.Inverse(earthFrame.rotation) * forward : forward;
            if (new Vector2(local.x, local.z).sqrMagnitude < 1e-6f) return 0f; // looking straight up or down: azimuth undefined
            return Mathf.DeltaAngle(heading, Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg);
        }

        // ---- Change detection: rebuild the mesh only when something visible can change -------------

        private void Sig(float value) { if (signatureLength < signature.Length) signature[signatureLength++] = value; }
        private void Sig(bool value) => Sig(value ? 1f : 0f);
        private void Sig(Vector3 v) { Sig(v.x); Sig(v.y); Sig(v.z); }
        private void Sig(Quaternion q) { Sig(q.x); Sig(q.y); Sig(q.z); Sig(q.w); }
        private void Sig(Rect r) { Sig(r.x); Sig(r.y); Sig(r.width); Sig(r.height); }

        private void BuildSignature(AviationFlightData data)
        {
            signatureLength = 0;
            Sig(viewRotation); Sig(view.fieldOfView); Sig(pixels); Sig(projection.m00); Sig(projection.m11); Sig(projection.m02); Sig(projection.m12);
            Sig(radius); Sig(windowPx); Sig(intensity); Sig(capDegrees); Sig(rungOuter); Sig(ladderInner);
            float x0 = 0f, y0 = 0f, x1 = 0f, y1 = 0f;
            for (int i = 0; i < keepOuts.Count; i++) { Rect r = keepOuts[i]; x0 += r.xMin; y0 += r.yMin; x1 += r.xMax; y1 += r.yMax; }
            Sig(keepOuts.Count); Sig(x0); Sig(y0); Sig(x1); Sig(y1);
            Sig(HasLiveAttitude); Sig(HasLiveVelocity); Sig(isHover); Sig(isUnusual); Sig(classicLocal); Sig(nativeXr); Sig(glideslopeMode);
            Sig(showFpa); Sig(showSceneCues); Sig(showHover); Sig(selectedFpaDegrees); Sig(hoverFullScaleKnots); Sig(fpaApproachAglFeet);
            Sig(horizonGapDegrees); Sig(windowFadeFraction); Sig(horizonOutsideWindowAlpha); Sig(labelledSceneCueLimit);
            Sig(cueColor.r); Sig(cueColor.g); Sig(cueColor.b); Sig(cueColor.a);
            if (earthFrame != null) Sig(earthFrame.rotation);
            if (data != null)
            {
                Sig(data.pitch); Sig(data.roll); Sig(data.heading); Sig(data.track); Sig(data.groundSpeed); Sig(data.verticalSpeed);
                Sig(data.altitudeAGL); Sig(data.altitudeAGLValid);
            }
            if (showSceneCues && HasLiveAttitude)
            {
                Sig(eyePosition);
                Vector3 sum = Vector3.zero, ends = Vector3.zero; float flags = 0f; int count = 0;
                // HashSet struct enumerator (no allocation); a mutable local, not a readonly 'using' copy.
                var anchors = FaaRotorcraftCueAnchor.EnumerateActive();
                while (anchors.MoveNext())
                    {
                        var a = anchors.Current;
                        if (a == null) continue;
                        count++; sum += a.transform.position;
                        if (a.lineEnd != null) ends += a.lineEnd.position;
                        flags += (a.selected ? 1f : 0f) + (a.positionValid ? 2f : 0f) + 4f * (int)a.kind + a.areaMeters.x * .001f + a.areaMeters.y * .01f +
                            a.maximumRangeMeters * .00001f + (a.identifier != null ? a.identifier.GetHashCode() * 1e-9f : 0f);
                    }
                Sig(count); Sig(sum); Sig(ends); Sig(flags);
            }
        }

        private bool SignatureChanged()
        {
            bool changed = signatureLength != lastSignatureLength;
            for (int i = 0; !changed && i < signatureLength; i++) changed = !signature[i].Equals(lastSignature[i]);
            if (!changed) return false;
            System.Array.Copy(signature, lastSignature, signatureLength);
            lastSignatureLength = signatureLength;
            return true;
        }

        // ---- Projection helpers -------------------------------------------------------------------

        private Vector3 EarthVector(Vector3 direction) => earthFrame != null ? earthFrame.rotation * direction : direction;
        private Vector3 Ray(float elevation, float bearing) => EarthVector(FaaRotorcraftCueMath.EarthRay(elevation, bearing));
        private Vector3 Rung(float elevation, float bearing, float lateral) => EarthVector(FaaRotorcraftCueMath.RungPoint(elevation, bearing, lateral));

        private bool ToPixel(Vector3 direction, out Vector2 pixel)
        {
            pixel = default;
            if (!FaaRotorcraftCueMath.TryProjectLocal(projection, inverseViewRotation * direction, out Vector2 viewport)) return false;
            pixel = new Vector2(pixels.x + viewport.x * pixels.width, pixels.y + viewport.y * pixels.height);
            return true;
        }

        private bool FromPixel(Vector2 pixel, out Vector3 direction)
        {
            direction = Vector3.forward;
            if (pixels.width <= 0f || pixels.height <= 0f) return false;
            var viewport = new Vector2((pixel.x - pixels.x) / pixels.width, (pixel.y - pixels.y) / pixels.height);
            if (!FaaRotorcraftCueMath.TryUnprojectLocal(projection, viewport, out Vector3 local)) return false;
            direction = (viewRotation * local).normalized;
            return FaaRotorcraftCueMath.Finite(direction);
        }

        private float RungRoll(float elevation, float bearing, float lateral) =>
            ToPixel(Rung(elevation, bearing, -lateral), out Vector2 l) && ToPixel(Rung(elevation, bearing, lateral), out Vector2 r) && (r - l).sqrMagnitude > .01f
                ? FaaRotorcraftCueMath.UprightDegrees(Mathf.Atan2(r.y - l.y, r.x - l.x) * Mathf.Rad2Deg) : 0f;

        // ---- Attitude: horizon, ladder, waterline, chevrons ---------------------------------------

        /// <summary>
        /// Wide earth horizon (the primary orientation cue). Built only where it is on screen, gapped symmetrically around the
        /// aircraft reference, around every protected HUD element and around the waterline and FPV (M8), full intensity inside the
        /// attitude window and quieter outside. Pieces shorter than 1.5 deg are left out (m3). Returns false when nothing is drawn.
        /// </summary>
        private bool DrawHorizon(float heading)
        {
            Vector3 up = EarthVector(Vector3.up), forward = viewRotation * Vector3.forward;
            Vector3 level = forward - up * Vector3.Dot(forward, up);
            if (level.sqrMagnitude < .0004f) return false; // Looking within ~1 degree of vertical: no horizon in view.
            level.Normalize();
            Vector3 side = Vector3.Cross(up, level);
            const float spread = .2f; // radians either side of the level forward direction; both points are in front of the camera.
            if (!ToPixel(level * Mathf.Cos(spread) - side * Mathf.Sin(spread), out Vector2 p0) ||
                !ToPixel(level * Mathf.Cos(spread) + side * Mathf.Sin(spread), out Vector2 p1)) return false;
            Vector2 d = p1 - p0;
            float length = d.magnitude;
            if (length < .5f || !FaaRotorcraftCueMath.Finite(length)) return false;
            Rect area = pixels;
            // Native XR: the mono view frustum is extended so neither eye sees the horizon end early.
            if (nativeXr) { float mx = area.width * .25f, my = area.height * .25f; area = Rect.MinMaxRect(area.xMin - mx, area.yMin - my, area.xMax + mx, area.yMax + my); }
            // Parametrize from the line point nearest the screen centre (robust when p0/p1 project far off screen).
            Vector2 direction = d / length, nearest = p0 + direction * Vector2.Dot(area.center - p0, direction);
            float span = area.width + area.height;
            Vector2 a = nearest - direction * span, b = nearest + direction * span;
            if (!FaaHudKeepOut.ClipSegment(area, ref a, ref b)) return false; // Horizon off screen.
            Vector2 ab = b - a;
            float ab2 = ab.sqrMagnitude;
            if (ab2 < 1f) return false;
            int cuts = 0;
            if (ToPixel(Ray(0f, heading - ladderInner), out Vector2 g0) && ToPixel(Ray(0f, heading + ladderInner), out Vector2 g1))
                AddCut(ref cuts, Param(g0, a, ab, ab2), Param(g1, a, ab, ab2));
            Vector2 centre = windowPx.center;
            for (int i = 0; i < keepOuts.Count; i++)
            {
                Rect r = keepOuts[i];
                if (r.Contains(centre)) continue; // Attitude keeps priority over anything covering its own field.
                Vector2 ca = a, cb = b;
                if (FaaHudKeepOut.ClipSegment(r, ref ca, ref cb)) AddCut(ref cuts, Param(ca, a, ab, ab2), Param(cb, a, ab, ab2));
            }
            for (int i = 0; i < occluders.Count; i++) // M8: the horizon breaks around the waterline and the FPV.
            {
                Vector2 ca = a, cb = b;
                if (FaaHudKeepOut.ClipSegment(occluders[i], ref ca, ref cb)) AddCut(ref cuts, Param(ca, a, ab, ab2), Param(cb, a, ab, ab2));
            }
            float w0 = 2f, w1 = -1f;
            Vector2 wa = a, wb = b;
            if (FaaHudKeepOut.ClipSegment(windowPx, ref wa, ref wb)) { w0 = Param(wa, a, ab, ab2); w1 = Param(wb, a, ab, ab2); if (w0 > w1) { float t = w0; w0 = w1; w1 = t; } }
            float fade = Mathf.Max(1f, Mathf.Max(fadeX, fadeY)) / Mathf.Sqrt(ab2);
            for (int i = 1; i < cuts; i++) // insertion sort, at most a few dozen cuts
            {
                float s = cutStart[i], e = cutEnd[i]; int j = i - 1;
                while (j >= 0 && cutStart[j] > s) { cutStart[j + 1] = cutStart[j]; cutEnd[j + 1] = cutEnd[j]; j--; }
                cutStart[j + 1] = s; cutEnd[j + 1] = e;
            }
            // m3: no slivers between cuts or at the screen edge.
            float shortest = focal.x * Mathf.Tan(MinHorizonPieceDegrees * Mathf.Deg2Rad) / Mathf.Sqrt(ab2);
            float begin = 0f;
            bool drawn = false;
            for (int i = 0; i <= cuts; i++)
            {
                float end = i < cuts ? cutStart[i] : 1f;
                if (end - begin >= shortest) { HorizonPiece(a, ab, begin, end, w0, w1, fade); drawn = true; }
                if (i < cuts) begin = Mathf.Max(begin, cutEnd[i]);
            }
            return drawn;
        }

        /// <summary>
        /// Standards 3.1 / 2.7: when the true horizon is off screen, a dashed horizon is clamped just inside the attitude-window edge
        /// nearest to it, parallel to the true horizon, with an open caret pointing toward it, so a sky/ground sense is always present.
        /// Nothing is drawn while the true horizon still crosses the window.
        /// </summary>
        private void DrawOffScaleHorizon()
        {
            if (windowPx.width <= 2f || windowPx.height <= 2f) return;
            Vector3 up = EarthVector(Vector3.up);
            Vector2 c = windowPx.center;
            if (!FromPixel(c, out Vector3 dc)) return;
            int sky = 0;
            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector2((i & 1) == 0 ? windowPx.xMin : windowPx.xMax, (i & 2) == 0 ? windowPx.yMin : windowPx.yMax);
                if (!FromPixel(corner, out Vector3 dir)) return;
                if (Vector3.Dot(dir, up) > 0f) sky++;
            }
            if (sky != 0 && sky != 4) return;
            float step = Mathf.Max(4f, .1f * Mathf.Min(windowPx.width, windowPx.height)), fc = Vector3.Dot(dc, up);
            if (!FromPixel(c + new Vector2(step, 0f), out Vector3 dx) || !FromPixel(c + new Vector2(0f, step), out Vector3 dy)) return;
            Vector2 skyward = new Vector2(Vector3.Dot(dx, up) - fc, Vector3.Dot(dy, up) - fc);
            skyward = skyward.sqrMagnitude > 1e-12f ? skyward.normalized : Vector2.up;
            Vector2 toward = sky == 4 ? -skyward : skyward, along = new Vector2(skyward.y, -skyward.x);
            float inset = Mathf.Max(capPixels, 2f * strokePixels + haloPixels);
            float tx = Mathf.Abs(toward.x) > 1e-4f ? (toward.x > 0f ? windowPx.xMax - c.x : c.x - windowPx.xMin) / Mathf.Abs(toward.x) : float.PositiveInfinity;
            float ty = Mathf.Abs(toward.y) > 1e-4f ? (toward.y > 0f ? windowPx.yMax - c.y : c.y - windowPx.yMin) / Mathf.Abs(toward.y) : float.PositiveInfinity;
            float reach = Mathf.Min(tx, ty) - inset;
            if (!(reach > 0f)) return;
            Vector2 q = c + toward * reach, a = q - along * (windowPx.width + windowPx.height), b = q + along * (windowPx.width + windowPx.height);
            if (!FaaHudKeepOut.ClipSegment(windowPx, ref a, ref b)) return;
            HorizonOffScale = true;
            const int dashes = 9;
            for (int k = 0; k < dashes; k += 2)
                PixelLine(Vector2.Lerp(a, b, k / (float)dashes), Vector2.Lerp(a, b, (k + 1) / (float)dashes), cueColor, primaryStroke, occluders);
            float arm = .8f * capPixels;
            Vector2 apex = q + toward * (.8f * inset);
            PixelLine(q - along * arm, apex, cueColor, primaryStroke, occluders);
            PixelLine(apex, q + along * arm, cueColor, primaryStroke, occluders);
        }

        private static float Param(Vector2 p, Vector2 a, Vector2 ab, float ab2) => Vector2.Dot(p - a, ab) / ab2;
        private void AddCut(ref int cuts, float s0, float s1)
        {
            if (cuts >= cutStart.Length || !FaaRotorcraftCueMath.Finite(s0) || !FaaRotorcraftCueMath.Finite(s1)) return;
            float lo = Mathf.Clamp01(Mathf.Min(s0, s1)), hi = Mathf.Clamp01(Mathf.Max(s0, s1));
            if (hi <= lo) return;
            cutStart[cuts] = lo; cutEnd[cuts] = hi; cuts++;
        }

        private void HorizonPiece(Vector2 a, Vector2 ab, float s0, float s1, float w0, float w1, float fade)
        {
            // About 8-degree chords keep the stroke width uniform; window/fade boundaries are exact break points.
            float step = Mathf.Max(.002f, focal.x * Mathf.Tan(8f * Mathf.Deg2Rad) / ab.magnitude);
            float s = s0;
            for (int guard = 0; s < s1 - 1e-5f && guard < 256; guard++)
            {
                float next = Mathf.Min(s1, s + step);
                next = Break(s, next, w0); next = Break(s, next, w0 + fade); next = Break(s, next, w1 - fade); next = Break(s, next, w1);
                if (FromPixel(a + ab * s, out Vector3 da) && FromPixel(a + ab * next, out Vector3 db))
                    Quad(da * radius, db * radius, HorizonTint(s, w0, w1, fade), HorizonTint(next, w0, w1, fade), primaryStroke, true);
                s = next;
            }
        }
        private static float Break(float s, float next, float b) => b > s + 1e-5f && b < next ? b : next;
        private Color HorizonTint(float s, float w0, float w1, float fade)
        {
            float k = w1 > w0 && s >= w0 && s <= w1 ? Mathf.Clamp01(Mathf.Min(s - w0, w1 - s) / Mathf.Max(1e-5f, fade)) : 0f;
            return FaaHudStyle.Dim(cueColor, Mathf.Lerp(horizonOutsideWindowAlpha, 1f, k));
        }

        /// <summary>
        /// 5-degree ladder confined to the attitude window. Rungs use constant screen angle (CONF-09); numerals sit at both ends and
        /// rotate with the rung (CONF-10). Positive rungs solid, negative dashed, hooks toward the horizon. A rung is drawn only as a
        /// complete pair (M7/C3): both halves (inner end, outer end and hook) inside the window and both numerals placeable inside it,
        /// clear of other labels and of the waterline/FPV; otherwise neither half is drawn, so no one-sided, unlabelled or edge-faded
        /// rung reaches the bank-scale end marks. Rungs break around the waterline and FPV (M8). No half-step ticks (m3).
        /// </summary>
        private void DrawLadder(float heading)
        {
            float inner = ladderInner, outer = rungOuter, em = capDegrees / CapEm;
            float numeral = 3f * GlyphAdvanceEm * em;
            float reach = focal.y * Mathf.Tan(Mathf.Min(60f, outer + LabelGapDegrees + numeral + 1f) * Mathf.Deg2Rad);
            Rect cull = Rect.MinMaxRect(windowPx.xMin - reach, windowPx.yMin - reach, windowPx.xMax + reach, windowPx.yMax + reach);
            float step = (outer - inner) / 6f;
            for (int index = 0; index < PitchText.Length; index++)
            {
                int pitch = -85 + index * 5;
                if (pitch == 0) continue;
                if (!ToPixel(Ray(pitch, heading), out Vector2 centre) || !cull.Contains(centre)) continue;
                float toward = -Mathf.Sign(pitch);
                // Recovery guidance is independent of the rung's own placement (standards 2.7).
                if (isUnusual && pitch % 10 == 0 && (pitch >= FaaRotorcraftCueMath.UnusualPitchUpEnter || pitch <= FaaRotorcraftCueMath.UnusualPitchDownEnter))
                    Chevron(pitch, heading);
                if (!RungHalfInside(pitch, heading, -1f, inner, outer, toward) || !RungHalfInside(pitch, heading, 1f, inner, outer, toward)) continue;
                string text = PitchText[index];
                float roll = RungRoll(pitch, heading, outer);
                float offset = outer + LabelGapDegrees + .5f * text.Length * GlyphAdvanceEm * em;
                Vector3 leftText = Rung(pitch, heading, -offset) * radius, rightText = Rung(pitch, heading, offset) * radius;
                if (!LabelBox(text, leftText, roll, Zone.Window, out Rect leftBox) || !LabelBox(text, rightText, roll, Zone.Window, out Rect rightBox) ||
                    leftBox.Overlaps(rightBox)) continue;
                LastRungCount++;
                for (int side = -1; side <= 1; side += 2)
                {
                    for (int s = 0; s < 6; s++)
                    {
                        if (pitch < 0 && (s & 1) != 0) continue;
                        ClippedLine(Rung(pitch, heading, side * (inner + s * step)), Rung(pitch, heading, side * (inner + (s + 1) * step)), cueColor, rungStroke, true);
                    }
                    ClippedLine(Rung(pitch, heading, side * outer), Rung(pitch + toward * HookDegrees, heading, side * outer), cueColor, rungStroke, true);
                }
                CommitLabel(index * 2, text, leftText, cueColor, roll, leftBox);
                CommitLabel(index * 2 + 1, text, rightText, cueColor, roll, rightBox);
            }
        }

        /// <summary>A rung half fits when its inner end, outer end and hook end all lie inside the window (its chords are straight on screen).</summary>
        private bool RungHalfInside(int pitch, float heading, float side, float inner, float outer, float toward) =>
            InWindow(Rung(pitch, heading, side * inner)) && InWindow(Rung(pitch, heading, side * outer)) &&
            InWindow(Rung(pitch + toward * HookDegrees, heading, side * outer));
        private bool InWindow(Vector3 direction) => ToPixel(direction, out Vector2 p) && windowPx.Contains(p);

        /// <summary>Red recovery chevron on an extreme rung, pointing toward the horizon (AC 25-11B A.2.2; AC 23.1311-1C 17.4.b).</summary>
        private void Chevron(int pitch, float heading)
        {
            float sign = Mathf.Sign(pitch);
            Vector3 apex = Rung(pitch - sign * 2.5f, heading, 0f);
            Vector3 left = Rung(pitch + sign, heading, -2f), right = Rung(pitch + sign, heading, 2f);
            ClippedLine(left, apex, FaaHudStyle.Red, primaryStroke, true);
            ClippedLine(apex, right, FaaHudStyle.Red, primaryStroke, true);
        }

        /// <summary>
        /// Gull-wing waterline from validated pitch/roll/true heading (CONF-01): +pitch puts it ABOVE the horizon. It is never dropped (C3):
        /// when it does not fit inside the attitude window it is pinned just inside the nearest edge, dashed and ghosted (W LIMIT, as the
        /// limited FPV), keeping its screen roll. It is cut under the FPV symbol so the two never read as one glyph (M8), and its padded
        /// bounds then break the horizon, rungs and FPA around it.
        /// </summary>
        private void DrawWaterline()
        {
            if (!waterlineValid) return;
            LastWaterlineDirection = waterlineDirection;
            WaterlineVisible = true;
            float u = waterlineUnit;
            Rect box = waterlineBox;
            if (waterlineProjected && FaaRotorcraftCueMath.Contains(windowPx, box))
            {
                Vector3 previous = FaaRotorcraftCueMath.BodyPoint(waterlineBody, WaterlineShape[0].x * u, WaterlineShape[0].y * u);
                for (int i = 1; i < WaterlineShape.Length; i++)
                {
                    Vector3 next = FaaRotorcraftCueMath.BodyPoint(waterlineBody, WaterlineShape[i].x * u, WaterlineShape[i].y * u);
                    ClippedLine(previous, next, cueColor, primaryStroke, false, symbolCuts);
                    previous = next;
                }
            }
            else
            {
                WaterlineLimited = true;
                // Screen roll of the wings (body right axis seen from the camera) keeps the ghost oriented like the real symbol.
                Vector3 wing = inverseViewRotation * (waterlineBody * Vector3.right);
                float roll = new Vector2(wing.x, wing.y).sqrMagnitude > 1e-8f ? Mathf.Atan2(wing.y, wing.x) : 0f;
                float cs = Mathf.Cos(roll), sn = Mathf.Sin(roll), unit = focal.y * Mathf.Tan(u * Mathf.Deg2Rad);
                Rect shape = WaterlineScreenBox(Vector2.zero, cs, sn, unit); // relative to the symbol centre
                Vector2 pinned;
                if (waterlineProjected) pinned = waterlinePixel + FaaRotorcraftCueMath.ClampInside(windowPx, WaterlineScreenBox(waterlinePixel, cs, sn, unit));
                else
                {
                    // Behind the view: pin along the screen direction of the reference, from the window centre.
                    Vector3 local = inverseViewRotation * waterlineDirection;
                    Vector2 d = new Vector2(local.x, local.y);
                    Rect inner = Rect.MinMaxRect(windowPx.xMin - shape.xMin, windowPx.yMin - shape.yMin, windowPx.xMax - shape.xMax, windowPx.yMax - shape.yMax);
                    pinned = inner.width > 0f && inner.height > 0f ? EdgePoint(inner, d.sqrMagnitude > 1e-10f ? d.normalized : Vector2.down) : windowPx.center;
                }
                Color ghost = FaaHudStyle.Dim(cueColor, GhostAlpha);
                Vector2 previous = pinned + WaterlinePoint(0, cs, sn, unit);
                for (int i = 1; i < WaterlineShape.Length; i++)
                {
                    Vector2 next = pinned + WaterlinePoint(i, cs, sn, unit);
                    PixelLine(previous, Vector2.Lerp(previous, next, .42f), ghost, primaryStroke, symbolCuts);
                    PixelLine(Vector2.Lerp(previous, next, .58f), next, ghost, primaryStroke, symbolCuts);
                    previous = next;
                }
                box = WaterlineScreenBox(pinned, cs, sn, unit);
            }
            Rect padded = Pad(box, occluderPad);
            occluders.Add(padded); placed.Add(padded);
        }
        private static Vector2 WaterlinePoint(int i, float cs, float sn, float unit)
        {
            Vector2 s = WaterlineShape[i];
            return new Vector2(s.x * cs - s.y * sn, s.x * sn + s.y * cs) * unit;
        }
        private Rect WaterlineScreenBox(Vector2 centre, float cs, float sn, float unit)
        {
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < WaterlineShape.Length; i++)
            {
                Vector2 p = centre + WaterlinePoint(i, cs, sn, unit);
                x0 = Mathf.Min(x0, p.x); y0 = Mathf.Min(y0, p.y); x1 = Mathf.Max(x1, p.x); y1 = Mathf.Max(y1, p.y);
            }
            float s = .5f * strokePixels + haloPixels;
            return Rect.MinMaxRect(x0 - s, y0 - s, x1 + s, y1 + s);
        }
        private static Rect Pad(Rect r, float pad) => Rect.MinMaxRect(r.xMin - pad, r.yMin - pad, r.xMax + pad, r.yMax + pad);
        /// <summary>Point where the ray from the rect centre along <paramref name="d"/> leaves the rect.</summary>
        private static Vector2 EdgePoint(Rect r, Vector2 d)
        {
            Vector2 c = r.center;
            float t = float.PositiveInfinity;
            if (Mathf.Abs(d.x) > 1e-5f) t = Mathf.Min(t, (d.x > 0f ? r.xMax - c.x : c.x - r.xMin) / Mathf.Abs(d.x));
            if (Mathf.Abs(d.y) > 1e-5f) t = Mathf.Min(t, (d.y > 0f ? r.yMax - c.y : c.y - r.yMin) / Mathf.Abs(d.y));
            return float.IsInfinity(t) ? c : c + d * t;
        }

        // ---- References: FPA, FPV, flags -------------------------------------------------------

        /// <summary>
        /// Selected-FPA reference: approach context only, cyan long dashes (distinct from negative rungs), clipped to the window and broken
        /// around the waterline/FPV. Its label is measured from the font and placed right of the reference, else mirrored left, inside the window.
        /// </summary>
        private void DrawFpa(float bearing)
        {
            float inner = ladderInner, outer = rungOuter, span = outer - inner;
            float dash = span * .9f / 3.3f, gap = span * .3f / 3.3f, fpa = selectedFpaDegrees;
            Color tint = FaaHudStyle.WithAlpha(FaaHudStyle.Cyan, .95f);
            for (int side = -1; side <= 1; side += 2)
                for (int k = 0; k < 3; k++)
                {
                    float from = inner + k * (dash + gap);
                    ClippedLine(Rung(fpa, bearing, side * from), Rung(fpa, bearing, side * (from + dash)), tint, rungStroke, true);
                }
            if (fpaLabelText == null || !fpaLabelValue.Equals(fpa) || fpaLabelEm < 0f)
            {
                fpaLabelValue = fpa;
                fpaLabelText = "FPA " + fpa.ToString("+0.0;-0.0;0.0", System.Globalization.CultureInfo.InvariantCulture) + "°";
                fpaLabelEm = MeasureEm(FpaSlot, fpaLabelText);
            }
            float em = capDegrees / CapEm, roll = RungRoll(fpa, bearing, outer);
            float offset = outer + LabelGapDegrees + .5f * fpaLabelEm * em;
            Vector3 right = Rung(fpa, bearing, offset) * radius, left = Rung(fpa, bearing, -offset) * radius;
            if (LabelBox(fpaLabelText, right, roll, Zone.Window, out Rect box, fpaLabelEm)) CommitLabel(FpaSlot, fpaLabelText, right, tint, roll, box);
            else if (LabelBox(fpaLabelText, left, roll, Zone.Window, out box, fpaLabelEm)) CommitLabel(FpaSlot, fpaLabelText, left, tint, roll, box);
        }

        /// <summary>
        /// Plans the FPV before anything is drawn: 0 = normal FPV, 1 = FOV-limited (ghost at the window edge), 2 = behind/out of view
        /// (hidden, annunciated). Sets <see cref="fpvPlanned"/> when a symbol will be drawn.
        /// </summary>
        private int PlanFpv(Vector3 direction)
        {
            fpvPlanned = false;
            if (!ToPixel(direction, out Vector2 p)) return 2;
            float r = focal.y * Mathf.Tan(fpvRadius * Mathf.Deg2Rad);
            fpvPixels = r;
            Rect inner = Rect.MinMaxRect(windowPx.xMin + r * 3f, windowPx.yMin + r, windowPx.xMax - r * 3f, windowPx.yMax - r * 2.2f);
            if (inner.width > 0f && inner.height > 0f && inner.Contains(p))
            {
                fpvPlanned = true; fpvGhost = false; fpvPixel = p; fpvDirection = direction;
                return 0;
            }
            // FOV-limited: a dashed ghost on the window edge, along the line from the window centre (AC 25-11B F.3.3.3, 7.5 Table F-2).
            Rect border = Rect.MinMaxRect(windowPx.xMin + r * 1.3f, windowPx.yMin + r * 1.3f, windowPx.xMax - r * 1.3f, windowPx.yMax - r * 1.3f);
            if (border.width <= 0f || border.height <= 0f) return 1;
            Vector2 c = border.center, d = p - c;
            float t = 1f;
            if (Mathf.Abs(d.x) > .001f) t = Mathf.Min(t, (d.x > 0f ? border.xMax - c.x : c.x - border.xMin) / Mathf.Abs(d.x));
            if (Mathf.Abs(d.y) > .001f) t = Mathf.Min(t, (d.y > 0f ? border.yMax - c.y : c.y - border.yMin) / Mathf.Abs(d.y));
            Vector2 g = c + d * Mathf.Clamp01(t);
            if (FromPixel(g, out Vector3 ghost)) { fpvPlanned = true; fpvGhost = true; fpvPixel = g; fpvDirection = ghost; }
            return 1;
        }

        /// <summary>Occlusion bands of the planned FPV (ring, both wings, fin), padded by stroke, halo and margin.</summary>
        private void FpvCuts(List<Rect> into)
        {
            float r = fpvPixels, band = .5f * strokePixels + haloPixels + occluderPad, x = fpvPixel.x, y = fpvPixel.y;
            into.Add(Rect.MinMaxRect(x - r - band, y - r - band, x + r + band, y + r + band));
            if (fpvGhost) return;
            into.Add(Rect.MinMaxRect(x - 3f * r - band, y - band, x - r, y + band));
            into.Add(Rect.MinMaxRect(x + r, y - band, x + 3f * r + band, y + band));
            into.Add(Rect.MinMaxRect(x - band, y + r, x + band, y + 2f * r + band));
        }

        private void DrawPlannedFpv()
        {
            FpvSymbol(fpvDirection, fpvGhost);
            float r = fpvPixels, x = fpvPixel.x, y = fpvPixel.y;
            // Lines break around the FPV's own bands (not its whole bounding box); labels avoid its bounding box.
            for (int i = 0; i < symbolCuts.Count; i++) occluders.Add(symbolCuts[i]);
            placed.Add(fpvGhost ? Rect.MinMaxRect(x - r * 1.3f, y - r * 1.3f, x + r * 1.3f, y + r * 1.3f) : Rect.MinMaxRect(x - r * 3f, y - r, x + r * 3f, y + r * 2.2f));
        }

        private void FpvSymbol(Vector3 direction, bool ghost)
        {
            Vector3 centre = direction * radius;
            float unit = radius * Mathf.Tan(fpvRadius * Mathf.Deg2Rad);
            Color tint = ghost ? FaaHudStyle.Dim(cueColor, GhostAlpha) : cueColor;
            Ring(centre, unit, tint, primaryStroke, ghost, true);
            if (ghost) return; // Limited FPV: dashed ring only, never mistaken for a conformal one.
            Vector3 right = viewRotation * Vector3.right, up = viewRotation * Vector3.up;
            Quad(centre - right * unit, centre - right * unit * 3f, tint, tint, primaryStroke, true);
            Quad(centre + right * unit, centre + right * unit * 3f, tint, tint, primaryStroke, true);
            Quad(centre + up * unit, centre + up * unit * 2f, tint, tint, primaryStroke, true);
        }

        private void Flag(int slot, string text, Vector2 pixel, Color color, bool boxed)
        {
            if (!FromPixel(pixel, out Vector3 direction)) return;
            Label(slot, text, direction * radius, color, 0f, Zone.Flag);
            if (!boxed) return;
            float em = capPixels / CapEm, hw = .5f * text.Length * GlyphAdvanceEm * em + .45f * em, hh = .85f * em;
            if (!FromPixel(pixel + new Vector2(-hw, -hh), out Vector3 a) || !FromPixel(pixel + new Vector2(hw, -hh), out Vector3 b) ||
                !FromPixel(pixel + new Vector2(hw, hh), out Vector3 c) || !FromPixel(pixel + new Vector2(-hw, hh), out Vector3 d)) return;
            Quad(a * radius, b * radius, color, color, primaryStroke, true); Quad(b * radius, c * radius, color, color, primaryStroke, true);
            Quad(c * radius, d * radius, color, color, primaryStroke, true); Quad(d * radius, a * radius, color, color, primaryStroke, true);
        }

        // ---- Scene references and hover ----------------------------------------------------------

        private void DrawSceneReferences(bool labelsAllowed)
        {
            // Bounded authored/explicit references only. No synthetic wire/terrain hazard detections.
            int count = 0;
            Vector3 viewUp = viewRotation * Vector3.up, viewRight = viewRotation * Vector3.right;
            float symbolDegrees = Mathf.Max(.42f, FaaRotorcraftCueMath.AngularSize(17f, view.fieldOfView, pixels.height, nativeXr));
            var anchors = FaaRotorcraftCueAnchor.EnumerateActive();
                while (anchors.MoveNext() && count < MaxSceneCues)
                {
                    var anchor = anchors.Current;
                    if (anchor == null || !anchor.TryPosition(out Vector3 position)) continue;
                    bool landing = anchor.kind == FaaRotorcraftCueAnchor.CueType.LandingArea;
                    bool hoverTarget = anchor.kind == FaaRotorcraftCueAnchor.CueType.HoverReference;
                    if ((landing || hoverTarget) && !anchor.selected) continue;
                    Vector3 center = position - eyePosition;
                    float distance = center.magnitude;
                    if (distance <= view.nearClipPlane + .1f || distance > anchor.maximumRangeMeters || distance > view.farClipPlane * .9f) continue;
                    if (!FaaRotorcraftCueMath.InView(view, center) || !ToPixel(center, out Vector2 p) || InKeepOut(p)) continue;
                    VisibleSceneCueCount++;
                    float size = distance * Mathf.Tan(symbolDegrees * Mathf.Deg2Rad);
                    Vector3 r = viewRight * size, u = viewUp * size;
                    bool obstacle = anchor.kind == FaaRotorcraftCueAnchor.CueType.Obstacle;
                    Color tint = obstacle ? FaaHudStyle.WithAlpha(FaaHudStyle.Amber, .95f) : cueColor;
                    if (obstacle)
                    {
                        SceneLine(center + u, center - u - r, tint); SceneLine(center - u - r, center - u + r, tint); SceneLine(center - u + r, center + u, tint);
                        if (anchor.lineEnd != null && FaaRotorcraftCueMath.Finite(anchor.lineEnd.position))
                            SceneLine(center, anchor.lineEnd.position - eyePosition, tint);
                    }
                    else
                    {
                        SceneLine(center + u, center + r, tint); SceneLine(center + r, center - u, tint);
                        SceneLine(center - u, center - r, tint); SceneLine(center - r, center + u, tint);
                    }
                    if (landing && FaaRotorcraftCueMath.Finite(anchor.areaMeters.x) && FaaRotorcraftCueMath.Finite(anchor.areaMeters.y) &&
                        anchor.areaMeters.x > 0f && anchor.areaMeters.y > 0f)
                    {
                        Vector3 right = anchor.transform.right * Mathf.Min(1000f, anchor.areaMeters.x) * .5f;
                        Vector3 forward = anchor.transform.forward * Mathf.Min(1000f, anchor.areaMeters.y) * .5f;
                        SceneLine(center - right - forward, center + right - forward, tint);
                        SceneLine(center + right - forward, center + right + forward, tint);
                        SceneLine(center + right + forward, center - right + forward, tint);
                        SceneLine(center - right + forward, center - right - forward, tint);
                    }
                    sceneAnchor[count] = anchor; sceneCentre[count] = center; sceneDistance[count] = distance; sceneSize[count] = size;
                    sceneTint[count] = tint; sceneLabelled[count] = false; count++;
                }
            if (!labelsAllowed) return; // Unusual attitude: symbols only.
            int labels = Mathf.Min(Mathf.Clamp(labelledSceneCueLimit, 1, MaxSceneCues), count);
            for (int n = 0; n < labels; n++)
            {
                int best = -1;
                for (int i = 0; i < count; i++) if (!sceneLabelled[i] && (best < 0 || sceneDistance[i] < sceneDistance[best])) best = i;
                if (best < 0) break;
                sceneLabelled[best] = true;
                Label(SceneSlot + n, SceneText(sceneAnchor[best], sceneDistance[best]), sceneCentre[best] - viewUp * sceneSize[best] * 2.3f,
                    sceneTint[best], 0f, Zone.KeepOut);
            }
            for (int i = 0; i < count; i++) sceneAnchor[i] = null;
        }

        private string SceneText(FaaRotorcraftCueAnchor anchor, float distance)
        {
            if (!sceneLabels.TryGetValue(anchor, out var cache))
            {
                if (sceneLabels.Count > 64) sceneLabels.Clear();
                cache = new SceneLabel();
                sceneLabels[anchor] = cache;
            }
            int bucket = FaaRotorcraftCueMath.CueDistanceBucket(distance);
            if (cache.Text != null && cache.Bucket == bucket && cache.Kind == anchor.kind && ReferenceEquals(cache.Id, anchor.identifier)) return cache.Text;
            cache.Bucket = bucket; cache.Kind = anchor.kind; cache.Id = anchor.identifier;
            string id = string.IsNullOrWhiteSpace(anchor.identifier) ? "REF" : anchor.identifier;
            if (id.Length > 18) id = id.Substring(0, 18);
            string prefix = anchor.kind == FaaRotorcraftCueAnchor.CueType.LandingArea ? "LZ REF " :
                anchor.kind == FaaRotorcraftCueAnchor.CueType.HoverReference ? "HOVER REF " :
                anchor.kind == FaaRotorcraftCueAnchor.CueType.Obstacle ? "OBS " : "WPT ";
            cache.Text = prefix + id + " " + FaaRotorcraftCueMath.CueDistance(distance);
            return cache.Text;
        }

        private bool InKeepOut(Vector2 p)
        {
            for (int i = 0; i < keepOuts.Count; i++) if (keepOuts[i].Contains(p)) return true;
            return false;
        }

        /// <summary>A georeferenced line at true depth; pieces whose midpoint falls in a protected HUD area are left out.</summary>
        private void SceneLine(Vector3 a, Vector3 b, Color tint)
        {
            const int pieces = 6;
            for (int i = 0; i < pieces; i++)
            {
                Vector3 pa = Vector3.Lerp(a, b, i / (float)pieces), pb = Vector3.Lerp(a, b, (i + 1) / (float)pieces);
                if (ToPixel((pa + pb) * .5f, out Vector2 mid) && InKeepOut(mid)) continue;
                Quad(pa, pb, tint, tint, rungStroke, false);
            }
        }

        private void DrawHover(Vector3 velocity, float heading)
        {
            // This separate plan-view instrument stays head-fixed. It is not a forward-looking FPV.
            float scalePixels = focal.y * Mathf.Tan(HoverScaleDegrees * Mathf.Deg2Rad), em = capPixels / CapEm;
            int choice = ChooseHoverCandidate(scalePixels, em);
            Vector2 cp = new Vector2(pixels.x + HoverCandidates[choice].x * pixels.width, pixels.y + HoverCandidates[choice].y * pixels.height);
            if (!FromPixel(cp, out Vector3 direction)) return;
            Vector3 center = direction * radius;
            float scale = radius * Mathf.Tan(HoverScaleDegrees * Mathf.Deg2Rad);
            Vector3 right = viewRotation * Vector3.right, up = viewRotation * Vector3.up;
            Color dim = FaaHudStyle.Dim(cueColor, .75f);
            Ring(center, scale, dim, rungStroke, false, true);
            Quad(center - right * scale, center + right * scale, dim, dim, minorStroke, false);
            Quad(center - up * scale, center + up * scale, dim, dim, minorStroke, false);
            Vector2 drift = FaaRotorcraftCueMath.HoverVelocity(velocity, heading);
            float fullScale = Mathf.Max(1f, hoverFullScaleKnots);
            bool inScale = drift.magnitude <= fullScale;
            if (inScale)
            {
                Vector3 endpoint = center + (right * drift.x + up * drift.y) * (scale / fullScale);
                Quad(center, endpoint, cueColor, cueColor, primaryStroke, true);
                Ring(endpoint, scale * .1f, cueColor, primaryStroke, false, true);
            }
            placed.Add(HoverBounds(cp, scalePixels, em));
            int key = Mathf.RoundToInt(fullScale) * 2 + (inScale ? 0 : 1);
            if (hoverLabelText == null || key != hoverLabelKey)
            {
                hoverLabelKey = key;
                string range = Mathf.RoundToInt(fullScale).ToString(System.Globalization.CultureInfo.InvariantCulture);
                hoverLabelText = inScale ? "HOVER " + range + " KT" : "DRIFT >" + range + " KT";
            }
            Label(HoverSlot, hoverLabelText, center - up * scale * 1.6f, cueColor, 0f, Zone.Flag);
        }

        private static Rect HoverBounds(Vector2 c, float r, float em)
        {
            float hw = Mathf.Max(r, 6.5f * GlyphAdvanceEm * em);
            return Rect.MinMaxRect(c.x - hw, c.y - r - 2.1f * em, c.x + hw, c.y + r);
        }

        /// <summary>First candidate clear of the attitude window and every protected area (kept while it stays clear); else least overlap.</summary>
        private int ChooseHoverCandidate(float r, float em)
        {
            if (hoverCandidate >= 0 && hoverCandidate < HoverCandidates.Length && HoverOverlap(hoverCandidate, r, em) <= 0f) return hoverCandidate;
            int best = 0; float least = float.PositiveInfinity;
            for (int i = 0; i < HoverCandidates.Length; i++)
            {
                float overlap = HoverOverlap(i, r, em);
                if (overlap < least) { least = overlap; best = i; }
                if (overlap <= 0f) break;
            }
            hoverCandidate = best;
            return best;
        }

        private float HoverOverlap(int candidate, float r, float em)
        {
            Vector2 c = new Vector2(pixels.x + HoverCandidates[candidate].x * pixels.width, pixels.y + HoverCandidates[candidate].y * pixels.height);
            Rect b = HoverBounds(c, r, em);
            float overlap = FaaRotorcraftCueMath.Contains(pixels, b) ? 0f : b.width * b.height;
            overlap += Intersection(b, windowPx);
            for (int i = 0; i < keepOuts.Count; i++) overlap += Intersection(b, keepOuts[i]);
            return overlap;
        }
        private static float Intersection(Rect a, Rect b)
        {
            float w = Mathf.Min(a.xMax, b.xMax) - Mathf.Max(a.xMin, b.xMin), h = Mathf.Min(a.yMax, b.yMax) - Mathf.Max(a.yMin, b.yMin);
            return w > 0f && h > 0f ? w * h : 0f;
        }

        // ---- Primitives --------------------------------------------------------------------------

        /// <summary>
        /// Great-circle chord between unit directions a-b, optionally clipped to the attitude window in screen space, and split around
        /// <paramref name="cuts"/> (pixel rects; default: the waterline/FPV occluders, M8). Every piece is unprojected exactly.
        /// </summary>
        private void ClippedLine(Vector3 a, Vector3 b, Color tint, float width, bool toWindow, List<Rect> cuts = null)
        {
            if (!ToPixel(a, out Vector2 pa) || !ToPixel(b, out Vector2 pb)) return;
            Segment(a, b, pa, pb, tint, width, toWindow, cuts ?? occluders);
        }

        /// <summary>A screen-space segment (pixels), unprojected onto the angular shell and split around <paramref name="cuts"/>.</summary>
        private void PixelLine(Vector2 pa, Vector2 pb, Color tint, float width, List<Rect> cuts)
        {
            if (!FromPixel(pa, out Vector3 a) || !FromPixel(pb, out Vector3 b)) return;
            Segment(a, b, pa, pb, tint, width, false, cuts);
        }

        private void Segment(Vector3 a, Vector3 b, Vector2 pa, Vector2 pb, Color tint, float width, bool toWindow, List<Rect> cuts)
        {
            Vector2 d = pb - pa;
            float d2 = d.sqrMagnitude;
            if (!(d2 > 1e-4f) || !FaaRotorcraftCueMath.Finite(d2)) return;
            int count = 1;
            pieceStart[0] = 0f; pieceEnd[0] = 1f;
            if (toWindow)
            {
                Vector2 ca = pa, cb = pb;
                if (!FaaHudKeepOut.ClipSegment(windowPx, ref ca, ref cb)) return;
                pieceStart[0] = Param(ca, pa, d, d2); pieceEnd[0] = Param(cb, pa, d, d2);
            }
            if (cuts != null)
                for (int i = 0; i < cuts.Count && count > 0; i++)
                {
                    Vector2 ca = pa, cb = pb;
                    if (FaaHudKeepOut.ClipSegment(cuts[i], ref ca, ref cb))
                        count = FaaRotorcraftCueMath.SubtractInterval(pieceStart, pieceEnd, count, Param(ca, pa, d, d2), Param(cb, pa, d, d2));
                }
            float crumb = 1.5f / Mathf.Sqrt(d2); // pieces under 1.5 px are not drawn
            for (int i = 0; i < count; i++)
            {
                float s = pieceStart[i], e = pieceEnd[i];
                if (e - s < crumb) continue;
                Vector3 da = a, db = b;
                if (s > 1e-4f && !FromPixel(pa + d * s, out da)) continue;
                if (e < 1f - 1e-4f && !FromPixel(pa + d * e, out db)) continue;
                Quad(da * radius, db * radius, tint, tint, width, true);
            }
        }

        private void Ring(Vector3 center, float size, Color tint, float width, bool dashed, bool halo)
        {
            Vector3 right = viewRotation * Vector3.right * size, up = viewRotation * Vector3.up * size;
            for (int i = 0; i < 40; i++)
            {
                if (dashed && (i & 1) != 0) continue;
                float a = i * Mathf.PI * 2f / 40f, b = (i + 1) * Mathf.PI * 2f / 40f;
                Quad(center + right * Mathf.Cos(a) + up * Mathf.Sin(a), center + right * Mathf.Cos(b) + up * Mathf.Sin(b), tint, tint, width, halo);
            }
        }

        /// <summary>Constant-angular-width quad between two eye-relative points; alpha follows the inspection dimming.</summary>
        private void Quad(Vector3 a, Vector3 b, Color ta, Color tb, float widthDegrees, bool halo)
        {
            if (!FaaRotorcraftCueMath.Finite(a) || !FaaRotorcraftCueMath.Finite(b)) return;
            Vector3 segment = b - a;
            if (segment.sqrMagnitude < .000001f) return;
            Vector3 side = Vector3.Cross(segment, (a + b) * .5f);
            if (side.sqrMagnitude < 1e-12f) return;
            side.Normalize();
            ta.a *= intensity; tb.a *= intensity;
            if (ta.a <= .002f && tb.a <= .002f) return;
            float near = view.nearClipPlane, da = Mathf.Max(near, a.magnitude), db = Mathf.Max(near, b.magnitude);
            if (halo && haloDegrees > 0f)
            {
                float k = Mathf.Tan((widthDegrees * .5f + haloDegrees) * Mathf.Deg2Rad);
                Emit(a, b, side * (da * k), side * (db * k), new Color(0f, 0f, 0f, ta.a * HaloAlpha), new Color(0f, 0f, 0f, tb.a * HaloAlpha), haloTriangles);
            }
            float w = Mathf.Tan(widthDegrees * .5f * Mathf.Deg2Rad);
            Emit(a, b, side * (da * w), side * (db * w), ta, tb, triangles);
        }

        private void Emit(Vector3 a, Vector3 b, Vector3 na, Vector3 nb, Color ca, Color cb, List<int> target)
        {
            int start = vertices.Count;
            vertices.Add(a - na); vertices.Add(a + na); vertices.Add(b + nb); vertices.Add(b - nb);
            colors.Add(ca); colors.Add(ca); colors.Add(cb); colors.Add(cb);
            target.Add(start); target.Add(start + 1); target.Add(start + 2);
            target.Add(start); target.Add(start + 2); target.Add(start + 3);
        }

        /// <summary>
        /// World text at a stable pool slot (text is reassigned only when it changes). Window labels must lie wholly inside the attitude
        /// window; KeepOut labels must avoid protected areas; any non-flag label overlapping a higher-priority one (or the waterline/FPV) is culled.
        /// </summary>
        private bool Label(int slot, string text, Vector3 relative, Color color, float rollDegrees, Zone zone)
        {
            if (slot < 0 || slot >= SlotCount || !LabelBox(text, relative, rollDegrees, zone, out Rect box)) return false;
            CommitLabel(slot, text, relative, color, rollDegrees, box);
            return true;
        }

        /// <summary>Screen box of a label and whether it may be placed (no side effects), so rung numerals can be placed as pairs.</summary>
        private bool LabelBox(string text, Vector3 relative, float rollDegrees, Zone zone, out Rect box, float widthEm = -1f)
        {
            box = default;
            if (string.IsNullOrEmpty(text) || worldLabelRoot == null || !ToPixel(relative, out Vector2 p) || relative.magnitude <= view.nearClipPlane) return false;
            float em = capPixels / CapEm, w = ((widthEm >= 0f ? widthEm : text.Length * GlyphAdvanceEm) + .3f) * em, h = em * 1.15f;
            float rad = rollDegrees * Mathf.Deg2Rad, cs = Mathf.Abs(Mathf.Cos(rad)), sn = Mathf.Abs(Mathf.Sin(rad));
            float bw = w * cs + h * sn, bh = w * sn + h * cs;
            box = new Rect(p.x - bw * .5f, p.y - bh * .5f, bw, bh);
            if (zone == Zone.Window && !FaaRotorcraftCueMath.Contains(windowPx, box)) return false;
            if (zone == Zone.KeepOut && FaaRotorcraftCueMath.Overlaps(box, keepOuts)) return false;
            if (zone != Zone.Flag)
                for (int i = 0; i < placed.Count; i++) if (placed[i].Overlaps(box)) return false;
            return true;
        }

        private void CommitLabel(int slot, string text, Vector3 relative, Color color, float rollDegrees, Rect box)
        {
            if (slot < 0 || slot >= SlotCount) return;
            placed.Add(box);
            var label = Slot(slot);
            slotUsed[slot] = true;
            if (!slotActive[slot]) { label.gameObject.SetActive(true); slotActive[slot] = true; }
            if (!ReferenceEquals(slotText[slot], text)) { label.text = text; slotText[slot] = text; }
            Color c = FaaHudStyle.WithAlpha(color, color.a * intensity);
            if (c != slotColor[slot]) { label.color = c; slotColor[slot] = c; }
            label.rectTransform.localPosition = relative;
            label.rectTransform.rotation = viewRotation * Quaternion.Euler(0f, 0f, rollDegrees);
            float lineDegrees = Mathf.Min(30f, capDegrees / Mathf.Max(.3f, capToLine));
            label.rectTransform.localScale = Vector3.one * (relative.magnitude * Mathf.Tan(lineDegrees * Mathf.Deg2Rad) / worldTextLineHeight);
        }

        /// <summary>Measured advance width of <paramref name="text"/> in cap-em units (called only when the text changes).</summary>
        private float MeasureEm(int slot, string text)
        {
            float estimate = text.Length * GlyphAdvanceEm;
            if (slot < 0 || slot >= SlotCount || worldLabelRoot == null || string.IsNullOrEmpty(text)) return estimate;
            var label = Slot(slot);
            float width = label.GetPreferredValues(text).x;
            return width > 0f && worldTextLineHeight > 0f ? width / worldTextLineHeight * CapEm / Mathf.Max(.3f, capToLine) : estimate;
        }

        private TextMeshPro Slot(int slot)
        {
            var existing = slots[slot];
            if (existing != null) return existing;
            var go = new GameObject("FAA Cue Label " + slot, typeof(RectTransform));
            go.transform.SetParent(worldLabelRoot, false);
            var text = go.AddComponent<TextMeshPro>();
            text.font = TMP_Settings.defaultFontAsset;
            if (labelMaterial != null) text.fontSharedMaterial = labelMaterial;
            text.fontSize = 16f; text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.richText = false;
            text.rectTransform.sizeDelta = new Vector2(340f, 24f);
            // Native TMP text uses world units, not the UI's pixel-sized font metrics.
            worldTextLineHeight = Mathf.Max(.01f, text.GetPreferredValues("0").y);
            var renderer = go.GetComponent<MeshRenderer>();
            renderer.sortingOrder = 5041;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = meshRenderer != null && meshRenderer.enabled;
            labelRenderers.Add(renderer);
            slots[slot] = text; slotActive[slot] = true; slotText[slot] = null; slotColor[slot] = new Color(0f, 0f, 0f, -1f);
            return text;
        }

        public void SetSelectedFpa(float degrees) => selectedFpaDegrees = FaaRotorcraftCueMath.SelectFpa(selectedFpaDegrees, degrees);
        public void SetSceneCuesVisible(bool visible) => showSceneCues = visible;
        public void SetHoverVisible(bool visible) => showHover = visible;
        public void SetFpaVisible(bool visible) => showFpa = visible;

        /// <summary>User action: place a declared 20m reference area on a raycast surface, never a landing-safety assessment.</summary>
        public void MarkLandingReference()
        {
            if (view == null || !HasLiveAttitude) { Feedback("LZ NOT MARKED: fresh attitude and camera required"); return; }
            Ray ray = new Ray(view.transform.position, view.transform.forward);
            int count = UnityEngine.Physics.RaycastNonAlloc(ray, landingHits, Mathf.Min(15000f, view.farClipPlane * .85f),
                landingSurfaceLayers, QueryTriggerInteraction.Ignore);
            // A saturated hit buffer is incomplete; fail closed instead of choosing a possibly occluded surface.
            if (count == landingHits.Length) { Feedback("LZ NOT MARKED: ambiguous surface intersections"); return; }
            int nearest = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                var hit = landingHits[i];
                if (hit.transform == null || aircraft != null && (hit.transform == aircraft || hit.transform.IsChildOf(aircraft))) continue;
                if (hit.distance < best) { nearest = i; best = hit.distance; }
            }
            if (nearest < 0) { Feedback("LZ NOT MARKED: look at a loaded surface with a collider"); return; }
            RaycastHit surface = landingHits[nearest];
            // This only avoids wall placement. It is not a rotorcraft landing slope limit.
            if (Vector3.Dot(surface.normal, EarthVector(Vector3.up)) < .5f)
            { Feedback("LZ NOT MARKED: selected surface is wall-like"); return; }
            ClearLandingReference();
            var go = new GameObject("Pilot-selected LZ reference (not surveyed)");
            go.transform.SetParent(surface.transform, true);
            Vector3 forward = Vector3.ProjectOnPlane(aircraft != null ? aircraft.forward : view.transform.forward, surface.normal);
            if (forward.sqrMagnitude < .001f) forward = Vector3.ProjectOnPlane(view.transform.up, surface.normal);
            go.transform.SetPositionAndRotation(surface.point + surface.normal * .05f, Quaternion.LookRotation(forward, surface.normal));
            userLandingReference = go.AddComponent<FaaRotorcraftCueAnchor>();
            userLandingReference.kind = FaaRotorcraftCueAnchor.CueType.LandingArea;
            userLandingReference.identifier = "PILOT";
            userLandingReference.Select();
            Feedback("LZ REFERENCE MARKED: declared 20m box, not a surveyed pad or landing clearance");
        }

        private void Feedback(string message) { actionFeedback = message; feedbackUntil = Time.unscaledTime + 5f; }

        public void ClearLandingReference()
        {
            if (userLandingReference == null) return;
            userLandingReference.gameObject.SetActive(false);
            Destroy(userLandingReference.gameObject);
            userLandingReference = null;
        }
    }
}
