using UnityEngine;
using AircraftControl.Core;
using UnityEngine.EventSystems;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
#endif

namespace AircraftControl.Camera
{
    /// <summary>
    /// Enhanced aircraft camera controller with smooth mouse movement and multiple view modes.
    /// Features:
    /// - Right-click to look around freely
    /// - Smooth return to forward view when releasing mouse
    /// - Multiple camera modes (Cockpit, Chase, Free)
    /// - Configurable sensitivity and smoothing
    /// 
    /// Setup:
    /// 1. Add this component to your camera
    /// 2. Assign the aircraft transform
    /// 3. Configure camera mode and settings
    /// </summary>
    [AddComponentMenu("Aircraft Control/Aircraft Camera Controller")]
    [DefaultExecutionOrder(11100)]
    public class AircraftCameraController : MonoBehaviour
    {
        #region Camera Modes
        
        public enum CameraMode
        {
            Cockpit,    // Fixed position in cockpit, rotates with look input
            Chase,      // Follows behind aircraft
            Free        // Free orbit around aircraft
        }
        
        #endregion
        
        #region Inspector Settings
        
        [Header("Target")]
        [Tooltip("The aircraft transform to follow")]
        [SerializeField] private Transform aircraftTransform;
        
        [Header("Camera Mode")]
        [SerializeField] private CameraMode cameraMode = CameraMode.Cockpit;
        
        [Header("Mouse Settings")]
        [Tooltip("Mouse sensitivity for looking around")]
        [Range(0.5f, 10f)]
        [SerializeField] private float mouseSensitivity = 3f;
        
        [Tooltip("Smoothing factor for mouse input (lower = smoother)")]
        [Range(0.01f, 1f)]
        [SerializeField] private float mouseSmoothing = 0.15f;
        
        [Tooltip("Speed to return to aircraft heading when not looking")]
        [Range(0.5f, 10f)]
        [SerializeField] private float returnSpeed = 2.5f;
        
        [Header("Look Limits")]
        [Tooltip("Maximum pitch angle (degrees up)")]
        [Range(30f, 89f)]
        [SerializeField] private float maxPitch = 80f;
        
        [Tooltip("Minimum pitch angle (degrees down)")]
        [Range(-89f, -30f)]
        [SerializeField] private float minPitch = -70f;
        
        [Tooltip("Maximum yaw angle from center (degrees left/right)")]
        [Range(90f, 180f)]
        [SerializeField] private float maxYaw = 160f;
        
        [Header("Chase Mode Settings")]
        [Tooltip("Distance behind aircraft")]
        [SerializeField] private float chaseDistance = 10f;
        
        [Tooltip("Height above aircraft")]
        [SerializeField] private float chaseHeight = 3f;
        
        [Tooltip("Chase position smoothing")]
        [Range(0.01f, 1f)]
        [SerializeField] private float chaseSmoothing = 0.1f;

        [Tooltip("Chase rotation smoothing")]
        [Range(0f, 1f)]
        [SerializeField] private float chaseRotationSmoothing = 0.12f;
        
        [Header("Cockpit Settings")]
        [Tooltip("Offset from aircraft pivot for cockpit camera")]
        [SerializeField] private Vector3 cockpitOffset = new Vector3(0f, 1.5f, 2f);

        [Tooltip("Smooth cockpit position to reduce jitter (0 = instant)")]
        [Range(0f, 1f)]
        [SerializeField] private float cockpitPositionSmoothing = 0.1f;

        [Tooltip("Smooth cockpit rotation to reduce jitter (0 = instant)")]
        [Range(0f, 1f)]
        [SerializeField] private float cockpitRotationSmoothing = 0.15f;

        [Header("HUD First Person Compensation")]
        [Tooltip("Aircraft controller used to derive climb/dive information for HUD-ready motion")]
        [SerializeField] private AircraftController aircraftController;

        [Tooltip("Auto-assign AircraftController from the aircraft transform if left empty")]
        [SerializeField] private bool autoFindAircraftController = true;

        [Tooltip("Blend toward the flight-path pitch so HUD feels anchored to real motion (0 = attitude only, 1 = full flight-path)")]
        [Range(0f, 1f)]
        [SerializeField] private float flightPathBlend = 0.6f;

        [Tooltip("Maximum pitch offset (degrees) contributed by flight-path blending")]
        [SerializeField] private float maxPitchCompensation = 12f;

