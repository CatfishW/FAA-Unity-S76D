using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR;

namespace FAA.Customization.Tests
{
    public class XR3DesktopSpectatorTests
    {
        private static Type Preview => Type.GetType("FAA.Headset.XR3DesktopHudPreview, Assembly-CSharp", true);
        private static Type Presenter => Type.GetType("FAA.Headset.XR3MixedRealityPresentation, Assembly-CSharp", true);
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static void Configure(Camera desktop, Camera headset, Vector2 size) => Preview.GetMethod("ConfigureSpectatorCamera")
            .Invoke(null, new object[] { desktop, headset, size });

        [TestCase(3440, 1440)]
        [TestCase(1920, 1080)]
        [TestCase(1024, 768)]
        public void SpectatorRendersDirectlyToTheDesktopWithoutChangingHeadsetPoseOrRenderState(int width, int height)
        {
            var headHost = new GameObject("Headset", typeof(Camera));
            var desktopHost = new GameObject("Mono spectator", typeof(Camera));
            var headTexture = new RenderTexture(16, 16, 0);
            var oldDesktopTexture = new RenderTexture(16, 16, 0);
            try
            {
                Camera head = headHost.GetComponent<Camera>(), desktop = desktopHost.GetComponent<Camera>();
                head.transform.SetLocalPositionAndRotation(new Vector3(1, 2, 3), Quaternion.Euler(7, 42, -3));
                head.targetTexture = headTexture;
                head.stereoTargetEye = StereoTargetEyeMask.Both;
                head.depth = 3;
                head.farClipPlane = 150000;
                head.clearFlags = CameraClearFlags.SolidColor;
                head.backgroundColor = Color.clear;
                head.cullingMask = 1 << 30;
                head.allowHDR = true;
                Matrix4x4 mono = Matrix4x4.Perspective(90, 1.1f, .03f, 150000);
                Matrix4x4 left = mono, right = mono;
                left.m02 = -.37f; right.m02 = .38f;
                head.projectionMatrix = mono;
                head.SetStereoProjectionMatrix(Camera.StereoscopicEye.Left, left);
                head.SetStereoProjectionMatrix(Camera.StereoscopicEye.Right, right);
                mono = head.projectionMatrix;
                left = head.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
                right = head.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
                Vector3 position = head.transform.localPosition;
                Quaternion rotation = head.transform.localRotation;
                desktop.enabled = false;
                desktop.targetTexture = oldDesktopTexture;
                desktop.targetDisplay = 1;
                desktop.stereoTargetEye = StereoTargetEyeMask.Both;
                desktop.depth = -5;

                Configure(desktop, head, new Vector2(width, height));

                Assert.That(desktop.enabled, Is.True);
                Assert.That(desktop.targetTexture, Is.Null);
                Assert.That(desktop.targetDisplay, Is.Zero);
                Assert.That(desktop.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.None));
                Assert.That(desktop.depth, Is.GreaterThan(head.depth));
                Assert.That(desktop.aspect, Is.EqualTo((float)width / height).Within(.0001f));
                Assert.That(desktop.rect, Is.EqualTo(new Rect(0, 0, 1, 1)));
                Assert.That(desktop.cullingMask, Is.EqualTo(1 << 30));
                Assert.That(desktop.farClipPlane, Is.EqualTo(head.farClipPlane));
                Assert.That(head.enabled, Is.True);
                Assert.That(head.targetTexture, Is.SameAs(headTexture));
                Assert.That(head.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.Both));
                Assert.That(head.depth, Is.EqualTo(3f));
                Assert.That(head.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
                Assert.That(head.backgroundColor, Is.EqualTo(Color.clear));
                Assert.That(head.cullingMask, Is.EqualTo(1 << 30));
                Assert.That(head.allowHDR, Is.True);
                Assert.That(head.transform.localPosition, Is.EqualTo(position));
                Assert.That(head.transform.localRotation, Is.EqualTo(rotation));
                Assert.That(head.projectionMatrix, Is.EqualTo(mono));
                Assert.That(head.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), Is.EqualTo(left));
                Assert.That(head.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), Is.EqualTo(right));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(headHost); UnityEngine.Object.DestroyImmediate(desktopHost);
                UnityEngine.Object.DestroyImmediate(headTexture); UnityEngine.Object.DestroyImmediate(oldDesktopTexture);
            }
        }

        [Test]
        public void MrDiscoveryAndReapplyKeepTheOwnedSpectatorEnabledEvenIfItWasPreviouslyCached()
        {
            var headHost = new GameObject("Headset", typeof(Camera));
            var ownerHost = new GameObject("Spectator owner");
            var spectatorHost = new GameObject("Spectator", typeof(Camera));
            var unrelatedHost = new GameObject("Unrelated opaque camera", typeof(Camera));
            var presenterHost = new GameObject("MR presenter");
            Component presenter = null;
            try
            {
                Camera head = headHost.GetComponent<Camera>(), spectator = spectatorHost.GetComponent<Camera>();
                spectatorHost.transform.SetParent(ownerHost.transform, false);
                Component owner = ownerHost.AddComponent(Preview);
                presenter = presenterHost.AddComponent(Presenter);
                Presenter.GetField("view", PrivateInstance).SetValue(presenter, head);
                Presenter.GetMethod("DiscoverPresentation", PrivateInstance).Invoke(presenter, null);
                Assert.That(spectator.enabled, Is.False, "An unowned camera is still suppressed by MR.");
                Preview.GetField("preview", PrivateInstance).SetValue(owner, spectator);
                Configure(spectator, head, new Vector2(1920, 1080));
                Presenter.GetMethod("DiscoverPresentation", PrivateInstance).Invoke(presenter, null);
                Presenter.GetMethod("ApplyPresentation", PrivateInstance).Invoke(presenter, null);
                Presenter.GetMethod("ApplyPresentation", PrivateInstance).Invoke(presenter, null);
                Assert.That(spectator.enabled, Is.True, "The standard desktop pass must survive each MR refresh.");
                Assert.That(unrelatedHost.GetComponent<Camera>().enabled, Is.False);
                Assert.That((bool)Preview.GetMethod("IsPreviewCamera").Invoke(null, new object[] { spectator }), Is.True);
                Presenter.GetMethod("OnDisable", PrivateInstance).Invoke(presenter, null);
                Assert.That(spectator.enabled, Is.True, "MR restoration must also leave the owned spectator alone.");
                Assert.That(unrelatedHost.GetComponent<Camera>().enabled, Is.True);
            }
            finally
            {
                if (presenter != null) Presenter.GetMethod("OnDisable", PrivateInstance).Invoke(presenter, null);
                UnityEngine.Object.DestroyImmediate(presenterHost); UnityEngine.Object.DestroyImmediate(ownerHost);
                UnityEngine.Object.DestroyImmediate(spectatorHost); UnityEngine.Object.DestroyImmediate(unrelatedHost);
                UnityEngine.Object.DestroyImmediate(headHost);
            }
        }

        [Test]
        public void SuspendingSpectatorRestoresMirrorAndPointerRouting()
        {
            var headHost = new GameObject("Headset", typeof(Camera));
            var ownerHost = new GameObject("Spectator owner");
            var spectatorHost = new GameObject("Spectator", typeof(Camera));
            var canvasHost = new GameObject("HUD", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            bool mirror = XRSettings.showDeviceView;
            FieldInfo active = Preview.GetField("active", BindingFlags.NonPublic | BindingFlags.Static);
            object previousActive = active.GetValue(null);
            try
            {
                Camera head = headHost.GetComponent<Camera>(), spectator = spectatorHost.GetComponent<Camera>();
                spectatorHost.transform.SetParent(ownerHost.transform, false);
                Component owner = ownerHost.AddComponent(Preview);
                Preview.GetField("preview", PrivateInstance).SetValue(owner, spectator);
                Preview.GetField("capturedMirror", PrivateInstance).SetValue(owner, true);
                Preview.GetField("originalMirror", PrivateInstance).SetValue(owner, mirror);
                active.SetValue(null, owner);
                XRSettings.showDeviceView = false;
                Configure(spectator, head, new Vector2(1920, 1080));
                Canvas canvas = canvasHost.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = head;
                Type adapterType = Type.GetType("FAA.Headset.XR3DesktopCanvasRaycaster, Assembly-CSharp", true);
                Component adapter = canvasHost.AddComponent(adapterType);
                GraphicRaycaster original = canvasHost.GetComponent<GraphicRaycaster>();
                adapterType.GetMethod("Bind").Invoke(adapter, new object[] { original, spectator });
                ((IList)Preview.GetField("pointerRaycasters", PrivateInstance).GetValue(owner)).Add(adapter);
                Assert.That(Preview.GetMethod("PointerCameraForCanvas").Invoke(null, new object[] { canvas }), Is.SameAs(spectator));
                Preview.GetMethod("SuspendPreview", PrivateInstance).Invoke(owner, null);
                Assert.That(spectator.enabled, Is.False);
                Assert.That(original.enabled, Is.True);
                Assert.That(XRSettings.showDeviceView, Is.EqualTo(mirror));
                Assert.That(Preview.GetMethod("PointerCameraForCanvas").Invoke(null, new object[] { canvas }), Is.SameAs(head));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasHost); UnityEngine.Object.DestroyImmediate(ownerHost);
                UnityEngine.Object.DestroyImmediate(spectatorHost); UnityEngine.Object.DestroyImmediate(headHost);
                XRSettings.showDeviceView = mirror;
                active.SetValue(null, previousActive);
            }
        }
    }
}
