using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace FAA.Customization.Tests
{
    public sealed class FaaSourceHealthTransitionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void StaleDiscoveredSourceLogsOneLossAndThenOneRecoveryWithoutChangingItsFreshnessLimit()
        {
            Type type = Type.GetType("FAA.XPlaneIntegration.Runtime.XPlane12ApiHudBridge, Assembly-CSharp");
            MethodInfo update = type?.GetMethod("UpdateDiscoveredHealth", Private);
            if (update == null) Assert.Ignore("The full XR integration source profile supplies discovery health.");
            Type data = Type.GetType("FAA.XPlaneIntegration.Runtime.XPlaneDiscoveryData, Assembly-CSharp", true);
            var root = new GameObject("Source health transition fixture");
            root.SetActive(false); // Avoid starting source discovery or a network transport.
            try
            {
                Component bridge = root.AddComponent(type);
                type.GetField("discoveryDriven", Private).SetValue(bridge, true);
                MethodInfo healthy = type.GetMethod("SetHealthy", Private);
                LogAssert.Expect(LogType.Log, new Regex("\\[XPlane12ApiHudBridge\\] Connected via"));
                healthy.Invoke(bridge, new object[] { true });
                double now = (double)data.GetProperty("Now").GetValue(null);
                type.GetField("discoveryLastReceived", Private).SetValue(bridge, now - 2d);
                LogAssert.Expect(LogType.Warning, new Regex("\\[XPlane12ApiHudBridge\\] Feed unhealthy: Selected source is stale"));
                for (int i = 0; i < 4; i++) update.Invoke(bridge, null);
                Assert.That(type.GetProperty("IsFeedHealthy").GetValue(bridge), Is.False);
                Assert.That((float)type.GetProperty("LastPacketAgeSeconds").GetValue(bridge), Is.GreaterThanOrEqualTo(2f));
                LogAssert.NoUnexpectedReceived();

                now = (double)data.GetProperty("Now").GetValue(null);
                type.GetField("discoveryLastReceived", Private).SetValue(bridge, now);
                LogAssert.Expect(LogType.Log, new Regex("\\[XPlane12ApiHudBridge\\] Connected via"));
                healthy.Invoke(bridge, new object[] { true });
                update.Invoke(bridge, null);
                Assert.That(type.GetProperty("IsFeedHealthy").GetValue(bridge), Is.True);
                Assert.That((float)type.GetProperty("LastPacketAgeSeconds").GetValue(bridge), Is.LessThan(1f));
                LogAssert.NoUnexpectedReceived();
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
    }
}
