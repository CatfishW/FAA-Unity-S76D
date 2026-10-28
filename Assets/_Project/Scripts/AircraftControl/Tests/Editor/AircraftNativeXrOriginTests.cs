using AircraftControl.Camera;
using NUnit.Framework;
using UnityEngine;

namespace AircraftControl.Tests.Editor
{
    public class AircraftNativeXrOriginTests
    {
        [Test]
        public void IdleHeadsetFollowsAProvisionalAircraftFrameWithoutClaimingSeatCalibration()
        {
            var head = new GameObject("Idle tracked camera");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                Vector3 rawPosition = new Vector3(.1f, 1.6f, -.2f);
                Quaternion rawRotation = Quaternion.Euler(-4, 20, 0);
                head.transform.SetLocalPositionAndRotation(rawPosition, rawRotation);
                Quaternion initialBody = AircraftNativeXrOrigin.BodyRotation(0, 0, 30);
                frame.Follow(head.transform, Vector3.zero, initialBody, Vector3.zero, false);
                Vector3 initialWaterline = Quaternion.Inverse(head.transform.rotation) * (initialBody * Vector3.forward);
                Quaternion turnedBody = AircraftNativeXrOrigin.BodyRotation(5, -10, 35);
                Vector3 position = new Vector3(100, 600, 50);
                frame.Follow(head.transform, position, turnedBody, Vector3.zero, false);
                Assert.That(frame.Origin, Is.Not.Null);
                Assert.That(frame.SeatCalibrated, Is.False);
                Assert.That(frame.CalibrationVersion, Is.Zero);
                Assert.That(head.transform.localPosition, Is.EqualTo(rawPosition));
                Assert.That(Quaternion.Angle(head.transform.localRotation, rawRotation), Is.LessThan(.05f));
                Assert.That(Vector3.Distance(Quaternion.Inverse(head.transform.rotation) * (turnedBody * Vector3.forward), initialWaterline), Is.LessThan(.001f));
                Assert.That(Vector3.Distance(frame.Origin.position, position), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(frame.BodyReferenceRotation, turnedBody), Is.LessThan(.05f));
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [Test]
        public void FirstTrackedSampleCalibratesOnceAndTemporaryTrackingLossPreservesThatSeat()
        {
            var head = new GameObject("Tracked head recovery");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(3, 4, 60);
                Vector3 seat = new Vector3(0, .07f, -.17f);
                frame.Follow(head.transform, Vector3.zero, body, seat, false);
                Vector3 neutral = new Vector3(.2f, 1.65f, -.1f);
                head.transform.SetLocalPositionAndRotation(neutral, Quaternion.Euler(0, 40, 0));
                frame.Follow(head.transform, Vector3.zero, body, seat, true);
                Assert.That(frame.SeatCalibrated, Is.True);
                Assert.That(frame.CalibrationVersion, Is.EqualTo(1));
                Assert.That(Vector3.Distance(head.transform.position, body * seat), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(head.transform.rotation, body), Is.LessThan(.05f));

                Vector3 rawPosition = neutral + new Vector3(.1f, .02f, -.03f);
                Quaternion rawRotation = Quaternion.Euler(-6, 55, 0);
                head.transform.SetLocalPositionAndRotation(rawPosition, rawRotation);
                foreach (bool tracked in new[] { true, false, false, true })
                {
                    body = Quaternion.AngleAxis(5, Vector3.up) * body;
                    frame.Follow(head.transform, Vector3.zero, body, seat, tracked);
                    Quaternion origin = body * Quaternion.Inverse(Quaternion.Euler(0, 40, 0));
                    Assert.That(frame.SeatCalibrated, Is.True);
                    Assert.That(frame.CalibrationVersion, Is.EqualTo(1), "Tracking loss/recovery must not establish another seat calibration.");
                    Assert.That(head.transform.localPosition, Is.EqualTo(rawPosition));
                    Assert.That(Vector3.Distance(head.transform.position, body * seat + origin * (rawPosition - neutral)), Is.LessThan(.001f));
                    Assert.That(Quaternion.Angle(head.transform.rotation, origin * rawRotation), Is.LessThan(.05f),
                        "Neither a later tracked sample nor tracking recovery may silently recenter the physical head.");
                }
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [Test]
        public void ReportedAbsenceWaitsForPresentTrackedPoseAndEstablishedSeatSurvivesAbsence()
        {
            var head = new GameObject("Presence-gated tracked head");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(4f, -6f, 80f);
                Vector3 position = new Vector3(20f, 300f, 40f), seat = new Vector3(0f, .07f, -.17f);
                for (int i = 0; i < 3; i++)
                {
                    head.transform.SetLocalPositionAndRotation(new Vector3(.2f + i, .6f, -.1f), Quaternion.Euler(0f, 75f, 0f));
                    frame.Follow(head.transform, position, body, seat, true, true, false);
                    Assert.That(frame.SeatCalibrated, Is.False, "A cached tracked sample from an unworn headset must remain provisional.");
                    Assert.That(frame.CalibrationVersion, Is.Zero);
                    Assert.That(Vector3.Distance(frame.Origin.position, position + body * seat), Is.LessThan(.001f));
                }
                Vector3 neutral = new Vector3(.1f, 1.65f, -.2f);
                Quaternion neutralRotation = Quaternion.Euler(0f, 35f, 0f);
                head.transform.SetLocalPositionAndRotation(neutral, neutralRotation);
                frame.Follow(head.transform, position, body, seat, true, true, true);
                Assert.That(frame.SeatCalibrated, Is.True);
                Assert.That(frame.CalibrationVersion, Is.EqualTo(1));
                Assert.That(Vector3.Distance(head.transform.position, position + body * seat), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(head.transform.rotation, body), Is.LessThan(.05f));

                Vector3 rawPosition = neutral + new Vector3(.15f, -.03f, .04f);
                Quaternion rawRotation = Quaternion.Euler(-5f, 48f, 2f);
                head.transform.SetLocalPositionAndRotation(rawPosition, rawRotation);
                foreach (bool tracked in new[] { true, false, true })
                {
                    body = Quaternion.AngleAxis(7f, Vector3.up) * body;
                    frame.Follow(head.transform, position, body, seat, tracked, true, false);
                    Quaternion origin = body * Quaternion.Inverse(neutralRotation);
                    Assert.That(frame.SeatCalibrated, Is.True, "Taking off the headset must not discard an established seat.");
                    Assert.That(frame.CalibrationVersion, Is.EqualTo(1));
                    Assert.That(head.transform.localPosition, Is.EqualTo(rawPosition));
                    Assert.That(Quaternion.Angle(head.transform.localRotation, rawRotation), Is.LessThan(.05f));
                    Assert.That(Vector3.Distance(head.transform.position, position + body * seat + origin * (rawPosition - neutral)), Is.LessThan(.001f));
                }
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProvidersWithoutPresenceKeepTheValidTrackedFallback(bool unusedPresenceValue)
        {
            var head = new GameObject("Provider without presence feature");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                head.transform.SetLocalPositionAndRotation(new Vector3(.2f, 1.6f, -.1f), Quaternion.Euler(0f, 25f, 0f));
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(2f, -3f, 90f);
                Vector3 position = new Vector3(50f, 400f, -20f);
                frame.Follow(head.transform, position, body, Vector3.zero, true, false, unusedPresenceValue);
                Assert.That(frame.SeatCalibrated, Is.True, "An unavailable optional presence feature must not disable another provider's tracked calibration.");
                Assert.That(frame.CalibrationVersion, Is.EqualTo(1));
                Assert.That(Vector3.Distance(head.transform.position, position), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(head.transform.rotation, body), Is.LessThan(.05f));
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [Test]
        public void ManualRecenterRejectsKnownAbsenceWithoutChangingTheEstablishedSeat()
        {
            var head = new GameObject("Known-absent manual recenter");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(0f, 0f, 100f);
                Vector3 position = new Vector3(20f, 40f, 60f);
                head.transform.SetLocalPositionAndRotation(new Vector3(.2f, .6f, -.1f), Quaternion.Euler(0f, 75f, 0f));
                frame.Follow(head.transform, position, body, Vector3.zero, true, true, false);
                Assert.That(frame.Recenter(position, body, Vector3.zero, true, true, false), Is.False);
                Assert.That(frame.SeatCalibrated, Is.False);
                Assert.That(frame.CalibrationVersion, Is.Zero);

                head.transform.SetLocalPositionAndRotation(new Vector3(.1f, 1.7f, -.2f), Quaternion.Euler(0f, 30f, 0f));
                frame.Follow(head.transform, position, body, Vector3.zero, true, true, true);
                Vector3 previousOrigin = frame.Origin.position;
                Quaternion previousRotation = frame.Origin.rotation;
                Vector3 rawPosition = new Vector3(.4f, 1.6f, .1f);
                Quaternion rawRotation = Quaternion.Euler(-4f, 50f, 0f);
                head.transform.SetLocalPositionAndRotation(rawPosition, rawRotation);
                Assert.That(frame.Recenter(position, body, Vector3.zero, true, true, false), Is.False);
                Assert.That(frame.SeatCalibrated, Is.True);
                Assert.That(frame.CalibrationVersion, Is.EqualTo(1));
                Assert.That(frame.Origin.position, Is.EqualTo(previousOrigin));
                Assert.That(Quaternion.Angle(frame.Origin.rotation, previousRotation), Is.LessThan(.001f));
                Assert.That(head.transform.localPosition, Is.EqualTo(rawPosition));
                Assert.That(Quaternion.Angle(head.transform.localRotation, rawRotation), Is.LessThan(.001f));
                Assert.That(frame.Recenter(position, body, Vector3.zero, true, true, true), Is.True);
                Assert.That(frame.CalibrationVersion, Is.EqualTo(2));
                Assert.That(Vector3.Distance(head.transform.position, position), Is.LessThan(.001f));
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [Test]
        public void PresenceCannotCalibrateAnUntrackedHeadset()
        {
            var head = new GameObject("Present but untracked head");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(0f, 0f, 30f);
                frame.Follow(head.transform, Vector3.zero, body, Vector3.zero, false, true, true);
                Assert.That(frame.SeatCalibrated, Is.False);
                Assert.That(frame.Recenter(Vector3.zero, body, Vector3.zero, false, true, true), Is.False);
                Assert.That(frame.CalibrationVersion, Is.Zero);
                frame.Follow(head.transform, Vector3.zero, body, Vector3.zero, true, true, true);
                Assert.That(frame.SeatCalibrated, Is.True);
                Assert.That(frame.CalibrationVersion, Is.EqualTo(1));
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [Test]
        public void ManualSeatRecenterRequiresRealTrackingAndCannotCalibrateAnIdleHeadset()
        {
            var head = new GameObject("Idle recenter fixture");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                head.transform.SetLocalPositionAndRotation(new Vector3(.2f, 1.6f, 0), Quaternion.Euler(0, 25, 0));
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(0, 0, 90);
                frame.Follow(head.transform, Vector3.zero, body, Vector3.zero, false);
                Vector3 provisionalPosition = frame.Origin.position;
                Assert.That(frame.Recenter(Vector3.zero, body, Vector3.zero, false), Is.False);
                Assert.That(frame.SeatCalibrated, Is.False);
                Assert.That(frame.Origin.position, Is.EqualTo(provisionalPosition));
                Assert.That(frame.Recenter(Vector3.zero, body, Vector3.zero, true), Is.True);
                Assert.That(frame.SeatCalibrated, Is.True);
                Vector3 calibratedPosition = frame.Origin.position;
                head.transform.localPosition += Vector3.right;
                Assert.That(frame.Recenter(Vector3.zero, body, Vector3.zero, false), Is.False);
                Assert.That(frame.Origin.position, Is.EqualTo(calibratedPosition));
                Assert.That(frame.SeatCalibrated, Is.True);
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [TestCase(0f, 5f, 0f)]
        [TestCase(8f, 5f, 15f)]
        [TestCase(-6f, -20f, -12f)]
        public void AircraftMotionMovesTheOriginWhileTrackedLocalPoseAndBoresightRemainStable(float pitch, float yawChange, float roll)
        {
            var stage = new GameObject("Original tracking stage");
            var head = new GameObject("Tracked camera", typeof(UnityEngine.Camera));
            var frame = new AircraftNativeXrOrigin();
            try
            {
                head.transform.SetParent(stage.transform, false);
                Vector3 rawPosition = new Vector3(.3f, 1.65f, -.2f);
                Quaternion rawRotation = Quaternion.Euler(0, 42f, 0);
                head.transform.SetLocalPositionAndRotation(rawPosition, rawRotation);
                var camera = head.GetComponent<UnityEngine.Camera>();
                Matrix4x4 projection = Matrix4x4.Frustum(-.09f, .12f, -.08f, .10f, .1f, 2500f);
                camera.projectionMatrix = projection;
                Vector3 seat = new Vector3(0, .07f, -.17f);
                frame.Follow(head.transform, Vector3.zero, AircraftNativeXrOrigin.BodyRotation(0, 0, 30f), seat);

                Quaternion body = AircraftNativeXrOrigin.BodyRotation(pitch, roll, 30f + yawChange);
                Vector3 position = new Vector3(125f, 700f, -85f);
                frame.Follow(head.transform, position, body, seat);
                Assert.That(head.transform.localPosition, Is.EqualTo(rawPosition), "The pose driver's local position must remain untouched.");
                Assert.That(Quaternion.Angle(head.transform.localRotation, rawRotation), Is.LessThan(.05f));
                Assert.That(Vector3.Distance(head.transform.position, position + body * seat), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(head.transform.rotation, body), Is.LessThan(.05f));
                Assert.That(Vector3.Distance(Quaternion.Inverse(head.transform.rotation) * (body * Vector3.forward), Vector3.forward), Is.LessThan(.001f),
                    "An AP heading/attitude change must not move the waterline away from a stationary neutral head.");
                Assert.That(camera.projectionMatrix, Is.EqualTo(projection), "The calibrated/asymmetric projection belongs to XR.");
            }
            finally { frame.Release(); Object.DestroyImmediate(stage); }
        }

        [Test]
        public void PhysicalHeadMotionRemainsRelativeToTheAircraftAndIsNeverOverwritten()
        {
            var stage = new GameObject("Tracking stage");
            var head = new GameObject("Tracked head");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                head.transform.SetParent(stage.transform, false);
                Vector3 neutral = new Vector3(.2f, 1.5f, .1f);
                head.transform.SetLocalPositionAndRotation(neutral, Quaternion.Euler(0, 35f, 0));
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(4f, -8f, 120f);
                Vector3 position = new Vector3(20, 30, 40), seat = new Vector3(0, 1, 2);
                frame.Follow(head.transform, position, body, seat);
                Vector3 rawPosition = neutral + new Vector3(.15f, .02f, -.1f);
                Quaternion rawRotation = Quaternion.Euler(-10f, 55f, 3f);
                head.transform.SetLocalPositionAndRotation(rawPosition, rawRotation);
                frame.Follow(head.transform, position, body, seat);
                Quaternion origin = body * Quaternion.Inverse(Quaternion.Euler(0, 35f, 0));
                Assert.That(head.transform.localPosition, Is.EqualTo(rawPosition));
                Assert.That(Quaternion.Angle(head.transform.localRotation, rawRotation), Is.LessThan(.05f));
                Assert.That(Vector3.Distance(head.transform.position, position + body * seat + origin * (rawPosition - neutral)), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(head.transform.rotation, origin * rawRotation), Is.LessThan(.05f));
            }
            finally { frame.Release(); Object.DestroyImmediate(stage); }
        }

        [Test]
        public void SeatRecenterUsesTheCurrentRawPoseAndReleaseRestoresOriginalHierarchy()
        {
            var stage = new GameObject("Original stage");
            var head = new GameObject("Tracked head");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                head.transform.SetParent(stage.transform, false);
                Vector3 originalPosition = new Vector3(.2f, 1.7f, -.1f);
                Quaternion originalRotation = Quaternion.Euler(-3, 20, 0);
                head.transform.SetLocalPositionAndRotation(originalPosition, originalRotation);
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(5, 10, 80);
                Vector3 position = new Vector3(10, 20, 30), seat = new Vector3(0, .07f, -.17f);
                frame.Follow(head.transform, position, body, seat);
                Vector3 currentPosition = new Vector3(.4f, 1.6f, .1f);
                Quaternion currentRotation = Quaternion.Euler(-8, 60, 0);
                head.transform.SetLocalPositionAndRotation(currentPosition, currentRotation);
                frame.Recenter(position, body, seat);
                Assert.That(head.transform.localPosition, Is.EqualTo(currentPosition));
                Assert.That(Quaternion.Angle(head.transform.localRotation, currentRotation), Is.LessThan(.05f));
                Assert.That(Vector3.Distance(head.transform.position, position + body * seat), Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(frame.BodyReferenceRotation, body), Is.LessThan(.05f));
                frame.Release();
                Assert.That(head.transform.parent, Is.EqualTo(stage.transform));
                Assert.That(head.transform.localPosition, Is.EqualTo(originalPosition));
                Assert.That(Quaternion.Angle(head.transform.localRotation, originalRotation), Is.LessThan(.05f));
            }
            finally { frame.Release(); Object.DestroyImmediate(stage); }
        }

        [Test]
        public void BodyDiagnosticsReadTheActualOriginRatherThanTheLastRequestedRotation()
        {
            var head = new GameObject("Tracked head");
            var frame = new AircraftNativeXrOrigin();
            try
            {
                Quaternion body = AircraftNativeXrOrigin.BodyRotation(4, 6, 100);
                frame.Follow(head.transform, Vector3.zero, body, Vector3.zero);
                frame.Origin.rotation = Quaternion.AngleAxis(12f, Vector3.up) * frame.Origin.rotation;
                Assert.That(Quaternion.Angle(frame.BodyReferenceRotation, body), Is.EqualTo(12f).Within(.01f));
                frame.Follow(head.transform, Vector3.zero, body, Vector3.zero);
                Assert.That(Quaternion.Angle(frame.BodyReferenceRotation, body), Is.LessThan(.05f));
            }
            finally { frame.Release(); Object.DestroyImmediate(head); }
        }

        [Test]
        public void PositiveFlightDataPitchPointsTheNativeBodyNoseUp()
        {
            Vector3 forward = AircraftNativeXrOrigin.BodyRotation(5, 0, 0) * Vector3.forward;
            Assert.That(forward.y, Is.GreaterThan(0));
            Assert.That(Mathf.Asin(forward.y) * Mathf.Rad2Deg, Is.EqualTo(5f).Within(.001f));
        }
    }
}
