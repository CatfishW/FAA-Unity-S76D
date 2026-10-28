using System;
using System.Collections.Generic;
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
    /// <summary>Opt-in observation of the actual final UI event path. Never dispatches or changes input.</summary>
    public sealed class XR3UiInputAudit : MonoBehaviour
    {
        private bool requested;
        private UIInputModule module;
        private readonly Vector3[] corners = new Vector3[4];
        private readonly Button[] nearest = new Button[3];
        private readonly Rect[] nearestBounds = new Rect[3];
        private readonly float[] nearestDistance = new float[3];
        public UIInputModule BoundModule => module;

        public static bool RequestsAudit(string[] arguments) => arguments != null && Array.Exists(arguments,
            argument => string.Equals(argument, "--xr3-ui-audit", StringComparison.OrdinalIgnoreCase));

        public static XR3UiInputAudit CreateIfRequested(string[] arguments)
        {
            if (!RequestsAudit(arguments)) return null;
            var host = new GameObject("FAA UI Input Audit");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            var audit = host.AddComponent<XR3UiInputAudit>(); audit.requested = true; audit.RefreshSubscription();
            return audit;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap() => CreateIfRequested(Environment.GetCommandLineArgs());
        private void OnEnable() { if (requested) RefreshSubscription(); }
        private void Update() { if (requested) RefreshSubscription(); }
        private void RefreshSubscription() => BindModule(EventSystem.current != null ? EventSystem.current.currentInputModule as UIInputModule : null);

        public void BindModule(UIInputModule next)
        {
            if (!requested || !isActiveAndEnabled) next = null;
            if (ReferenceEquals(module, next)) return;
            if (!ReferenceEquals(module, null))
            {
                module.pointerDown -= OnDown; module.pointerUp -= OnUp; module.pointerClick -= OnClick;
                module.finalizeRaycastResults -= OnFinalRaycast;
            }
            module = next;
            if (module != null)
            {
                module.pointerDown += OnDown; module.pointerUp += OnUp; module.pointerClick += OnClick;
                module.finalizeRaycastResults += OnFinalRaycast;
            }
            if (requested) Debug.Log("[FAA UI FINAL] " + DateTime.UtcNow.ToString("O") + " phase=module-bind module=" +
                (module != null ? module.GetType().Name + ":" + Path(module.gameObject) : "null"));
        }
        private void OnDisable() => BindModule(null);
        private void OnDestroy() => BindModule(null);
        private void OnDown(GameObject target, PointerEventData data) => LogEvent("down", target, data, data.pointerCurrentRaycast);
        private void OnUp(GameObject target, PointerEventData data) => LogEvent("up", target, data, data.pointerCurrentRaycast);
        private void OnClick(GameObject target, PointerEventData data) => LogEvent("click", target, data, data.pointerCurrentRaycast);
        private void OnFinalRaycast(PointerEventData data, List<RaycastResult> results)
        {
#if ENABLE_INPUT_SYSTEM
            if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame && !Mouse.current.leftButton.wasReleasedThisFrame) return;
#else
            if (!Input.GetMouseButtonDown(0) && !Input.GetMouseButtonUp(0)) return;
#endif
            RaycastResult winner = default;
            for (int i = 0; i < results.Count; i++) if (results[i].gameObject != null) { winner = results[i]; break; }
            LogEvent("final-raycast", winner.gameObject, data, winner);
        }

        private void LogEvent(string phase, GameObject target, PointerEventData data, RaycastResult winner)
        {
            if (!requested || data == null) return;
#if ENABLE_INPUT_SYSTEM
            bool down = Mouse.current != null && Mouse.current.leftButton.isPressed;
            bool pressed = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
            bool released = Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame;
#else
            bool down = Input.GetMouseButton(0), pressed = Input.GetMouseButtonDown(0), released = Input.GetMouseButtonUp(0);
#endif
            GameObject hit = winner.gameObject;
            string moduleName = winner.module != null ? winner.module.GetType().Name + ":" + Path(winner.module.gameObject) : "null";
            Debug.Log("[FAA UI FINAL] " + DateTime.UtcNow.ToString("O") + " phase=" + phase + " focused=" + Application.isFocused +
                " type=" + data.GetType().Name + " id=" + data.pointerId + " position=" + data.position +
                " rawMouse=" + down + "/" + pressed + "/" + released + " callbackTarget=" + Path(target) +
                " winner=" + Path(hit) + " module=" + moduleName + " order=" + winner.sortingOrder + " depth=" + winner.depth +
                " pressPosition=" + data.pressPosition + " eligible=" + data.eligibleForClick + " dragging=" + data.dragging +
                " pressed=" + Path(data.pointerPress) + " downHandler=" + Path(ExecuteEvents.GetEventHandler<IPointerDownHandler>(hit)) +
                " upHandler=" + Path(ExecuteEvents.GetEventHandler<IPointerUpHandler>(hit)) +
                " clickHandler=" + Path(ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit)) +
                " nearest=" + NearestButtons(data.position));
        }

        private string NearestButtons(Vector2 point)
        {
            Camera desktop = XR3DesktopHudPreview.OwnedDesktopCameraForAudit, head = Camera.main;
            if (desktop == null || head == null) return "no-owned-desktop";
            for (int i = 0; i < 3; i++) { nearest[i] = null; nearestDistance[i] = float.PositiveInfinity; }
            foreach (Button button in FindObjectsByType<Button>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                Graphic graphic = button.targetGraphic; Canvas canvas = graphic != null ? graphic.canvas : null;
                if (graphic == null || !graphic.isActiveAndEnabled || canvas == null ||
                    !canvas.rootCanvas.transform.IsChildOf(head.transform)) continue;
                float alpha = graphic.color.a;
                foreach (CanvasGroup group in button.GetComponentsInParent<CanvasGroup>(true))
                { if (!group.isActiveAndEnabled) continue; alpha *= group.alpha; if (group.ignoreParentGroups) break; }
                if (alpha < .05f || !ProjectBounds((RectTransform)button.transform, desktop, out Rect bounds)) continue;
                Vector2 closest = new Vector2(Mathf.Clamp(point.x, bounds.xMin, bounds.xMax), Mathf.Clamp(point.y, bounds.yMin, bounds.yMax));
                float distance = (point - closest).sqrMagnitude;
                for (int i = 0; i < 3; i++)
                    if (distance < nearestDistance[i])
                    {
                        for (int j = 2; j > i; j--) { nearest[j] = nearest[j - 1]; nearestBounds[j] = nearestBounds[j - 1]; nearestDistance[j] = nearestDistance[j - 1]; }
                        nearest[i] = button; nearestBounds[i] = bounds; nearestDistance[i] = distance; break;
                    }
            }
            var text = new StringBuilder();
            for (int i = 0; i < 3 && nearest[i] != null; i++)
            {
                if (i > 0) text.Append(" | ");
                Button button = nearest[i];
                text.Append(Path(button.gameObject)).Append(" bounds=").Append(nearestBounds[i].ToString("F2"))
                    .Append(" contains=").Append(RectTransformUtility.RectangleContainsScreenPoint((RectTransform)button.transform, point, desktop))
                    .Append(" enabled=").Append(button.enabled).Append(" interactable=").Append(button.IsInteractable())
                    .Append(" raycast=").Append(button.targetGraphic.raycastTarget).Append(" depth=").Append(button.targetGraphic.depth)
                    .Append(" culled=").Append(button.targetGraphic.canvasRenderer.cull);
            }
            return text.ToString();
        }

        private bool ProjectBounds(RectTransform rect, Camera camera, out Rect bounds)
        {
            rect.GetWorldCorners(corners); Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity), max = -min;
            foreach (Vector3 corner in corners)
            {
                Vector3 projected = camera.WorldToScreenPoint(corner);
                if (projected.z <= 0 || float.IsNaN(projected.x) || float.IsInfinity(projected.x) || float.IsNaN(projected.y) || float.IsInfinity(projected.y)) { bounds = default; return false; }
                min = Vector2.Min(min, projected); max = Vector2.Max(max, projected);
            }
            bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y); return true;
        }
        private static string Path(GameObject value)
        {
            if (value == null) return "null";
            string path = value.name;
            for (Transform parent = value.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }
    }
}
