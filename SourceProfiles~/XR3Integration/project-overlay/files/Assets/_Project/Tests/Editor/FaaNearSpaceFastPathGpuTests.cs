using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>Private enabled-camera/pair-cost checks, not Windows or headset FPS acceptance.</summary>
    public sealed class FaaNearSpaceFastPathGpuTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private delegate bool Project(Transform reference, Vector3 point, Camera view, out Vector3 screen);
        private delegate bool Density(Transform reference, Camera view, out float pixels);
        private delegate bool Inverse(RectTransform reference, Vector2 screen, Camera view, out Vector2 local);
        private delegate bool LocalMatrix(Transform child, Transform ancestor, out Matrix4x4 matrix);
        private static float sink;

        [Serializable] private sealed class PixelSample
        {
            public int frame, greenPixels;
            public Vector2 intended, observed;
            public float baselineDifferencePixels, inverseErrorUnits;
        }
        [Serializable] private sealed class GpuReport
        {
            public string engine, graphics, method;
            public float maximumRenderedErrorPixels, maximumBaselineDifferencePixels, maximumInverseErrorUnits;
            public float maximumRenderedBoundsErrorPixels;
            public float residualXRangePixels, residualYRangePixels;
            public long nearViewHits, nearLocalHits;
            public PixelSample[] samples;
        }
        [Serializable] private sealed class CostSample
        {
            public int frame, bundles, helperCalls, failures;
            public double microseconds, microsecondsPerBundle;
            public float checksum;
        }
        [Serializable] private sealed class CostArm
        {
            public string name;
            public double medianMicrosecondsPerBundle, p95MicrosecondsPerBundle;
            public int timedHelperCalls;
            public CostSample[] samples;
        }
        [Serializable] private sealed class CostReport
        {
            public string engine, graphics, method, qualification;
            public int frames, dynamicRects, repetitionsPerFrame, aircraftPoseUpdates, uiPoseUpdates;
            public bool diagnosticsEnabledDuringTiming;
            public long proofNearViewHits, proofNearLocalHits;
            public CostArm baseline, fast;
        }

        private sealed class Methods
        {
            public readonly string Name;
            public readonly Project Project;
            public readonly Density Density;
            public readonly Inverse Inverse;
            public readonly LocalMatrix Matrix;
            public Methods(bool baseline)
            {
                Name = baseline ? "archived-manual" : "owned-near-native";
                Type projection = baseline ? typeof(FAA.NearSpaceBaseline.IndicatorCanvasProjection) : T("IndicatorSystem.Core.IndicatorCanvasProjection");
                Type geometry = baseline ? typeof(FAA.NearSpaceBaseline.FaaCanvasLocalGeometry) : T("FAA.Customization.FaaCanvasLocalGeometry");
                Project = (Project)Delegate.CreateDelegate(typeof(Project), projection.GetMethod("TryProjectLocal", Any));
                Density = (Density)Delegate.CreateDelegate(typeof(Density), projection.GetMethod("TryPixelsPerReferenceUnit", Any));
                Inverse = (Inverse)Delegate.CreateDelegate(typeof(Inverse), projection.GetMethod("TryScreenToLocal", Any));
                Matrix = (LocalMatrix)Delegate.CreateDelegate(typeof(LocalMatrix), geometry.GetMethod("TryMatrix", Any));
            }
        }

        private sealed class Scene : IDisposable
        {
            private readonly object fixture;
            private readonly Type fixtureType;
            private readonly GameObject mrHost;
            private readonly Component mr;
            private readonly RenderTexture texture;
            public readonly GameObject Aircraft, Head, CanvasHost;
            public readonly Component Rig;
            public readonly Camera View;
            public readonly Canvas Canvas;
            public readonly Matrix4x4 Projection;
            public RenderTexture Texture => texture;
            public Scene()
            {
                fixtureType = typeof(FaaNearRenderSpaceIntegrationTests).GetNestedType("Fixture", BindingFlags.NonPublic);
                fixture = Activator.CreateInstance(fixtureType, true);
                Aircraft = (GameObject)fixtureType.GetField("Aircraft").GetValue(fixture);
                Head = (GameObject)fixtureType.GetField("Head").GetValue(fixture);
                Rig = (Component)fixtureType.GetField("Rig").GetValue(fixture);
                View = Head.GetComponent<Camera>();
                foreach (var script in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    if (script != null && script.GetType().FullName == "FAA.Headset.XR3HeadsetCompatibility") script.enabled = false;
                mrHost = new GameObject("Private near fast-path MR owner"); mrHost.SetActive(false);
                mr = mrHost.AddComponent(T("FAA.Headset.XR3MixedRealityPresentation")); Set(mr, "view", View);
                CanvasHost = new GameObject("Owned dynamic canvas", typeof(RectTransform), typeof(Canvas));
                Canvas = CanvasHost.GetComponent<Canvas>(); Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                texture = new RenderTexture(960, 540, 24, RenderTextureFormat.ARGB32); texture.Create();
                View.targetTexture = texture; View.pixelRect = new Rect(0, 0, 960, 540); View.aspect = 16f / 9f;
                View.nearClipPlane = .03f; View.farClipPlane = 20000; View.allowHDR = false; View.allowMSAA = false;
                float vertical = Mathf.Atan(Mathf.Tan(30 * Mathf.Deg2Rad) / View.aspect) * 2f * Mathf.Rad2Deg;
                Projection = Matrix4x4.Perspective(vertical, View.aspect, .03f, 20000);
                var projection = Projection; projection.m02 = .11f; projection.m12 = -.035f; Projection = projection;
                View.projectionMatrix = Projection;
                Set(mr, "<PassthroughActive>k__BackingField", true);
                Call(mr, "DiscoverPresentation"); Call(mr, "ApplyPresentation");
                UnityEngine.Canvas.ForceUpdateCanvases();
            }
            public void Move(int frame)
            {
                float t = frame * .037f;
                Aircraft.transform.SetPositionAndRotation(new Vector3(24500.1f + frame * 37.1f, 2500 + Mathf.Sin(t) * 5, -51000.2f + frame * 21.2f),
                    Quaternion.Euler(-2 + Mathf.Sin(t), 55 + t * 13, Mathf.Sin(t * .7f) * 3));
                Call(Rig, "UpdateNativeXrOrigin");
                Assert.That(View.projectionMatrix, Is.EqualTo(Projection), "The helper must preserve calibrated asymmetric projection.");
            }
            public void Dispose()
            {
                View.targetTexture = null;
                Set(mr, "<PassthroughActive>k__BackingField", false); Call(mr, "OnDisable");
                Object.DestroyImmediate(CanvasHost); Object.DestroyImmediate(mrHost);
                ((IDisposable)fixture).Dispose(); Object.DestroyImmediate(texture);
            }
        }

        private static Type T(string name, string assembly = "Assembly-CSharp") => Type.GetType(name + ", " + assembly, true);
        private static object Call(object owner, string name, params object[] values) => owner.GetType().GetMethod(name, Any).Invoke(owner, values);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Any).SetValue(owner, value);
        private static Type Space => T("AircraftControl.Camera.AircraftViewRenderSpace", "AircraftControl");
        private static void Diagnostics(bool enabled) => Space.GetProperty("DiagnosticsEnabled", Any).SetValue(null, enabled);
        private static void ResetDiagnostics() => Space.GetMethod("ResetGeometryDiagnostics", Any).Invoke(null, null);
        private static long Count(string name) => Convert.ToInt64(Space.GetProperty(name, Any).GetValue(null));
        private static string Output()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../validation-near-fastpath"));
            Directory.CreateDirectory(path); return path;
        }
        private static RectTransform Rect(string name, Transform parent, Vector2 size)
        {
            var host = new GameObject(name, typeof(RectTransform)); host.layer = 30; host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = size; return rect;
        }
        private static Vector2 Pixels(PixelSample sample) => sample.observed - sample.intended;

        [UnityTest, Explicit("Private normal enabled-camera GPU equivalence; run this exact method.")]
        public IEnumerator EnabledCameraMatchesProjectionAndInverseDuringNearOriginFlightAndDynamicUi()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Actual GPU required");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var scene = new Scene(); var readback = new Texture2D(960, 540, TextureFormat.RGB24, false);
            var baseline = new Methods(true); var fast = new Methods(false); var samples = new List<PixelSample>();
            float renderedError = 0, baselineError = 0, inverseError = 0, boundsError = 0;
            try
            {
                var group = Rect("Dynamic instrument group", scene.CanvasHost.transform, new Vector2(700, 500));
                group.anchoredPosition = new Vector2(-180, 30);
                var nested = Rect("Nested dynamic group", group, new Vector2(400, 300));
                var marker = Rect("Rendered green reference marker", nested, new Vector2(28, 28));
                marker.gameObject.AddComponent<CanvasRenderer>(); marker.gameObject.AddComponent<Image>().color = Color.green;
                Diagnostics(true); ResetDiagnostics();
                for (int n = -5; n < 80; n++)
                {
                    scene.Move(n); float t = n * .11f;
                    group.anchoredPosition = new Vector2(-180 + Mathf.Sin(t) * 60, 30 + Mathf.Cos(t * .7f) * 35);
                    group.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * .6f) * 7);
                    nested.anchoredPosition = new Vector2(Mathf.Sin(t * .8f) * 35, Mathf.Cos(t * .9f) * 25);
                    nested.localScale = new Vector3(.95f + Mathf.Sin(t) * .025f, 1.05f + Mathf.Cos(t) * .02f, 1);
                    marker.sizeDelta = new Vector2(28 + (n & 3), 28 + (n & 1));
                    Canvas.ForceUpdateCanvases();
                    yield return null; yield return null; // normal enabled Camera rendering; never Camera.Render
                    if (n < 0) continue;
                    Vector3 headLocal = scene.Head.transform.localPosition; Quaternion headRotation = scene.Head.transform.localRotation;
                    Assert.That(baseline.Project(marker, Vector3.zero, scene.View, out var oldScreen), Is.True);
                    Assert.That(fast.Project(marker, Vector3.zero, scene.View, out var screen), Is.True);
                    Assert.That(fast.Inverse(marker, screen, scene.View, out var local), Is.True);
                    Assert.That(fast.Density(marker, scene.View, out var density), Is.True); Assert.That(density, Is.GreaterThan(0));
                    Assert.That(fast.Matrix(marker, scene.CanvasHost.transform, out var matrix), Is.True);
                    object[] batchArgs={marker,marker.rect,scene.View,null,false};
                    Assert.That((bool)T("IndicatorSystem.Core.IndicatorCanvasProjection").GetMethod("TryProjectLocalBounds").Invoke(null,batchArgs),Is.True);
                    Rect projectedBounds=(Rect)batchArgs[3];
                    Assert.That(scene.Head.transform.localPosition, Is.EqualTo(headLocal));
                    Assert.That(scene.Head.transform.localRotation, Is.EqualTo(headRotation));
                    Assert.That(scene.View.projectionMatrix, Is.EqualTo(scene.Projection));
                    var previous = RenderTexture.active; RenderTexture.active = scene.Texture;
                    readback.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); readback.Apply(); RenderTexture.active = previous;
                    var pixels = readback.GetPixels32(); int left = 960, right = -1, bottom = 540, top = -1, count = 0;
                    int x0 = Mathf.Max(0, Mathf.FloorToInt(screen.x) - 40), x1 = Mathf.Min(959, Mathf.CeilToInt(screen.x) + 40);
                    int y0 = Mathf.Max(0, Mathf.FloorToInt(screen.y) - 40), y1 = Mathf.Min(539, Mathf.CeilToInt(screen.y) + 40);
                    for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++)
                    {
                        Color32 color = pixels[y * 960 + x]; if (color.g < 100 || color.g < color.r * 2 || color.g < color.b * 2) continue;
                        left = Mathf.Min(left, x); right = Mathf.Max(right, x); bottom = Mathf.Min(bottom, y); top = Mathf.Max(top, y); count++;
                    }
                    Assert.That(count, Is.GreaterThan(20), "The actual normal-frame marker must be visible, not just mathematically projectable.");
                    float boundDelta=Mathf.Max(Mathf.Abs(projectedBounds.xMin-(left+.5f)),Mathf.Abs(projectedBounds.xMax-(right+.5f)),Mathf.Abs(projectedBounds.yMin-(bottom+.5f)),Mathf.Abs(projectedBounds.yMax-(top+.5f)));
                    boundsError=Mathf.Max(boundsError,boundDelta);
                    var sample = new PixelSample { frame = n, greenPixels = count, intended = screen,
                        observed = new Vector2((left + right) * .5f + .5f, (bottom + top) * .5f + .5f),
                        baselineDifferencePixels = Vector2.Distance(oldScreen, screen), inverseErrorUnits = local.magnitude };
                    samples.Add(sample); renderedError = Mathf.Max(renderedError, Pixels(sample).magnitude);
                    baselineError = Mathf.Max(baselineError, sample.baselineDifferencePixels); inverseError = Mathf.Max(inverseError, sample.inverseErrorUnits);
                    if (n == 0 || n == 39 || n == 79) File.WriteAllBytes(Path.Combine(Output(), "near-fast-gpu-" + n.ToString("D3") + ".png"), readback.EncodeToPNG());
                }
                float xmin = float.PositiveInfinity, xmax = float.NegativeInfinity, ymin = float.PositiveInfinity, ymax = float.NegativeInfinity;
                foreach (var sample in samples) { var e = Pixels(sample); xmin = Mathf.Min(xmin, e.x); xmax = Mathf.Max(xmax, e.x); ymin = Mathf.Min(ymin, e.y); ymax = Mathf.Max(ymax, e.y); }
                var report = new GpuReport { engine = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName,
                    method = "80 enabled-camera RenderTexture frames after5 warmups; actual MR binding, distant moving data aircraft and dynamic nested UI; archived/manual versus production projection; no Camera.Render and no optical/wearer claim.",
                    maximumRenderedErrorPixels = renderedError, maximumBaselineDifferencePixels = baselineError, maximumInverseErrorUnits = inverseError,maximumRenderedBoundsErrorPixels=boundsError,
                    residualXRangePixels = xmax - xmin, residualYRangePixels = ymax - ymin, nearViewHits = Count("NearViewMatrixHits"), nearLocalHits = Count("NearLocalMatrixHits"), samples = samples.ToArray() };
                File.WriteAllText(Path.Combine(Output(), "enabled-camera-gpu.json"), JsonUtility.ToJson(report, true));
                Assert.That(report.nearViewHits, Is.GreaterThan(0)); Assert.That(report.nearLocalHits, Is.GreaterThan(0));
                Assert.That(baselineError, Is.LessThan(.05f)); Assert.That(inverseError, Is.LessThan(.02f));
                Assert.That(renderedError, Is.LessThanOrEqualTo(1.1f));
                Assert.That(boundsError,Is.LessThanOrEqualTo(1.6f),"Four-corner AABB covers the actual rotated/sheared marker within raster quantization.");
                Assert.That(report.residualXRangePixels, Is.LessThanOrEqualTo(1.1f)); Assert.That(report.residualYRangePixels, Is.LessThanOrEqualTo(1.1f));
            }
            finally { Diagnostics(false); Object.DestroyImmediate(readback); scene.Dispose(); }
            yield return new ExitPlayMode();
        }

        private static CostSample Measure(Methods methods, RectTransform[] rects, Transform canvas, Camera view, int frame, int repeats)
        {
            int failures = 0; float checksum = 0; long start = Stopwatch.GetTimestamp();
            for (int pass = 0; pass < repeats; pass++) foreach (var rect in rects)
            {
                bool p = methods.Project(rect, Vector3.zero, view, out var screen);
                bool d = methods.Density(rect, view, out var density);
                bool i = methods.Inverse(rect, screen, view, out var local);
                bool m = methods.Matrix(rect, canvas, out var matrix);
                if (!p || !d || !i || !m) failures++;
                checksum += screen.x * .001f + screen.y * .001f + density + local.x + local.y + matrix.m03 * .0001f;
            }
            long ticks = Stopwatch.GetTimestamp() - start; sink = checksum;
            int bundles = repeats * rects.Length; double micros = ticks * 1000000.0 / Stopwatch.Frequency;
            return new CostSample { frame = frame, bundles = bundles, helperCalls = bundles * 4, failures = failures,
                microseconds = micros, microsecondsPerBundle = micros / bundles, checksum = checksum };
        }
        private static CostArm Summarize(string name, List<CostSample> values)
        {
            var sorted = new List<double>(); int calls = 0;
            foreach (var value in values) { sorted.Add(value.microsecondsPerBundle); calls += value.helperCalls; }
            sorted.Sort();
            return new CostArm { name = name, medianMicrosecondsPerBundle = sorted[sorted.Count / 2],
                p95MicrosecondsPerBundle = sorted[Mathf.Min(sorted.Count - 1, Mathf.CeilToInt(sorted.Count * .95f) - 1)], timedHelperCalls = calls, samples = values.ToArray() };
        }

        [UnityTest, Explicit("Private paired moving-aircraft/dynamic-UI cost measurement; no predetermined speedup.")]
        public IEnumerator MovingAircraftDynamicUiReportsPairedBaselineAndFastGeometryCost()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Normal enabled-camera fixture requires a graphics device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var scene = new Scene(); var baseline = new Methods(true); var fast = new Methods(false);
            var oldSamples = new List<CostSample>(); var newSamples = new List<CostSample>();
            const int frames = 24, repetitions = 4, count = 24;
            var rects = new RectTransform[count]; var groups = new RectTransform[6]; long proofView = 0, proofLocal = 0;
            try
            {
                for (int n = 0; n < groups.Length; n++) groups[n] = Rect("Dynamic radar group " + n, scene.CanvasHost.transform, new Vector2(400, 300));
                for (int n = 0; n < rects.Length; n++)
                {
                    var wrapper = Rect("Nested layout " + n, groups[n % groups.Length], new Vector2(200, 100));
                    rects[n] = Rect("Dynamic UI element " + n, wrapper, new Vector2(45, 22));
                    rects[n].gameObject.AddComponent<CanvasRenderer>(); rects[n].gameObject.AddComponent<Image>().color = new Color(.05f, .15f, .65f, .8f);
                }
                Diagnostics(false);
                for (int frame = -3; frame < frames; frame++)
                {
                    scene.Move(frame); float t = frame * .13f;
                    for (int n = 0; n < groups.Length; n++)
                    {
                        groups[n].anchoredPosition = new Vector2((n % 3 - 1) * 380 + Mathf.Sin(t + n) * 15, (n / 3 - .5f) * 260);
                        groups[n].localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * .6f + n) * 5);
                    }
                    for (int n = 0; n < rects.Length; n++)
                    {
                        rects[n].anchoredPosition = new Vector2((n / 6 - 1.5f) * 48 + Mathf.Sin(t + n) * 11, Mathf.Cos(t * .7f + n) * 19);
                        rects[n].sizeDelta = new Vector2(45 + (frame + n & 3), 22 + (n & 1));
                        rects[n].localScale = new Vector3(.98f + Mathf.Sin(t + n) * .02f, 1.02f + Mathf.Cos(t + n) * .015f, 1);
                    }
                    Canvas.ForceUpdateCanvases(); yield return null; yield return null;
                    // This branch proof is outside timing; counters remain off in both measured arms.
                    Diagnostics(true); ResetDiagnostics();
                    var proof = Measure(fast, new[] { rects[0] }, scene.CanvasHost.transform, scene.View, frame, 1);
                    proofView += Count("NearViewMatrixHits"); proofLocal += Count("NearLocalMatrixHits"); Diagnostics(false);
                    Assert.That(proof.failures, Is.Zero);
                    CostSample before, after;
                    if ((frame & 1) == 0) { before = Measure(baseline, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); after = Measure(fast, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); }
                    else { after = Measure(fast, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); before = Measure(baseline, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); }
                    Assert.That(before.failures, Is.Zero); Assert.That(after.failures, Is.Zero);
                    Assert.That(Mathf.Abs(before.checksum - after.checksum), Is.LessThan(.2f), "Paired geometry remains equivalent during measured UI/aircraft changes.");
                    if (frame < 0) continue;
                    oldSamples.Add(before); newSamples.Add(after);
                }
                var report = new CostReport { engine = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName,
                    method = "Cached delegates (no reflection inside timing), alternating arm order per frame, actual near-MR camera/owner,24moving-aircraft poses and24dynamic nested UI transforms, project+density+inverse+canvas-matrix bundle;3warmup frames.",
                    qualification = "Mac Unity Editor geometry-call cost only; no minimum speedup imposed, no Windows/headset FPS or crash-resolution claim.",
                    frames = frames, dynamicRects = count, repetitionsPerFrame = repetitions, aircraftPoseUpdates = frames,
                    uiPoseUpdates = frames * (count + groups.Length), diagnosticsEnabledDuringTiming = false, proofNearViewHits = proofView, proofNearLocalHits = proofLocal,
                    baseline = Summarize(baseline.Name, oldSamples), fast = Summarize(fast.Name, newSamples) };
                File.WriteAllText(Path.Combine(Output(), "paired-dynamic-geometry-cost.json"), JsonUtility.ToJson(report, true));
                TestContext.WriteLine("Geometry microseconds/bundle baseline=" + report.baseline.medianMicrosecondsPerBundle + ", fast=" + report.fast.medianMicrosecondsPerBundle);
                Assert.That(proofView, Is.GreaterThan(0)); Assert.That(proofLocal, Is.GreaterThan(0));
            }
            finally { Diagnostics(false); scene.Dispose(); }
            yield return new ExitPlayMode();
        }
        [UnityTest, Explicit("Private paired moving-aircraft/dynamic-UI cost measurement; no predetermined speedup.")]
        public IEnumerator TwentyFourRegisteredRootsReportPairedDynamicGeometryCost()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Normal enabled-camera fixture requires a graphics device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var scene = new Scene(); var baseline = new Methods(true); var fast = new Methods(false);
            var oldSamples = new List<CostSample>(); var newSamples = new List<CostSample>();
            const int frames = 24, repetitions = 4, count = 24;
            var rects = new RectTransform[count]; var groups = new RectTransform[6]; long proofView = 0, proofLocal = 0;
            var extraRoots = new List<GameObject>();
            try
            {
                for (int n = 0; n < groups.Length; n++) groups[n] = Rect("Dynamic radar group " + n, scene.CanvasHost.transform, new Vector2(400, 300));
                for (int n = 0; n < rects.Length; n++)
                {
                    var wrapper = Rect("Nested layout " + n, groups[n % groups.Length], new Vector2(200, 100));
                    rects[n] = Rect("Dynamic UI element " + n, wrapper, new Vector2(45, 22));
                    rects[n].gameObject.AddComponent<CanvasRenderer>(); rects[n].gameObject.AddComponent<Image>().color = new Color(.05f, .15f, .65f, .8f);
                }
                // Keep the hot root at the end, with a nested same-owner root taking precedence.
                Space.GetMethod("ReleaseRenderRoot").Invoke(null,new object[]{scene.CanvasHost.transform,scene.View});
                for(int r=0;r<22;r++)
                {
                    var host=new GameObject("Representative registered canvas "+r,typeof(RectTransform),typeof(Canvas));
                    host.transform.SetParent(scene.View.transform,false);host.transform.localPosition=new Vector3(0,0,1.5f+r*.005f);host.transform.localScale=Vector3.one*.001f;
                    host.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;host.GetComponent<Canvas>().worldCamera=scene.View;
                    Space.GetMethod("RegisterRenderRoot").Invoke(null,new object[]{host.transform,scene.View});extraRoots.Add(host);
                }
                Space.GetMethod("RegisterRenderRoot").Invoke(null,new object[]{scene.CanvasHost.transform,scene.View});
                groups[0].gameObject.AddComponent<Canvas>().renderMode=RenderMode.WorldSpace;
                Space.GetMethod("RegisterRenderRoot").Invoke(null,new object[]{groups[0].transform,scene.View});
                var registry=(System.Collections.IDictionary)Space.GetField("renderRoots",Any).GetValue(null);
                Assert.That(registry.Count,Is.EqualTo(24),"The representative query must inspect24 registrations, not one.");
                Diagnostics(false);
                for (int frame = -3; frame < frames; frame++)
                {
                    scene.Move(frame); float t = frame * .13f;
                    for (int n = 0; n < groups.Length; n++)
                    {
                        groups[n].anchoredPosition = new Vector2((n % 3 - 1) * 380 + Mathf.Sin(t + n) * 15, (n / 3 - .5f) * 260);
                        groups[n].localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * .6f + n) * 5);
                    }
                    for (int n = 0; n < rects.Length; n++)
                    {
                        rects[n].anchoredPosition = new Vector2((n / 6 - 1.5f) * 48 + Mathf.Sin(t + n) * 11, Mathf.Cos(t * .7f + n) * 19);
                        rects[n].sizeDelta = new Vector2(45 + (frame + n & 3), 22 + (n & 1));
                        rects[n].localScale = new Vector3(.98f + Mathf.Sin(t + n) * .02f, 1.02f + Mathf.Cos(t + n) * .015f, 1);
                    }
                    Canvas.ForceUpdateCanvases(); yield return null; yield return null;
                    // This branch proof is outside timing; counters remain off in both measured arms.
                    Diagnostics(true); ResetDiagnostics();
                    var proof = Measure(fast, new[] { rects[0] }, scene.CanvasHost.transform, scene.View, frame, 1);
                    proofView += Count("NearViewMatrixHits"); proofLocal += Count("NearLocalMatrixHits"); Diagnostics(false);
                    Assert.That(proof.failures, Is.Zero);
                    CostSample before, after;
                    if ((frame & 1) == 0) { before = Measure(baseline, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); after = Measure(fast, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); }
                    else { after = Measure(fast, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); before = Measure(baseline, rects, scene.CanvasHost.transform, scene.View, frame, repetitions); }
                    Assert.That(before.failures, Is.Zero); Assert.That(after.failures, Is.Zero);
                    Assert.That(Mathf.Abs(before.checksum - after.checksum), Is.LessThan(.2f), "Paired geometry remains equivalent during measured UI/aircraft changes.");
                    if (frame < 0) continue;
                    oldSamples.Add(before); newSamples.Add(after);
                }
                var report = new CostReport { engine = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName,
                    method = "Cached delegates (no reflection inside timing), alternating arm order per frame, actual near-MR camera/owner,24moving-aircraft poses,24registeredcanvasroots, hot references under registry entries23/24 including nestedowner, and24dynamic nested UI transforms, project+density+inverse+canvas-matrix bundle;3warmup frames.",
                    qualification = "Mac Unity Editor geometry-call cost only; no minimum speedup imposed, no Windows/headset FPS or crash-resolution claim.",
                    frames = frames, dynamicRects = count, repetitionsPerFrame = repetitions, aircraftPoseUpdates = frames,
                    uiPoseUpdates = frames * (count + groups.Length), diagnosticsEnabledDuringTiming = false, proofNearViewHits = proofView, proofNearLocalHits = proofLocal,
                    baseline = Summarize(baseline.Name, oldSamples), fast = Summarize(fast.Name, newSamples) };
                File.WriteAllText(Path.Combine(Output(), "paired-dynamic-geometry-cost-24roots.json"), JsonUtility.ToJson(report, true));
                TestContext.WriteLine("Geometry microseconds/bundle baseline=" + report.baseline.medianMicrosecondsPerBundle + ", fast=" + report.fast.medianMicrosecondsPerBundle);
                Assert.That(proofView, Is.GreaterThan(0)); Assert.That(proofLocal, Is.GreaterThan(0));
            }
            finally { Diagnostics(false); foreach(var root in extraRoots) Object.DestroyImmediate(root); scene.Dispose(); }
            yield return new ExitPlayMode();
        }
    }
}
