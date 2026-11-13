using UnityEngine;

namespace TrafficRadar
{
    /// <summary>
    /// FAA TCAS-compliant threat level classification for aircraft traffic.
    /// Based on Traffic Collision Avoidance System (TCAS II) standards.
    /// </summary>
    public enum ThreatLevel
    {
        /// <summary>Non-threatening traffic in the area (cyan unfilled diamond)</summary>
        OtherTraffic,
        
        /// <summary>Traffic within observation radius but not a threat (cyan filled diamond)</summary>
        Proximate,
        
        /// <summary>Potential conflict, ~35-48 seconds to impact (amber filled circle)</summary>
        TrafficAdvisory,
        
        /// <summary>Collision risk, ~15-35 seconds to impact (red filled square)</summary>
        ResolutionAdvisory
    }

    /// <summary>
    /// FAA-standard colors and symbology for TCAS threat levels.
    /// </summary>
    public static class ThreatLevelConfig
    {
        // FAA TCAS Standard Colors
        public static readonly Color OtherTrafficColor = new Color(0f, 1f, 1f, 0.8f);      // Cyan
        public static readonly Color ProximateColor = new Color(0f, 1f, 1f, 1f);           // Cyan (filled)
        public static readonly Color TrafficAdvisoryColor = new Color(1f, 0.75f, 0f, 1f);  // Amber
        public static readonly Color ResolutionAdvisoryColor = new Color(1f, 0f, 0f, 1f);  // Red

        // Own aircraft: white (AC 20-172B / DO-317), distinct from cyan traffic. Red/amber are advisory-only.
        public static readonly Color OwnAircraftColor = new Color(0.85f, 1f, 1f, 1f);

        /// <summary>
        /// Gets the color for a given threat level.
        /// </summary>
        public static Color GetColor(ThreatLevel level)
        {
            switch (level)
            {
                case ThreatLevel.ResolutionAdvisory:
                    return ResolutionAdvisoryColor;
                case ThreatLevel.TrafficAdvisory:
                    return TrafficAdvisoryColor;
                case ThreatLevel.Proximate:
                    return ProximateColor;
                case ThreatLevel.OtherTraffic:
                default:
                    return OtherTrafficColor;
            }
        }

        /// <summary>
        /// Gets the symbol type for a given threat level.
        /// </summary>
        public static SymbolType GetSymbolType(ThreatLevel level)
        {
            switch (level)
            {
                case ThreatLevel.ResolutionAdvisory:
                    return SymbolType.FilledSquare;
                case ThreatLevel.TrafficAdvisory:
                    return SymbolType.FilledCircle;
                case ThreatLevel.Proximate:
                    return SymbolType.FilledDiamond;
                case ThreatLevel.OtherTraffic:
                default:
                    return SymbolType.UnfilledDiamond;
            }
        }
    }

    /// <summary>
    /// Symbol types for aircraft display on traffic radar.
    /// </summary>
    public enum SymbolType
    {
        UnfilledDiamond,  // Other traffic
        FilledDiamond,    // Proximate
        FilledCircle,     // Traffic Advisory
        FilledSquare      // Resolution Advisory
    }

    /// <summary>TCAS II display altitude bands (DO-185B): NORMAL ±2,700 ft, ABOVE/BELOW extend one side to 9,900 ft.</summary>
    public enum TrafficAltitudeBand { Normal, Above, Below, All }

    public static class TrafficAltitudeBands
    {
        public const float NormalLimitFt = 2700f, ExtendedLimitFt = 9900f;

        /// <summary>Unknown (non-finite) relative altitude is never filtered out: a non-altitude-reporting
        /// intruder may be co-altitude, so it stays on the display without a data tag.</summary>
        public static bool WithinBand(float relativeAltitudeFt, TrafficAltitudeBand band)
        {
            if (float.IsNaN(relativeAltitudeFt) || float.IsInfinity(relativeAltitudeFt)) return true;
            switch (band)
            {
                case TrafficAltitudeBand.Normal: return relativeAltitudeFt >= -NormalLimitFt && relativeAltitudeFt <= NormalLimitFt;
                case TrafficAltitudeBand.Above: return relativeAltitudeFt >= -NormalLimitFt && relativeAltitudeFt <= ExtendedLimitFt;
                case TrafficAltitudeBand.Below: return relativeAltitudeFt >= -ExtendedLimitFt && relativeAltitudeFt <= NormalLimitFt;
                default: return true;
            }
        }

        public static TrafficAltitudeBand Next(TrafficAltitudeBand band) => (TrafficAltitudeBand)(((int)band + 1) % 4);

