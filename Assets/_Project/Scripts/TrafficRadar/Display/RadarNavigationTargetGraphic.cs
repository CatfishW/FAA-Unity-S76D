using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using TrafficRadar.Core;

namespace TrafficRadar
{
    /// <summary>
    /// Crisp, resolution-independent target cue layered over the generated
    /// radar texture.  Keeping this as a MaskableGraphic avoids pixelation
    /// when the XR-3 simulator expands the scope to fullscreen.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    internal sealed class RadarNavigationTargetGraphic : MaskableGraphic
    {
        private Vector2 _normalizedPosition;
        private Color _accent = new Color(1f, 0.78f, 0.28f, 1f);
        private bool _visible;
        private bool _withinRange;
        private float _pulse;
        private bool _preview;

        public void SetTarget(
            Vector2 normalizedPosition,
            bool visible,
            bool withinRange,
            Color accent,
            float pulse,
            bool immediate,
            bool preview = false)
        {
            _normalizedPosition = normalizedPosition;
            _visible = visible;
            _withinRange = withinRange;
            _accent = accent;
            _pulse = pulse;
            _preview = preview;
            raycastTarget = false;
            // The immediate flag is intentionally accepted by the display API
            // so callers can use the same path for placement and animation.
            // Mesh regeneration itself is frame-safe in both cases.
            SetVerticesDirty();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper vertexHelper)
        {
            vertexHelper.Clear();
            if (!_visible)
            {
                return;
            }

            Rect rect = rectTransform.rect;
            float scopeRadius = Mathf.Max(24f, Mathf.Min(rect.width, rect.height) * 0.5f - 10f);
            Vector2 center = rect.center;
            Vector2 position = _normalizedPosition;
            if (position.sqrMagnitude <= 0.0001f)
            {
                position = Vector2.up * 0.035f;
            }

            Vector2 direction = position.normalized;
            Vector2 target = center + position * scopeRadius;
            Color tint = _withinRange
                ? _accent
                : new Color(1f, 0.58f, 0.18f, Mathf.Max(0.9f, _accent.a));

            float sizeScale = _preview ? 0.72f : 0.62f;
            float stemDarkWidth = _preview ? 3.2f : 3.6f;
            float stemWidth = _preview ? 1.15f : 1.05f;

            // A dark under-stroke protects the cue over dense sectional ink.
            AddLine(
                vertexHelper,
                center,
                target,
                stemDarkWidth,
                new Color(0.002f, 0.018f, 0.022f, 0.82f));
            AddLine(
                vertexHelper,
                center,
                target,
                stemWidth,
                new Color(tint.r, tint.g, tint.b, _preview ? 0.72f : 0.78f));

            // The diamond is deliberately asymmetrical (a small lead point)
            // so it reads as a selected navigation target rather than traffic.
            AddDiamond(
                vertexHelper,
                target,
                8.5f * sizeScale,
                new Color(tint.r, tint.g, tint.b, _preview ? 0.18f : 0.20f));
            AddDiamondOutline(
                vertexHelper,
                target,
                9.5f * sizeScale,
                _preview ? 1.15f : 1.25f,
                new Color(tint.r, tint.g, tint.b, _preview ? 0.92f : 1f));
            AddDisc(
                vertexHelper,
                target,
                _preview ? 1.55f : 1.7f,
                new Color(0.98f, 1f, 0.92f, 1f),
                16);

            float pulsePhase = Mathf.Repeat(_pulse, 1f);
            float pulseRadius = (_preview ? 8f : 9f) + pulsePhase * (_preview ? 7f : 8f);
            float pulseAlpha = (_preview ? 0.34f : 0.38f) * (1f - pulsePhase);
            AddRing(
                vertexHelper,
                target,
                pulseRadius,
                Mathf.Lerp(_preview ? 1.45f : 1.7f, _preview ? 0.55f : 0.65f, pulsePhase),
                new Color(tint.r, tint.g, tint.b, pulseAlpha),
                28);

            if (!_withinRange)
            {
                // Off-range targets stay represented at the perimeter with a
                // directional chevron, so a pilot never loses the bearing.
                Vector2 tip = center + direction * scopeRadius * 0.93f;
                Vector2 basePoint = tip - direction * (_preview ? 9f : 10f);
                Vector2 normal = new Vector2(-direction.y, direction.x);
                AddLine(vertexHelper, tip, basePoint + normal * (_preview ? 5f : 6f), _preview ? 1.35f : 1.6f, tint);
                AddLine(vertexHelper, tip, basePoint - normal * (_preview ? 5f : 6f), _preview ? 1.35f : 1.6f, tint);
            }
        }

        private static void AddDiamond(
            VertexHelper vertexHelper,
            Vector2 center,
            float radius,
            Color color)
        {
            int start = vertexHelper.currentVertCount;
            AddVertex(vertexHelper, center + Vector2.up * radius, color);
            AddVertex(vertexHelper, center + Vector2.right * radius, color);
            AddVertex(vertexHelper, center + Vector2.down * radius, color);
            AddVertex(vertexHelper, center + Vector2.left * radius, color);
            vertexHelper.AddTriangle(start, start + 1, start + 2);
            vertexHelper.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddDiamondOutline(
            VertexHelper vertexHelper,
            Vector2 center,
            float radius,
            float width,
            Color color)
        {
            Vector2 top = center + Vector2.up * radius;
            Vector2 right = center + Vector2.right * radius;
            Vector2 bottom = center + Vector2.down * radius;
            Vector2 left = center + Vector2.left * radius;
            AddLine(vertexHelper, top, right, width, color);
            AddLine(vertexHelper, right, bottom, width, color);
            AddLine(vertexHelper, bottom, left, width, color);
            AddLine(vertexHelper, left, top, width, color);
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
                float angle = Mathf.PI * 2f * i / segments;
                AddVertex(
                    vertexHelper,
                    center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius,
                    color);
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
                float angleA = Mathf.PI * 2f * i / segments;
                float angleB = Mathf.PI * 2f * (i + 1) / segments;
                Vector2 directionA = new Vector2(Mathf.Cos(angleA), Mathf.Sin(angleA));
                Vector2 directionB = new Vector2(Mathf.Cos(angleB), Mathf.Sin(angleB));
                AddQuad(
                    vertexHelper,
                    center + directionA * inner,
                    center + directionA * outer,
                    center + directionB * outer,
                    center + directionB * inner,
                    color);
            }
        }

        private static void AddLine(
            VertexHelper vertexHelper,
            Vector2 from,
            Vector2 to,
            float width,
            Color color)
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
