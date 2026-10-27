using System;
using System.Collections.Generic;
using FAA.Customization;
using IndicatorSystem.Core;
using TrafficRadar;
using UnityEngine;
using UnityEngine.UI;

namespace IndicatorSystem.Display
{
    /// <summary>Traffic-only glyph. Position, threat priority, edge arrow, and labels remain owned by the indicator system.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class TrafficAircraftSymbolGraphic : MaskableGraphic
    {
        public TrafficGlyphDetail CurrentDetail { get; private set; } = TrafficGlyphDetail.MediumType;
        public float SymbolScale { get; private set; } = 1f;
        public float FootprintHalfExtent => Mathf.Min(TrafficGlyphPolicy.MaximumHalfExtent, 26f * SymbolScale);

        private IndicatorData data;
        private bool initialized, unknown, outline, headingTip, stripe, tail, navigation, rotateHeading;
        private string targetId;
        private float markerSize;
        private Color port, starboard, tailColor;
        private int lastSignature;
        private Vector2[] projected = Array.Empty<Vector2>();
        private Vector3[] viewPoints = Array.Empty<Vector3>();
        private Vector2 projectedCenter;
        private float projectionScale;
        private Quaternion pose = Quaternion.identity;
        private static FaaSvgIconLibrary library;
        private static readonly Dictionary<FaaRadarIcon, Shape> FlatShapes = new Dictionary<FaaRadarIcon, Shape>();
        private static readonly Dictionary<TrafficRadarDataManager.AircraftType, Shape> VolumeShapes = new Dictionary<TrafficRadarDataManager.AircraftType, Shape>();
        private static readonly Color Halo = new Color(.002f, .008f, .012f, .95f);
        private Color Shadow => new Color(Halo.r, Halo.g, Halo.b, Halo.a * Mathf.Clamp01(data.Color.a));
        private static readonly Vector2[] Question = { new Vector2(-4, 5), new Vector2(-3, 8), new Vector2(2, 8), new Vector2(4, 5), new Vector2(3, 2), new Vector2(0, 0), new Vector2(0, -3) };

        private struct Edge { public int A, B, First, Second; }
        private struct Ring { public Vector3 Centre, A, B, Facing; public bool DarkCentre; }
        private sealed class Shape
        {
            public Vector3[] Vertices;
            public int[] Triangles;
            public Edge[] Edges;
            public Vector3 Nose, Port, Starboard, Tail;
            public Ring[] Rings = Array.Empty<Ring>();
        }

        public void ResetDetailHistory() { initialized = false; SetVerticesDirty(); }

        public void Configure(IndicatorData value, IndicatorSettings settings)
        {
            if (!string.Equals(targetId, value.Id, StringComparison.Ordinal)) initialized = false;
            targetId = value.Id;
            data = value;
            unknown = !KnownType(value.AircraftType);
            bool adaptive = settings == null || settings.useAdaptiveTrafficSymbols;
            bool valid = TrafficGlyphPolicy.HasValidAttitude(value);
            CurrentDetail = unknown ? TrafficGlyphDetail.FarDoubleDiamond : TrafficGlyphPolicy.ResolveDetail(value.DistanceNM,
                settings != null ? settings.closeDistanceNM : 5f, settings != null ? settings.farDistanceNM : 40f,
                settings != null ? settings.trafficLodHysteresisNM : .25f, adaptive, valid, CurrentDetail, initialized);
            SymbolScale = TrafficGlyphPolicy.ResolveScale(value.DistanceNM, settings == null || settings.enableDistanceScaling,
                settings != null ? settings.closeDistanceNM : 5f, settings != null ? settings.farDistanceNM : 40f,
                settings != null ? settings.closeDistanceScale : 1.5f, settings != null ? settings.farDistanceScale : .6f);
            outline = settings != null && settings.trafficSymbolsOutline;
            headingTip = settings == null || settings.showTrafficHeadingTip;
            stripe = settings != null && settings.showTrafficBodyStripe;
            tail = settings == null || settings.showTrafficTailLight;
            navigation = settings == null || settings.showNavigationLights;
            rotateHeading = settings == null || settings.rotateSymbolByHeading;
            markerSize = Mathf.Clamp(settings != null ? settings.navLightSize * .5f : 4f, 2f, 5f) * SymbolScale;
            port = settings != null ? settings.portLightColor : Color.red;
            starboard = settings != null ? settings.starboardLightColor : Color.green;
            tailColor = settings != null ? settings.tailLightColor : Color.white;
            color = value.Color;
            raycastTarget = false;
            Canvas owner = GetComponentInParent<Canvas>();
            if (owner != null && gameObject.layer != owner.gameObject.layer) gameObject.layer = owner.gameObject.layer;
            int signature = value.Type.GetHashCode();
            unchecked
            {
                signature = signature * 31 + value.AircraftType.GetHashCode();
                signature = signature * 31 + CurrentDetail.GetHashCode();
                signature = signature * 31 + SymbolScale.GetHashCode();
                signature = signature * 31 + value.RelativeHeadingDegrees.GetHashCode();
                signature = signature * 31 + value.ViewRelativeRotation.GetHashCode();
                signature = signature * 31 + value.HasHeading.GetHashCode();
                signature = signature * 31 + value.Color.GetHashCode();
                signature = signature * 31 + markerSize.GetHashCode();
                signature = signature * 31 + port.GetHashCode();
                signature = signature * 31 + starboard.GetHashCode();
                signature = signature * 31 + tailColor.GetHashCode();
                signature = signature * 31 + (outline ? 1 : 0) + (headingTip ? 2 : 0) + (stripe ? 4 : 0) +
                    (tail ? 8 : 0) + (navigation ? 16 : 0) + (rotateHeading ? 32 : 0);
            }
            if (!initialized || signature != lastSignature) SetVerticesDirty();
            lastSignature = signature;
            initialized = true;
        }

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            if (!initialized || data.Type != IndicatorType.Traffic) return;
            if (unknown) { DrawUnknown(mesh); return; }
            if (CurrentDetail == TrafficGlyphDetail.FarDoubleDiamond) { DrawDoubleDiamond(mesh); return; }
            bool volume = CurrentDetail == TrafficGlyphDetail.CloseAttitude;
            Shape shape = volume ? Volume(data.AircraftType) : Flat(data.AircraftType);
            pose = volume ? Quaternion.Normalize(data.ViewRelativeRotation) :
                data.HasHeading && rotateHeading && TrafficGlyphPolicy.Finite(data.Heading) && TrafficGlyphPolicy.Finite(data.RelativeHeadingDegrees)
                    ? Quaternion.Euler(0, 0, -data.RelativeHeadingDegrees) : Quaternion.identity;
            Project(shape, volume);
            foreach (Edge edge in shape.Edges)
                if (volume ? Silhouette(shape, edge) : edge.Second < 0)
                    Stroke(mesh, projected[edge.A], projected[edge.B], (outline ? 3.2f : 3.5f) * SymbolScale, Shadow);
            if (outline)
            {
                foreach (Edge edge in shape.Edges)
                    if (volume ? Silhouette(shape, edge) : edge.Second < 0) Stroke(mesh, projected[edge.A], projected[edge.B], 1.25f * SymbolScale, data.Color);
            }
            else
            {
                int start = mesh.currentVertCount;
                for (int i = 0; i < projected.Length; i++) mesh.AddVert(projected[i], data.Color, Vector2.zero);
                for (int i = 0; i < shape.Triangles.Length; i += 3)
                    mesh.AddTriangle(start + shape.Triangles[i], start + shape.Triangles[i + 1], start + shape.Triangles[i + 2]);
            }
            if (volume) DrawVolumeDetails(mesh,shape);
            if (stripe && (!volume || (pose * Vector3.up).z < -.05f))
                Stroke(mesh, ProjectPoint(volume ? new Vector3(0, .15f, -.65f) : Vector3.Lerp(shape.Tail, shape.Nose, .2f), volume),
                    ProjectPoint(volume ? new Vector3(0, .15f, .65f) : Vector3.Lerp(shape.Tail, shape.Nose, .8f), volume),
                    1.5f * SymbolScale, new Color(.55f, .58f, .61f, data.Color.a));
            if (!data.HasHeading || !TrafficGlyphPolicy.Finite(data.Heading) || !volume && !TrafficGlyphPolicy.Finite(data.RelativeHeadingDegrees)) return;
            if (headingTip)
            {
                Vector2 nose = ProjectPoint(shape.Nose, volume);
                Vector3 neck = volume ? shape.Nose - Vector3.forward * .3f : Vector3.Lerp(shape.Nose, shape.Tail, .2f);
                Vector2 direction = volume ? nose - ProjectPoint(neck, true) : (Vector2)(pose * Vector3.up);
                if (direction.sqrMagnitude < .0001f) direction = ProjectPoint(shape.Nose + Vector3.up * .15f, volume) - nose;
                NoseTriangle(mesh, nose, direction.normalized, MarkColor(starboard));
            }
            Vector3 observer = volume ? Quaternion.Inverse(pose) * Vector3.back : Vector3.up;
            bool portVisible = !volume || observer.x < .25f && observer.z > -.35f;
            bool starboardVisible = !volume || observer.x > -.25f && observer.z > -.35f;
            bool tailVisible = !volume || observer.z < .25f;
            if (navigation && portVisible) Marker(mesh, ProjectPoint(shape.Port, volume), markerSize * .5f, MarkColor(port));
            if (navigation && starboardVisible) Marker(mesh, ProjectPoint(shape.Starboard, volume), markerSize * .5f, MarkColor(starboard));
            if (tail && tailVisible) Marker(mesh, ProjectPoint(shape.Tail, volume), markerSize * .45f, MarkColor(tailColor));
        }