        public static string ShortLabel(TrafficAltitudeBand band) => band switch
        {
            TrafficAltitudeBand.Normal => "NORM", TrafficAltitudeBand.Above => "ABV",
            TrafficAltitudeBand.Below => "BLW", _ => "ALL ALT"
        };

        /// <summary>
        /// Scope footer annunciation of the active band. It is always shown (not only when filtering is unusual), so a band-filtered
        /// scope is never mistaken for an empty sky. ASCII only, so the static font atlas always has every glyph.
        /// </summary>
        public static string FooterLabel(TrafficAltitudeBand band) => band switch
        {
            TrafficAltitudeBand.Normal => "NORM ±2700 FT", TrafficAltitudeBand.Above => "ABOVE +9900 FT",
            TrafficAltitudeBand.Below => "BELOW -9900 FT", _ => "ALL ALT"
        };

        public static string ReadableLabel(TrafficAltitudeBand band) => band switch
        {
            TrafficAltitudeBand.Normal => "Normal ±2700", TrafficAltitudeBand.Above => "Above +9900",
            TrafficAltitudeBand.Below => "Below −9900", _ => "All altitudes"
        };
    }

    /// <summary>
    /// Configurable thresholds for threat level determination.
    /// Without a genuine TCAS advisory source the display may only classify proximate and other
    /// traffic (AC 20-172B). Amber TA / red RA symbols are never computed from distance alone.
    /// </summary>
    [System.Serializable]
    public class ThreatThresholds
    {
        [Header("Advisories (TCAS source only)")]
        [Tooltip("DEMONSTRATOR ONLY. Computes amber TA symbols from range/altitude. Leave off: no TCAS logic backs them.")]
        public bool allowComputedAdvisories = false;
        [Tooltip("DEMONSTRATOR ONLY. Computes red RA symbols. Requires allowComputedAdvisories; there is no RA manoeuvre guidance.")]
        public bool allowComputedResolutionAdvisory = false;

        [Header("Resolution Advisory (Red)")]
        [Tooltip("Maximum distance in nautical miles for RA")]
        public float raDistanceNM = 1.0f;
        [Tooltip("Maximum altitude difference in feet for RA")]
        public float raAltitudeFt = 300f;

        [Header("Traffic Advisory (Amber)")]
        [Tooltip("Maximum distance in nautical miles for TA")]
        public float taDistanceNM = 3.0f;
        [Tooltip("Maximum altitude difference in feet for TA")]
        public float taAltitudeFt = 500f;

        [Header("Proximate (Cyan Filled)")]
        [Tooltip("Maximum distance in nautical miles for Proximate")]
        public float proximateDistanceNM = 6.0f;
        [Tooltip("Maximum altitude difference in feet for Proximate")]
        public float proximateAltitudeFt = 1200f;

        /// <summary>
        /// Determines the threat level based on distance and absolute altitude difference.
        /// Unknown altitude is treated as co-altitude for the proximate test, never for an advisory.
        /// </summary>
        public ThreatLevel DetermineThreatLevel(float distanceNM, float altitudeDiffFt)
        {
            if (float.IsNaN(distanceNM) || float.IsInfinity(distanceNM)) return ThreatLevel.OtherTraffic;
            bool altitudeKnown = !float.IsNaN(altitudeDiffFt) && !float.IsInfinity(altitudeDiffFt);
            float separation = altitudeKnown ? Mathf.Abs(altitudeDiffFt) : 0f;
            if (allowComputedAdvisories && altitudeKnown)
            {
                if (allowComputedResolutionAdvisory && distanceNM <= raDistanceNM && separation <= raAltitudeFt)
                    return ThreatLevel.ResolutionAdvisory;
                if (distanceNM <= taDistanceNM && separation <= taAltitudeFt)
                    return ThreatLevel.TrafficAdvisory;
            }
            return distanceNM <= proximateDistanceNM && separation <= proximateAltitudeFt
                ? ThreatLevel.Proximate : ThreatLevel.OtherTraffic;
        }
    }

    /// <summary>
    /// Data structure for a traffic target on the radar.
    /// </summary>
    [System.Serializable]
    public class RadarTrafficTarget
    {
        public string icao24;
        public string callsign;
        public float latitude;
        public float longitude;
        public float altitudeFt;
        public float heading;
        public float groundSpeedKts;
        public float verticalRateFpm;
        public float sampleAgeSeconds;
        
        // Calculated fields
        public float distanceNM;
        public float bearingDeg;
        public float relativeAltitudeFt;
        public ThreatLevel threatLevel;
        
        // Radar display position (normalized -1 to 1)
        public Vector2 radarPosition;
    }
}
