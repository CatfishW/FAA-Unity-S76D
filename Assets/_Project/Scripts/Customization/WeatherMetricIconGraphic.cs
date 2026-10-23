using System;
using System.Collections.Generic;
using System.Globalization;
using AviationUI;
using FAA.XPlaneIntegration.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("FAA/Customization/Weather Metric Icon")]
    public sealed class WeatherMetricIconGraphic : MaskableGraphic
    {
        [SerializeField] private WeatherMetricIconKind iconKind;

        public WeatherMetricIconKind IconKind
        {
            get => iconKind;
            set
            {
                if (iconKind == value)
                {
                    return;
                }

                iconKind = value;
                SetVerticesDirty();
            }
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            Rect rect = rectTransform.rect;
            float size = Mathf.Max(1f, Mathf.Min(rect.width, rect.height));
            float unit = size / 24f;
            float stroke = Mathf.Max(1.15f, unit * 1.65f);
            Vector2 center = rect.center;
            Color32 tint = color;

            switch (iconKind)
            {
                case WeatherMetricIconKind.Temperature:
                    DrawTemperature(vertexHelper, center, unit, stroke, tint);
                    break;
                case WeatherMetricIconKind.Wind:
                    DrawWind(vertexHelper, center, unit, stroke, tint);
                    break;
                case WeatherMetricIconKind.Visibility:
                    DrawVisibility(vertexHelper, center, unit, stroke, tint);
                    break;
                case WeatherMetricIconKind.Pressure:
                    DrawPressure(vertexHelper, center, unit, stroke, tint);
                    break;
                case WeatherMetricIconKind.Precipitation:
                    DrawPrecipitation(vertexHelper, center, unit, stroke, tint);
                    break;
            }
        }

        private static void DrawTemperature(VertexHelper vh, Vector2 center, float unit, float stroke, Color32 tint)
        {
            Vector2 bulbCenter = center + Vector2.down * unit * 5f;
            AddLine(vh, bulbCenter + Vector2.up * unit, center + Vector2.up * unit * 7f, stroke * 1.8f, tint);
            AddCircle(vh, bulbCenter, unit * 3.2f, 18, tint, true, stroke);
            AddArc(vh, center + Vector2.up * unit * 2f, unit * 3.1f, -88f, 88f, 10, stroke, tint);
            AddLine(vh, center + new Vector2(unit * 2.5f, unit * 7f), center + new Vector2(unit * 5f, unit * 7f), stroke, tint);
        }

        private static void DrawWind(VertexHelper vh, Vector2 center, float unit, float stroke, Color32 tint)
        {
            AddLine(vh, center + new Vector2(-9f, 4.5f) * unit, center + new Vector2(6.5f, 4.5f) * unit, stroke, tint);
            AddArc(vh, center + new Vector2(6.5f, 2.3f) * unit, unit * 2.2f, 90f, 270f, 10, stroke, tint);
            AddLine(vh, center + new Vector2(-7f, 0f) * unit, center + new Vector2(3.5f, 0f) * unit, stroke, tint);
            AddLine(vh, center + new Vector2(-9f, -4.5f) * unit, center + new Vector2(7f, -4.5f) * unit, stroke, tint);
            AddLine(vh, center + new Vector2(7f, -4.5f) * unit, center + new Vector2(4.2f, -1.8f) * unit, stroke, tint);
        }

        private static void DrawVisibility(VertexHelper vh, Vector2 center, float unit, float stroke, Color32 tint)
        {
            AddArc(vh, center, unit * 9f, 18f, 162f, 15, stroke, tint);
            AddArc(vh, center, unit * 9f, 198f, 342f, 15, stroke, tint);
            AddCircle(vh, center, unit * 3f, 16, tint, false, stroke);
            AddCircle(vh, center, unit * 1.1f, 12, tint, true, stroke);
        }

        private static void DrawPressure(VertexHelper vh, Vector2 center, float unit, float stroke, Color32 tint)
        {
            AddCircle(vh, center, unit * 8.2f, 24, tint, false, stroke);
            AddLine(vh, center, center + new Vector2(4.8f, 4.6f) * unit, stroke * 1.25f, tint);
            AddCircle(vh, center, unit * 1.35f, 12, tint, true, stroke);
            AddLine(vh, center + new Vector2(-5.2f, -5.5f) * unit, center + new Vector2(5.2f, -5.5f) * unit, stroke, tint);
        }

        private static void DrawPrecipitation(VertexHelper vh, Vector2 center, float unit, float stroke, Color32 tint)
        {
            Vector2 tip = center + Vector2.up * unit * 9f;
            Vector2 left = center + new Vector2(-5.5f, -1.5f) * unit;
            Vector2 right = center + new Vector2(5.5f, -1.5f) * unit;
            AddTriangle(vh, tip, left, right, new Color32(tint.r, tint.g, tint.b, (byte)(tint.a * 0.52f)));
            AddCircle(vh, center + Vector2.down * unit * 2.8f, unit * 5.5f, 20, tint, true, stroke);
            AddLine(vh, tip, left, stroke, tint);
            AddLine(vh, tip, right, stroke, tint);
        }

        private static void AddArc(
            VertexHelper vh,
            Vector2 center,
            float radius,
            float startDegrees,
            float endDegrees,
            int segments,
            float width,
            Color32 tint)
        {
            Vector2 previous = center + Direction(startDegrees) * radius;
            for (int i = 1; i <= Mathf.Max(1, segments); i++)
            {
                float angle = Mathf.Lerp(startDegrees, endDegrees, i / (float)segments);
                Vector2 next = center + Direction(angle) * radius;
                AddLine(vh, previous, next, width, tint);
                previous = next;
            }
        }

        private static Vector2 Direction(float degrees)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(radians), Mathf.Sin(radians));
        }

        private static void AddLine(VertexHelper vh, Vector2 from, Vector2 to, float width, Color32 tint)
        {
            Vector2 delta = to - from;
            if (delta.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector2 perpendicular = new Vector2(-delta.y, delta.x).normalized * Mathf.Max(0.5f, width) * 0.5f;
            AddQuad(vh, from - perpendicular, from + perpendicular, to + perpendicular, to - perpendicular, tint);
        }

        private static void AddCircle(
            VertexHelper vh,
            Vector2 center,
            float radius,
            int segments,
            Color32 tint,
            bool filled,
            float stroke)
        {
            int safeSegments = Mathf.Max(8, segments);
            if (!filled)
            {
                AddArc(vh, center, radius, 0f, 360f, safeSegments, stroke, tint);
                return;
            }

            int centerIndex = vh.currentVertCount;
            AddVertex(vh, center, tint);
            for (int i = 0; i <= safeSegments; i++)
            {
                float angle = i / (float)safeSegments * Mathf.PI * 2f;
                AddVertex(vh, center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius, tint);
            }

            for (int i = 0; i < safeSegments; i++)
            {
                vh.AddTriangle(centerIndex, centerIndex + i + 1, centerIndex + i + 2);
            }
        }

        private static void AddTriangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Color32 tint)
        {
            int start = vh.currentVertCount;
            AddVertex(vh, a, tint);
            AddVertex(vh, b, tint);
            AddVertex(vh, c, tint);
            vh.AddTriangle(start, start + 1, start + 2);
        }

        private static void AddQuad(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color32 tint)
        {
            int start = vh.currentVertCount;
            AddVertex(vh, a, tint);
            AddVertex(vh, b, tint);
            AddVertex(vh, c, tint);
            AddVertex(vh, d, tint);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start + 2, start + 3, start);
        }

        private static void AddVertex(VertexHelper vh, Vector2 position, Color32 tint)
        {
            UIVertex vertex = UIVertex.simpleVert;
            vertex.position = position;
            vertex.color = tint;
            vh.AddVert(vertex);
        }
    }
}
