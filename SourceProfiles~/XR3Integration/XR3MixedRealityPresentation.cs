using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityCanvasScaler = UnityEngine.UI.CanvasScaler;
using UnityEngine.XR.Management;
using Varjo.XR;

namespace FAA.Headset
{
    /// <summary>Native XR-3 video passthrough with a resolution-independent HUD.</summary>
    [UnityEngine.Scripting.Preserve]
    [DefaultExecutionOrder(31900)]
    public sealed class XR3MixedRealityPresentation : MonoBehaviour
    {
        public const int HudLayer = 30;
        public const float DefaultHorizontalDegrees = 60f;
        public const float DefaultDistanceMeters = 1.5f;
        public static readonly Vector2 ReferenceSize = new Vector2(1920f, 1080f);

        public bool PassthroughActive { get; private set; }
        public int HeadlockedCanvasCount => canvases.Count;

        private sealed class CanvasState
        {
            public Canvas canvas;
            public Transform parent;
            public RenderMode mode;
            public Camera camera;
            public float planeDistance;
            public Vector2 anchorsMin, anchorsMax, pivot, position, size;
            public Vector3 localPosition, localScale;
            public Quaternion localRotation;
            public UnityCanvasScaler scaler;
            public bool scalerEnabled;
            public float scaleFactor;
        }

        private struct CameraState
        {
            public Camera camera;
            public CameraClearFlags clear;
            public Color background;
            public int mask;
            public bool hdr, enabled;
        }

        private readonly Dictionary<Canvas, CanvasState> canvases = new Dictionary<Canvas, CanvasState>();
        private readonly Dictionary<GameObject, int> layers = new Dictionary<GameObject, int>();
        private readonly Dictionary<Camera, CameraState> cameras = new Dictionary<Camera, CameraState>();
        private readonly Dictionary<Behaviour, bool> effects = new Dictionary<Behaviour, bool>();
        private readonly List<Canvas> releasedCanvases = new List<Canvas>();
        private static readonly Func<Canvas, bool> spatialCanvasOwner = ResolveSpatialCanvasOwner();
        private Camera view;
        private AircraftControl.Camera.AircraftCameraController renderSpaceRig;
        private static readonly Action refreshSpatialPose = ResolveSpatialPoseRefresh();

        private static Action ResolveSpatialPoseRefresh()
        {
            Type type = Type.GetType("FAA.Customization.FaaSpatialWorkspace, Assembly-CSharp");
            var current = type?.GetProperty("Current");
            var refresh = type?.GetMethod("RefreshTransforms");
            return current != null && refresh != null ? (Action)(() =>
            {
                object owner = current.GetValue(null);
                if (owner != null) refresh.Invoke(owner, null);
            }) : null;
        }

        private void ReleaseRenderSpace()
        {
            if (renderSpaceRig != null)
            {
                renderSpaceRig.SetNearRenderSpaceRequested(false);
                refreshSpatialPose?.Invoke();
            }
            renderSpaceRig = null;
        }

        private void UpdateRenderSpace()
        {
            var next = view != null && PassthroughActive && view.cullingMask == (1 << HudLayer)
                ? view.GetComponent<AircraftControl.Camera.AircraftCameraController>() : null;
            if (next != null && !next.isActiveAndEnabled) next = null;
            if (renderSpaceRig != next) { ReleaseRenderSpace(); renderSpaceRig = next; }
            if (renderSpaceRig == null) return;
            bool previous = renderSpaceRig.NearRenderSpaceActive;
            renderSpaceRig.SetNearRenderSpaceRequested(true);
            if (previous != renderSpaceRig.NearRenderSpaceActive) refreshSpatialPose?.Invoke();
        }
        private float nextDiscovery, nextAttempt, reportAt;
        private bool reportedPresentation;
        private bool capturedOpaque, originalOpaque, reportedUnavailable;

