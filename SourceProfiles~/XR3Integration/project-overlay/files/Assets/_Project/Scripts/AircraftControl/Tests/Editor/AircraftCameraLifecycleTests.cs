using System.Collections;
using System.Reflection;
using AircraftControl.Camera;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace AircraftControl.Tests.Editor
{
    public sealed class AircraftCameraLifecycleTests
    {
        private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static AircraftNativeXrOrigin Origin(AircraftCameraController controller) =>
            (AircraftNativeXrOrigin)typeof(AircraftCameraController).GetField("_nativeXrOrigin", Instance).GetValue(controller);
        private static void Quit(AircraftCameraController controller) =>
            typeof(AircraftCameraController).GetMethod("OnApplicationQuit", Instance).Invoke(controller, null);

        [Test]
        public void AbandonIsIdempotentAndNeverRestoresOrDestroysTheTrackedHierarchy()
        {
            var stage = new GameObject("Tracked stage");
            var head = new GameObject("Head");
            head.transform.SetParent(stage.transform, false);
            var origin = new AircraftNativeXrOrigin();
            origin.Follow(head.transform, new Vector3(50, 60, 70), Quaternion.Euler(4, 30, 5), Vector3.zero);
            Transform hierarchy = origin.Origin;
            Vector3 localPosition = head.transform.localPosition;
            Quaternion localRotation = head.transform.localRotation;
            origin.Abandon(); origin.Abandon();
            Assert.That(origin.Origin, Is.Null);
            Assert.That(origin.SeatCalibrated, Is.False);
            Assert.That(origin.CalibrationVersion, Is.Zero);
            Assert.That(head.transform.parent, Is.EqualTo(hierarchy));
            Assert.That(head.transform.localPosition, Is.EqualTo(localPosition));
            Assert.That(head.transform.localRotation, Is.EqualTo(localRotation));
            Assert.That(hierarchy != null, Is.True, "Natural hierarchy destruction remains Unity's responsibility at quit.");
            Object.DestroyImmediate(stage);
        }

        [UnityTest]
        public IEnumerator ActiveComponentDisableRestoresParentPoseSiblingAndTrackedProjection()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var stage = new GameObject("Original tracking stage");
            var first = new GameObject("Earlier sibling"); first.transform.SetParent(stage.transform, false);
            var head = new GameObject("Main Camera", typeof(UnityEngine.Camera)); head.transform.SetParent(stage.transform, false);
            var last = new GameObject("Later sibling"); last.transform.SetParent(stage.transform, false);
            Vector3 position = new Vector3(.15f, 1.62f, -.2f), scale = new Vector3(.9f, 1.1f, 1f);
            Quaternion rotation = Quaternion.Euler(3, 17, -2);
            head.transform.SetLocalPositionAndRotation(position, rotation); head.transform.localScale = scale;
            var camera = head.GetComponent<UnityEngine.Camera>();
            Matrix4x4 projection = Matrix4x4.Frustum(-.09f, .12f, -.08f, .1f, .1f, 2500f); camera.projectionMatrix = projection;
            var controller = head.AddComponent<AircraftCameraController>();
            Origin(controller).Follow(head.transform, new Vector3(100, 200, 300), Quaternion.Euler(5, 30, -4), Vector3.zero);
            controller.enabled = false; // Actual component lifecycle, not a direct Release-only test.
            Assert.That(head.transform.parent, Is.EqualTo(stage.transform));
            Assert.That(head.transform.localPosition, Is.EqualTo(position));
            Assert.That(Quaternion.Angle(head.transform.localRotation, rotation), Is.LessThan(.001f));
            Assert.That(head.transform.localScale, Is.EqualTo(scale));
            Assert.That(head.transform.GetSiblingIndex(), Is.EqualTo(1));
            Assert.That(head.activeInHierarchy, Is.True);
            Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
            Object.Destroy(stage); yield return null;
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator ParentDeactivationAndReenableRetainHeadAndOriginWithoutReactivatingCamera()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var stage = new GameObject("Original tracking stage");
            var head = new GameObject("Main Camera", typeof(UnityEngine.Camera)); head.transform.SetParent(stage.transform, false);
            Vector3 position = new Vector3(.1f, 1.6f, -.2f); Quaternion rotation = Quaternion.Euler(4, 20, -2);
            head.transform.SetLocalPositionAndRotation(position, rotation);
            var camera = head.GetComponent<UnityEngine.Camera>();
            Matrix4x4 projection = Matrix4x4.Frustum(-.09f, .12f, -.08f, .1f, .1f, 2500f); camera.projectionMatrix = projection;
            var controller = head.AddComponent<AircraftCameraController>();
            var origin = Origin(controller);
            typeof(AircraftCameraController).GetField("aircraftTransform", Instance).SetValue(controller, stage.transform);
            origin.Follow(head.transform, new Vector3(100, 200, 300), Quaternion.Euler(5, 30, -4), Vector3.zero);
            controller.SetNearRenderSpaceRequested(true);
            Transform hierarchy = origin.Origin;
            hierarchy.gameObject.SetActive(false); // Actual ancestor-triggered OnDisable from the failing shutdown path.
            Assert.That(head.activeSelf, Is.True);
            Assert.That(head.activeInHierarchy, Is.False, "Disable must not move the head beneath its active former parent.");
            Assert.That(head.transform.parent, Is.EqualTo(hierarchy));
            Assert.That(origin.Origin, Is.EqualTo(hierarchy));
            hierarchy.gameObject.SetActive(true);
            Assert.That(head.activeInHierarchy, Is.True);
            Assert.That(controller.NearRenderSpaceActive, Is.True, "A hierarchy toggle must not clear the owner's MR render-space request.");
            Assert.That(head.transform.localPosition, Is.EqualTo(position));
            Assert.That(Quaternion.Angle(head.transform.localRotation, rotation), Is.LessThan(.001f));
            Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
            controller.enabled = false; // A later ordinary active disable still releases ownership normally.
            Assert.That(head.transform.parent, Is.EqualTo(stage.transform));
            Object.Destroy(stage); yield return null;
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SceneUnloadLetsUnityDestroyTheOriginAndHeadWithoutRestoringEither()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            Scene scene = SceneManager.CreateScene("Owned tracking hierarchy unload");
            var stage = new GameObject("Unloaded tracking stage");
            SceneManager.MoveGameObjectToScene(stage, scene);
            var head = new GameObject("Main Camera", typeof(UnityEngine.Camera)); head.transform.SetParent(stage.transform, false);
            var controller = head.AddComponent<AircraftCameraController>();
            var origin = Origin(controller);
            origin.Follow(head.transform, new Vector3(100, 200, 300), Quaternion.Euler(5, 30, -4), Vector3.zero);
            Transform hierarchy = origin.Origin;
            yield return SceneManager.UnloadSceneAsync(scene);
            Assert.That(head == null, Is.True);
            Assert.That(hierarchy == null, Is.True);
            Assert.That(origin.Origin, Is.Null);
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator QuitThenParentShutdownAbandonsOwnershipWithoutCameraMutationOrResurrection()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            var stage = new GameObject("Quit tracking stage");
            var head = new GameObject("Main Camera", typeof(UnityEngine.Camera)); head.transform.SetParent(stage.transform, false);
            var camera = head.GetComponent<UnityEngine.Camera>();
            Matrix4x4 projection = Matrix4x4.Frustum(-.09f, .12f, -.08f, .1f, .1f, 2500f); camera.projectionMatrix = projection;
            var controller = head.AddComponent<AircraftCameraController>();
            var origin = Origin(controller);
            origin.Follow(head.transform, new Vector3(100, 200, 300), Quaternion.Euler(5, 30, -4), Vector3.zero);
            Transform hierarchy = origin.Origin;
            Vector3 position = head.transform.localPosition; Quaternion rotation = head.transform.localRotation;
            Quit(controller); Quit(controller); // Deterministic quit ordering, followed by real Unity deactivation/destruction.
            hierarchy.gameObject.SetActive(false);
            controller.SetNearRenderSpaceRequested(true);
            Assert.That(origin.Origin, Is.Null);
            Assert.That(head.transform.parent, Is.EqualTo(hierarchy));
            Assert.That(head.transform.localPosition, Is.EqualTo(position));
            Assert.That(head.transform.localRotation, Is.EqualTo(rotation));
            Assert.That(head.activeInHierarchy, Is.False);
            Assert.That(camera.projectionMatrix, Is.EqualTo(projection));
            Object.Destroy(stage); yield return null;
            Assert.That(head == null, Is.True);
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }
    }
}
