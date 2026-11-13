using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Digital left/right columns: IAS/ALT readouts, TQ, NR/N2, VS and the column layout (FaaNonConformalReflow).</summary>
    public class FaaDigitalColumnsTests
    {
        private readonly List<UnityEngine.Object> created = new();

        private static Type T(string name) => Type.GetType("FAA.Customization." + name + ", Assembly-CSharp", true);
        private static float Const(string type, string field) => Convert.ToSingle(T(type).GetField(field).GetValue(null));
        private static object Static(string type, string member, params object[] args) => T(type).GetMethod(member).Invoke(null, args);
        private static object Exceedance(string name) => Enum.Parse(T("FaaExceedance"), name);
        private static string State(Component g, string property) => g.GetType().GetProperty(property).GetValue(g).ToString();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created) if (o != null) UnityEngine.Object.DestroyImmediate(o);
            created.Clear();
        }

        private GameObject Track(GameObject go) { created.Add(go); return go; }

        private Component Instrument(string kind, params string[] staleChildren)
        {
            var go = Track(new GameObject("Digital column instrument " + kind, typeof(RectTransform)));
            foreach (string name in staleChildren)
            {
                var child = new GameObject(name, typeof(RectTransform));
                child.transform.SetParent(go.transform, false);
                child.AddComponent<TextMeshProUGUI>().text = "STALE";
            }
            var type = T("FaaEngineInstrumentGraphic");
            var graphic = go.AddComponent(type);
            type.GetMethod("Configure").Invoke(graphic, new object[] { Enum.Parse(T("FaaEngineInstrument"), kind), null, null });
            return graphic;
        }

        private static void Sample(Component g, float l, bool lv, float r, bool rv, float nr, bool nv, int engines = 2, bool airborne = true) =>
            g.GetType().GetMethod("ApplySample").Invoke(g, new object[] { l, lv, r, rv, nr, nv, engines, airborne });

        private static TMP_Text Text(Component g, string name) => g.transform.Find(name)?.GetComponent<TMP_Text>();

        private static List<UIVertex> Mesh(Component g)
        {
            var vh = new VertexHelper();
            g.GetType().GetMethod("OnPopulateMesh", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(VertexHelper) }, null)
                .Invoke(g, new object[] { vh });
            var list = new List<UIVertex>();
            for (int i = 0; i < vh.currentVertCount; i++) { var v = new UIVertex(); vh.PopulateUIVertex(ref v, i); list.Add(v); }
            vh.Dispose();
            return list;
        }

        /// <summary>Mesh vertices without the backing card and plates (symbols only).</summary>
        private static List<UIVertex> Symbols(Component g)
        {
            Color card = (Color)T("FaaDigitalColumnStyle").GetField("Card").GetValue(null), plate = (Color)T("FaaDigitalColumnStyle").GetField("Plate").GetValue(null);
            bool Backing(Color32 c, Color b) => Near(c, b) && Mathf.Abs(c.a / 255f - b.a) < .02f;
            return Mesh(g).FindAll(v => !Backing(v.color, card) && !Backing(v.color, plate));
        }

        private static bool Near(Color32 a, Color b) =>
            Mathf.Abs(a.r / 255f - b.r) < .02f && Mathf.Abs(a.g / 255f - b.g) < .02f && Mathf.Abs(a.b / 255f - b.b) < .02f;

        private static Color StyleColor(string name) => (Color)T("FaaHudStyle").GetField(name).GetValue(null);

        [Test]
        public void TypographyTokensMeetTheFaaFloorAtTheSmallestModuleScale()
        {
            float minLabel = Const("FaaHudStyle", "MinLabel"), minScale = Const("FaaHudStyle", "MinModuleScale");
            float label = Const("FaaDigitalColumnStyle", "LabelSize");
            Assert.That(label * minScale, Is.GreaterThanOrEqualTo(minLabel - .001f));
            // M10: captions, units, ids and scale numerals meet the Secondary token even at the smallest module scale.
            Assert.That(label * minScale, Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "Secondary") - .001f));
            Assert.That(Const("FaaDigitalColumnStyle", "ValueSize") * minScale, Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "Data") - .001f));
            Assert.That(Const("FaaDigitalColumnStyle", "VsValueSize") * minScale, Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "Secondary") - .001f));
            Assert.That(Const("FaaDigitalColumnStyle", "HeadlineSize"), Is.GreaterThan(Const("FaaDigitalColumnStyle", "ValueSize")),
                "Rotor NR is the most prominent engine number.");
            Assert.That(Const("FaaDigitalColumnStyle", "ValueSize"), Is.GreaterThan(label), "Values outrank their captions by size.");
            Assert.That(Const("FaaDigitalColumnStyle", "TitleAlpha"), Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "MinTextAlpha")));
            Assert.That(Const("FaaDigitalColumnStyle", "LabelAlpha"), Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "MinTextAlpha")));
            Assert.That(Const("FaaDigitalColumnStyle", "LineAlpha"), Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "MinQuietAlpha")));
        }

        [TestCase("Torque")]
        [TestCase("EngineSpeed")]
        [TestCase("VerticalSpeed")]
        public void EveryVisibleLabelIsLegibleHaloedAndSeparate(string kind)
        {
            var g = Instrument(kind);
            float floor = Const("FaaHudStyle", "MinLabel") / Const("FaaHudStyle", "MinModuleScale");
            float alpha = Const("FaaHudStyle", "MinQuietAlpha");
            var states = new[] { new[] { 95f, 96f, 100f }, new[] { 105f, 113f, 88f }, new[] { 135f, 101f, 101f } };
            foreach (var s in states)
            {
                Sample(g, kind == "VerticalSpeed" ? s[0] * 30f : s[0], true, s[1], true, s[2], true);
                var rects = new List<(string, Rect)>();
                var bounds = new Rect(-((RectTransform)g.transform).sizeDelta * .5f, ((RectTransform)g.transform).sizeDelta);
                foreach (var t in g.GetComponentsInChildren<TMP_Text>(false))
                {
                    if (string.IsNullOrEmpty(t.text)) continue;
                    Assert.That(t.fontSize, Is.GreaterThanOrEqualTo(floor - .001f), t.name);
                    Assert.That(t.color.a, Is.GreaterThanOrEqualTo(alpha), t.name);
                    if (t.font != null && t.font.material != null && t.font.material.HasProperty("_UnderlayColor"))
                        Assert.That(t.fontSharedMaterial.IsKeywordEnabled("UNDERLAY_ON"), Is.True, t.name + " needs the halo");
                    var rt = t.rectTransform;
                    var r = new Rect(rt.anchoredPosition - rt.sizeDelta * .5f, rt.sizeDelta);
                    Assert.That(bounds.Contains(r.min) && bounds.Contains(r.max), Is.True, t.name + " must stay inside the instrument rect (stable layout bounds)");
                    foreach (var (other, o) in rects) Assert.That(r.Overlaps(o), Is.False, t.name + " overlaps " + other);
                    rects.Add((t.name, r));
                }
            }
        }

        [Test]
        public void TorqueTakesCautionAndWarningColoursAndShowsTheTrueValueAboveTheScale()
        {
            var g = Instrument("Torque", "Units", "Scale 0", "Scale 1", "Scale 2", "Footer");
            foreach (string stale in new[] { "Units", "Scale 0", "Scale 1", "Scale 2", "Footer" })
                Assert.That(g.transform.Find(stale).gameObject.activeSelf, Is.False, stale + " is redundant chrome");
            Sample(g, 95f, true, 105f, true, 0, false);
            Assert.That(State(g, "LeftState"), Is.EqualTo("Normal"));
            Assert.That(State(g, "RightState"), Is.EqualTo("Caution"));
            Assert.That(Text(g, "Right Value").text, Is.EqualTo("105"));
            Assert.That(Near(Text(g, "Right Value").color, StyleColor("Amber")), Is.True);
            Assert.That(Text(g, "Title").text, Is.EqualTo("TQ %"));
            Assert.That(Text(g, "Left").text, Is.EqualTo("1"));
            Assert.That(Text(g, "Right").text, Is.EqualTo("2"));

            Sample(g, 135f, true, 400f, true, 0, false);
            Assert.That(State(g, "LeftState"), Is.EqualTo("Warning"));
            Assert.That(Text(g, "Left Value").text, Is.EqualTo("135"), "The digits keep the true value while the bar pegs.");
            Assert.That(Near(Text(g, "Left Value").color, StyleColor("Red")), Is.True);
            Assert.That(State(g, "RightState"), Is.EqualTo("Invalid"), "An implausible value is invalid data, not a plausible reading.");
            Assert.That(Text(g, "Right Value").text, Is.EqualTo("---"));
            float top = (float)Static("FaaEngineInstrumentGraphic", "RailY", 120f), x = -Const("FaaEngineInstrumentGraphic", "RailX");
            float apex = top + Const("FaaEngineInstrumentGraphic", "PegArrow");
            bool arrow = Mesh(g).Exists(v => Mathf.Abs(v.position.x - x) < .01f && Mathf.Abs(v.position.y - apex) < .01f);
            Assert.That(arrow, Is.True, "Pegged bar needs an arrowhead above the rail.");
            Sample(g, 100f, true, 100f, true, 0, false);
            arrow = Mesh(g).Exists(v => Mathf.Abs(v.position.x - x) < .01f && Mathf.Abs(v.position.y - apex) < .01f);
            Assert.That(arrow, Is.False);
        }

        [Test]
        public void CautionAddsABoxAndRailsCarryTheLimitMarks()
        {
            var g = Instrument("Torque");
            Sample(g, 95f, true, 95f, true, 0, false);
            var normal = Mesh(g);
            Color amber = StyleColor("Amber"), red = StyleColor("Red");
            int normalAmber = normal.FindAll(v => Near(v.color, amber)).Count;
            Assert.That(normalAmber, Is.GreaterThan(0), "Amber time-limited band on the rail.");
            Assert.That(normal.Exists(v => Near(v.color, red)), Is.True, "Single red limit line on the rail.");
            Sample(g, 105f, true, 95f, true, 0, false);
            Assert.That(Mesh(g).FindAll(v => Near(v.color, amber)).Count, Is.GreaterThan(normalAmber), "Caution value is boxed (non-colour coding).");
            Sample(g, 0f, false, 0f, false, 0, false);
            Assert.That(Text(g, "Left Value").text, Is.EqualTo("---"));
        }

        [Test]
        public void TorqueRailsCarryAGreenNormalBandAndBoldHaloedLimitMarks()
        {
            var g = Instrument("Torque");
            Sample(g, 60f, true, 60f, true, 0, false);
            var mesh = Mesh(g);
            Color green = StyleColor("Green"), amber = StyleColor("Amber"), red = StyleColor("Red");
            float x = -Const("FaaEngineInstrumentGraphic", "RailX"), inner = Const("FaaEngineInstrumentGraphic", "BandInner"),
                outer = Const("FaaEngineInstrumentGraphic", "BandOuter");
            float rail0 = (float)Static("FaaEngineInstrumentGraphic", "RailY", 0f), rail100 = (float)Static("FaaEngineInstrumentGraphic", "RailY", 100f),
                rail110 = (float)Static("FaaEngineInstrumentGraphic", "RailY", 110f);
            Assert.That(outer - inner, Is.GreaterThanOrEqualTo(6f), "Limit bands are at least 6 local units wide (M9).");
            bool Has(Color c, float vx, float vy) => mesh.Exists(v => Near(v.color, c) && Mathf.Abs(v.position.x - vx) < .01f && Mathf.Abs(v.position.y - vy) < .01f);
            Assert.That(Has(green, x - outer, rail0) && Has(green, x - outer, rail100), Is.True, "Green normal band 0-100 % outboard of the rail.");
            Assert.That(Has(amber, x - outer, rail100) && Has(amber, x - outer, rail110), Is.True, "Amber time-limited band 100-110 %.");
            var limit = mesh.FindAll(v => Near(v.color, red) && v.color.a > 200);
            Assert.That(limit.Count, Is.GreaterThan(0));
            float minY = float.MaxValue, maxY = float.MinValue, minX = float.MaxValue;
            foreach (var v in limit) { minY = Mathf.Min(minY, v.position.y); maxY = Mathf.Max(maxY, v.position.y); minX = Mathf.Min(minX, v.position.x); }
            Assert.That(maxY - minY, Is.GreaterThanOrEqualTo(3f - .001f), "Red limit line is at least 3 units thick.");
            Assert.That(minX, Is.LessThan(x - outer), "Red limit line extends past the rail and its band.");
            Color halo = (Color)T("FaaDigitalColumnStyle").GetField("Halo").GetValue(null);
            Assert.That(mesh.Exists(v => Near(v.color, halo) && Mathf.Abs(v.position.y - (rail110 + (Const("FaaEngineInstrumentGraphic", "LimitLineWidth") + 3f) * .5f)) < .01f),
                Is.True, "The red line sits on a dark halo.");
        }

        [Test]
        public void NrBlockDrawsTheClassicLowAndHighRotorLimitsOnAnExpandedScale()
        {
            var g = Instrument("EngineSpeed");
            Sample(g, 100f, true, 100f, true, 100f, true);
            var mesh = Mesh(g);
            Color green = StyleColor("Green"), amber = StyleColor("Amber"), red = StyleColor("Red");
            float Y(float p) => (float)Static("FaaEngineInstrumentGraphic", "RotorRailY", p);
            float L(string n) => Const("FaaRotorcraftLimits", n);
            float half = Const("FaaEngineInstrumentGraphic", "StripHalfWidth");
            bool Has(Color c, float vy) => mesh.Exists(v => Near(v.color, c) && Mathf.Abs(Mathf.Abs(v.position.x) - half) < .01f && Mathf.Abs(v.position.y - vy) < .01f);
            Assert.That(Has(amber, Y(L("NrWarningBelow"))) && Has(amber, Y(L("NrCautionBelow"))), Is.True, "Amber low-rotor band 91-95 %.");
            Assert.That(Has(green, Y(L("NrCautionBelow"))) && Has(green, Y(L("NrCautionAbove"))), Is.True, "Green normal band 95-107 %.");
            Assert.That(Has(amber, Y(L("NrCautionAbove"))) && Has(amber, Y(L("NrWarningAbove"))), Is.True, "Amber high band 107-110 %.");
            foreach (string limit in new[] { "NrWarningBelow", "NrWarningAbove" })
            {
                float y = Y(L(limit));
                Assert.That(mesh.Exists(v => Near(v.color, red) && Mathf.Abs(v.position.y - y) <= Const("FaaEngineInstrumentGraphic", "LimitLineWidth") * .5f + .01f && Mathf.Abs(v.position.x) < 13f),
                    Is.True, limit + " red line on the strip");
            }
            Assert.That(Y(L("NrWarningAbove")) - Y(L("NrWarningBelow")), Is.GreaterThanOrEqualTo(30f), "Expanded scale keeps the limit zones legible.");
            Assert.That(Y(50f), Is.EqualTo(Const("FaaEngineInstrumentGraphic", "RailBottom")).Within(1e-4f), "Below the scale pegs at the bottom.");
            Assert.That(Y(150f), Is.EqualTo(Const("FaaEngineInstrumentGraphic", "RailTop")).Within(1e-4f));
            Sample(g, 45f, true, 45f, true, 45f, true, 2, false);
            float bottom = Const("FaaEngineInstrumentGraphic", "RailBottom");
            Assert.That(Mesh(g).Exists(v => v.position.y < bottom - 6f && Mathf.Abs(v.position.x) < 1f), Is.True,
                "NR below the scale draws an arrowhead below the strip; the digits stay true.");
            Assert.That(Text(g, "Center Value").text, Is.EqualTo("45"));
        }

        [Test]
        public void VerticalSpeedPointerIsALargeFilledTriangle()
        {
            var g = Instrument("VerticalSpeed");
            Sample(g, 1000f, true, 0, false, 0, false);
            float y = (float)Static("FaaEngineInstrumentGraphic", "VerticalSpeedPosition", 1000f);
            Color ink = (Color)T("FaaDigitalColumnStyle").GetProperty("Normal").GetValue(null); // the pilot symbology colour
            float minY = float.MaxValue, maxY = float.MinValue, scaleX = Const("FaaEngineInstrumentGraphic", "VsScaleX");
            foreach (var v in Mesh(g))
                if (Near(v.color, ink) && v.position.x > scaleX + 4f && v.position.x < scaleX + 30f && Mathf.Abs(v.position.y - y) < 20f)
                { minY = Mathf.Min(minY, v.position.y); maxY = Mathf.Max(maxY, v.position.y); }
            Assert.That(maxY - minY, Is.GreaterThanOrEqualTo(Const("FaaEngineInstrumentGraphic", "VsPointerHeight") - .01f));
            Assert.That(Const("FaaEngineInstrumentGraphic", "VsPointerHeight") * Const("FaaHudStyle", "MinModuleScale") * 2560f / 1920f,
                Is.GreaterThanOrEqualTo(18f), "At least about 18 px tall on a 2560-wide view at the smallest module scale.");
            float boxLeft = Const("FaaEngineInstrumentGraphic", "VsBoxX");
            Assert.That(scaleX + 1.5f + Const("FaaEngineInstrumentGraphic", "VsPointerDepth"), Is.LessThan(boxLeft),
                "The pointer never runs under the value box.");
        }

        [Test]
        public void UnusualAttitudeDecluttersVsNumeralsAndN2SplitDigitsButKeepsAlerts()
        {
            var vs = Instrument("VerticalSpeed");
            vs.GetType().GetProperty("UnusualAttitudeDeclutter").SetValue(vs, true);
            Sample(vs, -800f, true, 0, false, 0, false);
            foreach (string n in new[] { "Upper", "Upper Mid", "Lower Mid", "Lower" }) Assert.That(Text(vs, n).text, Is.Empty, n);
            Assert.That(Text(vs, "Value").text, Is.EqualTo("−800"), "The VS digits stay.");
            vs.GetType().GetProperty("UnusualAttitudeDeclutter").SetValue(vs, false);
            Sample(vs, -800f, true, 0, false, 0, false);
            Assert.That(Text(vs, "Upper").text, Is.EqualTo("2"));

            var nr = Instrument("EngineSpeed");
            nr.GetType().GetProperty("UnusualAttitudeDeclutter").SetValue(nr, true);
            Sample(nr, 96f, true, 100f, true, 100f, true);
            Assert.That((bool)nr.GetType().GetProperty("ShowsN2Digits").GetValue(nr), Is.False, "A split alone is detail.");
            Assert.That(Text(nr, "Left").text, Is.EqualTo("1"));
            Sample(nr, 108f, true, 100f, true, 100f, true);
            Assert.That(Text(nr, "Left").text, Is.EqualTo("108"), "An N2 exceedance always shows its digits.");
        }

        [Test]
        public void InspectionKeepsDigitsAtTheAwarenessIntensityAndDimsDetail()
        {
            var inspection = T("FaaHudInspection");
            var flags = BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                float dimmed = Const("FaaHudInspection", "InspectionIntensity");
                inspection.GetField("fromIntensity", flags).SetValue(null, dimmed);
                inspection.GetField("toIntensity", flags).SetValue(null, dimmed);
                inspection.GetField("transitionStart", flags).SetValue(null, -100f);
                float awareness = Const("FaaHudInspection", "AwarenessIntensity");
                var g = Instrument("Torque");
                Sample(g, 80f, true, 82f, true, 0, false);
                Assert.That(Text(g, "Left Value").canvasRenderer.GetColor().a, Is.EqualTo(awareness).Within(.01f), "TQ digits are awareness readouts.");
                Assert.That(Text(g, "Title").canvasRenderer.GetColor().a, Is.EqualTo(dimmed).Within(.01f), "Captions dim with the forward HUD.");
                Assert.That(((Graphic)g).canvasRenderer.GetColor().a, Is.EqualTo(awareness).Within(.01f), "Plates and alert boxes keep the awareness alpha.");
                var mesh = Mesh(g);
                Color card = (Color)T("FaaDigitalColumnStyle").GetField("Card").GetValue(null);
                Assert.That(mesh.Exists(v => Mathf.Abs(v.color.a / 255f - card.a * dimmed / awareness) < .01f), Is.True, "The card and rails carry the detail ratio.");
                var vs = Instrument("VerticalSpeed");
                Sample(vs, 500f, true, 0, false, 0, false);
                Assert.That(Text(vs, "Value").canvasRenderer.GetColor().a, Is.EqualTo(awareness).Within(.01f), "VS digits are awareness readouts.");
                Assert.That(Text(vs, "Upper").canvasRenderer.GetColor().a, Is.EqualTo(dimmed).Within(.01f));
            }
            finally { inspection.GetMethod("ResetImmediate").Invoke(null, null); }
        }

        [Test]
        public void RotorNrIsTheHeadlineAndN2DigitsAppearOnlyWhenTheySplit()
        {
            var g = Instrument("EngineSpeed", "Left Value", "Right Value", "Footer", "Units", "Scale 2");
            foreach (string stale in new[] { "Left Value", "Right Value", "Footer", "Units", "Scale 2" })
                Assert.That(g.transform.Find(stale).gameObject.activeSelf, Is.False, stale);
            Sample(g, 100.2f, true, 100.3f, true, 100.4f, true);
            Assert.That(Text(g, "Title").text, Is.EqualTo("NR %"));
            Assert.That(Text(g, "Center Value").text, Is.EqualTo("100"));
            Assert.That(Text(g, "Center Value").fontSize, Is.EqualTo(Const("FaaDigitalColumnStyle", "HeadlineSize")));
            Assert.That(Text(g, "Left").text, Is.EqualTo("1"));
            Assert.That(Text(g, "Right").text, Is.EqualTo("2"));
            Assert.That(Text(g, "Caption").text, Is.EqualTo("N2"));
            Assert.That((bool)g.GetType().GetProperty("ShowsN2Digits").GetValue(g), Is.False, "The same number is not repeated three times.");
            Sample(g, 88f, true, 100.3f, true, 100.4f, true);
            Assert.That(Text(g, "Left").text, Is.EqualTo("88"));
            Assert.That(Text(g, "Right").text, Is.EqualTo("2"));
            Assert.That((bool)g.GetType().GetProperty("ShowsN2Digits").GetValue(g), Is.True);
            Sample(g, 100f, true, 100f, true, 0f, false);
            Assert.That(Text(g, "Center Value").text, Is.EqualTo("---"));
            Assert.That(Text(g, "Left").text, Is.EqualTo("100"), "With NR unavailable the N2 digits are the engine-speed numbers.");
        }

        [Test]
        public void LowRotorAlertsOnlyWhenAirborne()
        {
            var g = Instrument("EngineSpeed");
            Sample(g, 90f, true, 90f, true, 90f, true, 2, true);
            Assert.That(State(g, "RotorState"), Is.EqualTo("Warning"));
            Assert.That(Near(Text(g, "Center Value").color, StyleColor("Red")), Is.True);
            Sample(g, 60f, true, 60f, true, 60f, true, 2, false);
            Assert.That(State(g, "RotorState"), Is.EqualTo("Normal"), "Ground start or idle must not show red.");
            Sample(g, 108f, true, 108f, true, 108f, true, 2, false);
            Assert.That(State(g, "RotorState"), Is.EqualTo("Caution"));
            Assert.That((bool)g.GetType().GetProperty("ShowsN2Digits").GetValue(g), Is.True, "An N2 exceedance always shows its digits.");
        }

        [TestCase(100.2f, true, 100.4f, true, false, "Normal", false)]
        [TestCase(101.6f, true, 100.4f, true, false, "Normal", true)]
        [TestCase(101.2f, true, 100.4f, true, true, "Normal", true)]
        [TestCase(100.8f, true, 100.4f, true, true, "Normal", false)]
        [TestCase(100.8f, true, 100.4f, true, false, "Normal", false)]
        [TestCase(100.4f, true, 0f, false, false, "Normal", true)]
        [TestCase(108f, true, 108f, true, false, "Caution", true)]
        [TestCase(100f, false, 100f, true, true, "Invalid", false)]
        public void N2DigitsUseHysteresisAroundTheNrMatch(float n2, bool n2Valid, float nr, bool nrValid, bool wasShown, string state, bool expected) =>
            Assert.That(Static("FaaEngineInstrumentGraphic", "ShowN2Value", n2, n2Valid, nr, nrValid, wasShown, Exceedance(state)), Is.EqualTo(expected));

        [Test]
        public void RailMapsZeroToOneTwentyAndPegsAboveIt()
        {
            // Float geometry: compare with a tolerance (26.0 vs 26.0000019 failed an exact compare).
            float bottom = Const("FaaEngineInstrumentGraphic", "RailBottom"), height = Const("FaaEngineInstrumentGraphic", "RailHeight");
            Assert.That((float)Static("FaaEngineInstrumentGraphic", "RailY", 0f), Is.EqualTo(bottom).Within(1e-4f));
            Assert.That((float)Static("FaaEngineInstrumentGraphic", "RailY", 60f), Is.EqualTo(bottom + height * .5f).Within(1e-4f));
            Assert.That((float)Static("FaaEngineInstrumentGraphic", "RailY", 120f), Is.EqualTo(bottom + height).Within(1e-4f));
            Assert.That((float)Static("FaaEngineInstrumentGraphic", "RailY", 150f), Is.EqualTo(bottom + height).Within(1e-4f));
            Assert.That((float)Static("FaaEngineInstrumentGraphic", "RailY", float.NaN), Is.EqualTo(bottom).Within(1e-4f));
            Assert.That(Const("FaaEngineInstrumentGraphic", "RailTop"), Is.EqualTo(bottom + height).Within(1e-4f));
        }

        [Test]
        public void VerticalSpeedIsDeclutteredAndPegsWithAnArrowheadAtTheCorrectEnd()
        {
            var g = Instrument("VerticalSpeed", "Direction", "Footer", "Zero", "Units");
            foreach (string stale in new[] { "Direction", "Footer", "Zero", "Units" })
                Assert.That(g.transform.Find(stale).gameObject.activeSelf, Is.False, stale);
            Sample(g, 3000f, true, 0, false, 0, false);
            Assert.That(Text(g, "Value").text, Is.EqualTo("+3000"));
            Assert.That(Text(g, "Title").text, Is.EqualTo("VS FPM"));
            foreach (var t in g.GetComponentsInChildren<TMP_Text>(false))
                Assert.That(t.text, Is.Not.EqualTo("OFF SCALE").And.Not.EqualTo("DESCENT").And.Not.EqualTo("CLIMB"));
            float travel = Const("FaaEngineInstrumentGraphic", "VsTravel");
            float maxY = float.MinValue, minY = float.MaxValue;
            foreach (var v in Symbols(g)) { maxY = Mathf.Max(maxY, v.position.y); minY = Mathf.Min(minY, v.position.y); }
            Assert.That(maxY, Is.GreaterThanOrEqualTo(travel + 8f), "Climb off the top of the scale draws the arrowhead at the top.");
            Assert.That(minY, Is.GreaterThan(-travel - 2f));
            Sample(g, -3000f, true, 0, false, 0, false);
            minY = float.MaxValue;
            foreach (var v in Symbols(g)) minY = Mathf.Min(minY, v.position.y);
            Assert.That(minY, Is.LessThanOrEqualTo(-travel - 8f));
            Sample(g, 0f, false, 0, false, 0, false);
            Assert.That(Text(g, "Value").text, Is.EqualTo("---"));
            maxY = float.MinValue;
            foreach (var v in Symbols(g)) maxY = Mathf.Max(maxY, Mathf.Abs(v.position.y));
            Assert.That(maxY, Is.LessThan(travel + 2f), "Invalid data removes the pointer instead of parking it at zero.");
        }

        [Test]
        public void TextBearingFlightModulesNeverRenderBelowTheLegibilityFloor()
        {
            var type = T("FaaNonConformalScaleTarget");
            float floor = Const("FaaHudStyle", "MinModuleScale");
            foreach (string id in new[] { "airspeed", "altitude", "torque", "nr", "vertical-speed", "glideslope", "localizer", "heading", "fma", "bank" })
            {
                var go = Track(new GameObject(id, typeof(RectTransform)));
                var target = Activator.CreateInstance(type, id, id, go.transform, .72f, 0f);
                var layout = type.GetField("Layout").GetValue(target);
                layout.GetType().GetField("scale").SetValue(layout, .4f); // e.g. an old saved profile
                type.GetMethod("Apply").Invoke(target, null);
                Assert.That(go.transform.localScale.x, Is.EqualTo(floor).Within(.0001f), id);
                Assert.That((float)layout.GetType().GetField("scale").GetValue(layout), Is.EqualTo(floor).Within(.0001f), id + " stored scale is repaired");
            }
            var radar = Track(new GameObject("traffic", typeof(RectTransform)));
            var panel = Activator.CreateInstance(type, "traffic", "Traffic", radar.transform, 1f, 0f);
            var panelLayout = type.GetField("Layout").GetValue(panel);
            panelLayout.GetType().GetField("scale").SetValue(panelLayout, .4f);
            type.GetMethod("Apply").Invoke(panel, null);
            Assert.That(radar.transform.localScale.x, Is.EqualTo(.4f).Within(.0001f), "Non-text panels keep the ergonomic minimum.");
        }

        private sealed class Layout
        {
            public RectTransform Root;
            public object Reflow;
            public Camera View;
            public readonly Dictionary<string, object> Targets = new();
            public readonly Dictionary<string, RectTransform> Rects = new();
        }

        private Layout BuildLayout(params string[] ids)
        {
            var layout = new Layout();
            var canvasGo = Track(new GameObject("Reflow canvas", typeof(RectTransform), typeof(Canvas)));
            canvasGo.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;
            layout.Root = (RectTransform)canvasGo.transform;
            layout.Root.sizeDelta = new Vector2(1920f, 1080f);
            var sizes = new Dictionary<string, Vector2>
            {
                { "airspeed", new(180, 84) }, { "altitude", new(180, 84) }, { "vertical-speed", new(140, 248) }, { "glideslope", new(100, 220) },
                { "torque", new(148, 200) }, { "nr", new(148, 200) }, { "localizer", new(320, 64) }, { "fma", new(600, 64) }
            };
            var type = T("FaaNonConformalScaleTarget");
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
            float x = -700f;
            foreach (string id in ids)
            {
                var go = new GameObject(id, typeof(RectTransform), typeof(Image));
                go.transform.SetParent(layout.Root, false);
                var rt = (RectTransform)go.transform;
                rt.sizeDelta = sizes[id]; rt.anchoredPosition = new Vector2(x += 90f, 300f);
                var target = Activator.CreateInstance(type, id, id, go.transform, 1f, 0f);
                list.Add(target); layout.Targets[id] = target; layout.Rects[id] = rt;
            }
            layout.Reflow = Activator.CreateInstance(T("FaaNonConformalReflow"), list);
            layout.View = Track(new GameObject("Reflow camera")).AddComponent<Camera>();
            return layout;
        }

        private static void Apply(Layout l) => l.Reflow.GetType().GetMethod("Apply").Invoke(l.Reflow, new object[] { l.View });

        private static Rect Bounds(Layout l, string id)
        {
            var target = l.Targets[id];
            var args = new object[] { l.Root, null };
            Assert.That((bool)target.GetType().GetMethod("TryLayoutBounds").Invoke(target, args), Is.True, id);
            return (Rect)args[1];
        }

        private static float Scale(Layout l, string id)
        {
            var layout = l.Targets[id].GetType().GetField("Layout").GetValue(l.Targets[id]);
            return (float)layout.GetType().GetField("scale").GetValue(layout);
        }

        private static void SetScale(Layout l, string id, float value)
        {
            var layout = l.Targets[id].GetType().GetField("Layout").GetValue(l.Targets[id]);
            layout.GetType().GetField("scale").SetValue(layout, value);
        }

        [Test]
        public void ColumnsAreSymmetricBasicTWithReadoutsOnTheWaterline()
        {
            var l = BuildLayout("airspeed", "altitude", "vertical-speed", "glideslope", "torque", "nr");
            Apply(l);
            Rect alt = Bounds(l, "altitude"), ias = Bounds(l, "airspeed"), vsi = Bounds(l, "vertical-speed"), gs = Bounds(l, "glideslope"),
                tq = Bounds(l, "torque"), nr = Bounds(l, "nr");
            float waterline = 540f - Const("FaaNonConformalReflow", "WaterlineFromTop");
            Assert.That(alt.center.y, Is.EqualTo(waterline).Within(.05f));
            Assert.That(ias.center.y, Is.EqualTo(alt.center.y).Within(.05f), "IAS and ALT are level.");
            Assert.That(ias.center.x, Is.EqualTo(-alt.center.x).Within(.05f), "IAS and ALT mirror about the attitude field.");
            Assert.That(ias.size.x, Is.EqualTo(alt.size.x).Within(.05f));
            float field = Const("FaaNonConformalReflow", "AttitudeFieldHalfWidth");
            Assert.That(ias.xMax, Is.LessThanOrEqualTo(-field));
            Assert.That(gs.xMin, Is.GreaterThanOrEqualTo(field), "G/S stays out of the attitude field.");
            Assert.That(gs.xMax, Is.LessThanOrEqualTo(alt.xMin), "Lane order ATT | G/S | ALT.");
            Assert.That(vsi.xMin, Is.GreaterThanOrEqualTo(alt.xMax), "VS immediately right of ALT.");
            Assert.That(vsi.xMin - alt.xMax, Is.LessThan(20f));
            Assert.That(tq.yMax, Is.LessThanOrEqualTo(ias.yMin + .05f), "TQ below IAS.");
            Assert.That(nr.yMax, Is.LessThanOrEqualTo(alt.yMin + .05f), "NR below ALT.");
            Assert.That(tq.center.x, Is.EqualTo(ias.center.x).Within(.05f));
            Assert.That(nr.center.x, Is.EqualTo(alt.center.x).Within(.05f));
            Assert.That(nr.yMin, Is.GreaterThanOrEqualTo(540f - Const("FaaNonConformalReflow", "SymbologyBottom") - .05f));
            Assert.That(vsi.xMax, Is.LessThanOrEqualTo(960f));
        }

        [Test]
        public void GlideslopeLaneStaysReservedSoAltitudeAndVsNeverMove()
        {
            var l = BuildLayout("airspeed", "altitude", "vertical-speed", "glideslope", "torque", "nr");
            Apply(l);
            Vector2 alt = Bounds(l, "altitude").center, vsi = Bounds(l, "vertical-speed").center, nr = Bounds(l, "nr").center;
            l.Rects["glideslope"].gameObject.SetActive(false);
            Apply(l);
            Assert.That(Vector2.Distance(Bounds(l, "altitude").center, alt), Is.LessThan(.05f));
            Assert.That(Vector2.Distance(Bounds(l, "vertical-speed").center, vsi), Is.LessThan(.05f));
            Assert.That(Vector2.Distance(Bounds(l, "nr").center, nr), Is.LessThan(.05f));
            l.Rects["glideslope"].gameObject.SetActive(true);
            Apply(l);
            Assert.That(Vector2.Distance(Bounds(l, "vertical-speed").center, vsi), Is.LessThan(.05f));
        }

        [Test]
        public void CentreLanesPinTheFmaAtTheTopAndLocAboveTheHeadingScale()
        {
            var l = BuildLayout("airspeed", "altitude", "localizer", "fma");
            Apply(l);
            Rect fma = Bounds(l, "fma"), loc = Bounds(l, "localizer");
            Assert.That(fma.yMax, Is.EqualTo(540f - Const("FaaNonConformalReflow", "FmaLaneTop")).Within(.05f));
            Assert.That(fma.center.x, Is.EqualTo(0f).Within(.05f));
            float headingTop = 540f - Const("FaaNonConformalReflow", "HeadingLaneTop");
            Assert.That(loc.yMin, Is.EqualTo(headingTop + Const("FaaNonConformalReflow", "LocLaneGap")).Within(.05f));
            Assert.That(loc.center.x, Is.EqualTo(0f).Within(.05f));
        }

        [Test]
        public void PairedModulesShareOneSize()
        {
            var l = BuildLayout("airspeed", "altitude", "torque", "nr");
            SetScale(l, "airspeed", .8f); SetScale(l, "altitude", 1.1f);
            Apply(l);
            Assert.That(Scale(l, "airspeed"), Is.EqualTo(1.1f).Within(.0001f), "A mismatched saved profile takes the larger size.");
            Assert.That(Scale(l, "altitude"), Is.EqualTo(1.1f).Within(.0001f));
            SetScale(l, "airspeed", .9f);
            Apply(l);
            Assert.That(Scale(l, "altitude"), Is.EqualTo(.9f).Within(.0001f), "The size the pilot changed last wins.");
            SetScale(l, "nr", 1.3f);
            Apply(l);
            Assert.That(Scale(l, "torque"), Is.EqualTo(1.3f).Within(.0001f));
        }

        private (Component readout, TMP_Text value, TMP_Text unit, GameObject parent) Readout(bool altitude, bool withElement)
        {
            var parent = Track(new GameObject(altitude ? "Altimeter" : "Airspeed Indicator", typeof(RectTransform)));
            if (withElement) parent.AddComponent(Type.GetType("HUDControl.Elements.AirspeedIndicatorElement, Assembly-CSharp", true));
            var holder = new GameObject("Readout", typeof(RectTransform));
            holder.transform.SetParent(parent.transform, false);
            var value = new GameObject("Value", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            value.transform.SetParent(holder.transform, false);
            var unit = new GameObject("Unit", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            unit.transform.SetParent(holder.transform, false);
            var type = T("FaaPrimaryFlightReadout");
            var readout = holder.AddComponent(type);
            type.GetMethod("Configure").Invoke(readout, new object[] { value, unit, altitude });
            return (readout, value, unit, parent);
        }

        [Test]
        public void ReadoutsAreMirrorImagesWithOneCaptionPatternAPlateAndNoLeadingZero()
        {
            var (altReadout, altValue, altUnit, _) = Readout(true, false);
            var (_, iasValue, iasUnit, iasParent) = Readout(false, true);
            Assert.That(altUnit.text, Is.EqualTo("ALT FT"));
            Assert.That(iasUnit.text, Is.EqualTo("IAS KT"));
            Assert.That(iasValue.rectTransform.sizeDelta, Is.EqualTo(altValue.rectTransform.sizeDelta), "Mirror-image boxes.");
            Assert.That(altUnit.fontSize, Is.GreaterThanOrEqualTo(Const("FaaDigitalColumnStyle", "LabelSize")));
            Assert.That(altUnit.color.a, Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "MinTextAlpha")));
            var plate = altReadout.transform.Find("FAA Readout Frame/Contrast Background");
            Assert.That(plate, Is.Not.Null);
            var image = plate.GetComponent<Image>();
            Assert.That(image.color.a, Is.InRange(.4f, .9f), "Translucent dark plate behind the digits.");
            Assert.That(image.raycastTarget, Is.False);
            var agl = altReadout.transform.Find("AGL Readout")?.GetComponent<TMP_Text>();
            Assert.That(agl, Is.Not.Null);
            Assert.That(agl.text, Is.Empty, "AGL is part-time: empty until valid data below 1,000 ft.");
            var element = iasParent.GetComponent(Type.GetType("HUDControl.Elements.AirspeedIndicatorElement, Assembly-CSharp"));
            Assert.That(element.GetType().GetProperty("DisplayFormat").GetValue(element), Is.EqualTo(Const2("FaaPrimaryFlightReadout", "AirspeedFormat")));
            Assert.That(string.Format((string)element.GetType().GetProperty("DisplayFormat").GetValue(element), 95), Does.Not.Contain("095"));
            if (altValue.font != null && altValue.font.material != null && altValue.font.material.HasProperty("_UnderlayColor"))
                Assert.That(altValue.fontSharedMaterial.IsKeywordEnabled("UNDERLAY_ON"), Is.True, "Readout digits carry the halo.");
            // M10: one plate covers the digits and the caption; the AGL row has its own plate, transparent while AGL is hidden.
            var plateRect = (RectTransform)plate;
            float plateBottom = plateRect.anchoredPosition.y - plateRect.sizeDelta.y * .5f;
            float captionBottom = -(Const("FaaPrimaryFlightReadout", "UnitOffset") + Const("FaaPrimaryFlightReadout", "UnitHeight") * .5f);
            Assert.That(plateBottom, Is.LessThanOrEqualTo(captionBottom), "The plate extends under the caption.");
            Assert.That(altUnit.fontSize * Const("FaaHudStyle", "MinModuleScale"), Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "Secondary") - .001f));
            var aglPlate = altReadout.transform.Find("AGL Contrast Background");
            Assert.That(aglPlate, Is.Not.Null);
            Assert.That(aglPlate.GetComponent<Image>().color.a, Is.LessThan(.01f));
            Assert.That(aglPlate.GetSiblingIndex(), Is.LessThan(agl.transform.GetSiblingIndex()), "The AGL plate draws behind its text.");
        }

        private static string Const2(string type, string field) => (string)T(type).GetField(field).GetValue(null);
        private static Type ElementType => Type.GetType("HUDControl.Elements.AirspeedIndicatorElement, Assembly-CSharp", true);

        private (GameObject indicator, Behaviour live, Component readout, TMP_Text value) SceneShapedAirspeed(string serializedFormat)
        {
            // Mirrors ExperimentScene: "Airspeed Indicator" holds the ENABLED element that writes the digits (serialized zero-padded
            // format); its child "Airspeed Readout" holds a DISABLED duplicate with no readout plus the presentation component.
            var indicator = Track(new GameObject("Airspeed Indicator", typeof(RectTransform)));
            var live = (Behaviour)indicator.AddComponent(ElementType);
            var holder = new GameObject("Airspeed Readout", typeof(RectTransform));
            holder.transform.SetParent(indicator.transform, false);
            ((Behaviour)holder.AddComponent(ElementType)).enabled = false;
            var value = new GameObject("AirspeedReadoutText", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            value.transform.SetParent(holder.transform, false);
            var unit = new GameObject("Units", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            unit.transform.SetParent(holder.transform, false);
            ElementType.GetMethod("ConfigureVisuals").Invoke(live, new object[] { null, value, null });
            var serialized = new UnityEditor.SerializedObject(live);
            serialized.FindProperty("displayFormat").stringValue = serializedFormat;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            var type = T("FaaPrimaryFlightReadout");
            var readout = holder.AddComponent(type);
            type.GetMethod("Configure").Invoke(readout, new object[] { value, unit, false });
            return (indicator, live, readout, value);
        }

        [Test]
        public void AirspeedBindsTheElementThatWritesTheVisibleDigitsAndShowsNoLeadingZero()
        {
            var (_, live, readout, value) = SceneShapedAirspeed("{0:000}");
            Assert.That(readout.GetType().GetProperty("BoundAirspeedElement").GetValue(readout), Is.SameAs(live),
                "The enabled element that writes the digits is bound, not the disabled duplicate next to the presentation.");
            Assert.That(ElementType.GetProperty("DisplayFormat").GetValue(live), Is.EqualTo(Const2("FaaPrimaryFlightReadout", "AirspeedFormat")));
            ElementType.GetMethod("SetAirspeedData").Invoke(live, new object[] { 17f, true });
            Assert.That(value.text, Is.EqualTo("<mspace=0.62em>17</mspace>"));
            Assert.That(value.text, Does.Not.Contain("017"));
            Assert.That(Static("FaaPrimaryFlightReadout", "FormatAirspeed", 17.4f, true), Is.EqualTo("17"));
            Assert.That(Static("FaaPrimaryFlightReadout", "FormatAirspeed", 4f, false), Is.EqualTo("---"));
        }

        [Test]
        public void ASerializedZeroPaddedFormatStillRendersWithoutLeadingZeros()
        {
            var go = Track(new GameObject("Airspeed element only", typeof(RectTransform)));
            var element = go.AddComponent(ElementType);
            var value = new GameObject("Digits", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            value.transform.SetParent(go.transform, false);
            ElementType.GetMethod("ConfigureVisuals").Invoke(element, new object[] { null, value, null });
            var serialized = new UnityEditor.SerializedObject(element);
            serialized.FindProperty("displayFormat").stringValue = "{0:000}";
            serialized.ApplyModifiedPropertiesWithoutUndo();
            ElementType.GetMethod("SetAirspeedData").Invoke(element, new object[] { 4f, true });
            Assert.That(value.text, Is.EqualTo("4"));
            ElementType.GetMethod("SetDisplayFormat").Invoke(element, new object[] { "{0:D3}" });
            Assert.That(ElementType.GetProperty("DisplayFormat").GetValue(element), Is.EqualTo("{0:0}"));
        }

        [TestCase("{0:000}", "{0:0}")]
        [TestCase("<mspace=0.62em>{0:000}</mspace>", "<mspace=0.62em>{0:0}</mspace>")]
        [TestCase("{0:00}", "{0:0}")]
        [TestCase("{0:D3}", "{0:0}")]
        [TestCase("{0:F0}", "{0:F0}")]
        [TestCase("{0:#,##0}", "{0:#,##0}")]
        [TestCase("", "{0:0}")]
        [TestCase("{0:000", "{0:0}")]
        public void AirspeedFormatsLoseTheirZeroPadding(string format, string expected) =>
            Assert.That(ElementType.GetMethod("WithoutLeadingZeros").Invoke(null, new object[] { format }), Is.EqualTo(expected));

        [TestCase(150f, false, false)]
        [TestCase(156f, false, true)]
        [TestCase(154f, true, true)]
        [TestCase(152.9f, true, false)]
        [TestCase(float.NaN, true, false)]
        public void VneExceedanceIsHeldWithASmallHysteresis(float knots, bool was, bool expected) =>
            Assert.That(ElementType.GetMethod("ClassifyVne").Invoke(null, new object[] { knots, was }), Is.EqualTo(expected));

        [Test]
        public void IasAboveVneIsRedWithAFullAlertBoxInsideTheFrame()
        {
            var (_, live, readout, value) = SceneShapedAirspeed("{0:0}");
            var lateUpdate = readout.GetType().GetMethod("LateUpdate", BindingFlags.NonPublic | BindingFlags.Instance);
            ElementType.GetMethod("SetAirspeedData").Invoke(live, new object[] { 170f, true });
            lateUpdate.Invoke(readout, null);
            Assert.That((bool)readout.GetType().GetProperty("VneWarning").GetValue(readout), Is.True);
            Assert.That(Near(value.color, StyleColor("Red")), Is.True, "Red digits above Vne.");
            var frame = (RectTransform)readout.transform.Find("FAA Readout Frame");
            Assert.That(Near(frame.Find("Left").GetComponent<Image>().color, StyleColor("Red")), Is.True, "Brackets turn red (steady).");
            var bounds = new Rect(-frame.sizeDelta * .5f, frame.sizeDelta);
            foreach (string edge in new[] { "Alert Top", "Alert Bottom", "Alert Left", "Alert Right" })
            {
                var rt = (RectTransform)frame.Find(edge);
                Assert.That(rt, Is.Not.Null, edge);
                var r = new Rect(rt.anchoredPosition - rt.sizeDelta * .5f, rt.sizeDelta);
                Assert.That(r.xMin >= bounds.xMin - .01f && r.xMax <= bounds.xMax + .01f && r.yMin >= bounds.yMin - .01f && r.yMax <= bounds.yMax + .01f,
                    Is.True, edge + " stays inside the frame so the layout bounds never change");
                Assert.That(Mathf.Min(r.width, r.height), Is.GreaterThanOrEqualTo(2f), edge + " is heavier than the brackets");
            }
            ElementType.GetMethod("ClearExternalData").Invoke(live, null);
            lateUpdate.Invoke(readout, null);
            Assert.That((bool)readout.GetType().GetProperty("VneWarning").GetValue(readout), Is.False);
            foreach (string edge in new[] { "Alert Top", "Alert Bottom", "Alert Left", "Alert Right" })
                Assert.That(frame.Find(edge).GetComponent<Image>().color.a, Is.LessThan(.01f), edge);
        }

        [TestCase(950f, true, false, true)]
        [TestCase(1050f, true, false, false)]
        [TestCase(1050f, true, true, true)]
        [TestCase(1150f, true, true, false)]
        [TestCase(300f, false, true, false)]
        [TestCase(float.NaN, true, true, false)]
        [TestCase(-5f, true, false, false)]
        public void AglAppearsOnlyAtLowHeightWithHysteresis(float feet, bool valid, bool wasShown, bool expected) =>
            Assert.That(Static("FaaPrimaryFlightReadout", "ShouldShowAgl", feet, valid, wasShown), Is.EqualTo(expected));

        [TestCase(147f, 145)]
        [TestCase(452f, 450)]
        [TestCase(0f, 0)]
        public void AglRoundsSoTheLastDigitDoesNotFlicker(float feet, int expected) =>
            Assert.That(Static("FaaPrimaryFlightReadout", "RoundAgl", feet), Is.EqualTo(expected));

        [Test]
        public void ValueColoursFollowTheAlertPhilosophyAndDashesStayLegible()
        {
            Color normal = new Color(.2f, 1f, .2f, 1f);
            Color invalid = (Color)Static("FaaDigitalColumnStyle", "ValueColor", Exceedance("Invalid"), normal);
            Assert.That(invalid.a, Is.GreaterThanOrEqualTo(Const("FaaHudStyle", "MinTextAlpha")));
            Assert.That(Near((Color32)(Color)Static("FaaDigitalColumnStyle", "ValueColor", Exceedance("Caution"), normal), StyleColor("Amber")), Is.True);
            Assert.That(Near((Color32)(Color)Static("FaaDigitalColumnStyle", "ValueColor", Exceedance("Warning"), normal), StyleColor("Red")), Is.True);
            Assert.That(Near((Color32)(Color)Static("FaaDigitalColumnStyle", "ValueColor", Exceedance("Normal"), normal), normal), Is.True);
        }
    }
}
