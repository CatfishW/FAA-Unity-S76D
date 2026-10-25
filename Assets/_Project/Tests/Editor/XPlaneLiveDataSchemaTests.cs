using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace FAA.Customization.Tests
{
    public class XPlaneLiveDataSchemaTests
    {
        private static Type Bridge => Type.GetType("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge, Assembly-CSharp", true);
        private static Type Schema => Type.GetType("FAA.XPlaneIntegration.Runtime.XPlaneDiscoveryData, Assembly-CSharp", true);

        [TestCase(0, 2000f, 20f)]
        [TestCase(1, 3500f, 35f)]
        [TestCase(0, 12000f, 120f)]
        public void TorqueUsesMatchingEngineDomainInsteadOfPropAndTransmissionValues(int engine, float torque, float expected)
        {
            var values = new Dictionary<string, float>
            {
                [$"sim/flightmodel/engine/ENGN_TRQ[{engine}]"] = torque,
                ["sim/aircraft/controls/acf_trq_max_eng"] = 10000f,
                [$"sim/flightmodel/engine/ENGN_driv_TRQ[{engine}]"] = 9373f,
                [$"sim/flightmodel/engine/POINT_max_TRQ[{engine}]"] = 110f
            };
            object[] args = { values, engine, 0f };
            Assert.That(Bridge.GetMethod("TryCalculateTorquePercent").Invoke(null, args), Is.True);
            Assert.That((float)args[2], Is.EqualTo(expected).Within(.001f));
        }

        [TestCase(0f)]
        [TestCase(-100f)]
        [TestCase(float.NaN)]
        public void InvalidEngineLimitCannotFallBackToAnUnrelatedPropReference(float limit)
        {
            var values = new Dictionary<string, float>
            {
                ["sim/flightmodel/engine/ENGN_TRQ[0]"] = 50,
                ["sim/aircraft/controls/acf_trq_max_eng"] = limit,
                ["sim/flightmodel/engine/ENGN_driv_TRQ[0]"] = 50,
                ["sim/flightmodel/engine/POINT_max_TRQ[0]"] = 100
            };
            object[] args = { values, 0, 0f };
            Assert.That(Bridge.GetMethod("TryCalculateTorquePercent").Invoke(null, args), Is.False);
        }

        [TestCase(-2.26, true)]
        [TestCase(0, true)]
        [TestCase(120, true)]
        [TestCase(-2001, false)]
        [TestCase(double.NaN, false)]
        public void SignedNativePitotSampleDoesNotDiscardOtherValidFlightInstruments(double indicatedAirspeed, bool expected)
        {
            string[] names = (string[])Schema.GetField("Core").GetValue(null);
            var values = new Dictionary<string, double>();
            foreach (string name in names) values[name] = 0;
            values[names[0]] = 47.46; values[names[1]] = -122.3; values[names[2]] = 124;
            values[names[6]] = indicatedAirspeed;
            Assert.That(Schema.GetMethod("CoreValid").Invoke(null, new object[] { values }), Is.EqualTo(expected));
        }

        [Test]
        public void NativeSchemaIncludesEngineTorqueAndConfiguredEngineLimit()
        {
            string[] fields = (string[])Schema.GetField("Subscriptions").GetValue(null);
            Assert.That(fields, Does.Contain("sim/flightmodel/engine/ENGN_TRQ[0]"));
            Assert.That(fields, Does.Contain("sim/flightmodel/engine/ENGN_TRQ[1]"));
            Assert.That(fields, Does.Contain("sim/aircraft/controls/acf_trq_max_eng"));
        }
    }
}
