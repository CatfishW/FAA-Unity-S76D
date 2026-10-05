using System.Collections.Generic;
using UnityEngine;
using IndicatorSystem.Core;
using IndicatorSystem.Display;

namespace IndicatorSystem.Controller
{
    /// <summary>
    /// Main coordinator for the indicator system.
    /// Manages indicator lifecycle and coordinates between data sources and display.
    /// 
    /// Design Principles:
    /// - High Cohesion: Focuses on coordinating indicator display
    /// - Low Coupling: Receives targets via interface, no direct radar dependencies
    /// </summary>
    [AddComponentMenu("Indicator System/Indicator System Controller")]
    [DefaultExecutionOrder(12000)]
    public class IndicatorSystemController : MonoBehaviour
    {
        #region Inspector Fields
        
        [Header("Settings")]
        [Tooltip("Indicator settings asset. Created automatically if null.")]
        [SerializeField] private IndicatorSettings settings;
        
        [Header("Canvas")]
        [Tooltip("Target canvas for indicators. If null, creates its own overlay canvas.")]
        [SerializeField] private Canvas targetCanvas;
        
        [Header("Camera")]
        [Tooltip("Camera for screen-space calculations. Uses main camera if null.")]
        [SerializeField] private Camera targetCamera;
        
        [Header("Own Position")]
        [Tooltip("Reference latitude for position calculations")]
        [SerializeField] private double referenceLatitude = 33.6407;
        [Tooltip("Reference longitude for position calculations")]
        [SerializeField] private double referenceLongitude = -84.4277;
        [Tooltip("Reference altitude in meters")]
        [SerializeField] private float referenceAltitude = 313f;
        
        [Header("Debug")]
        [SerializeField] private bool verboseLogging = false;
        
        #endregion
        
        #region Private Fields
        
        private IndicatorPool _pool;
        private readonly Dictionary<string, IIndicatorTarget> _targets = new Dictionary<string, IIndicatorTarget>();
        private readonly List<IndicatorData> _indicatorDataList = new List<IndicatorData>();
        private readonly HashSet<string> _activeIds = new HashSet<string>();
        private readonly HashSet<string> _previousVisibleIds = new HashSet<string>();
        private IndicatorEdgeConfig _edgeConfig;
        private bool _isInitialized;
        private readonly List<Rect> _occupiedCueBounds = new List<Rect>();
        private IndicatorControlsPanel _controlsPanel;
        private CanvasGroup _cueGroup;
        private int _trafficVisible, _weatherVisible, _onScreenVisible, _offScreenVisible;
        
        #endregion
        
        #region Properties
        
        /// <summary>Current number of active indicators</summary>
        public int ActiveIndicatorCount => _pool?.ActiveCount ?? 0;
        
        /// <summary>Settings asset in use</summary>
        public IndicatorSettings Settings => settings;
        
        /// <summary>Whether the system is initialized and ready</summary>
        public bool IsInitialized => _isInitialized;
        
        /// <summary>Target canvas for indicators</summary>
        public Canvas TargetCanvas => targetCanvas;
        public int TrafficVisibleCount => _trafficVisible;
        public int WeatherVisibleCount => _weatherVisible;
        public int OnScreenCount => _onScreenVisible;
        public int OffScreenCount => _offScreenVisible;
        public int SuppressedCount => Mathf.Max(0, _indicatorDataList.Count - ActiveIndicatorCount);

        public bool IsTypeVisible(IndicatorType type) => settings != null && settings.enabled &&
            isActiveAndEnabled && settings.globalOpacity > 0f && ShouldShowType(type);

        public void SetTypeVisible(IndicatorType type, bool visible)
        {
            if (settings == null) return;
            if (type == IndicatorType.Traffic) settings.showTrafficIndicators = visible;
            if (type == IndicatorType.Weather) settings.showWeatherIndicators = visible;
            if (visible)
            {
                settings.enabled = true;
                if (settings.globalOpacity <= 0f) settings.globalOpacity = 1f;
            }
            RefreshSettings();
            if (_isInitialized && settings.enabled && settings.globalOpacity > 0f) UpdateIndicators();
            else _pool?.ReleaseAll();
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            Initialize();
        }
        
        private void OnEnable()
        {
            Application.onBeforeRender += RefreshProjectionBeforeRender;
            if (!_isInitialized)
                Initialize();
        }

        private void Start()
        {
            if (!_isInitialized)
                Initialize();
        }
        
