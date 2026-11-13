using UnityEngine;
using TMPro;
using AircraftControl.Core;
using FAA.Customization;

namespace HUDControl.Elements
{
    /// <summary>
    /// Airspeed Indicator element for Image-based HUD.
    /// Animates speed tape vertical position with strict bounds.
    /// </summary>
    [AddComponentMenu("HUD Control/Elements/Airspeed Indicator")]
    public class AirspeedIndicatorElement : Core.HUDElementBase
    {
        #region Inspector - UI References
        
        [Header("Airspeed References")]
        [Tooltip("Speed tape that scrolls vertically")]
        [SerializeField] private RectTransform speedTape;
        
        [Tooltip("Airspeed readout text")]
        [SerializeField] private TMP_Text airspeedReadout;
        
        [Tooltip("Airspeed window panel (non-animating)")]
        [SerializeField] private RectTransform windowPanel;
        
        #endregion
        
        #region Inspector - Animation Enables
        
        [Header("Animation Enables")]
        [Tooltip("Enable speed tape movement")]
        [SerializeField] private bool enableTape = true;
        
        [Tooltip("Enable airspeed readout")]
        [SerializeField] private bool enableReadout = true;
        
        #endregion
        
        #region Inspector - Bounds
        
        [Header("Airspeed Bounds")]
        [Tooltip("Pixels per knot of airspeed")]
        [SerializeField] private float pixelsPerKnot = 1f;
        
        [Tooltip("Maximum tape offset in pixels (KEEP SMALL)")]
        [SerializeField] private float maxTapeOffsetPixels = 30f;
        
        [Tooltip("Reference airspeed (tape centered at this speed)")]
        [SerializeField] private float referenceAirspeed = 100f;
        
        [Tooltip("Display format")]
        [SerializeField] private string displayFormat = "{0:0}";
        
        #endregion
        
        private float displayedAirspeed;
        private float targetAirspeed;
        private float lastDisplayedAirspeed = -1f;
        private Vector2 tapeBasePos;
        private bool hasExternalAirspeed;
        private bool externalDataUnavailable;
        private bool vneExceeded;
        private string resolvedSource, resolvedFormat;

        /// <summary>Knots below the DEMONSTRATOR Vne at which a held overspeed indication clears (no flicker at the limit).</summary>
        public const float VneHysteresisKnots = 2f;
        
        public override string ElementId => "Airspeed";
        
        protected override void OnInitialize()
        {
            displayedAirspeed = 0f;
            targetAirspeed = 0f;
            hasExternalAirspeed = false;
            externalDataUnavailable = false;
            vneExceeded = false;
            lastDisplayedAirspeed = -1f;
            
            if (speedTape != null)
                tapeBasePos = speedTape.anchoredPosition;

            SetTapeAvailable(true);
        }
        
        protected override void OnUpdateElement(AircraftState state)
        {
            if (state == null || externalDataUnavailable)
            {
                return;
            }

            float target = hasExternalAirspeed
                ? targetAirspeed
                : Mathf.Max(0f, state.IndicatedAirspeedKnots);
            float effectiveSmoothing = smoothing > 0f
                ? smoothing
                : Core.HUDAnimator.CalculateSmoothing(animationSpeed);
            displayedAirspeed = Core.HUDAnimator.SmoothValue(displayedAirspeed, target, effectiveSmoothing);
            
            // Speed tape movement
            if (enableTape && speedTape != null)
            {
                // Calculate offset relative to reference
                float deltaSpeed = displayedAirspeed - referenceAirspeed;
                float offset = deltaSpeed * pixelsPerKnot;
                offset = Mathf.Clamp(offset, -maxTapeOffsetPixels, maxTapeOffsetPixels);
                
                Vector2 newPos = tapeBasePos;
                newPos.y += offset;
                speedTape.anchoredPosition = newPos;
            }
            
            // Airspeed readout
            if (enableReadout && airspeedReadout != null)
            {
                int rounded = Mathf.RoundToInt(displayedAirspeed);
                
                if (rounded != Mathf.RoundToInt(lastDisplayedAirspeed))
                {
                    airspeedReadout.text = string.Format(EffectiveFormat, rounded);
                    lastDisplayedAirspeed = rounded;
                }
                ApplyReadoutColor();
            }
        }

        /// <summary>
        /// Runtime readout format (editor setup scripts author the field). Airspeed carries no leading zero; leading zeros
        /// are reserved for directional values, so zero padding is removed here as well.
        /// </summary>
        public void SetDisplayFormat(string format)
        {
            if (string.IsNullOrEmpty(format)) return;
            format = WithoutLeadingZeros(format);
            if (format == displayFormat) return;
            try { string.Format(format, 0); }
            catch (System.FormatException) { return; }
            displayFormat = format;
            lastDisplayedAirspeed = -1f;
            if (HasExternalData) UpdateReadout();
        }

        public string DisplayFormat => displayFormat;

        /// <summary>
        /// The format actually used. A serialized zero-padded format (for example '{0:000}' written by an older setup script) still
        /// renders 17 kt as "17", never "017": a zero-padded speed reads like a heading.
        /// </summary>
        public string EffectiveFormat
        {
            get
            {
                if (!ReferenceEquals(resolvedSource, displayFormat)) { resolvedSource = displayFormat; resolvedFormat = WithoutLeadingZeros(displayFormat); }
                return resolvedFormat;
            }
        }