        [Tooltip("Maximum vertical head-lag offset in meters when pulling positive/negative flight path angles")]
        [SerializeField] private float verticalOffsetMeters = 0.15f;

        [Tooltip("Vertical speed in m/s that produces full head-lag offset")]
        [SerializeField] private float verticalSpeedForFullOffset = 25f;

        [Tooltip("How quickly climb/dive compensation reacts (higher = snappier)")]
        [SerializeField] private float compensationSmoothing = 4f;
        
        [Header("Input")]
        [Tooltip("Mouse button to hold for free look (0=left, 1=right, 2=middle)")]
        [SerializeField] private int lookButton = 1;
        
        [Tooltip("Key to reset camera to forward view")]
        [SerializeField] private KeyCode resetKey = KeyCode.R;
        
        [Tooltip("Key to cycle camera modes")]
        [SerializeField] private KeyCode cycleModeKey = KeyCode.V;
        
        [Header("Debug")]
        [SerializeField] private bool showDebugInfo = false;
        
        #endregion
        
        #region Private Fields
        
        private bool _isLookActive;
        private bool _panelPointerCapture;
        public bool PanelPointerCaptured => _panelPointerCapture;
        public void SetPanelPointerCapture(bool captured) { _panelPointerCapture = captured; }
        private bool _lookWasHeld;
        private float _returnElapsed;
        private Vector2 _returnStart;
        [SerializeField, Min(0.1f)] private float autoReturnSeconds = 0.8f;
        private float _currentPitch;
        private float _currentYaw;
        private bool _panelInspection;
        private Vector2 _panelInspectionAngles;
        public bool IsPanelInspectionActive => _panelInspection;

        // Panel inspection and the pilot's FORWARD action share one ease-in/ease-out law (smoothstep) with both axes on one
        // progress parameter, so the view never dog-legs, never cuts, and yaw stays in the bounded seat domain (never behind the seat).
        [Tooltip("Shortest and longest panel-inspection turn (seconds). The duration scales with the sweep angle.")]
        [SerializeField, Range(.2f, .6f)] private float inspectionMinSeconds = .35f;
        [SerializeField, Range(.3f, .9f)] private float inspectionMaxSeconds = .5f;
        [Tooltip("Duration of the eased FORWARD / R return (seconds).")]
        [SerializeField, Range(.2f, 1.2f)] private float forwardReturnSeconds = .45f;
        private Vector2 _inspectionStart;
        private float _inspectionElapsed, _inspectionDuration, _returnDuration;

        // Desktop inspection zoom: a critically damped vertical-FOV override that is restored EXACTLY (the captured base value)
        // when the inspection ends by any route. Never applied to a tracked HMD or while an XR device is active.
        [Tooltip("Settling time of the desktop inspection zoom (critically damped, seconds).")]
        [SerializeField, Range(.25f, .6f)] private float fieldOfViewSeconds = .42f;
        /// <summary>Narrowest vertical FOV the inspection zoom may request (degrees).</summary>
        public const float MinFieldOfViewOverride = 15f;
        private UnityEngine.Camera _viewCamera;
        private bool _fovOverride, _fovRestoring, _fovMoving;
        private float _fovBase, _fovTarget, _fovVelocity, _fovElapsed;
        /// <summary>Current manual/inspection look offset (pitch, yaw) in degrees; yaw stays within the seat range.</summary>
        public Vector2 LookOffset => new Vector2(_currentPitch, _currentYaw);
        /// <summary>True while an inspection turn, an eased forward return or the inspection zoom is still moving the view.</summary>
        public bool IsViewTransitioning => _fovMoving || (
            _panelInspection ? _inspectionElapsed < _inspectionDuration :
            !_isLookActive && _returnDuration > 0f && _returnElapsed < _returnDuration && _returnStart != Vector2.zero);
        
        // Smoothed mouse input
        private float _smoothedMouseX;
        private float _smoothedMouseY;
        
        // Target for smooth return
        private float _targetPitch;
        private float _targetYaw;
        
        // Chase mode
        private Vector3 _chaseVelocity;
        
        // Free rotation storage
        private Quaternion _freeRotation;
        private Quaternion _lastAircraftRotation;

