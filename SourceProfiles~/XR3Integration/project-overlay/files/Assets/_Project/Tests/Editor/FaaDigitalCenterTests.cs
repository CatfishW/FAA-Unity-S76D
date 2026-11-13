using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Digital centre column: live FMA, bank/slip validity, LOC/G-S scales, heading tape and bridge data hygiene.</summary>
    public class FaaDigitalCenterTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        private static Type Fma => T("FAA.Customization.FaaFma");
        private static Type Annunciator => T("FAA.Customization.FaaFlightModeAnnunciator");
        private static Type Bridge => T("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge");
        private static Type Bank => T("HUDControl.Elements.BankScaleElement");

        private static object NewFma(bool valid, bool engaged, string lateral, string lateralArmed, string vertical, string verticalArmed, string status) =>
            Activator.CreateInstance(Fma, valid, engaged, "", "", lateral, lateralArmed, vertical, verticalArmed, status);

        private static object Call(object target, string method, params object[] args)
        {
            Type type = target as Type ?? target.GetType();
            MethodInfo info = null;
            foreach (MethodInfo candidate in type.GetMethods(Any))
                if (candidate.Name == method && candidate.GetParameters().Length == args.Length) { info = candidate; break; }
            Assert.That(info, Is.Not.Null, method);
            return info.Invoke(target is Type ? null : target, args);
        }

        private static object Get(object target, string property) => target.GetType().GetProperty(property, Any).GetValue(target);
        private static void SetField(object target, string field, object value)
        {
            FieldInfo info = target.GetType().GetField(field, Any);
            Assert.That(info, Is.Not.Null, field);
            info.SetValue(target, value);
        }

        // ---------------- FMA ----------------

        private static Component NewAnnunciator(out GameObject hudRoot)
        {
            hudRoot = new GameObject("Second Interation GUI", typeof(RectTransform));
            hudRoot.transform.localScale = Vector3.one * 540f;
            var fma = (Component)Annunciator.GetMethod("Ensure").Invoke(null, new object[] { hudRoot.transform });
            Assert.That(fma, Is.Not.Null);
            return fma;
        }

        [Test]
        public void Fma_IsADirectChildOfTheDigitalRootWithAKeepOutAndReferencePixelScale()
        {
            Component fma = NewAnnunciator(out GameObject root);
            try
            {
                Assert.That(fma.transform.parent, Is.SameAs(root.transform), "A direct child gets the Digital style gate (hidden in Classic).");
                Assert.That(fma.gameObject.name, Is.EqualTo("FAA Flight Mode Annunciator"));
                Component region = fma.GetComponent(T("FAA.Customization.FaaHudKeepOutRegion"));
                Assert.That(region, Is.Not.Null);
                Assert.That(Get(region, "Kind").ToString(), Is.EqualTo("Symbology"));
                Assert.That(fma.transform.localScale.x * 540f, Is.EqualTo(1f).Within(.001f), "1 local unit = 1 reference pixel.");
                var rect = (RectTransform)fma.transform;
                Assert.That(rect.sizeDelta.x, Is.LessThanOrEqualTo(620f), "Zone Z1 is 620 ref wide.");
                Assert.That((float)Annunciator.GetField("TopFromScreenTop").GetValue(null) + rect.sizeDelta.y, Is.LessThanOrEqualTo(80f));
                Assert.That((float)Annunciator.GetField("TopFromScreenTop").GetValue(null), Is.GreaterThanOrEqualTo(14f));
                Assert.That(Call(Annunciator, "Ensure", root.transform), Is.SameAs(fma), "Ensure is idempotent.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Fma_ShowsLiveColumnsAndKeepsTheCollectiveColumnEmpty()
        {
            Component fma = NewAnnunciator(out GameObject root);
            try
            {
                Call(fma, "Present", NewFma(true, true, "HDG", "", "ALT", "", "CPL"), 0f);
                Assert.That(Call(fma, "ActiveText", 0), Is.EqualTo(""), "No collective source: C stays empty.");
                Assert.That(Call(fma, "ActiveText", 1), Is.EqualTo("HDG"));
                Assert.That(Call(fma, "ActiveText", 2), Is.EqualTo("ALT"));
                Assert.That(Call(fma, "ArmedText", 2), Is.EqualTo(""));
                Assert.That(Call(fma, "ActiveText", 3), Is.EqualTo("CPL"));
                Assert.That((bool)Call(fma, "BoxShown", 1), Is.False, "The first sample initialises the tracker without a change box.");

                var texts = fma.GetComponentsInChildren<TMP_Text>(true);
                foreach (var text in texts)
                {
                    Assert.That(text.fontSize, Is.GreaterThanOrEqualTo(FaaHudStyleMinLabel()), text.name);
                    Assert.That(text.raycastTarget, Is.False);
                }

                TMP_Text active = Array.Find(texts, t => t.name == "R Active");
                TMP_Text armed = Array.Find(texts, t => t.name == "P Armed");
                Assert.That(active.fontSize, Is.GreaterThan(armed.fontSize), "Armed modes are smaller than active modes.");
                Assert.That(active.color.a, Is.GreaterThanOrEqualTo(.85f));
                Assert.That(armed.color.b, Is.GreaterThan(armed.color.r), "Armed modes are cyan.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static float FaaHudStyleMinLabel() => (float)T("FAA.Customization.FaaHudStyle").GetField("MinLabel").GetRawConstantValue();

        [Test]
        public void Fma_BoxesANewlyEngagedModeForTenSeconds()
        {
            Component fma = NewAnnunciator(out GameObject root);
            try
            {
                Call(fma, "Present", NewFma(true, true, "HDG", "", "ALT", "G/S ARM", "CPL"), 0f);
                Assert.That(Call(fma, "ArmedText", 2), Is.EqualTo("G/S ARM"));
                Call(fma, "Present", NewFma(true, true, "HDG", "", "G/S", "", "CPL"), 1f);
                Assert.That((bool)Call(fma, "BoxShown", 2), Is.True, "Armed-to-engaged transition gets a box.");
                Assert.That((bool)Call(fma, "BoxShown", 1), Is.False);
                Call(fma, "Render", 10.5f);
                Assert.That((bool)Call(fma, "BoxShown", 2), Is.True);
                Call(fma, "Render", 11.5f);
                Assert.That((bool)Call(fma, "BoxShown", 2), Is.False, "The mode-change box is removed after 10 s.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Fma_ApOffIsBoxedFlashingThenSteadyThenRemoved()
        {
            Component fma = NewAnnunciator(out GameObject root);
            try
            {
                Call(fma, "Present", NewFma(true, true, "HDG", "", "ALT", "", "CPL"), 0f);
                Call(fma, "Present", NewFma(true, false, "", "", "", "", ""), 1f);
                Assert.That(Call(fma, "ActiveText", 1), Is.EqualTo(""), "No modes while the AP/FD is off.");
                Assert.That(Call(fma, "ActiveText", 3), Is.EqualTo("AP OFF"));
                Assert.That((bool)Call(fma, "BoxShown", 3), Is.True);
                Call(fma, "Render", 1.35f); // dark half of the 2 Hz flash
                Assert.That((float)Get(fma, "StatusAlpha"), Is.Zero);
                Call(fma, "Render", 1.05f);
                Assert.That((float)Get(fma, "StatusAlpha"), Is.EqualTo(1f));
                Call(fma, "Render", 7.35f); // steady after 5 s
                Assert.That((float)Get(fma, "StatusAlpha"), Is.EqualTo(1f));
                Assert.That(Call(fma, "ActiveText", 3), Is.EqualTo("AP OFF"));
                Call(fma, "Render", 11.5f);
                Assert.That(Call(fma, "ActiveText", 3), Is.EqualTo(""), "AP OFF is removed after 10 s.");
                Assert.That((bool)Call(fma, "BoxShown", 3), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Fma_StaleDataIsFlaggedNeverFrozen()
        {
            Component fma = NewAnnunciator(out GameObject root);
            try
            {
                Call(fma, "Present", NewFma(true, true, "NAV", "", "VS", "ALT ARM", "FD"), 0f);
                Call(fma, "Present", NewFma(false, false, "", "", "", "", "FMA --"), 1f);
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(Call(fma, "ActiveText", i), Is.EqualTo(""));
                    Assert.That(Call(fma, "ArmedText", i), Is.EqualTo(""));
                    Assert.That((bool)Call(fma, "BoxShown", i), Is.False);
                }
                Assert.That(Call(fma, "ActiveText", 3), Is.EqualTo("FMA --"));
                Assert.That((bool)Call(fma, "BoxShown", 3), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Sanitizer_RetiresTheStaticFmaArtwork()
        {
            var root = new GameObject("Second Interation GUI", typeof(RectTransform));
            try
            {
                var bank = new GameObject("Bank Scale", typeof(RectTransform));
                bank.transform.SetParent(root.transform, false);
                var crp = new GameObject("CRP", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                crp.transform.SetParent(bank.transform, false);
                bool found = (bool)Call(T("FAA.Customization.FaaHudRuntimeSanitizer"), "RetireStaticFlightModeArtwork", root);
                Assert.That(found, Is.True);
                Assert.That(crp.activeSelf, Is.False);
                Assert.That(crp.GetComponent<Image>().enabled, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Sanitizer_OnlineMapLayerIsRenderedOnlyByTheMapCamera()
        {
            var mapCameraObject = new GameObject("Map Camera", typeof(Camera));
            var pilotCameraObject = new GameObject("Pilot Test Camera", typeof(Camera));
            try
            {
                Camera mapCamera = mapCameraObject.GetComponent<Camera>(), pilotCamera = pilotCameraObject.GetComponent<Camera>();
                mapCamera.cullingMask = -1; pilotCamera.cullingMask = -1;
                Type sanitizer = T("FAA.Customization.FaaHudRuntimeSanitizer");
                bool applied = (bool)Call(sanitizer, "ApplyOnlineMapLayerIsolation", 23, new List<Camera> { mapCamera, pilotCamera });
                Assert.That(applied, Is.True);
                Assert.That(mapCamera.cullingMask, Is.EqualTo(1 << 23));
                Assert.That(pilotCamera.cullingMask & (1 << 23), Is.Zero);
                Assert.That(pilotCamera.cullingMask & 1, Is.EqualTo(1), "Other layers stay visible.");
                Call(sanitizer, "ApplyOnlineMapLayerIsolation", 23, new List<Camera> { mapCamera, pilotCamera });
                Assert.That(mapCamera.cullingMask, Is.EqualTo(1 << 23), "Idempotent.");
                pilotCamera.cullingMask = -1;
                Assert.That((bool)Call(sanitizer, "ApplyOnlineMapLayerIsolation", 0, new List<Camera> { pilotCamera }), Is.False);
                Assert.That(pilotCamera.cullingMask, Is.EqualTo(-1), "The Default layer is never culled.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(mapCameraObject);
                UnityEngine.Object.DestroyImmediate(pilotCameraObject);
            }
        }

        // ---------------- Bank scale ----------------

        private static Component NewBank(out GameObject root, out Image pointer, out Image slip)
        {
            root = new GameObject("Bank Scale", typeof(RectTransform));
            root.SetActive(false);
            var ip = new GameObject("Bank Scale IP", typeof(RectTransform));
            ip.transform.SetParent(root.transform, false);
            var pointerObject = new GameObject("Roll Pointer", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            pointerObject.transform.SetParent(ip.transform, false);
            var slipObject = new GameObject("Slip Slider", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            slipObject.transform.SetParent(ip.transform, false);
            ((RectTransform)slipObject.transform).sizeDelta = new Vector2(20f, 6f);
            pointer = pointerObject.GetComponent<Image>();
            slip = slipObject.GetComponent<Image>();
            Component bank = root.AddComponent(Bank);
            SetField(bank, "bankScaleIP", ip.transform);
            SetField(bank, "rollPointer", (RectTransform)pointerObject.transform);
            SetField(bank, "slipSlider", (RectTransform)slipObject.transform);
            Call(bank, "Initialize");
            return bank;
        }

        [Test]
        public void Bank_InvalidAttitudeRemovesPointerAndBrick_MissingSlipRemovesBrick()
        {
            Component bank = NewBank(out GameObject root, out Image pointer, out Image slip);
            try
            {
                Call(bank, "ConfigureMeasuredSlip");
                Assert.That(slip.enabled, Is.False, "No measured slip yet: no brick (never a frozen centre).");
                Assert.That(pointer.enabled, Is.True);
                Call(bank, "SetSlipData", .3f, true);
                Assert.That(slip.enabled, Is.True);
                Call(bank, "SetSlipData", float.NaN, false);
                Assert.That(slip.enabled, Is.False);
                Call(bank, "SetSlipData", .3f, true);
                Call(bank, "SetAttitudeValid", false);
                Assert.That(pointer.enabled, Is.False);
                Assert.That(slip.enabled, Is.False);
                Call(bank, "SetAttitudeValid", true);
                Assert.That(pointer.enabled, Is.True);
                Assert.That(slip.enabled, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Bank_SlipTravelIsOneBrickWidthAtFullScaleAndClamped()
        {
            Assert.That((float)Call(Bank, "SlipTravel", 1f, 20f, 1f), Is.EqualTo(20f).Within(1e-4f));
            Assert.That((float)Call(Bank, "SlipTravel", .25f, 20f, 1f), Is.EqualTo(5f).Within(1e-4f));
            Assert.That((float)Call(Bank, "SlipTravel", -3f, 20f, 1f), Is.EqualTo(-20f).Within(1e-4f));
            Assert.That((float)Call(Bank, "SlipTravel", 1f, 20f, 4f), Is.EqualTo(20f).Within(1e-4f), "Never more than one brick width.");
            Assert.That((float)Call(Bank, "SlipTravel", float.NaN, 20f, 1f), Is.Zero);
        }

        [Test]
        public void Bank_RollBeyondTheScaleEndIsFlaggedNotSilentlyClamped()
        {
            Component bank = NewBank(out GameObject root, out _, out _);
            try
            {
                object state = Activator.CreateInstance(Type.GetType("AircraftControl.Core.AircraftState, AircraftControl", true));
                float limit = (float)Get(bank, "MaxBankAngle");
                state.GetType().GetField("Roll").SetValue(state, limit + 15f);
                Call(bank, "UpdateElement", state);
                Assert.That((bool)Get(bank, "OverBank"), Is.True);
                state.GetType().GetField("Roll").SetValue(state, limit - 5f);
                Call(bank, "UpdateElement", state);
                Assert.That((bool)Get(bank, "OverBank"), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void Bridge_StaleFeedRemovesTheRollPointer()
        {
            Component bank = NewBank(out GameObject root, out Image pointer, out _);
            var bridgeObject = new GameObject("Bridge stale test");
            bridgeObject.SetActive(false);
            try
            {
                Component bridge = bridgeObject.AddComponent(Bridge);
                Array elements = Array.CreateInstance(Bank, 1);
                elements.SetValue(bank, 0);
                SetField(bridge, "_bankScaleElements", elements);
                Call(bridge, "ClearEngineHudPointers");
                Assert.That((bool)Get(bank, "PointerVisible"), Is.False);
                Assert.That(pointer.enabled, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(bridgeObject);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        // ---------------- Bridge data hygiene ----------------

        [Test]
        public void Bridge_EnginePercentagesAreTrueValuesAndOnlyNonsenseIsInvalid()
        {
            var systems = new Dictionary<string, float>
            {
                ["sim/flightmodel/engine/ENGN_driv_TRQ[0]"] = 1300f,
                ["sim/flightmodel/engine/POINT_max_TRQ[0]"] = 1000f,
                ["sim/cockpit2/engine/indicators/prop_speed_rpm[0]"] = 230f * 60f / (2f * Mathf.PI),
                ["sim/aircraft/controls/acf_RSC_redline_prp"] = 200f,
                ["sim/cockpit2/engine/indicators/N2_percent[0]"] = 112f,
            };
            object[] torque = { systems, 0, 0f };
            Assert.That((bool)Call(Bridge, "TryCalculateTorquePercent", torque), Is.True);
            Assert.That((float)torque[2], Is.EqualTo(130f).Within(.01f), "No clamp to the 120 % display range.");
            object[] rotor = { systems, 0, 0f };
            Assert.That((bool)Call(Bridge, "TryCalculateRotorNrPercent", rotor), Is.True);
            Assert.That((float)rotor[2], Is.EqualTo(115f).Within(.01f), "No clamp to 110 %.");
            object[] n2 = { systems, "sim/cockpit2/engine/indicators/N2_percent[0]", 150f, 0f };
            Assert.That((bool)Call(Bridge, "TryReadEnginePercent", n2), Is.True);
            Assert.That((float)n2[3], Is.EqualTo(112f));

            systems["sim/flightmodel/engine/ENGN_driv_TRQ[0]"] = 3500f;
            torque = new object[] { systems, 0, 0f };
            Assert.That((bool)Call(Bridge, "TryCalculateTorquePercent", torque), Is.False, "350 % torque is a data fault.");
            systems["sim/cockpit2/engine/indicators/prop_speed_rpm[0]"] = 500f * 60f / (2f * Mathf.PI);
            rotor = new object[] { systems, 0, 0f };
            Assert.That((bool)Call(Bridge, "TryCalculateRotorNrPercent", rotor), Is.False, "250 % NR is a data fault.");
            systems["sim/cockpit2/engine/indicators/N2_percent[0]"] = 160f;
            n2 = new object[] { systems, "sim/cockpit2/engine/indicators/N2_percent[0]", 150f, 0f };
            Assert.That((bool)Call(Bridge, "TryReadEnginePercent", n2), Is.False);
            systems["sim/cockpit2/engine/indicators/N2_percent[0]"] = -1f;
            n2 = new object[] { systems, "sim/cockpit2/engine/indicators/N2_percent[0]", 150f, 0f };
            Assert.That((bool)Call(Bridge, "TryReadEnginePercent", n2), Is.False);
        }

        [Test]
        public void Bridge_WeatherReturnsUseSolidDiscreteLevelsOnANearBlackScope()
        {
            Color32 Level(float strength, float precipitation) => (Color32)Call(Bridge, "ModernWeatherReturnColor", strength, precipitation);
            Color32 light = (Color32)Bridge.GetField("WeatherLevel1Light").GetValue(null);
            Color32 moderate = (Color32)Bridge.GetField("WeatherLevel2Moderate").GetValue(null);
            Color32 heavy = (Color32)Bridge.GetField("WeatherLevel3Heavy").GetValue(null);
            Assert.That(light, Is.EqualTo(new Color32(0, 199, 51, 255)));
            Assert.That(moderate, Is.EqualTo(new Color32(255, 217, 0, 255)));
            Assert.That(heavy, Is.EqualTo(new Color32(255, 38, 26, 255)));
            Assert.That(Level(.05f, .9f), Is.EqualTo(light));
            Assert.That(Level(.3f, .9f), Is.EqualTo(light), "No alpha ramp: a weak return is still solid level 1.");
            Assert.That(Level(.7f, .2f), Is.EqualTo(moderate));
            Assert.That(Level(.97f, .3f), Is.EqualTo(moderate), "Heavy needs heavy precipitation.");
            Assert.That(Level(.97f, .8f), Is.EqualTo(heavy));

            const int size = 64;
            var pixels = new Color32[size * size];
            Call(Bridge, "DrawModernRadarBackdrop", pixels, size, size, 32, 4, 56f, 55f);
            Assert.That(pixels[30 * size + 32], Is.EqualTo(new Color32(4, 10, 14, 240)), "No-return backdrop is opaque near-black.");
            Assert.That(pixels[(size - 1) * size].a, Is.Zero, "No rectangular plate outside the scan sector.");
        }

        // ---------------- Navigation scales ----------------

        private static Component NewScale(GameObject root, bool vertical, Component lateral, Component glide)
        {
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = vertical ? new Vector2(100f, 220f) : new Vector2(320f, 64f);
            Type type = T("FAA.Customization.FaaNavigationScaleGraphic");
            Component scale = root.AddComponent(type);
            type.GetMethod("Configure").Invoke(scale, new object[] { vertical, lateral, glide, null });
            return scale;
        }

        private static int Vertices(Graphic graphic)
        {
            MethodInfo populate = graphic.GetType().GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(VertexHelper) }, null);
            using (var helper = new VertexHelper())
            {
                populate.Invoke(graphic, new object[] { helper });
                return helper.currentVertCount;
            }
        }

        [Test]
        public void LocalizerScale_RealGuidanceShowsOneLegibleSourceLabelAndCalibratedDiamond()
        {
            var elementObject = new GameObject("Localizer Position Ind.");
            elementObject.SetActive(false);
            var scaleObject = new GameObject("FAA Clean Navigation Scale", typeof(RectTransform));
            try
            {
                Component localizer = elementObject.AddComponent(T("HUDControl.Elements.LocalizerElement"));
                Call(localizer, "SetDeviation", 1f);
                Component scale = NewScale(scaleObject, false, localizer, null);
                Assert.That((bool)Get(scale, "HasGuidance"), Is.True);
                Assert.That((float)Get(scale, "Position"), Is.EqualTo(1f / 2.5f).Within(1e-4f), "1 dot = 1/2.5 of full scale.");
                Transform mode = scaleObject.transform.Find("Mode");
                Assert.That(mode.gameObject.activeSelf, Is.True);
                var title = mode.GetComponent<TMP_Text>();
                Assert.That(title.text, Is.EqualTo("LOC"));
                Assert.That(title.fontSize, Is.GreaterThanOrEqualTo(18f));
                Assert.That(title.color.a, Is.GreaterThanOrEqualTo(.85f));
                Assert.That(title.color.g, Is.GreaterThan(title.color.r), "Monochrome HUD colour, never amber.");
                foreach (string hidden in new[] { "Detail", "Positive", "Negative" })
                    Assert.That(scaleObject.transform.Find(hidden).gameObject.activeSelf, Is.False, hidden);
                Assert.That(Vertices((Graphic)scale), Is.GreaterThan(0));
                Assert.That(((Graphic)scale).color.a, Is.EqualTo(1f));

                Call(localizer, "SetDeviation", float.NaN);
                Call(scale, "Refresh");
                Assert.That((bool)Get(scale, "HasGuidance"), Is.False);
                Assert.That(mode.gameObject.activeSelf, Is.False);
                Assert.That(Vertices((Graphic)scale), Is.Zero);

                Call(localizer, "SetDeviation", 2.5f);
                Assert.That((float)Call(localizer, "GetDisplayedDeviation"), Is.EqualTo(2.5f), "Re-acquired guidance does not slide in from a stale value.");
                Call(scale, "Refresh");
                Assert.That((bool)Get(scale, "Clamped"), Is.True, "Full-scale deviation is shown pegged.");
                Assert.That((float)Get(scale, "Position"), Is.EqualTo(1f).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scaleObject);
                UnityEngine.Object.DestroyImmediate(elementObject);
            }
        }

        [Test]
        public void GlideslopeScale_ShowsGsOnlyWithRealDeviation()
        {
            var elementObject = new GameObject("Glidescope");
            elementObject.SetActive(false);
            var scaleObject = new GameObject("FAA Clean Navigation Scale", typeof(RectTransform));
            try
            {
                Component glide = elementObject.AddComponent(T("HUDControl.Elements.GlidescopeElement"));
                Call(glide, "SetDeviation", -1f);
                Component scale = NewScale(scaleObject, true, null, glide);
                Assert.That((bool)Get(scale, "HasGuidance"), Is.True);
                Assert.That(scaleObject.transform.Find("Mode").GetComponent<TMP_Text>().text, Is.EqualTo("G/S"));
                Assert.That((float)Get(scale, "Position"), Is.EqualTo(.4f).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(scaleObject);
                UnityEngine.Object.DestroyImmediate(elementObject);
            }
        }

        [TestCase("HUDControl.Elements.LocalizerElement", "cdiNeedle")]
        [TestCase("HUDControl.Elements.GlidescopeElement", "glidescopeNeedle")]
        public void DeviationBars_NeverCreateTheRetiredAmberMapTargetCue(string typeName, string needleField)
        {
            var root = new GameObject("Deviation bar", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                var needle = new GameObject("Needle", typeof(RectTransform));
                needle.transform.SetParent(root.transform, false);
                Component element = root.AddComponent(T(typeName));
                SetField(element, needleField, (RectTransform)needle.transform);
                SetField(element, "showNavigationTargetCue", true);
                Call(element, "Initialize");
                Call(element, "UpdateNavigationTargetCue");
                Assert.That(root.transform.Find("FAA Navigation Target Cue"), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ---------------- Heading tape ----------------

        private static Component NewTape(GameObject root, float heading)
        {
            Type type = T("FAA.Customization.FaaHeadingTapeOverlay");
            Component overlay = root.AddComponent(type);
            SetField(overlay, "autoFindSources", false);
            SetField(overlay, "flightDataProvider", null);
            SetField(overlay, "aircraftController", null);
            SetField(overlay, "headingHud", null);
            SetField(overlay, "headingTarget", null);
            SetField(overlay, "_displayedHeading", heading);
            Call(overlay, "Update");
            return overlay;
        }

        [Test]
        public void HeadingTape_NorthReads360WithTrueAnnunciatedOnceAndNoNumeralUnderTheBox()
        {
            var root = new GameObject("Heading tape north", typeof(RectTransform));
            try
            {
                // Runtime-only children are created in edit mode only when they already exist (the scene is never modified).
                new GameObject("Heading Reference", typeof(RectTransform)).transform.SetParent(root.transform, false);
                NewTape(root, 0f);
                var readout = root.transform.Find("Current Heading Readout").GetComponent<TMP_Text>();
                Assert.That(readout.text, Does.Contain("360"), "AC 25-11B: north reads 360.");
                Assert.That(readout.text, Does.Not.Contain("T"), "The reference is its own legible label, not a small suffix.");
                var reference = root.transform.Find("Heading Reference").GetComponent<TMP_Text>();
                Assert.That(reference.text, Is.EqualTo("TRU"), "TRUE reference annunciated once.");
                Assert.That(reference.fontSize, Is.GreaterThanOrEqualTo(22f));
                Assert.That(readout.fontSize, Is.GreaterThanOrEqualTo(34f));
                Assert.That(readout.color.a, Is.GreaterThanOrEqualTo(.85f));
                Transform clip = root.transform.Find("Heading Tape Clip");
                float boxHalf = (float)T("FAA.Customization.FaaHeadingTapeOverlay").GetField("ReadoutBoxWidth").GetRawConstantValue() * .5f;
                foreach (TMP_Text label in clip.GetComponentsInChildren<TMP_Text>(false))
                {
                    if (!label.enabled || string.IsNullOrEmpty(label.text)) continue;
                    float x = ((RectTransform)label.transform.parent).anchoredPosition.x;
                    Assert.That(Mathf.Abs(x), Is.GreaterThanOrEqualTo(boxHalf + label.rectTransform.sizeDelta.x * .5f),
                        label.text + " would sit under the readout box.");
                    Assert.That(label.fontSize, Is.GreaterThanOrEqualTo(24f));
                    Assert.That(label.color.a, Is.GreaterThanOrEqualTo(.85f));
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void HeadingTape_LaneAndRowsStayInZoneFiveAndNeverOverlap()
        {
            Type type = T("FAA.Customization.FaaHeadingTapeOverlay");
            float Const(string name) => (float)type.GetField(name).GetRawConstantValue();
            float top = Const("LaneTopFromScreenTop");
            Assert.That(top, Is.GreaterThanOrEqualTo(780f), "Below the protected attitude field.");
            Assert.That(top + Const("TopExtent") + Const("BottomExtent"), Is.LessThanOrEqualTo(990f), "Clear of the bottom chrome.");
            Assert.That(top + Const("TopExtent") + Const("BottomExtent"), Is.LessThanOrEqualTo(852f), "Zone Z5 at module scale 1.");
            Assert.That(Const("BugRowY") - 5f, Is.GreaterThanOrEqualTo(Const("LabelY") + Const("LabelHeight") * .5f),
                "The bug row sits above the numerals, never on them.");
            Assert.That(Const("BugRowY") + 5f, Is.LessThanOrEqualTo(Const("TopExtent")));
            Assert.That(Const("ReadoutBoxCenterY") + Const("ReadoutBoxHeight") * .5f, Is.LessThanOrEqualTo(Const("BugRowY") - 5f),
                "The bug row clears the readout box.");
        }

        [Test]
        public void HeadingTape_MarkerTextIsFixedPerSlotWhileTheCardScrolls()
        {
            var root = new GameObject("Heading tape slots", typeof(RectTransform));
            try
            {
                Component overlay = NewTape(root, 20f);
                Transform clip = root.transform.Find("Heading Tape Clip");
                var before = new Dictionary<Transform, string>();
                foreach (TMP_Text label in clip.GetComponentsInChildren<TMP_Text>(true)) before[label.transform] = label.text;
                SetField(overlay, "_displayedHeading", 24f);
                Call(overlay, "UpdateTape", true);
                foreach (TMP_Text label in clip.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (!before.TryGetValue(label.transform, out string old) || string.IsNullOrEmpty(old) || !label.gameObject.activeInHierarchy) continue;
                    Assert.That(label.text, Is.EqualTo(old), "Scrolling must move markers, not rebuild their text.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
