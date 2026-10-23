using System;
using UnityEngine;
using UnityEngine.XR;

namespace FAA.Headset
{
    /// <summary>Centered desktop HUD preview independent of Varjo's asymmetric eye mirror.</summary>
    [UnityEngine.Scripting.Preserve, DefaultExecutionOrder(32000)]
    public sealed class XR3DesktopHudPreview : MonoBehaviour
    {
        private Camera preview;
        private RenderTexture texture;
        private XR3MixedRealityPresentation presentation;
        private bool capturedMirror, originalMirror;
        private float nextRender, nextDiscovery;

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
            desktop.nearClipPlane = .03f; desktop.farClipPlane = 2000;
            desktop.clearFlags = CameraClearFlags.SolidColor; desktop.backgroundColor = Color.black;
            desktop.cullingMask = 1 << XR3MixedRealityPresentation.HudLayer;
            desktop.allowHDR = false; desktop.allowMSAA = false;
            desktop.transform.SetPositionAndRotation(headset.transform.position, headset.transform.rotation);
            desktop.ResetProjectionMatrix(); desktop.ResetWorldToCameraMatrix();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || Application.platform != RuntimePlatform.WindowsPlayer) return;
            if (presentation == null && Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + 1f;
                presentation = FindAnyObjectByType<XR3MixedRealityPresentation>();
            }
            if (presentation == null || !presentation.PassthroughActive || Camera.main == null) return;
            if (!capturedMirror)
            {
                originalMirror = XRSettings.showDeviceView; capturedMirror = true;
                XRSettings.showDeviceView = false;
                Debug.Log("[FAA MR] Centered desktop HUD preview active; headset stereo projections unchanged.");
            }
            if (Time.unscaledTime < nextRender) return;
            nextRender = Time.unscaledTime + 1f / 30f;
            float scale = Mathf.Min(1f, 1920f / Mathf.Max(1, Screen.width), 1080f / Mathf.Max(1, Screen.height));
            int width = Mathf.Max(1, Mathf.RoundToInt(Screen.width * scale));
            int height = Mathf.Max(1, Mathf.RoundToInt(Screen.height * scale));
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (preview != null) preview.targetTexture = null;
                if (texture != null) { texture.Release(); Destroy(texture); }
                texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                    { name = "FAA Centered HUD Preview", vrUsage = VRTextureUsage.None };
                texture.Create();
            }
            if (preview == null)
            {
                var host = new GameObject("FAA Desktop Preview Camera"); host.transform.SetParent(transform, false);
                preview = host.AddComponent<Camera>(); preview.enabled = false;
            }
            preview.targetTexture = texture;
            ConfigureCamera(preview, Camera.main, new Vector2(width, height));
            preview.Render();
        }

        private void OnGUI()
        {
            if (texture == null || !capturedMirror || Event.current.type != EventType.Repaint) return;
            GUI.depth = -1000;
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), texture, ScaleMode.StretchToFill, false);
        }

        private void OnDisable()
        {
            if (capturedMirror) XRSettings.showDeviceView = originalMirror;
            capturedMirror = false;
            if (preview != null) { preview.targetTexture = null; Destroy(preview.gameObject); }
            if (texture != null) { texture.Release(); Destroy(texture); }
        }
    }
}
