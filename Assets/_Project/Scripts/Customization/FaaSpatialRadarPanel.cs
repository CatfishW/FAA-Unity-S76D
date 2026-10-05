using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Moves the ORIGINAL radar canvas, including controls, status and drawers. No duplicated radar/telemetry.
    /// The original hierarchy is untouched; disabled/native->desktop restores the captured Canvas exactly.
    /// </summary>
    public sealed class FaaSpatialRadarPanel
    {
        public readonly string Id;
        public readonly Canvas Canvas;
        public readonly RectTransform Radar;
        public readonly FaaSpatialLayoutEntry Layout;
        public bool IsUtility { get; }
        private readonly float physicalWidth;
        private readonly FaaRadarGroupFootprint footprint = new();
        public Vector2 ProtectedPhysicalSize { get; private set; }
        public float PhysicalWidth => physicalWidth;
        public float PhysicalHeight => physicalWidth * Mathf.Max(Radar.rect.height, TopAnchorHeight) / Mathf.Max(1f,Radar.rect.width);
        /// <summary>
        /// Utility panels whose content height changes with state (Hand Studio) keep their TOP edge fixed in the cockpit: the panel
        /// is placed as if it were this tall (panel units) and shorter content shrinks from the bottom, so nothing under the
        /// pointer jumps. 0 = centre anchoring (the default). The protected footprint always uses the full height.
        /// </summary>
        public float TopAnchorHeight { get; set; }
        public FaaSpatialLayoutEntry DefaultLayout { get; private set; }
        public bool IsSpatial { get; private set; }
        public GameObject Handle => handle != null ? handle.gameObject : null;
        public Vector3 WorldCenter => Radar.TransformPoint(Radar.rect.center);

        private readonly FaaSpatialWorkspace owner;
        private readonly RectTransform canvasRect;
        private readonly UnityEngine.UI.CanvasScaler scaler;
        private readonly GraphicRaycaster raycaster;
        private TrackedDeviceGraphicRaycaster trackedRaycaster;
        private bool ownsTrackedRaycaster;
        private readonly Vector3[] corners = new Vector3[4];
        private readonly CanvasGroup[] visibility;
        private RectTransform handle;
        private CanvasState original;
        // Full-map sizing: units per metre stay constant (up to MaxFullMapGrowth) while the traffic display is maximised,
        // instead of squeezing an 800-unit map into the compact diameter (which made its text unreadably small).
        public const float MaxFullMapGrowth = 2f;
        private readonly TrafficRadar.TrafficRadarDisplay fullMapDisplay;
        private float compactWidth;
        // Focus de-emphasis while another panel is inspected: workspace-owned CanvasGroups only (never one another system writes).
        // Utility canvases are workspace-owned, so their canvas root carries the group. Radar canvases have other owners (traffic
        // focus fades the weather canvas root, presentation fades the display), so a radar uses groups on the highest descendants
        // that carry no foreign group, collected when needed and re-collected at most twice a second while de-emphasised.
        private readonly List<CanvasGroup> focusGroups = new();
        private readonly HashSet<CanvasGroup> ownedFocus = new();
        private int focusHierarchyCount = -1;
        private float nextFocusCollect;
        private bool focusLive = true;
        public float FocusAlpha { get; private set; } = 1f;
        /// <summary>Set by the workspace while another panel is inspected: this panel's projection meets a protected HUD area, the
        /// inspected panel or the screen edge, so it is hidden instead of merely de-emphasised.</summary>
        public bool NeighbourHidden { get; set; }
        private readonly List<Graphic> boundsGraphics = new();
        private readonly List<CanvasGroup> boundsGroups = new();
        private readonly List<TMP_Text> textScratch = new();

        private struct CanvasState
        {
            public RenderMode mode;
            public Camera camera;
            public Vector2 anchorMin, anchorMax, pivot, size, anchored;
            public Vector3 position, rotationEuler, scale;
            public float planeDistance;
            public bool scalerEnabled;
        }

        public FaaSpatialRadarPanel(FaaSpatialWorkspace workspace, string id, Canvas canvas, RectTransform radar, float yaw,
            bool utility = false, float widthMeters = .42f)
        {
            owner = workspace; Id = id; Canvas = canvas; Radar = radar;
            IsUtility=utility;physicalWidth=widthMeters;
            canvasRect = (RectTransform)canvas.transform;
            scaler = canvas.GetComponent<UnityEngine.UI.CanvasScaler>();
            raycaster = canvas.GetComponent<GraphicRaycaster>();
            visibility = radar.GetComponentsInParent<CanvasGroup>(true);
            if (!utility) fullMapDisplay = radar.GetComponentInChildren<TrafficRadar.TrafficRadarDisplay>(true);
            Layout = new FaaSpatialLayoutEntry { id = id,
                yaw = Mathf.Sign(yaw) * (utility ? FaaPeripheralPanelLayout.DefaultYaw : 95f),
                elevation = utility ? FaaPeripheralPanelLayout.DefaultElevation : -32f,
                distance = utility ? FaaPeripheralPanelLayout.DefaultDistance : 1.7f, scale = 1f };
            DefaultLayout = Layout.Copy();
            BuildHandle();
        }

        public void SetSpatial(bool value)
        {
            if (Canvas == null || Radar == null || IsSpatial == value) return;
            if (value)
            {
                original = new CanvasState
                {
                    mode = Canvas.renderMode, camera = Canvas.worldCamera, planeDistance = Canvas.planeDistance,
                    anchorMin = canvasRect.anchorMin, anchorMax = canvasRect.anchorMax, pivot = canvasRect.pivot,
                    size = canvasRect.sizeDelta, anchored = canvasRect.anchoredPosition,
                    position = canvasRect.localPosition, rotationEuler = canvasRect.localEulerAngles, scale = canvasRect.localScale,
                    scalerEnabled = scaler != null && scaler.enabled
                };
                trackedRaycaster = Canvas.GetComponent<TrackedDeviceGraphicRaycaster>();
                if (trackedRaycaster == null)
                {
                    trackedRaycaster = Canvas.gameObject.AddComponent<TrackedDeviceGraphicRaycaster>();
                    ownsTrackedRaycaster = true;
                }
            }
            else RestoreCanvas();
            IsSpatial = value;
        }

        private void RestoreCanvas()
        {
            if (Canvas == null) return;
            Canvas.renderMode = original.mode; Canvas.worldCamera = original.camera; Canvas.planeDistance = original.planeDistance;
            canvasRect.anchorMin = original.anchorMin; canvasRect.anchorMax = original.anchorMax;
            canvasRect.pivot = original.pivot; canvasRect.sizeDelta = original.size;
            canvasRect.anchoredPosition = original.anchored; canvasRect.localPosition = original.position;
            canvasRect.localEulerAngles = original.rotationEuler; canvasRect.localScale = original.scale;
            if (scaler != null) scaler.enabled = original.scalerEnabled;
            if (ownsTrackedRaycaster && trackedRaycaster != null) Object.Destroy(trackedRaycaster);
            ownsTrackedRaycaster = false;
        }

        public void Apply(Transform cockpit, Camera view, bool editing)
        {
            if (Canvas == null || Radar == null) return;
            ProtectedPhysicalSize = IsUtility ? new Vector2(physicalWidth, PhysicalHeight) : footprint.Measure(Canvas, Radar) * (2f * physicalWidth);
            // Utilities also stay inside a comfortable elevation band (no extreme look-down onto terrain when inspected).
            if (IsUtility) FaaPeripheralPanelLayout.ProtectUtility(Layout,ProtectedPhysicalSize.x,ProtectedPhysicalSize.y,DefaultLayout.yaw);
            else FaaPeripheralPanelLayout.Protect(Layout,ProtectedPhysicalSize.x,ProtectedPhysicalSize.y,DefaultLayout.yaw);
            if (handle != null)
            {
                handle.gameObject.SetActive(editing && Radar.gameObject.activeInHierarchy);
                handle.sizeDelta = new Vector2(IsUtility ? Radar.rect.width-32f : Mathf.Max(180f, Radar.rect.width), 30f);
                handle.SetAsLastSibling();
            }
            if (!IsSpatial || cockpit == null || view == null) return;
            if (scaler != null) scaler.enabled = false;
            if(Canvas.renderMode != RenderMode.WorldSpace)Canvas.renderMode = RenderMode.WorldSpace;
            if(Canvas.worldCamera != view)Canvas.worldCamera = view;
            var centreAnchor = new Vector2(.5f,.5f);
            if (canvasRect.anchorMin != centreAnchor) canvasRect.anchorMin = centreAnchor;
            if (canvasRect.anchorMax != centreAnchor) canvasRect.anchorMax = centreAnchor;
            if (canvasRect.pivot != centreAnchor) canvasRect.pivot = centreAnchor;
            if (canvasRect.sizeDelta != new Vector2(1920,1080)) canvasRect.sizeDelta = new Vector2(1920,1080);
            Vector3 local = FaaSpatialLayoutMath.Position(Layout);
            Vector3 position = cockpit.TransformPoint(local);
            Quaternion rotation = cockpit.rotation * Quaternion.LookRotation(local.normalized, Vector3.up);
            // Compact layout: constant physical diameter. Maximised full map: constant units per metre up to MaxFullMapGrowth,
            // which the reserved group footprint (measured from the compact layout with its drawers) already covers.
            float width = Mathf.Max(100f, Radar.rect.width * Mathf.Abs(Radar.localScale.x));
            bool fullMap = fullMapDisplay != null && fullMapDisplay.IsFullscreen;
            if (!fullMap || compactWidth <= 0f) compactWidth = width;
            float growth = fullMap ? Mathf.Clamp(width / compactWidth, 1f, MaxFullMapGrowth) : 1f;
            float metersPerUnit = physicalWidth * Layout.scale * growth / width;
            Vector3 parentScale = canvasRect.parent != null ? canvasRect.parent.lossyScale : Vector3.one;
            canvasRect.rotation = rotation;
            canvasRect.localScale = new Vector3(metersPerUnit / Mathf.Max(.0001f, Mathf.Abs(parentScale.x)),
                metersPerUnit / Mathf.Max(.0001f, Mathf.Abs(parentScale.y)), metersPerUnit / Mathf.Max(.0001f, Mathf.Abs(parentScale.z)));
            // Absolute positioning without subtracting large world-space coordinates each frame.
            Rect content = Radar.rect;
            Vector2 anchor = TopAnchorHeight > 0f ? new Vector2(content.center.x, content.yMax - TopAnchorHeight * .5f) : content.center;
            if (FaaCanvasLocalGeometry.TryMatrix(Radar,canvasRect,out var contentMatrix))
                canvasRect.position = position - canvasRect.TransformVector(contentMatrix.MultiplyPoint3x4(anchor));
        }

        public bool TryRay(Ray ray, Camera view, out float distance, out Vector3 hit, bool includeHeader = true)
        {
            hit = Vector3.zero; distance = 0f;
            if (Radar == null || !Radar.gameObject.activeInHierarchy || Canvas == null || !Canvas.isActiveAndEnabled) return false;
            // A de-emphasised or hidden neighbour (another panel is inspected) never takes pointer input.
            if (FocusAlpha < .5f) return false;
            foreach (var group in visibility)
                if (group != null && group.isActiveAndEnabled && group.alpha <= .001f) return false;
            if (IsSpatial)
            {
                var plane = new Plane(Radar.forward, WorldCenter);
                if (!plane.Raycast(ray, out distance) || distance <= 0f || distance > 10f) return false;
                hit = ray.GetPoint(distance);
                Vector3 local = Radar.InverseTransformPoint(hit);
                Rect bounds = Radar.rect;
                if (includeHeader) bounds = Rect.MinMaxRect(bounds.xMin, bounds.yMin - 25f, bounds.xMax, bounds.yMax + 105f);
                return bounds.Contains(new Vector2(local.x, local.y));
            }
            if (view == null) return false;
            Vector3 screen = view.WorldToScreenPoint(ray.GetPoint(2f));
            Camera uiCamera = Canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : Canvas.worldCamera;
            if (screen.z <= 0f || !RectTransformUtility.RectangleContainsScreenPoint(Radar, screen, uiCamera)) return false;
            distance = 2f; hit = ray.GetPoint(distance); return true;
        }

        /// <summary>
        /// Visible angular bounds in the cockpit frame (degrees; yaw unwrapped around the layout yaw). Utility: the panel rect (a
        /// top-anchored utility: its reserved tallest height).
        /// Radar: the scope plus its header envelope and any open drawer whose CanvasGroup chain is visible. Allocation-free after
        /// the first call; used only when an inspection starts.
        /// </summary>
        public bool TryVisibleAngularBounds(Transform cockpit, out Vector2 yawRange, out Vector2 elevationRange)
        {
            yawRange = elevationRange = default;
            if (cockpit == null || Radar == null || !Radar.gameObject.activeInHierarchy) return false;
            var b = new AngularBounds(cockpit, Layout.yaw);
            Rect r = Radar.rect;
            if (!IsUtility) r = Rect.MinMaxRect(r.xMin, r.yMin - 25f, r.xMax, r.yMax + 105f); // same header envelope as TryRay
            // A top-anchored utility reserves its tallest height, so rows appearing later never force the view to re-aim.
            else if (TopAnchorHeight > r.height) r = Rect.MinMaxRect(r.xMin, r.yMax - TopAnchorHeight, r.xMax, r.yMax);
            b.Add(Radar.TransformPoint(new Vector3(r.xMin, r.yMin, 0f))); b.Add(Radar.TransformPoint(new Vector3(r.xMin, r.yMax, 0f)));
            b.Add(Radar.TransformPoint(new Vector3(r.xMax, r.yMin, 0f))); b.Add(Radar.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
            if (!IsUtility && Canvas != null)
            {
                float limit = 4f * Mathf.Max(100f, Radar.rect.width) * Mathf.Abs(Radar.lossyScale.x);
                boundsGraphics.Clear(); Canvas.GetComponentsInChildren(false, boundsGraphics);
                foreach (var g in boundsGraphics)
                {
                    if (g == null || !g.isActiveAndEnabled || g.color.a <= .02f || g.transform.IsChildOf(Radar) || Masked(g.transform) || ChainAlpha(g.transform) <= .02f) continue;
                    g.rectTransform.GetWorldCorners(corners);
                    if (Vector3.Distance(corners[0], corners[2]) > limit) continue;
                    for (int i = 0; i < 4; i++) b.Add(corners[i]);
                }
                boundsGraphics.Clear();
            }
            if (!b.Any) return false;
            yawRange = new Vector2(b.YawMin, b.YawMax); elevationRange = new Vector2(b.ElevationMin, b.ElevationMax);
            return true;
        }
        /// <summary>
        /// Screen rectangle (pixels, bottom-left origin) of the panel and, for a radar, its header envelope, through
        /// <paramref name="view"/>. False when any corner is behind the camera. Allocation-free.
        /// </summary>
        public bool TryScreenRect(Camera view, out Rect rect)
        {
            rect = default;
            if (view == null || Radar == null || !Radar.gameObject.activeInHierarchy) return false;
            Rect r = Radar.rect;
            if (!IsUtility) r = Rect.MinMaxRect(r.xMin, r.yMin - 25f, r.xMax, r.yMax + 105f);
            float x0 = float.PositiveInfinity, y0 = float.PositiveInfinity, x1 = float.NegativeInfinity, y1 = float.NegativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                Vector3 local = new Vector3(i < 2 ? r.xMin : r.xMax, (i & 1) == 0 ? r.yMin : r.yMax, 0f);
                Vector3 screen = view.WorldToScreenPoint(Radar.TransformPoint(local));
                if (screen.z <= .01f || !FaaSpatialLayoutMath.Finite(screen)) return false;
                x0 = Mathf.Min(x0, screen.x); y0 = Mathf.Min(y0, screen.y); x1 = Mathf.Max(x1, screen.x); y1 = Mathf.Max(y1, screen.y);
            }
            rect = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        /// <summary>
        /// Angular height (degrees, seen from <paramref name="eye"/>) of the smallest visible text on the panel, or 0 when none is
        /// visible. TMP font size is the rendered (auto-sized) size. Used once when an inspection starts; no per-frame work.
        /// </summary>
        public float SmallestTextDegrees(Vector3 eye)
        {
            Transform root = IsUtility || Canvas == null ? (Transform)Radar : Canvas.transform;
            if (root == null) return 0f;
            float best = float.PositiveInfinity;
            textScratch.Clear(); root.GetComponentsInChildren(false, textScratch);
            foreach (var t in textScratch)
            {
                if (t == null || !t.isActiveAndEnabled || t.color.a < .1f || string.IsNullOrEmpty(t.text)) continue;
                if (handle != null && t.transform.IsChildOf(handle)) continue; // the layout grip shows only while editing
                if (!IsUtility && (Masked(t.transform) || ChainAlpha(t.transform) < .1f)) continue;
                float size = t.fontSize * Mathf.Abs(t.transform.lossyScale.y), distance = Vector3.Distance(eye, t.transform.position);
                if (size <= 0f || distance < .05f || !FaaSpatialLayoutMath.Finite(size)) continue;
                best = Mathf.Min(best, Mathf.Atan2(size, distance) * Mathf.Rad2Deg);
            }
            textScratch.Clear();
            return float.IsInfinity(best) ? 0f : best;
        }

        private struct AngularBounds
        {
            private readonly Transform cockpit; private readonly float reference;
            public float YawMin, YawMax, ElevationMin, ElevationMax; public bool Any;
            public AngularBounds(Transform cockpit, float reference)
            { this.cockpit = cockpit; this.reference = reference; YawMin = ElevationMin = float.PositiveInfinity; YawMax = ElevationMax = float.NegativeInfinity; Any = false; }
            public void Add(Vector3 world)
            {
                Vector3 p = cockpit.InverseTransformPoint(world);
                if (!FaaSpatialLayoutMath.Finite(p) || p.sqrMagnitude < 1e-6f) return;
                float yaw = reference + Mathf.DeltaAngle(reference, Mathf.Atan2(p.x, p.z) * Mathf.Rad2Deg);
                float elevation = Mathf.Atan2(p.y, new Vector2(p.x, p.z).magnitude) * Mathf.Rad2Deg;
                YawMin = Mathf.Min(YawMin, yaw); YawMax = Mathf.Max(YawMax, yaw);
                ElevationMin = Mathf.Min(ElevationMin, elevation); ElevationMax = Mathf.Max(ElevationMax, elevation); Any = true;
            }
        }
        private bool Masked(Transform t)
        {
            for (Transform a = t.parent; a != null && a != Canvas.transform; a = a.parent)
            {
                var m = a.GetComponent<RectMask2D>(); if (m != null && m.enabled) return true;
                var s = a.GetComponent<Mask>(); if (s != null && s.enabled) return true;
            }
            return false;
        }
        private float ChainAlpha(Transform t)
        {
            boundsGroups.Clear(); t.GetComponentsInParent(false, boundsGroups);
            float a = 1f;
            foreach (var g in boundsGroups) { if (g == null || !g.enabled) continue; a *= g.alpha; if (g.ignoreParentGroups) break; }
            boundsGroups.Clear(); return a;
        }

        /// <summary>
        /// Moves this panel's focus alpha toward <paramref name="target"/> (0..1) by at most <paramref name="maxDelta"/> through
        /// workspace-owned CanvasGroups (see the field notes), created only when first needed. A de-emphasised panel (below 0.5)
        /// is not interactable and does not block raycasts. Writes only when the value changes.
        /// </summary>
        public void StepFocus(float target, float maxDelta)
        {
            if (Canvas == null) return;
            float next = Mathf.MoveTowards(FocusAlpha, Mathf.Clamp01(target), Mathf.Max(0f, maxDelta));
            bool changed = !Mathf.Approximately(next, FocusAlpha);
            FocusAlpha = next;
            if (focusGroups.Count == 0 && next >= .999f) return; // nothing created until a panel is first de-emphasised
            bool collected = false;
            if ((focusGroups.Count == 0 || next < .999f) && Time.unscaledTime >= nextFocusCollect &&
                Canvas.transform.hierarchyCount != focusHierarchyCount)
            { CollectFocusGroups(); collected = true; }
            if (!changed && !collected) return;
            bool live = next >= .5f;
            foreach (var g in focusGroups)
            {
                if (g == null) continue;
                if (Mathf.Abs(g.alpha - next) > .001f) g.alpha = next;
                if (collected || live != focusLive) { g.interactable = live; g.blocksRaycasts = live; }
            }
            focusLive = live;
        }
        private void CollectFocusGroups()
        {
            nextFocusCollect = Time.unscaledTime + .5f;
            focusHierarchyCount = Canvas.transform.hierarchyCount;
            if (IsUtility) AddFocusTarget(Canvas.transform, 0);
            else for (int i = 0; i < Canvas.transform.childCount; i++) AddFocusTarget(Canvas.transform.GetChild(i), 1);
            for (int i = focusGroups.Count - 1; i >= 0; i--) if (focusGroups[i] == null) focusGroups.RemoveAt(i);
        }
        private void AddFocusTarget(Transform t, int depth)
        {
            if (t == null || handle != null && t == handle) return;
            var existing = t.GetComponent<CanvasGroup>();
            if (existing == null)
            {
                var g = t.gameObject.AddComponent<CanvasGroup>();
                g.alpha = FocusAlpha; ownedFocus.Add(g); focusGroups.Add(g); return;
            }
            if (ownedFocus.Contains(existing) || depth >= 3) return;
            for (int i = 0; i < t.childCount; i++) AddFocusTarget(t.GetChild(i), depth + 1);
        }

        public void Reset()
        {
            Layout.yaw = DefaultLayout.yaw; Layout.elevation = DefaultLayout.elevation;
            Layout.distance = DefaultLayout.distance; Layout.scale = DefaultLayout.scale;
        }

        private void BuildHandle()
        {
            var go = new GameObject("FAA Layout Grip - " + Id, typeof(RectTransform), typeof(Image), typeof(FaaWorkspaceDragHandle));
            handle = (RectTransform)go.transform; handle.SetParent(Radar, false);
            handle.anchorMin = handle.anchorMax = new Vector2(.5f, 1f);
            handle.anchoredPosition = new Vector2(0f, IsUtility ? -14f : 85f); handle.sizeDelta = new Vector2(Radar.rect.width, 30);
            go.GetComponent<Image>().color = new Color(.03f, .21f, .23f, .96f);
            go.GetComponent<FaaWorkspaceDragHandle>().Configure(owner, Id, false);
            var labelGo = new GameObject("Grip Caption", typeof(RectTransform)); labelGo.transform.SetParent(handle, false);
            var text = labelGo.AddComponent<TextMeshProUGUI>(); text.font = TMP_Settings.defaultFontAsset;
            // Radar canvases use more units per metre than the utility panels, so their grip caption is larger in units.
            text.text = "RIGHT-DRAG TO MOVE   |   XR: PINCH HANDLE"; text.fontSize = IsUtility ? FaaWorkspaceUi.MinTextSize : 18f;
            text.enableAutoSizing = true; text.fontSizeMin = FaaWorkspaceUi.MinTextSize; text.fontSizeMax = text.fontSize;
            text.alignment = TextAlignmentOptions.Center; text.raycastTarget = false;
            text.rectTransform.anchorMin = Vector2.zero; text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = text.rectTransform.offsetMax = Vector2.zero;
            handle.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            SetSpatial(false);
            if (handle != null) Object.Destroy(handle.gameObject);
            foreach (var g in ownedFocus) if (g != null) Object.Destroy(g);
            ownedFocus.Clear(); focusGroups.Clear(); focusHierarchyCount = -1; FocusAlpha = 1f; focusLive = true; NeighbourHidden = false;
        }
    }
}
