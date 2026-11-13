using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    /// <summary>
    /// Conformal scene-cue human-factors rules: waterline sign (CONF-01), screen-fixed attitude window (CONF-02, CC-10, CL-10),
    /// symmetric horizon gap (CONF-04), screen-angle rungs (CONF-09), upright numerals (CONF-10), unusual-attitude hysteresis (CONF-07),
    /// approach-only FPA reference (CONF-08), visual-angle sizing (CONF-06) and ft/NM scene distances (CONF-14).
    /// </summary>
    public class FaaConformalWindowTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type MathType => Type.GetType("FAA.Customization.FaaRotorcraftCueMath, Assembly-CSharp", true);
        private static object Call(string method, params object[] args)
        {
            foreach (var m in MathType.GetMethods(Any))
                if (m.Name == method && m.GetParameters().Length == args.Length && Matches(m.GetParameters(), args)) return m.Invoke(null, args);
            throw new MissingMethodException(MathType.Name, method);
        }
        private static bool Matches(ParameterInfo[] parameters, object[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                Type p = parameters[i].ParameterType.IsByRef ? parameters[i].ParameterType.GetElementType() : parameters[i].ParameterType;
                if (args[i] != null && !p.IsInstanceOfType(args[i])) return false;
            }
            return true;
        }
        private static float Elevation(Vector3 v) => Mathf.Asin(Mathf.Clamp(v.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        private static float Bearing(Vector3 v) => Mathf.Repeat(Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg, 360f);

        [TestCase(10f, 90f)]
        [TestCase(-7f, 0f)]
        [TestCase(25f, 215f)]
        public void BodyRotation_PositivePitchIsNoseUp_NotTheMirroredTransformSign(float pitch, float heading)
        {
            var body = (Quaternion)Call("BodyRotation", pitch, 0f, heading);
            Vector3 forward = body * Vector3.forward;
            Assert.That(Elevation(forward), Is.EqualTo(pitch).Within(.001f));
            Assert.That(Mathf.DeltaAngle(Bearing(forward), heading), Is.EqualTo(0f).Within(.001f));
        }

        [Test]
        public void BodyRotation_RightBankLowersTheRightWing()
        {
            var body = (Quaternion)Call("BodyRotation", 0f, 30f, 0f);
            Assert.That((body * Vector3.right).y, Is.LessThan(-.49f));
            Assert.That((body * Vector3.up).x, Is.GreaterThan(.49f), "Right bank tilts the lift vector right.");
        }

        [TestCase(5f)]
        [TestCase(12f)]
        [TestCase(-8f)]
        public void Waterline_PositivePitchProjectsAboveTheHorizon(float pitch)
        {
            var go = new GameObject("Waterline sign camera", typeof(Camera));
            try
            {
                var camera = go.GetComponent<Camera>();
                camera.aspect = 16f / 9f; camera.fieldOfView = 60f;
                var body = (Quaternion)Call("BodyRotation", pitch, 0f, 0f);
                object[] waterline = { camera, body * Vector3.forward, null };
                object[] horizon = { camera, (Vector3)Call("EarthRay", 0f, 0f), null };
                Assert.That((bool)Call("TryProjectDirection", waterline), Is.True);
                Assert.That((bool)Call("TryProjectDirection", horizon), Is.True);
                float dy = ((Vector2)waterline[2]).y - ((Vector2)horizon[2]).y;
                Assert.That(Mathf.Sign(dy), Is.EqualTo(Mathf.Sign(pitch)), "Nose-up must put the waterline ABOVE the horizon (CONF-01).");
                float expected = Mathf.Tan(pitch * Mathf.Deg2Rad) / Mathf.Tan(30f * Mathf.Deg2Rad) * .5f;
                Assert.That(dy, Is.EqualTo(expected).Within(.0005f));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(0f)]
        [TestCase(45f)]
        [TestCase(75f)]
        public void RungPoint_KeepsAConstantAngularOffsetAtEveryElevation(float elevation)
        {
            var centre = (Vector3)Call("EarthRay", elevation, 30f);
            var outer = (Vector3)Call("RungPoint", elevation, 30f, 6f);
            Assert.That(Vector3.Angle(centre, outer), Is.EqualTo(6f).Within(.01f));
            Assert.That(outer.magnitude, Is.EqualTo(1f).Within(.0001f));
        }

        [Test]
        public void RungPoint_IsHorizontalOnScreenWhenLookingAtTheRung()
        {
            var go = new GameObject("Rung camera", typeof(Camera));
            try
            {
                var camera = go.GetComponent<Camera>();
                camera.aspect = 16f / 9f;
                camera.transform.rotation = Quaternion.Euler(-40f, 0f, 0f); // Looking 40 degrees up.
                object[] left = { camera, (Vector3)Call("RungPoint", 40f, 0f, -6f), null };
                object[] right = { camera, (Vector3)Call("RungPoint", 40f, 0f, 6f), null };
                Assert.That((bool)Call("TryProjectDirection", left) && (bool)Call("TryProjectDirection", right), Is.True);
                Assert.That(((Vector2)left[2]).y, Is.EqualTo(((Vector2)right[2]).y).Within(.0001f));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(-2.7f, true)]
        [TestCase(2.7f, true)]
        [TestCase(-2.9f, false)]
        [TestCase(2.9f, false)]
        [TestCase(357.5f, true)]
        public void HorizonGap_IsSymmetricAboutTheAircraftReference(float bearing, bool expected) =>
            Assert.That((bool)Call("InHorizonGap", bearing, 2.8f), Is.EqualTo(expected));

        [TestCase(31f, 0f, false, true)]
        [TestCase(29f, 0f, false, false)]
        [TestCase(26f, 0f, true, true)]
        [TestCase(24f, 0f, true, false)]
        [TestCase(0f, 61f, false, true)]
        [TestCase(0f, -61f, false, true)]
        [TestCase(0f, 58f, true, true)]
        [TestCase(0f, 54f, true, false)]
        [TestCase(-21f, 0f, false, true)]
        [TestCase(-19f, 0f, false, false)]
        [TestCase(-16f, 0f, true, true)]
        [TestCase(-14f, 0f, true, false)]
        [TestCase(0f, 300f, false, false)] // 300 deg is a 60 deg left bank: not beyond the threshold.
        [TestCase(float.NaN, 0f, true, false)]
        [TestCase(0f, float.PositiveInfinity, true, false)]
        public void UnusualAttitude_EngagesAndReleasesWithHysteresis(float pitch, float roll, bool was, bool expected) =>
            Assert.That((bool)Call("UnusualAttitude", pitch, roll, was), Is.EqualTo(expected));

        [TestCase(true, false, false, true, 1200f, false, true)]
        [TestCase(true, false, false, true, 8000f, false, false)]
        [TestCase(true, false, false, true, 8000f, true, true)]
        [TestCase(true, true, false, true, 1200f, false, false)]
        [TestCase(true, false, true, true, 1200f, true, false)]
        [TestCase(true, false, false, false, 1200f, false, false)]
        [TestCase(false, false, false, true, 1200f, true, false)]
        [TestCase(true, false, false, true, float.NaN, false, false)]
        public void SelectedFpaReference_OnlyInAnApproachContext(bool enabled, bool hover, bool unusual, bool aglValid, float agl, bool gs, bool expected) =>
            Assert.That((bool)Call("ShowFpaReference", enabled, hover, unusual, aglValid, agl, gs, 1500f), Is.EqualTo(expected));

        private static Rect Window(Rect start, List<Rect> keepOuts, float pad = 24f) =>
            (Rect)Call("AttitudeWindow", start, keepOuts, pad, new Vector2(384f, 345f), new Rect(1, 2, 3, 4));
        private static readonly Rect Start4K = Rect.MinMaxRect(1421f, 799f, 2419f, 1728f);

        [Test]
        public void AttitudeWindow_ClearsSideColumnsHeadingScaleFmaAndGlideslope()
        {
            var keepOuts = new List<Rect>
            {
                Rect.MinMaxRect(1131, 942, 1440, 1294),  // torque intrudes on the left
                Rect.MinMaxRect(1173, 1347, 1361, 1461), // airspeed (clear)
                Rect.MinMaxRect(2300, 1000, 2380, 1500), // glideslope scale inside the right edge
                Rect.MinMaxRect(2435, 1340, 2711, 1468), // altitude (clear)
                Rect.MinMaxRect(1544, 646, 2279, 830),   // heading scale intrudes from below
                Rect.MinMaxRect(1641, 1700, 2199, 2060), // FMA row intrudes from above
            };
            Rect w = Window(Start4K, keepOuts);
            Assert.That(w.xMin, Is.EqualTo(1464f).Within(.01f));
            Assert.That(w.xMax, Is.EqualTo(2276f).Within(.01f));
            Assert.That(w.yMin, Is.EqualTo(854f).Within(.01f));
            Assert.That(w.yMax, Is.EqualTo(1676f).Within(.01f));
            foreach (var r in keepOuts) Assert.That(r.Overlaps(w), Is.False, "No protected element may overlap the attitude window: " + r);
        }

        [Test]
        public void AttitudeWindow_QuadrantElementMovesTheCheaperEdge_AndIgnoresAnElementOverItsCentre()
        {
            // A dial in the lower-left corner: shrinking the bottom costs less area than shrinking the left side.
            var corner = new List<Rect> { Rect.MinMaxRect(1000, 600, 1900, 860) };
            Rect w = Window(Start4K, corner, 0f);
            Assert.That(w.yMin, Is.EqualTo(860f).Within(.01f));
            Assert.That(w.xMin, Is.EqualTo(Start4K.xMin).Within(.01f));
            var covering = new List<Rect> { Rect.MinMaxRect(1800, 1200, 2000, 1300) };
            Assert.That(Window(Start4K, covering, 0f), Is.EqualTo(Start4K), "Attitude keeps priority over an element drawn across its own centre.");
        }

        [Test]
        public void AttitudeWindow_FallsBackWhenSqueezedBelowTheMinimumSize()
        {
            var squeeze = new List<Rect> { Rect.MinMaxRect(1000, 800, 1900, 1800), Rect.MinMaxRect(1940, 800, 3000, 1800) };
            Assert.That(Window(Start4K, squeeze, 0f), Is.EqualTo(new Rect(1, 2, 3, 4)));
        }

        [Test]
        public void SmoothWindow_SnapsInwardEasesOutwardAndIgnoresSubPixelNoise()
        {
            Rect current = Rect.MinMaxRect(100, 100, 900, 700);
            Rect inward = (Rect)Call("SmoothWindow", current, Rect.MinMaxRect(150, 100, 900, 700), .016f, .08f, 1f);
            Assert.That(inward.xMin, Is.EqualTo(150f), "A new protected element is cleared immediately.");
            Rect outward = (Rect)Call("SmoothWindow", current, Rect.MinMaxRect(50, 100, 900, 700), .016f, .08f, 1f);
            Assert.That(outward.xMin, Is.LessThan(100f).And.GreaterThan(50f), "Released space is regained smoothly, never by a jump.");
            Rect noise = (Rect)Call("SmoothWindow", current, Rect.MinMaxRect(100.6f, 99.5f, 900.4f, 700.7f), .016f, .08f, 1f);
            Assert.That(noise, Is.EqualTo(current));
        }

        [Test]
        public void EdgeFade_IsZeroAtTheEdgeAndFullInside()
        {
            var r = new Rect(0, 0, 1000, 800);
            Assert.That((float)Call("EdgeFade", new Vector2(0, 400), r, 60f, 48f), Is.EqualTo(0f));
            Assert.That((float)Call("EdgeFade", new Vector2(30, 400), r, 60f, 48f), Is.EqualTo(.5f).Within(.0001f));
            Assert.That((float)Call("EdgeFade", new Vector2(500, 400), r, 60f, 48f), Is.EqualTo(1f));
        }

        [Test]
        public void AngularSize_MatchesTheEyeOnDesktopAndIsOneToOneInXr()
        {
            Assert.That((float)Call("AngularSize", 28f, 60f, 2160f, true), Is.EqualTo(28f / 60f).Within(.0001f));
            float desktop = (float)Call("AngularSize", 28f, 60f, 2160f, false);
            // 28 arcmin at 700 mm is 18.3 reference units: 36.6 px of 2160 at a 1870 px focal length.
            Assert.That(desktop, Is.EqualTo(Mathf.Atan(28f * .654f * 2f / 1870.6f) * Mathf.Rad2Deg).Within(.002f));
            Assert.That((float)Call("AngularSize", 28f, 60f, 1440f, false), Is.EqualTo(desktop).Within(.0001f), "Pixel height cancels with the scaled canvas.");
            float line = (float)Call("AngularSize", 3.4377f, 60f, 2160f, false);
            Assert.That(Mathf.Tan(line * Mathf.Deg2Rad) * 1870.6f, Is.EqualTo(4.5f).Within(.1f), "1 mrad stroke is about 4.5 px at 4K.");
        }

        [TestCase(0f, 0f)]
        [TestCase(45f, 45f)]
        [TestCase(120f, -60f)]
        [TestCase(-100f, 80f)]
        [TestCase(-90f, 90f)]
        [TestCase(270f, 90f)]
        public void RungNumerals_StayUprightRelativeToTheRung(float angle, float expected) =>
            Assert.That((float)Call("UprightDegrees", angle), Is.EqualTo(expected).Within(.0001f));

        [TestCase(150f, "500 FT")]
        [TestCase(10f, "50 FT")]
        [TestCase(3704f, "2.0 NM")]
        [TestCase(float.NaN, "--")]
        public void SceneCueDistance_UsesFeetAndNauticalMiles(float meters, string expected) =>
            Assert.That((string)Call("CueDistance", meters), Is.EqualTo(expected));

        [Test]
        public void SceneCueDistanceBucket_ChangesOnlyWhenTheDisplayedValueChanges()
        {
            Assert.That((int)Call("CueDistanceBucket", 150f), Is.EqualTo((int)Call("CueDistanceBucket", 152f)));
            Assert.That((int)Call("CueDistanceBucket", 150f), Is.Not.EqualTo((int)Call("CueDistanceBucket", 170f)));
            Assert.That((int)Call("CueDistanceBucket", 3704f), Is.EqualTo((int)Call("CueDistanceBucket", 3710f)));
        }

        [Test]
        public void UnprojectInvertsProjection_IncludingAsymmetricXrFrusta()
        {
            var symmetric = Matrix4x4.Perspective(60f, 16f / 9f, .1f, 1000f);
            var asymmetric = Matrix4x4.Frustum(-.07f, .05f, -.05f, .045f, .1f, 1000f);
            foreach (var matrix in new[] { symmetric, asymmetric })
                foreach (var viewport in new[] { new Vector2(.5f, .5f), new Vector2(.1f, .9f), new Vector2(.83f, .2f) })
                {
                    object[] back = { matrix, viewport, null };
                    Assert.That((bool)Call("TryUnprojectLocal", back), Is.True);
                    object[] again = { matrix, (Vector3)back[2], null };
                    Assert.That((bool)Call("TryProjectLocal", again), Is.True);
                    Assert.That(Vector2.Distance((Vector2)again[2], viewport), Is.LessThan(.00001f));
                }
        }

        // ---- Review fix wave: window around the waterline (C3), occlusion (M8), pairs (M7), slivers (m3) ----

        [Test]
        public void GrowToward_ExtendsOnlyTowardTheContent_NeverShrinksAndStaysInBounds()
        {
            Rect window = Rect.MinMaxRect(100, 100, 200, 200), bounds = Rect.MinMaxRect(0, 0, 300, 190);
            Rect below = (Rect)Call("GrowToward", window, Rect.MinMaxRect(120, 40, 180, 90), bounds);
            Assert.That(below, Is.EqualTo(Rect.MinMaxRect(100, 40, 200, 200)), "Grows down to the waterline; the window's own top is kept although it exceeds the bounds.");
            Rect far = (Rect)Call("GrowToward", window, Rect.MinMaxRect(-500, 150, -400, 160), bounds);
            Assert.That(far.xMin, Is.EqualTo(0f), "Never beyond the field bounds.");
            Assert.That((Rect)Call("GrowToward", window, Rect.MinMaxRect(120, 120, 180, 180), bounds), Is.EqualTo(window), "Content inside: unchanged.");
        }

        [Test]
        public void AttitudeWindow_GrownTowardTheWaterline_KeepsTheFieldSideOfEveryProtectedElement()
        {
            Rect grown = Rect.MinMaxRect(100, 50, 500, 700);   // grown from (100,300)-(500,700) toward a low waterline
            var reference = new Vector2(300, 500);            // attitude-field centre
            var heading = new List<Rect> { Rect.MinMaxRect(150, 380, 450, 420) };
            Rect w = (Rect)Call("AttitudeWindow", grown, heading, 0f, new Vector2(50, 50), new Rect(1, 2, 3, 4), reference);
            Assert.That(w.yMin, Is.EqualTo(420f).Within(.01f), "An element below the field centre pushes the bottom edge up, never the top edge down.");
            Assert.That(w.yMax, Is.EqualTo(700f).Within(.01f));
            Rect byOwnCentre = (Rect)Call("AttitudeWindow", grown, heading, 0f, new Vector2(50, 50), new Rect(1, 2, 3, 4));
            Assert.That(byOwnCentre.yMax, Is.EqualTo(380f).Within(.01f), "Without the field reference the grown window would jump below the element.");
        }

        [Test]
        public void ClampInside_PinsASymbolJustInsideTheNearestEdge()
        {
            Rect window = Rect.MinMaxRect(0, 0, 100, 100);
            Assert.That((Vector2)Call("ClampInside", window, Rect.MinMaxRect(40, -30, 60, -20)), Is.EqualTo(new Vector2(0, 30)));
            Assert.That((Vector2)Call("ClampInside", window, Rect.MinMaxRect(110, 40, 130, 50)), Is.EqualTo(new Vector2(-30, 0)));
            Assert.That((Vector2)Call("ClampInside", window, Rect.MinMaxRect(10, 10, 20, 20)), Is.EqualTo(Vector2.zero));
            Assert.That((Vector2)Call("ClampInside", window, Rect.MinMaxRect(-50, 40, 250, 50)), Is.EqualTo(new Vector2(-50, 0)), "Wider than the window: centred.");
        }

        [TestCase(2.8f, 2.47f, 3.47f)]
        [TestCase(2.8f, 1.5f, 2.8f)]
        [TestCase(2.8f, float.NaN, 2.8f)]
        public void RungInner_StaysOneDegreeClearOfTheWaterlineTips(float gap, float halfWidth, float expected) =>
            Assert.That((float)Call("RungInnerDegrees", gap, halfWidth), Is.EqualTo(expected).Within(.0001f));

        [Test]
        public void SubtractInterval_SplitsTrimsAndRemovesPieces()
        {
            float[] s = new float[4], e = new float[4];
            s[0] = 0f; e[0] = 1f;
            int n = (int)Call("SubtractInterval", s, e, 1, .3f, .5f);
            Assert.That(n, Is.EqualTo(2)); Assert.That(new[] { s[0], e[0], s[1], e[1] }, Is.EqualTo(new[] { 0f, .3f, .5f, 1f }));
            n = (int)Call("SubtractInterval", s, e, n, .9f, 1.2f);
            Assert.That(e[1], Is.EqualTo(.9f));
            n = (int)Call("SubtractInterval", s, e, n, -1f, .1f);
            Assert.That(s[0], Is.EqualTo(.1f));
            n = (int)Call("SubtractInterval", s, e, n, .2f, .95f);
            Assert.That(n, Is.EqualTo(1)); Assert.That(new[] { s[0], e[0] }, Is.EqualTo(new[] { .1f, .2f }));
            float[] fs = { 0f }, fe = { 1f };
            Assert.That((int)Call("SubtractInterval", fs, fe, 1, .4f, .6f), Is.EqualTo(1), "A full buffer keeps only the first part of a split.");
            Assert.That(fe[0], Is.EqualTo(.4f));
        }

        // ---- Layer integration (EditMode, isolated fixture; never touches the live bridge) ----

        [Test]
        public void Layer_DrawsWaterlineFromDataConfinesNumeralsAndFlagsStaleAttitude()
        {
            var shader = Resources.Load<Shader>("Shaders/FaaRotorcraftConformal");
            if (shader == null || !shader.isSupported) Assert.Ignore("Conformal line shader unavailable in this editor (no graphics device).");
            Type layerType = Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp", true);
            Type bridgeType = Type.GetType("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge, Assembly-CSharp", true);
            Type dataType = Type.GetType("AviationUI.AviationFlightData, Assembly-CSharp", true);
            var owner = new GameObject("Conformal window fixture");
            var bridgeObject = new GameObject("Inactive fixture bridge"); bridgeObject.SetActive(false);
            var created = new List<UnityEngine.Object>();
            try
            {
                var cameraObject = new GameObject("Fixture camera", typeof(Camera));
                cameraObject.transform.SetParent(owner.transform, false);
                var camera = cameraObject.GetComponent<Camera>();
                camera.enabled = false; camera.pixelRect = new Rect(0, 0, 1920, 1080); camera.aspect = 16f / 9f; camera.fieldOfView = 60f; camera.farClipPlane = 9000f;
                var air = new GameObject("Fixture aircraft"); air.transform.SetParent(owner.transform, false);
                var canvasObject = new GameObject("Fixture canvas", typeof(RectTransform), typeof(Canvas));
                canvasObject.transform.SetParent(owner.transform, false);
                var bridge = bridgeObject.AddComponent(bridgeType);
                var data = Activator.CreateInstance(dataType);
                void SetData(string field, object value) => dataType.GetField(field, Any).SetValue(data, value);
                SetData("attitudeValid", true); SetData("groundVelocityValid", true); SetData("altitudeAGLValid", true);
                SetData("pitch", 10f); SetData("roll", 0f); SetData("heading", 0f); SetData("track", 0f);
                SetData("groundSpeed", 80f); SetData("verticalSpeed", 0f); SetData("altitudeAGL", 8000f);
                bridgeType.GetField("_latestFlightData", Any).SetValue(bridge, data);
                bridgeType.GetProperty("IsFeedHealthy", Any).SetValue(bridge, true);
                bridgeType.GetProperty("LastPacketAgeSeconds", Any).SetValue(bridge, .05f);
                var layer = owner.AddComponent(layerType);
                layerType.GetField("flightData", Any).SetValue(layer, bridge);
                layerType.GetMethod("Bind").Invoke(layer, new object[] { camera, air.transform, canvasObject.GetComponent<Canvas>() });
                MethodInfo refresh = layerType.GetMethod("RefreshPresentation");
                T Get<T>(string property) => (T)layerType.GetProperty(property, Any).GetValue(layer);
                refresh.Invoke(layer, null);
                foreach (var field in new[] { "mesh", "material", "labelMaterial" })
                    if (layerType.GetField(field, Any).GetValue(layer) is UnityEngine.Object o && o != null) created.Add(o);

                Assert.That(Get<bool>("HasLiveAttitude"), Is.True);
                Vector3 waterline = Get<Vector3>("LastWaterlineDirection");
                Assert.That(Elevation(waterline), Is.EqualTo(10f).Within(.01f), "Waterline from validated pitch, not the OwnAircraft transform.");
                Assert.That(camera.WorldToViewportPoint(camera.transform.position + waterline * 1000f).y, Is.GreaterThan(.5f));
                Assert.That(Get<int>("LastRungCount"), Is.InRange(3, 10), "Only rungs that can reach the attitude window are generated.");
                Assert.That(Get<bool>("FpaReferenceVisible"), Is.False, "No selected-FPA reference in cruise (HAGL 8000 ft).");
                Assert.That(Get<bool>("UnusualAttitude"), Is.False);
                Rect window = Get<Rect>("AttitudeWindowPixels");
                int numerals = 0;
                foreach (var text in owner.GetComponentsInChildren<TMPro.TMP_Text>())
                {
                    if (!text.name.StartsWith("FAA Cue Label") || !int.TryParse(text.text, out _)) continue;
                    numerals++;
                    Vector3 screen = camera.WorldToScreenPoint(text.transform.position);
                    Assert.That(window.Contains(new Vector2(screen.x, screen.y)), Is.True, "Pitch numeral " + text.text + " outside the attitude window.");
                }
                Assert.That(numerals, Is.GreaterThan(0));
                int before = Get<int>("LastVertexCount");
                refresh.Invoke(layer, null);
                Assert.That(Get<int>("LastVertexCount"), Is.EqualTo(before), "Unchanged inputs keep the same mesh.");

                SetData("altitudeAGL", 900f); refresh.Invoke(layer, null);
                Assert.That(Get<bool>("FpaReferenceVisible"), Is.True, "Approach context shows the selected-FPA reference.");

                SetData("pitch", 35f); refresh.Invoke(layer, null);
                Assert.That(Get<bool>("UnusualAttitude"), Is.True);
                Assert.That((bool)layerType.GetProperty("UnusualAttitudeActive", Any).GetValue(null), Is.True);
                Assert.That(Get<bool>("FpaReferenceVisible"), Is.False, "Unusual attitude removes the FPA reference.");
                SetData("pitch", 27f); refresh.Invoke(layer, null);
                Assert.That(Get<bool>("UnusualAttitude"), Is.True, "Hysteresis holds the declutter until pitch < +25.");
                SetData("pitch", 5f); refresh.Invoke(layer, null);
                Assert.That(Get<bool>("UnusualAttitude"), Is.False);

                bridgeType.GetProperty("LastPacketAgeSeconds", Any).SetValue(bridge, 5f); refresh.Invoke(layer, null);
                Assert.That(Get<bool>("HasLiveAttitude"), Is.False);
                Assert.That(Get<bool>("AttitudeFlagVisible"), Is.True, "Stale attitude shows the boxed ATT flag.");
                Assert.That(Get<int>("LastRungCount"), Is.EqualTo(0), "No ladder on invalid attitude.");
                Assert.That(Get<bool>("UnusualAttitude"), Is.False);
            }
            finally
            {
                foreach (var o in created) if (o != null) UnityEngine.Object.DestroyImmediate(o);
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(bridgeObject);
            }
        }

        [Test]
        public void ConformalController_RestoresTheHeadingRootOnlyAfterProjectingIt()
        {
            Type controllerType = Type.GetType("FAA.Customization.FaaConformalHudController, Assembly-CSharp", true);
            var host = new GameObject("Heading restore fixture");
            var headingObject = new GameObject("FAA Heading Tape Overlay", typeof(RectTransform));
            try
            {
                var heading = headingObject.GetComponent<RectTransform>();
                var controller = host.AddComponent(controllerType);
                void Set(string field, object value) => controllerType.GetField(field, Any).SetValue(controller, value);
                Set("_headingHudRoot", heading);
                Set("_headingHeadFixedAnchoredPosition", new Vector2(0f, -180f));
                Set("_headingHeadFixedLocalRotation", Quaternion.identity);
                Set("_headingHeadFixedRootCaptured", true);
                heading.anchoredPosition = new Vector2(0f, -250f); // Re-laned by the heading tape owner.
                MethodInfo restore = controllerType.GetMethod("RestoreHeadFixedRoot", Any);
                restore.Invoke(controller, null);
                Assert.That(heading.anchoredPosition, Is.EqualTo(new Vector2(0f, -250f)), "An unprojected heading root is never pinned back.");
                Set("_headingProjected", true);
                restore.Invoke(controller, null);
                Assert.That(heading.anchoredPosition, Is.EqualTo(new Vector2(0f, -180f)), "A projected heading root returns to its head-fixed lane.");
                Assert.That((bool)controllerType.GetField("_headingProjected", Any).GetValue(controller), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(headingObject);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        /// <summary>Isolated layer fixture: 1920x1080 camera at 60 deg FOV, an inactive bridge with injected data, no live HUD.</summary>
        private sealed class LayerFixture : IDisposable
        {
            public readonly Type LayerType = Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp", true);
            private readonly Type bridgeType = Type.GetType("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge, Assembly-CSharp", true);
            private readonly Type dataType = Type.GetType("AviationUI.AviationFlightData, Assembly-CSharp", true);
            private readonly GameObject owner = new GameObject("Conformal fix-wave fixture"), bridgeObject = new GameObject("Inactive fixture bridge");
            private readonly object data;
            public readonly Camera Camera;
            public readonly Component Layer;

            public LayerFixture(float pitch)
            {
                bridgeObject.SetActive(false);
                var cameraObject = new GameObject("Fixture camera", typeof(Camera));
                cameraObject.transform.SetParent(owner.transform, false);
                Camera = cameraObject.GetComponent<Camera>();
                Camera.enabled = false; Camera.pixelRect = new Rect(0, 0, 1920, 1080); Camera.aspect = 16f / 9f; Camera.fieldOfView = 60f; Camera.farClipPlane = 9000f;
                var air = new GameObject("Fixture aircraft"); air.transform.SetParent(owner.transform, false);
                var canvasObject = new GameObject("Fixture canvas", typeof(RectTransform), typeof(Canvas));
                canvasObject.transform.SetParent(owner.transform, false);
                var bridge = bridgeObject.AddComponent(bridgeType);
                data = Activator.CreateInstance(dataType);
                Set("attitudeValid", true); Set("groundVelocityValid", true); Set("altitudeAGLValid", true);
                Set("pitch", pitch); Set("roll", 0f); Set("heading", 0f); Set("track", 0f);
                Set("groundSpeed", 80f); Set("verticalSpeed", 0f); Set("altitudeAGL", 8000f);
                bridgeType.GetField("_latestFlightData", Any).SetValue(bridge, data);
                bridgeType.GetProperty("IsFeedHealthy", Any).SetValue(bridge, true);
                bridgeType.GetProperty("LastPacketAgeSeconds", Any).SetValue(bridge, .05f);
                Layer = owner.AddComponent(LayerType);
                LayerType.GetField("flightData", Any).SetValue(Layer, bridge);
                LayerType.GetMethod("Bind").Invoke(Layer, new object[] { Camera, air.transform, canvasObject.GetComponent<Canvas>() });
            }
            public void Set(string field, object value) => dataType.GetField(field, Any).SetValue(data, value);
            public void Refresh() => LayerType.GetMethod("RefreshPresentation").Invoke(Layer, null);
            public T Get<T>(string member) => LayerType.GetProperty(member, Any) is PropertyInfo p ? (T)p.GetValue(Layer) : (T)LayerType.GetField(member, Any).GetValue(Layer);
            public List<string> Numerals()
            {
                var list = new List<string>();
                foreach (var text in owner.GetComponentsInChildren<TMPro.TMP_Text>())
                    if (text.name.StartsWith("FAA Cue Label") && int.TryParse(text.text, out _)) list.Add(text.text);
                return list;
            }
            public void Dispose()
            {
                foreach (var field in new[] { "mesh", "material", "labelMaterial" })
                    if (LayerType.GetField(field, Any).GetValue(Layer) is UnityEngine.Object o && o != null) UnityEngine.Object.DestroyImmediate(o);
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(bridgeObject);
            }
        }

        private static void RequireShader()
        {
            var shader = Resources.Load<Shader>("Shaders/FaaRotorcraftConformal");
            if (shader == null || !shader.isSupported) Assert.Ignore("Conformal line shader unavailable in this editor (no graphics device).");
        }

        [Test]
        public void Layer_LowWaterlineGrowsTheWindowInsteadOfBeingDropped()
        {
            RequireShader();
            using var f = new LayerFixture(-14f);
            f.Refresh();
            Rect window = f.Get<Rect>("AttitudeWindowPixels");
            Vector2 w = f.Camera.WorldToScreenPoint(f.Camera.transform.position + f.Get<Vector3>("LastWaterlineDirection") * 1000f);
            var viewport = (Rect)Type.GetType("FAA.Customization.FaaHudKeepOut, Assembly-CSharp", true).GetField("DefaultAttitudeViewport").GetValue(null);
            Assert.That(w.y, Is.LessThan(1080f * viewport.yMin), "Precondition: the waterline is below the default window.");
            Assert.That(f.Get<bool>("WaterlineVisible"), Is.True, "The aircraft reference is never silently removed (C3).");
            Assert.That(f.Get<bool>("WaterlineLimited"), Is.False, "The window is built around the waterline, so it is drawn at its true place.");
            Assert.That(window.Contains(w), Is.True);
            Assert.That(window.yMax, Is.LessThanOrEqualTo(1080f * viewport.yMax + .01f), "The window never grows above its top (roll-scale ends).");
        }

        [Test]
        public void Layer_WaterlineOutsideTheField_IsPinnedGhostedAndAnnunciated()
        {
            RequireShader();
            using var f = new LayerFixture(0f);
            f.Camera.transform.rotation = Quaternion.Euler(0f, 40f, 0f); // looking 40 deg right of the boresight
            f.Refresh();
            Assert.That(f.Get<bool>("WaterlineVisible"), Is.True);
            Assert.That(f.Get<bool>("WaterlineLimited"), Is.True, "Outside the attitude field the waterline is pinned at the edge, dashed and ghosted.");
            Assert.That(f.Get<string>("DataStatus"), Does.Contain("W LIMIT"));
            Assert.That(f.Get<float>("ViewHeadingOffsetDegrees"), Is.EqualTo(40f).Within(.01f), "Camera yaw vs validated heading is reported (C3 investigation).");
            f.Camera.transform.rotation = Quaternion.identity; f.Refresh();
            Assert.That(f.Get<bool>("WaterlineLimited"), Is.False);
            Assert.That(f.Get<string>("DataStatus"), Does.Not.Contain("W LIMIT"));
        }

        [TestCase(0f, 0f)]
        [TestCase(6f, 30f)]
        [TestCase(-4f, -45f)]
        public void Layer_RungsAreDrawnOnlyAsLabelledPairs(float pitch, float roll)
        {
            RequireShader();
            using var f = new LayerFixture(pitch);
            f.Set("roll", roll);
            f.Camera.transform.rotation = Quaternion.Euler(-pitch, 0f, -roll); // cockpit view: the ladder rotates, the waterline is centred
            f.Refresh();
            var numerals = f.Numerals();
            int rungs = f.Get<int>("LastRungCount");
            Assert.That(rungs, Is.GreaterThan(0));
            Assert.That(numerals.Count, Is.EqualTo(2 * rungs), "Every drawn rung carries both numerals (M7).");
            var counts = new Dictionary<string, int>();
            foreach (var n in numerals) counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
            foreach (var pair in counts) Assert.That(pair.Value, Is.EqualTo(2), "Rung " + pair.Key + " is one-sided.");
        }

        [Test]
        public void Layer_OccludesAroundTheWaterlineAndFpv()
        {
            RequireShader();
            using var f = new LayerFixture(0f); // level, FPV on the horizon next to the waterline
            f.Refresh();
            var occluders = f.Get<List<Rect>>("occluders");
            Assert.That(occluders.Count, Is.EqualTo(5), "Waterline box plus the FPV ring, wings and fin bands break the lines around them (M8).");
            Assert.That(f.Get<List<Rect>>("symbolCuts").Count, Is.EqualTo(4), "The waterline itself is cut under the FPV symbol.");
            float inner = f.Get<float>("ladderInner"), unit = f.Get<float>("waterlineUnit");
            Assert.That(inner, Is.GreaterThanOrEqualTo(3f * unit + 1f - 1e-4f), "Rung inner ends and the horizon gap clear the W tips by 1 deg.");
        }

        [Test]
        public void Layer_OffScreenHorizonIsClampedToTheWindowEdge()
        {
            RequireShader();
            using var f = new LayerFixture(0f);
            f.Camera.transform.rotation = Quaternion.Euler(-60f, 0f, 0f); // looking 60 deg up: the true horizon is below the view
            f.Refresh();
            Assert.That(f.Get<bool>("HorizonOffScale"), Is.True, "A horizon reference stays visible in every attitude (standards 3.1).");
            Assert.That(f.Get<bool>("WaterlineLimited"), Is.True);
            f.Camera.transform.rotation = Quaternion.identity; f.Refresh();
            Assert.That(f.Get<bool>("HorizonOffScale"), Is.False);
        }

        [Test]
        public void ConformalController_HoldsTheCameraOnTheBoresightAndRestoresTheBlend()
        {
            Type controllerType = Type.GetType("FAA.Customization.FaaConformalHudController, Assembly-CSharp", true);
            Type rigType = Type.GetType("AircraftControl.Camera.AircraftCameraController, AircraftControl", true);
            var host = new GameObject("Blend fixture");
            var first = new GameObject("Blend camera A", typeof(Camera));
            var second = new GameObject("Blend camera B", typeof(Camera));
            try
            {
                var rigA = first.AddComponent(rigType); var rigB = second.AddComponent(rigType);
                PropertyInfo blend = rigType.GetProperty("FlightPathBlend");
                blend.SetValue(rigA, .6f); blend.SetValue(rigB, .4f);
                var controller = host.AddComponent(controllerType);
                MethodInfo hold = controllerType.GetMethod("HoldBoresightView", Any);
                FieldInfo camera = controllerType.GetField("projectionCamera", Any);
                camera.SetValue(controller, first.GetComponent<Camera>());
                hold.Invoke(controller, new object[] { true });
                Assert.That((float)blend.GetValue(rigA), Is.EqualTo(0f), "Conformal layer active: camera on the airframe boresight.");
                hold.Invoke(controller, new object[] { true });
                Assert.That((float)blend.GetValue(rigA), Is.EqualTo(0f));
                camera.SetValue(controller, second.GetComponent<Camera>());
                hold.Invoke(controller, new object[] { true });
                Assert.That((float)blend.GetValue(rigA), Is.EqualTo(.6f).Within(1e-6f), "A replaced camera gets its own value back.");
                Assert.That((float)blend.GetValue(rigB), Is.EqualTo(0f));
                hold.Invoke(controller, new object[] { false });
                Assert.That((float)blend.GetValue(rigB), Is.EqualTo(.4f).Within(1e-6f), "Leaving conformal mode restores the previous blend.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }
    }
}
