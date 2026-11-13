using System;
using System.Reflection;
using AircraftControl.Camera;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AircraftControl.Tests.Editor
{
    public sealed class AircraftViewRenderSpaceTests
    {
        private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
        public sealed class Fixture : IDisposable
        {
            public readonly GameObject Stage = new GameObject("Render-space original stage");
            public readonly GameObject Aircraft = new GameObject("Unchanged data-world aircraft");
            public readonly GameObject Head = new GameObject("Tracked render-space camera", typeof(UnityEngine.Camera));
            public readonly AircraftCameraController Rig;
            public readonly AircraftNativeXrOrigin Origin;
            public Fixture()
            {
                Aircraft.transform.SetPositionAndRotation(new Vector3(12466.1f, 318.2f, -4424.2f), Quaternion.Euler(-3f, 57f, 8f));
                Head.transform.SetParent(Stage.transform, false);
                Head.transform.SetLocalPositionAndRotation(new Vector3(.17f, 1.62f, -.23f), Quaternion.Euler(-5f, 21f, 2f));
                Head.AddComponent(Type.GetType("UnityEngine.InputSystem.XR.TrackedPoseDriver, Unity.InputSystem", true));
                Rig = Head.AddComponent<AircraftCameraController>();
                // Non-ExecuteAlways lifecycle callbacks are not dispatched by EditMode fixtures.
                AircraftViewRenderSpace.Bind(Head.GetComponent<UnityEngine.Camera>(), Rig);
                typeof(AircraftCameraController).GetField("aircraftTransform", Hidden).SetValue(Rig, Aircraft.transform);
                Origin = (AircraftNativeXrOrigin)typeof(AircraftCameraController).GetField("_nativeXrOrigin", Hidden).GetValue(Rig);
                Origin.Follow(Head.transform, Aircraft.transform.position, Aircraft.transform.rotation, new Vector3(0, 1.5f, 2f), true, true, true);
            }
            public void Refresh() => typeof(AircraftCameraController).GetMethod("UpdateNativeXrOrigin", Hidden).Invoke(Rig, null);
            public void Dispose()
            {
                Rig.SetNearRenderSpaceRequested(false); Origin.Release();
                Object.DestroyImmediate(Head); Object.DestroyImmediate(Stage); Object.DestroyImmediate(Aircraft);
            }
        }

        [Test]
        public void NearOriginPreservesTrackedLocalPoseProjectionBodyAndCalibratedSeatThroughMovementAndRestore()
        {
            using (var f = new Fixture())
            {
                var camera = f.Head.GetComponent<UnityEngine.Camera>();
                Matrix4x4 projection = Matrix4x4.Frustum(-.09f, .12f, -.08f, .1f, .1f, 2500f); camera.projectionMatrix = projection;
                Vector3 local = f.Head.transform.localPosition; Quaternion rotation = f.Head.transform.localRotation;
                int calibration = f.Origin.CalibrationVersion;
                f.Rig.SetNearRenderSpaceRequested(true);
                var space = AircraftViewRenderSpace.ForCamera(camera);
                Assert.That(space.IsActive, Is.True); Assert.That(f.Head.transform.position.magnitude, Is.LessThan(5f));
                for (int n = 0; n < 30; n++)
                {
                    f.Aircraft.transform.SetPositionAndRotation(new Vector3(12466f + n * 29f, 318f, -4424f + n * 17f), Quaternion.Euler(-2, 57 + n, 8));
                    f.Refresh();
                    Assert.That(f.Head.transform.localPosition, Is.EqualTo(local));
                    Assert.That(f.Head.transform.localRotation, Is.EqualTo(rotation));
                    Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
                    Assert.That(f.Origin.CalibrationVersion, Is.EqualTo(calibration));
                    Assert.That(f.Origin.SeatCalibrated, Is.True);
                    Assert.That(Quaternion.Angle(f.Rig.AircraftReferenceRotation, f.Aircraft.transform.rotation), Is.LessThan(.05f));
                    Assert.That(f.Head.transform.position.magnitude, Is.LessThan(5f));
                }
                Vector3 dataEye = space.RenderToDataPoint(f.Head.transform.position);
                f.Rig.SetNearRenderSpaceRequested(false);
                Assert.That(space.IsActive, Is.False); Assert.That(space.Offset, Is.EqualTo(Vector3.zero));
                Assert.That(Vector3.Distance(f.Head.transform.position, dataEye), Is.LessThan(.003f));
                Assert.That(f.Head.transform.localPosition, Is.EqualTo(local)); Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
                Assert.That(f.Rig.RecenterNativeXrSeat(), Is.False, "No tracked/present test device is claimed.");
                Assert.That(f.Origin.CalibrationVersion, Is.EqualTo(calibration));
            }
        }

        [Test]
        public void PointAndRayConversionsPreserveDataWorldGeometryAndUnrelatedViews()
        {
            using (var f = new Fixture())
            {
                f.Rig.SetNearRenderSpaceRequested(true);
                var camera = f.Head.GetComponent<UnityEngine.Camera>(); var space = AircraftViewRenderSpace.ForCamera(camera);
                var other = new GameObject("Unrelated desktop view", typeof(UnityEngine.Camera));
                try
                {
                    Vector3 data = f.Aircraft.transform.position + new Vector3(400, 30, 900);
                    Vector3 render = space.DataToRenderPoint(data);
                    Assert.That(Vector3.Distance(space.RenderToDataPoint(render), data), Is.LessThan(.001f));
                    Ray ray = new Ray(f.Head.transform.position, f.Head.transform.forward);
                    Ray logical = space.RenderToDataRay(ray);
                    Assert.That(logical.direction, Is.EqualTo(ray.direction));
                    Assert.That(logical.origin, Is.EqualTo(space.RenderToDataPoint(ray.origin)));
                    Assert.That(AircraftViewRenderSpace.DataToRenderPoint(other.GetComponent<UnityEngine.Camera>(), data), Is.EqualTo(data));
                    Assert.That(AircraftViewRenderSpace.ReferenceToDataPoint(f.Aircraft.transform, data), Is.EqualTo(data));
                    Assert.That(AircraftViewRenderSpace.ReferenceToDataPoint(f.Head.transform, ray.origin), Is.EqualTo(logical.origin));
                }
                finally { Object.DestroyImmediate(other); }
            }
        }

        [Test]
        public void DataTransformMutationCannotPublishANewerOffsetWithAnOlderRenderEye()
        {
            using (var f = new Fixture())
            {
                f.Rig.SetNearRenderSpaceRequested(true);
                var space = AircraftViewRenderSpace.ForCamera(f.Head.GetComponent<UnityEngine.Camera>());
                Vector3 oldOffset = space.Offset, oldEye = space.RenderToDataPoint(f.Head.transform.position);
                Vector3 delta = new Vector3(7000f, 11f, -2300f);
                f.Aircraft.transform.position += delta;
                Assert.That(space.Offset, Is.EqualTo(oldOffset), "The applied pose owns its offset until the next Follow.");
                Assert.That(space.RenderToDataPoint(f.Head.transform.position), Is.EqualTo(oldEye));
                f.Refresh();
                Assert.That(space.Offset, Is.EqualTo(f.Aircraft.transform.position));
                Assert.That(Vector3.Distance(space.RenderToDataPoint(f.Head.transform.position), oldEye + delta), Is.LessThan(.003f));
            }
        }

        [Test]
        public void ExplicitSpectatorSharesOneOwnerAndDisableCannotRetainAnOffset()
        {
            using (var f = new Fixture())
            {
                var host = new GameObject("Explicit spectator", typeof(UnityEngine.Camera));
                try
                {
                    var preview = host.GetComponent<UnityEngine.Camera>();
                    var space = AircraftViewRenderSpace.Bind(preview, f.Rig);
                    f.Rig.SetNearRenderSpaceRequested(true);
                    Assert.That(space.Offset, Is.EqualTo(f.Aircraft.transform.position));
                    f.Rig.enabled = false;
                    typeof(AircraftCameraController).GetMethod("OnDisable", Hidden).Invoke(f.Rig, null);
                    Assert.That(space.IsActive, Is.False); Assert.That(space.Offset, Is.EqualTo(Vector3.zero));
                    f.Rig.enabled = true;
                    Assert.That(space.IsActive, Is.False, "Disable releases the MR request; re-enable cannot silently revive it.");
                    AircraftViewRenderSpace.Bind(preview, null);
                    Assert.That(space.Owner, Is.Null); Assert.That(space.DataToRenderPoint(Vector3.one), Is.EqualTo(Vector3.one));
                }
                finally { Object.DestroyImmediate(host); }
            }
        }
    }
}
