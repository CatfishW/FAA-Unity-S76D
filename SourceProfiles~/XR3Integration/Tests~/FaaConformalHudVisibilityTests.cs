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
    public sealed class FaaConformalHudVisibilityTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        private static void Set(object o, string name, object value) => o.GetType().GetField(name, Any).SetValue(o, value);
        private static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Any).Invoke(o, args);
        [SetUp]
        public void RequiresFullConformalProfile()
        { if (Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp", false) == null) Assert.Ignore("Full XR integration conformal layer is optional in the standard profile."); }
        [Serializable] private sealed class NativeRow { public int scenario; public float nativeAlpha; public bool visible; }
        [Serializable] private sealed class NativeReport { public NativeRow[] rows; }
        [Serializable] private sealed class Report
        {
            public string engine, graphics;
            public int bindGroups, beforeRedPixels, hiddenRedPixels, shownRedPixels, floorWarningRedPixels;
            public bool sameFrameHide, sameFrameShow;
            public float minimumPilotOpacity;
            public bool hideAccepted, showAccepted, hiddenRendererEnabled;
            public float hideCanvasAlpha;
        }
        private static bool Execute(Component registry, string command) => (bool)registry.GetType().GetMethod("ExecuteCommand", Any, null,
            new[] { typeof(string), typeof(string), typeof(Dictionary<string, object>) }, null).Invoke(registry,
                new object[] { "symbology", command, new Dictionary<string, object>() });
        private static int RedPixels(RenderTexture texture, Texture2D readback)
        {
            var previous = RenderTexture.active; RenderTexture.active = texture;
            readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); readback.Apply(); RenderTexture.active = previous;
            int red = 0;
            foreach (Color32 p in readback.GetPixels32()) if (p.r > 30 && p.r > p.g * 2 && p.r > p.b * 2) red++;
            return red;
        }

        [UnityTest, Explicit("Actual registry hide/show through ordinary enabled-camera frames; run this exact method.")]
        public IEnumerator RegistryHideMustSuppressLateCreatedCanvasGroupWorldAttitudeFlagAndShowRestoresIt()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires actual graphics device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var cameraHost = new GameObject("Visibility command camera", typeof(Camera)); cameraHost.tag = "MainCamera";
            var view = cameraHost.GetComponent<Camera>();
            var canvasHost = new GameObject("FAASymbologyCanvas", typeof(RectTransform), typeof(Canvas));
            var primary = new GameObject("Second Interation GUI", typeof(RectTransform)); primary.transform.SetParent(canvasHost.transform, false);
            var aircraft = new GameObject("Visibility test aircraft");
            var layerHost = new GameObject("World conformal visibility owner");
            var managerHost = new GameObject("Inactive color owner"); managerHost.SetActive(false);
            var adapterHost = new GameObject("Inactive symbology command target"); adapterHost.SetActive(false);
            var registryHost = new GameObject("Inactive registry fixture"); registryHost.SetActive(false);
            var independent = new GameObject("XPlaneTrafficRadarCanvas", typeof(RectTransform), typeof(Canvas));
            var chrome = new GameObject("FAA Pilot Chrome Canvas", typeof(RectTransform), typeof(Canvas));
            Component manager = managerHost.AddComponent(T("FAA.Customization.SymbologyColorManager"));
            Component adapter = adapterHost.AddComponent(T("VoiceControl.Adapters.SymbologyColorVoiceAdapter")); Set(adapter, "colorManager", manager);
            Component registry = registryHost.AddComponent(T("VoiceControl.Core.VoiceCommandRegistry")); Call(registry, "RegisterTarget", adapter);
            Component layer = layerHost.AddComponent(T("FAA.Customization.FaaRotorcraftConformalLayer"));
            var texture = new RenderTexture(1024, 576, 24); var readback = new Texture2D(1024, 576, TextureFormat.RGB24, false);
            string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../visibility-gpu")); Directory.CreateDirectory(output);
            var report = new Report { engine = Application.unityVersion, graphics = SystemInfo.graphicsDeviceName };
            try
            {
                foreach (var script in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    if (script != null && script.GetType().FullName == "FAA.Headset.XR3HeadsetCompatibility") script.enabled = false;
                view.clearFlags = CameraClearFlags.SolidColor; view.backgroundColor = Color.black; view.cullingMask = 1 << 30;
                view.aspect = 16f / 9f; view.fieldOfView = 36; view.nearClipPlane = .03f; view.farClipPlane = 10000;
                view.allowHDR = false; view.allowMSAA = false; texture.Create(); view.targetTexture = texture;
                T("FAA.Headset.XR3MixedRealityPresentation").GetMethod("FitHeadlockedCanvas", Any).Invoke(null,
                    new object[] { canvasHost.GetComponent<Canvas>(), view, null });
                Call(layer, "Bind", view, aircraft.transform, canvasHost.GetComponent<Canvas>());
                report.bindGroups = primary.GetComponentsInParent<CanvasGroup>(true).Length;
                Assert.That(report.bindGroups, Is.Zero, "Reproduce the real command adding its group after layer binding.");
                yield return null; yield return null;
                report.beforeRedPixels = RedPixels(texture, readback); Assert.That(report.beforeRedPixels, Is.GreaterThan(10));
                File.WriteAllBytes(Path.Combine(output, "hud-on-invalid-attitude.png"), readback.EncodeToPNG());
                Call(layer, "RefreshPresentation");
                int commandFrame = Time.frameCount;
                Assert.That(Execute(registry, "hide"), Is.True); Call(layer, "RefreshBeforeRender");
                report.sameFrameHide = !((Renderer)layer.GetType().GetField("meshRenderer", Any).GetValue(layer)).enabled;
                Assert.That(report.sameFrameHide, Is.True, "Visibility changes must bypass the unchanged pose/frame cache.");
                Assert.That(Execute(registry, "show"), Is.True); Call(layer, "RefreshBeforeRender");
                report.sameFrameShow = ((Renderer)layer.GetType().GetField("meshRenderer", Any).GetValue(layer)).enabled;
                Assert.That(report.sameFrameShow, Is.True); Assert.That(Time.frameCount, Is.EqualTo(commandFrame));
                report.hideAccepted = Execute(registry, "hide"); Assert.That(report.hideAccepted, Is.True);
                report.hideCanvasAlpha = canvasHost.GetComponent<CanvasGroup>().alpha; Assert.That(report.hideCanvasAlpha, Is.Zero);
                // The command can run after LateUpdate with the exact same pose/frame. Exercise final admission too.
                Call(layer, "RefreshBeforeRender");
                yield return null; yield return null;
                report.hiddenRedPixels = RedPixels(texture, readback);
                report.hiddenRendererEnabled = ((Renderer)layer.GetType().GetField("meshRenderer", Any).GetValue(layer)).enabled;
                File.WriteAllBytes(Path.Combine(output, "hud-off-must-be-empty.png"), readback.EncodeToPNG());
                Assert.That(independent.GetComponent<CanvasGroup>(), Is.Null, "Radar remains outside the flight HUD visibility owner.");
                Assert.That(chrome.GetComponent<CanvasGroup>(), Is.Null, "Chrome remains usable while flight symbology is hidden.");
                report.showAccepted = Execute(registry, "show"); Assert.That(report.showAccepted, Is.True);
                Call(layer, "RefreshBeforeRender");
                yield return null; yield return null;
                report.shownRedPixels = RedPixels(texture, readback); Assert.That(report.shownRedPixels, Is.GreaterThan(10));
                File.WriteAllBytes(Path.Combine(output, "hud-on-warning-restored.png"), readback.EncodeToPNG());
                var opacityHost = new GameObject("Inactive minimum brightness fixture"); opacityHost.SetActive(false);
                try
                {
                    Component opacity = opacityHost.AddComponent(T("FAA.Customization.FaaHudOpacityController"));
                    Set(opacity, "persistPreference", false); Set(opacity, "hudRoots", new List<Transform> { primary.transform });
                    Call(opacity, "RefreshTargets"); Call(opacity, "SetOpacityPercent", 0); // Existing safe floor, not hide.
                    report.minimumPilotOpacity = (float)opacity.GetType().GetProperty("Opacity").GetValue(opacity);
                    Assert.That(report.minimumPilotOpacity, Is.EqualTo(.35f).Within(.0001f));
                    Call(layer, "RefreshBeforeRender"); yield return null; yield return null;
                    report.floorWarningRedPixels = RedPixels(texture, readback);
                    Assert.That(report.floorWarningRedPixels, Is.GreaterThan(10), "The safe brightness floor must not hide an invalid-attitude warning while HUD is ON.");
                }
                finally { Object.DestroyImmediate(opacityHost); }
                File.WriteAllText(Path.Combine(output, "registry-hide-show-normal-render.json"), JsonUtility.ToJson(report, true));
                Assert.That(report.hiddenRedPixels, Is.Zero, "Actual symbology.hide must hide the separate ATT mesh/text, including a CanvasGroup created after Bind.");
                Assert.That(report.hiddenRendererEnabled, Is.False);
            }
            finally
            {
                view.targetTexture = null; Object.DestroyImmediate(layerHost); Object.DestroyImmediate(canvasHost); Object.DestroyImmediate(aircraft);
                Object.DestroyImmediate(adapterHost); Object.DestroyImmediate(managerHost); Object.DestroyImmediate(registryHost);
                Object.DestroyImmediate(independent); Object.DestroyImmediate(chrome); Object.DestroyImmediate(cameraHost); Object.DestroyImmediate(texture); Object.DestroyImmediate(readback);
            }
            yield return new ExitPlayMode();
        }

        [UnityTest, Explicit("Compare live hierarchy visibility with native CanvasRenderer inherited alpha during ordinary frames.")]
        public IEnumerator LiveHierarchyMatchesNativeGroupsWithDisabledAndIgnoreParentRules()
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) Assert.Ignore("Requires actual graphics device");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); yield return new EnterPlayMode();
            var parent = new GameObject("Native hierarchy parent");
            var root = new GameObject("Native visibility source", typeof(RectTransform), typeof(Canvas)); root.transform.SetParent(parent.transform, false);
            var primary = new GameObject("Second Interation GUI", typeof(RectTransform)); primary.transform.SetParent(root.transform, false);
            var markerHost = new GameObject("Native reference graphic", typeof(RectTransform), typeof(Image)); markerHost.transform.SetParent(primary.transform, false);
            var viewHost = new GameObject("Native alpha test camera", typeof(Camera)); var view = viewHost.GetComponent<Camera>();
            var owner = new GameObject("Disabled geometry owner"); owner.SetActive(false); Component layer = owner.AddComponent(T("FAA.Customization.FaaRotorcraftConformalLayer"));
            var texture = new RenderTexture(64, 64, 0); var rows = new List<NativeRow>();
            try
            {
                var canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = view;
                root.transform.position = new Vector3(0, 0, 2); root.transform.localScale = Vector3.one * .01f;
                var marker = markerHost.GetComponent<Image>(); marker.color = Color.white;
                texture.Create(); view.targetTexture = texture;
                Call(layer, "Bind", view, parent.transform, canvas);
                // All groups are created after Bind; no membership or parent alpha is cached.
                CanvasGroup outer = parent.AddComponent<CanvasGroup>(), source = root.AddComponent<CanvasGroup>(), inner = primary.AddComponent<CanvasGroup>();
                for (int n = 0; n < 9; n++)
                {
                    outer.enabled = source.enabled = inner.enabled = true; outer.alpha = source.alpha = inner.alpha = 1;
                    outer.ignoreParentGroups = source.ignoreParentGroups = inner.ignoreParentGroups = false;
                    switch (n)
                    {
                        case 1: source.alpha = 0; break;
                        case 2: source.alpha = 0; source.enabled = false; break;
                        case 3: outer.alpha = 0; break;
                        case 4: outer.alpha = 0; inner.ignoreParentGroups = true; break;
                        case 5: outer.alpha = 0; inner.ignoreParentGroups = true; inner.alpha = 0; break;
                        case 6: outer.alpha = 0; inner.ignoreParentGroups = true; inner.enabled = false; break;
                        case 7: inner.alpha = .02f; source.alpha = .02f; break;
                        case 8: source.alpha = .4f; inner.interactable = inner.blocksRaycasts = false; break;
                    }
                    Canvas.ForceUpdateCanvases(); yield return null; yield return null;
                    float inherited = marker.canvasRenderer.GetInheritedAlpha();
                    bool visible = (bool)Call(layer, "IsSourceHudVisible");
                    rows.Add(new NativeRow { scenario = n, nativeAlpha = inherited, visible = visible });
                    Assert.That(visible, Is.EqualTo(inherited > .001f), "Match native UI alpha semantics in scenario " + n);
                }
                string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../visibility-gpu")); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "native-canvas-group-semantics.json"), JsonUtility.ToJson(new NativeReport { rows = rows.ToArray() }, true));
            }
            finally
            {
                view.targetTexture = null; Object.DestroyImmediate(owner); Object.DestroyImmediate(parent); Object.DestroyImmediate(viewHost); Object.DestroyImmediate(texture);
            }
            yield return new ExitPlayMode();
        }
    }
}
