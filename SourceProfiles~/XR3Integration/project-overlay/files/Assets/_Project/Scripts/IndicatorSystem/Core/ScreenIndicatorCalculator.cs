using UnityEngine;

namespace IndicatorSystem.Core
{
    /// <summary>
    /// Static utility for calculating screen-space indicator positioning.
    /// High cohesion: Single responsibility for geometric calculations.
    /// </summary>
    public static class ScreenIndicatorCalculator
    {
        private const float NauticalMileToMeters = 1852f;
        private const float FeetToMeters = 0.3048f;

        /// <summary>Per-target presentation history; it never changes the camera, head pose or projection.</summary>
        public sealed class VisibilityHistory
        {
            private bool sampled, onScreen;
            private Camera view;
            private Rect region, viewport;
            private string id, label;
            private IndicatorType type;
            private System.Type targetType;

            public void Reset() { sampled = onScreen = false; view = null; id = label = null; targetType = null; }

            internal bool Select(bool rawInside, Vector2 absolute, IIndicatorTarget target, Camera camera,
                Rect physicalRegion, Rect cameraViewport, float reentryInset)
            {
                // Pixel-scale rounding is not a new view. Keep the original context until a real
                // layout change crosses half a pixel; do not reset for moving camera/world poses.
                if (view != camera || id != target.Id || label != target.Label || type != target.Type || targetType != target.GetType() ||
                    !SameRect(region, physicalRegion) || !SameRect(viewport, cameraViewport))
                {
                    Reset(); view = camera; id = target.Id; label = target.Label; type = target.Type; targetType = target.GetType();
                    region = physicalRegion; viewport = cameraViewport;
                }
                bool selected = rawInside;
                if (rawInside && sampled && !onScreen)
                {
                    Rect usable = Rect.MinMaxRect(Mathf.Max(physicalRegion.xMin, cameraViewport.xMin), Mathf.Max(physicalRegion.yMin, cameraViewport.yMin),
                        Mathf.Min(physicalRegion.xMax, cameraViewport.xMax), Mathf.Min(physicalRegion.yMax, cameraViewport.yMax));
                    float band = IsFinite(reentryInset) ? Mathf.Clamp(reentryInset, 0f, Mathf.Max(0f, Mathf.Min(usable.width, usable.height)) * .1f) : 0f;
                    selected = absolute.x >= usable.xMin + band && absolute.x <= usable.xMax - band &&
                               absolute.y >= usable.yMin + band && absolute.y <= usable.yMax - band;
                }
                sampled = true;
                onScreen = selected; // A genuinely outside/behind sample always leaves immediately.
                return selected;
            }

            private static bool SameRect(Rect a, Rect b) => Mathf.Abs(a.xMin - b.xMin) <= .5f && Mathf.Abs(a.yMin - b.yMin) <= .5f &&
                Mathf.Abs(a.xMax - b.xMax) <= .5f && Mathf.Abs(a.yMax - b.yMax) <= .5f;
        }

        /// <summary>
        /// Calculate indicator data from a world position.
        /// </summary>
        /// <param name="worldPosition">Target position in world space</param>
        /// <param name="camera">Camera to use for screen projection</param>
        /// <param name="edgeConfig">Edge clamping configuration</param>
        /// <returns>Calculated indicator data with screen position and visibility</returns>
        public static IndicatorData CalculateIndicator(
            IIndicatorTarget target,
            Camera camera,
            IndicatorEdgeConfig edgeConfig) => CalculateIndicatorInRect(target, camera, edgeConfig, camera.pixelRect);

        /// <summary>Project with the full camera, then clip/clamp cues to an absolute pixel region.</summary>
        public static IndicatorData CalculateIndicatorInRect(
            IIndicatorTarget target,
            Camera camera,
            IndicatorEdgeConfig edgeConfig,
            Rect region) => CalculateIndicatorCore(target, camera, edgeConfig, region, null, 0f);

        /// <summary>
        /// Uses the same single projection as the stateless path. After leaving the usable frame,
        /// reacquire its interior by a small spatial inset before replacing an edge cue with an exact target glyph.
        /// </summary>
        public static IndicatorData CalculateIndicatorWithVisibilityHistory(IIndicatorTarget target, Camera camera,
            IndicatorEdgeConfig edgeConfig, Rect region, VisibilityHistory history, float reentryInset)
            => CalculateIndicatorCore(target, camera, edgeConfig, region, history, reentryInset);

