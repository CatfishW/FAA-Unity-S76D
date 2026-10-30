using UnityEngine;

namespace FAA.Customization
{
    public enum FaaExceedance { Normal=0, Caution=1, Warning=2, Invalid=3 }

    /// <summary>
    /// Engine, rotor and airspeed limit classification shared by the Digital and Classic Analog symbology.
    /// DEMONSTRATOR ASSUMPTIONS for an S-76D-class twin. These are NOT Rotorcraft Flight Manual data: replace them with the
    /// S-76D RFM Section 1 limits (or aircraft limit datarefs) before operational evaluation. <see cref="Configured"/> stays false until then.
    /// Colour use follows AC 20-88A / AC 25-11B: green normal, amber caution or time-limited, red exceedance.
    /// </summary>
    public static class FaaRotorcraftLimits
    {
        /// <summary>False while the demonstrator defaults below are in use.</summary>
        public static bool Configured=false;

        public const float TorqueCautionAbove=100f;   // AEO maximum continuous
        public const float TorqueWarningAbove=110f;   // transient maximum
        public const float TorqueScaleMax=120f;       // display scale; values above it peg the pointer, the digits stay true

        public const float NrWarningBelow=91f;
        public const float NrCautionBelow=95f;
        public const float NrCautionAbove=107f;
        public const float NrWarningAbove=110f;
        public const float NrScaleMax=120f;

        public const float N2CautionAbove=107f;
        public const float N2WarningAbove=110f;

        public const float VneKnots=155f;

        /// <summary>Values beyond these are treated as invalid data, not as exceedances.</summary>
        public const float TorqueImplausibleAbove=300f, NrImplausibleAbove=200f, N2ImplausibleAbove=150f;

        private static bool Usable(float v,bool valid)=>valid&&!float.IsNaN(v)&&!float.IsInfinity(v)&&v>=0;

        public static FaaExceedance Torque(float percent,bool valid)
        {
            if(!Usable(percent,valid)||percent>TorqueImplausibleAbove)return FaaExceedance.Invalid;
            return percent>TorqueWarningAbove?FaaExceedance.Warning:percent>TorqueCautionAbove?FaaExceedance.Caution:FaaExceedance.Normal;
        }

        /// <summary>Rotor NR. Low-side alerts apply only when airborne, so ground start or idle does not show red.</summary>
        public static FaaExceedance RotorNr(float percent,bool valid,bool airborne=true)
        {
            if(!Usable(percent,valid)||percent>NrImplausibleAbove)return FaaExceedance.Invalid;
            if(percent>NrWarningAbove)return FaaExceedance.Warning;
            if(percent>NrCautionAbove)return FaaExceedance.Caution;
            if(airborne)
            {
                if(percent<NrWarningBelow)return FaaExceedance.Warning;
                if(percent<NrCautionBelow)return FaaExceedance.Caution;
            }
            return FaaExceedance.Normal;
        }

        /// <summary>Engine N2 (power turbine), high side only.</summary>
        public static FaaExceedance EngineN2(float percent,bool valid)
        {
            if(!Usable(percent,valid)||percent>N2ImplausibleAbove)return FaaExceedance.Invalid;
            return percent>N2WarningAbove?FaaExceedance.Warning:percent>N2CautionAbove?FaaExceedance.Caution:FaaExceedance.Normal;
        }

        public static FaaExceedance Airspeed(float knots,bool valid)
        {
            if(!Usable(knots,valid))return FaaExceedance.Invalid;
            return knots>VneKnots?FaaExceedance.Warning:FaaExceedance.Normal;
        }

        public static FaaHudSeverity ToSeverity(FaaExceedance e)
        {
            switch(e)
            {
                case FaaExceedance.Caution: return FaaHudSeverity.Caution;
                case FaaExceedance.Warning: return FaaHudSeverity.Warning;
                case FaaExceedance.Invalid: return FaaHudSeverity.Invalid;
                default: return FaaHudSeverity.Normal;
            }
        }

        public static Color ColorFor(FaaExceedance e,Color normal)=>FaaHudStyle.ForSeverity(ToSeverity(e),normal);

        /// <summary>Simple airborne estimate for low-side NR alerting when no weight-on-wheels signal exists.</summary>
        public static bool LikelyAirborne(float indicatedAirspeedKt,float heightAboveGroundFt,bool hagValid)
            => (hagValid&&heightAboveGroundFt>10f)||indicatedAirspeedKt>30f;
    }
}
