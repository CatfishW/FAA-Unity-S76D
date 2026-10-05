using UnityEngine;
using UnityEngine.UI;
using AircraftControl.Core;
using FAA.Customization;

namespace HUDControl.Elements
{
    /// <summary>
    /// Bank Scale element for the Digital HUD.
    /// Drives the roll (sky) pointer and the slip/skid brick from measured data, 1:1 with roll (no easing on flight data).
    /// In play mode the sanitizer switches it to the procedural <see cref="FaaDigitalBankGraphic"/> (one centre and radius for the arc,
    /// pointer and brick, standard 10/20/30/60 deg ticks) and hides the legacy bitmap arc, pointer and brick.
    /// Invalid or stale attitude removes the pointer and brick (AC 25-11B: never a frozen, plausible value);
    /// a missing slip measurement removes the brick. Bank beyond the scale end (60 deg, the arc end) keeps the pointer pegged but changes
    /// its appearance (amber, flashing for 5 s, then steady) so the clamp is never silent.
    /// </summary>
    [AddComponentMenu("HUD Control/Elements/Bank Scale")]
    public class BankScaleElement : Core.HUDElementBase
    {
        #region Inspector - UI References

        [Header("Bank Scale References")]
        [Tooltip("Bank scale arc Image")]
        [SerializeField] private RectTransform bankScale;

        [Tooltip("Bank scale inner part")]
        [SerializeField] private Transform bankScaleIP;

        [Tooltip("Roll pointer indicator")]
        [SerializeField] private RectTransform rollPointer;

        [Tooltip("Slip/Skid slider")]
        [SerializeField] private RectTransform slipSlider;

        #endregion

        #region Inspector - Animation Enables

        [Header("Animation Enables")]
        [Tooltip("Enable bank scale rotation")]
        [SerializeField] private bool enableBankRotation = true;

        [Tooltip("Enable roll pointer rotation (if not rotating scale)")]
        [SerializeField] private bool enablePointerRotation = false;

        [Tooltip("Enable slip/skid indicator")]
        [SerializeField] private bool enableSlip = true;

        [Tooltip("Rotate scale (true) or pointer (false)")]
        [SerializeField] private bool rotateScale = false;

        [Tooltip("Enable Bank Scale IP rotation on Z axis for roll indication")]
        [SerializeField] private bool enableBankScaleIPRotation = true;

        #endregion

        #region Inspector - Bounds

        [Header("Bank Bounds")]
        [Tooltip("Maximum bank angle in degrees (the scale end; the procedural scale forces its 60 deg arc end)")]
        [SerializeField] private float maxBankAngle = 60f;

        [Header("Slip Bounds")]
        [Tooltip("Legacy fallback only (used when the slip brick has no measurable width): pixels per unit of slip")]
        [SerializeField] private float slipPixelsPerUnit = 10f;

        [Tooltip("Legacy fallback only: maximum slip offset in pixels")]
        [SerializeField] private float maxSlipOffsetPixels = 15f;

        [Tooltip("Brick travel at |slip| = 1 (g_side, clamped to +/-1), in brick widths. Travel is limited to one brick width.")]
        [SerializeField] private float slipTravelInSliderWidths = 1f;

        [Tooltip("Simulate slip from rudder input (demo only; measured slip from the bridge disables this)")]
        [SerializeField] private bool simulateSlip = true;

        #endregion

        private float displayedRoll;
        private float rawRoll;
        private float displayedSlip;
        private float lastSlipTime = -1f;
        private FaaDigitalBankGraphic procedural;
        private float targetSlip;
        private bool slipValid;
        private bool attitudeValid = true;
        private bool overBank;
        private float overBankOnset = -100f;
        private Vector2 slipBasePos;
        private bool slipBaseCaptured;
        private Graphic[] pointerGraphics;
        private Graphic[] slipGraphics;
        private Color[] pointerNormalColors;
        private CanvasGroup[] intensityGroups;
        private bool pointerShown = true;
        private bool slipShown = true;
        private bool overBankStyled;

        public override string ElementId => "BankScale";
        /// <summary>True while the roll pointer is drawn (attitude valid).</summary>
        public bool PointerVisible => attitudeValid;
        /// <summary>True while the slip brick is drawn (valid attitude and a real slip source).</summary>
        public bool SlipVisible => attitudeValid && enableSlip && (simulateSlip || slipValid);
        /// <summary>True while bank exceeds the scale end and the pointer is pegged.</summary>
        public bool OverBank => overBank;
        public float MaxBankAngle => maxBankAngle;
        /// <summary>The procedural Digital scale, or null while the legacy bitmap scale is used (edit mode, tests).</summary>
        public FaaDigitalBankGraphic ProceduralScale => procedural;

