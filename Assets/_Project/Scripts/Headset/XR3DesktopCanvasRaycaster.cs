using System.Collections.Generic;
using System;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

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
        private static readonly bool auditEnabled = Array.Exists(Environment.GetCommandLineArgs(),
            argument => string.Equals(argument, "--xr3-ui-audit", StringComparison.OrdinalIgnoreCase));

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
            ? XR3DesktopHudPreview.SynchronizePointerCamera(desktop) : original != null ? original.eventCamera : base.eventCamera;
        private Canvas Target => target != null ? target : target = GetComponent<Canvas>();

        public override void Raycast(PointerEventData data, List<RaycastResult> results)
        {
            if (data == null || Target == null || !Target.isActiveAndEnabled) return;
            int firstResult = results.Count;
            // Workspace hands reserve -2200 and below; XRUI supplies its own tracked event type.
            // Their positions belong to the headset camera, not the desktop window.
            if (desktop == null || Target.renderMode == RenderMode.ScreenSpaceOverlay ||
                data is TrackedDeviceEventData || data.pointerId <= -2000)
            {
                if (original != null) original.Raycast(data, results);
                AuditMousePress(data, results, firstResult, desktop == null ? "original:no-desktop" :
                    Target.renderMode == RenderMode.ScreenSpaceOverlay ? "original:overlay" : "original:tracked");
                return;
            }
            XR3DesktopHudPreview.SynchronizePointerCamera(desktop);
            base.Raycast(data, results);
            AuditMousePress(data, results, firstResult, "desktop");
        }

        private void AuditMousePress(PointerEventData data, List<RaycastResult> results, int firstResult, string branch)
        {
            if (!auditEnabled) return;
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame) return;
#else
            if (!Input.GetMouseButtonDown(0)) return;
#endif
            Camera source = original != null ? original.eventCamera : null;
            float distance = source != null && desktop != null ? Vector3.Distance(source.transform.position, desktop.transform.position) : -1f;
            float angle = source != null && desktop != null ? Quaternion.Angle(source.transform.rotation, desktop.transform.rotation) : -1f;
            var hits = new StringBuilder();
            for (int i = firstResult; i < results.Count && i < firstResult + 6; i++)
            {
                var hit = results[i];
                if (hits.Length > 0) hits.Append(" | ");
                hits.Append(hit.gameObject != null ? HierarchyPath(hit.gameObject.transform) : "null")
                    .Append(" order=").Append(hit.sortingOrder).Append(" depth=").Append(hit.depth);
            }
            Debug.Log("[FAA UI AUDIT] " + DateTime.UtcNow.ToString("O") + " canvas=" + HierarchyPath(Target.transform) +
                " dataType=" + data.GetType().Name + " pointerId=" + data.pointerId + " position=" + data.position +
                " screen=" + Screen.width + "x" + Screen.height + " branch=" + branch +
                " mode=" + Target.renderMode + " rootMode=" + Target.rootCanvas.renderMode + " root=" + Target.isRootCanvas +
                " order=" + Target.sortingOrder + " original=" + (source != null ? source.name : "null") +
                " desktop=" + (desktop != null ? desktop.name : "null") + " poseDistance=" + distance + " poseAngle=" + angle +
                " appended=" + (results.Count - firstResult) + " hits=" + hits);
        }

        private static string HierarchyPath(Transform value)
        {
            string path = value.name;
            for (Transform parent = value.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        public void Restore()
        {
            if (original != null && restoreOriginal) original.enabled = true;
            original = null; desktop = null; restoreOriginal = false;
        }
        protected override void OnDisable() { Restore(); base.OnDisable(); }
    }
}
