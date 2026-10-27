using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using FAA.XPlaneIntegration.Runtime;

namespace FAA.Headset
{
    /// <summary>Read-only local evidence for remote headset verification; contains no control endpoint.</summary>
    [UnityEngine.Scripting.Preserve, DefaultExecutionOrder(32000)]
    public sealed class XR3RuntimeReport : MonoBehaviour
    {
        [Serializable] private sealed class Snapshot
        {
            public string utc, loader, graphicsDevice;
            public int processId, frameCount;
            public float uptimeSeconds, applicationFps, packetAgeSeconds = -1;
            public bool applicationFocused, xrDeviceActive, xrDisplayRunning, passthroughRequestAccepted;
            public bool headsetConnected, tracked, positionAvailable, rotationAvailable, userPresenceAvailable, userPresent;
            public bool flightFeedHealthy, handServiceConnected, leftHand, rightHand, nativeOriginActive, nativeSeatCalibrated;
            public bool refreshRateAvailable, gpuTimeAvailable, droppedFramesAvailable, framePresentsAvailable;
            public float displayRefreshHz, appGpuSeconds;
            public int droppedFrames, framePresents;
            public Vector3 worldEyePosition, localEyePosition, worldEyeEuler;
            public float cameraAlpha, bodyReferenceErrorDegrees = -1;
            public int cameraCullingMask, desktopWidth, desktopHeight;
        }
        private readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>();
        private float nextReport, intervalStart;
        private int intervalFrames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || FindAnyObjectByType<XR3RuntimeReport>() != null) return;
            var host = new GameObject("FAA XR-3 Runtime Report"); DontDestroyOnLoad(host); host.AddComponent<XR3RuntimeReport>();
        }

        private void LateUpdate()
        {
            intervalFrames++;
            if (Time.unscaledTime < nextReport) return;
            float elapsed = Time.unscaledTime - intervalStart;
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            Camera view = Camera.main;
            var report = new Snapshot
            {
                utc = DateTime.UtcNow.ToString("O"), processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                loader = manager?.activeLoader != null ? manager.activeLoader.GetType().FullName : "none",
                graphicsDevice = SystemInfo.graphicsDeviceName, uptimeSeconds = Time.realtimeSinceStartup,
                applicationFps = elapsed > 0 ? intervalFrames / elapsed : 0, frameCount = Time.frameCount,
                applicationFocused = Application.isFocused, xrDeviceActive = XRSettings.isDeviceActive,
                desktopWidth = Screen.width, desktopHeight = Screen.height
            };
            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            head.TryGetFeatureValue(CommonUsages.isTracked, out report.tracked);
            report.positionAvailable = head.TryGetFeatureValue(CommonUsages.devicePosition, out _);
            report.rotationAvailable = head.TryGetFeatureValue(CommonUsages.deviceRotation, out _);
            report.userPresenceAvailable = head.TryGetFeatureValue(CommonUsages.userPresence, out report.userPresent);
            if (report.loader == "Varjo.XR.VarjoLoader") report.headsetConnected = Varjo.XR.Varjo.IsHMDConnected();
            displays.Clear(); SubsystemManager.GetSubsystems(displays);
            foreach (var display in displays)
            {
                if (!display.running) continue;
                report.xrDisplayRunning = true;
                report.refreshRateAvailable = display.TryGetDisplayRefreshRate(out report.displayRefreshHz);
                report.gpuTimeAvailable = display.TryGetAppGPUTimeLastFrame(out report.appGpuSeconds);
                report.droppedFramesAvailable = display.TryGetDroppedFrameCount(out report.droppedFrames);
                report.framePresentsAvailable = display.TryGetFramePresentCount(out report.framePresents);
                break;
            }
            var mr = FindAnyObjectByType<XR3MixedRealityPresentation>();
            report.passthroughRequestAccepted = mr != null && mr.PassthroughActive;
            var hands = FindAnyObjectByType<XR3HandTracking>();
            report.handServiceConnected = hands != null && hands.ServiceConnected;
            report.leftHand = hands != null && hands.LeftTracked; report.rightHand = hands != null && hands.RightTracked;
            var bridge = FindAnyObjectByType<XPlane12ApiHudBridge>();
            report.flightFeedHealthy = bridge != null && bridge.IsFeedHealthy;
            if (bridge != null && !float.IsInfinity(bridge.LastPacketAgeSeconds) && !float.IsNaN(bridge.LastPacketAgeSeconds))
                report.packetAgeSeconds = bridge.LastPacketAgeSeconds;
            if (view != null)
            {
                report.worldEyePosition = view.transform.position; report.localEyePosition = view.transform.localPosition;
                report.worldEyeEuler = view.transform.eulerAngles; report.cameraAlpha = view.backgroundColor.a;
                report.cameraCullingMask = view.cullingMask;
                var controller = view.GetComponent<AircraftControl.Camera.AircraftCameraController>();
                var origin = controller != null ? controller.GetType().GetProperty("NativeXrTrackingOrigin")?.GetValue(controller) as Transform : null;
                report.nativeOriginActive = origin != null;
                var calibrated = controller != null ? controller.GetType().GetProperty("NativeXrSeatCalibrated")?.GetValue(controller) : null;
                report.nativeSeatCalibrated = calibrated is bool ready && ready;
                var property = controller != null && report.nativeOriginActive ? controller.GetType().GetProperty("NativeXrOriginBodyErrorDegrees") : null;
                if (property != null) report.bodyReferenceErrorDegrees = (float)property.GetValue(controller);
            }
            WriteSnapshot(Path.Combine(Application.persistentDataPath, "XR3-runtime-report.json"), JsonUtility.ToJson(report, true));
            intervalFrames = 0; intervalStart = Time.unscaledTime; nextReport = Time.unscaledTime + 1f;
        }

        public static void WriteSnapshot(string path, string json)
        {
            string temporary = path + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (IOException) { /* Readers may briefly hold the report; retry next second. */ }
            catch (UnauthorizedAccessException) { /* Diagnostics cannot interrupt the flight display. */ }
        }
    }
}