        private void LateUpdate()
        {
            if (!_isInitialized)
            {
                Initialize();
            }

            if (!_isInitialized || settings == null || !settings.enabled || settings.globalOpacity <= 0f)
            {
                _pool?.ReleaseAll();
                _trafficVisible = _weatherVisible = _onScreenVisible = _offScreenVisible = 0;
                _indicatorDataList.Clear();
                return;
            }
            
            UpdateIndicators();
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= RefreshProjectionBeforeRender;
            _pool?.ReleaseAll();
            _trafficVisible = _weatherVisible = _onScreenVisible = _offScreenVisible = 0;
        }

        [BeforeRenderOrder(100)]
        private void RefreshProjectionBeforeRender()
        {
            if (_isInitialized && settings != null && settings.enabled && settings.globalOpacity > 0f)
                UpdateIndicators();
        }

        private void OnDestroy()
        {
            if (_pool != null) DestroyOwnedObject(_pool.gameObject);
            if (_controlsPanel != null) DestroyOwnedObject(_controlsPanel.gameObject);
        }

        private static void DestroyOwnedObject(GameObject owned)
        {
            if (Application.isPlaying) Destroy(owned);
            else DestroyImmediate(owned);
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Initialize the indicator system.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized)
                return;
            
            // Ensure settings exist
            if (settings == null)
            {
                settings = IndicatorSettings.CreateDefault();
                Log("Created default settings");
            }
            
            // Setup camera
            if (targetCamera == null)
            {
                targetCamera = FindBestCamera();
            }
            
            if (targetCamera == null)
            {
                Debug.LogError("[IndicatorSystem] No camera found! Assign a camera or ensure a MainCamera exists.");
                return;
            }
            
            // Create indicator pool
            if (targetCanvas != null)
            {
                // Use the assigned canvas
                _pool = IndicatorPool.CreateOnCanvas(targetCanvas, settings);
                Log($"Created pool on assigned canvas: {targetCanvas.name}");
            }
            else
            {
                // Create our own canvas
                _pool = IndicatorPool.CreateWithCanvas(transform, settings);
                Log("Created pool with new overlay canvas");
            }
            
            // Cache edge config
            _edgeConfig = settings.GetEdgeConfig();
            
            _isInitialized = true;
            if (settings.usePilotCueStyle && _controlsPanel == null && _pool != null)
                _controlsPanel = IndicatorControlsPanel.Create(this, _pool.Container.parent);
            Log("Indicator system initialized");
        }

