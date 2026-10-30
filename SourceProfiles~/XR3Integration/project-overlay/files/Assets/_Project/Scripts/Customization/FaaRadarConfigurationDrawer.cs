using TMPro;
using System.Collections.Generic;
using TrafficRadar;
using WeatherRadar;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(CanvasGroup))]
    [AddComponentMenu("FAA/Customization/FAA Radar Configuration Drawer")]
    public sealed class FaaRadarConfigurationDrawer : MonoBehaviour
    {
        private const float OpenDuration = 0.24f;
        private const float CloseDuration = 0.16f;
        private const float HiddenScale = 0.94f;

        private RectTransform _rectTransform;
        private CanvasGroup _canvasGroup;
        private bool _targetVisible;
        private bool _reducedMotion;
        private bool _initialized;
        private float _progress;
        private readonly List<CanvasGroup> _contentGroups = new List<CanvasGroup>();
        private readonly List<RectTransform> _contentRows = new List<RectTransform>();

        public bool TargetVisible => _targetVisible;
        public float Progress => _progress;
        private float _baseScale = 1f;

        /// <summary>Readability scale (FaaRadarVisualStyle.ReadabilityScale) the drawer animates around.</summary>
        public float BaseScale
        {
            get => _baseScale;
            set
            {
                float next = Mathf.Clamp(value, 1f, FaaRadarVisualStyle.MaximumReadabilityScale);
                if (Mathf.Abs(next - _baseScale) < .001f) return;
                _baseScale = next;
                Apply(_progress);
            }
        }

        // Keep the original one-argument API for existing scene scripts and
        // reflection-based editor tests. Advanced rows are opt-in through the
        // explicitly named overload below, avoiding an ambiguous Configure()
        // method lookup at runtime.
        public void Configure(bool reducedMotion)
        {
            ConfigureInternal(reducedMotion, null);
        }

        /// <summary>
        /// Configure the drawer and optionally scope its fade/interactable
        /// state to advanced rows.  The primary row remains visible so the
        /// compact summary and FULL/REST escape button are always usable.
        /// </summary>
        public void ConfigureWithContentRows(bool reducedMotion, params RectTransform[] contentRows)
        {
            ConfigureInternal(reducedMotion, contentRows);
        }

        private void ConfigureInternal(bool reducedMotion, RectTransform[] contentRows)
        {
            _reducedMotion = reducedMotion;
            EnsureReferences();
            SetContentRows(contentRows);
            if (!_initialized)
            {
                _initialized = true;
                Snap(false);
            }
        }

        public void SetContentRows(params RectTransform[] contentRows)
        {
            _contentGroups.Clear();
            _contentRows.Clear();

            if (contentRows == null)
            {
                return;
            }

            foreach (RectTransform row in contentRows)
            {
                if (row == null || row == transform)
                {
                    continue;
                }

                CanvasGroup group = row.GetComponent<CanvasGroup>() ?? row.gameObject.AddComponent<CanvasGroup>();
                _contentRows.Add(row);
                _contentGroups.Add(group);
            }
        }

        public void SetVisible(bool visible, bool immediate = false)
        {
            EnsureReferences();
            if (!_initialized)
            {
                _initialized = true;
                Snap(false);
            }

            if (_targetVisible == visible && !immediate)
            {
                if (!Mathf.Approximately(_progress, visible ? 1f : 0f))
                {
                    enabled = true;
                }

                return;
            }

            _targetVisible = visible;
            if (immediate)
            {
                Snap(visible);
                return;
            }

            enabled = true;
        }

        private void Awake()
        {
            EnsureReferences();
        }

        private void Update()
        {
            float target = _targetVisible ? 1f : 0f;
            float duration = _targetVisible ? OpenDuration : CloseDuration;
            if (_reducedMotion)
            {
                duration = Mathf.Min(duration, 0.08f);
            }

            _progress = Mathf.MoveTowards(_progress, target, Time.unscaledDeltaTime / Mathf.Max(0.01f, duration));
            Apply(_progress);
            if (Mathf.Approximately(_progress, target))
            {
                enabled = false;
            }
        }

        private void EnsureReferences()
        {
            _rectTransform = GetComponent<RectTransform>();
            _canvasGroup = GetComponent<CanvasGroup>() ?? gameObject.AddComponent<CanvasGroup>();
        }

        private void Snap(bool visible)
        {
            _targetVisible = visible;
            _progress = visible ? 1f : 0f;
            Apply(_progress);
            enabled = false;
        }

        private void Apply(float progress)
        {
            if (_canvasGroup == null || _rectTransform == null)
            {
                return;
            }

            float eased = EaseOutQuart(Mathf.Clamp01(progress));

            if (_contentGroups.Count > 0)
            {
                // Keep the strip shell and primary row live.  Only the
                // secondary/tertiary configuration rows participate in the
                // drawer animation; otherwise CanvasGroup on the strip would
                // also hide FULL/REST and leave a focused pilot without an
                // on-screen exit affordance.
                _canvasGroup.alpha = 1f;
                _canvasGroup.interactable = true;
                _canvasGroup.blocksRaycasts = true;
                _rectTransform.localScale = new Vector3(_baseScale, _baseScale, 1f);
                bool contentInteractive = _targetVisible && progress >= 0.98f;
                bool contentRaycasts = _targetVisible && progress >= 0.12f;
                for (int i = 0; i < _contentGroups.Count; i++)
                {
                    CanvasGroup group = _contentGroups[i];
                    if (group != null)
                    {
                        group.alpha = eased;
                        group.interactable = contentInteractive;
                        group.blocksRaycasts = contentRaycasts;
                    }

                    RectTransform row = i < _contentRows.Count ? _contentRows[i] : null;
                    if (row != null)
                    {
                        float rowScale = _reducedMotion ? 1f : Mathf.Lerp(HiddenScale, 1f, eased);
                        row.localScale = new Vector3(rowScale, rowScale, 1f);
                    }
                }

                return;
            }

            _canvasGroup.alpha = eased;
            _canvasGroup.interactable = _targetVisible && progress >= 0.98f;
            _canvasGroup.blocksRaycasts = _targetVisible && progress >= 0.12f;
            float scale = _baseScale * (_reducedMotion ? 1f : Mathf.Lerp(HiddenScale, 1f, eased));
            _rectTransform.localScale = new Vector3(scale, scale, 1f);
        }

        public static float EaseOutQuart(float value)
        {
            float inverse = 1f - Mathf.Clamp01(value);
            return 1f - inverse * inverse * inverse * inverse;
        }
    }
}
