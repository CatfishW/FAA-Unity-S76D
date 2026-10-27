using System;
using System.IO;
using Leap;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Management;

namespace FAA.Headset
{
    /// <summary>Connects native Varjo sessions to the embedded Ultraleap service and reports live hands.</summary>
    [UnityEngine.Scripting.Preserve, DefaultExecutionOrder(11000)]
    public sealed class XR3HandTracking : MonoBehaviour
    {
        public bool LeftTracked { get; private set; }
        public bool RightTracked { get; private set; }
        public bool ServiceConnected { get; private set; }
        public LeapXRServiceProvider Provider => provider;
        private LeapXRServiceProvider provider;
        private GameObject ownedProvider;
        private Canvas statusCanvas;
        private TMP_Text status;
        private long timestamp;
        private float lastChange = float.NegativeInfinity, nextDiscovery, nextReport;
        private bool everLeft, everRight;
        private bool awaitingFreshFrame = true;
        private string lastStatus;
        private const int ReconnectionIntervalFrames = 180;

        [Serializable]
        private sealed class Snapshot
        {
            public string utc;
            public bool connected, left, right, everLeft, everRight;
            public int devices;
            public long timestamp;
            public float frameAgeSeconds;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || FindAnyObjectByType<XR3HandTracking>() != null) return;
            var host = new GameObject("FAA XR-3 Hand Tracking");
            DontDestroyOnLoad(host); host.AddComponent<XR3HandTracking>();
        }

        public static int CountFreshHands(Leap.Frame frame, bool connected, float ageSeconds, out bool left, out bool right)
        {
            left = right = false;
            if (!connected || frame == null || frame.Timestamp <= 0 || frame.Hands == null ||
                float.IsNaN(ageSeconds) || float.IsInfinity(ageSeconds) || ageSeconds < 0 || ageSeconds > .2f) return 0;
            foreach (Leap.Hand hand in frame.Hands)
            {
                if (hand == null || !Finite(hand.PalmPosition)) continue;
                if (hand.IsLeft) left = true; else right = true;
            }
            return (left ? 1 : 0) + (right ? 1 : 0);
        }

        private static bool Finite(Vector3 p) =>
            !float.IsNaN(p.x) && !float.IsNaN(p.y) && !float.IsNaN(p.z) &&
            !float.IsInfinity(p.x) && !float.IsInfinity(p.y) && !float.IsInfinity(p.z);

        private void Update()
        {
            if (!Application.isPlaying || Application.platform != RuntimePlatform.WindowsPlayer) return;
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager?.activeLoader == null || manager.activeLoader.GetType().FullName != "Varjo.XR.VarjoLoader") { ResetFreshness(); return; }
            Camera camera = Camera.main;
            if (camera == null) { ResetFreshness(); return; }
            if (Time.unscaledTime >= nextDiscovery)
            {
                nextDiscovery = Time.unscaledTime + 1f;
                ResolveProvider(camera);
                if (status == null) CreateStatus();
                // Existing workspace rays are UI too; the passthrough camera renders the HUD layer.
                foreach (LineRenderer ray in FindObjectsByType<LineRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (ray.name.StartsWith("FAA Layout Pointer ", StringComparison.Ordinal)) ray.gameObject.layer = XR3MixedRealityPresentation.HudLayer;
            }
            if (provider == null) { ResetFreshness(); return; }
            provider.mainCamera = camera;
            bool connected = provider.isActiveAndEnabled && provider.IsConnected();
            if (!connected) ResetFreshness();
            ServiceConnected = connected;
            Leap.Frame frame = ServiceConnected ? provider.CurrentFrame : null;
            if (frame != null) ObserveTimestamp(frame.Timestamp, Time.unscaledTime);
            float age = Time.unscaledTime - lastChange;
            CountFreshHands(frame, ServiceConnected, age, out bool left, out bool right);
            LeftTracked = left; RightTracked = right;
            everLeft |= left; everRight |= right;
            string caption = left && right ? "HANDS DETECTED: LEFT + RIGHT" : left ? "LEFT HAND DETECTED" : right ? "RIGHT HAND DETECTED" :
                ServiceConnected ? "HANDS: RAISE IN FRONT OF HEADSET" : "HAND TRACKING: CONNECTING";
            if (status != null)
            {
                status.text = caption;
                status.color = left || right ? new Color(.35f, 1f, .65f) : new Color(1f, .75f, .25f);
            }
            if (caption != lastStatus) { lastStatus = caption; Debug.Log("[FAA HANDS] " + caption); }
            if (Time.unscaledTime < nextReport) return;
            nextReport = Time.unscaledTime + 1f;
            var snapshot = new Snapshot
            {
                utc = DateTime.UtcNow.ToString("O"), connected = ServiceConnected, devices = provider.GetLeapController().Devices.Count,
                left = left, right = right, everLeft = everLeft, everRight = everRight,
                timestamp = timestamp, frameAgeSeconds = float.IsInfinity(age) ? -1f : age
            };
            try { File.WriteAllText(Path.Combine(Application.persistentDataPath, "XR3-hand-tracking.json"), JsonUtility.ToJson(snapshot, true)); }
            catch (IOException) { /* A diagnostics reader can briefly hold the file; retry next second. */ }
        }

