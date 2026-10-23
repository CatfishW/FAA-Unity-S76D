using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public class XR3MixedRealityPresentationTests
    {
        private static Type Presenter => Type.GetType("FAA.Headset.XR3MixedRealityPresentation, Assembly-CSharp", true);
        private static object Call(string name, params object[] args) => Presenter.GetMethod(name,
            BindingFlags.Public | BindingFlags.Static).Invoke(null, args);

        [TestCase(null, true)]
        [TestCase("--xr3", true)]
        [TestCase("--xr3-vr", false)]
        [TestCase("--XR3-SIM", false)]
        [TestCase("-xr3-sim", false)]
        public void NativeLaunchDefaultsToMixedRealityWithExplicitOptOut(string argument, bool expected)
        {
            var args = argument == null ? null : new[] { "FAA-XR3.exe", argument };
            Assert.That(Call("RequestsMixedReality", new object[] { args }), Is.EqualTo(expected));
        }

        [TestCase(1f)]
        [TestCase(2f)]
        [TestCase(.5f)]
        public void HeadlockedHudHasFixedAngularSizeIndependentOfCameraScale(float parentScale)
        {
            var cameraHost = new GameObject("MR view", typeof(Camera));
            var hudHost = new GameObject("HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            try
            {
                Camera camera = cameraHost.GetComponent<Camera>();
                camera.transform.localScale = Vector3.one * parentScale;
                camera.transform.SetPositionAndRotation(new Vector3(21, 3, -5), Quaternion.Euler(7, 31, 0));
                Canvas canvas = hudHost.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                CanvasScaler scaler = hudHost.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(800, 600);
                Call("FitHeadlockedCanvas", canvas, camera, scaler);
                RectTransform rect = (RectTransform)canvas.transform;
                Vector3 left = rect.TransformPoint(new Vector3(-960, 0, 0)) - camera.transform.position;
                Vector3 right = rect.TransformPoint(new Vector3(960, 0, 0)) - camera.transform.position;
                Assert.That(Vector3.Angle(left, right), Is.EqualTo(60f).Within(.001f));
                Assert.That(Vector3.Distance(rect.position, camera.transform.position), Is.EqualTo(1.5f).Within(.001f));
                Assert.That(rect.rect.size, Is.EqualTo(new Vector2(1920, 1080)));
                Assert.That(rect.parent, Is.EqualTo(camera.transform));
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
                Assert.That(canvas.worldCamera, Is.EqualTo(camera));
                Assert.That(scaler.enabled, Is.False);
                Assert.That(canvas.scaleFactor, Is.EqualTo(1f));
            }
            finally { UnityEngine.Object.DestroyImmediate(hudHost); UnityEngine.Object.DestroyImmediate(cameraHost); }
        }

        [Test]
        public void InvalidGeometryInputsUseFiniteClampedDimensions()
        {
            Vector2 fallback = (Vector2)Call("CanvasMeters", float.NaN, float.PositiveInfinity);
            Vector2 expected = (Vector2)Call("CanvasMeters", 1.5f, 60f);
            Assert.That(fallback, Is.EqualTo(expected));
            Vector2 clamped = (Vector2)Call("CanvasMeters", -10f, 200f);
            Assert.That(clamped, Is.EqualTo((Vector2)Call("CanvasMeters", .5f, 80f)));
            Assert.That(fallback.y / fallback.x, Is.EqualTo(1080f / 1920f).Within(.00001f));
        }

        [Test]
        public void PresentationPreservesWorldPanelsAndRestoresCameraAndHudOnDisable()
        {
            var cameraHost = new GameObject("MR view", typeof(Camera));
            var otherHost = new GameObject("Opaque overlay view", typeof(Camera));
            var textureHost = new GameObject("Radar texture view", typeof(Camera));
            var hudHost = new GameObject("HUD", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            var panelHost = new GameObject("Cockpit panel", typeof(RectTransform), typeof(Canvas));
            var presenterHost = new GameObject("MR presenter");
            var texture = new RenderTexture(16, 16, 0);
            try
            {
                Camera camera = cameraHost.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.Skybox; camera.backgroundColor = Color.blue;
                camera.cullingMask = 12345; camera.allowHDR = true;
                Camera other = otherHost.GetComponent<Camera>();
                Camera radar = textureHost.GetComponent<Camera>(); radar.targetTexture = texture;
                Canvas hud = hudHost.GetComponent<Canvas>(); hud.renderMode = RenderMode.ScreenSpaceOverlay;
                hudHost.layer = 5;
                Canvas panel = panelHost.GetComponent<Canvas>(); panel.renderMode = RenderMode.WorldSpace;
                panel.transform.position = new Vector3(3, 2, 1);
                Vector3 panelPosition = panel.transform.position;
                Component presenter = presenterHost.AddComponent(Presenter);
                Presenter.GetField("view", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(presenter, camera);
                Presenter.GetMethod("DiscoverPresentation", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(presenter, null);
                Presenter.GetMethod("ApplyPresentation", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(presenter, null);
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.SolidColor));
                Assert.That(camera.backgroundColor.a, Is.Zero);
                Assert.That(camera.cullingMask, Is.EqualTo(1 << 30));
                Assert.That(camera.allowHDR, Is.False);
                Assert.That(other.enabled, Is.False);
                Assert.That(radar.enabled, Is.True, "Render-to-texture instrument cameras must keep running.");
                Assert.That(panel.transform.position, Is.EqualTo(panelPosition));
                Assert.That(panel.transform.parent, Is.Null);
                Assert.That(panelHost.layer, Is.EqualTo(30));
                // EditMode does not dispatch runtime MonoBehaviour lifecycle callbacks.
                Presenter.GetMethod("OnDisable", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(presenter, null);
                UnityEngine.Object.DestroyImmediate(presenterHost);
                Assert.That(hud.renderMode, Is.EqualTo(RenderMode.ScreenSpaceOverlay));
                Assert.That(hud.transform.parent, Is.Null);
                Assert.That(hudHost.layer, Is.EqualTo(5));
                Assert.That(hudHost.GetComponent<CanvasScaler>().enabled, Is.True);
                Assert.That(camera.clearFlags, Is.EqualTo(CameraClearFlags.Skybox));
                Assert.That(camera.cullingMask, Is.EqualTo(12345));
                Assert.That(camera.backgroundColor, Is.EqualTo(Color.blue));
                Assert.That(camera.allowHDR, Is.True);
                Assert.That(other.enabled, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(presenterHost);
                UnityEngine.Object.DestroyImmediate(hudHost); UnityEngine.Object.DestroyImmediate(panelHost);
                UnityEngine.Object.DestroyImmediate(cameraHost); UnityEngine.Object.DestroyImmediate(otherHost);
                UnityEngine.Object.DestroyImmediate(textureHost); UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
