using System;
using UnityEngine;

namespace FAA.Customization
{
    /// <summary>
    /// Shared state for side-panel inspection (chrome HAND STUDIO / WEATHER / TRAFFIC / SETTINGS, F9, or a view turned off-axis).
    /// While the camera inspects a side panel, the forward head-fixed HUD is drawn over a sideways view, so its forward-referenced
    /// attitude and heading would mislead and clutter the panel. Policy (one writer: FaaSpatialWorkspace):
    /// - essential awareness readouts (IAS, ALT, TQ/NR digits, VS readout, FMA, heading readout) use
    ///   max(FaaSpatialWorkspace.ForwardIntensityFor(t), <see cref="AwarenessHudIntensity"/>), see FaaSpatialWorkspace.AwarenessIntensityFor;
    /// - every other forward layer uses FaaSpatialWorkspace.ForwardIntensityFor(t) (<see cref="ForwardHudIntensity"/>, or 1 for the
    ///   instrument previewed from Settings);
    /// - caution/warning alerts are never dimmed (the workspace ends the inspection dimming while an exceedance is active).
    /// No ticking is required: intensity is evaluated from time.
    /// </summary>
    public static class FaaHudInspection
    {
        /// <summary>Forward HUD intensity while a side panel is inspected: dimmed, never fully removed.</summary>
        public const float InspectionIntensity=.16f;

        private static bool active;
        private static string target="";
        private static float fromIntensity=1,toIntensity=1,transitionStart=-10;

        public static bool Active=>active;
        /// <summary>Id of the inspected panel ("settings", "camera-controls", "weather", "traffic", ...) or empty.</summary>
        public static string TargetId=>target;
        public static event Action Changed;

        /// <summary>Intensity for essential awareness readouts (IAS, ALT, TQ/NR, VS, FMA, heading readout) while a side panel is inspected.
        /// They stay legible because continuously monitored flight data is never hidden (AC 25-11B).</summary>
        public const float AwarenessIntensity=.65f;

        /// <summary>Smoothed multiplier for awareness readouts: 1 when looking forward, <see cref="AwarenessIntensity"/> while inspecting.</summary>
        public static float AwarenessHudIntensity=>Mathf.Lerp(AwarenessIntensity,1f,Mathf.InverseLerp(InspectionIntensity,1f,ForwardHudIntensity));

        /// <summary>Awareness policy for a layer whose own forward intensity is <paramref name="forwardIntensity"/>: never below <see cref="AwarenessHudIntensity"/>.</summary>
        public static float AwarenessFor(float forwardIntensity)=>Mathf.Max(Mathf.Clamp01(forwardIntensity),AwarenessHudIntensity);

        /// <summary>Smoothed 0..1 multiplier for forward HUD layers (1 when looking forward).</summary>
        public static float ForwardHudIntensity
        {
            get
            {
                float t=Mathf.Clamp01((Time.unscaledTime-transitionStart)/Mathf.Max(.01f,FaaHudStyle.FadeSeconds));
                return Mathf.Lerp(fromIntensity,toIntensity,t);
            }
        }

        public static void Begin(string panelId)
        {
            panelId??="";
            if(active&&target==panelId)return;
            Retarget(InspectionIntensity);active=true;target=panelId;Changed?.Invoke();
        }

        public static void End()
        {
            if(!active)return;
            Retarget(1);active=false;target="";Changed?.Invoke();
        }

        /// <summary>Test/reset hook: returns to forward view immediately.</summary>
        public static void ResetImmediate(){active=false;target="";fromIntensity=toIntensity=1;transitionStart=-10;Changed?.Invoke();}

        private static void Retarget(float to){fromIntensity=ForwardHudIntensity;toIntensity=to;transitionStart=Time.unscaledTime;}
    }
}
