using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaNearRenderProbeRing : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); const int segments = 256;
            for (int n = 0; n < segments; n++)
            {
                float a = n * 2f * Mathf.PI / segments, b = (n + 1) * 2f * Mathf.PI / segments;
                int k = vh.currentVertCount;
                foreach (Vector2 p in new[] { new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 93.4f,
                    new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 94.6f, new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 94.6f,
                    new Vector2(Mathf.Cos(b), Mathf.Sin(b)) * 93.4f }) vh.AddVert(p, Color.green, Vector2.zero);
                vh.AddTriangle(k, k + 1, k + 2); vh.AddTriangle(k, k + 2, k + 3);
            }
        }
    }

    public sealed class FaaNearRenderSpaceGpuTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        [Serializable] private sealed class Sample { public int frame, ringPixels; public float center; }
        [Serializable] private sealed class Arm { public string name; public float minimum, maximum, maximumAdjacentStep; public Sample[] samples; }
        [Serializable] private sealed class Report { public string engine, graphics, method; public Arm[] arms; }
        private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Any).SetValue(owner, value);
        private static object Call(object owner, string name, params object[] values) => owner.GetType().GetMethod(name, Any).Invoke(owner, values);

        [UnityTest, Explicit("Dedicated normal enabled-camera rendering regression; run this exact method.")]
        public IEnumerator ActualPresentationKeepsHeadlockedEdgesStableAtLargeGeographicPositions()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires actual graphics device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var fixtureType = typeof(FaaNearRenderSpaceIntegrationTests).GetNestedType("Fixture", BindingFlags.NonPublic);
            object fixture = Activator.CreateInstance(fixtureType, true);
            var aircraft = (GameObject)fixtureType.GetField("Aircraft").GetValue(fixture);
            var head = (GameObject)fixtureType.GetField("Head").GetValue(fixture);
            var rig = (Component)fixtureType.GetField("Rig").GetValue(fixture);
            var view = head.GetComponent<Camera>();
            var mrHost = new GameObject("Private MR presentation regression owner"); mrHost.SetActive(false);
            Component mr = mrHost.AddComponent(T("FAA.Headset.XR3MixedRealityPresentation")); Set(mr, "view", view);
            var canvasHost = new GameObject("Normal-frame actual fitted HUD", typeof(RectTransform), typeof(Canvas)); canvasHost.layer = 30;
            var ring = new GameObject("Static IAS precision ring", typeof(RectTransform), typeof(CanvasRenderer), typeof(FaaNearRenderProbeRing));
            ring.transform.SetParent(canvasHost.transform, false); ring.layer = 30;
            ((RectTransform)ring.transform).anchoredPosition = new Vector2(-320, 20);
            ((RectTransform)ring.transform).sizeDelta = new Vector2(200, 200);
            var texture = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            var readback = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            var arms = new List<Arm>();
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../render-origin-gpu")); Directory.CreateDirectory(output);
            try
            {
                foreach (var script in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    if (script != null && script.GetType().FullName == "FAA.Headset.XR3HeadsetCompatibility") script.enabled = false;
                view.cullingMask = 1 << 30; view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = Color.black;
                view.nearClipPlane = .03f; view.farClipPlane = 20000; view.allowHDR = false; view.allowMSAA = false;
                view.aspect = 1920f / 1080f;
                float vertical = Mathf.Atan(Mathf.Tan(30 * Mathf.Deg2Rad) / view.aspect) * 2f * Mathf.Rad2Deg;
                Matrix4x4 projection = Matrix4x4.Perspective(vertical, view.aspect, .03f, 20000); projection[0, 2] = .08f; view.projectionMatrix = projection;
                texture.Create(); view.targetTexture = texture;
                T("FAA.Headset.XR3MixedRealityPresentation").GetMethod("FitHeadlockedCanvas", Any).Invoke(null, new object[] { canvasHost.GetComponent<Canvas>(), view, null });
                Canvas.ForceUpdateCanvases();
                Vector3 local = head.transform.localPosition; Quaternion rotation = head.transform.localRotation;
                foreach (bool near in new[] { false, true, false })
                {
                    string name = near ? "mr-near-render" : arms.Count == 0 ? "original-data-world" : "restored-data-world";
                    Set(mr, "<PassthroughActive>k__BackingField", near);
                    if (near) Call(mr, "ApplyPresentation"); else Call(mr, "BeforeRender");
                    var samples = new List<Sample>();
                    for (int n = -8; n < 120; n++)
                    {
                        float t = n / 30f;
                        aircraft.transform.SetPositionAndRotation(new Vector3(12466.1f + t * 46, 318.2f, -4424.2f + t * 28), Quaternion.Euler(-2 + Mathf.Sin(t), 55 + t * 7, -3));
                        Call(rig, "UpdateNativeXrOrigin");
                        yield return null; yield return null;
                        if (n < 0) continue;
                        Assert.That(head.transform.localPosition, Is.EqualTo(local)); Assert.That(head.transform.localRotation, Is.EqualTo(rotation));
                        Assert.That(view.projectionMatrix, Is.EqualTo(projection), "Native/asymmetric calibration is never changed by the space owner.");
                        var previous = RenderTexture.active; RenderTexture.active = texture;
                        readback.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); readback.Apply(); RenderTexture.active = previous;
                        var data = readback.GetPixels32(); int left = 1920, right = -1, count = 0;
                        for (int y = 450; y < 670; y++) for (int x = 400; x < 700; x++)
                        {
                            Color32 p = data[y * 1920 + x]; if (p.g < 50 || p.g < p.r * 2) continue;
                            left = Mathf.Min(left, x); right = Mathf.Max(right, x); count++;
                        }
                        Assert.That(count, Is.GreaterThan(0)); samples.Add(new Sample { frame = n, center = (left + right) * .5f, ringPixels = count });
                        if (n < 3 || n == 119) File.WriteAllBytes(Path.Combine(output, name + "-" + n.ToString("D3") + ".png"), readback.EncodeToPNG());
                    }
                    var arm = new Arm { name = name, minimum = float.MaxValue, maximum = float.MinValue, samples = samples.ToArray() };
                    for (int n = 0; n < samples.Count; n++)
                    {
                        arm.minimum = Mathf.Min(arm.minimum, samples[n].center); arm.maximum = Mathf.Max(arm.maximum, samples[n].center);
                        if (n > 0) arm.maximumAdjacentStep = Mathf.Max(arm.maximumAdjacentStep, Mathf.Abs(samples[n].center - samples[n - 1].center));
                    }
                    arms.Add(arm); TestContext.WriteLine(name + ": center=" + arm.minimum + ".." + arm.maximum + ", maximum adjacent shift=" + arm.maximumAdjacentStep);
                }
                File.WriteAllText(Path.Combine(output, "actual-presentation-normal-render.json"), JsonUtility.ToJson(new Report { engine = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName,
                    method = "Actual AircraftCameraController/NativeXrOrigin and XR3MixedRealityPresentation fitting/ownership, normal enabled Camera frames; no Camera.Render. 120 pixel samples per arm; large geographic motion and body rotation; original/MR-near/restored sequence.", arms = arms.ToArray() }, true));
                Assert.That(arms[0].maximum - arms[0].minimum, Is.GreaterThanOrEqualTo(2f), "The original-path control must reproduce the precision failure.");
                Assert.That(arms[1].maximum - arms[1].minimum, Is.LessThanOrEqualTo(1f), "Actual near-render MR presentation must keep static geometry stable.");
                Assert.That(arms[2].maximum - arms[2].minimum, Is.GreaterThanOrEqualTo(2f), "Release restores the original geographic contract, not a hidden camera rewrite.");
            }
            finally
            {
                view.targetTexture = null; Set(mr, "<PassthroughActive>k__BackingField", false); Call(mr, "OnDisable");
                Object.DestroyImmediate(canvasHost); Object.DestroyImmediate(mrHost); ((IDisposable)fixture).Dispose();
                Object.DestroyImmediate(readback); Object.DestroyImmediate(texture);
            }
            yield return new ExitPlayMode();
        }
    }
}