        // First person HUD compensation
        private float _smoothedFlightPathPitch;
        private float _currentVerticalOffset;
        private Quaternion _aircraftReferenceRotation = Quaternion.identity;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Current camera mode
        /// </summary>
        public CameraMode Mode
        {
            get => cameraMode;
            set => SetCameraMode(value);
        }
        
        /// <summary>
        /// Whether the user is currently looking around
        /// </summary>
        public bool IsLookActive => _isLookActive;
        public Transform AircraftTransform => aircraftTransform;

        /// <summary>
        /// The aircraft-relative cockpit viewing rotation with the manual
        /// look-around offset removed. Conformal HUDs use this reference so
        /// their boresight remains tied to the aircraft while the pilot looks
        /// through the side windows.
        /// </summary>
        public Quaternion AircraftReferenceRotation => _aircraftReferenceRotation;

        /// <summary>
        /// Share (0..1) of the flight-path angle blended into the cockpit reference pitch. 0 keeps the view on the airframe
        /// boresight (waterline at screen centre), which a conformal HUD mode should use; the serialized scene value is the default.
        /// </summary>
        public float FlightPathBlend
        {
            get => flightPathBlend;
            set => flightPathBlend = float.IsNaN(value) || float.IsInfinity(value) ? flightPathBlend : Mathf.Clamp01(value);
        }

        /// <summary>A desktop inspection zoom target is set (see <see cref="SetInspectionFieldOfView"/>).</summary>
        public bool IsFieldOfViewOverridden => _fovOverride;
        /// <summary>The zoom is still moving toward its target, or back toward <see cref="BaseFieldOfView"/>.</summary>
        public bool IsFieldOfViewTransitioning => _fovMoving;
        /// <summary>The vertical FOV that is restored after an inspection zoom (the camera's own FOV when no zoom is active).</summary>
        public float BaseFieldOfView => _fovOverride || _fovRestoring ? _fovBase : ViewCamera != null ? ViewCamera.fieldOfView : 60f;
        /// <summary>Vertical FOV the view is easing toward (the base FOV when no zoom is set).</summary>
        public float FieldOfViewTarget => _fovOverride ? _fovTarget : BaseFieldOfView;
        private UnityEngine.Camera ViewCamera => _viewCamera != null ? _viewCamera : (_viewCamera = GetComponent<UnityEngine.Camera>());

        #endregion
        
        #region Unity Lifecycle
        
        private void Start()
        {
            if (!ResolveTarget())
            {
                Debug.LogWarning("[AircraftCameraController] Waiting for an aircraft target; view will bind when it becomes available.");
                return;
            }

            if (autoFindAircraftController && aircraftController == null)
            {
                aircraftController = aircraftTransform.GetComponent<AircraftController>()
                    ?? aircraftTransform.GetComponentInParent<AircraftController>();
            }
            
            // Initialize rotation tracking
            _freeRotation = aircraftTransform.rotation;
            _lastAircraftRotation = aircraftTransform.rotation;
            _currentPitch = 0f;
            _currentYaw = 0f;
            _smoothedFlightPathPitch = GetAircraftPitchDegrees();
            _aircraftReferenceRotation = aircraftTransform.rotation;
        }
        
        private void LateUpdate()
        {
            if (!ResolveTarget()) return;
#if ENABLE_INPUT_SYSTEM
            // A real HMD (or explicit XR simulator test) owns its pose. Never
            // spring a physically tracked head back to the aircraft direction.
            var trackedPose = GetComponent<TrackedPoseDriver>();
            if (trackedPose != null && trackedPose.isActiveAndEnabled)
            {
                // Keep the conformal reference current without taking ownership
                // of the tracked head pose or applying desktop look compensation.
                _aircraftReferenceRotation = aircraftTransform.rotation;
                RestoreFieldOfViewImmediate(); // Never zoom a tracked head.
                return;
            }
#endif
            if (UnityEngine.XR.XRSettings.isDeviceActive) RestoreFieldOfViewImmediate();
            
            // Check for mode cycle
            if (KeyPressed(cycleModeKey))
            {
                CycleCameraMode();
            }
            
            // Check for reset
            if (KeyPressed(resetKey))
            {
                ResetView();
            }
            
            // Handle look input
            HandleLookInput();
            
            // Update camera based on mode
            switch (cameraMode)
            {
                case CameraMode.Cockpit:
                    UpdateCockpitCamera();
                    break;
                case CameraMode.Chase:
                    UpdateChaseCamera();
                    break;
                case CameraMode.Free:
                    UpdateFreeCamera();
                    break;
            }
            StepFieldOfView(Time.unscaledDeltaTime);
        }

