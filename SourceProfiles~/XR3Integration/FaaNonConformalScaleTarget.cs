using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>Explicit non-conformal module only; never register a whole attitude/conformal ancestor.</summary>
    public sealed class FaaNonConformalScaleTarget
    {
        /// <summary>
        /// Text-bearing flight modules. Their scale never goes below <see cref="FaaHudStyle.MinModuleScale"/>, so a sizing preset,
        /// pinch or old saved profile cannot push flight text below the FAA minimum character size.
        /// </summary>
        public static readonly HashSet<string> LegibleTextModules = new(System.StringComparer.Ordinal)
            { "airspeed", "altitude", "torque", "nr", "vertical-speed", "glideslope", "localizer", "heading", "fma", "bank" };

        public readonly string Id, Caption;
        public readonly Transform Target;
        public readonly FaaSpatialLayoutEntry Layout;
        public readonly float DefaultScale;
        /// <summary>Smallest scale this module renders at (text-bearing flight modules: at least <see cref="FaaHudStyle.MinModuleScale"/>).</summary>
        public readonly float MinimumLegibleScale;
        public Vector3 BaseScale { get; }
        private readonly Vector3 basePosition;
        private Graphic[] graphics;
        private int graphicsHierarchyCount;
        private readonly CanvasGroup[] visibility;
        private readonly Vector3[] corners = new Vector3[4];

        public FaaNonConformalScaleTarget(string id, string caption, Transform target, float defaultScale, float minimumLegibleScale = 0f)
        {
            Id = id; Caption = caption; Target = target;
            MinimumLegibleScale = Mathf.Max(FaaSpatialLayoutMath.Finite(minimumLegibleScale) ? minimumLegibleScale : 0f, DefaultMinimumScale(id));
            DefaultScale = Mathf.Max(defaultScale, MinimumLegibleScale);
            BaseScale = target.localScale;
            basePosition = target.localPosition;
            Layout = new FaaSpatialLayoutEntry { id = id, scale = DefaultScale };
            graphics = target.GetComponentsInChildren<Graphic>(true);
            graphicsHierarchyCount = target.hierarchyCount;
            visibility = target.GetComponentsInParent<CanvasGroup>(true);
        }

        public static float DefaultMinimumScale(string id) =>
            id != null && LegibleTextModules.Contains(id) ? FaaHudStyle.MinModuleScale : FaaSpatialLayoutMath.MinScale;

        /// <summary>The scale actually applied: the stored layout scale bounded by the legibility floor.</summary>
        public float EffectiveScale => FaaSpatialLayoutMath.LegibleScale(Layout.scale, MinimumLegibleScale);

        public void Apply()
        {
            if (Target == null) return;
            // Repair stored values below the floor (including existing saved profiles) so the size controls show what is drawn.
            if (FaaSpatialLayoutMath.Finite(Layout.scale) && Layout.scale < MinimumLegibleScale) Layout.scale = MinimumLegibleScale;
            Vector3 scale = BaseScale * EffectiveScale;
            if (Target.localScale != scale) Target.localScale = scale;
        }
        public void Restore()
        {
            if (Target == null) return;
            Target.localScale = BaseScale; Target.localPosition = basePosition;
        }

        public bool PlaceScreenCenter(Camera view, Vector2 center)
        {
            if (Target == null || !(Target.parent is RectTransform parent) || !TryScreenBounds(view, out Rect bounds)) return false;
            Canvas canvas = Target.GetComponentInParent<Canvas>();
            Camera ui = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, bounds.center, ui, out Vector2 before) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, center, ui, out Vector2 after)) return false;
            Vector2 delta = after - before;
            if (delta.sqrMagnitude > .00000001f) Target.localPosition += new Vector3(delta.x, delta.y, 0f);
            return true;
        }

        public bool TryLayoutBounds(RectTransform root, out Rect bounds)
        {
            bounds = default;
            if (Target == null || root == null || !Target.gameObject.activeInHierarchy) return false;
            RefreshGraphicsIfHierarchyChanged();
            bool any = false;
            Vector2 minimum = new Vector2(float.PositiveInfinity,float.PositiveInfinity), maximum = -minimum;
            foreach (var graphic in graphics)
            {
                if (graphic == null || !graphic.isActiveAndEnabled || graphic.color.a <= .001f ||
                    !FaaCanvasLocalGeometry.TryMatrix(graphic.transform,root,out var matrix)) continue;
                Rect rect = FaaCanvasLocalGeometry.TransformRect(graphic.rectTransform.rect,matrix);
                if (rect.width > root.rect.width || rect.height > root.rect.height) continue;
                minimum = Vector2.Min(minimum,rect.min); maximum = Vector2.Max(maximum,rect.max); any = true;
            }
            if (!any) return false;
            bounds = Rect.MinMaxRect(minimum.x-4,minimum.y-4,maximum.x+4,maximum.y+4);
            return true;
        }

        /// <summary>Instruments create their labels at runtime; include them once the hierarchy changes (rare, so no per-frame allocation).</summary>
        private void RefreshGraphicsIfHierarchyChanged()
        {
            int count = Target.hierarchyCount;
            if (count == graphicsHierarchyCount) return;
            graphicsHierarchyCount = count;
            graphics = Target.GetComponentsInChildren<Graphic>(true);
        }

        public bool PlaceLayoutCenter(RectTransform root, Vector2 centre)
        {
            if (!TryLayoutBounds(root,out Rect bounds) || !FaaCanvasLocalGeometry.TryMatrix(Target.parent,root,out var matrix)) return false;
            Vector2 delta = centre-bounds.center;
            // A sub-hundredth reference pixel is not a new layout. Avoid dirtying the Canvas at rest.
            if (delta.sqrMagnitude < .0001f) return true;
            Vector3 localDelta = matrix.inverse.MultiplyVector(new Vector3(delta.x,delta.y,0));
            if (FaaSpatialLayoutMath.Finite(localDelta)) Target.localPosition += localDelta;
            return true;
        }

        public bool TryRay(Ray ray, Camera view, out float distance)
        {
            distance = 0f;
            if (!TryScreenBounds(view, out Rect bounds)) return false;
            Canvas canvas = null;
            foreach (var graphic in graphics)
                if (graphic != null && graphic.isActiveAndEnabled && graphic.canvas != null) { canvas = graphic.canvas; break; }
            if (canvas == null) return false;
            float depth = canvas.renderMode == RenderMode.ScreenSpaceCamera ? canvas.planeDistance : 1.2f;
            var plane = canvas.renderMode == RenderMode.WorldSpace ? new Plane(canvas.transform.forward, canvas.transform.position) :
                new Plane(view.transform.forward, view.transform.position + view.transform.forward * depth);
            if (!plane.Raycast(ray, out distance) || distance <= 0f) return false;
            Vector3 screen = view.WorldToScreenPoint(ray.GetPoint(distance));
            return screen.z > 0f && bounds.Contains(new Vector2(screen.x, screen.y));
        }

        // A screen-fixed camera canvas is laid out in root-canvas coordinates. Before Unity's canvas pass its world pose
        // can still belong to the previous camera/aircraft pose; projecting that pose shifts selection/keep-out bounds away
        // from the dial actually drawn. Derive desktop pixel bounds directly, without moving or forcing the canvas.
        // World-space and native/stereo canvases retain their existing projection path.
        private static bool TryScreenFixedCameraBounds(Graphic graphic,Camera view,out Rect bounds)
        {
            bounds=default;
            if(view.stereoEnabled||UnityEngine.XR.XRSettings.isDeviceActive)return false;
            Canvas root=graphic.canvas.rootCanvas;
            if(root==null||root.renderMode!=RenderMode.ScreenSpaceCamera||!(root.transform is RectTransform frame))return false;
            Rect logical=frame.rect,pixels=root.pixelRect;
            if(logical.width<=0f||logical.height<=0f||pixels.width<=0f||pixels.height<=0f||
                !FaaCanvasLocalGeometry.TryMatrix(graphic.transform,frame,out var matrix))return false;
            Rect local=FaaCanvasLocalGeometry.TransformRect(graphic.rectTransform.rect,matrix);
            bounds=Rect.MinMaxRect(pixels.xMin+(local.xMin-logical.xMin)*pixels.width/logical.width,
                pixels.yMin+(local.yMin-logical.yMin)*pixels.height/logical.height,
                pixels.xMin+(local.xMax-logical.xMin)*pixels.width/logical.width,
                pixels.yMin+(local.yMax-logical.yMin)*pixels.height/logical.height);
            return true;
        }

        public bool TryScreenBounds(Camera view, out Rect bounds)
        {
            bounds = default;
            if (Target == null || !Target.gameObject.activeInHierarchy || view == null) return false;
            RefreshGraphicsIfHierarchyChanged();
            foreach (var group in visibility)
                if (group != null && group.isActiveAndEnabled && group.alpha <= .001f) return false;
            bool any = false;
            Vector2 minimum = new Vector2(float.PositiveInfinity, float.PositiveInfinity), maximum = -minimum;
            foreach (Graphic g in graphics)
            {
                if (g == null || !g.isActiveAndEnabled || g.color.a <= .001f || g.canvas == null || !g.canvas.isActiveAndEnabled) continue;
                Vector2 a,b;
                if(TryScreenFixedCameraBounds(g,view,out Rect screenFixed)) { a=screenFixed.min;b=screenFixed.max; }
                else
                {
                    Camera uiCamera = g.canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : g.canvas.worldCamera;
                    if(g.canvas.rootCanvas.renderMode==RenderMode.WorldSpace&&uiCamera!=null)
                    {
                        g.rectTransform.GetLocalCorners(corners);
                        if(!IndicatorSystem.Core.IndicatorCanvasProjection.TryProjectLocal(g.rectTransform,corners[0],uiCamera,out var first)||
                            !IndicatorSystem.Core.IndicatorCanvasProjection.TryProjectLocal(g.rectTransform,corners[2],uiCamera,out var last))continue;
                        a=first;b=last;
                    }
                    else
                    {
                        g.rectTransform.GetWorldCorners(corners);
                        a = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[0]);
                        b = RectTransformUtility.WorldToScreenPoint(uiCamera, corners[2]);
                    }
                }
                if (Mathf.Abs(b.x - a.x) > view.pixelWidth || Mathf.Abs(b.y - a.y) > view.pixelHeight) continue;
                minimum = Vector2.Min(minimum, Vector2.Min(a, b)); maximum = Vector2.Max(maximum, Vector2.Max(a, b)); any = true;
            }
            if (!any) return false;
            bounds = Rect.MinMaxRect(minimum.x - 8f, minimum.y - 8f, maximum.x + 8f, maximum.y + 8f);
            return bounds.width > 1f && bounds.height > 1f;
        }
    }
}
