using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Customization.Tests
{
    public sealed class XR3CollapsedMouseTapTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type RepairType => Type.GetType("FAA.Headset.XR3CollapsedMouseTapRepair, Assembly-CSharp", true);
        private readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
        private Mouse mouse, previousMouse;
        private InputSettings.UpdateMode previousMode;
        private XRUIInputModule module;
        private Component repair;
        private Vector2 point;
        private int clicks;
        private GameObject button;
        private static readonly MethodInfo ProcessModule = typeof(XRUIInputModule).GetMethod("DoProcess", Any);
        private bool Repair(bool focused = true) => (bool)RepairType.GetMethod("ProcessPendingTap").Invoke(repair, new object[] { module, focused });
        private int Recovered => (int)RepairType.GetProperty("RecoveredTapCount").GetValue(repair);
        private GameObject Host(string name, params Type[] types) { var host = new GameObject(name, types); owned.Add(host); return host; }
        private void Process() => ProcessModule.Invoke(module, null);
        private void Queue(bool pressed, Vector2 position)
        {
            var state = new MouseState { position = position };
            if (pressed) state = state.WithButton(MouseButton.Left);
            InputSystem.QueueStateEvent(mouse, state);
        }
        private void Tap()
        {
            Queue(true, point); Queue(false, point); InputSystem.Update();
            Assert.That(mouse.leftButton.isPressed, Is.False);
        }

        [SetUp]
        public void CreateRealXriButtonPipeline()
        {
            clicks = 0; previousMouse = Mouse.current; previousMode = InputSystem.settings.updateMode;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            mouse = InputSystem.AddDevice<Mouse>(); mouse.MakeCurrent();
            var events = Host("Tap events", typeof(EventSystem)); module = events.AddComponent<XRUIInputModule>();
            typeof(XRUIInputModule).GetMethod("OnEnable", Any).Invoke(module, null);
            module.enableXRInput = false; module.enableTouchInput = false; module.enableGamepadInput = false; module.enableJoystickInput = false;
            module.enableMouseInput = true;
            var cameraHost = Host("Tap test camera", typeof(Camera)); Camera camera = cameraHost.GetComponent<Camera>();
            var texture = new RenderTexture(1280, 720, 0); owned.Add(texture); camera.targetTexture = texture; camera.aspect = 1280f / 720f;
            var canvasHost = Host("Tap canvas", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            Canvas canvas = canvasHost.GetComponent<Canvas>(); canvas.renderMode = RenderMode.WorldSpace; canvas.worldCamera = camera;
            ((RectTransform)canvas.transform).sizeDelta = new Vector2(1280, 720); canvas.transform.position = Vector3.forward * 1.5f;
            canvas.transform.localScale = Vector3.one * .001f;
            typeof(GraphicRaycaster).GetMethod("OnEnable", Any).Invoke(canvasHost.GetComponent<GraphicRaycaster>(), null);
            button = Host("Actual test button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
            button.transform.SetParent(canvasHost.transform, false); ((RectTransform)button.transform).sizeDelta = new Vector2(200, 80);
            button.GetComponent<Button>().onClick.AddListener(() => clicks++);
            Canvas.ForceUpdateCanvases(); camera.Render(); point = camera.WorldToScreenPoint(button.transform.position);
            repair = Host("Tap repair").AddComponent(RepairType);
            RepairType.GetMethod("OnEnable", Any).Invoke(repair, null);
            Queue(false, point); InputSystem.Update(); Process();
            Assert.That(module.GetCurrentGameObject(0), Is.SameAs(button), "Establish actual normal XRI/uGUI hit testing first.");
        }

        [TearDown]
        public void Cleanup()
        {
            if (repair != null) RepairType.GetMethod("OnDisable", Any).Invoke(repair, null);
            if (module != null) typeof(XRUIInputModule).GetMethod("OnDisable", Any).Invoke(module, null);
            foreach (var item in owned) if (item != null) UnityEngine.Object.DestroyImmediate(item); owned.Clear();
            if (mouse != null && mouse.added) InputSystem.RemoveDevice(mouse);
            if (previousMouse != null && previousMouse.added) previousMouse.MakeCurrent();
            InputSystem.settings.updateMode = previousMode;
        }

        [Test]
        public void QueuedSameUpdateTapIsLostByOriginalPollButClicksTheRealButtonOnceAfterRepair()
        {
            Tap(); Process(); Assert.That(clicks, Is.Zero, "Original once-per-frame polling loses the complete tap.");
            Tap(); Assert.That(Repair(), Is.True); Assert.That(mouse.leftButton.isPressed, Is.False, "Never mutate device state.");
            Process(); Assert.That(clicks, Is.EqualTo(1)); Assert.That(Recovered, Is.EqualTo(1));
        }

        [Test]
        public void HeldPressAndReleaseUseTheUnchangedNormalPipeline()
        {
            Queue(true, point); InputSystem.Update(); Assert.That(Repair(), Is.False); Process(); Assert.That(clicks, Is.Zero);
            Queue(false, point); InputSystem.Update(); Assert.That(Repair(), Is.False); Process();
            Assert.That(clicks, Is.EqualTo(1)); Assert.That(Recovered, Is.Zero);
        }

        [Test]
        public void NormalHeldDragAndCollapsedFastDragAreNotReinterpretedAsTaps()
        {
            Vector2 away = point + Vector2.right * 250;
            Queue(true, point); InputSystem.Update(); Assert.That(Repair(), Is.False); Process();
            Queue(true, away); InputSystem.Update(); Assert.That(Repair(), Is.False); Process();
            Queue(false, away); InputSystem.Update(); Assert.That(Repair(), Is.False); Process();
            Queue(true, point); Queue(true, away); Queue(false, point); InputSystem.Update();
            Assert.That(Repair(), Is.False); Process(); Assert.That(clicks, Is.Zero); Assert.That(Recovered, Is.Zero);
        }

        [Test]
        public void NoInputAndRepeatedProcessingNeverReplayARecoveredClick()
        {
            Assert.That(Repair(), Is.False); Process(); Assert.That(clicks, Is.Zero);
            for (int i = 1; i <= 3; i++)
            {
                Tap(); Assert.That(Repair(), Is.True); Assert.That(Repair(), Is.False); Process(); Process();
                Assert.That(clicks, Is.EqualTo(i));
            }
            InputSystem.Update(); Assert.That(Repair(), Is.False); Process(); Assert.That(clicks, Is.EqualTo(3));
        }

        [Test]
        public void BurstFocusLossAndDisabledMouseAreRejectedWithoutDeferredReplay()
        {
            Queue(true, point); Queue(false, point); Queue(true, point); Queue(false, point); InputSystem.Update();
            Assert.That(Repair(), Is.False); Process();
            Tap(); Assert.That(Repair(false), Is.False); Assert.That(Repair(), Is.False); Process();
            module.enableMouseInput = false; Tap(); Assert.That(Repair(), Is.False); Process();
            Assert.That(clicks, Is.Zero); Assert.That(Recovered, Is.Zero);
        }

        [Test]
        public void CustomActionModeRemainsUntouched()
        {
            typeof(XRUIInputModule).GetField("m_UseBuiltInInputSystemActions", Any).SetValue(module, false);
            Tap(); Assert.That(Repair(), Is.False); Process();
            Assert.That(clicks, Is.Zero); Assert.That(Recovered, Is.Zero);
        }

        [Test]
        public void EventsAlreadyConsumedByAnotherInputListenerAreNotRecovered()
        {
            Action<InputEventPtr, InputDevice> consume = (input, device) => { if (device == mouse) input.handled = true; };
            RepairType.GetMethod("OnDisable", Any).Invoke(repair, null);
            InputSystem.onEvent += consume;
            RepairType.GetMethod("OnEnable", Any).Invoke(repair, null);
            try
            {
                Tap(); Assert.That(Repair(), Is.False); Process();
                Assert.That(clicks, Is.Zero); Assert.That(Recovered, Is.Zero);
            }
            finally { InputSystem.onEvent -= consume; }
        }

        private sealed class PassiveTrackedInteractor : IUIInteractor
        {
            public void UpdateUIModel(ref TrackedDeviceModel model) { }
            public bool TryGetUIModel(out TrackedDeviceModel model) { model = default; return false; }
        }
        [Test]
        public void RepairLeavesRegisteredTrackedDeviceModelsAndOtherMouseButtonsUntouched()
        {
            var interactor = new PassiveTrackedInteractor(); module.RegisterInteractor(interactor);
            Assert.That(module.GetTrackedDeviceModel(interactor, out var before), Is.True);
            var stateField = typeof(XRUIInputModule).GetField("m_PointerState", Any);
            object original = stateField.GetValue(module);
            stateField.FieldType.GetProperty("rightButtonPressed").SetValue(original, true); stateField.SetValue(module, original);
            Tap(); Assert.That(Repair(), Is.True);
            object after = stateField.GetValue(module);
            Assert.That(((MouseButtonModel)stateField.FieldType.GetProperty("rightButton").GetValue(after)).isDown, Is.True);
            Assert.That(module.GetTrackedDeviceModel(interactor, out var tracked), Is.True);
            Assert.That(tracked.pointerId, Is.EqualTo(before.pointerId)); Assert.That(tracked.position, Is.EqualTo(before.position));
            Assert.That(tracked.orientation, Is.EqualTo(before.orientation)); Assert.That(tracked.raycastPoints, Is.SameAs(before.raycastPoints));
        }
    }
}
