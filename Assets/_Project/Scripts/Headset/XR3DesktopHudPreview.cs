using System;
using System.Collections.Generic;
using UnityEngine.UI;
using UnityEngine;
using UnityEngine.XR;

namespace FAA.Headset
{
    /// <summary>Centered desktop HUD preview independent of Varjo's asymmetric eye mirror.</summary>
    [UnityEngine.Scripting.Preserve, DefaultExecutionOrder(32000)]
    public sealed class XR3DesktopHudPreview : MonoBehaviour
    {
        private static XR3DesktopHudPreview active;
        private Camera preview;
        private readonly List<XR3DesktopCanvasRaycaster> pointerRaycasters = new List<XR3DesktopCanvasRaycaster>();
        private float nextInputDiscovery;
        private XR3MixedRealityPresentation presentation;
        private bool capturedMirror, originalMirror;
        private float nextDiscovery;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer) return;
            foreach (string arg in Environment.GetCommandLineArgs())
                if (arg.Equals("--xr3-eye-mirror", StringComparison.OrdinalIgnoreCase)) return;
            if (FindAnyObjectByType<XR3DesktopHudPreview>() != null) return;
            var host = new GameObject("FAA Centered Desktop HUD Preview");
            DontDestroyOnLoad(host); host.AddComponent<XR3DesktopHudPreview>();
        }

        public static Camera PointerCameraForCanvas(Canvas canvas)
        {
            return active != null && active.capturedMirror && canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? SynchronizePointerCamera(active.preview) : canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        }

        /// <summary>Read-only diagnostic access; does not refresh or reconfigure a camera.</summary>
        public static Camera OwnedDesktopCameraForAudit => active != null && active.isActiveAndEnabled && active.capturedMirror &&
            active.preview != null && active.preview.enabled ? active.preview : null;

        /// <summary>Input may run after the head moves but before this owner's LateUpdate.</summary>
        public static Camera SynchronizePointerCamera(Camera candidate)
        {
            if (candidate == null || active == null || !active.isActiveAndEnabled || !active.capturedMirror ||
                active.preview != candidate || !candidate.enabled) return candidate;
            Camera headset = Camera.main;
            if (headset != null && headset != candidate)
                ConfigureSpectatorCamera(candidate, headset, new Vector2(Screen.width, Screen.height));
            return candidate;
        }

        public static bool IsPreviewCamera(Camera camera)
        {
            if (camera == null) return false;
            var owner = camera.GetComponentInParent<XR3DesktopHudPreview>();
            return owner != null && owner.preview == camera;
        }

        public static void ConfigureCamera(Camera desktop, Camera headset, Vector2 displaySize)
        {
            if (desktop == null || headset == null || desktop == headset) return;
            desktop.stereoTargetEye = StereoTargetEyeMask.None;
            desktop.orthographic = false; desktop.usePhysicalProperties = false;
            desktop.rect = new Rect(0, 0, 1, 1);
            desktop.aspect = Mathf.Max(1, displaySize.x) / Mathf.Max(1, displaySize.y);
            Vector2 size = XR3MixedRealityPresentation.CanvasMeters(
                XR3MixedRealityPresentation.DefaultDistanceMeters, XR3MixedRealityPresentation.DefaultHorizontalDegrees);
            float halfHeight = Mathf.Max(size.y * .5f, size.x / (2 * desktop.aspect)) * 1.08f;
            desktop.fieldOfView = 2 * Mathf.Atan(halfHeight / XR3MixedRealityPresentation.DefaultDistanceMeters) * Mathf.Rad2Deg;
            desktop.nearClipPlane = .03f; desktop.farClipPlane = Mathf.Max(2000f, headset.farClipPlane);
            desktop.clearFlags = CameraClearFlags.SolidColor; desktop.backgroundColor = Color.black;
            desktop.cullingMask = 1 << XR3MixedRealityPresentation.HudLayer;
            desktop.allowHDR = false; desktop.allowMSAA = false;
            desktop.transform.SetPositionAndRotation(headset.transform.position, headset.transform.rotation);
            var space = AircraftControl.Camera.AircraftViewRenderSpace.ForCamera(headset);
            AircraftControl.Camera.AircraftViewRenderSpace.Bind(desktop, space != null ? space.Owner : null);
            desktop.ResetProjectionMatrix(); desktop.ResetWorldToCameraMatrix();
        }

        /// <summary>Use Unity's normal mono camera pass for the desktop display, never an XR eye/GUI overlay.</summary>
        public static void ConfigureSpectatorCamera(Camera desktop, Camera headset, Vector2 displaySize)
        {
            if (desktop == null || headset == null || desktop == headset) return;
            desktop.targetTexture = null;
            desktop.targetDisplay = 0;
            ConfigureCamera(desktop, headset, displaySize);
            desktop.depth = headset.depth + 1f;
            desktop.enabled = true;
        }

        private void OnEnable() => Application.onBeforeRender += RefreshSpectatorPose;

        [BeforeRenderOrder(32000)]
        private void RefreshSpectatorPose()
        {
            AircraftControl.Camera.RenderPhaseAudit.Mark("Spectator begin");
            if (active == this && capturedMirror && preview != null && preview.enabled &&
                presentation != null && presentation.PassthroughActive && Camera.main != null)
                ConfigureSpectatorCamera(preview, Camera.main, new Vector2(Screen.width, Screen.height));
            AircraftControl.Camera.RenderPhaseAudit.Mark("Spectator end");
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || Application.platform != RuntimePlatform.WindowsPlayer) return;
            if (presentation == null && Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + 1f;
                presentation = FindAnyObjectByType<XR3MixedRealityPresentation>();
            }
            if (presentation == null || !presentation.PassthroughActive || Camera.main == null) { SuspendPreview(); return; }
            if (!capturedMirror)
            {
                originalMirror = XRSettings.showDeviceView; capturedMirror = true;
                XRSettings.showDeviceView = false;
                Debug.Log("[FAA MR] Centered mono desktop spectator active; headset stereo projections unchanged.");
            }
            XRSettings.showDeviceView = false;
            if (preview == null)
            {
                var host = new GameObject("FAA Desktop Preview Camera"); host.transform.SetParent(transform, false);
                preview = host.AddComponent<Camera>(); preview.enabled = false;
            }
            ConfigureSpectatorCamera(preview, Camera.main, new Vector2(Screen.width, Screen.height));
            active = this;
            if (Time.unscaledTime >= nextInputDiscovery)
            {
                nextInputDiscovery = Time.unscaledTime + .25f;
                DiscoverPointerRaycasters();
            }
        }

        private void DiscoverPointerRaycasters()
        {
            foreach (GraphicRaycaster source in FindObjectsByType<GraphicRaycaster>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (source is XR3DesktopCanvasRaycaster || !source.enabled) continue;
                Canvas canvas = source.GetComponent<Canvas>();
                if (canvas == null || canvas.renderMode == RenderMode.ScreenSpaceOverlay) continue;
                var adapter = source.GetComponent<XR3DesktopCanvasRaycaster>();
                if (adapter == null) adapter = source.gameObject.AddComponent<XR3DesktopCanvasRaycaster>();
                adapter.enabled = true; adapter.Bind(source, preview);
                if (!pointerRaycasters.Contains(adapter)) pointerRaycasters.Add(adapter);
            }
        }

        private void SuspendPreview()
        {
            if (active == this) active = null;
            if (capturedMirror) XRSettings.showDeviceView = originalMirror;
            capturedMirror = false;
            foreach (var adapter in pointerRaycasters)
                if (adapter != null) { adapter.Restore(); DestroyOwned(adapter); }
            pointerRaycasters.Clear();
            if (preview != null)
            {
                AircraftControl.Camera.AircraftViewRenderSpace.Bind(preview, null);
                preview.enabled = false;
            }
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= RefreshSpectatorPose;
            SuspendPreview();
            if (preview != null) DestroyOwned(preview.gameObject);
            preview = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value);
            else DestroyImmediate(value);
        }
    }
}
