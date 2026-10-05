using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public class FaaCleanHudPresentationTests
    {
        private static Type Runtime(string name) => Type.GetType(name + ", Assembly-CSharp", true);

        [TestCase(-247f)]
        [TestCase(0f)]
        [TestCase(247f)]
        public void HeadingTargetCaptionStaysInsideTheTape(float cueX)
        {
            float offset = (float)Runtime("FAA.Customization.FaaHeadingTapeOverlay")
                .GetMethod("GetNavigationLabelOffset").Invoke(null, new object[] { cueX, 260f, 75f });
            Assert.That(Mathf.Abs(cueX + offset) + 75f, Is.LessThanOrEqualTo(260f));
        }

        [Test]
        public void TrafficMenu_CloseRequestIsImmediateEvenWhileItFades()
        {
            GameObject root = new GameObject("Traffic menu state", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                Type type = Runtime("FAA.Customization.TrafficRadarContextMenu");
                Component menu = root.AddComponent(type);
                type.GetField("_targetOpen", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(menu, true);
                type.GetField("_progress", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(menu, 1f);
                type.GetMethod("Close").Invoke(menu, new object[] { false });
                Assert.That(type.GetProperty("IsRequestedOpen").GetValue(menu), Is.False);
                Assert.That(type.GetProperty("IsOpen").GetValue(menu), Is.True,
                    "The fade can continue without keeping the companion bar open.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TestCase("GetNavigationDeviation")]
        [TestCase("GetGlideslopeDeviation")]
        public void MissingDeviationDataDoesNotBecomeAnOnCourseZero(string methodName)
        {
            Type bridge = Runtime("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge");
            float result = (float)bridge.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object[] { new System.Collections.Generic.Dictionary<string, float>(), float.NaN });
            Assert.That(float.IsNaN(result), Is.True);
        }

        [TestCase("LocalizerElement")]
        [TestCase("GlidescopeElement")]
        public void NavigationElement_InvalidOrStaleDataClearsGuidance(string elementName)
        {
            GameObject root = new GameObject("Guidance validity");
            root.SetActive(false);
            try
            {
                Type type = Runtime("HUDControl.Elements." + elementName);
                Component element = root.AddComponent(type);
                type.GetMethod("SetDeviation").Invoke(element, new object[] { 0f });
                Assert.That(type.GetProperty("HasDeviationData").GetValue(element), Is.True);
                type.GetMethod("SetDeviation").Invoke(element, new object[] { float.NaN });
                Assert.That(type.GetProperty("HasDeviationData").GetValue(element), Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void RadarDrawer_HidesTheEntireBarIncludingItsPrimaryRow()
        {
            var owner = new GameObject("Inactive radar owner");
            owner.SetActive(false); // Don't bind to real scene radars during tests.
            var strip = new GameObject("Control strip", typeof(RectTransform), typeof(CanvasGroup));
            try
            {
                Type ownerType = Runtime("FAA.Customization.FaaRadarControlsOverlay");
                Component overlay = owner.AddComponent(ownerType);
                var primary = new GameObject("Primary", typeof(RectTransform), typeof(Image));
                primary.transform.SetParent(strip.transform);
                var secondary = new GameObject("Secondary", typeof(RectTransform));
                secondary.transform.SetParent(strip.transform);
                object drawer = ownerType.GetMethod("EnsureDrawer", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(overlay, new object[] { strip.GetComponent<RectTransform>() });
                CanvasGroup group = strip.GetComponent<CanvasGroup>();
                Assert.That(group.alpha, Is.Zero, "The primary bar must not remain visible at startup.");
                Assert.That(group.blocksRaycasts, Is.False);
                MethodInfo setVisible = drawer.GetType().GetMethod("SetVisible");
                setVisible.Invoke(drawer, new object[] { true, true });
                Assert.That(group.alpha, Is.EqualTo(1f));
                Assert.That(group.interactable, Is.True);
                setVisible.Invoke(drawer, new object[] { false, true });
                Assert.That(group.alpha, Is.Zero);
                Assert.That(group.interactable, Is.False);
                Assert.That(group.blocksRaycasts, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
                UnityEngine.Object.DestroyImmediate(strip);
            }
        }

        [TestCase(3840f, 2160f)]
        [TestCase(1920f, 1080f)]
        [TestCase(1280f, 720f)]
        [TestCase(640f, 480f)]
        public void WheelAndCommandColumn_FitInsideTheViewport(float width, float height)
        {
            Type menu = Runtime("VoiceControl.UI.UIToolkitRadialMenuAdvanced");
            float scale = (float)menu.GetMethod("CalculateMenuScale").Invoke(null, new object[] { width, height, 820f, 560f });
            Assert.That(scale * 820f, Is.LessThanOrEqualTo(width - 40f + .1f));
            Assert.That(scale * 560f, Is.LessThanOrEqualTo(height - 72f + .1f));
        }

        [Test]
        public void NavigationScale_WithoutTargetNeverShowsAnOnCourseDiamond()
        {
            GameObject root = new GameObject("Navigation scale", typeof(RectTransform));
            try
            {
                Type type = Runtime("FAA.Customization.FaaNavigationScaleGraphic");
                Component scale = root.AddComponent(type);
                type.GetMethod("Configure").Invoke(scale, new object[] { false, null, null, null });
                Assert.That((bool)type.GetField("hasGuidance", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(scale), Is.False);
                // No guidance: no permanent 'NO TARGET' scale, no labels, no mesh and no layout bounds (colour alpha 0).
                foreach (string label in new[] { "Detail", "Mode", "Positive", "Negative" })
                {
                    Transform child = root.transform.Find(label);
                    Assert.That(child == null || !child.gameObject.activeSelf, Is.True, label + " must be hidden without guidance.");
                }
                Assert.That(PopulatedVertexCount((Graphic)scale), Is.Zero, "No rail, dots or diamond without guidance.");
                Assert.That(((Graphic)scale).color.a, Is.LessThanOrEqualTo(.001f), "A hidden scale must not report layout or keep-out bounds.");
                Assert.That(((Graphic)scale).raycastTarget, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void NavigationScale_MapOnlyVerticalModeIsNotLabeledGlideslope()
        {
            GameObject root = new GameObject("Vertical map scale", typeof(RectTransform));
            try
            {
                Type type = Runtime("FAA.Customization.FaaNavigationScaleGraphic");
                Component scale = root.AddComponent(type);
                type.GetMethod("Configure").Invoke(scale, new object[] { true, null, null, null });
                // Without real G/S deviation the vertical scale is hidden: never 'G/S', never an along-track map mode.
                Transform mode = root.transform.Find("Mode");
                Assert.That(mode == null || !mode.gameObject.activeSelf, Is.True);
                if (mode != null)
                {
                    Graphic text = mode.GetComponent<Graphic>();
                    Assert.That(text.GetType().GetProperty("text").GetValue(text), Is.Not.EqualTo("G/S"));
                    Assert.That(text.GetType().GetProperty("text").GetValue(text), Is.Not.EqualTo("ALONG TRACK"));
                }
                Assert.That(PopulatedVertexCount((Graphic)scale), Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        // ---------------- Digital FMA: same axis prefixes as Classic, unusual-attitude declutter ----------------

        private static Component NewDigitalFma(out GameObject hudRoot)
        {
            hudRoot = new GameObject("Second Interation GUI", typeof(RectTransform));
            hudRoot.transform.localScale = Vector3.one * 540f;
            var fma = (Component)Runtime("FAA.Customization.FaaFlightModeAnnunciator").GetMethod("Ensure").Invoke(null, new object[] { hudRoot.transform });
            Assert.That(fma, Is.Not.Null);
            return fma;
        }

        private static object Fma(bool valid, bool engaged, string lateral, string vertical, string verticalArmed, string status) =>
            Activator.CreateInstance(Runtime("FAA.Customization.FaaFma"), valid, engaged, "", "", lateral, "", vertical, verticalArmed, status);

        private static object Invoke(object target, string method, params object[] args)
        {
            foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
                if (m.Name == method && m.GetParameters().Length == args.Length) return m.Invoke(target, args);
            Assert.Fail(method + " not found");
            return null;
        }

        [Test]
        public void DigitalFma_UsesTheClassicAxisPrefixesSmallAndDimOutsideTheModeBox()
        {
            Component fma = NewDigitalFma(out GameObject root);
            try
            {
                Invoke(fma, "Present", Fma(true, true, "HDG", "ALT", "", "CPL"), 0f);
                var prefixes = (string[])Runtime("FAA.Customization.FaaClassicAnalogHud").GetField("FmaPrefixes").GetValue(null);
                Assert.That(Invoke(fma, "PrefixText", 1), Is.EqualTo(prefixes[1].Trim()), "Same roll-axis prefix as Classic.");
                Assert.That(Invoke(fma, "PrefixText", 2), Is.EqualTo(prefixes[2].Trim()), "Same pitch-axis prefix as Classic.");
                Assert.That(Invoke(fma, "PrefixText", 0), Is.EqualTo(""), "No prefix on an empty column.");
                Assert.That(Invoke(fma, "ActiveText", 1), Is.EqualTo("HDG"), "The mode text itself is unchanged.");

                var texts = fma.GetComponentsInChildren<TMPro.TMP_Text>(true);
                TMPro.TMP_Text prefix = Array.Find(texts, t => t.name == "R Prefix"), active = Array.Find(texts, t => t.name == "R Active");
                Assert.That(prefix, Is.Not.Null);
                Assert.That(prefix.fontSize, Is.LessThan(active.fontSize), "Small.");
                Assert.That(prefix.fontSize, Is.GreaterThanOrEqualTo((float)Runtime("FAA.Customization.FaaHudStyle").GetField("MinLabel").GetRawConstantValue()));
                Assert.That(prefix.color.a, Is.LessThan(active.color.a), "Dim.");
                Assert.That(prefix.color.a, Is.GreaterThanOrEqualTo((float)Runtime("FAA.Customization.FaaHudStyle").GetField("MinQuietAlpha").GetRawConstantValue() - .001f));
                float prefixRight = prefix.rectTransform.anchoredPosition.x + prefix.rectTransform.sizeDelta.x * .5f;
                float boxLeft = active.rectTransform.anchoredPosition.x - (active.GetPreferredValues(active.text).x + 14f) * .5f;
                Assert.That(prefixRight, Is.LessThanOrEqualTo(boxLeft + .01f), "The prefix sits outside the mode-change box.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test]
        public void DigitalFma_ArmedModesAreRemovedInAnUnusualAttitude()
        {
            Component fma = NewDigitalFma(out GameObject root);
            try
            {
                Invoke(fma, "Present", Fma(true, true, "HDG", "ALT", "G/S ARM", "CPL"), 0f);
                Assert.That(Invoke(fma, "ArmedText", 2), Is.EqualTo("G/S ARM"));
                Invoke(fma, "Render", .1f, true);
                Assert.That(Invoke(fma, "ArmedText", 2), Is.EqualTo(""), "Declutter: no armed modes in an unusual attitude.");
                Assert.That(Invoke(fma, "ActiveText", 2), Is.EqualTo("ALT"), "Active modes stay.");
                Invoke(fma, "Render", .2f, false);
                Assert.That(Invoke(fma, "ArmedText", 2), Is.EqualTo("G/S ARM"), "Restored on recovery.");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static int PopulatedVertexCount(Graphic graphic)
        {
            MethodInfo populate = graphic.GetType().GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(VertexHelper) }, null);
            Assert.That(populate, Is.Not.Null);
            using (var helper = new VertexHelper())
            {
                populate.Invoke(graphic, new object[] { helper });
                return helper.currentVertCount;
            }
        }
    }
}
