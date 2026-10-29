using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public sealed class TrafficCueRenderOrderTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int CueOrder = 5080;
        private static Type T(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            { var type = assembly.GetType(name); if (type != null) return type; }
            throw new TypeLoadException(name);
        }
        private static ScriptableObject Settings()
        {
            var settings = ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings"));
            settings.GetType().GetField("usePilotCueStyle").SetValue(settings, true);
            settings.GetType().GetField("useCustomPrefabs").SetValue(settings, false);
            return settings;
        }
        private static Component Pool(Canvas canvas, ScriptableObject settings) =>
            (Component)T("IndicatorSystem.Display.IndicatorPool").GetMethod("CreateOnCanvas").Invoke(null, new object[] { canvas, settings });

        [TestCase(0)] [TestCase(100)] [TestCase(5020)] [TestCase(6000)]
        public void AssignedCanvasKeepsCuesAboveConformalLinesAndPreservesHigherAuthoredOrder(int order)
        {
            var host = new GameObject("Assigned cue canvas", typeof(RectTransform), typeof(Canvas));
            var settings = Settings();
            try
            {
                Canvas canvas = host.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.sortingOrder = order;
                host.transform.SetPositionAndRotation(new Vector3(13, 17, 19), Quaternion.Euler(3, 7, 11)); host.transform.localScale = Vector3.one * .002f;
                var position = host.transform.position; var rotation = host.transform.rotation; var scale = host.transform.localScale;
                Pool(canvas, settings);
                Assert.That(canvas.sortingOrder, Is.EqualTo(Mathf.Max(order, CueOrder)));
                Assert.That(host.GetComponentsInChildren<Canvas>(true).Length, Is.EqualTo(1), "Preserve the original canvas and projection context.");
                Assert.That(canvas.renderMode, Is.EqualTo(RenderMode.WorldSpace));
                Assert.That(host.transform.position, Is.EqualTo(position)); Assert.That(host.transform.rotation, Is.EqualTo(rotation)); Assert.That(host.transform.localScale, Is.EqualTo(scale));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(settings); }
        }

        [Test]
        public void RuntimeCanvasFactoryUsesTheSameCueOrder()
        {
            var host = new GameObject("Runtime cue parent"); var settings = Settings();
            try
            {
                var pool = (Component)T("IndicatorSystem.Display.IndicatorPool").GetMethod("CreateWithCanvas").Invoke(null, new object[] { host.transform, settings });
                Assert.That(pool.GetComponentInParent<Canvas>().sortingOrder, Is.EqualTo(CueOrder));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(settings); }
        }

        [TestCase(false)] [TestCase(true)]
        public void ExistingIndicatorReadmissionRepairsAChangedCanvasOrder(bool dataOverload)
        {
            var host = new GameObject("Late normalized cue canvas", typeof(RectTransform), typeof(Canvas));
            var settings = Settings();
            try
            {
                Canvas canvas = host.GetComponent<Canvas>(); Component pool = Pool(canvas, settings);
                object data = Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));
                MethodInfo get = pool.GetType().GetMethod("GetIndicator", dataOverload ? new[] { typeof(string), data.GetType() } : new[] { typeof(string) });
                object[] args = dataOverload ? new[] { (object)"REUSED", data } : new object[] { "REUSED" };
                object first = get.Invoke(pool, args); canvas.sortingOrder = 5020;
                Assert.That(get.Invoke(pool, args), Is.SameAs(first), "Check the early reused-instance path, not only new allocation.");
                Assert.That(canvas.sortingOrder, Is.EqualTo(CueOrder));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(settings); }
        }

        [TestCase(0)] [TestCase(6000)]
        public void RadarNormalizationKeepsIndicatorOrderAboveWorldStrokes(int authoredOrder)
        {
            const string prefix = "Cue ordering normalization ";
            var host = new GameObject(prefix + "owner"); host.SetActive(false);
            var canvasHost = new GameObject(prefix + "indicators", typeof(RectTransform), typeof(Canvas));
            var settings = Settings();
            try
            {
                Canvas canvas = canvasHost.GetComponent<Canvas>(); canvas.sortingOrder = authoredOrder;
                Pool(canvas, settings);
                Component sanitizer = host.AddComponent(T("FAA.Customization.FaaHudRuntimeSanitizer"));
                void Set(string key, object value) => sanitizer.GetType().GetField(key, Any).SetValue(sanitizer, value);
                Set("indicatorCanvasName", canvasHost.name); Set("weatherRadarCanvasName", prefix + "weather"); Set("trafficRadarCanvasName", prefix + "traffic");
                Set("weatherRadarRootName", prefix + "missing weather root"); Set("trafficRadarRootName", prefix + "missing traffic root"); Set("createRadarControlStrips", false);
                sanitizer.GetType().GetMethod("NormalizeRadarPairLayout", Any).Invoke(sanitizer, null);
                Assert.That(canvas.sortingOrder, Is.EqualTo(Mathf.Max(authoredOrder, CueOrder)), "The later sanitizer must agree with the cue presentation policy.");
            }
            finally
            {
                foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (canvas != null && canvas.name.StartsWith(prefix, StringComparison.Ordinal)) UnityEngine.Object.DestroyImmediate(canvas.gameObject);
                UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [TestCase(false, 77, 10)] [TestCase(true, 77, 10)] [TestCase(true, 6000, 10)] [TestCase(false, 77, 6000)]
        public void NestedCanvasUpdatesItsEffectiveSortingOwnerOnly(bool ownOrder, int childOrder, int rootOrder)
        {
            var root = new GameObject("Inherited cue canvas", typeof(RectTransform), typeof(Canvas));
            var child = new GameObject("Nested assigned cue canvas", typeof(RectTransform), typeof(Canvas));
            var settings = Settings();
            try
            {
                child.transform.SetParent(root.transform, false);
                Canvas outer = root.GetComponent<Canvas>(), inner = child.GetComponent<Canvas>();
                outer.renderMode = RenderMode.WorldSpace; outer.sortingOrder = rootOrder;
                inner.overrideSorting = ownOrder; inner.sortingOrder = childOrder;
                int authoredChildOrder = new SerializedObject(inner).FindProperty("m_SortingOrder").intValue;
                Pool(inner, settings);
                Assert.That(outer.sortingOrder, Is.EqualTo(ownOrder ? rootOrder : Mathf.Max(rootOrder, CueOrder)));
                Assert.That(inner.sortingOrder, Is.EqualTo(ownOrder ? Mathf.Max(childOrder, CueOrder) : Mathf.Max(rootOrder, CueOrder)));
                if (!ownOrder) Assert.That(new SerializedObject(inner).FindProperty("m_SortingOrder").intValue, Is.EqualTo(authoredChildOrder), "Inherited order changes without rewriting the nested authored value.");
                Assert.That(inner.overrideSorting, Is.EqualTo(ownOrder));
                Assert.That(root.GetComponentsInChildren<Canvas>(true).Length, Is.EqualTo(2));
                Assert.That(child.transform.parent, Is.EqualTo(root.transform));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); UnityEngine.Object.DestroyImmediate(settings); }
        }

        [UnityTest, Explicit("Dedicated actual-camera GPU ordering regression; run this exact method.")]
        public IEnumerator ReadoutPlateCoversCrossingConformalLineDuringNormalCameraRendering()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("A graphics device is required.");
            if (Resources.Load<Shader>("Shaders/FaaRotorcraftConformal") == null) Assert.Ignore("The optional full integration conformal overlay shader is not imported in this profile.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var cameraHost = new GameObject("Cue ordering test camera", typeof(Camera));
            var canvasHost = new GameObject("Assigned native-style cue canvas", typeof(RectTransform), typeof(Canvas));
            var lineHost = new GameObject("Crossing conformal line", typeof(MeshFilter), typeof(MeshRenderer));
            var settings = Settings(); var texture = new RenderTexture(512, 256, 24); var readback = new Texture2D(512, 256, TextureFormat.RGB24, false);
            Mesh mesh = null; Material material = null; Camera view = cameraHost.GetComponent<Camera>();
            try
            {
                foreach (var script in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    if (script != null && script.GetType().FullName == "FAA.Headset.XR3HeadsetCompatibility") script.enabled = false;
                view.orthographic = true; view.orthographicSize = 1.28f; view.aspect = 2; view.nearClipPlane = .03f; view.farClipPlane = 20;
                view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = Color.black; view.cullingMask = 1 << 31; view.allowHDR = false; view.allowMSAA = false;
                texture.Create(); view.targetTexture = texture;
                Canvas canvas = canvasHost.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = view; canvas.sortingOrder = 0; canvasHost.layer = 31;
                var root = (RectTransform)canvasHost.transform; root.sizeDelta = new Vector2(512, 256); root.localScale = Vector3.one * .01f; root.position = new Vector3(0, 0, 2);
                var pool = Pool(canvas, settings);
                var container = (RectTransform)pool.GetType().GetProperty("Container").GetValue(pool);
                var cue = (Component)T("IndicatorSystem.Display.PilotIndicatorCue").GetMethod("Create").Invoke(null, new object[] { container });
                ((RectTransform)cue.transform).anchoredPosition = new Vector2(-150, 0);
                object data = Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));
                void Set(string key, object value) => data.GetType().GetField(key).SetValue(data, value);
                Set("Type", Enum.Parse(T("IndicatorSystem.Core.IndicatorType"), "Traffic"));
                Set("AircraftType", Enum.Parse(T("TrafficRadar.TrafficRadarDataManager+AircraftType"), "General"));
                Set("Visibility", Enum.Parse(T("IndicatorSystem.Core.IndicatorVisibility"), "OffScreen")); Set("Color", Color.cyan); Set("Label", "ORDER TEST");
                Set("HasReadoutSide", true); Set("ReadoutOnLeft", false); Set("DistanceNM", 10f); Set("RelativeAltitudeFeet", -1000f);
                cue.GetType().GetMethod("PresentWithSettings").Invoke(cue, new[] { data, settings });
                var plate = (RectTransform)cue.GetType().GetField("_plate", Any).GetValue(cue);
                Shader shader = Resources.Load<Shader>("Shaders/FaaRotorcraftConformal"); Assert.That(shader, Is.Not.Null);
                material = new Material(shader); mesh = new Mesh();
                mesh.vertices = new[] { new Vector3(-3, -.03f, 4), new Vector3(3, -.03f, 4), new Vector3(3, .03f, 4), new Vector3(-3, .03f, 4) };
                mesh.colors = new[] { Color.green, Color.green, Color.green, Color.green }; mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 }; mesh.RecalculateBounds();
                lineHost.layer = 31; lineHost.GetComponent<MeshFilter>().sharedMesh = mesh; var renderer = lineHost.GetComponent<MeshRenderer>(); renderer.sharedMaterial = material; renderer.sortingOrder = 5040;
                UnityEngine.Canvas.ForceUpdateCanvases();
                float minimumCovered = 1f, maximumCovered = 0f, minimumExposed = 1f;
                for (int frame = 0; frame < 16; frame++)
                {
                    lineHost.transform.position = new Vector3(0, Mathf.Sin(frame) * .004f, 0);
                    yield return null; yield return null;
                    var previous = RenderTexture.active; RenderTexture.active = texture; readback.ReadPixels(new Rect(0, 0, 512, 256), 0, 0); readback.Apply(); RenderTexture.active = previous;
                    Vector3 platePoint = view.WorldToScreenPoint(plate.TransformPoint(new Vector3(3, 0, 0)));
                    Color covered = readback.GetPixel(Mathf.RoundToInt(platePoint.x), 128), exposed = readback.GetPixel(10, 128);
                    minimumCovered = Mathf.Min(minimumCovered, covered.g); maximumCovered = Mathf.Max(maximumCovered, covered.g); minimumExposed = Mathf.Min(minimumExposed, exposed.g);
                    Assert.That(exposed.g, Is.GreaterThan(.8f), "The enabled camera must actually render the green overlay line.");
                    Assert.That(covered.g, Is.LessThan(exposed.g * .65f), "The real readout plate must attenuate the line; a bright line above the label is the regression.");
                }
                TestContext.WriteLine("16 normal-render frames: the assigned world-space cue plate covers the crossing conformal overlay line; no Camera.Render call. Covered green=" + minimumCovered + ".." + maximumCovered + "; exposed green minimum=" + minimumExposed + ".");
            }
            finally
            {
                view.targetTexture = null; UnityEngine.Object.DestroyImmediate(canvasHost); UnityEngine.Object.DestroyImmediate(lineHost); UnityEngine.Object.DestroyImmediate(cameraHost);
                UnityEngine.Object.DestroyImmediate(settings); UnityEngine.Object.DestroyImmediate(mesh); UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(readback); UnityEngine.Object.DestroyImmediate(texture);
            }
            yield return new ExitPlayMode();
        }
    }
}