        private static IndicatorData CalculateIndicatorCore(IIndicatorTarget target, Camera camera,
            IndicatorEdgeConfig edgeConfig, Rect region, VisibilityHistory history, float reentryInset)
        {
            var data = new IndicatorData
            {
                Id = target.Id,
                Type = target.Type,
                Color = target.DisplayColor,
                Priority = target.Priority,
                Label = target.Label,
                DistanceNM = target.DistanceNM,
                RelativeAltitudeFeet = target.RelativeAltitudeFeet,
                WorldPosition = target.WorldPosition,
                AircraftType = target.AircraftType,
                WeatherKind = target is IWeatherIndicatorTarget weather ? weather.WeatherKind : WeatherCueKind.Return,
                IsIllustrativeWeather = target is IWeatherIndicatorTarget source && source.IsIllustrative,
                Heading = target.Heading,
                ProjectionRect = region,
                ViewRelativeRotation = Quaternion.identity,
                IsActive = true
            };

            if (target is ITrafficIndicatorKinematics traffic && target.Type == IndicatorType.Traffic)
            {
                data.HasHeading = traffic.HasHeading && IsFinite(target.Heading);
                data.HasPitch = traffic.HasPitch && IsFinite(traffic.PitchDegrees) && Mathf.Abs(traffic.PitchDegrees) <= 90f;
                data.HasRoll = traffic.HasRoll && IsFinite(traffic.RollDegrees) && Mathf.Abs(traffic.RollDegrees) <= 360f;
                data.HasVerticalSpeed = traffic.HasVerticalSpeed && IsFinite(traffic.VerticalSpeedFpm);
                data.PitchDegrees = data.HasPitch ? traffic.PitchDegrees : 0f;
                data.RollDegrees = data.HasRoll ? traffic.RollDegrees : 0f;
                data.VerticalSpeedFpm = data.HasVerticalSpeed ? traffic.VerticalSpeedFpm : 0f;
                Vector3 sight = AircraftControl.Camera.AircraftViewRenderSpace.DataToRenderPoint(camera, target.WorldPosition) - camera.transform.position;
                // Keep reported field validity distinct from view-pose validity:
                // a coincident/invalid sight line cannot define observer axes.
                if (data.HasHeading && data.HasPitch && data.HasRoll) data.ViewRelativeRotation = new Quaternion();
                if (data.HasHeading && IsFinite(sight.x) && IsFinite(sight.y) && IsFinite(sight.z) && sight.sqrMagnitude > 0.0001f)
                {
                    float sightHeading = Mathf.Atan2(sight.x, sight.z) * Mathf.Rad2Deg;
                    data.RelativeHeadingDegrees = Mathf.DeltaAngle(sightHeading, target.Heading);
                    if (data.HasPitch && data.HasRoll)
                    {
                        Vector3 up = camera.transform.up;
                        if (Mathf.Abs(Vector3.Dot(sight.normalized, up)) > 0.999f) up = camera.transform.forward;
                        Quaternion basis = Quaternion.LookRotation(sight, up);
                        Quaternion body = Quaternion.Euler(-data.PitchDegrees, target.Heading, -data.RollDegrees);
                        data.ViewRelativeRotation = Quaternion.Inverse(basis) * body;
                    }
                }
            }

            // Check distance limit
            if (!IsFinite(region.x) || !IsFinite(region.y) || !IsFinite(region.width) || !IsFinite(region.height) ||
                region.width <= 0f || region.height <= 0f ||
                !IsFinite(target.DistanceNM) || target.DistanceNM < 0f ||
                !IsFinite(target.WorldPosition.x) || !IsFinite(target.WorldPosition.y) ||
                !IsFinite(target.WorldPosition.z) || target.DistanceNM > edgeConfig.MaxDisplayDistance)
            {
                history?.Reset();
                data.Visibility = IndicatorVisibility.OutOfRange;
                data.IsActive = false;
                return data;
            }

            // Get viewport position (0-1 range)
            Rect viewport = camera.pixelRect;
            if (history != null && (!IsFinite(viewport.x) || !IsFinite(viewport.y) || !IsFinite(viewport.width) || !IsFinite(viewport.height) || viewport.width <= 0f || viewport.height <= 0f))
            {
                history.Reset(); data.Visibility = IndicatorVisibility.OutOfRange; data.IsActive = false;
                return data;
            }
            Vector3 renderPosition = AircraftControl.Camera.AircraftViewRenderSpace.DataToRenderPoint(camera, target.WorldPosition);
            Vector3 viewportPos = camera.WorldToViewportPoint(renderPosition);
            if (history != null && (!IsFinite(viewportPos.x) || !IsFinite(viewportPos.y) || !IsFinite(viewportPos.z) || viewportPos.z == 0f))
            {
                history.Reset(); data.Visibility = IndicatorVisibility.OutOfRange; data.IsActive = false;
                return data;
            }
            
            // Check if behind camera
            if (viewportPos.z < 0)
            {
                data.Visibility = IndicatorVisibility.Behind;
                // Flip position for behind-camera targets
                viewportPos.x = 1f - viewportPos.x;
                viewportPos.y = 1f - viewportPos.y;
            }

            // Camera projection stays in the actual eye/spectator pixel rect.
            // The HUD region affects clipping and arrow anchors, never projection.
            float screenWidth = region.width;
            float screenHeight = region.height;
            Vector2 absolute = new Vector2(viewport.x + viewportPos.x * viewport.width,
                viewport.y + viewportPos.y * viewport.height);
            Vector2 screenPos = absolute - region.position;

            // Visibility describes the actual camera frustum, not our UI inset.
            // Padding only locates off-screen arrows; a visible edge target
            // must keep its exact projection instead of becoming a false arrow.
            float padding = Mathf.Clamp(edgeConfig.EdgePadding, 0f, Mathf.Min(screenWidth, screenHeight) * 0.45f);
            bool isOnScreen = viewportPos.z > 0 &&
                              viewportPos.x >= 0f && viewportPos.x <= 1f &&
                              viewportPos.y >= 0f && viewportPos.y <= 1f &&
                              screenPos.x >= 0f &&
                              screenPos.x <= screenWidth &&
                              screenPos.y >= 0f &&
                              screenPos.y <= screenHeight;

            if (history != null) isOnScreen = history.Select(isOnScreen, absolute, target, camera, region, viewport, reentryInset);

            if (isOnScreen)
            {
                data.Visibility = IndicatorVisibility.OnScreen;
                data.ScreenPosition = absolute;
                data.ArrowRotation = 0f;
            }
            else
            {
                // Off-screen: clamp to edge and calculate arrow rotation
                if (data.Visibility != IndicatorVisibility.Behind)
                    data.Visibility = IndicatorVisibility.OffScreen;
                
                Vector2 screenCenter = new Vector2(screenWidth / 2f, screenHeight / 2f);
                // Directly behind projects onto the screen centre. Give it an
                // explicit aft cue at the bottom edge, never an on-screen target.
                // Calibrated asymmetric XR projections need not put the eye's
                // physical forward/back axis at viewport (0.5,0.5).
                Vector3 viewDirection = camera.transform.InverseTransformPoint(renderPosition);
                bool directlyBehind = viewDirection.z < 0f &&
                    viewDirection.x * viewDirection.x + viewDirection.y * viewDirection.y <=
                    viewDirection.z * viewDirection.z * 0.000001f;
                if (directlyBehind || (screenPos - screenCenter).sqrMagnitude < 0.001f)
                    screenPos = screenCenter + Vector2.down * screenHeight;
                Vector2 clampedPos = ClampToScreenEdge(screenPos, screenWidth, screenHeight, padding);
                data.ScreenPosition = clampedPos + region.position;
                
                // Calculate arrow rotation pointing toward target
                Vector2 direction = (screenPos - screenCenter).normalized;
                data.ArrowRotation = Mathf.Atan2(direction.x, direction.y) * Mathf.Rad2Deg;
            }

            return data;
        }