        /// <summary>
        /// Switches to the procedural scale: creates it under this module root, hides the bitmap arc, pointer and brick, stops the legacy
        /// pivot rotation and sets the scale end to the arc end. Idempotent; called by the runtime sanitizer in play mode.
        /// </summary>
        public FaaDigitalBankGraphic EnsureProceduralScale()
        {
            if (procedural == null) procedural = FaaDigitalBankGraphic.Ensure(transform);
            if (procedural == null) return null;
            maxBankAngle = FaaDigitalBankGraphic.ScaleEndDegrees;
            enableBankRotation = false;
            enablePointerRotation = false;
            enableBankScaleIPRotation = false;
            HideBitmapScale();
            intensityGroups = null;
            PushProcedural();
            return procedural;
        }

        private void HideBitmapScale()
        {
            Transform[] parts = { bankScale, rollPointer, slipSlider };
            foreach (Transform part in parts)
            {
                if (part == null || part == transform || (procedural != null && procedural.transform.IsChildOf(part))) continue;
                foreach (Graphic g in part.GetComponentsInChildren<Graphic>(true))
                {
                    g.enabled = false;
                    g.raycastTarget = false;
                }
                if (part.gameObject.activeSelf) part.gameObject.SetActive(false);
            }
        }

        protected override void OnInitialize()
        {
            displayedRoll = 0f;
            displayedSlip = 0f;
            CaptureSlipBase();
            CacheGraphics();
            ApplyVisibility(true);
        }

        private void CaptureSlipBase()
        {
            if (slipSlider != null && !slipBaseCaptured)
            {
                slipBasePos = slipSlider.anchoredPosition;
                slipBaseCaptured = true;
            }
        }

        private void CacheGraphics()
        {
            pointerGraphics = rollPointer != null ? rollPointer.GetComponentsInChildren<Graphic>(true) : new Graphic[0];
            slipGraphics = slipSlider != null ? slipSlider.GetComponentsInChildren<Graphic>(true) : new Graphic[0];
            pointerNormalColors = new Color[pointerGraphics.Length];
            for (int i = 0; i < pointerGraphics.Length; i++)
                pointerNormalColors[i] = pointerGraphics[i] != null ? pointerGraphics[i].color : Color.white;
        }

        protected override void OnUpdateElement(AircraftState state)
        {
            float roll = Mathf.DeltaAngle(0f, state.Roll);
            bool exceeded = attitudeValid && Mathf.Abs(roll) > maxBankAngle;
            if (exceeded && !overBank) overBankOnset = Time.unscaledTime;
            overBank = exceeded;
            rawRoll = roll;
            // 1:1 with the conformal horizon: no easing on flight data (the bridge already interpolates between packets).
            displayedRoll = Mathf.Clamp(roll, -maxBankAngle, maxBankAngle);

            // Bank rotation
            if (rotateScale && enableBankRotation)
            {
                if (bankScale != null)
                    bankScale.localRotation = Quaternion.Euler(0, 0, displayedRoll);
            }
            else if (enablePointerRotation && rollPointer != null)
            {
                rollPointer.localRotation = Quaternion.Euler(0, 0, -displayedRoll);
            }

            // Bank Scale IP rotation (independent of scale rotation)
            if (enableBankScaleIPRotation && bankScaleIP != null)
            {
                bankScaleIP.localRotation = Quaternion.Euler(0, 0, displayedRoll);
            }

            // Slip: jitter removal only, the same filter as the Classic brick (FaaAnalogAnimation, tau about 70 ms).
            if (enableSlip)
            {
                float target = simulateSlip ? -state.RudderInput : targetSlip;
                float now = Time.unscaledTime;
                displayedSlip = lastSlipTime < 0f ? target : FaaAnalogAnimation.Damp(displayedSlip, target, now - lastSlipTime);
                if (now > lastSlipTime) lastSlipTime = now;
            }

            if (procedural != null)
            {
                PushProcedural();
                return;
            }

            // Slip indicator (same sign as the Classic bank graphic: brick x = +slip)
            if (enableSlip && slipSlider != null)
            {
                CaptureSlipBase();
                float width = Mathf.Abs(slipSlider.rect.width * slipSlider.localScale.x);
                float slipOffset = width > 1e-6f
                    ? SlipTravel(displayedSlip, width, slipTravelInSliderWidths)
                    : Mathf.Clamp(displayedSlip * slipPixelsPerUnit, -maxSlipOffsetPixels, maxSlipOffsetPixels);

                Vector2 newPos = slipBasePos;
                newPos.x += slipOffset;
                if ((slipSlider.anchoredPosition - newPos).sqrMagnitude > 1e-12f)
                    slipSlider.anchoredPosition = newPos;
            }
        }

        /// <summary>Brick offset in the brick's parent units: slip (clamped +/-1) times travel, limited to one brick width.</summary>
        public static float SlipTravel(float slip, float sliderWidth, float travelInWidths)
        {
            if (float.IsNaN(slip) || float.IsInfinity(slip)) return 0f;
            float width = Mathf.Abs(sliderWidth);
            return Mathf.Clamp(Mathf.Clamp(slip, -1f, 1f) * width * Mathf.Max(0f, travelInWidths), -width, width);
        }