        private Color MarkColor(Color marker) { marker.a *= Mathf.Clamp01(data.Color.a); return marker; }
        private void Marker(VertexHelper mesh, Vector2 centre, float radius, Color tint)
        {
            Disc(mesh, centre, radius + SymbolScale, Shadow);
            Disc(mesh, centre, radius, tint);
        }
        private void NoseTriangle(VertexHelper mesh, Vector2 centre, Vector2 forward, Color tint)
        {
            if (forward.sqrMagnitude < .0001f) forward = Vector2.up;
            float length = markerSize * .7f;
            Vector2 side = new Vector2(-forward.y, forward.x) * length * .65f;
            Vector2 tip = centre + forward * length;
            Vector2 a = centre - forward * length * .55f + side;
            Vector2 b = centre - forward * length * .55f - side;
            Stroke(mesh, tip, a, 2f * SymbolScale, Shadow);
            Stroke(mesh, a, b, 2f * SymbolScale, Shadow);
            Stroke(mesh, b, tip, 2f * SymbolScale, Shadow);
            int start = mesh.currentVertCount;
            mesh.AddVert(tip, tint, Vector2.zero); mesh.AddVert(a, tint, Vector2.zero); mesh.AddVert(b, tint, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2);
        }
        private static bool KnownType(TrafficRadarDataManager.AircraftType type) => type == TrafficRadarDataManager.AircraftType.Commercial ||
            type == TrafficRadarDataManager.AircraftType.General || type == TrafficRadarDataManager.AircraftType.Military || type == TrafficRadarDataManager.AircraftType.Helicopter;

