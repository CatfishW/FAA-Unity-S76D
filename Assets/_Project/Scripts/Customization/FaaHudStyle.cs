using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace FAA.Customization
{
    public enum FaaHudSeverity { Normal=0, Advisory=1, Caution=2, Warning=3, Invalid=4 }

    /// <summary>
    /// Shared human-factors tokens for every HUD symbology and screen-chrome module, so all layers look and behave alike.
    /// Sizes are reference-canvas units (1920x1080 canvas, scaled x2 on a 3840x2160 game view).
    /// Basis: AC 25-11B colour table and HUD appendix; AC 25.1322-1 alert colours; HF-STD-001B and DOT/FAA/TC-13/44 character size
    /// (16 arcmin minimum, 20-24 arcmin preferred). 1 arcmin is about 0.65 reference units at a 700 mm eye distance on a 27-inch 4K monitor.
    /// </summary>
    public static class FaaHudStyle
    {
        // ---- Colour (one meaning per colour; red/amber are reserved for alerts and limits) ----
        /// <summary>Normal HUD symbology and current data (matches the existing HUD green).</summary>
        public static readonly Color Green=new Color(.2f,1f,.2f,1f);
        /// <summary>Caution, time-limited range, or a non-normal source.</summary>
        public static readonly Color Amber=new Color(1f,.76f,.1f,1f);
        /// <summary>Warning or limit exceedance.</summary>
        public static readonly Color Red=new Color(1f,.27f,.22f,1f);
        /// <summary>Armed modes, selected or target values, and advisories (multi-colour Set 2).</summary>
        public static readonly Color Cyan=new Color(.35f,.9f,1f,1f);
        /// <summary>Primary chrome text (controls and status) outside the flight symbology.</summary>
        public static readonly Color White=new Color(.92f,.96f,.98f,1f);
        /// <summary>Secondary chrome text.</summary>
        public static readonly Color ChromeQuiet=new Color(.70f,.80f,.86f,1f);
        public static readonly Color ChromePlate=new Color(.02f,.05f,.07f,.88f);
        public static readonly Color ChromeButton=new Color(.07f,.12f,.16f,.96f);
        public static readonly Color ChromeButtonActive=new Color(.10f,.33f,.37f,1f);
        public static readonly Color ChromeOutline=new Color(.55f,.76f,.84f,.40f);

        // ---- Typography floors (TMP font size in reference units) ----
        /// <summary>Absolute minimum for any pilot-read label (~16-17 arcmin cap height at 650-700 mm).</summary>
        public const float MinLabel=15f;
        /// <summary>Scale numerals, units, captions.</summary>
        public const float Secondary=18f;
        /// <summary>Secondary flight data values (TQ, NR, VS, heading tape numerals).</summary>
        public const float Data=24f;
        /// <summary>Primary readouts (IAS, ALT, HDG).</summary>
        public const float Primary=34f;
        /// <summary>Chrome buttons and status text.</summary>
        public const float Chrome=16f;
        /// <summary>Alpha floor for pilot-read text; contrast is never bought with transparency.</summary>
        public const float MinTextAlpha=.85f;
        /// <summary>Alpha floor for secondary/quiet labels (still at least 3:1 with the halo).</summary>
        public const float MinQuietAlpha=.70f;
        /// <summary>Smallest scale any text-bearing flight module may be shrunk to before its text drops below the floors.</summary>
        public const float MinModuleScale=.75f;

        // ---- Timing ----
        /// <summary>Mode-change emphasis box duration (AC 25.1329-1C, ~10 s).</summary>
        public const float ModeChangeBoxSeconds=10f;
        /// <summary>Flash rate for alerts (2-3 Hz band shared by AC 25-11B and HF-STD-001B).</summary>
        public const float BlinkHz=2f;
        /// <summary>Flashing stops after this many seconds, then the cue stays steady.</summary>
        public const float BlinkSeconds=5f;
        /// <summary>Fade duration for menus, panels and inspection dimming (no easing on flight data).</summary>
        public const float FadeSeconds=.2f;

        /// <summary>One global blink clock so every flashing element is synchronised.</summary>
        public static bool BlinkOn => Mathf.Repeat(Time.unscaledTime*BlinkHz,1f)<.6f;
        /// <summary>True while a cue whose condition started at <paramref name="onsetTime"/> should be drawn (flashes, then steady).</summary>
        public static bool BlinkVisible(float onsetTime) => Time.unscaledTime-onsetTime>=BlinkSeconds || BlinkOn;

        public static Color ForSeverity(FaaHudSeverity severity,Color normal)
        {
            switch(severity)
            {
                case FaaHudSeverity.Caution: return Amber;
                case FaaHudSeverity.Warning: return Red;
                case FaaHudSeverity.Advisory: return Cyan;
                default: return normal;
            }
        }
        public static Color WithAlpha(Color c,float a)=>new Color(c.r,c.g,c.b,a);
        /// <summary>Scales alpha only (Color*float would also darken RGB).</summary>
        public static Color Dim(Color c,float k)=>new Color(c.r,c.g,c.b,c.a*k);
        public static float Legible(float size,float floor=MinLabel)=>Mathf.Max(size,floor);

        // ---- Halo (TMP underlay) so text keeps >=3:1 local contrast over bright sky/terrain ----
        private static readonly Dictionary<(Material,int),Material> haloCache=new();
        /// <summary>
        /// Shared distance-field material with a soft dark underlay. Underlay is used instead of outline because
        /// SymbologyColorManager rewrites outlineColor with the pilot tint.
        /// </summary>
        public static Material HaloMaterial(TMP_FontAsset font,float strength=.75f)
        {
            if(font==null||font.material==null)return null;
            var key=(font.material,Mathf.RoundToInt(Mathf.Clamp01(strength)*100));
            if(haloCache.TryGetValue(key,out var cached)&&cached!=null)return cached;
            var m=new Material(font.material){name="FAA HUD Halo",hideFlags=HideFlags.HideAndDontSave};
            if(m.HasProperty("_UnderlayColor"))
            {
                m.EnableKeyword("UNDERLAY_ON");
                m.SetColor("_UnderlayColor",new Color(0,0,0,Mathf.Clamp01(strength)));
                if(m.HasProperty("_UnderlayDilate"))m.SetFloat("_UnderlayDilate",.35f);
                if(m.HasProperty("_UnderlaySoftness"))m.SetFloat("_UnderlaySoftness",.3f);
                if(m.HasProperty("_UnderlayOffsetX"))m.SetFloat("_UnderlayOffsetX",0);
                if(m.HasProperty("_UnderlayOffsetY"))m.SetFloat("_UnderlayOffsetY",0);
            }
            haloCache[key]=m;return m;
        }
        public static void ApplyHalo(TMP_Text text,float strength=.75f)
        {
            if(text==null||text.font==null)return;
            var m=HaloMaterial(text.font,strength);
            if(m!=null&&text.fontSharedMaterial!=m){text.fontSharedMaterial=m;text.UpdateMeshPadding();}
        }
        /// <summary>Applies size floor, alpha floor and halo in one call. Returns the text for chaining.</summary>
        public static TMP_Text StyleText(TMP_Text text,float size,float floor,Color color,float alphaFloor=MinTextAlpha,bool halo=true)
        {
            if(text==null)return null;
            text.fontSize=Mathf.Max(size,floor);
            if(text.enableAutoSizing)text.fontSizeMin=Mathf.Max(text.fontSizeMin,floor);
            text.color=WithAlpha(color,Mathf.Max(color.a,alphaFloor));
            if(halo)ApplyHalo(text);
            return text;
        }
    }
}
