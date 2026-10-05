using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace TrafficRadar.Core
{
    /// <summary>
    /// Main controller for the Traffic Radar system.
    /// Acts as a facade/mediator between data sources, processing, and display.
    /// 
    /// Design principles:
    /// - High Cohesion: Focuses only on coordinating radar components
    /// - Low Coupling: Communicates via interfaces and events
    /// - Dependency Injection: Components can be swapped via inspector or runtime
    /// </summary>
    [AddComponentMenu("Traffic Radar/Traffic Radar Controller")]
    public class TrafficRadarController : MonoBehaviour
    {
        #region Inspector Fields
        
        [Header("Data Source")]
        [Tooltip("TrafficRadarDataManager component providing aircraft data")]
        [SerializeField] private TrafficRadarDataManager dataManager;
        
        [Header("Display")]
        [Tooltip("TrafficRadarDisplay component for rendering")]
        [SerializeField] private TrafficRadarDisplay radarDisplay;
        
        [Header("Radar Settings")]
        [Tooltip("Radar range in nautical miles")]
        [SerializeField] private float rangeNM = 40f;
        
        [Tooltip("Available range options. One list for every control (TCAS-style 2-80 NM).")]
        [SerializeField] private float[] rangeOptionsNM = { 2f, 5f, 10f, 20f, 40f, 80f };
        
        [Tooltip("Maximum targets to display")]
        [SerializeField] private int maxTargets = 50;
        
        [Header("Threat Thresholds")]
        [SerializeField] private ThreatThresholds threatThresholds = new ThreatThresholds();
        
        [Header("Update Settings")]
        [Tooltip("How often to process and update display (Hz)")]
        [SerializeField] private float updateRate = 2f;
        
        [Header("Auto-Range Settings")]
        [Tooltip("Automatically adjust radar range to include nearby aircraft")]
        [SerializeField] private bool autoRangeEnabled = true;
        
        [Tooltip("Minimum range for auto-range in NM")]
        [SerializeField] private float autoRangeMinNM = 5f;

        [Tooltip("Auto-range never zooms out beyond this; far traffic must not collapse near traffic onto own-ship.")]
        [SerializeField] private float autoRangeMaxNM = 40f;

        [Tooltip("Auto-range frames this many nearest displayed aircraft (plus every proximate/advisory target).")]
        [SerializeField] private int autoRangeNearestCount = 4;

        [Tooltip("A smaller auto range is applied only after it has been sufficient for this long (expansion is immediate).")]
        [SerializeField] private float autoRangeShrinkDelaySeconds = 5f;

        [Header("Altitude Band")]
        [Tooltip("Relative-altitude display band. Advisories are always shown.")]
        [SerializeField] private TrafficAltitudeBand altitudeBand = TrafficAltitudeBand.Normal;
        
        [Tooltip("Also update data manager's fetch radius when position changes (may override manual radius)")]
        [SerializeField] private bool syncFetchRadiusWithRange = false;
        
        [Header("Debug")]
        [SerializeField] private bool verboseLogging = false;
        
        [Header("Events")]
        public UnityEvent<int> OnTargetCountChanged;
        public UnityEvent<ThreatLevel> OnHighestThreatChanged;
        public UnityEvent<IReadOnlyList<RadarTarget>> OnTargetsUpdated;
        
        #endregion
        
        #region Private Fields
        
        private RadarDataProcessor _processor;
        private RadarDataProcessor _indicatorProcessor;
        private List<AircraftState> _cachedAircraftStates = new List<AircraftState>();
        private OwnShipPosition _currentOwnPosition;
        private OwnShipPosition _processedOwnPosition;
        private bool _hasProcessedOwnPosition;
        private float _nextUpdateTime;
        private int _lastTargetCount;
        private ThreatLevel _lastHighestThreat = ThreatLevel.OtherTraffic;
        private IReadOnlyList<RadarTarget> _currentTargets;
        private bool _hasLiveOwnPosition;
        private bool _rangeOptionsNormalized;
        private readonly TrafficAutoRange _autoRange = new TrafficAutoRange();
        private readonly List<float> _autoDistances = new List<float>();
        private readonly List<ThreatLevel> _autoThreats = new List<ThreatLevel>();
        
        #endregion
        
        #region Properties
        
        public float RangeNM
        {
            get => rangeNM;
            set
            {
                EnsureRangeOptions();
                rangeNM = Mathf.Clamp(value, 1f, rangeOptionsNM[rangeOptionsNM.Length - 1]);
                if (_processor != null)
                    _processor.RangeNM = rangeNM;
                // Update the display's range
                if (radarDisplay != null)
                    radarDisplay.RangeNM = rangeNM;
                
                if (verboseLogging) Log($"Range set to {rangeNM} NM");
            }
        }
        
        public IReadOnlyList<RadarTarget> CurrentTargets => _currentTargets;
        /// <summary>The single range-step list shared by settings, quick menu and keyboard.</summary>
        public IReadOnlyList<float> RangeOptionsNM { get { EnsureRangeOptions(); return rangeOptionsNM; } }
        /// <summary>True once a live own-ship fix (SetOwnPosition) has arrived; auto-range waits for it.</summary>
        public bool HasLiveOwnPosition => _hasLiveOwnPosition;
        public TrafficAltitudeBand AltitudeBand
        {
            get => altitudeBand;
            set
            {
                if (altitudeBand == value) return;
                altitudeBand = value;
                if (_processor != null) _processor.AltitudeBand = altitudeBand;
                ProcessCurrentData();
            }
        }
        public int TargetCount => _currentTargets?.Count ?? 0;
        public ThreatLevel HighestThreat => _lastHighestThreat;
        public OwnShipPosition OwnPosition => _currentOwnPosition;
        public OwnShipPosition TargetReferencePosition => _hasProcessedOwnPosition ? _processedOwnPosition : _currentOwnPosition;

        /// <summary>Independent marker range; never changes the radar zoom or its target cap.</summary>
        public IReadOnlyList<RadarTarget> GetIndicatorTargets(float markerRangeNM)
        {
            if (_indicatorProcessor == null)
                _indicatorProcessor = new RadarDataProcessor(threatThresholds);
            _indicatorProcessor.RangeNM = markerRangeNM;
            _indicatorProcessor.MaxTargets = 200;
            // Match the map's ownship sample, not a newer heading/altitude
            // received between its processing tick and this screen-cue refresh.
            return _indicatorProcessor.ProcessAircraft(_cachedAircraftStates, TargetReferencePosition);
        }
        public bool AutoRangeEnabled
        {
            get => autoRangeEnabled;
            set => autoRangeEnabled = value;
        }
        public int MaxTargets
        {
            get => maxTargets;
            set
            {
                int clampedValue = Mathf.Clamp(value, 1, 200);
                if (maxTargets == clampedValue)
                {
                    return;
                }

                maxTargets = clampedValue;
                if (_processor != null)
                {
                    _processor.MaxTargets = maxTargets;
                }

                ProcessCurrentData();
            }
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            EnsureRangeOptions();
            AutoFindComponents();
            InitializeProcessor();
        }
        
        private void OnEnable()
        {
            SubscribeToEvents();
        }
        
        private void OnDisable()
        {
            UnsubscribeFromEvents();
        }
        
        private void Start()
        {
            // Initialize own position from position updater or use default
            UpdateOwnPosition();
            
            // Set initial range
            RangeNM = rangeNM;
            
            // Force initial data fetch and processing
            if (dataManager != null && dataManager.AircraftCount > 0)
            {
                ProcessCurrentData();
            }
        }
        
        private void Update()
        {
            // Periodic processing
            if (Time.time >= _nextUpdateTime)
            {
                ProcessCurrentData();
                _nextUpdateTime = Time.time + (1f / updateRate);
            }
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Cycle through available range options
        /// </summary>
        public void CycleRange()
        {
            float next = NextRangeOption(rangeNM, 1);
            RangeNM = next > rangeNM + .01f ? next : RangeOptionsNM[0];
        }
        
        /// <summary>
        /// Increase range to next higher option
        /// </summary>
        public void IncreaseRange()
        {
            RangeNM = NextRangeOption(rangeNM, 1);
        }
        
        /// <summary>
        /// Decrease range to next lower option
        /// </summary>
        public void DecreaseRange()
        {
            RangeNM = NextRangeOption(rangeNM, -1);
        }

        /// <summary>Next option strictly above (direction &gt; 0) or below the given range; stays put at the ends.
        /// Works from intermediate ranges left by a smooth zoom.</summary>
        public float NextRangeOption(float fromRangeNM, int direction)
        {
            var options = RangeOptionsNM;
            if (direction > 0)
            {
                for (int i = 0; i < options.Count; i++) if (options[i] > fromRangeNM + .01f) return options[i];
                return options[options.Count - 1];
            }
            for (int i = options.Count - 1; i >= 0; i--) if (options[i] < fromRangeNM - .01f) return options[i];
            return options[0];
        }

        public void CycleAltitudeBand() => AltitudeBand = TrafficAltitudeBands.Next(altitudeBand);
        
        /// <summary>
        /// Force immediate data refresh
        /// </summary>
        public void RefreshData()
        {
            if (dataManager != null)
            {
                dataManager.FetchDataNow();
            }
        }
        
        /// <summary>
        /// Set own-ship position manually
        /// </summary>
        public void SetOwnPosition(double lat, double lon, float altMeters, float heading)
        {
            bool firstLiveFix = !_hasLiveOwnPosition;
            _hasLiveOwnPosition = true;
            _currentOwnPosition = new OwnShipPosition
            {
                Latitude = lat,
                Longitude = lon,
                AltitudeMeters = altMeters,
                HeadingDegrees = heading
            };
            
            // Update data manager's geographic filter position (not radius unless syncFetchRadiusWithRange is enabled)
            if (dataManager != null)
            {
                if (syncFetchRadiusWithRange)
                {
                    // Auto-calculate radius based on range
                    float radiusKm = rangeNM * 1.852f * 1.5f; // 50% buffer
                    dataManager.SetGeographicFilter((float)lat, (float)lon, radiusKm);
                }
                else
                {
                    // Only update position, preserve user's manual radius setting
                    dataManager.SetReferencePosition((float)lat, (float)lon);
                }
            }
            
            // The first live fix replaces the data-manager reference point; re-frame at once.
            if (firstLiveFix) _autoRange.Reset();
            ProcessTraffic(firstLiveFix);
        }
        
        /// <summary>
        /// Find the optimal range to display all nearby aircraft
        /// </summary>
        public void AutoAdjustRange()
        {
            _autoRange.Reset();
            float selected = ComputeAutoRange();
            if (!Mathf.Approximately(selected, rangeNM)) RangeNM = selected;
        }

        /// <summary>
        /// Auto range frames the traffic the pilot can actually see (in the altitude band): every
        /// proximate/advisory target and the N nearest others, with a 15% margin, inside
        /// [min, max]. Far traffic never forces a zoom-out that collapses near traffic onto own-ship.
        /// </summary>
        private static readonly List<float> SortScratch = new List<float>();

        public static float SelectAutoRange(IReadOnlyList<float> distancesNm, IReadOnlyList<ThreatLevel> threats,
            IReadOnlyList<float> options, float minimumNm, float maximumNm, int nearestCount)
        {
            float lo = Mathf.Min(minimumNm, maximumNm), hi = Mathf.Max(minimumNm, maximumNm);
            int count = distancesNm != null ? distancesNm.Count : 0;
            float required = 0f;
            if (count == 0) required = 10f;
            else
            {
                // Nth-nearest distance (main-thread scratch buffer; no per-tick allocation).
                SortScratch.Clear();
                for (int i = 0; i < count; i++) SortScratch.Add(distancesNm[i]);
                SortScratch.Sort();
                float nth = SortScratch[Mathf.Clamp(nearestCount, 1, count) - 1];
                required = nth;
                for (int i = 0; i < count; i++)
                    if (threats != null && i < threats.Count && threats[i] >= ThreatLevel.Proximate)
                        required = Mathf.Max(required, distancesNm[i]);
                required *= 1.15f;
            }
            float chosen = hi;
            if (options != null)
                for (int i = 0; i < options.Count; i++)
                    if (options[i] >= required - .001f) { chosen = options[i]; break; }
            return Mathf.Clamp(chosen, lo, hi);
        }

        public void SetAutoRangeEnabled(bool enabled)
        {
            autoRangeEnabled = enabled;
            if (autoRangeEnabled)
            {
                AutoAdjustRange();
            }
        }

        public void ToggleAutoRange()
        {
            SetAutoRangeEnabled(!autoRangeEnabled);
        }

        public void SetMaxTargets(int value)
        {
            MaxTargets = value;
        }

        public void IncreaseMaxTargets(int step = 5)
        {
            MaxTargets += Mathf.Max(1, step);
        }

        public void DecreaseMaxTargets(int step = 5)
        {
            MaxTargets -= Mathf.Max(1, step);
        }
        
        #endregion
        
        #region Private Methods
        
        private void AutoFindComponents()
        {
            if (dataManager == null)
                dataManager = FindAnyObjectByType<TrafficRadarDataManager>();
            
            if (radarDisplay == null)
                radarDisplay = FindAnyObjectByType<TrafficRadarDisplay>();
            
            // Log what was found
            Log($"Components found - DataManager: {dataManager != null}, Display: {radarDisplay != null}");
        }
        
        private void InitializeProcessor()
        {
            EnsureRangeOptions();
            _processor = new RadarDataProcessor(threatThresholds)
            {
                RangeNM = rangeNM,
                MaxTargets = maxTargets,
                AltitudeBand = altitudeBand
            };
        }
        
        private void SubscribeToEvents()
        {
            if (dataManager != null)
            {
                dataManager.onDataUpdated.AddListener(OnDataManagerUpdated);
            }
        }
        
        private void UnsubscribeFromEvents()
        {
            if (dataManager != null)
            {
                dataManager.onDataUpdated.RemoveListener(OnDataManagerUpdated);
            }
        }
        
        private void OnDataManagerUpdated(List<TrafficRadarDataManager.AircraftData> aircraftList)
        {
            if (verboseLogging) Log($"Received {aircraftList.Count} aircraft from data manager");
            
            // Convert to AircraftState list
            _cachedAircraftStates.Clear();
            foreach (var aircraft in aircraftList)
            {
                _cachedAircraftStates.Add(new AircraftState
                {
                    Icao24 = aircraft.icao24,
                    Callsign = aircraft.callsign,
                    AircraftType = aircraft.type,
                    Latitude = aircraft.latitude,
                    Longitude = aircraft.longitude,
                    AltitudeMeters = aircraft.altitude,
                    Heading = aircraft.heading,
                    VelocityMps = aircraft.velocity,
                    VerticalRateMps = aircraft.verticalRate,
                    OnGround = aircraft.onGround,
                    LastUpdate = aircraft.lastUpdateTime
                });
            }
            
            // Update own position and process immediately
            UpdateOwnPosition();
            ProcessCurrentData();
        }
        
        private void UpdateOwnPosition()
        {
            // Use data manager's reference position
            // The OwnAircraftRadarBridge will call SetOwnPosition() to update dynamically
            if (dataManager != null && _currentOwnPosition.Latitude == 0 && _currentOwnPosition.Longitude == 0)
            {
                _currentOwnPosition = new OwnShipPosition
                {
                    Latitude = dataManager.referenceLatitude,
                    Longitude = dataManager.referenceLongitude,
                    AltitudeMeters = 313, // Default altitude (~1000 ft)
                    HeadingDegrees = 0
                };
                Log($"Using data manager reference position: {dataManager.referenceLatitude:F4}, {dataManager.referenceLongitude:F4}");
            }
        }
        
        private void ProcessCurrentData() => ProcessTraffic(false);

        private void ProcessTraffic(bool immediateAutoRange)
        {
            if (_processor == null)
            {
                InitializeProcessor();
            }
            
            // Use cached aircraft states if available, otherwise try to get from data manager
            if (_cachedAircraftStates.Count == 0 && dataManager != null && dataManager.AircraftCount > 0)
            {
                foreach (var aircraft in dataManager.AircraftList)
                {
                    _cachedAircraftStates.Add(new AircraftState
                    {
                        Icao24 = aircraft.icao24,
                        Callsign = aircraft.callsign,
                        AircraftType = aircraft.type,
                        Latitude = aircraft.latitude,
                        Longitude = aircraft.longitude,
                        AltitudeMeters = aircraft.altitude,
                        Heading = aircraft.heading,
                        VelocityMps = aircraft.velocity,
                        VerticalRateMps = aircraft.verticalRate,
                        OnGround = aircraft.onGround,
                        LastUpdate = aircraft.lastUpdateTime
                    });
                }
            }
            
            // Auto range runs only at runtime and only from a live own-ship fix; the data-manager
            // reference point can be hundreds of NM away and must never drive the zoom.
            if (autoRangeEnabled && _hasLiveOwnPosition && isActiveAndEnabled)
            {
                float proposed = ComputeAutoRange();
                float next = immediateAutoRange ? proposed
                    : _autoRange.Step(rangeNM, proposed, Time.unscaledTime, autoRangeShrinkDelaySeconds);
                if (!Mathf.Approximately(next, rangeNM)) RangeNM = next;
            }

            // Process aircraft into radar targets
            _processedOwnPosition = _currentOwnPosition;
            _hasProcessedOwnPosition = true;
            _currentTargets = _processor.ProcessAircraft(_cachedAircraftStates, _processedOwnPosition);
            
            // Log processing results (diagnostics only: never build strings or scan distances otherwise)
            if (verboseLogging && _cachedAircraftStates.Count > 0)
            {
                Log($"Processed {_cachedAircraftStates.Count} aircraft -> {_currentTargets.Count} targets in range ({rangeNM} NM)");
                
                if (_currentTargets.Count == 0 && _cachedAircraftStates.Count > 0)
                {
                    // Calculate distance to nearest aircraft for debugging
                    float nearestDist = float.MaxValue;
                    string nearestCallsign = "";
                    foreach (var ac in _cachedAircraftStates)
                    {
                        float dist = CalculateDistanceNM(_currentOwnPosition.Latitude, _currentOwnPosition.Longitude,
                                                         ac.Latitude, ac.Longitude);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearestCallsign = ac.Callsign;
                        }
                    }
                    Log($"WARNING: No targets in range! Own pos: {_currentOwnPosition.Latitude:F4}, {_currentOwnPosition.Longitude:F4}. Nearest aircraft '{nearestCallsign}' at {nearestDist:F1} NM");
                }
            }
            
            // Update display
            UpdateDisplay();
            
            // Check for count/threat changes
            CheckForChanges();
        }
        
        private void AutoAdjustRangeToIncludeAircraft()
        {
            // Retained for the debug context menu: same capped, band-aware selection as live auto range.
            AutoAdjustRange();
            ProcessCurrentData();
        }

        private float ComputeAutoRange()
        {
            EnsureRangeOptions();
            _autoDistances.Clear(); _autoThreats.Clear();
            float ownAltFt = _currentOwnPosition.AltitudeFeet;
            for (int i = 0; i < _cachedAircraftStates.Count; i++)
            {
                var ac = _cachedAircraftStates[i];
                if (ac.Latitude == 0 && ac.Longitude == 0) continue;
                float distance = CalculateDistanceNM(_currentOwnPosition.Latitude, _currentOwnPosition.Longitude, ac.Latitude, ac.Longitude);
                if (float.IsNaN(distance) || float.IsInfinity(distance)) continue;
                float relative = ac.AltitudeFeet - ownAltFt;
                ThreatLevel threat = threatThresholds.DetermineThreatLevel(distance, Mathf.Abs(relative));
                // Only traffic the pilot will actually see may drive the zoom.
                if (threat < ThreatLevel.TrafficAdvisory && !TrafficAltitudeBands.WithinBand(relative, altitudeBand)) continue;
                _autoDistances.Add(distance); _autoThreats.Add(threat);
            }
            float floor = Mathf.Clamp(autoRangeMinNM, rangeOptionsNM[0], rangeOptionsNM[rangeOptionsNM.Length - 1]);
            float ceiling = Mathf.Clamp(autoRangeMaxNM, floor, rangeOptionsNM[rangeOptionsNM.Length - 1]);
            return SelectAutoRange(_autoDistances, _autoThreats, rangeOptionsNM, floor, ceiling, autoRangeNearestCount);
        }

        /// <summary>Normalise serialized scene values to the single 2-80 NM list (drops legacy 150 NM).</summary>
        private void EnsureRangeOptions()
        {
            if (_rangeOptionsNormalized && rangeOptionsNM != null && rangeOptionsNM.Length > 0) return;
            _rangeOptionsNormalized = true;
            var list = new List<float> { 2f, 5f };
            if (rangeOptionsNM != null)
                foreach (float option in rangeOptionsNM)
                    if (option >= 1f && option <= 80f && !list.Exists(o => Mathf.Approximately(o, option))) list.Add(option);
            if (list.Count < 3) list.AddRange(new[] { 10f, 20f, 40f, 80f });
            list.Sort();
            for (int i = list.Count - 1; i > 0; i--) if (Mathf.Approximately(list[i], list[i - 1])) list.RemoveAt(i);
            rangeOptionsNM = list.ToArray();
            autoRangeMinNM = Mathf.Clamp(autoRangeMinNM, rangeOptionsNM[0], 5f);
            rangeNM = Mathf.Clamp(rangeNM, 1f, rangeOptionsNM[rangeOptionsNM.Length - 1]);
        }
        
        private float CalculateDistanceNM(double lat1, double lon1, double lat2, double lon2)
        {
            const float EarthRadiusKm = 6371f;
            const float KmToNm = 0.539957f;
            
            float dLat = (float)(lat2 - lat1) * Mathf.Deg2Rad;
            float dLon = (float)(lon2 - lon1) * Mathf.Deg2Rad;
            
            float a = Mathf.Sin(dLat / 2) * Mathf.Sin(dLat / 2) +
                      Mathf.Cos((float)lat1 * Mathf.Deg2Rad) * Mathf.Cos((float)lat2 * Mathf.Deg2Rad) *
                      Mathf.Sin(dLon / 2) * Mathf.Sin(dLon / 2);
            
            float c = 2 * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1 - a));
            
            return EarthRadiusKm * c * KmToNm;
        }
        
        private void UpdateDisplay()
        {
            if (radarDisplay == null || _currentTargets == null)
                return;
            
            // The display converts targets itself (OnControllerTargetsUpdated); no per-tick copy here.
            // Invoke the targets updated event for the display to pick up
            // The display listens to the provider, so we need to update via provider or directly
            // For now, we'll fire our own event that can be subscribed to
            OnTargetsUpdated?.Invoke(_currentTargets);
        }
        
        private void CheckForChanges()
        {
            if (_currentTargets == null)
                return;
            
            // Check target count change
            int currentCount = _currentTargets.Count;
            if (currentCount != _lastTargetCount)
            {
                _lastTargetCount = currentCount;
                OnTargetCountChanged?.Invoke(currentCount);
            }
            
            // Check highest threat change
            ThreatLevel highest = ThreatLevel.OtherTraffic;
            foreach (var target in _currentTargets)
            {
                if (target.ThreatLevel > highest)
                    highest = target.ThreatLevel;
            }
            
            if (highest != _lastHighestThreat)
            {
                _lastHighestThreat = highest;
                OnHighestThreatChanged?.Invoke(highest);
            }
        }
        
        private void Log(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[TrafficRadarController] {message}");
            }
        }
        
        #endregion
        
        #region Debug
        
