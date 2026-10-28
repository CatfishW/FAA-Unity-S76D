using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Headset
{
    /// <summary>Retains an observed mouse tap that XRI's once-per-frame pressed-state poll cannot see.</summary>
    [DefaultExecutionOrder(-201)]
    public sealed class XR3CollapsedMouseTapRepair : MonoBehaviour
    {
        // Pinned XRI 3.x has no public mouse-model access. All model changes below use its public transition setters.
        private static readonly FieldInfo PointerState = typeof(XRUIInputModule).GetField("m_PointerState", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type ModelType = PointerState?.FieldType;
        private static readonly PropertyInfo ModelId = ModelType?.GetProperty("pointerId");
        private static readonly PropertyInfo ModelLeft = ModelType?.GetProperty("leftButton");
        private static readonly PropertyInfo ModelPosition = ModelType?.GetProperty("position");
        private static readonly PropertyInfo ModelPressed = ModelType?.GetProperty("leftButtonPressed");
        private static readonly FieldInfo BuiltInActions = typeof(XRUIInputModule).GetField("m_UseBuiltInInputSystemActions", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo ButtonDelta = typeof(MouseButtonModel).GetProperty("lastFrameDelta", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly bool audit = Array.Exists(Environment.GetCommandLineArgs(), argument => argument == "--xr3-ui-audit");
        private const float PositionToleranceSquared = 4f;
        private const double MaximumTapSeconds = .5;
        private Mouse mouse;
        private Vector2 position, pressPosition, releasePosition;
        private bool observing, beganReleased, observedDown, moved;
        private int transitions;
        private double pressedAt, releasedAt;
        private PointerEventData probe;
        public int RecoveredTapCount { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindAnyObjectByType<XR3CollapsedMouseTapRepair>() != null) return;
            var host = new GameObject("FAA Collapsed Mouse Tap Repair"); DontDestroyOnLoad(host);
            host.AddComponent<XR3CollapsedMouseTapRepair>();
        }

        private void OnEnable()
        {
            if (observing) return;
            InputSystem.onBeforeUpdate += BeginInputUpdate; InputSystem.onEvent += ObserveMouseEvent;
            observing = true; BeginInputUpdate();
        }
        private void OnDisable()
        {
            if (observing) { InputSystem.onBeforeUpdate -= BeginInputUpdate; InputSystem.onEvent -= ObserveMouseEvent; }
            observing = false; ClearTap(); mouse = null;
        }
        private void OnApplicationFocus(bool focused) { if (!focused) ClearTap(); }
        private void BeginInputUpdate()
        {
            ClearTap(); mouse = Mouse.current;
            if (mouse == null) return;
            observedDown = mouse.leftButton.isPressed; beganReleased = !observedDown;
            position = mouse.position.ReadValue();
        }
        private void ClearTap() { transitions = 0; moved = false; pressedAt = releasedAt = 0; }

        private void ObserveMouseEvent(InputEventPtr input, InputDevice device)
        {
            if (device != mouse || mouse == null || mouse != Mouse.current ||
                !input.IsA<StateEvent>() && !input.IsA<DeltaStateEvent>()) return;
            if (input.handled) { moved = true; return; }
            if (mouse.position.ReadValueFromEvent(input, out Vector2 point)) position = point;
            if (!Finite(position)) { moved = true; return; }
            if (transitions > 0 && (position - pressPosition).sqrMagnitude > PositionToleranceSquared) moved = true;
            if (!mouse.leftButton.ReadValueFromEvent(input, out float value)) return;
            bool down = value >= mouse.leftButton.pressPointOrDefault;
            if (down == observedDown) return;
            observedDown = down; transitions++;
            if (down) { pressPosition = position; pressedAt = input.time; }
            else { releasePosition = position; releasedAt = input.time; }
        }

        private void Update()
        {
            var current = EventSystem.current;
            ProcessPendingTap(current != null ? current.currentInputModule as XRUIInputModule : null, Application.isFocused);
        }

        /// <summary>Only augments the desktop pointer model; XRI still performs its normal hit test and dispatch.</summary>
        public bool ProcessPendingTap(XRUIInputModule module, bool focused)
        {
            bool candidate = transitions == 2 && beganReleased && !observedDown && !moved &&
                releasedAt >= pressedAt && releasedAt - pressedAt <= MaximumTapSeconds;
            ClearTap(); // Consume once, including every rejected tap.
            if (!candidate || !focused || mouse == null || !mouse.added || mouse != Mouse.current ||
                mouse.leftButton.isPressed ||
                !Finite(mouse.position.ReadValue()) || (mouse.position.ReadValue() - releasePosition).sqrMagnitude > PositionToleranceSquared ||
                module == null || !module.isActiveAndEnabled || !module.enableMouseInput) return false;
            if (PointerState == null || ModelId == null || ModelLeft == null || ModelPosition == null || ModelPressed == null || BuiltInActions == null || ButtonDelta == null) return false;
            if (!(bool)BuiltInActions.GetValue(module)) return false; // Custom action mappings retain their original semantics.
            object model = PointerState.GetValue(module);
            var leftButton = (MouseButtonModel)ModelLeft.GetValue(model);
            if (Convert.ToInt32(ModelId.GetValue(model)) != 0 || leftButton.isDown || Convert.ToInt32(ButtonDelta.GetValue(leftButton)) != 0) return false;
            if (probe == null) probe = new PointerEventData(EventSystem.current);
            leftButton.CopyTo(probe);
            if (probe.dragging || probe.pointerPress != null || probe.pointerDrag != null) return false;
            ModelPosition.SetValue(model, mouse.position.ReadValue());
            ModelPressed.SetValue(model, true); ModelPressed.SetValue(model, false);
            PointerState.SetValue(module, model);
            RecoveredTapCount++;
            if (audit) Debug.Log("[FAA UI TAP] Retained observed stationary desktop tap at " + releasePosition + "; total=" + RecoveredTapCount);
            return true;
        }

        private static bool Finite(Vector2 point) => !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
            !float.IsNaN(point.y) && !float.IsInfinity(point.y);
    }
}
