using UnityEngine;
using System.Collections.Generic;

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
        private sealed class RenderRoot
        {
            public Transform Root, Parent;
            public AircraftViewRenderSpace Space;
            public AircraftCameraController Owner;
        }
        private static readonly Dictionary<Transform, RenderRoot> renderRoots = new Dictionary<Transform, RenderRoot>();
        private static readonly List<Transform> releasedRoots = new List<Transform>();
        private const float MaximumNearCoordinate = 128f;
        public static bool DiagnosticsEnabled { get; set; }
        public static long NearViewMatrixHits { get; private set; }
        public static long NearLocalMatrixHits { get; private set; }
        public static void ResetGeometryDiagnostics() { NearViewMatrixHits = NearLocalMatrixHits = 0; }
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
            if (space != null)
            {
                if (space.Owner != owner)
                {
                    space.ReleaseRenderRoots();
                    if (space.Owner != null) space.Owner.NativeRenderSpaceChanged -= space.OnRenderSpaceChanged;
                    space.Owner = owner;
                    if (owner != null) owner.NativeRenderSpaceChanged += space.OnRenderSpaceChanged;
                }
            }
            return space;
        }

        /// <summary>Explicit MR canvas/view ownership; coordinate proximity alone never registers a root.</summary>
        public static bool RegisterRenderRoot(Transform root, UnityEngine.Camera view)
        {
            var space = ForCamera(view);
            if (root == null || space == null || space.Owner == null) return false;
            if (renderRoots.TryGetValue(root, out var current) && current.Space == space && current.Owner == space.Owner && current.Parent == root.parent) return true;
            releasedRoots.Clear();
            foreach (var item in renderRoots)
                if (item.Key == null || item.Value.Space == null || item.Value.Owner == null) releasedRoots.Add(item.Key);
            foreach (var released in releasedRoots) renderRoots.Remove(released);
            releasedRoots.Clear();
            renderRoots[root] = new RenderRoot { Root = root, Parent = root.parent, Space = space, Owner = space.Owner };
            return true;
        }

        public static void ReleaseRenderRoot(Transform root, UnityEngine.Camera view)
        {
            if (object.ReferenceEquals(root, null) || !renderRoots.TryGetValue(root, out var entry)) return;
            if (entry.Space == ForCamera(view)) renderRoots.Remove(root);
        }

        private void ReleaseRenderRoots()
        {
            releasedRoots.Clear();
            foreach (var item in renderRoots) if (item.Key == null || item.Value.Space == this) releasedRoots.Add(item.Key);
            foreach (var root in releasedRoots) renderRoots.Remove(root);
            releasedRoots.Clear();
        }

        private static bool Near(Vector3 value) => Finite(value.x) && Finite(value.y) && Finite(value.z) &&
            Mathf.Abs(value.x) <= MaximumNearCoordinate && Mathf.Abs(value.y) <= MaximumNearCoordinate && Mathf.Abs(value.z) <= MaximumNearCoordinate;
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Matrix4x4 value)
        {
            for (int row = 0; row < 4; row++) for (int column = 0; column < 4; column++) if (!Finite(value[row, column])) return false;
            return true;
        }

        private static RenderRoot OwnedRoot(Transform reference)
        {
            if (renderRoots.Count == 0 || reference == null) return null;
            RenderRoot found = null;
            foreach (var item in renderRoots)
            {
                RenderRoot entry = item.Value;
                if (entry.Root == null || entry.Space == null || entry.Owner != entry.Space.Owner) continue;
                if (reference != entry.Root && !reference.IsChildOf(entry.Root)) continue;
                // Activity getters cross the native boundary. Only matching roots need them;
                // most references belong to one of many registered canvases.
                if (!entry.Space.isActiveAndEnabled || !entry.Space.IsActive) continue;
                // Nested explicitly registered roots take precedence over an outer owner's registration.
                if (found == null || entry.Root.IsChildOf(found.Root)) found = entry;
            }
            return found != null && found.Root.parent == found.Parent && Near(reference.position) && Near(found.Root.position) && Near(found.Space.transform.position) ? found : null;
        }

        public static bool TryGetNearViewMatrix(Transform reference, UnityEngine.Camera view, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            var space = ForCamera(view);
            if (space == null || !space.isActiveAndEnabled || !space.IsActive || !Near(view.transform.position)) return false;
            var root = OwnedRoot(reference);
            if (root == null || root.Owner != space.Owner) return false;
            Matrix4x4 candidate = view.worldToCameraMatrix * reference.localToWorldMatrix;
            if (!Finite(candidate)) return false;
            matrix = candidate;
            if (DiagnosticsEnabled) NearViewMatrixHits++;
            return true;
        }

        public static bool TryGetNearLocalMatrix(Transform child, Transform ancestor, out Matrix4x4 matrix)
        {
            matrix = Matrix4x4.identity;
            var childRoot = OwnedRoot(child);
            if (childRoot == null || ancestor == null || !Near(ancestor.position)) return false;
            // The nearest explicit root already establishes ownership for ancestors
            // inside that root. Ancestors above it may belong to another owner.
            var ancestorRoot = ancestor == childRoot.Root || ancestor.IsChildOf(childRoot.Root)
                ? childRoot : OwnedRoot(ancestor);
            if (childRoot == null || ancestorRoot == null || childRoot.Owner != ancestorRoot.Owner) return false;
            Transform requestedChild = child;
            // Preserve the original helper's strict depth<128 ancestor contract.
            bool descendant = false;
            for (int depth = 0; child != null && depth < 128; depth++, child = child.parent)
                if (child == ancestor) { descendant = true; break; }
            if (!descendant) return false;
            // worldToLocal is singular for zero scale. Exact nonzero/finite validation
            // deliberately accepts well-conditioned .001 HUD scale (world det1e-9).
            Matrix4x4 inverse = ancestor.worldToLocalMatrix;
            float determinant = inverse.m00 * (inverse.m11 * inverse.m22 - inverse.m12 * inverse.m21) -
                inverse.m01 * (inverse.m10 * inverse.m22 - inverse.m12 * inverse.m20) +
                inverse.m02 * (inverse.m10 * inverse.m21 - inverse.m11 * inverse.m20);
            if (!Finite(determinant) || determinant == 0f) return false;
            Matrix4x4 candidate = inverse * requestedChild.localToWorldMatrix;
            if (!Finite(candidate)) return false;
            matrix = candidate;
            if (DiagnosticsEnabled) NearLocalMatrixHits++;
            return true;
        }

        private void OnRenderSpaceChanged(bool active) { if (!active) ReleaseRenderRoots(); }
        private void OnDisable() => ReleaseRenderRoots();
        private void OnDestroy()
        {
            ReleaseRenderRoots();
            if (Owner != null) Owner.NativeRenderSpaceChanged -= OnRenderSpaceChanged;
        }
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRenderRootRegistry()
        {
            renderRoots.Clear(); releasedRoots.Clear(); DiagnosticsEnabled = false; ResetGeometryDiagnostics();
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
