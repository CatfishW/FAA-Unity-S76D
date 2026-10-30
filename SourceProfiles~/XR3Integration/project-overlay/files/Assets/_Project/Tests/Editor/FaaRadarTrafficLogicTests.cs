using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    /// <summary>Traffic/weather radar data rules: auto range, TCAS altitude band, priority truncation,
    /// no fabricated advisories, chart coverage and the shared weather palette.</summary>
    public class FaaRadarTrafficLogicTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType(name, true);
        private static Type Controller => T("TrafficRadar.Core.TrafficRadarController, TrafficRadar");
        private static Type Threat => T("TrafficRadar.ThreatLevel, TrafficRadar");
        private static Type Band => T("TrafficRadar.TrafficAltitudeBand, TrafficRadar");
        private static Type Bands => T("TrafficRadar.TrafficAltitudeBands, TrafficRadar");
        private static Type Processor => T("TrafficRadar.Core.RadarDataProcessor, TrafficRadar");
        private static Type State => T("TrafficRadar.Core.AircraftState, TrafficRadar");
        private static Type OwnShip => T("TrafficRadar.Core.OwnShipPosition, TrafficRadar");
        private static object Level(string name) => Enum.Parse(Threat, name);

        private static float AutoRange(float[] distances, string[] threats, float min = 5f, float max = 40f, int nearest = 4)
        {
            var threatList = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Threat));
            foreach (var t in threats) threatList.Add(Level(t));
            var options = new List<float> { 2f, 5f, 10f, 20f, 40f, 80f };
            return (float)Controller.GetMethod("SelectAutoRange").Invoke(null,
                new object[] { new List<float>(distances), threatList, options, min, max, nearest });
        }

        [Test]
        public void AutoRange_FramesTheNearestTrafficInsteadOfRatchetingTo150()
        {
            var far = new[] { 5.3f, 6.1f, 7.4f, 8.8f, 10.2f, 12f, 14f, 16.5f, 18f, 20.4f, 23f, 25.7f };
            var none = new string[far.Length];
            for (int i = 0; i < none.Length; i++) none[i] = "OtherTraffic";
            Assert.That(AutoRange(far, none), Is.EqualTo(20f), "4th nearest 8.8 NM x 1.15 -> 20 NM, never 150.");
            Assert.That(AutoRange(new[] { 3f, 4f, 5f, 6f, 7.9f },
                new[] { "Proximate", "OtherTraffic", "OtherTraffic", "OtherTraffic", "OtherTraffic" }), Is.EqualTo(10f));
            Assert.That(AutoRange(new[] { 60f, 70f }, new[] { "OtherTraffic", "OtherTraffic" }), Is.EqualTo(40f),
                "Far traffic is capped at the auto-range maximum.");
            Assert.That(AutoRange(new float[0], new string[0]), Is.EqualTo(10f));
            Assert.That(AutoRange(new[] { .4f }, new[] { "OtherTraffic" }), Is.EqualTo(5f), "Auto range never goes below its minimum.");
        }

        [Test]
        public void AutoRange_ExpandsAtOnceAndShrinksOnlyAfterTheHoldTime()
        {
            var governorType = T("TrafficRadar.Core.TrafficAutoRange, TrafficRadar");
            var governor = Activator.CreateInstance(governorType);
            var step = governorType.GetMethod("Step");
            float Step(float current, float proposed, float now) => (float)step.Invoke(governor, new object[] { current, proposed, now, 5f });
            Assert.That(Step(10f, 40f, 0f), Is.EqualTo(40f), "Expansion is immediate.");
            Assert.That(Step(40f, 10f, 1f), Is.EqualTo(40f));
            Assert.That(Step(40f, 10f, 5.5f), Is.EqualTo(40f), "Still inside the 5 s hold.");
            Assert.That(Step(40f, 10f, 6.1f), Is.EqualTo(10f));
            Assert.That(Step(10f, 5f, 7f), Is.EqualTo(10f), "A new shrink restarts the hold.");
            Assert.That(Step(10f, 20f, 8f), Is.EqualTo(20f));
            Assert.That(Step(20f, 5f, 9f), Is.EqualTo(20f), "Pumping up resets the shrink timer.");
        }

        [Test]
        public void RangeList_IsOneTcasStyleListAndStepsFromIntermediateZoom()
        {
            var go = new GameObject("Range list test"); go.SetActive(false);
            try
            {
                var controller = go.AddComponent(Controller);
                var options = (IList)Controller.GetProperty("RangeOptionsNM").GetValue(controller);
                CollectionAssert.AreEqual(new[] { 2f, 5f, 10f, 20f, 40f, 80f }, options);
                var next = Controller.GetMethod("NextRangeOption");
                Assert.That(next.Invoke(controller, new object[] { 13f, 1 }), Is.EqualTo(20f));
                Assert.That(next.Invoke(controller, new object[] { 13f, -1 }), Is.EqualTo(10f));
                Assert.That(next.Invoke(controller, new object[] { 80f, 1 }), Is.EqualTo(80f));
                Assert.That(next.Invoke(controller, new object[] { 2f, -1 }), Is.EqualTo(2f));
                Controller.GetProperty("RangeNM").SetValue(controller, 150f);
                Assert.That(Controller.GetProperty("RangeNM").GetValue(controller), Is.EqualTo(80f), "The legacy 150 NM range is gone.");
            }
            finally { UnityEngine.Object.DestroyImmediate(go); }
        }

        [TestCase(2700f, "Normal", true)]
        [TestCase(2701f, "Normal", false)]
        [TestCase(-2701f, "Normal", false)]
        [TestCase(9000f, "Above", true)]
        [TestCase(-9000f, "Above", false)]
        [TestCase(-9000f, "Below", true)]
        [TestCase(9901f, "Above", false)]
        [TestCase(20000f, "All", true)]
        [TestCase(float.NaN, "Normal", true)]
        public void AltitudeBand_FollowsTheTcasConvention(float relativeFeet, string band, bool expected) =>
            Assert.That(Bands.GetMethod("WithinBand").Invoke(null, new[] { (object)relativeFeet, Enum.Parse(Band, band) }), Is.EqualTo(expected));

        [Test]
        public void Classifier_NeverFabricatesTrafficOrResolutionAdvisories()
        {
            var thresholds = Activator.CreateInstance(T("TrafficRadar.ThreatThresholds, TrafficRadar"));
            var classify = thresholds.GetType().GetMethod("DetermineThreatLevel");
            Assert.That(classify.Invoke(thresholds, new object[] { .3f, 50f }).ToString(), Is.EqualTo("Proximate"),
                "Without TCAS advisory data, close traffic is proximate (filled diamond), never amber/red.");
            Assert.That(classify.Invoke(thresholds, new object[] { 2f, 400f }).ToString(), Is.EqualTo("Proximate"));
            Assert.That(classify.Invoke(thresholds, new object[] { 8f, 400f }).ToString(), Is.EqualTo("OtherTraffic"));
            Assert.That(classify.Invoke(thresholds, new object[] { 4f, 3000f }).ToString(), Is.EqualTo("OtherTraffic"));
            thresholds.GetType().GetField("allowComputedAdvisories").SetValue(thresholds, true);
            Assert.That(classify.Invoke(thresholds, new object[] { .3f, 50f }).ToString(), Is.EqualTo("TrafficAdvisory"),
                "Even the demonstrator switch never computes an RA without its own explicit flag.");
        }

        [Test]
        public void SymbolLimit_SortsByThreatBeforeTruncating()
        {
            var thresholds = Activator.CreateInstance(T("TrafficRadar.ThreatThresholds, TrafficRadar"));
            var processor = Activator.CreateInstance(Processor, thresholds);
            Processor.GetProperty("MaxTargets").SetValue(processor, 5);
            Processor.GetProperty("RangeNM").SetValue(processor, 40f);
            var states = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(State));
            // Seven distant aircraft first in source order, then one proximate aircraft last.
            for (int i = 0; i < 7; i++) states.Add(Aircraft("FAR" + i, 35.0 + .2 + i * .02, -80.0, 610f));
            states.Add(Aircraft("NEAR", 35.03, -80.0, 620f));
            var targets = (IList)Processor.GetMethod("ProcessAircraft").Invoke(processor, new[] { states, Own(35.0, -80.0, 610f) });
            Assert.That(targets.Count, Is.EqualTo(5));
            Assert.That(Field(targets[0], "Icao24"), Is.EqualTo("NEAR"), "Proximate traffic is first and never dropped by the cap.");
        }

        [Test]
        public void AltitudeBand_RemovesOutOfBandClutterButNotAdvisories()
        {
            var thresholds = Activator.CreateInstance(T("TrafficRadar.ThreatThresholds, TrafficRadar"));
            var processor = Activator.CreateInstance(Processor, thresholds);
            Processor.GetProperty("RangeNM").SetValue(processor, 40f);
            Processor.GetProperty("AltitudeBand").SetValue(processor, Enum.Parse(Band, "Normal"));
            var states = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(State));
            states.Add(Aircraft("HIGH", 35.1, -80.0, 610f + 1524f)); // +5,000 ft
            states.Add(Aircraft("LEVEL", 35.1, -80.1, 610f + 152f));  // +500 ft
            var process = Processor.GetMethod("ProcessAircraft");
            var normal = (IList)process.Invoke(processor, new[] { states, Own(35.0, -80.0, 610f) });
            Assert.That(normal.Count, Is.EqualTo(1));
            Assert.That(Field(normal[0], "Icao24"), Is.EqualTo("LEVEL"));
            Processor.GetProperty("AltitudeBand").SetValue(processor, Enum.Parse(Band, "Above"));
            Assert.That(((IList)process.Invoke(processor, new[] { states, Own(35.0, -80.0, 610f) })).Count, Is.EqualTo(2));
            // An advisory (demonstrator switch on) is kept even far outside the band.
            thresholds.GetType().GetField("allowComputedAdvisories").SetValue(thresholds, true);
            thresholds.GetType().GetField("taAltitudeFt").SetValue(thresholds, 6000f);
            Processor.GetProperty("AltitudeBand").SetValue(processor, Enum.Parse(Band, "Normal"));
            states.Clear(); states.Add(Aircraft("TA", 35.02, -80.0, 610f + 1524f));
            Assert.That(((IList)process.Invoke(processor, new[] { states, Own(35.0, -80.0, 610f) })).Count, Is.EqualTo(1));
        }

        [Test]
        public void ChartCoverage_RejectsAPartialMosaic()
        {
            var provider = T("TrafficRadar.FAASectionalChartProvider, TrafficRadar");
            var covers = provider.GetMethod("CoversUv");
            Assert.That(covers.Invoke(null, new object[] { new Rect(-.07f, -.21f, 1.42f, 1.42f) }), Is.False,
                "150 NM on a 3x3 zoom-8 mosaic covers only 70% of the scope.");
            Assert.That(covers.Invoke(null, new object[] { new Rect(.25f, .25f, .5f, .5f) }), Is.True);
            Assert.That(covers.Invoke(null, new object[] { new Rect(float.NaN, 0f, .5f, .5f) }), Is.False);
            var display = T("TrafficRadar.TrafficRadarDisplay, TrafficRadar");
            Assert.That(display.GetField("ChartMaxRangeNM").GetRawConstantValue(), Is.EqualTo(40f));
        }

        [Test]
        public void TrafficOverlay_TextAndRingsStayLegibleAndDistinct()
        {
            var overlay = T("TrafficRadar.RadarTrafficOverlay, TrafficRadar");
            Assert.That((float)overlay.GetMethod("TagFontSize").Invoke(null, new object[] { false, 1f }), Is.GreaterThanOrEqualTo(15f));
            Assert.That((float)overlay.GetMethod("TagFontSize").Invoke(null, new object[] { true, 1.3f }), Is.GreaterThan(17f));
            Assert.That(overlay.GetMethod("HalfRingFraction").Invoke(null, new object[] { 4 }), Is.EqualTo(.5f));
            Assert.That((float)overlay.GetMethod("HalfRingFraction").Invoke(null, new object[] { 3 }), Is.EqualTo(2f / 3f).Within(.0001f));
            var inner = overlay.GetMethod("ShowInnerRing");
            Assert.That(inner.Invoke(null, new object[] { 10f }), Is.True, "2 NM ring at 10 NM range.");
            Assert.That(inner.Invoke(null, new object[] { 40f }), Is.False);
            Assert.That(inner.Invoke(null, new object[] { 2f }), Is.False, "At 2 NM the inner ring would coincide with the outer ring.");
            Assert.That(overlay.GetMethod("FormatRelativeAltitude").Invoke(null, new object[] { -1200f }), Is.EqualTo("−12"));
        }

        [Test]
        public void WeatherPalette_IsOneDiscreteArinc708Scale()
        {
            var palette = T("WeatherRadar.WeatherRadarPalette, WeatherRadar");
            Color Get(string name) => (Color)palette.GetField(name).GetValue(null);
            Assert.That(Get("Level1"), Is.EqualTo(new Color(0f, .78f, .20f, 1f)));
            Assert.That(Get("Level2"), Is.EqualTo(new Color(1f, .85f, 0f, 1f)));
            Assert.That(Get("Level3"), Is.EqualTo(new Color(1f, .15f, .10f, 1f)));
            Assert.That(Get("Level4"), Is.EqualTo(new Color(1f, .20f, 1f, 1f)));
            Assert.That(palette.GetField("NoReturn").GetValue(null), Is.EqualTo(new Color32(4, 10, 14, 240)));
            var level = palette.GetMethod("LevelFromIntensity");
            Assert.That(level.Invoke(null, new object[] { .01f }), Is.EqualTo(0));
            Assert.That(level.Invoke(null, new object[] { .2f }), Is.EqualTo(1));
            Assert.That(level.Invoke(null, new object[] { .5f }), Is.EqualTo(2));
            Assert.That(level.Invoke(null, new object[] { .7f }), Is.EqualTo(3));
            Assert.That(level.Invoke(null, new object[] { .95f }), Is.EqualTo(4));
            Assert.That(level.Invoke(null, new object[] { float.NaN }), Is.EqualTo(0));
            Assert.That(((Color32)palette.GetMethod("ForLevel").Invoke(null, new object[] { 0 })).a, Is.EqualTo(0));
        }

        [Test]
        public void WeatherProvider_TrainingCellsNeverOverwriteALivePictureAndAreLabelled()
        {
            var type = T("WeatherRadar.XPlaneOriginalWeatherRadarProvider, WeatherRadar");
            var go = new GameObject("Weather source test"); go.SetActive(false);
            var real = new Texture2D(4, 4); var training = new Texture2D(4, 4);
            try
            {
                var provider = go.AddComponent(type);
                type.GetField("simulatorFallbackEnabled", Any).SetValue(provider, true);
                var fallback = type.GetMethod("OnSimulatorFallbackDataUpdated", Any);
                fallback.Invoke(provider, new object[] { training });
                Assert.That(type.GetProperty("LastPublishWasSimulatorFallback").GetValue(provider), Is.True);
                var published = (Texture)type.GetField("radarTexture", Any).GetValue(provider);
                Assert.That(published.name, Does.EndWith((string)type.GetField("TrainingTextureSuffix").GetRawConstantValue()));
                type.GetMethod("PublishTexture", new[] { typeof(Texture2D), typeof(string) }).Invoke(provider, new object[] { real, "X-Plane" });
                Assert.That(type.GetProperty("LastPublishWasSimulatorFallback").GetValue(provider), Is.False);
                fallback.Invoke(provider, new object[] { training });
                Assert.That(type.GetProperty("LastPublishWasSimulatorFallback").GetValue(provider), Is.False,
                    "A fresh X-Plane picture must not be replaced by synthetic cells under the same label.");
                type.GetField("lastRealPublishRealtime", Any).SetValue(provider, -1f);
                type.GetProperty("PreferBridgePicture").SetValue(provider, true);
                fallback.Invoke(provider, new object[] { training });
                Assert.That(type.GetProperty("LastPublishWasSimulatorFallback").GetValue(provider), Is.False,
                    "While the bridge feed is healthy, training cells stay off.");
            }
            finally
            {
                var provider = go.GetComponent(type);
                if (provider != null && type.GetField("radarTexture", Any).GetValue(provider) is Texture2D copy) UnityEngine.Object.DestroyImmediate(copy);
                UnityEngine.Object.DestroyImmediate(go);
                UnityEngine.Object.DestroyImmediate(real); UnityEngine.Object.DestroyImmediate(training);
            }
        }

        private static object Aircraft(string id, double lat, double lon, float altitudeMeters)
        {
            var state = Activator.CreateInstance(State);
            State.GetField("Icao24").SetValue(state, id);
            State.GetField("Latitude").SetValue(state, lat);
            State.GetField("Longitude").SetValue(state, lon);
            State.GetField("AltitudeMeters").SetValue(state, altitudeMeters);
            State.GetField("LastUpdate").SetValue(state, DateTime.UtcNow);
            return state;
        }

        private static object Own(double lat, double lon, float altitudeMeters)
        {
            var own = Activator.CreateInstance(OwnShip);
            OwnShip.GetField("Latitude").SetValue(own, lat);
            OwnShip.GetField("Longitude").SetValue(own, lon);
            OwnShip.GetField("AltitudeMeters").SetValue(own, altitudeMeters);
            return own;
        }

        private static object Field(object boxed, string name) => boxed.GetType().GetField(name).GetValue(boxed);
    }
}
