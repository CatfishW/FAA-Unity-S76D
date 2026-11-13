using UnityEngine;

namespace WeatherRadar
{
    /// <summary>
    /// The single weather-radar return colour scale (ARINC 708A / RTCA DO-220 convention), shared by every
    /// weather source and by the bridge's procedural depiction: black for no return, then discrete green,
    /// yellow, red and magenta for increasing precipitation. No intermediate hues (lime, orange) and no
    /// alpha ramps, so a level always reads as the same colour.
    /// Level 4 (magenta) means extreme precipitation only; it must never be derived from scalar turbulence.
    /// </summary>
    public static class WeatherRadarPalette
    {
        public static readonly Color Level1 = new Color(0.00f, 0.78f, 0.20f, 1f);   // light
        public static readonly Color Level2 = new Color(1.00f, 0.85f, 0.00f, 1f);   // moderate
        public static readonly Color Level3 = new Color(1.00f, 0.15f, 0.10f, 1f);   // heavy
        public static readonly Color Level4 = new Color(1.00f, 0.20f, 1.00f, 1f);   // extreme (magenta)

        public static readonly Color32 Level1Color32 = Level1;
        public static readonly Color32 Level2Color32 = Level2;
        public static readonly Color32 Level3Color32 = Level3;
        public static readonly Color32 Level4Color32 = Level4;
        public static readonly Color32 None = new Color32(0, 0, 0, 0);

        /// <summary>Opaque near-black 'no return' face (not translucent glass).</summary>
        public static readonly Color32 NoReturn = new Color32(4, 10, 14, 240);

        public const int MaxLevel = 4;

        public static Color32 ForLevel(int level)
        {
            switch (level)
            {
                case 1: return Level1Color32;
                case 2: return Level2Color32;
                case 3: return Level3Color32;
                case 4: return Level4Color32;
                default: return None;
            }
        }

        /// <summary>Discrete level for a normalised 0..1 return intensity; invalid input is no return.</summary>
        public static int LevelFromIntensity(float intensity)
        {
            if (float.IsNaN(intensity) || float.IsInfinity(intensity) || intensity < .05f) return 0;
            return intensity < .35f ? 1 : intensity < .6f ? 2 : intensity < .85f ? 3 : 4;
        }

        public static string LevelName(int level) => level switch
        {
            1 => "LIGHT", 2 => "MOD", 3 => "HEAVY", 4 => "EXTREME", _ => "NONE"
        };
    }
}