        /// <summary>
        /// Clamp a screen position to the screen edge with padding.
        /// </summary>
        private static Vector2 ClampToScreenEdge(Vector2 screenPos, float width, float height, float padding)
        {
            Vector2 center = new Vector2(width / 2f, height / 2f);
            Vector2 direction = screenPos - center;
            
            if (direction.sqrMagnitude < 0.001f)
                return center;

            direction.Normalize();

            // Calculate intersection with screen bounds
            float minX = padding;
            float maxX = width - padding;
            float minY = padding;
            float maxY = height - padding;

            // Find the scalar to reach each edge
            float tX = direction.x != 0 
                ? (direction.x > 0 ? (maxX - center.x) / direction.x : (minX - center.x) / direction.x) 
                : float.MaxValue;
            float tY = direction.y != 0 
                ? (direction.y > 0 ? (maxY - center.y) / direction.y : (minY - center.y) / direction.y) 
                : float.MaxValue;

            // Use the smaller scalar (first intersection)
            float t = Mathf.Min(Mathf.Abs(tX), Mathf.Abs(tY));
            
            return center + direction * t;
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static Vector3 RadarBearingToWorldPosition(float distanceNM, float bearingDegrees,
            float relativeAltitudeFeet, Vector3 origin)
        {
            float angle = bearingDegrees * Mathf.Deg2Rad;
            float distance = Mathf.Max(0f, distanceNM) * NauticalMileToMeters;
            return origin + new Vector3(distance * Mathf.Sin(angle),
                relativeAltitudeFeet * FeetToMeters, distance * Mathf.Cos(angle));
        }

        /// <summary>
        /// Convert geographic coordinates to Unity world position.
        /// Uses a simple local tangent plane approximation around a reference point.
        /// </summary>
        /// <param name="latitude">Target latitude in degrees</param>
        /// <param name="longitude">Target longitude in degrees</param>
        /// <param name="altitudeMeters">Target altitude in meters MSL</param>
        /// <param name="refLatitude">Reference latitude (own position)</param>
        /// <param name="refLongitude">Reference longitude (own position)</param>
        /// <param name="refAltitude">Reference altitude in meters</param>
        /// <param name="metersPerUnit">Meters per Unity unit (default 1)</param>
        /// <returns>World position in Unity coordinates</returns>
        public static Vector3 GeoToWorldPosition(
            double latitude, double longitude, float altitudeMeters,
            double refLatitude, double refLongitude, float refAltitude,
            float metersPerUnit = 1f)
        {
            // Earth's radius in meters
            const double EarthRadius = 6371000.0;
            
            // Convert to radians
            double latRad = latitude * Mathf.Deg2Rad;
            double lonRad = longitude * Mathf.Deg2Rad;
            double refLatRad = refLatitude * Mathf.Deg2Rad;
            double refLonRad = refLongitude * Mathf.Deg2Rad;
            
            // Calculate differences
            double dLat = latRad - refLatRad;
            double dLon = lonRad - refLonRad;
            
            // Convert to meters using small angle approximation
            double northMeters = dLat * EarthRadius;
            double eastMeters = dLon * EarthRadius * System.Math.Cos(refLatRad);
            double upMeters = altitudeMeters - refAltitude;
            
            // Convert to Unity coordinates (X=East, Y=Up, Z=North)
            return new Vector3(
                (float)(eastMeters / metersPerUnit),
                (float)(upMeters / metersPerUnit),
                (float)(northMeters / metersPerUnit)
            );
        }

        /// <summary>
        /// Convert a heading-up radar bearing/range into a world point relative to the headset/camera.
        /// This keeps screen-edge cues aligned with X-Plane radar symbology even when the aircraft turns.
        /// </summary>
        public static Vector3 RadarRelativeToWorldPosition(
            float distanceNM,
            float relativeBearingDegrees,
            float relativeAltitudeFeet,
            Transform reference)
        {
            return RadarRelativeMetersToWorldPosition(
                Mathf.Max(0f, distanceNM) * NauticalMileToMeters,
                relativeBearingDegrees,
                relativeAltitudeFeet * FeetToMeters,
                reference);
        }

        /// <summary>
        /// Convert a heading-up radar bearing/range into a world point relative to the headset/camera.
        /// </summary>
        public static Vector3 RadarRelativeMetersToWorldPosition(
            float distanceMeters,
            float relativeBearingDegrees,
            float verticalOffsetMeters,
            Transform reference)
        {
            Vector3 origin = reference != null ? reference.position : Vector3.zero;
            Vector3 forward = reference != null ? reference.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f)
            {
                forward = Vector3.forward;
            }
            forward.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            float bearingRad = relativeBearingDegrees * Mathf.Deg2Rad;
            float sideMeters = distanceMeters * Mathf.Sin(bearingRad);
            float forwardMeters = distanceMeters * Mathf.Cos(bearingRad);

            return origin + right * sideMeters + forward * forwardMeters + Vector3.up * verticalOffsetMeters;
        }

        /// <summary>
        /// Return a signed heading-up bearing where 0 is ahead, +90 is right, and -90 is left.
        /// </summary>
        public static float CalculateRelativeBearing(float absoluteBearingDegrees, float ownHeadingDegrees)
        {
            return Mathf.DeltaAngle(ownHeadingDegrees, absoluteBearingDegrees);
        }

        /// <summary>
        /// Calculate bearing from reference to target position.
        /// </summary>
        public static float CalculateBearing(double lat1, double lon1, double lat2, double lon2)
        {
            float dLon = (float)(lon2 - lon1) * Mathf.Deg2Rad;
            float lat1Rad = (float)lat1 * Mathf.Deg2Rad;
            float lat2Rad = (float)lat2 * Mathf.Deg2Rad;

            float y = Mathf.Sin(dLon) * Mathf.Cos(lat2Rad);
            float x = Mathf.Cos(lat1Rad) * Mathf.Sin(lat2Rad) -
                      Mathf.Sin(lat1Rad) * Mathf.Cos(lat2Rad) * Mathf.Cos(dLon);

            float bearing = Mathf.Atan2(y, x) * Mathf.Rad2Deg;
            return (bearing + 360f) % 360f;
        }
    }
}
