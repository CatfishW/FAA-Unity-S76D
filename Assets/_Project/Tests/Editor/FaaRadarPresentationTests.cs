using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public class FaaRadarPresentationTests
    {
        private static Type Presentation => Type.GetType("FAA.Customization.FaaRadarPresentation, Assembly-CSharp");

        [Test]
        public void ContentOpacity_HasARealNativeCanvasGroup()
        {
            var go = new GameObject("Radar Content Test", typeof(RectTransform));
            try
            {
                var group = (CanvasGroup)Presentation.GetMethod("EnsureContentGroup").Invoke(null, new object[] { go });
                Assert.That(go.GetComponent<CanvasGroup>() != null, Is.True);
                group.alpha = 0f;
                Assert.That(go.GetComponent<CanvasGroup>().alpha, Is.Zero);
                Assert.That(Presentation.GetMethod("EnsureContentGroup").Invoke(null, new object[] { go }), Is.SameAs(group));
                Assert.That(go.GetComponents<CanvasGroup>().Length, Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(220f, 220f)]
        [TestCase(360f, 360f)]
        [TestCase(560f, 560f)]
        [TestCase(360f, 400f)]
        public void CompactChart_FillsResizedCircularScope(float width, float height)
        {
            var type = Type.GetType("TrafficRadar.TrafficRadarDisplay, TrafficRadar");
            var result = (Vector2)type.GetMethod("CalculateCompactChartSize").Invoke(null, new object[] { new Vector2(width, height) });
            Assert.That(result, Is.EqualTo(Vector2.one * Mathf.Min(width, height)));
        }

        [TestCase(false, true, true, true, false, false, "DisplayOff")]
        [TestCase(false, true, false, false, false, false, "DisplayOff")]
        [TestCase(true, false, false, false, false, false, "Preview")]
        [TestCase(true, true, false, false, false, false, "Waiting")]
        [TestCase(true, true, false, true, false, false, "Stale")]
        [TestCase(true, true, false, true, true, true, "Stale")]
        [TestCase(true, true, true, true, true, false, "Standby")]
        [TestCase(true, true, true, true, false, true, "RadarOff")]
        [TestCase(true, true, true, true, false, false, "Live")]
        public void Status_DistinguishesVisibilityFreshnessAndPower(bool on, bool playing,
            bool fresh, bool previous, bool standby, bool powerOff, string expected)
        {
            Assert.That(Presentation.GetMethod("ResolveState").Invoke(null,
                new object[] { on, playing, fresh, previous, standby, powerOff }).ToString(), Is.EqualTo(expected));
        }

        [TestCase(160f, 1, 40f)]
        [TestCase(160f, 2, 80f)]
        [TestCase(160f, 3, 120f)]
        [TestCase(40f, 2, 20f)]
        [TestCase(5f, 1, 1.25f)]
        public void WeatherRanges_TrackSelectedRange(float range, int ring, float expected)
        {
            var type = Type.GetType("WeatherRadar.XPlaneWeatherRadarGeometry, WeatherRadar");
            Assert.That(type.GetMethod("RangeAtRing").Invoke(null, new object[] { range, ring, 4 }), Is.EqualTo(expected));
        }

        [Test]
        public void WeatherSector_EntireArcFitsTexture()
        {
            var type = Type.GetType("WeatherRadar.XPlaneWeatherRadarGeometry, WeatherRadar");
            float aspect = (float)type.GetField("Aspect").GetRawConstantValue();
            float radius = (float)type.GetField("Radius").GetRawConstantValue();
            float origin = (float)type.GetField("OriginHeight").GetRawConstantValue();
            float halfAngle = (float)type.GetField("HalfAngle").GetRawConstantValue();
            Assert.That(radius + origin, Is.LessThan(1f));
            Assert.That(Mathf.Sin(halfAngle * Mathf.Deg2Rad) * radius, Is.LessThan(aspect * .5f));
        }

        [TestCase(220f)]
        [TestCase(360f)]
        [TestCase(800f)]
        public void RadarChrome_OnlyTheDisplaySegmentsInterceptClicksAndFooterDoesNotOverlap(float width)
        {
            var go = new GameObject("Radar Chrome Test", typeof(RectTransform));
            try
            {
                var rect = go.GetComponent<RectTransform>();
                rect.sizeDelta = new Vector2(width, width);
                var presentation = go.AddComponent(Presentation);
                var on = rect.Find("Radar Status Header/Display Switch/On").GetComponent<Button>();
                var off = rect.Find("Radar Status Header/Display Switch/Off").GetComponent<Button>();
                // Each option is its own action; selecting the active option changes nothing.
                off.onClick.Invoke();
                Assert.That(Presentation.GetProperty("IsDisplayOn").GetValue(presentation), Is.False);
                off.onClick.Invoke();
                Assert.That(Presentation.GetProperty("IsDisplayOn").GetValue(presentation), Is.False);
                on.onClick.Invoke();
                Assert.That(Presentation.GetProperty("IsDisplayOn").GetValue(presentation), Is.True);
                on.onClick.Invoke();
                Assert.That(Presentation.GetProperty("IsDisplayOn").GetValue(presentation), Is.True);
                foreach (var graphic in go.GetComponentsInChildren<Graphic>(true))
                    Assert.That(graphic.raycastTarget, Is.EqualTo(graphic == on.targetGraphic || graphic == off.targetGraphic), graphic.name);
                var footer = rect.Find("Radar Status Footer");
                var range = footer.Find("Range") as RectTransform;
                var mode = footer.Find("Mode") as RectTransform;
                float footerWidth = ((RectTransform)footer).rect.width;
                Assert.That(range.rect.width + mode.rect.width + 8f, Is.LessThanOrEqualTo(footerWidth));
                var header = (RectTransform)rect.Find("Radar Status Header");
                var title = (RectTransform)header.Find("Instrument");
                var displaySwitch = (RectTransform)header.Find("Display Switch");
                Assert.That(10f + title.rect.width + displaySwitch.rect.width + 6f, Is.LessThanOrEqualTo(header.rect.width),
                    "The title and the DISPLAY selector never overlap.");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void DisplaySelector_SeparatesTheFunctionNameFromItsState()
        {
            var go = new GameObject("Radar Display Selector Test", typeof(RectTransform));
            try
            {
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 220f);
                var presentation = go.AddComponent(Presentation);
                var selector = go.transform.Find("Radar Status Header/Display Switch");
                var caption = selector.Find("Caption").GetComponent<TMPro.TMP_Text>();
                var on = selector.Find("On");
                var off = selector.Find("Off");
                Assert.That(caption.text, Is.EqualTo("DISPLAY"), "The caption names the function and never changes with state.");
                Assert.That(on.Find("Label").GetComponent<TMPro.TMP_Text>().text, Is.EqualTo("ON"));
                Assert.That(off.Find("Label").GetComponent<TMPro.TMP_Text>().text, Is.EqualTo("OFF"));
                Assert.That(on.GetComponent<Image>().color, Is.Not.EqualTo(off.GetComponent<Image>().color),
                    "The active option is shown by fill, not by rewording the control.");
                Color onFill = on.GetComponent<Image>().color;
                Presentation.GetMethod("SetDisplayEnabled").Invoke(presentation, new object[] { false });
                Assert.That(caption.text, Is.EqualTo("DISPLAY"));
                Assert.That(off.GetComponent<Image>().color, Is.EqualTo(onFill), "Selecting OFF moves the fill to OFF.");
                foreach (var label in new[] { caption, on.Find("Label").GetComponent<TMPro.TMP_Text>(), off.Find("Label").GetComponent<TMPro.TMP_Text>() })
                {
                    Assert.That(label.fontSize, Is.GreaterThanOrEqualTo(15f));
                    if (label.font == null) continue;
                    Assert.That(label.GetPreferredValues(label.text).x, Is.LessThanOrEqualTo(label.rectTransform.rect.width + .5f), label.text);
                }
                var title = go.transform.Find("Radar Status Header/Instrument").GetComponent<TMPro.TMP_Text>();
                if (title.font != null)
                    foreach (string name in new[] { "WEATHER", "TRAFFIC" })
                        Assert.That(title.GetPreferredValues(name).x, Is.LessThanOrEqualTo(title.rectTransform.rect.width + .5f), name);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(220f)]
        [TestCase(360f)]
        public void RadarChrome_EveryTextMeetsTheFaaMinimumAndTheFooterHasAPlate(float width)
        {
            var go = new GameObject("Radar Legibility Test", typeof(RectTransform));
            try
            {
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(width, width);
                var presentation = go.AddComponent(Presentation);
                Presentation.GetMethod("SetDisplayEnabled").Invoke(presentation, new object[] { false });
                foreach (var text in go.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (!text.gameObject.activeSelf) continue;
                    Assert.That(text.fontSize, Is.GreaterThanOrEqualTo(15f), text.name);
                }
                var footer = go.transform.Find("Radar Status Footer");
                Assert.That(footer.Find("Mode").GetComponent<TMPro.TMP_Text>().overflowMode,
                    Is.Not.EqualTo(TMPro.TextOverflowModes.Ellipsis), "Footer parts are dropped by priority, never cut off with an ellipsis.");
                var plate = footer.GetComponent<Image>();
                Assert.That(plate, Is.Not.Null, "The footer must not float over terrain without a plate.");
                Assert.That(plate.color.a, Is.GreaterThanOrEqualTo(.85f));
                Assert.That(plate.raycastTarget, Is.False);
                Assert.That(go.transform.Find("Radar Status Header/Display Switch/Caption").GetComponent<TMPro.TMP_Text>().text,
                    Is.EqualTo("DISPLAY"), "A constant caption names the function; the filled segment shows its state.");
                foreach (var text in go.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    Assert.That(text.overflowMode, Is.Not.EqualTo(TMPro.TextOverflowModes.Ellipsis), text.name);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void Footer_DropsLowPriorityPartsInsteadOfTruncating()
        {
            var compose = Presentation.GetMethod("ComposeFooter");
            var measure = Presentation.GetMethod("EstimateTextWidth");
            var parts = new[] { "TRK UP", "ABV", "3 NO TAG", "12 TFC" };
            foreach (float width in new[] { 60f, 100f, 144f, 400f })
            {
                string text = (string)compose.Invoke(null, new object[] { parts, 4, width, 15f });
                Assert.That(text, Does.StartWith("TRK UP"), "Orientation is always shown.");
                if (text != "TRK UP")
                    Assert.That((float)measure.Invoke(null, new object[] { text, 15f }), Is.LessThanOrEqualTo(width));
            }
            Assert.That(compose.Invoke(null, new object[] { parts, 4, 400f, 15f }), Is.EqualTo("TRK UP · ABV · 3 NO TAG · 12 TFC"));
            Assert.That(compose.Invoke(null, new object[] { new[] { "TRK UP", null, null, "4 TFC" }, 4, 400f, 15f }), Is.EqualTo("TRK UP · 4 TFC"));
        }

        [TestCase(80f, "80 NM")]
        [TestCase(160f, "160 NM")]
        [TestCase(320f, "320 NM")]
        public void WeatherFooter_RangeAlwaysCarriesItsUnitAndIsNeverTruncated(float rangeNm, string expected)
        {
            Assert.That(Presentation.GetMethod("FormatRange").Invoke(null, new object[] { rangeNm }), Is.EqualTo(expected));
            foreach (float rootWidth in new[] { 160f, 220f, 296f })
            {
                var go = new GameObject("Weather Footer Test", typeof(RectTransform));
                try
                {
                    go.GetComponent<RectTransform>().sizeDelta = new Vector2(rootWidth, rootWidth);
                    go.AddComponent(Presentation);
                    var footer = (RectTransform)go.transform.Find("Radar Status Footer");
                    var range = footer.Find("Range").GetComponent<TMPro.TMP_Text>();
                    var detail = footer.Find("Mode").GetComponent<TMPro.TMP_Text>();
                    Assert.That(range.overflowMode, Is.Not.EqualTo(TMPro.TextOverflowModes.Ellipsis), "Numeric fields are never ellipsis-cut.");
                    Assert.That(range.fontSize, Is.GreaterThanOrEqualTo(15f));
                    if (range.font == null) Assert.Inconclusive("No TMP default font asset to measure with.");
                    float needed = range.GetPreferredValues(expected).x;
                    Assert.That(needed, Is.GreaterThan(1f));
                    Assert.That(needed, Is.LessThanOrEqualTo(range.rectTransform.rect.width), expected + " at root " + rootWidth);
                    // The rest of the footer still fits a full weather detail line beside it.
                    Assert.That(detail.GetPreferredValues("WX+T · TILT -2.5°").x, Is.LessThanOrEqualTo(detail.rectTransform.rect.width));
                    Assert.That(range.rectTransform.rect.width + detail.rectTransform.rect.width + 24f, Is.LessThanOrEqualTo(footer.rect.width + .5f));
                }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            }
        }

        [Test]
        public void TrafficFooter_AnnunciatesTheBandAndCountsTrafficInPlainWords()
        {
            Assert.That(Presentation.GetMethod("TrafficCountText").Invoke(null, new object[] { 0 }), Is.EqualTo("NO TFC"));
            Assert.That(Presentation.GetMethod("TrafficCountText").Invoke(null, new object[] { 1 }), Is.EqualTo("1 TFC"));
            Assert.That(Presentation.GetMethod("TrafficCountText").Invoke(null, new object[] { 12 }), Is.EqualTo("12 TFC"));
            var bands = Type.GetType("TrafficRadar.TrafficAltitudeBands, TrafficRadar", true);
            var band = Type.GetType("TrafficRadar.TrafficAltitudeBand, TrafficRadar", true);
            var footerLabel = bands.GetMethod("FooterLabel");
            Assert.That(footerLabel.Invoke(null, new[] { Enum.Parse(band, "Normal") }), Is.EqualTo("NORM ±2700 FT"));
            Assert.That(footerLabel.Invoke(null, new[] { Enum.Parse(band, "Above") }), Is.EqualTo("ABOVE +9900 FT"));
            Assert.That(footerLabel.Invoke(null, new[] { Enum.Parse(band, "Below") }), Is.EqualTo("BELOW -9900 FT"));
            Assert.That(footerLabel.Invoke(null, new[] { Enum.Parse(band, "All") }), Is.EqualTo("ALL ALT"));

            var go = new GameObject("Traffic Footer Test", typeof(RectTransform));
            try
            {
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(220f, 220f);
                var presentation = go.AddComponent(Presentation);
                var kind = Type.GetType("FAA.Customization.FaaRadarKind, Assembly-CSharp", true);
                Presentation.GetMethod("Configure").Invoke(presentation, new[] { Enum.Parse(kind, "Traffic"), null });
                var footer = (RectTransform)go.transform.Find("Radar Status Footer");
                var bandLabel = footer.Find("Altitude Band").GetComponent<TMPro.TMP_Text>();
                var range = footer.Find("Range").GetComponent<TMPro.TMP_Text>();
                var orientation = footer.Find("Mode").GetComponent<TMPro.TMP_Text>();
                Assert.That(bandLabel.gameObject.activeSelf, Is.True);
                Assert.That(bandLabel.text, Is.EqualTo("NORM ±2700 FT"), "The default band is annunciated, not only unusual ones.");
                Assert.That(orientation.text, Is.EqualTo("N UP").Or.EqualTo("TRK UP"));
                Assert.That(orientation.text, Does.Not.Contain("TGT"));
                Assert.That(range.text, Does.EndWith(" NM"));
                Assert.That(bandLabel.rectTransform.anchoredPosition.y, Is.LessThan(range.rectTransform.anchoredPosition.y),
                    "Band and count sit on their own row under range and orientation.");
                Assert.That(footer.rect.height, Is.GreaterThanOrEqualTo(bandLabel.rectTransform.rect.height + range.rectTransform.rect.height));
                if (bandLabel.font != null)
                    foreach (string text in new[] { "NORM ±2700 FT", "ABOVE +9900 FT", "BELOW -9900 FT" })
                        Assert.That(bandLabel.GetPreferredValues(text).x + bandLabel.GetPreferredValues("12 TFC").x + 8f,
                            Is.LessThanOrEqualTo(footer.rect.width - 16f), text);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void WeatherFocusFrame_StaysBetweenTheHeaderAndFooterPlates()
        {
            var surface = Type.GetType("FAA.Customization.FaaRadarInteractionSurface, Assembly-CSharp", true);
            var fit = surface.GetMethod("FitFocusFrame");
            float inset = (float)surface.GetField("BracketInset").GetRawConstantValue();
            var picture = new Rect(-110f, -40f, 220f, 156f);
            foreach (float gap in new[] { 6f, 7.5f, 12f })
            {
                float footerTop = picture.yMin - gap, headerBottom = picture.yMax + gap;
                var frame = (Rect)fit.Invoke(null, new object[] { picture, headerBottom, footerTop });
                Assert.That(frame.yMin, Is.GreaterThan(footerTop), "The frame never reaches into the footer plate.");
                Assert.That(frame.yMin + inset - 1f, Is.GreaterThan(footerTop), "The bottom bracket strokes stay above the range text.");
                Assert.That(frame.yMax, Is.LessThan(headerBottom), "The frame never reaches into the header plate.");
                Assert.That(frame.xMin, Is.LessThan(picture.xMin));
                Assert.That(frame.xMax, Is.GreaterThan(picture.xMax));
            }
            var free = (Rect)fit.Invoke(null, new object[] { picture, float.NaN, float.NaN });
            Assert.That(free.yMin, Is.LessThan(picture.yMin), "Without plates the frame is the picture plus padding.");
        }

        [TestCase(true, false, true, true, true, true, false, "TRAINING WX")]
        [TestCase(true, false, true, true, true, false, true, "TRAINING WX · STALE")]
        [TestCase(true, false, false, true, true, true, false, "X-PLANE WX · LIVE")]
        [TestCase(false, true, false, false, true, false, true, "FALLBACK DATA · STALE")]
        [TestCase(false, true, false, false, true, true, true, "FALLBACK DATA")]
        [TestCase(true, true, false, true, true, true, true, "FALLBACK DATA")]
        [TestCase(false, false, false, false, true, false, false, "X-PLANE · WAITING")]
        [TestCase(false, false, false, false, false, false, false, "X-PLANE · PREVIEW")]
        public void SourceLine_MatchesTheActualProvenance(bool weather, bool fallback, bool training, bool procedural,
            bool playing, bool fresh, bool previous, string expected)
        {
            Assert.That(Presentation.GetMethod("SourceText").Invoke(null,
                new object[] { weather, fallback, training, procedural, playing, fresh, previous }), Is.EqualTo(expected));
            bool caution = (bool)Presentation.GetMethod("SourceIsCaution").Invoke(null, new object[] { fallback, training, playing, fresh });
            Assert.That(caution, Is.EqualTo(fallback || training || (playing && !fresh)));
            if (fallback || training) Assert.That(expected, Does.Not.Contain("LIVE"), "LIVE is reserved for the local X-Plane simulator.");
        }

        [Test]
        public void WeatherFace_LabelsOnlyHalfAndFullRangeAboveTheFloor()
        {
            var go = new GameObject("Weather Face Labels", typeof(RectTransform));
            try
            {
                var type = Type.GetType("WeatherRadar.XPlaneWeatherRadarFace, WeatherRadar");
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(204f, 144f);
                var face = go.AddComponent(type);
                type.GetMethod("Configure").Invoke(face, new object[] { null });
                var labels = go.GetComponentsInChildren<TMPro.TMP_Text>(true);
                Assert.That(labels.Length, Is.EqualTo(2));
                Assert.That(labels[0].text, Is.EqualTo("80"));
                Assert.That(labels[1].text, Is.EqualTo("160"));
                for (int i = 0; i < labels.Length; i++)
                {
                    var label = labels[i];
                    Assert.That(label.fontSize, Is.GreaterThanOrEqualTo(15f));
                    Assert.That(label.outlineWidth, Is.GreaterThan(0f), "A halo keeps range labels legible over returns.");
                    var plate = (Rect)type.GetMethod("LabelPlate").Invoke(face, new object[] { i });
                    Assert.That(plate.Contains(label.rectTransform.anchoredPosition), Is.True, "Each range label sits on a knockout plate.");
                    Assert.That(plate.height, Is.GreaterThanOrEqualTo(label.fontSize));
                    if (label.font != null)
                        Assert.That(plate.width, Is.GreaterThanOrEqualTo(label.GetPreferredValues(label.text).x), label.text);
                    Assert.That(label.rectTransform.anchoredPosition.x, Is.LessThan(-20f), "Labels sit beside the left radial, off boresight.");
                    Assert.That(Mathf.Abs(label.rectTransform.anchoredPosition.x) + 15f, Is.LessThanOrEqualTo(102f), "Labels stay inside the sector.");
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void Readability_ReferenceCanvasIsUnscaledAndASmallWorldPanelIsEnlarged()
        {
            var style = Type.GetType("FAA.Customization.FaaRadarVisualStyle, Assembly-CSharp", true);
            var measure = style.GetMethod("ReadabilityScale");
            var cameraGo = new GameObject("Readability Camera", typeof(Camera));
            var canvasGo = new GameObject("Readability Canvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var camera = cameraGo.GetComponent<Camera>();
                camera.fieldOfView = 60f;
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                var panel = new GameObject("Panel", typeof(RectTransform)).GetComponent<RectTransform>();
                panel.SetParent(canvasGo.transform, false);
                // Audited panel: 0.00181 m per unit at 1.67 m (about one reference px per unit).
                canvasGo.transform.position = new Vector3(0, 0, 1.67f);
                canvasGo.transform.localScale = Vector3.one * .00181f;
                Assert.That((float)measure.Invoke(null, new object[] { panel }), Is.EqualTo(1f).Within(.05f));
                // Same physical radar with a 296-unit root: each unit is 26% smaller, so text is enlarged.
                canvasGo.transform.localScale = Vector3.one * (.42f * .95f / 296f);
                Assert.That((float)measure.Invoke(null, new object[] { panel }), Is.GreaterThan(1.25f));
                // Zooming the view in to inspect the panel must magnify the text, not shrink it back to the floor.
                float unzoomed = (float)measure.Invoke(null, new object[] { panel });
                camera.fieldOfView = 30f;
                Assert.That((float)measure.Invoke(null, new object[] { panel }), Is.EqualTo(unzoomed).Within(.001f));
                camera.fieldOfView = 60f;
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                Assert.That((float)measure.Invoke(null, new object[] { panel }), Is.GreaterThanOrEqualTo(1f));
            }
            finally { UnityEngine.Object.DestroyImmediate(canvasGo); UnityEngine.Object.DestroyImmediate(cameraGo); }
        }

        [Test]
        public void WeatherFace_GeneratesCrispGeometryWithoutRasterText()
        {
            var go = new GameObject("Weather Face Test", typeof(RectTransform));
            try
            {
                var type = Type.GetType("WeatherRadar.XPlaneWeatherRadarFace, WeatherRadar");
                go.GetComponent<RectTransform>().sizeDelta = new Vector2(320f, 226f);
                var face = go.AddComponent(type);
                Assert.That(go.GetComponent<CanvasRenderer>(), Is.Not.Null,
                    "A vector graphic needs a CanvasRenderer, not only generated vertices.");
                using var vertices = new VertexHelper();
                type.GetMethod("OnPopulateMesh", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(VertexHelper) }, null)
                    .Invoke(face, new object[] { vertices });
                Assert.That(vertices.currentVertCount, Is.GreaterThan(1000));
                Assert.That(((Graphic)face).raycastTarget, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }
    }
}
