using UnityEngine;

namespace FAA.Customization
{
    /// <summary>
    /// Presentation tokens for the Digital left/right columns (IAS, ALT, VS, TQ, NR/N2), built on <see cref="FaaHudStyle"/>.
    /// Sizes are module-local units: 1 unit = 1 reference px at module scale 1. Text-bearing flight modules never render below
    /// <see cref="FaaHudStyle.MinModuleScale"/> (see <see cref="FaaNonConformalScaleTarget"/>), so every size here meets its token
    /// (Secondary/Data/Primary) at that floor, which keeps every pilot-read string well above <see cref="FaaHudStyle.MinLabel"/>.
    /// Each column instrument sits on one translucent dark <see cref="Card"/> (captions, scales and ids included) and every number on a
    /// darker <see cref="Plate"/>, with a full-strength halo, so contrast holds over haze, sky and terrain alike.
    /// </summary>
    public static class FaaDigitalColumnStyle
    {
        /// <summary>Titles, units, engine ids and scale numerals: Secondary 18 at the 0.75 module floor (24 x .75 = 18 ref).</summary>
        public const float LabelSize = 24f;
        /// <summary>TQ and N2 digits: the Data token (24 ref) at the module floor.</summary>
        public const float ValueSize = 32f;
        /// <summary>Rotor NR, the primary rotorcraft parameter, is the largest engine number.</summary>
        public const float HeadlineSize = 36f;
        /// <summary>Vertical speed digits.</summary>
        public const float VsValueSize = 30f;
        /// <summary>IAS/ALT digits; the authored readout adds about x1.08, so this is the Primary token (34 ref) at the module floor.</summary>
        public const float ReadoutSize = 42f;

        /// <summary>Captions and ids are opaque: hierarchy comes from size and position, never from transparency.</summary>
        public const float TitleAlpha = 1f, LabelAlpha = 1f, LineAlpha = .85f, FillAlpha = .6f;
        /// <summary>Underlay strength for every column string (strongest halo: contrast over bright haze).</summary>
        public const float HaloStrength = 1f;
        /// <summary>Dark backing plate behind every number. Overlay canvases draw after the world-space horizon/ladder,
        /// so the plate occludes the lower-priority conformal line (AC 25-11B: higher priority occludes lower).</summary>
        public static readonly Color Plate = new Color(.015f, .055f, .065f, .62f);
        /// <summary>Lighter card behind a whole column instrument (captions, scale numerals, rails and ids).</summary>
        public static readonly Color Card = new Color(.015f, .055f, .065f, .36f);
        /// <summary>Dark halo under limit lines and pointers.</summary>
        public static readonly Color Halo = new Color(0f, 0f, 0f, .6f);
        /// <summary>Legacy IAS/ALT floor while inspecting (kept for Classic callers); the columns use <see cref="AwarenessIntensity"/>.</summary>
        public const float ReadoutAwarenessFloor = .55f;
        /// <summary>The one invalid-data glyph used across the columns (the bar or pointer is removed at the same time).</summary>
        public const string Invalid = "---";

        // One caption pattern ("<parameter> <unit>") shared by Digital and Classic (m1: identical labels in both styles).
        public const string AirspeedCaption = "IAS KT", AltitudeCaption = "ALT FT", VerticalSpeedCaption = "VS FPM",
            TorqueCaption = "TQ %", RotorCaption = "NR %";

        private static SymbologyColorManager manager;
        private static float nextSearch = float.NegativeInfinity;

        /// <summary>
        /// Normal-state hue: the pilot's symbology colour (SymbologyColorManager tints the rest of the Digital HUD), otherwise HUD green.
        /// Caution and warning always override it with amber and red.
        /// </summary>
        public static Color Normal
        {
            get
            {
                if (manager == null && Time.realtimeSinceStartup >= nextSearch)
                {
                    nextSearch = Time.realtimeSinceStartup + 1f;
                    manager = Object.FindAnyObjectByType<SymbologyColorManager>();
                }
                if (manager == null || !manager.isActiveAndEnabled) return FaaHudStyle.Green;
                Color c = manager.CurrentColor;
                return c.a <= .01f ? FaaHudStyle.Green : new Color(c.r, c.g, c.b, 1f);
            }
        }

        /// <summary>Forward-HUD multiplier for column detail (captions, scales, rails, cards); dims while a side panel is inspected.</summary>
        public static float ColumnIntensity => FaaHudInspection.ForwardHudIntensity;
        /// <summary>Awareness readouts (IAS/ALT/TQ/NR/VS digits and their plates): never below <see cref="FaaHudInspection.AwarenessHudIntensity"/>.</summary>
        public static float ReadoutIntensity => Mathf.Max(FaaHudInspection.ForwardHudIntensity, FaaHudInspection.AwarenessHudIntensity);

        /// <summary>Detail intensity for one instrument: 1 while it is the instrument previewed from Settings.</summary>
        public static float DetailIntensity(Transform element) => FaaSpatialWorkspace.ForwardIntensityFor(element);
        /// <summary>Awareness-readout intensity for one instrument: max(<see cref="DetailIntensity"/>, AwarenessHudIntensity).</summary>
        public static float AwarenessIntensity(float detailIntensity) => Mathf.Max(detailIntensity, FaaHudInspection.AwarenessHudIntensity);

        /// <summary>Value colour for an exceedance class: dashes stay legible (MinTextAlpha), alerts are opaque amber/red.</summary>
        public static Color ValueColor(FaaExceedance state, Color normal) => state == FaaExceedance.Invalid
            ? FaaHudStyle.WithAlpha(normal, FaaHudStyle.MinTextAlpha) : FaaHudStyle.WithAlpha(FaaRotorcraftLimits.ColorFor(state, normal), 1f);

        /// <summary>
        /// Sets a renderer's colour multiplier only when it differs. Also undoes the RGB/alpha that SymbologyColorManager
        /// writes into CanvasRenderer colours, which would otherwise multiply amber/red into the pilot hue.
        /// </summary>
        public static void SetRendererAlpha(CanvasRenderer renderer, float alpha)
        {
            if (renderer == null) return;
            var target = new Color(1f, 1f, 1f, Mathf.Clamp01(alpha));
            if (renderer.GetColor() != target) renderer.SetColor(target);
        }
    }
}