        /// <summary>Measured slip drives the brick; the brick stays hidden until valid data arrives.</summary>
        public void ConfigureMeasuredSlip()
        {
            enableSlip = true;
            simulateSlip = false;
            ApplyVisibility(false);
        }

        /// <summary>Measured slip (g_side, +/-1). Invalid or non-finite data removes the brick instead of centring it.</summary>
        public void SetSlipData(float slip, bool valid)
        {
            simulateSlip = false;
            enableSlip = true;
            slipValid = valid && !float.IsNaN(slip) && !float.IsInfinity(slip);
            if (slipValid) targetSlip = Mathf.Clamp(slip, -1f, 1f);
            ApplyVisibility(false);
        }

        /// <summary>Attitude validity from the data source. Invalid or stale attitude removes the roll pointer and slip brick.</summary>
        public void SetAttitudeValid(bool valid)
        {
            attitudeValid = valid;
            if (!valid) overBank = false;
            ApplyVisibility(false);
        }

        public void SetSlipValue(float slip)
        {
            SetSlipData(slip, true);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;
            if (procedural != null)
            {
                PushProcedural();
                ApplyForwardIntensity();
                return;
            }
            ApplyVisibility(false);
            ApplyOverBankCue();
            ApplyForwardIntensity();
        }

        /// <summary>Feeds the procedural scale: raw roll (it pegs and flags beyond the arc end), validity, slip, pilot colour, blink phase.</summary>
        private void PushProcedural()
        {
            if (procedural == null) return;
            bool slipAvailable = enableSlip && (simulateSlip || slipValid);
            bool flashOn = !overBank || FaaHudStyle.BlinkVisible(overBankOnset);
            procedural.Present(rawRoll, attitudeValid, displayedSlip, slipAvailable, FaaHudPilotColor.Resolve(FaaHudStyle.Green), flashOn);
        }

        private void ApplyVisibility(bool force)
        {
            if (procedural != null)
            {
                PushProcedural();
                return;
            }
            if (pointerGraphics == null || slipGraphics == null) CacheGraphics();
            bool pointer = PointerVisible, slip = SlipVisible;
            if (force || pointer != pointerShown)
            {
                pointerShown = pointer;
                foreach (var g in pointerGraphics) if (g != null) g.enabled = pointer;
            }
            if (force || slip != slipShown)
            {
                slipShown = slip;
                foreach (var g in slipGraphics) if (g != null) g.enabled = slip;
            }
        }

        private void ApplyOverBankCue()
        {
            if (pointerGraphics == null) return;
            if (overBank)
            {
                float alpha = FaaHudStyle.BlinkVisible(overBankOnset) ? 1f : 0f;
                for (int i = 0; i < pointerGraphics.Length; i++)
                {
                    var g = pointerGraphics[i];
                    if (g == null) continue;
                    Color amber = FaaHudStyle.WithAlpha(FaaHudStyle.Amber, Mathf.Max(pointerNormalColors[i].a, FaaHudStyle.MinTextAlpha));
                    if (g.color != amber) g.color = amber;
                    if (!Mathf.Approximately(g.canvasRenderer.GetAlpha(), alpha)) g.canvasRenderer.SetAlpha(alpha);
                }
                overBankStyled = true;
                return;
            }
            for (int i = 0; i < pointerGraphics.Length; i++)
            {
                var g = pointerGraphics[i];
                if (g == null) continue;
                if (overBankStyled)
                {
                    // Return to the pilot-selected symbology colour, keeping the authored alpha.
                    Color normal = FaaHudPilotColor.Resolve(pointerNormalColors[i]);
                    g.color = FaaHudStyle.WithAlpha(normal, pointerNormalColors[i].a);
                    g.canvasRenderer.SetAlpha(1f);
                }
                pointerNormalColors[i] = g.color;
            }
            overBankStyled = false;
        }

        /// <summary>Dims the arc and pointer while a side panel is inspected (FaaHudInspection), except for the instrument previewed from
        /// Settings. The root CanvasGroup belongs to the style gate.</summary>
        private void ApplyForwardIntensity()
        {
            if (intensityGroups == null)
            {
                var targets = procedural != null ? new Transform[] { procedural.transform } : new Transform[] { bankScale, bankScaleIP };
                intensityGroups = new CanvasGroup[targets.Length];
                for (int i = 0; i < targets.Length; i++)
                {
                    Transform t = targets[i];
                    if (t == null || t == transform) continue;
                    var group = t.GetComponent<CanvasGroup>();
                    if (group == null) group = t.gameObject.AddComponent<CanvasGroup>();
                    group.interactable = false; group.blocksRaycasts = false;
                    intensityGroups[i] = group;
                }
            }
            float k = FAA.Customization.FaaSpatialWorkspace.ForwardIntensityFor(transform);
            foreach (var group in intensityGroups)
                if (group != null && Mathf.Abs(group.alpha - k) > .002f) group.alpha = k;
        }

        public float GetDisplayedRoll() => displayedRoll;
        public float GetDisplayedSlip() => displayedSlip;
    }
}
