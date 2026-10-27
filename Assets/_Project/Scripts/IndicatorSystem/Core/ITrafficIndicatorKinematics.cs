using UnityEngine;

namespace IndicatorSystem.Core
{
    /// <summary>Optional reported traffic pose; absent fields remain unknown, including zero-valued defaults.</summary>
    public interface ITrafficIndicatorKinematics
    {
        bool HasHeading { get; }
        bool HasPitch { get; }
        bool HasRoll { get; }
        bool HasVerticalSpeed { get; }
        float PitchDegrees { get; }
        float RollDegrees { get; }
        float VerticalSpeedFpm { get; }
    }
}
