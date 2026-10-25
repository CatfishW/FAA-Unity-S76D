using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Headset
{
    /// <summary>Desktop preview hit testing, retaining the original camera for tracked/hand pointers.</summary>
    [RequireComponent(typeof(Canvas))]
    public sealed class XR3DesktopCanvasRaycaster : GraphicRaycaster
    {
        private GraphicRaycaster original;
        private Camera desktop;
        private Canvas target;
        private bool restoreOriginal;

        public void Bind(GraphicRaycaster source, Camera desktopCamera)
        {
            if (original == null)
            {
                original = source; restoreOriginal = source != null && source.enabled;
                if (source != null)
                {
                    ignoreReversedGraphics = source.ignoreReversedGraphics;
                    blockingObjects = source.blockingObjects; blockingMask = source.blockingMask;
                }
            }
            desktop = desktopCamera;
            if (original != null) original.enabled = false;
        }

        public override Camera eventCamera => desktop != null && Target != null && Target.renderMode != RenderMode.ScreenSpaceOverlay
            ? desktop : original != null ? original.eventCamera : base.eventCamera;
        private Canvas Target => target != null ? target : target = GetComponent<Canvas>();

        public override void Raycast(PointerEventData data, List<RaycastResult> results)
        {
            if (data == null || Target == null || !Target.isActiveAndEnabled) return;
            // Workspace hands reserve -2200 and below; XRUI supplies its own tracked event type.
            // Their positions belong to the headset camera, not the desktop window.
            if (desktop == null || Target.renderMode == RenderMode.ScreenSpaceOverlay ||
                data is TrackedDeviceEventData || data.pointerId <= -2000)
            {
                if (original != null) original.Raycast(data, results);
                return;
            }
            base.Raycast(data, results);
        }

        public void Restore()
        {
            if (original != null && restoreOriginal) original.enabled = true;
            original = null; desktop = null; restoreOriginal = false;
        }
        protected override void OnDisable() { Restore(); base.OnDisable(); }
    }
}