        private static Camera FindBestCamera()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera != null)
            {
                return mainCamera;
            }

            Camera[] cameras = FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            Camera fallback = null;
            foreach (Camera camera in cameras)
            {
                if (camera == null)
                {
                    continue;
                }

                if (camera.isActiveAndEnabled)
                {
                    return camera;
                }

                fallback ??= camera;
            }

            return fallback;
        }
        
        /// <summary>
        /// Set the target canvas at runtime.
        /// Note: This destroys and recreates the pool.
        /// </summary>
        public void SetTargetCanvas(Canvas canvas)
        {
            if (canvas == targetCanvas)
                return;
            
            targetCanvas = canvas;
            
            // Reinitialize pool if already running
            if (_isInitialized)
            {
                if (_controlsPanel != null) DestroyOwnedObject(_controlsPanel.gameObject);
                // Destroy old pool
                if (_pool != null)
                {
                    Destroy(_pool.gameObject);
                }
                
                // Create new pool on the canvas
                if (targetCanvas != null)
                {
                    _pool = IndicatorPool.CreateOnCanvas(targetCanvas, settings);
                }
                else
                {
                    _pool = IndicatorPool.CreateWithCanvas(transform, settings);
                }
                if (settings.usePilotCueStyle)
                    _controlsPanel = IndicatorControlsPanel.Create(this, _pool.Container.parent);
                
                Log($"Recreated pool on canvas: {(targetCanvas != null ? targetCanvas.name : "new overlay")}");
            }
        }
        
        /// <summary>
        /// Update the reference position for calculations.
        /// </summary>
        public void SetReferencePosition(double lat, double lon, float altMeters)
        {
            referenceLatitude = lat;
            referenceLongitude = lon;
            referenceAltitude = altMeters;
        }
        
        /// <summary>
        /// Add or update a target for indication.
        /// </summary>
        /// <param name="target">Target to display indicator for</param>
        public void AddOrUpdateTarget(IIndicatorTarget target)
        {
            if (target == null || string.IsNullOrEmpty(target.Id))
                return;
            
            _targets[target.Id] = target;
        }
        
        /// <summary>
        /// Add or update multiple targets.
        /// </summary>
        public void AddOrUpdateTargets(IEnumerable<IIndicatorTarget> targets)
        {
            foreach (var target in targets)
            {
                AddOrUpdateTarget(target);
            }
        }
        
        /// <summary>
        /// Replace all targets with a new set.
        /// </summary>
        public void SetTargets(IEnumerable<IIndicatorTarget> targets)
        {
            _targets.Clear();
            AddOrUpdateTargets(targets);
        }

        /// <summary>
        /// Replace only targets of one source type, preserving other active sources.
        /// </summary>
        public void SetTargetsForType(IndicatorType type, IEnumerable<IIndicatorTarget> targets)
        {
            var incomingTargets = new List<IIndicatorTarget>();
            var incomingIds = new HashSet<string>();
            if (targets != null)
            {
                foreach (var target in targets)
                {
                    if (target == null || string.IsNullOrEmpty(target.Id))
                    {
                        continue;
                    }

                    incomingTargets.Add(target);
                    incomingIds.Add(target.Id);
                }
            }

            var toRemove = new List<string>();
            foreach (var pair in _targets)
            {
                if (pair.Value != null && pair.Value.Type == type && !incomingIds.Contains(pair.Key))
                {
                    toRemove.Add(pair.Key);
                }
            }

            foreach (string id in toRemove)
            {
                RemoveTarget(id);
            }

            AddOrUpdateTargets(incomingTargets);
        }
        
        /// <summary>
        /// Remove a target by ID.
        /// </summary>
        public void RemoveTarget(string id)
        {
            if (_targets.Remove(id))
            {
                _pool?.ReleaseIndicator(id);
            }
        }
        
        /// <summary>
        /// Clear all targets and indicators.
        /// </summary>
        public void ClearAll()
        {
            _targets.Clear();
            _pool?.ReleaseAll();
        }
        
        /// <summary>
        /// Force refresh of edge configuration from settings.
        /// </summary>
        public void RefreshSettings()
        {
            if (settings != null)
            {
                _edgeConfig = settings.GetEdgeConfig();
            }
        }
        
        /// <summary>
        /// Set the global opacity for all indicators via settings.
        /// </summary>
        public void SetGlobalOpacity(float alpha)
        {
            if (settings != null)
            {
                settings.globalOpacity = Mathf.Clamp01(alpha);
                Log($"Global opacity set to {alpha:F2}");
            }
        }
        
        /// <summary>
        /// Set the opacity for nearby indicators via settings.
        /// </summary>
        public void SetNearbyOpacity(float alpha, float distanceThresholdNM = -1f)
        {
            if (settings != null)
            {
                settings.nearbyOpacity = Mathf.Clamp01(alpha);
                settings.useProximityOpacity = true;
                
                if (distanceThresholdNM > 0f)
                {
                    settings.nearbyDistanceThresholdNM = distanceThresholdNM;
                }
                
                Log($"Nearby opacity set to {alpha:F2} within {settings.nearbyDistanceThresholdNM:F1} NM");
            }
        }
        
        /// <summary>
        /// Disable proximity-based opacity mode.
        /// </summary>
        public void DisableProximityOpacity()
        {
            if (settings != null)
            {
                settings.useProximityOpacity = false;
                Log("Proximity opacity disabled");
            }
        }
        
        /// <summary>
        /// Toggle proximity-based opacity mode.
        /// </summary>
        public void ToggleProximityOpacity()
        {
            if (settings != null)
            {
                settings.useProximityOpacity = !settings.useProximityOpacity;
                Log($"Proximity opacity: {(settings.useProximityOpacity ? "enabled" : "disabled")}");
            }
        }
        
        /// <summary>
        /// Show all indicators at full opacity.
        /// </summary>
        public void ShowAllIndicators()
        {
            SetGlobalOpacity(1f);
            DisableProximityOpacity();
            
            // Also ensure type visibility is enabled
            if (settings != null)
            {
                settings.enabled = true;
                settings.showTrafficIndicators = true;
                settings.showWeatherIndicators = true;
                settings.showWaypointIndicators = true;
            }
            
            Log("All indicators shown");
        }
        
        /// <summary>
        /// Hide all indicators by setting opacity to 0.
        /// </summary>
        public void HideAllIndicators()
        {
            SetGlobalOpacity(0f);
            Log("All indicators hidden");
        }
        
        #endregion
        
        #region Private Methods
        
        private System.Comparison<IndicatorData> _priorityComparison;
        private int CompareIndicators(IndicatorData a, IndicatorData b)
        {
            int priorityCompare = b.Priority.CompareTo(a.Priority);
            if (priorityCompare != 0)
            {
                return priorityCompare;
            }

            // Within the same urgency, keep a visible cue ahead of a competing
            // overlapping label. Tiny range changes must not alternate winners.
            int retained = _previousVisibleIds.Contains(b.Id).CompareTo(_previousVisibleIds.Contains(a.Id));
            if (settings.usePilotCueStyle && retained != 0) return retained;
            int distance = a.DistanceNM.CompareTo(b.DistanceNM);
            return distance != 0 ? distance : string.CompareOrdinal(a.Id, b.Id);
        }

        private int _chromeRectStart, _chromeRectEnd;
        private void LiftAboveChrome(ref IndicatorData data, ref Rect bounds)
        {
            float half = Screen.height * .5f;
            for (int i = _chromeRectStart; i < _chromeRectEnd && i < _occupiedCueBounds.Count; i++)
            {
                Rect chrome = _occupiedCueBounds[i];
                if (chrome.center.y > half || !chrome.Overlaps(bounds)) continue; // only the bottom-edge chrome (bar, flyouts)
                float dy = chrome.yMax + 2f - bounds.yMin;
                if (dy <= 0f) continue;
                bounds.y += dy; data.ScreenPosition.y += dy;
            }
        }

        private bool OverlapsOccupied(Rect bounds)
        {
            for (int i = 0; i < _occupiedCueBounds.Count; i++) if (_occupiedCueBounds[i].Overlaps(bounds)) return true;
            return false;
        }

        /// <summary>Head-fixed cue arrows dim with the rest of the forward HUD while a side panel is inspected (FaaHudInspection).</summary>
        private void ApplyInspectionDimming()
        {
            if (_pool == null || _pool.Container == null) return;
            if (_cueGroup == null || _cueGroup.gameObject != _pool.Container.gameObject)
            {
                _cueGroup = _pool.Container.GetComponent<CanvasGroup>();
                if (_cueGroup == null) _cueGroup = _pool.Container.gameObject.AddComponent<CanvasGroup>();
                _cueGroup.interactable = false; _cueGroup.blocksRaycasts = false;
            }
            float alpha = FAA.Customization.FaaHudInspection.ForwardHudIntensity;
            if (!Mathf.Approximately(_cueGroup.alpha, alpha)) _cueGroup.alpha = alpha;
        }

        private void UpdateIndicators()
        {
            if (_pool == null || targetCamera == null)
                return;
            
            _indicatorDataList.Clear();
            _previousVisibleIds.Clear();
            _previousVisibleIds.UnionWith(_activeIds);
            _activeIds.Clear();
            _occupiedCueBounds.Clear();
            if (settings.usePilotCueStyle && _controlsPanel != null && _controlsPanel.isActiveAndEnabled && !_controlsPanel.IsDocked)
                _occupiedCueBounds.Add(_controlsPanel.OccupiedScreenBounds());
            // Edge arrows never sit on the chrome bar, an open flyout (brief, cue controls, key list) or the status chip.
            _chromeRectStart = _occupiedCueBounds.Count;
            if (settings.usePilotCueStyle) FAA.Customization.FaaPilotChrome.CollectOccupiedScreenRects(_occupiedCueBounds);
            _chromeRectEnd = _occupiedCueBounds.Count;
            ApplyInspectionDimming();
            _trafficVisible = _weatherVisible = _onScreenVisible = _offScreenVisible = 0;
            _edgeConfig = settings.GetEdgeConfig();
            // Unusual attitude (AC 25-11B 5.10.3.2, rule 2.7): secondary cues declutter with the rest of the display.
            // Only traffic advisories (TA/RA priority) stay, because they may matter for the recovery itself.
            bool declutter = FAA.Customization.FaaRotorcraftConformalLayer.UnusualAttitudeActive;
            float canvasScale = targetCanvas != null ? targetCanvas.scaleFactor : 1f;
            if (settings.usePilotCueStyle)
                _edgeConfig.EdgePadding = Mathf.Max(72f, settings.edgePadding) * canvasScale;
            
            // Calculate indicator data for each target
            foreach (var kvp in _targets)
            {
                var target = kvp.Value;
                
                // Skip by type if disabled
                if (!ShouldShowType(target.Type))
                    continue;

                if (declutter && !KeepDuringUnusualAttitude(target.Type, target.Priority))
                    continue;
                
                // Skip by distance
                if (target.DistanceNM < settings.minDisplayDistance)
                    continue;
                
                // Calculate screen-space data
                var data = ScreenIndicatorCalculator.CalculateIndicator(target, targetCamera, _edgeConfig);
                
                if (data.IsActive)
                {
                    _indicatorDataList.Add(data);
                }
            }
            
            // Keep the display sparse by taking the most important and nearest targets first.
            // Cached delegate: the comparison runs every frame (twice with before-render), so never allocate a closure here.
            _indicatorDataList.Sort(_priorityComparison ??= CompareIndicators);
            
            // Limit to max indicators
            for (int i = 0; i < _indicatorDataList.Count && _activeIds.Count < settings.maxIndicators; i++)
            {
                var data = _indicatorDataList[i];
                if (settings.usePilotCueStyle)
                {
                    Rect bounds = PilotIndicatorCue.ScreenBounds(data, canvasScale * settings.globalScale);
                    // An edge-clamped (off-screen) cue only shows a direction, so lift it above the chrome instead of
                    // dropping it; an on-screen cue marks a real position and is never moved.
                    if (data.Visibility != IndicatorVisibility.OnScreen) LiftAboveChrome(ref data, ref bounds);
                    if (OverlapsOccupied(bounds)) continue;
                    _occupiedCueBounds.Add(bounds);
                }
                
                // Pass the data to pool so it can select correct prefab
                var element = _pool.GetIndicator(data.Id, data);
                
                if (element != null)
                {
                    element.UpdateIndicator(data, settings);
                    _activeIds.Add(data.Id);
                    if (data.Type == IndicatorType.Traffic) _trafficVisible++;
                    if (data.Type == IndicatorType.Weather) _weatherVisible++;
                    if (data.Visibility == IndicatorVisibility.OnScreen) _onScreenVisible++;
                    else _offScreenVisible++;
                }
            }
            
            // Release indicators for targets no longer present
            _pool.ReleaseExcept(_activeIds);
        }
        
        /// <summary>Cues kept while an unusual attitude is annunciated: traffic advisories only (TA priority 2, RA priority 3).</summary>
        public static bool KeepDuringUnusualAttitude(IndicatorType type, int priority) =>
            type == IndicatorType.Traffic && priority >= 2;

        private bool ShouldShowType(IndicatorType type)
        {
            switch (type)
            {
                case IndicatorType.Traffic:
                    return settings.showTrafficIndicators;
                case IndicatorType.Weather:
                    return settings.showWeatherIndicators;
                case IndicatorType.Waypoint:
                    return settings.showWaypointIndicators;
                default:
                    return true;
            }
        }
        
        private void Log(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[IndicatorSystem] {message}");
            }
        }
        
        #endregion
        
        #region Context Menu
        
#if UNITY_EDITOR
        [ContextMenu("Debug: Log Status")]
        private void DebugLogStatus()
        {
            Debug.Log($"=== Indicator System Status ===");
            Debug.Log($"Initialized: {_isInitialized}");
            Debug.Log($"Targets: {_targets.Count}");
            Debug.Log($"Active Indicators: {_pool?.ActiveCount ?? 0}");
            Debug.Log($"Pool Available: {_pool?.AvailableCount ?? 0}");
            Debug.Log($"Reference Position: {referenceLatitude:F4}, {referenceLongitude:F4}");
            Debug.Log($"Target Canvas: {(targetCanvas != null ? targetCanvas.name : "Auto-created")}");
            Debug.Log($"Using Custom Prefabs: {(settings != null ? settings.useCustomPrefabs.ToString() : "N/A")}");
        }
        
        [ContextMenu("Debug: Clear All")]
        private void DebugClearAll()
        {
            ClearAll();
        }
        
        [ContextMenu("Debug: Reinitialize")]
        private void DebugReinitialize()
        {
            if (_pool != null)
            {
                Destroy(_pool.gameObject);
            }
            _isInitialized = false;
            _targets.Clear();
            Initialize();
        }
#endif
        
        #endregion
    }
}