        private void DrawDoubleDiamond(VertexHelper mesh)
        {
            float r = 17f * SymbolScale;
            Diamond(mesh, rectTransform.rect.center, r);
            Diamond(mesh, rectTransform.rect.center, r * .65f);
        }
        private void DrawUnknown(VertexHelper mesh)
        {
            Vector2 c = rectTransform.rect.center;
            float scale = SymbolScale;
            Diamond(mesh, c, 17f * scale);
            for (int i = 1; i < Question.Length; i++) ContrastStroke(mesh, c + Question[i - 1] * scale, c + Question[i] * scale);
            Disc(mesh, c + new Vector2(0, -8) * scale, 1.2f * scale, data.Color);
        }
        private void Diamond(VertexHelper mesh, Vector2 c, float r)
        {
            ContrastStroke(mesh, c + Vector2.up * r, c + Vector2.right * r);
            ContrastStroke(mesh, c + Vector2.right * r, c + Vector2.down * r);
            ContrastStroke(mesh, c + Vector2.down * r, c + Vector2.left * r);
            ContrastStroke(mesh, c + Vector2.left * r, c + Vector2.up * r);
        }
        private void ContrastStroke(VertexHelper mesh, Vector2 a, Vector2 b)
        {
            Stroke(mesh, a, b, 3.2f * SymbolScale, Shadow);
            Stroke(mesh, a, b, 1.35f * SymbolScale, data.Color);
        }

