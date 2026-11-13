using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Headset
{
    /// <summary>Standard Button dispatch with exact, callback-scoped pointer provenance.</summary>
    public sealed class XR3DesktopPointerInput : Button
    {
        private static PointerEventData dispatch;
        private static XR3DesktopPointerInput target;
        private static readonly FieldInfo builtInActions = typeof(XRUIInputModule).GetField(
            "m_UseBuiltInInputSystemActions", BindingFlags.Instance | BindingFlags.NonPublic);

        public static bool IsDesktopMouseDispatch => target != null && IsDesktopMousePointer(dispatch);

        public static bool IsDesktopMousePointer(PointerEventData data)
        {
            if (data == null || data.button != PointerEventData.InputButton.Left ||
                data is TrackedDeviceEventData || data.pointerId <= -2000) return false;
            if (data is ExtendedPointerEventData extended)
                return extended.device is Mouse && extended.pointerType == UnityEngine.InputSystem.UI.UIPointerType.MouseOrPen;
            // Unknown event subclasses and custom action mappings keep their native semantics.
            if (data.GetType() != typeof(PointerEventData)) return false;
            if (data.currentInputModule is StandaloneInputModule) return data.pointerId == -1;
            // XRI 3.6's built-in PointerModel(0) copies mouse button state; touch/tracked
            // models have separate IDs/types. The existing tap repair uses this same
            // guarded built-in-model contract. This does not infer a mouse from presence.
            var module = data.currentInputModule as XRUIInputModule;
            return module != null && module.isActiveAndEnabled && module.enableMouseInput &&
                data.pointerId == 0 && builtInActions != null && (bool)builtInActions.GetValue(module);
        }

        public override void OnPointerClick(PointerEventData data)
        {
            var previous = dispatch; var previousTarget = target;
            dispatch = data; target = this;
            try { base.OnPointerClick(data); }
            finally { dispatch = previous; target = previousTarget; }
        }

        public override void OnSubmit(BaseEventData data)
        {
            var previous = dispatch; var previousTarget = target;
            dispatch = null; target = null;
            try { base.OnSubmit(data); }
            finally { dispatch = previous; target = previousTarget; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDispatch() { dispatch = null; target = null; }
    }
}
