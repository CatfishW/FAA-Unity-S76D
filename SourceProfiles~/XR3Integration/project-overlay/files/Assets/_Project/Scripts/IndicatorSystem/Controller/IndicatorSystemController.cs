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
    [DefaultExecutionOrder(31950)]
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
        private sealed class CuePresentation
        {
            public bool HasSide, Left, HasEdgePosition, Displaced, Suppressed;
            public float EdgeY, Scale;
            public Rect Region;
        }
        private readonly Dictionary<string,CuePresentation> _presentation = new Dictionary<string,CuePresentation>();
        private readonly List<string> _presentationRemoved = new List<string>();
        private string _projectionPhase="refresh";
        
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
            if (_isInitialized && settings.enabled && settings.globalOpacity > 0f){_projectionPhase="settings";UpdateIndicators();}
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
            
            _projectionPhase="late";UpdateIndicators();
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= RefreshProjectionBeforeRender;
            _pool?.ReleaseAll();
            _trafficVisible = _weatherVisible = _onScreenVisible = _offScreenVisible = 0;
            _presentation.Clear();
        }

        [BeforeRenderOrder(11000)]
        private void RefreshProjectionBeforeRender()
        {
            AircraftControl.Camera.RenderPhaseAudit.Mark("Indicators begin");
            if (_isInitialized && settings != null && settings.enabled && settings.globalOpacity > 0f)
            {
                _projectionPhase="before-render";UpdateIndicators();
            }
            AircraftControl.Camera.RenderPhaseAudit.Mark("Indicators end");
        }

        private void OnDestroy()
        {
            IndicatorLayoutAudit.Finish();
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
            _presentationRemoved.Clear();
            foreach(var pair in _presentation)if(!_targets.ContainsKey(pair.Key))_presentationRemoved.Add(pair.Key);
            foreach(string id in _presentationRemoved)_presentation.Remove(id);
            _presentationRemoved.Clear();
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
            _presentation.Remove(id);
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
            _presentation.Clear();
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

        private void LiftAboveChrome(ref IndicatorData data, ref Rect bounds)
        {
            float width = PilotIndicatorCue.ScreenBoundsForSettings(data, 1f, settings).width;
            float scale = width > 0 ? bounds.width / width : 1f;
            if (TryPlaceOffscreenCue(ref data, scale, settings, _occupiedCueBounds, out Rect placed)) bounds = placed;
        }

        private static readonly System.Func<List<Rect>, bool, int> KeepOutCollector = ResolveKeepOutCollector();
        private static System.Func<List<Rect>, bool, int> ResolveKeepOutCollector()
        {
            // The standard source profile has no full HUD-region registry.
            var type = System.Type.GetType("FAA.Customization.FaaHudKeepOut, Assembly-CSharp");
            var method = type?.GetMethod("Collect", new[] { typeof(List<Rect>), typeof(bool) });
            return method != null ? (System.Func<List<Rect>, bool, int>)System.Delegate.CreateDelegate(typeof(System.Func<List<Rect>, bool, int>), method) : null;
        }

        /// <summary>Move only an edge cue's vertical anchor to the nearest clear slot inside its physical HUD.</summary>
        public static bool TryPlaceOffscreenCue(ref IndicatorData data, float pixelScale, IndicatorSettings cueSettings,
            IReadOnlyList<Rect> occupied, out Rect bounds)
            => TryPlaceOffscreenCueNearest(ref data,pixelScale,cueSettings,occupied,data.ScreenPosition.y,0f,out bounds);

        private static bool TryPlaceOffscreenCueNearest(ref IndicatorData data,float pixelScale,IndicatorSettings cueSettings,
            IReadOnlyList<Rect> occupied,float preferredY,float clearance,out Rect bounds)
        {
            IndicatorData original = data;
            bounds = PilotIndicatorCue.ScreenBoundsForSettings(original, pixelScale, cueSettings);
            if (!TrafficGlyphPolicy.Finite(original.ScreenPosition.x) || !TrafficGlyphPolicy.Finite(original.ScreenPosition.y)) return false;
            if (original.Visibility == IndicatorVisibility.OnScreen)
            {
                if (occupied != null) for (int i = 0; i < occupied.Count; i++) if (occupied[i].Overlaps(bounds)) return false;
                return true;
            }
            Rect region = original.ProjectionRect;
            if (!TrafficGlyphPolicy.Finite(region.xMin) || !TrafficGlyphPolicy.Finite(region.yMin) ||
                !TrafficGlyphPolicy.Finite(region.xMax) || !TrafficGlyphPolicy.Finite(region.yMax) || region.width <= 0 || region.height <= 0) return false;
            var centered = original; centered.ScreenPosition.y = region.center.y;
            Rect centerBounds = PilotIndicatorCue.ScreenBoundsForSettings(centered, pixelScale, cueSettings);
            float halfHeight = centerBounds.height * .5f;
            float low = region.yMin + halfHeight, high = region.yMax - halfHeight;
            if (!TrafficGlyphPolicy.Finite(halfHeight) || low > high) return false;
            float bestDistance = float.PositiveInfinity;
            IndicatorData best = original; Rect bestBounds = bounds;
            void Consider(float y)
            {
                if (!TrafficGlyphPolicy.Finite(y) || y < low || y > high) return;
                var candidate = original; candidate.ScreenPosition.y = y;
                Rect trial = PilotIndicatorCue.ScreenBoundsForSettings(candidate, pixelScale, cueSettings);
                if (trial.xMin < region.xMin - .01f || trial.xMax > region.xMax + .01f ||
                    trial.yMin < region.yMin - .01f || trial.yMax > region.yMax + .01f) return;
                if (!ClearWithMargin(trial,occupied,clearance)) return;
                float distance = Mathf.Abs(y - preferredY);
                if (distance > bestDistance || Mathf.Approximately(distance, bestDistance) && y >= best.ScreenPosition.y) return;
                bestDistance = distance; best = candidate; bestBounds = trial;
            }
            Consider(Mathf.Clamp(original.ScreenPosition.y, low, high));
            if (occupied != null)
                for (int i = 0; i < occupied.Count; i++)
                {
                    Rect obstacle = occupied[i];
                    if (obstacle.xMax <= centerBounds.xMin-clearance || obstacle.xMin >= centerBounds.xMax+clearance) continue;
                    float gap=Mathf.Max(2f,clearance+2f);
                    Consider(obstacle.yMin - halfHeight - gap);
                    Consider(obstacle.yMax + halfHeight + gap);
                }
            if (float.IsPositiveInfinity(bestDistance)) return false;
            data = best; bounds = bestBounds; return true;
        }

        private static bool ClearWithMargin(Rect bounds,IReadOnlyList<Rect> occupied,float margin)
        {
            bounds=Rect.MinMaxRect(bounds.xMin-margin,bounds.yMin-margin,bounds.xMax+margin,bounds.yMax+margin);
            if(occupied!=null)for(int i=0;i<occupied.Count;i++)if(bounds.Overlaps(occupied[i]))return false;
            return true;
        }

        // Keep decorative readouts and displaced edge lanes stable across tiny camera/telemetry changes.
        // The real on-screen target anchor, arrow direction and geometric visibility are never filtered.
        private bool TryLayoutPilotCue(ref IndicatorData data,float pixelScale,out Rect bounds)
        {
            string id=data.Id??string.Empty;
            if(!_presentation.TryGetValue(id,out var state))_presentation[id]=state=new CuePresentation();
            float scale=TrafficGlyphPolicy.Finite(pixelScale)&&pixelScale>.0001f?pixelScale:1f;
            Rect region=data.ProjectionRect;
            float centre=region.width>0?region.center.x:Screen.width*.5f;
            float sideMargin=16f*scale;
            if(!state.HasSide){state.Left=data.ScreenPosition.x>centre;state.HasSide=true;}
            else if(state.Left&&data.ScreenPosition.x<centre-sideMargin)state.Left=false;
            else if(!state.Left&&data.ScreenPosition.x>centre+sideMargin)state.Left=true;
            data.HasReadoutSide=true;data.ReadoutOnLeft=state.Left;
            bounds=PilotIndicatorCue.ScreenBoundsForSettings(data,scale,settings);
            // A label always points inward at the physical viewport edges; the centre deadband cannot clip it.
            if(bounds.xMin<region.xMin||bounds.xMax>region.xMax)
            {
                var alternate=data;alternate.ReadoutOnLeft=!data.ReadoutOnLeft;
                Rect other=PilotIndicatorCue.ScreenBoundsForSettings(alternate,scale,settings);
                if(other.xMin>=region.xMin&&other.xMax<=region.xMax)
                {data=alternate;bounds=other;state.Left=data.ReadoutOnLeft;}
            }
            bool offscreen=data.Visibility!=IndicatorVisibility.OnScreen;
            if(!offscreen){state.HasEdgePosition=false;state.Displaced=false;}
            else
            {
                float originalY=data.ScreenPosition.y;
                bool sameSpace=Mathf.Abs(state.Region.width-region.width)<1f&&Mathf.Abs(state.Region.height-region.height)<1f&&
                    Vector2.Distance(state.Region.position,region.position)<4f&&Mathf.Abs(state.Scale-scale)<.01f;
                float preferred=state.HasEdgePosition&&state.Displaced&&sameSpace&&!ClearWithMargin(bounds,_occupiedCueBounds,4f*scale)
                    ?state.EdgeY:originalY;
                if(!TryPlaceOffscreenCueNearest(ref data,scale,settings,_occupiedCueBounds,preferred,state.Suppressed?4f*scale:0f,out bounds))
                {state.Suppressed=true;return false;}
                state.EdgeY=data.ScreenPosition.y;state.HasEdgePosition=true;
                state.Displaced=Mathf.Abs(state.EdgeY-originalY)>.5f;state.Region=region;state.Scale=scale;
            }
            if(OverlapsOccupied(bounds)){state.Suppressed=true;return false;}
            // Hide immediately when unsafe; reacquire only after useful clearance, rather than flashing at a pixel boundary.
            if(state.Suppressed&&!ClearWithMargin(bounds,_occupiedCueBounds,4f*scale))return false;
            state.Suppressed=false;return true;
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

        private Canvas ProjectionCanvas => targetCanvas != null ? targetCanvas :
            _pool != null && _pool.Container != null ? _pool.Container.GetComponentInParent<Canvas>() : null;

        /// <summary>The physical HUD window in camera pixels; XR eye projection remains owned by the runtime.</summary>
        public static Rect GetProjectionRect(Canvas canvas, Camera view)
        {
            if (view == null) return default;
            Rect cameraRect = view.pixelRect;
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return cameraRect;
            var rect = canvas.transform as RectTransform;
            if (rect == null) return cameraRect;
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < 4; i++)
            {
                Vector3 local=new Vector3((i&1)==0?rect.rect.xMin:rect.rect.xMax,(i&2)==0?rect.rect.yMin:rect.rect.yMax,0);
                if(!IndicatorCanvasProjection.TryProjectLocal(rect,local,view,out Vector3 point))return cameraRect;
                if (point.z <= 0 || !TrafficGlyphPolicy.Finite(point.x) || !TrafficGlyphPolicy.Finite(point.y) || !TrafficGlyphPolicy.Finite(point.z)) return cameraRect;
                min = Vector2.Min(min, point); max = Vector2.Max(max, point);
            }
            min = Vector2.Max(min, cameraRect.min); max = Vector2.Min(max, cameraRect.max);
            return max.x - min.x > 1f && max.y - min.y > 1f ? Rect.MinMaxRect(min.x, min.y, max.x, max.y) : cameraRect;
        }

        public static float PixelsPerReferenceUnit(Canvas canvas, Camera view)
        {
            if (canvas == null) return 1f;
            if (canvas.renderMode != RenderMode.WorldSpace || view == null) return Mathf.Max(.001f, canvas.scaleFactor);
            return IndicatorCanvasProjection.TryPixelsPerReferenceUnit(canvas.rootCanvas.transform,view,out float pixels)
                ? pixels:Mathf.Max(.001f,canvas.scaleFactor);
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
            // Protect visible flight instruments and chrome; the central attitude window remains available to real targets.
            if (settings.usePilotCueStyle) KeepOutCollector?.Invoke(_occupiedCueBounds, true);
            ApplyInspectionDimming();
            _trafficVisible = _weatherVisible = _onScreenVisible = _offScreenVisible = 0;
            _edgeConfig = settings.GetEdgeConfig();
            // Unusual attitude (AC 25-11B 5.10.3.2, rule 2.7): secondary cues declutter with the rest of the display.
            // Only traffic advisories (TA/RA priority) stay, because they may matter for the recovery itself.
            bool declutter = FAA.Customization.FaaRotorcraftConformalLayer.UnusualAttitudeActive;
            Canvas projectionCanvas = ProjectionCanvas;
            Rect projectionRect = GetProjectionRect(projectionCanvas, targetCamera);
            float canvasScale = PixelsPerReferenceUnit(projectionCanvas, targetCamera);
            if (settings.usePilotCueStyle)
                _edgeConfig.EdgePadding = Mathf.Max(Mathf.Max(72f, 60f * settings.globalScale), settings.edgePadding) * canvasScale;
            
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
                var data = ScreenIndicatorCalculator.CalculateIndicatorInRect(target, targetCamera, _edgeConfig, projectionRect);
                
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
                var raw=data;
                if (settings.usePilotCueStyle)
                {
                    if(!TryLayoutPilotCue(ref data,canvasScale*settings.globalScale,out Rect bounds))
                    {IndicatorLayoutAudit.Write(raw,data,canvasScale,_projectionPhase,"suppressed",_occupiedCueBounds.Count);continue;}
                    _occupiedCueBounds.Add(bounds);
                }
                
                // Pass the data to pool so it can select correct prefab
                var element = _pool.GetIndicator(data.Id, data);
                
                if (element != null)
                {
                    element.UpdateIndicator(data, settings);
                    IndicatorLayoutAudit.Write(raw,data,canvasScale,_projectionPhase,"shown",_occupiedCueBounds.Count);
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
