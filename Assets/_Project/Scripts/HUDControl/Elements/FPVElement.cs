using UnityEngine;
using AircraftControl.Core;

namespace HUDControl.Elements
{
    /// <summary>
    /// FPV (Flight Path Vector) element for Image-based HUD.
    /// Animates FPV marker position based on flight path angle.
    /// All animations have strict bounds.
    /// </summary>
    [AddComponentMenu("HUD Control/Elements/FPV")]
    public class FPVElement : Core.HUDElementBase
    {
        #region Inspector - UI References
        
        [Header("FPV References")]
        [Tooltip("FPV marker Image")]
        [SerializeField] private RectTransform fpvMarker;
        
        #endregion
        
        #region Inspector - Animation Enables
        
        [Header("Animation Enables")]
        [Tooltip("Enable vertical FPV movement")]
        [SerializeField] private bool enableVertical = true;
        
        [Tooltip("Enable horizontal FPV movement (drift)")]
        [SerializeField] private bool enableHorizontal = false;
        
        #endregion
        
        #region Inspector - Bounds
        
        [Header("FPV Bounds")]
        [Tooltip("Pixels per degree of FPA")]
        [SerializeField] private float pixelsPerDegree = 2f;
        
        [Tooltip("Maximum vertical offset in pixels (KEEP SMALL)")]
        [SerializeField] private float maxVerticalOffsetPixels = 15f;
        
        [Tooltip("Maximum horizontal offset in pixels (KEEP SMALL)")]
        [SerializeField] private float maxHorizontalOffsetPixels = 15f;
        
        #endregion
        
        private float displayedVertical;
        private float displayedHorizontal;
        private Vector2 fpvBasePos;
        private Vector3 fpvBaseScale = Vector3.one;
        
        public override string ElementId => "FPV";
        
        protected override void CacheReferences()
        {
            base.CacheReferences();
            
            if (fpvMarker == null)
                fpvMarker = rectTransform;
        }
        
        protected override void OnInitialize()
        {
            displayedVertical = 0f;
            displayedHorizontal = 0f;
            
            if (fpvMarker != null)
            {
                fpvBasePos = fpvMarker.anchoredPosition;
                if (fpvMarker.localScale != Vector3.zero) fpvBaseScale = fpvMarker.localScale;
            }
        }
        
        protected override void OnUpdateElement(AircraftState state)
        {
            if (fpvMarker == null) return;

            // Never show a plausible-looking FPV without a computable flight path (no frozen zero at low speed).
            // Only the scale is changed: the conformal layer may have retired this graphic via Graphic.enabled.
            bool available = TryCalculateFPA(state, out float fpa);
            Vector3 visibleScale = available ? fpvBaseScale : Vector3.zero;
            if (fpvMarker.localScale != visibleScale) fpvMarker.localScale = visibleScale;
            if (!available) return;
            
            Vector2 newPos = fpvBasePos;
            
            // Vertical FPV (flight path angle)
            if (enableVertical)
            {
                displayedVertical = Core.HUDAnimator.SmoothValue(displayedVertical, fpa, smoothing);
                
                float vOffset = -displayedVertical * pixelsPerDegree;
                vOffset = Mathf.Clamp(vOffset, -maxVerticalOffsetPixels, maxVerticalOffsetPixels);
                newPos.y += vOffset;
            }
            
            // Horizontal FPV (drift): AircraftState carries no measured ground track, so no lateral offset is invented
            // (rudder input is not drift). The conformal layer draws the earth-referenced FPV from the real track.
            if (enableHorizontal) displayedHorizontal = 0f;
            
            fpvMarker.anchoredPosition = newPos;
        }
        
        private static bool TryCalculateFPA(AircraftState state, out float fpa)
        {
            fpa = 0f;
            if (state == null || float.IsNaN(state.GroundSpeedKnots) || float.IsInfinity(state.GroundSpeedKnots) ||
                float.IsNaN(state.VerticalSpeedFpm) || float.IsInfinity(state.VerticalSpeedFpm) || state.GroundSpeedKnots < 10f) return false;
            float vsKnots = state.VerticalSpeedFpm / 101.269f;
            fpa = Mathf.Clamp(Mathf.Atan2(vsKnots, state.GroundSpeedKnots) * Mathf.Rad2Deg, -20f, 20f);
            return true;
        }
        
        public float GetDisplayedVertical() => displayedVertical;
        public float GetDisplayedHorizontal() => displayedHorizontal;
    }
}
