using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class XR3HandTrackingTests
    {
        private static Type Tracker => Type.GetType("FAA.Headset.XR3HandTracking, Assembly-CSharp", true);

        [TestCase(false, 0)]
        [TestCase(true, 0)]
        [TestCase(true, 1)]
        public void MissingDeviceOrStalledFramesTriggerControllerRecoveryAfterFiveWallClockSecondsWithBoundedRetries(bool serviceConnected, int devices)
        {
            var host = new GameObject("Controller watchdog fixture");
            host.SetActive(false);
            try
            {
                Component tracker = host.AddComponent(Tracker);
                MethodInfo policy = Tracker.GetMethod("ShouldRecoverController", BindingFlags.NonPublic | BindingFlags.Instance);
                Func<double, bool> reset = now => (bool)policy.Invoke(tracker, new object[] { true, serviceConnected, devices, false, now });
                Assert.That(reset(0), Is.False);
                Assert.That(reset(4.999), Is.False);
                Assert.That(reset(5), Is.True, "A service-connected pipe must not conceal zero devices or a frozen frame.");
                Assert.That(reset(5.001), Is.False);
                Assert.That(reset(9.999), Is.False);
                Assert.That(reset(10), Is.True, "Persistent failure permits one reset per five wall-clock seconds.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void AdvancingDeviceFramesAreHealthyEvenWhenNoHandsAreVisibleAndRestartTheFailureGracePeriod()
        {
            var host = new GameObject("Healthy device watchdog fixture");
            host.SetActive(false);
            try
            {
                Component tracker = host.AddComponent(Tracker);
                MethodInfo policy = Tracker.GetMethod("ShouldRecoverController", BindingFlags.NonPublic | BindingFlags.Instance);
                Func<bool, double, bool> reset = (fresh, now) => (bool)policy.Invoke(tracker, new object[] { true, true, 1, fresh, now });
                Assert.That(reset(false, 0), Is.False);
                Assert.That(reset(true, 4), Is.False);
                Assert.That(reset(true, 20), Is.False, "Device freshness does not require a visible hand.");
                Assert.That(reset(false, 21), Is.False);
                Assert.That(reset(false, 25.999), Is.False);
                Assert.That(reset(false, 26), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void DisabledProviderNeverResetsOrCreatesAController()
        {
            var host = new GameObject("Disabled controller watchdog fixture");
            var providerHost = new GameObject("Disabled hand provider");
            host.SetActive(false); providerHost.SetActive(false);
            try
            {
                Component tracker = host.AddComponent(Tracker);
                Type providerType = Type.GetType("Leap.LeapXRServiceProvider, Ultraleap.Tracking.Core", true);
                Type serviceType = Type.GetType("Leap.LeapServiceProvider, Ultraleap.Tracking.Core", true);
                Component provider = providerHost.AddComponent(providerType);
                Tracker.GetMethod("BindProvider", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(tracker, new object[] { provider });
                MethodInfo policy = Tracker.GetMethod("ShouldRecoverController", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(policy.Invoke(tracker, new object[] { false, true, 0, false, 0d }), Is.False);
                Assert.That(policy.Invoke(tracker, new object[] { false, true, 0, false, 100d }), Is.False);
                Tracker.GetMethod("ResetStalledController", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(tracker, null);
                Assert.That(Tracker.GetProperty("ControllerResetAttempts").GetValue(tracker), Is.Zero);
                Assert.That(serviceType.GetField("_leapController", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(provider), Is.Null);
                Assert.That(policy.Invoke(tracker, new object[] { true, true, 0, false, 101d }), Is.False);
                Assert.That(policy.Invoke(tracker, new object[] { true, true, 0, false, 105.999d }), Is.False);
                Assert.That(policy.Invoke(tracker, new object[] { true, true, 0, false, 106d }), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(providerHost); }
        }

        [Test]
        public void NativeProviderContinuesBoundedRecoveryAfterTheSdkDefaultRetryBudget()
        {
            Type providerType = Type.GetType("Leap.LeapXRServiceProvider, Ultraleap.Tracking.Core", true);
            Type serviceType = Type.GetType("Leap.LeapServiceProvider, Ultraleap.Tracking.Core", true);
            var host = new GameObject("Inactive hand provider recovery fixture");
            host.SetActive(false);
            try
            {
                Component provider = host.AddComponent(providerType);
                providerType.GetProperty("deviceOffsetYAxis").SetValue(provider, -.0112f);
                providerType.GetProperty("deviceOffsetZAxis").SetValue(provider, .0999f);
                Assert.That(serviceType.GetField("_leapController", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(provider), Is.Null,
                    "An inactive Editor fixture must not open a real hand-tracking connection.");
                Tracker.GetMethod("ConfigureRecovery", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { provider });
                Assert.That(serviceType.GetField("_reconnectionAttempts").GetValue(provider), Is.EqualTo(-1));
                Assert.That(serviceType.GetField("_reconnectionInterval").GetValue(provider), Is.EqualTo(180));
                Assert.That(providerType.GetProperty("deviceOffsetYAxis").GetValue(provider), Is.EqualTo(-.0112f));
                Assert.That(providerType.GetProperty("deviceOffsetZAxis").GetValue(provider), Is.EqualTo(.0999f));
                FieldInfo attempts = serviceType.GetField("_numberOfReconnectionAttempts", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo checks = serviceType.GetField("_framesSinceServiceConnectionChecked", BindingFlags.Instance | BindingFlags.NonPublic);
                attempts.SetValue(provider, 5);
                checks.SetValue(provider, 0);
                serviceType.GetMethod("checkConnectionIntegrity", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(provider, null);
                Assert.That(checks.GetValue(provider), Is.EqualTo(1),
                    "Recovery must keep scheduling retries after the SDK's default five attempts, without starting a hot retry loop.");
                Assert.That(attempts.GetValue(provider), Is.EqualTo(5), "One check must not bypass the 180-check backoff.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void DisableDropsTrackedStateAndCachedFramesCannotBecomeFreshOnResume()
        {
            var host = new GameObject("Hand freshness fixture");
            host.SetActive(false);
            try
            {
                Component tracker = host.AddComponent(Tracker);
                MethodInfo observe = Tracker.GetMethod("ObserveTimestamp", BindingFlags.NonPublic | BindingFlags.Instance);
                FieldInfo changed = Tracker.GetField("lastChange", BindingFlags.NonPublic | BindingFlags.Instance);
                observe.Invoke(tracker, new object[] { 123L, 10f });
                observe.Invoke(tracker, new object[] { 124L, 10.01f });
                Assert.That(changed.GetValue(tracker), Is.EqualTo(10.01f));
                foreach (string property in new[] { "LeftTracked", "RightTracked", "ServiceConnected" })
                    Tracker.GetField("<" + property + ">k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(tracker, true);
                Tracker.GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(tracker, null);
                foreach (string property in new[] { "LeftTracked", "RightTracked", "ServiceConnected" })
                    Assert.That(Tracker.GetProperty(property).GetValue(tracker), Is.False);
                observe.Invoke(tracker, new object[] { 124L, 20f });
                Assert.That(changed.GetValue(tracker), Is.EqualTo(float.NegativeInfinity), "A cached frame cannot reacquire a hand.");
                observe.Invoke(tracker, new object[] { 125L, 20.01f });
                Assert.That(changed.GetValue(tracker), Is.EqualTo(20.01f), "A genuinely new timestamp restores freshness.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void ReplacingAProviderResetsItsTimestampHistoryAndConfiguresTheReusedProvider()
        {
            var host = new GameObject("Provider replacement fixture");
            var a = new GameObject("Inactive provider A");
            var b = new GameObject("Inactive provider B");
            host.SetActive(false); a.SetActive(false); b.SetActive(false);
            try
            {
                Component tracker = host.AddComponent(Tracker);
                Type providerType = Type.GetType("Leap.LeapXRServiceProvider, Ultraleap.Tracking.Core", true);
                Component first = a.AddComponent(providerType), second = b.AddComponent(providerType);
                MethodInfo bind = Tracker.GetMethod("BindProvider", BindingFlags.NonPublic | BindingFlags.Instance);
                MethodInfo observe = Tracker.GetMethod("ObserveTimestamp", BindingFlags.NonPublic | BindingFlags.Instance);
                bind.Invoke(tracker, new object[] { first });
                observe.Invoke(tracker, new object[] { 100L, 1f });
                observe.Invoke(tracker, new object[] { 101L, 1.01f });
                bind.Invoke(tracker, new object[] { second });
                Assert.That(Tracker.GetField("timestamp", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tracker), Is.EqualTo(0L));
                Assert.That(Tracker.GetField("lastChange", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(tracker), Is.EqualTo(float.NegativeInfinity));
                Assert.That(providerType.GetField("_reconnectionAttempts").GetValue(second), Is.EqualTo(-1));
                Assert.That(providerType.GetField("_reconnectionInterval").GetValue(second), Is.EqualTo(180));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b); }
        }
        private static object Frame(bool both = true)
        {
            var type = Type.GetType("Leap.Frame, Ultraleap.Tracking.Core", true);
            object frame = Activator.CreateInstance(type, new object[] { 1u });
            type.GetField("Timestamp").SetValue(frame, 123L);
            var hands = (IList)type.GetField("Hands").GetValue(frame);
            foreach (bool left in both ? new[] { true, true, false } : new[] { true })
            {
                var handType = Type.GetType("Leap.Hand, Ultraleap.Tracking.Core", true);
                object hand = Activator.CreateInstance(handType);
                handType.GetField("IsLeft").SetValue(hand, left);
                handType.GetField("PalmPosition").SetValue(hand, new Vector3(.1f, .2f, .4f));
                hands.Add(hand);
            }
            return frame;
        }

        private static object[] Detect(object frame, bool connected, float age)
        {
            object[] args = { frame, connected, age, false, false };
            int count = (int)Tracker.GetMethod("CountFreshHands", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
            return new[] { (object)count, args[3], args[4] };
        }

        [Test]
        public void FreshLiveFramesRecognizeBothHandsAndIgnoreDuplicateChirality()
        {
            Assert.That(Detect(Frame(), true, .01f), Is.EqualTo(new object[] { 2, true, true }));
            Assert.That(Detect(Frame(false), true, .1f), Is.EqualTo(new object[] { 1, true, false }));
        }

        [TestCase(false, 0f)]
        [TestCase(true, .21f)]
        [TestCase(true, -1f)]
        [TestCase(true, float.NaN)]
        [TestCase(true, float.PositiveInfinity)]
        public void LostConnectionAndStaleOrInvalidFramesNeverReportTrackedHands(bool connected, float age)
        {
            Assert.That(Detect(Frame(), connected, age), Is.EqualTo(new object[] { 0, false, false }));
        }

        [Test]
        public void MissingTimestampOrInvalidPalmNeverCountsAsRecognition()
        {
            Assert.That(Detect(null, true, 0), Is.EqualTo(new object[] { 0, false, false }));
            object frame = Frame(false);
            frame.GetType().GetField("Timestamp").SetValue(frame, 0L);
            Assert.That(Detect(frame, true, 0), Is.EqualTo(new object[] { 0, false, false }));
            frame.GetType().GetField("Timestamp").SetValue(frame, 123L);
            var hands = (IList)frame.GetType().GetField("Hands").GetValue(frame);
            hands[0].GetType().GetField("PalmPosition").SetValue(hands[0], new Vector3(float.NaN, 0, 0));
            Assert.That(Detect(frame, true, 0), Is.EqualTo(new object[] { 0, false, false }));
        }
    }
}
