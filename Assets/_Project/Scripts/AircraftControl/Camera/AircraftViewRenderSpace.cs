using UnityEngine;

namespace AircraftControl.Camera
{
    /// <summary>
    /// Per-view translation between the unchanged geographic/physics world and a near-origin MR view.
    /// Rotations and tracked camera/projection matrices are never changed here. A spectator may share
    /// the same owner explicitly; an unrelated camera never inherits a global offset.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AircraftViewRenderSpace : MonoBehaviour
    {
        public AircraftCameraController Owner { get; private set; }
        public bool IsActive => Owner != null && Owner.NearRenderSpaceActive;
        public Vector3 Offset => IsActive ? Owner.NativeRenderWorldOffset : Vector3.zero;
        public Vector3 DataToRenderPoint(Vector3 point) => point - Offset;
        public Vector3 RenderToDataPoint(Vector3 point) => point + Offset;
        public Ray RenderToDataRay(Ray ray) => new Ray(RenderToDataPoint(ray.origin), ray.direction);

        public static AircraftViewRenderSpace ForCamera(UnityEngine.Camera view) =>
            view != null ? view.GetComponent<AircraftViewRenderSpace>() : null;

        public static AircraftViewRenderSpace Bind(UnityEngine.Camera view, AircraftCameraController owner)
        {
            if (view == null) return null;
            var space = ForCamera(view);
            if (space == null && owner != null) space = view.gameObject.AddComponent<AircraftViewRenderSpace>();
            if (space != null) space.Owner = owner;
            return space;
        }

        public static Vector3 DataToRenderPoint(UnityEngine.Camera view, Vector3 point)
        {
            var space = ForCamera(view);
            return space != null ? space.DataToRenderPoint(point) : point;
        }

        public static Vector3 RenderToDataPoint(UnityEngine.Camera view, Vector3 point)
        {
            var space = ForCamera(view);
            return space != null ? space.RenderToDataPoint(point) : point;
        }

        /// <summary>Only a reference inside an explicitly bound view is render-world; an aircraft/terrain reference remains data-world.</summary>
        public static Vector3 ReferenceToDataPoint(Transform reference, Vector3 point)
        {
            var space = reference != null ? reference.GetComponentInParent<AircraftViewRenderSpace>() : null;
            return space != null ? space.RenderToDataPoint(point) : point;
        }
    }
}
