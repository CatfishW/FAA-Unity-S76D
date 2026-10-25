using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class XR3ConformalRenderingTests
    {
        private static Type Preview => Type.GetType("FAA.Headset.XR3DesktopHudPreview, Assembly-CSharp", true);

        [Test]
        public void NativeAttitudeWindowUsesProjectedHudAreaInsteadOfTheWholeEyeBuffer()
        {
            Type layerType = Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp");
            if (layerType == null) Assert.Ignore("Full integration profile conformal mesh implementation.");
            var view = new GameObject("Native view", typeof(Camera));
            var hud = new GameObject("Angular HUD", typeof(RectTransform), typeof(Canvas));
            var host = new GameObject("Layer");
            var aircraft = new GameObject("Aircraft");
            try
            {
                Camera camera = view.GetComponent<Camera>(); camera.fieldOfView = 96; camera.aspect = 1;
                Canvas canvas = hud.GetComponent<Canvas>();
                Type.GetType("FAA.Headset.XR3MixedRealityPresentation, Assembly-CSharp", true)
                    .GetMethod("FitHeadlockedCanvas").Invoke(null, new object[] { canvas, camera, null });
                Component layer = host.AddComponent(layerType);
                layerType.GetMethod("Bind").Invoke(layer, new object[] { camera, aircraft.transform, canvas });
                layerType.GetMethod("CacheView", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(layer, null);
                layerType.GetField("nativeXr", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(layer, true);
                layerType.GetField("zoneFrame", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(layer, -1);
                layerType.GetField("windowReady", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(layer, false);
                layerType.GetMethod("RefreshZones", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(layer, null);
                Rect window = (Rect)layerType.GetProperty("AttitudeWindowPixels").GetValue(layer);
                RectTransform rect = (RectTransform)canvas.transform;
                float bankBottom = camera.WorldToScreenPoint(rect.TransformPoint(new Vector3(0, rect.rect.yMin + .78f * rect.rect.height, 0))).y;
                float headingTop = camera.WorldToScreenPoint(rect.TransformPoint(new Vector3(0, rect.rect.yMin + .30f * rect.rect.height, 0))).y;
                Assert.That(window.yMax, Is.LessThan(bankBottom), "Pitch labels must stay below the bank/FMA lanes.");
                Assert.That(window.yMin, Is.GreaterThan(headingTop), "Pitch labels must stay above the heading and hand-status lanes.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(hud);
                UnityEngine.Object.DestroyImmediate(view); UnityEngine.Object.DestroyImmediate(aircraft);
            }
        }

        [Test]
        public void PreviewRetainsTheHeadsetFarRangeForDistantAngularGeometry()
        {
            var head = new GameObject("Head", typeof(Camera));
            var desktop = new GameObject("Desktop", typeof(Camera));
            try
            {
                Camera h = head.GetComponent<Camera>(), d = desktop.GetComponent<Camera>();
                h.farClipPlane = 150000;
                Preview.GetMethod("ConfigureCamera").Invoke(null, new object[] { d, h, new Vector2(1600, 900) });
                Assert.That(d.farClipPlane, Is.GreaterThan(4000), "The angular cue shell is normally 4 km away.");
                Assert.That(d.farClipPlane, Is.EqualTo(h.farClipPlane));
                Assert.That(h.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.Both));
            }
            finally { UnityEngine.Object.DestroyImmediate(head); UnityEngine.Object.DestroyImmediate(desktop); }
        }

        [Test]
        public void ConformalMeshesAndLabelsRenderInTheOwnedPreviewAndHeadset()
        {
            Type layerType = Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp");
            if (layerType == null) Assert.Ignore("Full integration profile conformal mesh implementation.");
            var view = new GameObject("View", typeof(Camera));
            var aircraft = new GameObject("Aircraft");
            var canvasHost = new GameObject("Fixed HUD", typeof(RectTransform), typeof(Canvas));
            var layerHost = new GameObject("Conformal");
            var previewHost = new GameObject("Preview owner");
            var previewCameraHost = new GameObject("Preview camera", typeof(Camera));
            var unrelated = new GameObject("Unrelated camera", typeof(Camera));
            try
            {
                Camera camera = view.GetComponent<Camera>(); camera.farClipPlane = 150000;
                var layer = layerHost.AddComponent(layerType);
                layerType.GetMethod("Bind").Invoke(layer, new object[] { camera, aircraft.transform, canvasHost.GetComponent<Canvas>() });
                layerType.GetMethod("RefreshPresentation").Invoke(layer, null);
                MeshRenderer[] renderers = layerHost.GetComponentsInChildren<MeshRenderer>(true);
                Assert.That(renderers.Length, Is.GreaterThan(0));
                foreach (MeshRenderer renderer in renderers)
                    Assert.That(renderer.gameObject.layer, Is.EqualTo(30), renderer.name + " must survive the MR HUD-only culling mask.");
                previewCameraHost.transform.SetParent(previewHost.transform, false);
                Component owner = previewHost.AddComponent(Preview);
                Camera desktop = previewCameraHost.GetComponent<Camera>();
                Preview.GetField("preview", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(owner, desktop);
                MethodInfo ready = layerType.GetMethod("CameraReady", BindingFlags.NonPublic | BindingFlags.Instance);
                ready.Invoke(layer, new object[] { camera }); Assert.That(renderers[0].enabled, Is.True);
                ready.Invoke(layer, new object[] { unrelated.GetComponent<Camera>() }); Assert.That(renderers[0].enabled, Is.False);
                ready.Invoke(layer, new object[] { desktop }); Assert.That(renderers[0].enabled, Is.True,
                    "The conformal camera callback must not hide geometry from the centered preview.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(layerHost); UnityEngine.Object.DestroyImmediate(previewHost);
                UnityEngine.Object.DestroyImmediate(previewCameraHost); UnityEngine.Object.DestroyImmediate(canvasHost);
                UnityEngine.Object.DestroyImmediate(aircraft); UnityEngine.Object.DestroyImmediate(view);
                UnityEngine.Object.DestroyImmediate(unrelated);
            }
        }
    }
}