        /// <summary>Replaces zero-padded integer specifiers ('{0:000}', '{0:00}', '{0:D3}') with '{0:0}'; rich-text wrappers are kept.</summary>
        public static string WithoutLeadingZeros(string format)
        {
            if (string.IsNullOrEmpty(format)) return "{0:0}";
            string result = System.Text.RegularExpressions.Regex.Replace(format, @"\{0:(?:0{2,}|[Dd][0-9]+)\}", "{0:0}");
            try { string.Format(result, 0); return result; }
            catch (System.FormatException) { return "{0:0}"; }
        }

        /// <summary>The TMP text this element writes (used to bind presentation to the element that drives the visible digits).</summary>
        public TMP_Text Readout => airspeedReadout;

        /// <summary>
        /// Above the DEMONSTRATOR Vne (<see cref="FaaRotorcraftLimits.VneKnots"/>, not RFM data), held until the airspeed is
        /// <see cref="VneHysteresisKnots"/> below it. Drives the red digits here and the red box in FaaPrimaryFlightReadout.
        /// </summary>
        public bool VneExceeded => HasExternalData && vneExceeded;

        public static bool ClassifyVne(float knots, bool wasExceeded)
        {
            if (float.IsNaN(knots) || float.IsInfinity(knots)) return false;
            knots = Mathf.Max(0f, knots);
            return FaaRotorcraftLimits.Airspeed(wasExceeded ? knots + VneHysteresisKnots : knots, true) == FaaExceedance.Warning;
        }
        
        /// <summary>
        /// Feed an authoritative X-Plane value. The first valid packet establishes
        /// the display without a jump; subsequent packets remain animation targets.
        /// </summary>
        public void SetAirspeedData(float value, bool valid)
        {
            if (!valid || float.IsNaN(value) || float.IsInfinity(value))
            {
                ClearExternalData();
                return;
            }

            targetAirspeed = Mathf.Max(0f, value);
            if (!hasExternalAirspeed)
            {
                displayedAirspeed = targetAirspeed;
            }

            hasExternalAirspeed = true;
            externalDataUnavailable = false;
            SetTapeAvailable(true);
            UpdateReadout();
        }

        /// <summary>
        /// Bind only objects authored in the scene or prefab. No UI is created here.
        /// </summary>
        public void ConfigureVisuals(RectTransform tape, TMP_Text readout, RectTransform window)
        {
            speedTape = tape;
            airspeedReadout = readout;
            windowPanel = window;
            tapeBasePos = speedTape != null ? speedTape.anchoredPosition : Vector2.zero;
            SetTapeAvailable(true);
            UpdateReadout();
        }

        public void ClearExternalData()
        {
            hasExternalAirspeed = false;
            externalDataUnavailable = true;
            vneExceeded = false;
            targetAirspeed = 0f;
            displayedAirspeed = 0f;
            SetTapeAvailable(false);
            SetReadoutUnavailable();
        }

        public bool HasExternalData => hasExternalAirspeed && !externalDataUnavailable;
        public float GetTargetAirspeed() => targetAirspeed;
        public float GetDisplayedAirspeed() => displayedAirspeed;

        private void UpdateReadout()
        {
            if (!enableReadout || airspeedReadout == null)
            {
                return;
            }

            int rounded = Mathf.RoundToInt(displayedAirspeed);
            if (rounded != Mathf.RoundToInt(lastDisplayedAirspeed) || airspeedReadout.text == FaaDigitalColumnStyle.Invalid)
            {
                airspeedReadout.text = string.Format(EffectiveFormat, rounded);
                lastDisplayedAirspeed = rounded;
            }

            ApplyReadoutColor();
        }

        /// <summary>
        /// Pilot symbology colour in the normal range; red above the DEMONSTRATOR Vne in FaaRotorcraftLimits (not RFM data).
        /// The readout frame adds the red box (flashing, then steady).
        /// </summary>
        private void ApplyReadoutColor()
        {
            if (!HasExternalData) return;
            vneExceeded = ClassifyVne(displayedAirspeed, vneExceeded);
            if (airspeedReadout == null) return;
            Color color = vneExceeded ? FaaHudStyle.Red : FaaHudStyle.WithAlpha(FaaDigitalColumnStyle.Normal, 1f);
            if (airspeedReadout.color != color) airspeedReadout.color = color;
        }

        private void SetReadoutUnavailable()
        {
            if (airspeedReadout == null)
            {
                return;
            }

            // Dashes, never a frozen zero; still legible (MinTextAlpha) so the loss of data is noticed.
            airspeedReadout.text = FaaDigitalColumnStyle.Invalid;
            airspeedReadout.color = FaaHudStyle.WithAlpha(FaaDigitalColumnStyle.Normal, FaaHudStyle.MinTextAlpha);
        }

        private static void SetTapeAvailable(RectTransform tape, bool available)
        {
            if (tape != null && tape.gameObject.activeSelf != available)
            {
                tape.gameObject.SetActive(available);
            }
        }

        private void SetTapeAvailable(bool available)
        {
            SetTapeAvailable(speedTape, available);
        }
    }
}