        private void Project(Shape shape, bool volume)
        {
            if (projected.Length != shape.Vertices.Length) { projected = new Vector2[shape.Vertices.Length]; viewPoints = new Vector3[shape.Vertices.Length]; }
            Vector2 min = new Vector2(float.PositiveInfinity, float.PositiveInfinity), max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            for (int i = 0; i < shape.Vertices.Length; i++)
            {
                viewPoints[i] = pose * shape.Vertices[i];
                float perspective = volume ? 1f / Mathf.Max(2f, 4f + viewPoints[i].z) : 1f;
                Vector2 p = new Vector2(viewPoints[i].x, viewPoints[i].y) * perspective;
                projected[i] = p; min = Vector2.Min(min, p); max = Vector2.Max(max, p);
            }
            foreach (Ring ring in shape.Rings)
            {
                if (!RingVisible(ring)) continue;
                for (int i = 0; i < 16; i++)
                {
                    float angle = i * Mathf.PI / 8f;
                    Vector3 p = pose * (ring.Centre + ring.A * Mathf.Cos(angle) + ring.B * Mathf.Sin(angle));
                    Vector2 point = new Vector2(p.x,p.y) / Mathf.Max(2f,4f+p.z);
                    min = Vector2.Min(min,point); max = Vector2.Max(max,point);
                }
            }
            projectedCenter = (min + max) * .5f;
            projectionScale = TrafficGlyphPolicy.BaseDiameter * SymbolScale / Mathf.Max(.0001f, Mathf.Max(max.x - min.x, max.y - min.y));
            for (int i = 0; i < projected.Length; i++) projected[i] = (projected[i] - projectedCenter) * projectionScale + rectTransform.rect.center;
        }
        private Vector2 ProjectPoint(Vector3 point, bool volume)
        {
            Vector3 p = pose * point;
            float perspective = volume ? 1f / Mathf.Max(2f, 4f + p.z) : 1f;
            return (new Vector2(p.x, p.y) * perspective - projectedCenter) * projectionScale + rectTransform.rect.center;
        }
        private bool Silhouette(Shape shape, Edge edge) => edge.Second < 0 || Facing(shape, edge.First) != Facing(shape, edge.Second);
        private bool Facing(Shape shape, int face)
        {
            int i = face * 3;
            Vector3 a = viewPoints[shape.Triangles[i]], b = viewPoints[shape.Triangles[i + 1]], c = viewPoints[shape.Triangles[i + 2]];
            return Vector3.Cross(b - a, c - a).z <= 0f;
        }

        private bool RingVisible(Ring ring) => ring.Facing == Vector3.zero ||
            Vector3.Dot(Quaternion.Inverse(pose) * Vector3.back,ring.Facing) > .1f;
        private void DrawVolumeDetails(VertexHelper mesh, Shape shape)
        {
            foreach (Ring ring in shape.Rings)
            {
                if (!RingVisible(ring)) continue;
                Vector2 centre = ProjectPoint(ring.Centre,true);
                if (ring.DarkCentre && !outline)
                {
                    int start = mesh.currentVertCount; mesh.AddVert(centre,Shadow,Vector2.zero);
                    for (int i = 0; i <= 16; i++)
                    {
                        float angle = i * Mathf.PI / 8f;
                        mesh.AddVert(ProjectPoint(ring.Centre + ring.A*Mathf.Cos(angle) + ring.B*Mathf.Sin(angle),true),Shadow,Vector2.zero);
                        if (i > 0) mesh.AddTriangle(start,start+i,start+i+1);
                    }
                }
                for (int i = 0; i < 16; i++)
                {
                    float a = i*Mathf.PI/8f, b = (i+1)*Mathf.PI/8f;
                    Vector2 from = ProjectPoint(ring.Centre + ring.A*Mathf.Cos(a) + ring.B*Mathf.Sin(a),true);
                    Vector2 to = ProjectPoint(ring.Centre + ring.A*Mathf.Cos(b) + ring.B*Mathf.Sin(b),true);
                    Stroke(mesh,from,to,2.6f*SymbolScale,Shadow);
                    Stroke(mesh,from,to,1.05f*SymbolScale,data.Color);
                }
            }
        }

        private static FaaRadarIcon Icon(TrafficRadarDataManager.AircraftType type) => type == TrafficRadarDataManager.AircraftType.Commercial ? FaaRadarIcon.AircraftAirliner :
            type == TrafficRadarDataManager.AircraftType.General ? FaaRadarIcon.AircraftGeneral :
            type == TrafficRadarDataManager.AircraftType.Military ? FaaRadarIcon.AircraftMilitary : FaaRadarIcon.AircraftHelicopter;
        private static Shape Flat(TrafficRadarDataManager.AircraftType type)
        {
            FaaRadarIcon icon = Icon(type);
            if (FlatShapes.TryGetValue(icon, out Shape cached)) return cached;
            if (library == null) library = Resources.Load<FaaSvgIconLibrary>(FaaSvgIconLibrary.ResourcePath);
            FaaSvgIconLibrary.Entry entry = library != null ? library.Find(icon) : null;
            if (entry == null || entry.vertices.Length == 0)
            {
                cached = FlatFallback(type);
                FlatShapes.Add(icon, cached);
                return cached;
            }
            var vertices = new List<Vector3>();
            var remap = new int[entry.vertices.Length];
            var unique = new Dictionary<Vector2, int>();
            Vector3 nose = Vector3.zero, left = Vector3.zero, right = Vector3.zero, rear = Vector3.zero;
            for (int i = 0; i < entry.vertices.Length; i++)
            {
                Vector2 p = entry.vertices[i];
                Vector2 key = new Vector2(Mathf.Round(p.x * 10000f), Mathf.Round(p.y * 10000f));
                if (!unique.TryGetValue(key, out int index)) { index = vertices.Count; unique.Add(key, index); vertices.Add(p); }
                remap[i] = index;
                if (p.y > nose.y) nose = p;
                if (p.y < rear.y) rear = p;
                if (p.x < left.x) left = p;
                if (p.x > right.x) right = p;
            }
            int[] triangles = new int[entry.triangles.Length];
            for (int i = 0; i < triangles.Length; i++) triangles[i] = remap[entry.triangles[i]];
            cached = SolidFlatContours(MakeShape(vertices.ToArray(), triangles));
            if (cached.Triangles.Length == 0) cached = FlatFallback(type);
            cached.Nose = new Vector3(0, nose.y, 0); cached.Port = left; cached.Starboard = right; cached.Tail = new Vector3(0, rear.y, 0);
            FlatShapes.Add(icon, cached);
            return cached;
        }