        private static Func<Canvas, bool> ResolveSpatialCanvasOwner()
        {
            // The older source profile has no spatial workspace. Keep this integration optional.
            Type type = Type.GetType("FAA.Customization.FaaSpatialWorkspace, Assembly-CSharp");
            var method = type?.GetMethod("OwnsCanvas", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return method != null ? (Func<Canvas, bool>)Delegate.CreateDelegate(typeof(Func<Canvas, bool>), method) : null;
        }

        public void ReleaseHeadlockedCanvas(Canvas canvas)
        {
            if (canvas == null || !canvases.TryGetValue(canvas, out CanvasState state)) return;
            // The spatial owner already placed the canvas. Preserve that world pose while
            // releasing the camera parent installed during early XR startup.
            if (view != null && canvas.transform.parent == view.transform)
                canvas.transform.SetParent(state.parent, true);
            canvases.Remove(canvas);
            Debug.Log("[FAA MR] World-space panel released from headlocked HUD: " + canvas.name);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || !RequestsMixedReality(Environment.GetCommandLineArgs()))
                return;
            if (FindFirstObjectByType<XR3MixedRealityPresentation>() != null) return;
            var host = new GameObject("FAA XR-3 Mixed Reality Presentation");
            DontDestroyOnLoad(host);
            host.AddComponent<XR3MixedRealityPresentation>();
        }

