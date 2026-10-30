using UnityEngine;

namespace AircraftControl.Camera
{
    /// <summary>Moves the aircraft tracking frame while the pose driver owns the head's local pose.</summary>
    public sealed class AircraftNativeXrOrigin
    {
        public Transform Origin { get; private set; }
        public bool SeatCalibrated { get; private set; }
        public int CalibrationVersion { get; private set; }
        public Quaternion BodyReferenceRotation => Origin != null ? Origin.rotation * neutralYaw : Quaternion.identity;

        private Transform head, originalParent;
        private Vector3 originalPosition, originalScale, neutralPosition;
        private Quaternion originalRotation, neutralYaw;
        private int originalSibling;

        // Flight data uses nose-up positive pitch; Unity's positive X points the nose down.
        public static Quaternion BodyRotation(float pitch, float roll, float heading) => Quaternion.Euler(-pitch, heading, -roll);

        /// <summary>A reported absence cannot establish a physical seat; providers without presence retain tracked fallback.</summary>
        public static bool CanCalibrateSeat(bool trackingValid, bool presenceAvailable, bool userPresent) =>
            trackingValid && (!presenceAvailable || userPresent);

        public void Follow(Transform trackedHead, Vector3 aircraftPosition, Quaternion bodyRotation, Vector3 cockpitOffset,
            bool trackingValid = true, bool presenceAvailable = false, bool userPresent = false)
        {
            if (trackedHead == null) return;
            if (head != trackedHead || Origin == null) Bind(trackedHead);
            if (!SeatCalibrated && CanCalibrateSeat(trackingValid, presenceAvailable, userPresent)) CalibrateSeat();
            Quaternion rotation = bodyRotation * Quaternion.Inverse(neutralYaw);
            Vector3 seat = aircraftPosition + bodyRotation * cockpitOffset;
            Origin.SetPositionAndRotation(seat - rotation * neutralPosition, rotation);
        }

        private void Bind(Transform trackedHead)
        {
            Release();
            head = trackedHead;
            originalParent = head.parent;
            originalPosition = head.localPosition;
            originalRotation = head.localRotation;
            originalScale = head.localScale;
            originalSibling = head.GetSiblingIndex();
            Origin = new GameObject("FAA Native XR Aircraft Tracking Origin").transform;
            Origin.SetParent(originalParent, false);
            // Keep the exact tracking-space pose. Only the new parent moves during flight.
            head.SetParent(Origin, false);
            // An idle headset still needs an aircraft-relative frame for the desktop mirror.
            // This provisional neutral does not claim that a physical seat pose was observed.
            neutralPosition = Vector3.zero;
            neutralYaw = Quaternion.identity;
            SeatCalibrated = false;
        }

        private void CalibrateSeat()
        {
            neutralPosition = head.localPosition;
            Vector3 forward = Vector3.ProjectOnPlane(head.localRotation * Vector3.forward, Vector3.up);
            neutralYaw = forward.sqrMagnitude > .0001f ? Quaternion.LookRotation(forward.normalized, Vector3.up) : Quaternion.identity;
            SeatCalibrated = true;
            CalibrationVersion++;
        }

        /// <summary>Explicit recenter uses the same tracked/presence gate and leaves an existing seat unchanged on refusal.</summary>
        public bool Recenter(Vector3 aircraftPosition, Quaternion bodyRotation, Vector3 cockpitOffset,
            bool trackingValid = true, bool presenceAvailable = false, bool userPresent = false)
        {
            if (head == null || Origin == null || !CanCalibrateSeat(trackingValid, presenceAvailable, userPresent)) return false;
            CalibrateSeat();
            Follow(head, aircraftPosition, bodyRotation, cockpitOffset);
            return true;
        }

        public void Release()
        {
            if (head != null && Origin != null && head.parent == Origin)
            {
                head.SetParent(originalParent, false);
                head.localPosition = originalPosition;
                head.localRotation = originalRotation;
                head.localScale = originalScale;
                head.SetSiblingIndex(originalSibling);
            }
            if (Origin != null)
            {
                if (Application.isPlaying) Object.Destroy(Origin.gameObject);
                else Object.DestroyImmediate(Origin.gameObject);
            }
            Abandon();
        }

        /// <summary>
        /// Drops managed ownership during application/scene teardown. Unity owns destruction of that
        /// hierarchy; restoring its child while the parent deactivates can reactivate the camera.
        /// Unlike Release, this never reads or writes a Transform or schedules object destruction.
        /// </summary>
        public void Abandon()
        {
            Origin = null;
            SeatCalibrated = false;
            CalibrationVersion = 0;
            head = originalParent = null;
        }
    }
}