        // The icon library contains tessellated SVG strokes. Fill the outer contours,
        // not those narrow stroke ribbons; contained inner ribbon contours are holes
        // in the source stroke, not holes in the aircraft silhouette.
        private static Shape SolidFlatContours(Shape source)
        {
            var edges = new List<Edge>();
            foreach (Edge edge in source.Edges) if (edge.Second < 0) edges.Add(edge);
            var adjacent = new List<int>[source.Vertices.Length];
            for (int i = 0; i < edges.Count; i++)
            {
                Edge edge = edges[i];
                (adjacent[edge.A] ??= new List<int>()).Add(i);
                (adjacent[edge.B] ??= new List<int>()).Add(i);
            }
            var used = new bool[edges.Count];
            var loops = new List<List<int>>();
            for (int i = 0; i < edges.Count; i++)
            {
                if (used[i]) continue;
                Edge edge = edges[i]; used[i] = true;
                int start = edge.A, current = edge.B;
                var loop = new List<int> { start };
                for (int step = 0; current != start && step <= edges.Count; step++)
                {
                    loop.Add(current); int next = -1;
                    foreach (int candidate in adjacent[current]) if (!used[candidate]) { next = candidate; break; }
                    if (next < 0) break;
                    used[next] = true; edge = edges[next];
                    current = edge.A == current ? edge.B : edge.A;
                }
                if (current == start && loop.Count >= 3 && Mathf.Abs(PolygonArea(source.Vertices,loop)) > .001f) loops.Add(loop);
            }
            var filled = new List<int>();
            for (int i = 0; i < loops.Count; i++)
            {
                float area = Mathf.Abs(PolygonArea(source.Vertices,loops[i])); int depth = 0;
                for (int j = 0; j < loops.Count; j++)
                {
                    if (i == j || Mathf.Abs(PolygonArea(source.Vertices,loops[j])) <= area) continue;
                    bool contained = true;
                    foreach (int vertex in loops[i])
                        if (!InsidePolygon(source.Vertices[vertex],source.Vertices,loops[j])) { contained = false; break; }
                    if (contained) depth++;
                }
                if ((depth & 1) == 0) EarClip(source.Vertices,loops[i],filled);
            }
            return MakeShape(source.Vertices,filled.ToArray());
        }
        private static float PolygonArea(Vector3[] vertices, List<int> loop)
        {
            float area = 0;
            for (int i = 0; i < loop.Count; i++)
            {
                Vector3 a = vertices[loop[i]], b = vertices[loop[(i+1)%loop.Count]];
                area += a.x*b.y - b.x*a.y;
            }
            return area * .5f;
        }
        private static bool InsidePolygon(Vector2 p, Vector3[] vertices, List<int> loop)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count-1; i < loop.Count; j = i++)
            {
                Vector2 a = vertices[loop[i]], b = vertices[loop[j]];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x) inside = !inside;
            }
            return inside;
        }
        private static float Cross2(Vector2 a, Vector2 b, Vector2 c) => (b.x-a.x)*(c.y-a.y)-(b.y-a.y)*(c.x-a.x);
        private static void EarClip(Vector3[] vertices, List<int> contour, List<int> triangles)
        {
            var polygon = new List<int>(contour);
            if (PolygonArea(vertices,polygon) < 0f) polygon.Reverse();
            for (int i = polygon.Count-1; i >= 0 && polygon.Count > 3; i--)
                if (Mathf.Abs(Cross2(vertices[polygon[(i+polygon.Count-1)%polygon.Count]],vertices[polygon[i]],vertices[polygon[(i+1)%polygon.Count]])) < .00001f)
                    polygon.RemoveAt(i);
            int guard = polygon.Count * polygon.Count;
            while (polygon.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < polygon.Count; i++)
                {
                    int a = polygon[(i+polygon.Count-1)%polygon.Count], b = polygon[i], c = polygon[(i+1)%polygon.Count];
                    if (Cross2(vertices[a],vertices[b],vertices[c]) <= .000001f) continue;
                    bool occupied = false;
                    foreach (int point in polygon)
                    {
                        if (point == a || point == b || point == c) continue;
                        Vector2 p = vertices[point];
                        if (Cross2(vertices[a],vertices[b],p) >= -.000001f && Cross2(vertices[b],vertices[c],p) >= -.000001f && Cross2(vertices[c],vertices[a],p) >= -.000001f)
                        { occupied = true; break; }
                    }
                    if (occupied) continue;
                    triangles.Add(a); triangles.Add(b); triangles.Add(c); polygon.RemoveAt(i); clipped = true; break;
                }
                if (!clipped) break;
            }
            if (polygon.Count == 3) { triangles.Add(polygon[0]); triangles.Add(polygon[1]); triangles.Add(polygon[2]); }
        }

        private static Shape FlatFallback(TrafficRadarDataManager.AircraftType type)
        {
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            FlatQuad(vertices, triangles, -.12f, -.9f, .12f, 1.05f);
            FlatQuad(vertices, triangles, -1f, -.1f, 1f, .12f);
            FlatQuad(vertices, triangles, -.4f, -.9f, .4f, -.7f);
            if (type == TrafficRadarDataManager.AircraftType.Helicopter) FlatQuad(vertices, triangles, -.04f, -1f, .04f, 1f);
            Shape shape = MakeShape(vertices.ToArray(), triangles.ToArray());
            shape.Nose = new Vector3(0, 1.05f, 0); shape.Tail = new Vector3(0, -.9f, 0);
            shape.Port = Vector3.left; shape.Starboard = Vector3.right;
            return shape;
        }
        private static void FlatQuad(List<Vector3> vertices, List<int> triangles, float x0, float y0, float x1, float y1)
        {
            int start = vertices.Count;
            vertices.Add(new Vector3(x0,y0,0)); vertices.Add(new Vector3(x1,y0,0)); vertices.Add(new Vector3(x1,y1,0)); vertices.Add(new Vector3(x0,y1,0));
            triangles.Add(start); triangles.Add(start+1); triangles.Add(start+2); triangles.Add(start); triangles.Add(start+2); triangles.Add(start+3);
        }

        private static Shape Volume(TrafficRadarDataManager.AircraftType type)
        {
            if (VolumeShapes.TryGetValue(type, out Shape cached)) return cached;
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            var rings = new List<Ring>();
            bool helicopter = type == TrafficRadarDataManager.AircraftType.Helicopter;
            bool general = type == TrafficRadarDataManager.AircraftType.General;
            bool military = type == TrafficRadarDataManager.AircraftType.Military;
            float[] z = helicopter ? new[] { -1.05f, -.65f, -.25f, .3f, .65f } : new[] { -1f, -.65f, .25f, .75f, 1.05f };
            float[] width = helicopter ? new[] { .025f, .04f, .18f, .2f, .07f } : new[] { .035f, .1f, .14f, .09f, .015f };
            float[] height = helicopter ? new[] { .035f, .05f, .17f, .18f, .09f } : new[] { .04f, .09f, .14f, .09f, .02f };
            if (general) height = new[] { .04f, .10f, .22f, .15f, .03f };
            Fuselage(vertices, triangles, z, width, height);
            float span = military ? .95f : 1.05f;
            float tipZ = type == TrafficRadarDataManager.AircraftType.General ? .12f : type == TrafficRadarDataManager.AircraftType.Military ? -.43f : -.2f;
            if (helicopter)
            {
                Slab(vertices, triangles, new[] { new Vector3(-1.1f,.43f,-.04f), new Vector3(1.1f,.43f,-.04f), new Vector3(1.1f,.43f,.04f), new Vector3(-1.1f,.43f,.04f) }, Vector3.up * .025f);
                Slab(vertices, triangles, new[] { new Vector3(-.04f,.43f,-.95f), new Vector3(.04f,.43f,-.95f), new Vector3(.04f,.43f,.95f), new Vector3(-.04f,.43f,.95f) }, Vector3.up * .025f);
                Rod(vertices,triangles,new Vector3(0,.13f,0),new Vector3(0,.43f,0),.045f);
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float x = sign*.25f;
                    Rod(vertices,triangles,new Vector3(x,-.28f,-.55f),new Vector3(x,-.28f,.48f),.035f);
                    Rod(vertices,triangles,new Vector3(x,-.28f,.48f),new Vector3(x,-.20f,.65f),.035f);
                    foreach (float support in new[] { -.3f,.3f }) Rod(vertices,triangles,new Vector3(sign*.12f,-.04f,support),new Vector3(x,-.28f,support),.025f);
                }
                rings.Add(new Ring { Centre = new Vector3(.065f,.10f,-1.05f), A = Vector3.up*.18f, B = Vector3.forward*.18f, Facing = Vector3.right });
                Rod(vertices,triangles,new Vector3(.065f,-.08f,-1.05f),new Vector3(.065f,.28f,-1.05f),.018f);
                Rod(vertices,triangles,new Vector3(.065f,.10f,-1.23f),new Vector3(.065f,.10f,-.87f),.018f);
            }
            else for (int sign = -1; sign <= 1; sign += 2)
            {
                Slab(vertices, triangles, new[] { new Vector3(sign*.09f,0,military ? .65f : .32f), new Vector3(sign*span,0,tipZ), new Vector3(sign*span,0,tipZ-.18f), new Vector3(sign*.09f,0,military ? -.5f : -.35f) }, Vector3.up * .03f);
            }
            Slab(vertices, triangles, new[] { new Vector3(-.4f,0,-.7f), new Vector3(.4f,0,-.7f), new Vector3(.4f,0,-.92f), new Vector3(-.4f,0,-.92f) }, Vector3.up * .025f);
            foreach (float finX in military ? new[] { -.2f,.2f } : new[] { 0f })
                Slab(vertices, triangles, new[] { new Vector3(finX,.04f,-.58f), new Vector3(finX,.4f,-.84f), new Vector3(finX,.4f,-1f), new Vector3(finX,.04f,-.94f) }, Vector3.right * .025f);
            if (type == TrafficRadarDataManager.AircraftType.Commercial)
                foreach (float engine in new[] { -.78f,-.42f,.42f,.78f })
                    Fuselage(vertices,triangles,new[] { -.3f,.04f,.3f },new[] { .07f,.105f,.075f },new[] { .07f,.105f,.075f },new Vector3(engine,-.14f,0));
            if (military) foreach (float engine in new[] { -.14f,.14f })
            {
                Fuselage(vertices,triangles,new[] { -1.17f,-.9f,-.45f },new[] { .095f,.11f,.09f },new[] { .09f,.10f,.08f },new Vector3(engine,-.025f,0));
                rings.Add(new Ring { Centre = new Vector3(engine,-.025f,-1.175f), A = Vector3.right*.075f, B = Vector3.up*.07f, Facing = Vector3.back, DarkCentre = true });
            }
            if (general)
            {
                rings.Add(new Ring { Centre = new Vector3(0,0,1.09f), A = Vector3.right*.3f, B = Vector3.up*.3f });
                foreach (Vector3 wheel in new[] { new Vector3(-.3f,-.32f,-.12f),new Vector3(.3f,-.32f,-.12f),new Vector3(0,-.29f,.65f) })
                {
                    Rod(vertices,triangles,new Vector3(wheel.x*.4f,-.04f,wheel.z),wheel,.025f);
                    var circle = new Vector3[8];
                    for (int i = 0; i < 8; i++) { float angle = i*Mathf.PI*.25f; circle[i] = wheel + new Vector3(0,Mathf.Cos(angle)*.075f,Mathf.Sin(angle)*.075f); }
                    Slab(vertices,triangles,circle,Vector3.right*.035f);
                }
            }
            cached = MakeShape(vertices.ToArray(), triangles.ToArray());
            cached.Rings = rings.ToArray();
            cached.Nose = new Vector3(0, 0, z[z.Length - 1]);
            cached.Port = helicopter ? new Vector3(-1.1f, .43f, 0) : new Vector3(-span, 0, tipZ - .09f);
            cached.Starboard = new Vector3(-cached.Port.x, cached.Port.y, cached.Port.z);
            cached.Tail = new Vector3(0, .04f, z[0]);
            VolumeShapes.Add(type, cached);
            return cached;
        }
        private static Shape MakeShape(Vector3[] vertices, int[] triangles)
        {
            var edges = new Dictionary<ulong, Edge>();
            for (int i = 0; i < triangles.Length; i += 3)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[i + k], b = triangles[i + (k + 1) % 3];
                    ulong key = ((ulong)(uint)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);
                    if (edges.TryGetValue(key, out Edge edge)) { edge.Second = i / 3; edges[key] = edge; }
                    else edges.Add(key, new Edge { A = a, B = b, First = i / 3, Second = -1 });
                }
            var boundary = new List<Edge>();
            foreach (Edge edge in edges.Values) boundary.Add(edge);
            return new Shape { Vertices = vertices, Triangles = triangles, Edges = boundary.ToArray() };
        }
        private static void Fuselage(List<Vector3> vertices, List<int> triangles, float[] z, float[] width, float[] height, Vector3 offset = default)
        {
            int start = vertices.Count;
            Vector3 centre = offset + Vector3.forward * (z[0]+z[z.Length-1]) * .5f;
            for (int ring = 0; ring < z.Length; ring++) for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI * .25f;
                vertices.Add(new Vector3(Mathf.Cos(angle) * width[ring], Mathf.Sin(angle) * height[ring], z[ring]) + offset);
            }
            for (int ring = 0; ring < z.Length - 1; ring++) for (int i = 0; i < 8; i++)
            {
                int a = start + ring * 8 + i, b = start + ring * 8 + (i + 1) % 8, c = b + 8, d = a + 8;
                Face(vertices, triangles, a, b, c, centre); Face(vertices, triangles, a, c, d, centre);
            }
            for (int i = 1; i < 7; i++)
            {
                Face(vertices, triangles, start, start + i, start + i + 1, centre);
                int end = start + (z.Length - 1) * 8;
                Face(vertices, triangles, end, end + i, end + i + 1, centre);
            }
        }
        private static void Rod(List<Vector3> vertices,List<int> triangles,Vector3 from,Vector3 to,float radius)
        {
            Vector3 direction = (to-from).normalized;
            Vector3 a = Vector3.Cross(direction,Mathf.Abs(direction.y) > .9f ? Vector3.right : Vector3.up).normalized*radius;
            Vector3 b = Vector3.Cross(direction,a).normalized*radius;
            Slab(vertices,triangles,new[] { from-a,from+a,to+a,to-a },b);
        }
        private static void Slab(List<Vector3> vertices, List<int> triangles, Vector3[] polygon, Vector3 thickness)
        {
            int start = vertices.Count, n = polygon.Length; Vector3 centre = Vector3.zero;
            foreach (Vector3 p in polygon) { vertices.Add(p - thickness); centre += p; }
            foreach (Vector3 p in polygon) vertices.Add(p + thickness);
            centre /= n;
            for (int i = 1; i < n - 1; i++)
            {
                Face(vertices, triangles, start, start + i, start + i + 1, centre);
                Face(vertices, triangles, start + n, start + n + i, start + n + i + 1, centre);
            }
            for (int i = 0; i < n; i++)
            {
                int a = start + i, b = start + (i + 1) % n;
                Face(vertices, triangles, a, b, b + n, centre); Face(vertices, triangles, a, b + n, a + n, centre);
            }
        }
        private static void Face(List<Vector3> vertices, List<int> triangles, int a, int b, int c, Vector3 centre)
        {
            if (Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), (vertices[a] + vertices[b] + vertices[c]) / 3f - centre) < 0f)
            { int swap = b; b = c; c = swap; }
            triangles.Add(a); triangles.Add(b); triangles.Add(c);
        }

        private static void Stroke(VertexHelper mesh, Vector2 a, Vector2 b, float width, Color tint)
        {
            if ((b - a).sqrMagnitude < .00001f) return;
            Vector2 n = new Vector2(-(b - a).y, (b - a).x).normalized * width * .5f;
            int start = mesh.currentVertCount;
            mesh.AddVert(a - n, tint, Vector2.zero); mesh.AddVert(a + n, tint, Vector2.zero);
            mesh.AddVert(b + n, tint, Vector2.zero); mesh.AddVert(b - n, tint, Vector2.zero);
            mesh.AddTriangle(start, start + 1, start + 2); mesh.AddTriangle(start, start + 2, start + 3);
        }
        private static void Disc(VertexHelper mesh, Vector2 centre, float radius, Color tint)
        {
            int start = mesh.currentVertCount; mesh.AddVert(centre, tint, Vector2.zero);
            for (int i = 0; i <= 12; i++)
            {
                float angle = i * Mathf.PI / 6f;
                mesh.AddVert(centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, tint, Vector2.zero);
                if (i > 0) mesh.AddTriangle(start, start + i, start + i + 1);
            }
        }
    }
}
