using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class FaaNativeXrGestureLifecycleTests
    {
        [Test]
        public void NativeWorkspacePrefersTheStatusTrackersHeadsetStreamAndRejectsDesktopProviders()
        {
            Type input = Type.GetType("FAA.Customization.FaaWorkspaceGestureInput, Assembly-CSharp", true);
            Type desktopType = Type.GetType("Leap.LeapServiceProvider, Ultraleap.Tracking.Core", true);
            Type headsetType = Type.GetType("Leap.LeapXRServiceProvider, Ultraleap.Tracking.Core", true);
            var desktopHost = new GameObject("Inactive desktop provider fixture");
            var headsetHost = new GameObject("Inactive headset provider fixture");
            desktopHost.SetActive(false); headsetHost.SetActive(false);
            try
            {
                Component desktop = desktopHost.AddComponent(desktopType), headset = headsetHost.AddComponent(headsetType);
                MethodInfo select = input.GetMethod("PreferredProviderForMode", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(select.Invoke(null, new object[] { true, desktop, headset, true }), Is.SameAs(headset));
                Assert.That(select.Invoke(null, new object[] { true, desktop, headset, false }), Is.Null,
                    "Native XR must not silently use a desktop-offset stream while the headset source is unavailable.");
                Assert.That(select.Invoke(null, new object[] { false, desktop, headset, true }), Is.SameAs(desktop),
                    "Desktop mode retains its existing provider selection.");
            }
            finally { UnityEngine.Object.DestroyImmediate(desktopHost); UnityEngine.Object.DestroyImmediate(headsetHost); }
        }

        [Test]
        public void NativeWorkspaceKeepsAnExistingHeadsetProviderWhenThePreferredOneIsUnavailable()
        {
            Type input = Type.GetType("FAA.Customization.FaaWorkspaceGestureInput, Assembly-CSharp", true);
            Type headsetType = Type.GetType("Leap.LeapXRServiceProvider, Ultraleap.Tracking.Core", true);
            var currentHost = new GameObject("Inactive XR fallback fixture");
            var preferredHost = new GameObject("Inactive preferred XR fixture");
            currentHost.SetActive(false); preferredHost.SetActive(false);
            try
            {
                Component current = currentHost.AddComponent(headsetType), preferred = preferredHost.AddComponent(headsetType);
                MethodInfo select = input.GetMethod("PreferredProviderForMode", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(select.Invoke(null, new object[] { true, current, preferred, false }), Is.SameAs(current));
            }
            finally { UnityEngine.Object.DestroyImmediate(currentHost); UnityEngine.Object.DestroyImmediate(preferredHost); }
        }

        [Test]
        public void SeatDetectsATrackingOriginInstalledAfterInitialWorkspaceBinding()
        {
            Type type = Type.GetType("FAA.Customization.FaaTrackedSeatReference, Assembly-CSharp", true);
            object seat = Activator.CreateInstance(type);
            var stage = new GameObject("Original stage");
            var origin = new GameObject("Late native origin");
            var head = new GameObject("Tracked head");
            try
            {
                head.transform.SetParent(stage.transform, false);
                type.GetMethod("Capture").Invoke(seat, new object[] { head.transform });
                Assert.That(type.GetMethod("MatchesTrackingSpace").Invoke(seat, new object[] { head.transform }), Is.True);
                head.transform.SetParent(origin.transform, false);
                Assert.That(type.GetMethod("MatchesTrackingSpace").Invoke(seat, new object[] { head.transform }), Is.False);
                type.GetMethod("Capture").Invoke(seat, new object[] { head.transform });
                Assert.That(type.GetMethod("MatchesTrackingSpace").Invoke(seat, new object[] { head.transform }), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(stage); UnityEngine.Object.DestroyImmediate(origin); }
        }

        [Test]
        public void RepeatedFocusLossCancellationKeepsAHeldPinchBlockedUntilRelease()
        {
            Type type = Type.GetType("FAA.Customization.FaaWorkspaceGestureInput, Assembly-CSharp", true);
            var host = new GameObject("Gesture cancellation fixture");
            host.SetActive(false);
            try
            {
                Component input = host.AddComponent(type);
                var pinched = (bool[])type.GetField("pinched", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(input);
                var releaseRequired = (bool[])type.GetField("releaseRequired", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(input);
                pinched[0] = true;
                type.GetMethod("CancelPointers").Invoke(input, null);
                type.GetMethod("CancelPointers").Invoke(input, null);
                Assert.That(pinched[0], Is.False);
                Assert.That(releaseRequired[0], Is.True,
                    "Each unfocused Update cancels input; a second cancellation must not rearm a held pinch.");
                type.GetMethod("Feed", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(input,
                    new object[] { 0, new Ray(Vector3.zero, Vector3.forward), Vector3.forward, true, 1f });
                Assert.That(releaseRequired[0], Is.True, "A still-pinched tracking sample must remain blocked after focus returns.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
    }
}