        private void ResolveProvider(Camera camera)
        {
            if (provider != null) { ConfigureRecovery(provider); return; }
            foreach (LeapXRServiceProvider existing in FindObjectsByType<LeapXRServiceProvider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (existing.isActiveAndEnabled) { BindProvider(existing); return; }
            ownedProvider = new GameObject("FAA XR-3 Ultraleap Provider");
            ownedProvider.SetActive(false);
            ownedProvider.transform.SetParent(transform, false);
            BindProvider(ownedProvider.AddComponent<LeapXRServiceProvider>());
            provider.mainCamera = camera;
            provider.deviceOffsetMode = LeapXRServiceProvider.DeviceOffsetMode.ManualHeadOffset;
            provider.deviceOffsetYAxis = -.0112f; provider.deviceOffsetZAxis = .0999f; provider.deviceTiltXAxis = 0f;
            ownedProvider.SetActive(true);
            Debug.Log("[FAA HANDS] Native Varjo Ultraleap provider enabled with XR-3 sensor offset.");
        }

        private static void ConfigureRecovery(LeapXRServiceProvider candidate)
        {
            if (candidate == null) return;
            // The SDK otherwise stops after five failed service checks. Keep retrying
            // across a Varjo Base/service restart, with its bounded frame-based backoff.
            candidate._reconnectionAttempts = -1;
            candidate._reconnectionInterval = ReconnectionIntervalFrames;
        }

        private void BindProvider(LeapXRServiceProvider candidate)
        {
            if (provider != candidate) { provider = candidate; ResetFreshness(); }
            ConfigureRecovery(candidate);
        }

        private void ResetFreshness()
        {
            LeftTracked = RightTracked = ServiceConnected = false;
            timestamp = 0;
            lastChange = float.NegativeInfinity;
            awaitingFreshFrame = true;
        }

        private void ObserveTimestamp(long observed, float now)
        {
            if (observed <= 0) return;
            // The first frame can be the SDK's cached pre-disconnect frame.
            // Only a subsequent timestamp change establishes fresh sensor data.
            if (awaitingFreshFrame) { timestamp = observed; awaitingFreshFrame = false; return; }
            if (observed != timestamp) { timestamp = observed; lastChange = now; }
        }

        private void CreateStatus()
        {
            var host = new GameObject("FAA Hand Tracking Status", typeof(RectTransform), typeof(Canvas));
            host.transform.SetParent(transform, false);
            host.layer = XR3MixedRealityPresentation.HudLayer;
            statusCanvas = host.GetComponent<Canvas>();
            statusCanvas.renderMode = RenderMode.ScreenSpaceOverlay; statusCanvas.sortingOrder = 7500;
            var textHost = new GameObject("Hand detection", typeof(RectTransform), typeof(TextMeshProUGUI));
            textHost.transform.SetParent(host.transform, false); textHost.layer = host.layer;
            var rect = (RectTransform)textHost.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = new Vector2(0, -440); rect.sizeDelta = new Vector2(760, 34);
            status = textHost.GetComponent<TextMeshProUGUI>();
            status.font = TMP_Settings.defaultFontAsset; status.fontSize = 22;
            status.alignment = TextAlignmentOptions.Center; status.raycastTarget = false;
            Type regionType = Type.GetType("FAA.Customization.FaaHudKeepOutRegion, Assembly-CSharp");
            Type kindType = Type.GetType("FAA.Customization.FaaKeepOutKind, Assembly-CSharp");
            if (regionType != null && kindType != null)
                regionType.GetMethod("Ensure").Invoke(null,
                    new object[] { textHost, "xr3:hand-status", Enum.Parse(kindType, "Chrome"), 4f });
        }

        private void OnDisable()
        {
            ResetFreshness();
            if (ownedProvider != null) ownedProvider.SetActive(false);
            if (statusCanvas != null) statusCanvas.gameObject.SetActive(false);
        }
        private void OnEnable()
        {
            ResetFreshness();
            if (ownedProvider != null) ownedProvider.SetActive(true);
            if (statusCanvas != null) statusCanvas.gameObject.SetActive(true);
        }
        private void OnDestroy()
        {
            if (ownedProvider != null) Destroy(ownedProvider);
            if (statusCanvas != null) Destroy(statusCanvas.gameObject);
        }
    }
}