#if UNITY_EDITOR
        private void OnValidate()
        {
            // Ensure range is within options
            if (rangeOptionsNM != null && rangeOptionsNM.Length > 0)
            {
                System.Array.Sort(rangeOptionsNM);
            }
            _rangeOptionsNormalized = false;
        }
        
        [ContextMenu("Debug: Log Status")]
        private void DebugLogStatus()
        {
            Debug.Log($"=== TrafficRadarController Status ===");
            Debug.Log($"Own Position: {_currentOwnPosition.Latitude:F4}, {_currentOwnPosition.Longitude:F4}");
            Debug.Log($"Range: {rangeNM} NM");
            Debug.Log($"Cached Aircraft: {_cachedAircraftStates.Count}");
            Debug.Log($"Targets in Range: {_currentTargets?.Count ?? 0}");
            Debug.Log($"Data Manager Aircraft: {dataManager?.AircraftCount ?? 0}");
            
            if (_cachedAircraftStates.Count > 0)
            {
                Debug.Log($"First aircraft: {_cachedAircraftStates[0].Callsign} at {_cachedAircraftStates[0].Latitude:F4}, {_cachedAircraftStates[0].Longitude:F4}");
            }
        }
        
        [ContextMenu("Debug: Force Auto-Range")]
        private void DebugForceAutoRange()
        {
            AutoAdjustRangeToIncludeAircraft();
        }
#endif
        
        #endregion
    }

    /// <summary>Auto-range hysteresis: expand at once, shrink only after the smaller range has
    /// been sufficient continuously for the delay. Prevents range pumping as traffic moves.</summary>
    public sealed class TrafficAutoRange
    {
        private float _lowerSince = -1f;

        public void Reset() => _lowerSince = -1f;

        public float Step(float currentNm, float proposedNm, float now, float shrinkDelaySeconds)
        {
            if (proposedNm >= currentNm - .001f) { _lowerSince = -1f; return proposedNm; }
            if (_lowerSince < 0f) { _lowerSince = now; return currentNm; }
            if (now - _lowerSince < Mathf.Max(0f, shrinkDelaySeconds)) return currentNm;
            _lowerSince = -1f; // a further shrink must hold for the full delay again
            return proposedNm;
        }
    }
}
