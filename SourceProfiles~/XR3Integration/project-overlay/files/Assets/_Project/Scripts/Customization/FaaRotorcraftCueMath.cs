using System.Collections.Generic;
using UnityEngine;

namespace FAA.Customization
{
    /// <summary>Pure geometry for the research rotorcraft display. Unity axes: east X, up Y, north Z.</summary>
    public static class FaaRotorcraftCueMath
    {
        public const float KnotsToMetersPerSecond = 0.514444444f;
        public const float FeetPerMinuteToMetersPerSecond = 0.00508f;

        public static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        public static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);

        public static Vector3 EarthRay(float elevationDegrees, float trueBearingDegrees)
        {
            if (!Finite(elevationDegrees) || !Finite(trueBearingDegrees)) return Vector3.zero;
            float e = elevationDegrees * Mathf.Deg2Rad;
            float b = trueBearingDegrees * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(b) * Mathf.Cos(e), Mathf.Sin(e), Mathf.Cos(b) * Mathf.Cos(e));
        }

        /// <summary>Uses ground track, never magnetic heading or indicated airspeed. Zero speed needs no track.</summary>
        public static bool TryGroundVelocity(float groundSpeedKnots, float trueTrackDegrees,
            float verticalSpeedFeetPerMinute, out Vector3 velocity)
        {
            velocity = Vector3.zero;
            if (!Finite(groundSpeedKnots) || groundSpeedKnots < 0f || !Finite(verticalSpeedFeetPerMinute)) return false;
            if (groundSpeedKnots > 0.01f && !Finite(trueTrackDegrees)) return false;
            if (groundSpeedKnots > 0.01f)
                velocity = EarthRay(0f, trueTrackDegrees) * (groundSpeedKnots * KnotsToMetersPerSecond);
            velocity.y = verticalSpeedFeetPerMinute * FeetPerMinuteToMetersPerSecond;
            return Finite(velocity);
        }

        public static bool TryFlightPathAngle(Vector3 velocity, out float degrees)
        {
            degrees = 0f;
            if (!Finite(velocity) || velocity.sqrMagnitude < 0.000001f) return false;
            degrees = Mathf.Atan2(velocity.y, new Vector2(velocity.x, velocity.z).magnitude) * Mathf.Rad2Deg;
            return true;
        }

        /// <summary>Hysteresis avoids mode flicker: enter at 5 kt, leave at 8 kt. Invalid speed is not hover.</summary>
        public static bool UseHover(float knots, bool wasHover, float enter = 5f, float exit = 8f) =>
            Finite(knots) && knots >= 0f && knots < (wasHover ? Mathf.Max(enter, exit) : enter);

        /// <summary>Plan-view instrument units: right X, forward Y, in knots. Not a conformal world vector.</summary>
        public static Vector2 HoverVelocity(Vector3 groundVelocity, float trueHeadingDegrees)
        {
            if (!Finite(groundVelocity) || !Finite(trueHeadingDegrees)) return Vector2.zero;
            Vector3 bodyHorizontal = Quaternion.AngleAxis(-trueHeadingDegrees, Vector3.up) * groundVelocity;
            return new Vector2(bodyHorizontal.x, bodyHorizontal.z) / KnotsToMetersPerSecond;
        }

        /// <summary>Stable screen observation for tests/status only; native stereo rendering projects the real mesh per eye.</summary>
        public static bool TryProjectDirection(Camera view, Vector3 worldDirection, out Vector2 viewport)
        {
            viewport = Vector2.zero;
            if (view == null || view.orthographic || !Finite(worldDirection) || worldDirection.sqrMagnitude < .000001f) return false;
            return TryProjectLocal(view.nonJitteredProjectionMatrix, Quaternion.Inverse(view.transform.rotation) * worldDirection, out viewport);
        }

        /// <summary>Projects a camera-space direction (+Z forward) to a viewport point. Allocation-free; callers cache the matrix once per frame.</summary>
        public static bool TryProjectLocal(Matrix4x4 projection, Vector3 local, out Vector2 viewport)
        {
            viewport = Vector2.zero;
            if (!Finite(local) || local.z <= .0001f) return false;
            Vector4 clip = projection * new Vector4(local.x, local.y, -local.z, 0f);
            if (!Finite(clip.w) || Mathf.Abs(clip.w) < .000001f) return false;
            viewport = new Vector2(clip.x / clip.w * .5f + .5f, clip.y / clip.w * .5f + .5f);
            return Finite(viewport.x) && Finite(viewport.y);
        }

        /// <summary>Inverse of <see cref="TryProjectLocal"/>: the camera-space direction (z = 1) that lands on a viewport point. Works for asymmetric (XR) frusta.</summary>
        public static bool TryUnprojectLocal(Matrix4x4 projection, Vector2 viewport, out Vector3 local)
        {
            local = Vector3.forward;
            if (!Finite(viewport.x) || !Finite(viewport.y)) return false;
            float nx = viewport.x * 2f - 1f, ny = viewport.y * 2f - 1f;
            float a11 = projection.m00 - nx * projection.m30, a12 = projection.m01 - nx * projection.m31, b1 = projection.m02 - nx * projection.m32;
            float a21 = projection.m10 - ny * projection.m30, a22 = projection.m11 - ny * projection.m31, b2 = projection.m12 - ny * projection.m32;
            float det = a11 * a22 - a12 * a21;
            if (!Finite(det) || Mathf.Abs(det) < 1e-9f) return false;
            local = new Vector3((b1 * a22 - a12 * b2) / det, (a11 * b2 - b1 * a21) / det, 1f);
            return Finite(local);
        }

        public static bool InView(Camera view, Vector3 direction, float margin = .015f) =>
            TryProjectDirection(view, direction, out Vector2 p) &&
            p.x >= margin && p.x <= 1f - margin && p.y >= margin && p.y <= 1f - margin;

        public static bool Fresh(bool healthy, float packetAge, float maximumAge) =>
            healthy && Finite(packetAge) && packetAge >= 0f && packetAge <= maximumAge;

        public static float SelectFpa(float current, float requested) =>
            Finite(requested) ? Mathf.Clamp(requested, -15f, 10f) : current;

        // ---------------------------------------------------------------------------------------------
        // Windowed conformal ladder, data-driven waterline, declutter and legibility (FAA HUD overhaul).
        // ---------------------------------------------------------------------------------------------

        /// <summary>Unusual-attitude declutter with hysteresis (AC 25-11B 5.10.3.2, F.5.4.6.3; AC 23.1311-1C 17.4). Simulator choices, not RFM data.</summary>
        public const float UnusualBankEnter = 60f, UnusualPitchUpEnter = 30f, UnusualPitchDownEnter = -20f;
        public const float UnusualBankExit = 55f, UnusualPitchUpExit = 25f, UnusualPitchDownExit = -15f;
        /// <summary>Desktop eye geometry shared with FaaHudStyle: 1 arcmin is about .654 reference units (1080-high canvas) at 700 mm on a 27-in 4K monitor.</summary>
        public const float ReferenceUnitsPerArcmin = .654f, ReferenceCanvasHeight = 1080f, ArcminPerMilliradian = 3.4377f;
        /// <summary>HF-STD-001B 5.3.3.2.6/.9: HUD alphanumerics at least 28 arcmin (8.1 mrad); line width 1.0 +/- 0.2 mrad.</summary>
        public const float HudAlphanumericArcmin = 28f;
        /// <summary>Selected-FPA reference is shown only in an approach context (HAGL at or below this height).</summary>
        public const float DefaultFpaApproachAglFeet = 1500f;

        /// <summary>
        /// Aircraft body rotation from validated flight data. X-Plane: +pitch nose up, +roll right wing down, true heading.
        /// Unity Euler: +X is nose DOWN and +Z rolls left, so both signs are inverted. Never read attitude from the OwnAircraft transform.
        /// </summary>
        public static Quaternion BodyRotation(float pitch, float roll, float trueHeading) => Quaternion.Euler(-pitch, trueHeading, -roll);

        /// <summary>Direction at a constant angular offset <paramref name="lateralDegrees"/> from the rung centre (screen-angle rung, not earth bearing).</summary>
        public static Vector3 RungPoint(float elevation, float heading, float lateralDegrees)
        {
            Vector3 centre = EarthRay(elevation, heading), side = EarthRay(0f, heading + 90f);
            if (!Finite(lateralDegrees)) return centre;
            float a = lateralDegrees * Mathf.Deg2Rad;
            return (centre * Mathf.Cos(a) + side * Mathf.Sin(a)).normalized;
        }

        /// <summary>Gnomonic body-frame point: <paramref name="x"/> right and <paramref name="y"/> up, in degrees from the body forward axis.</summary>
        public static Vector3 BodyPoint(Quaternion body, float x, float y) =>
            (body * new Vector3(Mathf.Tan(x * Mathf.Deg2Rad), Mathf.Tan(y * Mathf.Deg2Rad), 1f)).normalized;

        /// <summary>True when a relative bearing falls inside the symmetric horizon gap around the aircraft reference.</summary>
        public static bool InHorizonGap(float relativeBearing, float gapDegrees) =>
            Finite(relativeBearing) && Mathf.Abs(Mathf.DeltaAngle(0f, relativeBearing)) < gapDegrees;

        /// <summary>Enter at |bank| &gt; 60 or pitch &gt; +30 or pitch &lt; -20; release only when |bank| &lt; 55 and -15 &lt; pitch &lt; +25. Invalid input is never unusual.</summary>
        public static bool UnusualAttitude(float pitch, float roll, bool wasUnusual)
        {
            if (!Finite(pitch) || !Finite(roll)) return false;
            float bank = Mathf.Abs(Mathf.DeltaAngle(0f, roll));
            if (wasUnusual) return !(bank < UnusualBankExit && pitch > UnusualPitchDownExit && pitch < UnusualPitchUpExit);
            return bank > UnusualBankEnter || pitch > UnusualPitchUpEnter || pitch < UnusualPitchDownEnter;
        }

        /// <summary>Selected-FPA reference only in an approach context (valid HAGL at or below the limit, or G/S active/armed), never in hover or unusual attitude.</summary>
        public static bool ShowFpaReference(bool enabled, bool hover, bool unusual, bool aglValid, float aglFeet, bool glideslopeMode,
            float maxAglFeet = DefaultFpaApproachAglFeet) =>
            enabled && !hover && !unusual && (glideslopeMode || aglValid && Finite(aglFeet) && aglFeet >= 0f && aglFeet <= maxAglFeet);

        /// <summary>
        /// Shrinks <paramref name="start"/> so it overlaps none of the protected rectangles. Each rectangle that overlaps the window and lies
        /// left, right, above or below the window centre pushes the nearest edge past it (the side that loses the least area is chosen).
        /// Rectangles containing the centre are ignored (attitude has priority). A result smaller than <paramref name="minimumSize"/> returns
        /// <paramref name="fallback"/>.
        /// </summary>
        public static Rect AttitudeWindow(Rect start, IReadOnlyList<Rect> keepOuts, float paddingPixels, Vector2 minimumSize, Rect fallback) =>
            AttitudeWindow(start, keepOuts, paddingPixels, minimumSize, fallback, start.center);

        /// <summary>
        /// As <see cref="AttitudeWindow(Rect, IReadOnlyList{Rect}, float, Vector2, Rect)"/>, but the side of each protected rectangle the
        /// window keeps is decided relative to <paramref name="reference"/> (the attitude-field centre). A window grown toward the
        /// aircraft reference therefore never jumps across the heading scale or a side column; a rectangle covering the reference is ignored.
        /// </summary>
        public static Rect AttitudeWindow(Rect start, IReadOnlyList<Rect> keepOuts, float paddingPixels, Vector2 minimumSize, Rect fallback, Vector2 reference)
        {
            if (!FiniteRect(start) || start.width <= 1f || start.height <= 1f) return fallback;
            Rect w = start; float pad = Mathf.Max(0f, Finite(paddingPixels) ? paddingPixels : 0f);
            Vector2 c = Finite(reference.x) && Finite(reference.y) ? reference : start.center;
            int count = keepOuts != null ? keepOuts.Count : 0;
            for (int i = 0; i < count; i++)
            {
                Rect r = keepOuts[i];
                if (!FiniteRect(r) || r.width <= 0f || r.height <= 0f || r.Contains(c)) continue;
                if (r.xMax + pad <= w.xMin || r.xMin - pad >= w.xMax || r.yMax + pad <= w.yMin || r.yMin - pad >= w.yMax) continue;
                float best = float.PositiveInfinity; int edge = -1;
                if (r.xMax <= c.x) Consider(0, (r.xMax + pad - w.xMin) * w.height, ref best, ref edge);
                if (r.xMin >= c.x) Consider(1, (w.xMax - (r.xMin - pad)) * w.height, ref best, ref edge);
                if (r.yMax <= c.y) Consider(2, (r.yMax + pad - w.yMin) * w.width, ref best, ref edge);
                if (r.yMin >= c.y) Consider(3, (w.yMax - (r.yMin - pad)) * w.width, ref best, ref edge);
                if (edge == 0) w.xMin = Mathf.Max(w.xMin, r.xMax + pad);
                else if (edge == 1) w.xMax = Mathf.Min(w.xMax, r.xMin - pad);
                else if (edge == 2) w.yMin = Mathf.Max(w.yMin, r.yMax + pad);
                else if (edge == 3) w.yMax = Mathf.Min(w.yMax, r.yMin - pad);
                if (w.width <= 1f || w.height <= 1f) return fallback;
            }
            return w.width < minimumSize.x || w.height < minimumSize.y ? fallback : w;
        }
        private static void Consider(int edge, float loss, ref float best, ref int chosen) { if (loss < best) { best = loss; chosen = edge; } }
        public static bool FiniteRect(Rect r) => Finite(r.x) && Finite(r.y) && Finite(r.width) && Finite(r.height);

        /// <summary>
        /// The attitude window built around the projected aircraft reference (C3): <paramref name="window"/> is extended toward
        /// <paramref name="content"/> (the waterline symbol plus the pitch margin around it) but never beyond <paramref name="bounds"/>,
        /// and never made smaller than <paramref name="window"/>. Protected rectangles are cleared afterwards by <see cref="AttitudeWindow(Rect, IReadOnlyList{Rect}, float, Vector2, Rect, Vector2)"/>.
        /// </summary>
        public static Rect GrowToward(Rect window, Rect content, Rect bounds)
        {
            if (!FiniteRect(window) || !FiniteRect(content) || !FiniteRect(bounds)) return window;
            return Rect.MinMaxRect(
                Mathf.Min(window.xMin, Mathf.Max(content.xMin, bounds.xMin)), Mathf.Min(window.yMin, Mathf.Max(content.yMin, bounds.yMin)),
                Mathf.Max(window.xMax, Mathf.Min(content.xMax, bounds.xMax)), Mathf.Max(window.yMax, Mathf.Min(content.yMax, bounds.yMax)));
        }

        /// <summary>Smallest offset that moves <paramref name="box"/> inside <paramref name="window"/>: zero when already inside, centred on an axis where it cannot fit.</summary>
        public static Vector2 ClampInside(Rect window, Rect box)
        {
            if (!FiniteRect(window) || !FiniteRect(box)) return Vector2.zero;
            return new Vector2(Inside(window.xMin, window.xMax, box.xMin, box.xMax), Inside(window.yMin, window.yMax, box.yMin, box.yMax));
        }
        private static float Inside(float min, float max, float bMin, float bMax)
        {
            if (bMax - bMin >= max - min) return (min + max) * .5f - (bMin + bMax) * .5f;
            return bMin < min ? min - bMin : bMax > max ? max - bMax : 0f;
        }

        /// <summary>Rung inner end and horizon half-gap: at least the configured gap and 1 degree clear of the waterline wing tips (M8).</summary>
        public static float RungInnerDegrees(float gapDegrees, float waterlineHalfWidthDegrees) =>
            Mathf.Max(Finite(gapDegrees) ? gapDegrees : 0f, Finite(waterlineHalfWidthDegrees) ? waterlineHalfWidthDegrees + 1f : 0f);

        /// <summary>
        /// Removes [<paramref name="cutStart"/>, <paramref name="cutEnd"/>] from the sorted, disjoint pieces [starts[i], ends[i]] in place and
        /// returns the new count. When the arrays are full a split keeps only its first part, so geometry is dropped, never overlapped.
        /// </summary>
        public static int SubtractInterval(float[] starts, float[] ends, int count, float cutStart, float cutEnd)
        {
            if (starts == null || ends == null || !(cutEnd > cutStart)) return count;
            count = Mathf.Min(count, Mathf.Min(starts.Length, ends.Length));
            for (int i = count - 1; i >= 0; i--)
            {
                float s = starts[i], e = ends[i];
                if (cutEnd <= s || cutStart >= e) continue;
                bool left = cutStart > s, right = cutEnd < e;
                if (left && right)
                {
                    ends[i] = cutStart;
                    if (count >= starts.Length || count >= ends.Length) continue;
                    for (int j = count; j > i + 1; j--) { starts[j] = starts[j - 1]; ends[j] = ends[j - 1]; }
                    starts[i + 1] = cutEnd; ends[i + 1] = e; count++;
                }
                else if (left) ends[i] = cutStart;
                else if (right) starts[i] = cutEnd;
                else { for (int j = i; j < count - 1; j++) { starts[j] = starts[j + 1]; ends[j] = ends[j + 1]; } count--; }
            }
            return count;
        }

        /// <summary>
        /// Layout smoothing without jitter: an edge that must move inward (a new protected element) snaps at once so nothing is ever
        /// crossed; an edge that may move outward eases with time constant <paramref name="tau"/>. Changes below the deadband are ignored.
        /// </summary>
        public static Rect SmoothWindow(Rect current, Rect target, float deltaTime, float tau = .08f, float deadbandPixels = 1f)
        {
            if (!FiniteRect(current) || current.width <= 0f || current.height <= 0f) return target;
            float k = tau <= 0f || !Finite(deltaTime) ? 1f : 1f - Mathf.Exp(-Mathf.Max(0f, deltaTime) / tau);
            float xMin = Edge(current.xMin, target.xMin, true, k, deadbandPixels), xMax = Edge(current.xMax, target.xMax, false, k, deadbandPixels);
            float yMin = Edge(current.yMin, target.yMin, true, k, deadbandPixels), yMax = Edge(current.yMax, target.yMax, false, k, deadbandPixels);
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }
        private static float Edge(float current, float target, bool minEdge, float k, float deadband)
        {
            float delta = target - current;
            if (Mathf.Abs(delta) <= deadband) return current;
            bool inward = minEdge ? delta > 0f : delta < 0f;
            return inward ? target : current + delta * k;
        }

        /// <summary>0 at the window edge, 1 once a point is <paramref name="fadeX"/>/<paramref name="fadeY"/> pixels inside.</summary>
        public static float EdgeFade(Vector2 p, Rect r, float fadeX, float fadeY)
        {
            float x = fadeX <= 0f ? 1f : Mathf.Clamp01(Mathf.Min(p.x - r.xMin, r.xMax - p.x) / fadeX);
            float y = fadeY <= 0f ? 1f : Mathf.Clamp01(Mathf.Min(p.y - r.yMin, r.yMax - p.y) / fadeY);
            return Mathf.Min(x, y);
        }

        public static bool Contains(Rect outer, Rect inner) =>
            inner.xMin >= outer.xMin && inner.xMax <= outer.xMax && inner.yMin >= outer.yMin && inner.yMax <= outer.yMax;

        public static bool Overlaps(Rect r, IReadOnlyList<Rect> zones)
        {
            int count = zones != null ? zones.Count : 0;
            for (int i = 0; i < count; i++) if (zones[i].Overlaps(r)) return true;
            return false;
        }

        /// <summary>
        /// Camera-space angle (degrees) of a real visual angle: 1:1 in native XR, otherwise through the desktop eye geometry, the camera's
        /// vertical FOV and pixel height. Desktop and headset therefore present the same apparent size at the eye.
        /// </summary>
        public static float AngularSize(float arcmin, float verticalFovDegrees, float pixelHeight, bool nativeXr)
        {
            if (!Finite(arcmin) || arcmin <= 0f) return 0f;
            if (nativeXr || !Finite(verticalFovDegrees) || verticalFovDegrees <= 1f || verticalFovDegrees >= 179f || !Finite(pixelHeight) || pixelHeight <= 1f)
                return arcmin / 60f;
            float pixels = arcmin * ReferenceUnitsPerArcmin * pixelHeight / ReferenceCanvasHeight;
            float focal = pixelHeight * .5f / Mathf.Tan(verticalFovDegrees * .5f * Mathf.Deg2Rad);
            return Mathf.Atan(pixels / focal) * Mathf.Rad2Deg;
        }

        /// <summary>Wraps a screen angle to (-90, 90] so rotated text is never upside down.</summary>
        public static float UprightDegrees(float degrees)
        {
            if (!Finite(degrees)) return 0f;
            float a = Mathf.Repeat(degrees + 90f, 180f) - 90f;
            return a <= -90f ? a + 180f : a;
        }

        /// <summary>Scene-cue distance in HUD units: feet (50 ft steps) below 0.3 NM, otherwise NM with one decimal.</summary>
        public static string CueDistance(float meters)
        {
            if (!Finite(meters) || meters < 0f) return "--";
            if (meters < .3f * 1852f) return (Mathf.Round(meters * 3.28084f / 50f) * 50f).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " FT";
            return (meters / 1852f).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " NM";
        }
        /// <summary>Change key for <see cref="CueDistance"/>, so labels are rebuilt only when the displayed value changes.</summary>
        public static int CueDistanceBucket(float meters) =>
            !Finite(meters) || meters < 0f ? int.MinValue : meters < .3f * 1852f ? Mathf.RoundToInt(meters * 3.28084f / 50f) : 1000000 + Mathf.RoundToInt(meters / 185.2f);
    }
}
