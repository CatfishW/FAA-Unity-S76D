using UnityEngine;

namespace IndicatorSystem.Core
{
    public enum TrafficGlyphDetail { FarDoubleDiamond, MediumType, CloseAttitude }

    /// <summary>Distance detail is independent of threat classification and never invents an attitude.</summary>
    public static class TrafficGlyphPolicy
    {
        public const float BaseDiameter = 42f;
        public const float MaximumSymbolScale = 64f / BaseDiameter;
        public const float MaximumHalfExtent = 40f;

        public static TrafficGlyphDetail ResolveDetail(float distanceNM, float closeDistanceNM, float farDistanceNM,
            float hysteresisNM, bool adaptive, bool validAttitude, TrafficGlyphDetail previous, bool initialized)
        {
            if (!adaptive) return TrafficGlyphDetail.MediumType;
            if (!Finite(distanceNM) || distanceNM < 0f) return TrafficGlyphDetail.FarDoubleDiamond;
            float close = Finite(closeDistanceNM) ? Mathf.Max(0f, closeDistanceNM) : 5f;
            float far = Finite(farDistanceNM) ? Mathf.Max(close + .1f, farDistanceNM) : Mathf.Max(close + .1f, 40f);
            float band = Finite(hysteresisNM) ? Mathf.Clamp(hysteresisNM, 0f, (far - close) * .45f) : 0f;
            if (!initialized)
                return distanceNM > far ? TrafficGlyphDetail.FarDoubleDiamond :
                    distanceNM <= close && validAttitude ? TrafficGlyphDetail.CloseAttitude : TrafficGlyphDetail.MediumType;

            if (previous == TrafficGlyphDetail.CloseAttitude && !validAttitude)
                return distanceNM > far + band ? TrafficGlyphDetail.FarDoubleDiamond : TrafficGlyphDetail.MediumType;
            switch (previous)
            {
                case TrafficGlyphDetail.CloseAttitude:
                    return distanceNM > far + band ? TrafficGlyphDetail.FarDoubleDiamond :
                        distanceNM > close + band ? TrafficGlyphDetail.MediumType : TrafficGlyphDetail.CloseAttitude;
                case TrafficGlyphDetail.FarDoubleDiamond:
                    if (distanceNM >= far - band) return TrafficGlyphDetail.FarDoubleDiamond;
                    return validAttitude && distanceNM < close - band ? TrafficGlyphDetail.CloseAttitude : TrafficGlyphDetail.MediumType;
                default:
                    return distanceNM > far + band ? TrafficGlyphDetail.FarDoubleDiamond :
                        validAttitude && distanceNM < close - band ? TrafficGlyphDetail.CloseAttitude : TrafficGlyphDetail.MediumType;
            }
        }

        public static float ResolveScale(float distanceNM, bool scaling, float closeDistanceNM, float farDistanceNM,
            float closeScale, float farScale)
        {
            if (!scaling) return 1f;
            float close = Finite(closeDistanceNM) ? Mathf.Max(0, closeDistanceNM) : 5f;
            float far = Finite(farDistanceNM) ? Mathf.Max(close + .1f, farDistanceNM) : Mathf.Max(close + .1f, 40f);
            float nearSize = Finite(closeScale) ? closeScale : 1.5f;
            float farSize = Finite(farScale) ? farScale : .6f;
            float t = Finite(distanceNM) ? Mathf.InverseLerp(close, far, Mathf.Max(0, distanceNM)) : 1f;
            return Mathf.Clamp(Mathf.Lerp(nearSize, farSize, t), .35f, MaximumSymbolScale);
        }

        // Upstream Has* flags represent fresh source validity, not merely the presence of a zero-valued field.
        public static bool HasValidAttitude(IndicatorData data) => data.HasHeading && data.HasPitch && data.HasRoll &&
            Finite(data.Heading) && Finite(data.PitchDegrees) && Finite(data.RollDegrees) && ValidRotation(data.ViewRelativeRotation);

        public static bool ValidRotation(Quaternion rotation)
        {
            float length = Quaternion.Dot(rotation, rotation);
            return Finite(rotation.x) && Finite(rotation.y) && Finite(rotation.z) && Finite(rotation.w) && Finite(length) && length > .0001f;
        }

        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
