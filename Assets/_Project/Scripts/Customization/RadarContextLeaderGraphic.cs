using System.Collections;
using System.Collections.Generic;
using System;
using System.Globalization;
using TMPro;
using TrafficRadar;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Lightweight UI mesh for animated action leader lines and target pulses.
    /// It avoids LineRenderer/world-space conversion so the same geometry is
    /// sharp in desktop, XR simulator, and screen-space overlay canvases.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class RadarContextLeaderGraphic : MaskableGraphic
    {
        private struct Connector
        {
            public Vector2 Source;
            public Vector2 Target;
            public bool Visible;
            public bool Selected;
            public bool Focused;
            public Color Tint;
        }

        private readonly List<Connector> _connectors = new List<Connector>();
        public float Reveal { get; set; }
        public float Pulse { get; set; }
        public float IdleAlpha { get; set; } = 0.24f;

        public void BeginLayout(int count)
        {
            while (_connectors.Count < count)
            {
                _connectors.Add(default);
            }

            if (_connectors.Count > count)
            {
                _connectors.RemoveRange(count, _connectors.Count - count);
            }
        }

        public void SetConnector(
            int index,
            Vector2 source,
            Vector2 target,
            bool visible,
            bool selected,
            bool focused,
            Color tint)
        {
            if (index < 0 || index >= _connectors.Count)
            {
                return;
            }

            _connectors[index] = new Connector
            {
                Source = source,
                Target = target,
                Visible = visible,
                Selected = selected,
                Focused = focused,
                Tint = tint
            };
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            float reveal = Mathf.Clamp01(Reveal);
            if (reveal <= 0.001f)
            {
                return;
            }

            for (int i = 0; i < _connectors.Count; i++)
            {
                Connector connector = _connectors[i];
                if (!connector.Visible)
                {
                    continue;
                }

                float stagedReveal = Mathf.Clamp01(reveal * 1.22f - i * 0.055f);
                bool emphasized = connector.Selected || connector.Focused;
                float baseAlpha = connector.Selected ? 1f : connector.Focused ? 0.98f : IdleAlpha;
                Color tint = connector.Tint.a > 0f ? connector.Tint : FaaRadarVisualStyle.Accent;
                Color main = new Color(tint.r, tint.g, tint.b, baseAlpha * stagedReveal);

                IReadOnlyList<Vector2> points = BuildConnectorCurve(connector.Source, connector.Target);
                if (emphasized)
                {
                    Color halo = new Color(0.004f, 0.018f, 0.025f, 0.82f * stagedReveal);
                    DrawPartialPolyline(vertexHelper, points, stagedReveal, 6.2f, halo);
                }
                DrawPartialPolyline(
                    vertexHelper,
                    points,
                    stagedReveal,
                    connector.Selected ? 3.2f : connector.Focused ? 2.8f : 1.25f,
                    main);

                if (stagedReveal >= 0.96f)
                {
                    AddDisc(vertexHelper, connector.Target, emphasized ? 4.2f : 2.1f, main, 14);
                    if (emphasized)
                    {
                        AddRing(
                            vertexHelper,
                            connector.Target,
                            8f,
                            1.4f,
                            new Color(main.r, main.g, main.b, main.a * 0.68f),
                            24);
                    }
                }

                if (connector.Selected && Pulse > 0f)
                {
                    float pulse = Mathf.Clamp01(Pulse);
                    float radius = Mathf.Lerp(8f, 22f, pulse);
                    float alpha = (1f - pulse) * 0.9f;
                    AddRing(
                        vertexHelper,
                        connector.Target,
                        radius,
                        Mathf.Lerp(2.4f, 1f, pulse),
                        new Color(main.r, main.g, main.b, alpha),
                        32);
                }
            }
        }

        private static IReadOnlyList<Vector2> BuildConnectorCurve(Vector2 source, Vector2 target)
        {
            const int segmentCount = 10;
            Vector2[] points = new Vector2[segmentCount + 1];
            float direction = target.x >= source.x ? 1f : -1f;
            Vector2 controlA = source + Vector2.right * (direction * 34f);
            Vector2 controlB = target - Vector2.right * (direction * 52f);
            for (int i = 0; i <= segmentCount; i++)
            {
                float t = i / (float)segmentCount;
                float inverse = 1f - t;
                points[i] = inverse * inverse * inverse * source +
                            3f * inverse * inverse * t * controlA +
                            3f * inverse * t * t * controlB +
                            t * t * t * target;
            }
            return points;
        }

        private static void DrawPartialPolyline(
            VertexHelper vertexHelper,
            IReadOnlyList<Vector2> points,
            float progress,
            float width,
            Color color)
        {
            float totalLength = 0f;
            for (int i = 0; i < points.Count - 1; i++)
            {
                totalLength += Vector2.Distance(points[i], points[i + 1]);
            }

            float remaining = totalLength * Mathf.Clamp01(progress);
            for (int i = 0; i < points.Count - 1 && remaining > 0f; i++)
            {
                Vector2 from = points[i];
                Vector2 to = points[i + 1];
                float length = Vector2.Distance(from, to);
                if (length <= 0.001f)
                {
                    continue;
                }

                float segmentAmount = Mathf.Clamp01(remaining / length);
                AddLine(vertexHelper, from, Vector2.Lerp(from, to, segmentAmount), width, color);
                remaining -= length;
            }
        }

        private static void AddLine(VertexHelper vertexHelper, Vector2 from, Vector2 to, float width, Color color)
        {
            Vector2 delta = to - from;
            if (delta.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            Vector2 normal = new Vector2(-delta.y, delta.x).normalized * (width * 0.5f);
            AddQuad(
                vertexHelper,
                from - normal,
                from + normal,
                to + normal,
                to - normal,
                color);
        }

        private static void AddDisc(
            VertexHelper vertexHelper,
            Vector2 center,
            float radius,
            Color color,
            int segments)
        {
            int centerIndex = vertexHelper.currentVertCount;
            AddVertex(vertexHelper, center, color);
            for (int i = 0; i <= segments; i++)
            {
                float radians = Mathf.PI * 2f * i / segments;
                AddVertex(vertexHelper, center + new Vector2(Mathf.Cos(radians), Mathf.Sin(radians)) * radius, color);
                if (i > 0)
                {
                    vertexHelper.AddTriangle(centerIndex, centerIndex + i, centerIndex + i + 1);
                }
            }
        }

        private static void AddRing(
            VertexHelper vertexHelper,
            Vector2 center,
            float radius,
            float width,
            Color color,
            int segments)
        {
            float inner = Mathf.Max(0f, radius - width * 0.5f);
            float outer = radius + width * 0.5f;
            for (int i = 0; i < segments; i++)
            {
                float a0 = Mathf.PI * 2f * i / segments;
                float a1 = Mathf.PI * 2f * (i + 1) / segments;
                Vector2 d0 = new Vector2(Mathf.Cos(a0), Mathf.Sin(a0));
                Vector2 d1 = new Vector2(Mathf.Cos(a1), Mathf.Sin(a1));
                AddQuad(
                    vertexHelper,
                    center + d0 * inner,
                    center + d0 * outer,
                    center + d1 * outer,
                    center + d1 * inner,
                    color);
            }
        }

        private static void AddQuad(
            VertexHelper vertexHelper,
            Vector2 a,
            Vector2 b,
            Vector2 c,
            Vector2 d,
            Color color)
        {
            int start = vertexHelper.currentVertCount;
            AddVertex(vertexHelper, a, color);
            AddVertex(vertexHelper, b, color);
            AddVertex(vertexHelper, c, color);
            AddVertex(vertexHelper, d, color);
            vertexHelper.AddTriangle(start, start + 1, start + 2);
            vertexHelper.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddVertex(VertexHelper vertexHelper, Vector2 position, Color color)
        {
            UIVertex vertex = UIVertex.simpleVert;
            vertex.position = position;
            vertex.color = color;
            vertexHelper.AddVert(vertex);
        }
    }
}