        private void OnDisable() => RestoreFieldOfViewImmediate();
        
        #endregion
        
        #region Input Handling
        
        private void HandleLookInput()
        {
            bool held;
            Vector2 delta;
#if ENABLE_INPUT_SYSTEM
            var mouse = Mouse.current;
            held = mouse != null && (lookButton == 0 ? mouse.leftButton.isPressed :
                lookButton == 2 ? mouse.middleButton.isPressed : mouse.rightButton.isPressed);
            delta = mouse != null ? mouse.delta.ReadValue() * 0.1f : Vector2.zero;
#else
            held = Input.GetMouseButton(lookButton);
            delta = new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));
#endif
            ProcessLookInput(held && Application.isFocused, delta,
                EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(), Time.unscaledDeltaTime);
        }

        private void ProcessLookInput(bool held, Vector2 delta, bool overUi, float dt)
        {
            if (_panelPointerCapture)
            {
                _isLookActive = false; _lookWasHeld = held;
                return; // Right-button panel ownership wins until release, even after leaving its bounds.
            }
            if (_panelInspection)
            {
                bool deliberateLookPress = held && !_lookWasHeld && !overUi;
                _lookWasHeld = held;
                if (!deliberateLookPress)
                {
                    _isLookActive = false;
                    _inspectionElapsed += Mathf.Max(0f, dt);
                    float e = EaseInOut(_inspectionDuration > 0f ? _inspectionElapsed / _inspectionDuration : 1f);
                    // Plain (non-wrapping) interpolation: a left-to-right change of panel always turns through the front.
                    _currentPitch = Mathf.Lerp(_inspectionStart.x, _panelInspectionAngles.x, e);
                    _currentYaw = Mathf.Lerp(_inspectionStart.y, _panelInspectionAngles.y, e);
                    return;
                }
                _currentPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, _currentPitch), minPitch, maxPitch);
                _currentYaw = Mathf.Clamp(Mathf.DeltaAngle(0f, _currentYaw), -maxYaw, maxYaw);
                _panelInspection = false;
                ReleaseFieldOfView(); // The inspection zoom belongs to the inspection.
                _lookWasHeld = false; // Pass the genuine outside-UI press to the normal capture path.
            }
            bool wasActive = _isLookActive;
            // Only a press that starts outside UI can capture the view. Dragging
            // a chart or slider must not turn the camera after leaving that UI.
            if (!held) _isLookActive = false;
            else if (!_lookWasHeld) _isLookActive = !overUi;
            _lookWasHeld = held;
            if (_isLookActive)
            {
                if (!wasActive) _freeRotation = aircraftTransform != null ? aircraftTransform.rotation : transform.rotation;
                _currentYaw = Mathf.Clamp(_currentYaw + delta.x * mouseSensitivity, -maxYaw, maxYaw);
                _currentPitch = Mathf.Clamp(_currentPitch - delta.y * mouseSensitivity, minPitch, maxPitch);
                _returnStart = new Vector2(_currentPitch, _currentYaw);
                _returnElapsed = 0f;
                _returnDuration = 0f; // A released free look uses the free-look return time, not the FORWARD time.
                return;
            }
            _returnElapsed += Mathf.Max(0f, dt);
            Vector2 offset = ReturnLookOffset(_returnStart, _returnElapsed, _returnDuration > 0f ? _returnDuration : autoReturnSeconds);
            _currentPitch = offset.x;
            _currentYaw = offset.y;
            _smoothedMouseX = _smoothedMouseY = 0f;
        }

        public static Vector2 ReturnLookOffset(Vector2 start, float elapsed, float duration)
        {
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.1f, duration));
            return start * (1f - t * t * (3f - 2f * t));
        }

        /// <summary>Smoothstep ease-in/ease-out on 0..1 (zero velocity at both ends).</summary>
        public static float EaseInOut(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        /// <summary>Inspection turn duration for a sweep (degrees): the shortest turn is minSeconds, a sweep of 180 degrees or more is maxSeconds.</summary>
        public static float InspectionTurnSeconds(float sweepDegrees, float minSeconds, float maxSeconds) =>
            Mathf.Lerp(minSeconds, Mathf.Max(minSeconds, maxSeconds), Mathf.Clamp01(Mathf.Abs(sweepDegrees) / 180f));

        private static bool KeyPressed(KeyCode key)
        {
#if ENABLE_INPUT_SYSTEM
            return Keyboard.current != null && System.Enum.TryParse(key.ToString(), true, out Key inputKey)
                && inputKey != Key.None && Keyboard.current[inputKey].wasPressedThisFrame;
#else
            return Input.GetKeyDown(key);
#endif
        }

        private bool ResolveTarget()
        {
            if (aircraftTransform != null) return true;
            if (aircraftController == null && autoFindAircraftController)
                aircraftController = FindFirstObjectByType<AircraftController>();
            if (aircraftController == null) return false;
            SetTarget(aircraftController.transform);
            return true;
        }

        private void OnApplicationFocus(bool focused)
        {
            if (focused) return;
            if (_panelInspection) ReleaseFieldOfView();
            _panelInspection = false;
            _isLookActive = false;
            _returnStart = new Vector2(_currentPitch, _currentYaw);
            _returnElapsed = 0f;
        }
        
        #endregion
        
        #region Camera Mode Updates
        
        private void UpdateCockpitCamera()
        {
            // Position in cockpit
            Vector3 cockpitPosition = aircraftTransform.TransformPoint(cockpitOffset);

            // Calculate base rotation and then apply climb/dive compensation so HUD stays believable in first person
            Quaternion aircraftRotation = aircraftTransform.rotation;
            Vector3 aircraftEuler = aircraftRotation.eulerAngles;
            float basePitch = GetAircraftPitchDegrees();
            float compensatedPitch = GetCompensatedPitch(basePitch);
            aircraftEuler.x = -compensatedPitch; // Invert so nose-up points camera to the sky
            Quaternion compensatedRotation = Quaternion.Euler(aircraftEuler);
            _aircraftReferenceRotation = compensatedRotation;

            // Apply vertical head-lag offset driven by climb/dive
            cockpitPosition += GetVerticalOffset(compensatedRotation);
            float positionLerp = GetSmoothingFactor(cockpitPositionSmoothing);
            if (cockpitPositionSmoothing <= 0f)
            {
                transform.position = cockpitPosition;
            }
            else
            {
                transform.position = Vector3.Lerp(transform.position, cockpitPosition, positionLerp);
            }
            
            // Combine compensated aircraft rotation with look offset (local rotation)
            Quaternion lookOffset = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            Quaternion targetRotation = compensatedRotation * lookOffset;
            // The manual offset already has a timed smooth return. Filtering
            // aircraft heading here introduces a second, misleading view lag.
            transform.rotation = targetRotation;
        }
        
        private void UpdateChaseCamera()
        {
            _aircraftReferenceRotation = aircraftTransform.rotation;
            // Calculate desired position behind aircraft
            Vector3 desiredPosition = aircraftTransform.position 
                - aircraftTransform.forward * chaseDistance 
                + Vector3.up * chaseHeight;
            
            // Smooth follow
            transform.position = Vector3.SmoothDamp(
                transform.position, 
                desiredPosition, 
                ref _chaseVelocity, 
                chaseSmoothing
            );
            
            // Look at aircraft with look offset
            Vector3 lookTarget = aircraftTransform.position;
            Quaternion baseLookRotation = Quaternion.LookRotation(lookTarget - transform.position);
            Quaternion lookOffset = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            
            Quaternion targetRotation = baseLookRotation * lookOffset;
            float rotationLerp = GetSmoothingFactor(chaseRotationSmoothing);
            if (chaseRotationSmoothing <= 0f)
            {
                transform.rotation = targetRotation;
            }
            else
            {
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationLerp);
            }
        }
        
        private void UpdateFreeCamera()
        {
            _lastAircraftRotation = aircraftTransform.rotation;
            _aircraftReferenceRotation = aircraftTransform.rotation;
            
            // Update free rotation with aircraft movement and look input
            if (!_isLookActive)
            {
                // Follow aircraft rotation smoothly
                _freeRotation = Quaternion.Slerp(_freeRotation, aircraftTransform.rotation, returnSpeed * Time.deltaTime);
            }
            
            // Apply look offset
            Quaternion lookOffset = Quaternion.Euler(_currentPitch, _currentYaw, 0f);
            transform.rotation = _freeRotation * lookOffset;
            
            // Position follows aircraft
            transform.position = aircraftTransform.position;
        }
        
        #endregion
        
        #region Public Methods
        
        /// <summary>
        /// Set the camera mode
        /// </summary>
        public void SetCameraMode(CameraMode mode)
        {
            cameraMode = mode;
            ResetViewImmediate();
            
            if (showDebugInfo)
            {
                Debug.Log($"[AircraftCameraController] Mode changed to: {mode}");
            }
        }
        
        /// <summary>
        /// Cycle through available camera modes
        /// </summary>
        public void CycleCameraMode()
        {
            int nextMode = ((int)cameraMode + 1) % 3;
            SetCameraMode((CameraMode)nextMode);
        }
        
        /// <summary>
        /// Pilot FORWARD action (R key, FORWARD button): ends any panel inspection and eases the view back to the aircraft
        /// boresight over <see cref="forwardReturnSeconds"/> with the same smoothstep law as the inspection turn. No cut.
        /// Chase/free cameras and mode changes use <see cref="ResetViewImmediate"/>.
        /// </summary>
        public void ResetView()
        {
            if (cameraMode != CameraMode.Cockpit) { ResetViewImmediate(); return; }
            _panelInspection = false;
            ReleaseFieldOfView(); // Eased back to the exact base FOV alongside the return turn.
            _isLookActive = false;
            _currentPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, _currentPitch), minPitch, maxPitch);
            _currentYaw = Mathf.Clamp(Mathf.DeltaAngle(0f, _currentYaw), -maxYaw, maxYaw);
            _returnStart = new Vector2(_currentPitch, _currentYaw);
            _returnElapsed = 0f;
            _returnDuration = forwardReturnSeconds;
            _targetPitch = 0f;
            _targetYaw = 0f;
            _smoothedMouseX = 0f;
            _smoothedMouseY = 0f;
        }

        /// <summary>Snaps the view forward in one frame (camera-mode changes, chase/free camera).</summary>
        public void ResetViewImmediate()
        {
            _panelInspection = false;
            RestoreFieldOfViewImmediate();
            _returnStart = Vector2.zero;
            _returnElapsed = 0f;
            _returnDuration = 0f;
            _currentPitch = 0f;
            _currentYaw = 0f;
            _targetPitch = 0f;
            _targetYaw = 0f;
            _smoothedMouseX = 0f;
            _smoothedMouseY = 0f;
            
            if (aircraftTransform != null)
            {
                _freeRotation = aircraftTransform.rotation;
                _lastAircraftRotation = aircraftTransform.rotation;
            }
        }
        
        /// <summary>
        /// Set the target aircraft transform
        /// </summary>
        public void SetTarget(Transform target)
        {
            aircraftTransform = target;
            if (autoFindAircraftController && target != null)
                aircraftController = target.GetComponent<AircraftController>() ?? target.GetComponentInParent<AircraftController>();
            if (target != null)
            {
                _freeRotation = target.rotation;
                _lastAircraftRotation = target.rotation;
            }
        }
        
        /// <summary>
        /// Set look sensitivity
        /// </summary>
        public void SetSensitivity(float sensitivity)
        {
            mouseSensitivity = Mathf.Clamp(sensitivity, 0.5f, 10f);
        }

        /// <summary>Explicit desktop-only side inspection. Never moves UI into the forward view or drives a tracked HMD.</summary>
        public bool BeginPanelInspection(float yaw,float elevation)
        {
            if (!isActiveAndEnabled || cameraMode != CameraMode.Cockpit || UnityEngine.XR.XRSettings.isDeviceActive ||
                float.IsNaN(yaw) || float.IsInfinity(yaw) || float.IsNaN(elevation) || float.IsInfinity(elevation)) return false;
#if ENABLE_INPUT_SYSTEM
            var tracking = GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
            if (tracking != null && tracking.isActiveAndEnabled) return false;
#endif
            // Work in the bounded seat domain (yaw within +/-maxYaw, no wrap) so the eased path always passes through forward.
            _currentPitch = Mathf.Clamp(Mathf.DeltaAngle(0f, _currentPitch), minPitch, maxPitch);
            _currentYaw = Mathf.Clamp(Mathf.DeltaAngle(0f, _currentYaw), -maxYaw, maxYaw);
            _panelInspectionAngles = new Vector2(Mathf.Clamp(-elevation,minPitch,maxPitch),Mathf.Clamp(Mathf.DeltaAngle(0f,yaw),-maxYaw,maxYaw));
            _inspectionStart = new Vector2(_currentPitch, _currentYaw);
            float sweep = Mathf.Max(Mathf.Abs(_panelInspectionAngles.y - _currentYaw), Mathf.Abs(_panelInspectionAngles.x - _currentPitch));
            _inspectionDuration = InspectionTurnSeconds(sweep, inspectionMinSeconds, inspectionMaxSeconds);
            _inspectionElapsed = 0f;
            _returnStart = Vector2.zero; _returnElapsed = 0f; _returnDuration = 0f;
            _panelInspection = true; _isLookActive = _lookWasHeld = false;
            // A previous panel's zoom is released; the caller sets this panel's zoom (if any) right after, and the
            // critically damped FOV then moves on from where it is, with its velocity preserved (no jump).
            ReleaseFieldOfView();
            return true;
        }

        /// <summary>
        /// Desktop inspection zoom (call after <see cref="BeginPanelInspection"/>): eases the vertical FOV, critically damped over
        /// about <see cref="fieldOfViewSeconds"/>, to <paramref name="verticalFov"/> so a side panel's text is legible. It never widens
        /// beyond <see cref="BaseFieldOfView"/>, never runs on a tracked HMD or an active XR device, and the exact base FOV is restored
        /// when the inspection ends (FORWARD/R, a deliberate look, a mode change, focus loss) or on <see cref="ReleaseFieldOfView"/>.
        /// </summary>
        public bool SetInspectionFieldOfView(float verticalFov)
        {
            var view = ViewCamera;
            if (!_panelInspection || view == null || !isActiveAndEnabled || cameraMode != CameraMode.Cockpit ||
                UnityEngine.XR.XRSettings.isDeviceActive || view.stereoEnabled || float.IsNaN(verticalFov) || float.IsInfinity(verticalFov)) return false;
#if ENABLE_INPUT_SYSTEM
            var tracking = GetComponent<TrackedPoseDriver>();
            if (tracking != null && tracking.isActiveAndEnabled) return false;
#endif
            if (!_fovOverride && !_fovRestoring) { _fovBase = view.fieldOfView; _fovVelocity = 0f; }
            _fovTarget = Mathf.Clamp(verticalFov, Mathf.Min(MinFieldOfViewOverride, _fovBase), _fovBase);
            _fovOverride = true; _fovRestoring = false; _fovElapsed = 0f; _fovMoving = true;
            return true;
        }

        /// <summary>Eases the view back to the exact <see cref="BaseFieldOfView"/> (no effect when no zoom is active).</summary>
        public void ReleaseFieldOfView()
        {
            if (!_fovOverride) return;
            _fovOverride = false; _fovRestoring = true; _fovElapsed = 0f; _fovMoving = true;
        }

        /// <summary>Restores the exact base FOV in one frame (mode change, XR, disable).</summary>
        public void RestoreFieldOfViewImmediate()
        {
            if (!_fovOverride && !_fovRestoring) return;
            var view = ViewCamera;
            if (view != null) view.fieldOfView = _fovBase;
            _fovOverride = _fovRestoring = _fovMoving = false; _fovVelocity = 0f; _fovElapsed = 0f;
        }

        /// <summary>Angular frequency of the critically damped zoom: within 0.2 % of the step after <paramref name="seconds"/>.</summary>
        public static float FieldOfViewOmega(float seconds) => 8.4f / Mathf.Max(.05f, seconds);

        /// <summary>Exact critically damped step toward <paramref name="target"/> over <paramref name="dt"/> (stable for any dt, no overshoot from rest).</summary>
        public static void CriticallyDampedStep(ref float value, ref float velocity, float target, float omega, float dt)
        {
            if (dt <= 0f || omega <= 0f || float.IsNaN(dt) || float.IsInfinity(dt)) return;
            float x = value - target, j = velocity + omega * x, e = Mathf.Exp(-omega * dt);
            value = target + (x + j * dt) * e;
            velocity = (velocity - omega * j * dt) * e;
        }

        private void StepFieldOfView(float dt)
        {
            if (!_fovMoving) return;
            var view = ViewCamera;
            if (view == null) { _fovOverride = _fovRestoring = _fovMoving = false; return; }
            float target = _fovOverride ? _fovTarget : _fovBase, value = view.fieldOfView;
            CriticallyDampedStep(ref value, ref _fovVelocity, target, FieldOfViewOmega(fieldOfViewSeconds), dt);
            _fovElapsed += Mathf.Max(0f, dt);
            bool settled = Mathf.Abs(value - target) < .01f && Mathf.Abs(_fovVelocity) < .5f;
            if (_fovRestoring && (settled || _fovElapsed >= fieldOfViewSeconds * 2f))
            {
                view.fieldOfView = _fovBase; // exact restore
                _fovRestoring = _fovMoving = false; _fovVelocity = 0f;
                return;
            }
            if (_fovOverride && settled) { value = target; _fovVelocity = 0f; _fovMoving = false; }
            if (!Mathf.Approximately(view.fieldOfView, value)) view.fieldOfView = value;
        }
        
        #endregion
        
        #region Debug
        
        private void OnGUI()
        {
            if (!showDebugInfo) return;
            
            GUILayout.BeginArea(new Rect(Screen.width - 260, 10, 250, 150));
            GUILayout.BeginVertical("box");
            
            GUILayout.Label("=== Camera Controller ===");
            GUILayout.Label($"Mode: {cameraMode}");
            GUILayout.Label($"Looking: {_isLookActive}");
            GUILayout.Label($"Pitch: {_currentPitch:F1}° | Yaw: {_currentYaw:F1}°");
            GUILayout.Label($"[RMB] Look | [R] Reset | [V] Cycle Mode");
            
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        #endregion

        #region Helpers

        private float GetAircraftPitchDegrees()
        {
            if (aircraftController != null && aircraftController.State != null)
            {
                return aircraftController.State.Pitch;
            }

            if (aircraftTransform == null)
            {
                return 0f;
            }

            Vector3 forward = aircraftTransform.forward;
            float horizontalMag = new Vector2(forward.x, forward.z).magnitude;
            return Mathf.Atan2(forward.y, horizontalMag) * Mathf.Rad2Deg;
        }

        private float GetCompensatedPitch(float basePitch)
        {
            if (aircraftController == null || aircraftController.State == null || flightPathBlend <= 0f)
            {
                return basePitch;
            }

            var state = aircraftController.State;
            float flightPathPitch = Mathf.Atan2(state.VerticalSpeedMps, Mathf.Max(state.GroundSpeedMps, 0.1f)) * Mathf.Rad2Deg;
            float clampedPitch = Mathf.Clamp(flightPathPitch, basePitch - maxPitchCompensation, basePitch + maxPitchCompensation);
            _smoothedFlightPathPitch = Mathf.Lerp(_smoothedFlightPathPitch, clampedPitch, Time.deltaTime * compensationSmoothing);
            return Mathf.Lerp(basePitch, _smoothedFlightPathPitch, flightPathBlend);
        }

        private Vector3 GetVerticalOffset(Quaternion referenceRotation)
        {
            if (aircraftController == null || aircraftController.State == null || verticalOffsetMeters <= 0f)
            {
                return Vector3.zero;
            }

            var state = aircraftController.State;
            float normalizedVs = 0f;
            if (verticalSpeedForFullOffset > 0.01f)
            {
                normalizedVs = Mathf.Clamp(state.VerticalSpeedMps / verticalSpeedForFullOffset, -1f, 1f);
            }

            float targetOffset = -normalizedVs * verticalOffsetMeters; // Positive climb pushes pilot down into the seat
            _currentVerticalOffset = Mathf.Lerp(_currentVerticalOffset, targetOffset, Time.deltaTime * compensationSmoothing);
            return referenceRotation * (Vector3.up * _currentVerticalOffset);
        }

        private static float NormalizeAngle(float angle)
        {
            angle %= 360f;
            if (angle > 180f) angle -= 360f;
            return angle;
        }

        private static float GetSmoothingFactor(float smoothing)
        {
            if (smoothing <= 0f)
            {
                return 1f;
            }

            return Mathf.Clamp01(smoothing * 60f * Time.deltaTime);
        }
        
        #endregion
    }
}