        public static bool RequestsMixedReality(string[] arguments)
        {
            if (arguments == null) return true;
            foreach (string argument in arguments)
                if (string.Equals(argument, "--xr3-vr", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "--xr3-sim", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-xr3-sim", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static Vector2 CanvasMeters(float distance, float horizontalDegrees)
        {
            distance = IsFinite(distance) ? Mathf.Clamp(distance, .5f, 5f) : DefaultDistanceMeters;
            horizontalDegrees = IsFinite(horizontalDegrees) ? Mathf.Clamp(horizontalDegrees, 30f, 80f) : DefaultHorizontalDegrees;
            float width = 2f * distance * Mathf.Tan(horizontalDegrees * Mathf.Deg2Rad * .5f);
            return new Vector2(width, width * ReferenceSize.y / ReferenceSize.x);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
            Application.onBeforeRender += BeforeRender;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { ReleaseRenderSpace(); view = null; nextDiscovery = 0; reportedPresentation = false; reportAt = Time.unscaledTime + 3f; }

        private void LateUpdate()
        {
            if (!Application.isPlaying || Application.platform != RuntimePlatform.WindowsPlayer) return;
            // Varjo can reconnect the same Unity session after Base/headset sleep.
            // Reassert the idempotent video/alpha settings instead of trusting a stale startup flag.
            if (Time.unscaledTime >= nextAttempt)
            {
                nextAttempt = Time.unscaledTime + 1f;
                TryStartPassthrough();
            }
            if (!PassthroughActive) { ReleaseRenderSpace(); return; }
            if (Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + .5f;
                DiscoverPresentation();
            }
            ApplyPresentation();
            if (!reportedPresentation && Time.unscaledTime >= reportAt)
            {
                reportedPresentation = true;
                Debug.Log($"[FAA MR] Presentation ready: camera={view.name}, alpha={view.backgroundColor.a}, " +
                    $"cullingMask={view.cullingMask}, opaque={VarjoRendering.GetOpaque()}, headlockedCanvases={canvases.Count}.");
                foreach (CanvasState state in canvases.Values)
                    if (state.canvas != null && state.canvas.isActiveAndEnabled)
                        Debug.Log($"[FAA MR] HUD canvas={state.canvas.name}, rect={((RectTransform)state.canvas.transform).rect.size}, " +
                            $"worldScale={state.canvas.transform.lossyScale}, mode={state.canvas.renderMode}.");
            }
        }

        private void TryStartPassthrough()
        {
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager == null || manager.activeLoader == null ||
                manager.activeLoader.GetType().FullName != "Varjo.XR.VarjoLoader" ||
                Varjo.XR.Varjo.GetVarjoSession() == IntPtr.Zero) { PassthroughActive = false; return; }
            if (!VarjoMixedReality.IsMRAvailable())
            {
                PassthroughActive = false;
                if (!reportedUnavailable) Debug.LogWarning("[FAA MR] XR-3 video passthrough is unavailable. Check headset connection and Varjo Base mixed-reality access.");
                reportedUnavailable = true;
                return;
            }
            if (!capturedOpaque) { originalOpaque = VarjoRendering.GetOpaque(); capturedOpaque = true; }
            VarjoRendering.SetOpaque(false);
            if (!VarjoMixedReality.StartRender()) { PassthroughActive = false; return; }
            view = Camera.main;
            if (view == null) { PassthroughActive = false; ReleaseRenderSpace(); return; }
            if (PassthroughActive) return;
            PassthroughActive = true;
            reportedUnavailable = false;
            reportAt = Time.unscaledTime + 3f;
            DiscoverPresentation();
            ApplyPresentation();
            Debug.Log("[FAA MR] XR-3 video passthrough started; opaque=false, transparent camera, HUD-only rendering, headlocked HUD=60x36 degrees at 1.5m.");
        }

        private void DiscoverPresentation()
        {
            if (view == null) view = Camera.main;
            if (view == null) return;
            foreach (Camera camera in FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (camera.targetTexture != null || camera.cameraType != CameraType.Game || XR3DesktopHudPreview.IsPreviewCamera(camera)) continue;
                if (!cameras.ContainsKey(camera)) cameras.Add(camera, new CameraState
                {
                    camera = camera, clear = camera.clearFlags, background = camera.backgroundColor,
                    mask = camera.cullingMask, hdr = camera.allowHDR, enabled = camera.enabled
                });
                if (camera != view) camera.enabled = false;
                foreach (Behaviour effect in camera.GetComponents<Behaviour>())
                    if (effect != null && effect.GetType().FullName == "UnityEngine.PostProcessing.PostProcessingBehaviour")
                    {
                        if (!effects.ContainsKey(effect)) effects.Add(effect, effect.enabled);
                        effect.enabled = false;
                    }
            }
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // Existing world-space cockpit panels keep their placement. All HUD
                // canvases share a private layer so terrain/sky cannot cover the room.
                foreach (Transform child in canvas.GetComponentsInChildren<Transform>(true))
                {
                    if (!layers.ContainsKey(child.gameObject)) layers.Add(child.gameObject, child.gameObject.layer);
                    child.gameObject.layer = HudLayer;
                }
                if (spatialCanvasOwner != null && spatialCanvasOwner(canvas))
                {
                    ReleaseHeadlockedCanvas(canvas);
                    continue;
                }
                if (!canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace || canvases.ContainsKey(canvas)) continue;
                var rect = (RectTransform)canvas.transform;
                var scaler = canvas.GetComponent<UnityCanvasScaler>();
                canvases.Add(canvas, new CanvasState
                {
                    canvas = canvas, parent = rect.parent, mode = canvas.renderMode, camera = canvas.worldCamera,
                    planeDistance = canvas.planeDistance, anchorsMin = rect.anchorMin, anchorsMax = rect.anchorMax,
                    pivot = rect.pivot, position = rect.anchoredPosition, size = rect.sizeDelta,
                    localPosition = rect.localPosition, localRotation = rect.localRotation, localScale = rect.localScale,
                    scaler = scaler, scalerEnabled = scaler != null && scaler.enabled, scaleFactor = canvas.scaleFactor
                });
            }
        }

        [BeforeRenderOrder(10000)]
        private void BeforeRender() { AircraftControl.Camera.RenderPhaseAudit.Mark("MR begin"); if (PassthroughActive) ApplyPresentation(); else ReleaseRenderSpace(); AircraftControl.Camera.RenderPhaseAudit.Mark("MR end"); }

        private void ApplyPresentation()
        {
            if (view == null || !PassthroughActive) { ReleaseRenderSpace(); return; }
            foreach (CameraState state in cameras.Values)
                if (state.camera != null && state.camera != view && !XR3DesktopHudPreview.IsPreviewCamera(state.camera)) state.camera.enabled = false;
            foreach (var state in effects) if (state.Key != null) state.Key.enabled = false;
            view.clearFlags = CameraClearFlags.SolidColor;
            view.backgroundColor = Color.clear;
            view.allowHDR = false;
            view.cullingMask = 1 << HudLayer;
            UpdateRenderSpace();
            releasedCanvases.Clear();
            foreach (Canvas canvas in canvases.Keys)
                if (canvas != null && spatialCanvasOwner != null && spatialCanvasOwner(canvas)) releasedCanvases.Add(canvas);
            foreach (Canvas canvas in releasedCanvases) ReleaseHeadlockedCanvas(canvas);
            foreach (CanvasState state in canvases.Values)
                if (state.canvas != null) FitHeadlockedCanvas(state.canvas, view, state.scaler);
        }

        public static void FitHeadlockedCanvas(Canvas canvas, Camera camera, UnityCanvasScaler scaler = null)
        {
            if (canvas == null || camera == null) return;
            var rect = (RectTransform)canvas.transform;
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = camera;
            canvas.scaleFactor = 1f;
            if (scaler != null) scaler.enabled = false;
            if (rect.parent != camera.transform) rect.SetParent(camera.transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = ReferenceSize;
            Vector2 meters = CanvasMeters(DefaultDistanceMeters, DefaultHorizontalDegrees);
            Vector3 parentScale = camera.transform.lossyScale;
            float pixel = meters.x / ReferenceSize.x;
            rect.localScale = new Vector3(pixel / Mathf.Max(.0001f, Mathf.Abs(parentScale.x)),
                pixel / Mathf.Max(.0001f, Mathf.Abs(parentScale.y)), pixel / Mathf.Max(.0001f, Mathf.Abs(parentScale.z)));
            // This canvas is already parented to the eye. Writing a world position and converting it back into
            // millimetre HUD coordinates loses precision at flight-scale origins and makes the native UI swim.
            // A scaled camera still keeps the original physical distance, without a world-origin round trip.
            Vector3 localPosition=parentScale==Vector3.one?new Vector3(0,0,DefaultDistanceMeters):
                camera.transform.InverseTransformVector(camera.transform.forward*DefaultDistanceMeters);
            if(!rect.localPosition.Equals(localPosition))rect.localPosition=localPosition;
            if(!rect.localRotation.Equals(Quaternion.identity))rect.localRotation=Quaternion.identity;
        }

        private void OnDisable()
        {
            ReleaseRenderSpace();
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Application.onBeforeRender -= BeforeRender;
            foreach (CanvasState state in canvases.Values)
            {
                if (state.canvas == null) continue;
                var rect = (RectTransform)state.canvas.transform;
                rect.SetParent(state.parent, false);
                state.canvas.renderMode = state.mode; state.canvas.worldCamera = state.camera;
                state.canvas.planeDistance = state.planeDistance;
                state.canvas.scaleFactor = state.scaleFactor;
                rect.anchorMin = state.anchorsMin; rect.anchorMax = state.anchorsMax; rect.pivot = state.pivot;
                rect.sizeDelta = state.size; rect.anchoredPosition = state.position;
                rect.localPosition = state.localPosition; rect.localRotation = state.localRotation; rect.localScale = state.localScale;
                if (state.scaler != null) state.scaler.enabled = state.scalerEnabled;
            }
            foreach (var state in layers) if (state.Key != null) state.Key.layer = state.Value;
            foreach (CameraState state in cameras.Values)
                if (state.camera != null && !XR3DesktopHudPreview.IsPreviewCamera(state.camera))
                {
                    state.camera.clearFlags = state.clear; state.camera.backgroundColor = state.background;
                    state.camera.cullingMask = state.mask; state.camera.allowHDR = state.hdr; state.camera.enabled = state.enabled;
                }
            foreach (var state in effects) if (state.Key != null) state.Key.enabled = state.Value;
            if (Application.platform == RuntimePlatform.WindowsPlayer && capturedOpaque && Varjo.XR.Varjo.GetVarjoSession() != IntPtr.Zero)
            {
                if (PassthroughActive) VarjoMixedReality.StopRender();
                VarjoRendering.SetOpaque(originalOpaque);
            }
            PassthroughActive = false;
            canvases.Clear(); layers.Clear(); cameras.Clear(); effects.Clear();
            capturedOpaque = false; reportedPresentation = false; reportedUnavailable = false;
            nextAttempt = nextDiscovery = 0f;
            view = null;
        }
    }
}
