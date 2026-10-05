using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Shared runtime visual language for the FAA radar controls. The project
    /// builds these controls procedurally, so a generated sliced sprite keeps
    /// rounded corners crisp without introducing another binary UI asset.
    /// </summary>
    internal static class FaaRadarVisualStyle
    {
        public static readonly Color Glass = new Color(0.012f, 0.045f, 0.055f, 0.965f);
        public static readonly Color GlassRaised = new Color(0.024f, 0.090f, 0.105f, 0.975f);
        public static readonly Color GlassHover = new Color(0.035f, 0.155f, 0.170f, 1f);
        public static readonly Color GlassPressed = new Color(0.026f, 0.220f, 0.205f, 1f);
        public static readonly Color Accent = new Color(0.30f, 0.94f, 0.88f, 1f);
        public static readonly Color Stroke = new Color(0.36f, 0.94f, 0.88f, 0.26f);
        public static readonly Color TextPrimary = new Color(0.89f, 1f, 0.98f, 1f);
        public static readonly Color TextSecondary = new Color(0.56f, 0.84f, 0.82f, 1f);

        /// <summary>FAA minimum text size for radar UI in radar-canvas units (= FaaHudStyle.MinLabel reference units
        /// once <see cref="ReadabilityScale"/> is applied).</summary>
        public const float MinimumFont = FaaHudStyle.MinLabel;
        /// <summary>Largest compensation applied for a small world-space panel.</summary>
        public const float MaximumReadabilityScale = 2f;

        private const int TextureSize = 64;

        /// <summary>
        /// How much radar UI must be enlarged so one canvas unit renders at least one reference unit
        /// (1/1080 of the view height, the unit FaaHudStyle sizes are defined in). Screen-space canvases
        /// at the 1920x1080 reference return 1. World-space panels are measured from their angular size
        /// as seen straight on (unit length / camera distance), so the result does not depend on where the
        /// camera is currently looking. Never shrinks below 1.
        /// </summary>
        public static float ReadabilityScale(RectTransform rect)
        {
            if (rect == null) return 1f;
            Canvas canvas = rect.GetComponentInParent<Canvas>();
            if (canvas == null) return 1f;
            canvas = canvas.rootCanvas;
            float unit = Mathf.Abs(rect.lossyScale.y);
            if (unit <= 0f || float.IsNaN(unit)) return 1f;
            float ratio;
            if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
            {
                ratio = unit / (Mathf.Max(1, Screen.height) / 1080f);
            }
            else
            {
                Camera cam = canvas.worldCamera != null ? canvas.worldCamera : Camera.main;
                if (cam == null) return 1f;
                float distance = Vector3.Distance(cam.transform.position, rect.position);
                if (distance < .05f) return 1f;
                if (cam.stereoEnabled)
                {
                    // Head-mounted: the view angle IS the physical angle. Require a 16 arcmin cap height
                    // (HF-STD-001B) for the minimum font, assuming a 0.7 cap-height ratio.
                    float capArcmin = MinimumFont * .7f * unit / distance * 3437.75f;
                    ratio = capArcmin / 16f;
                }
                else
                {
                    // Desktop: one reference unit is 1/1080 of the rendered view height, measured at the camera's widest
                    // (un-zoomed) field of view. If the view zooms in to inspect a panel, the text is magnified instead of being
                    // shrunk back to the floor, and its size never pumps while the field of view eases.
                    float viewHeight = cam.orthographic ? cam.orthographicSize * 2f
                        : 2f * distance * Mathf.Tan(ReferenceFieldOfView(cam) * .5f * Mathf.Deg2Rad);
                    ratio = unit * 1080f / Mathf.Max(.0001f, viewHeight);
                }
            }
            return ratio <= .0001f ? MaximumReadabilityScale : Mathf.Clamp(1f / ratio, 1f, MaximumReadabilityScale);
        }

        private static Camera _fovCamera;
        private static float _widestFov;

        /// <summary>
        /// Widest vertical field of view seen for <paramref name="cam"/> (degrees). A temporary zoom-in (for example while a panel
        /// is inspected) does not lower it, so readability sizing follows the camera's normal view, not the magnified one.
        /// </summary>
        public static float ReferenceFieldOfView(Camera cam)
        {
            if (cam == null) return FaaInspectionFraming.DefaultVerticalFov;
            float fov = Mathf.Clamp(cam.fieldOfView, 5f, 170f);
            if (cam != _fovCamera) { _fovCamera = cam; _widestFov = fov; }
            else if (fov > _widestFov) _widestFov = fov;
            return _widestFov;
        }

        /// <summary>Quantised, hysteretic scale so head motion never makes text sizes pump.</summary>
        public static float StableScale(float current, float measured)
        {
            float quantised = Mathf.Round(Mathf.Clamp(measured, 1f, MaximumReadabilityScale) * 20f) / 20f;
            return Mathf.Abs(quantised - current) >= .06f || current < 1f ? quantised : current;
        }
        private static readonly Dictionary<int, Sprite> RoundedSprites = new Dictionary<int, Sprite>();

        public static void ApplyRounded(Image image, Color color, int radius = 12)
        {
            if (image == null)
            {
                return;
            }

            image.sprite = GetRoundedSprite(radius);
            image.type = Image.Type.Sliced;
            image.preserveAspect = false;
            image.color = color;
        }

        public static void ConfigureButton(Button button, Image image)
        {
            if (button == null || image == null)
            {
                return;
            }

            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;
            ColorBlock colors = button.colors;
            // Tint colors multiply the Image color. White keeps the authored
            // glass tone; the other values brighten it without a neon flash.
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.22f, 1.38f, 1.34f, 1f);
            colors.pressedColor = new Color(0.78f, 1.20f, 1.10f, 1f);
            colors.selectedColor = new Color(1.16f, 1.32f, 1.28f, 1f);
            colors.disabledColor = new Color(0.44f, 0.50f, 0.50f, 0.48f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.10f;
            button.colors = colors;
        }

        public static Shadow EnsureDropShadow(GameObject gameObject, Color color, Vector2 distance)
        {
            if (gameObject == null)
            {
                return null;
            }

            Shadow shadow = null;
            foreach (Shadow candidate in gameObject.GetComponents<Shadow>())
            {
                // Outline derives from Shadow; keep the two effects independent.
                if (candidate != null && candidate.GetType() == typeof(Shadow))
                {
                    shadow = candidate;
                    break;
                }
            }

            if (shadow == null)
            {
                shadow = gameObject.AddComponent<Shadow>();
            }

            shadow.effectColor = color;
            shadow.effectDistance = distance;
            shadow.useGraphicAlpha = true;
            return shadow;
        }

        private static Sprite GetRoundedSprite(int radius)
        {
            radius = Mathf.Clamp(radius, 4, TextureSize / 2 - 2);
            if (RoundedSprites.TryGetValue(radius, out Sprite cached) && cached != null)
            {
                return cached;
            }

            Texture2D texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false, true)
            {
                name = $"FAA Rounded Glass {radius}",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            Color32[] pixels = new Color32[TextureSize * TextureSize];
            float half = TextureSize * 0.5f;
            float innerExtent = half - radius;
            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    float px = Mathf.Abs(x + 0.5f - half) - innerExtent;
                    float py = Mathf.Abs(y + 0.5f - half) - innerExtent;
                    float outsideX = Mathf.Max(px, 0f);
                    float outsideY = Mathf.Max(py, 0f);
                    float signedDistance = Mathf.Sqrt(outsideX * outsideX + outsideY * outsideY) +
                                           Mathf.Min(Mathf.Max(px, py), 0f) - radius;
                    byte alpha = (byte)Mathf.RoundToInt(Mathf.Clamp01(0.75f - signedDistance) * 255f);
                    pixels[y * TextureSize + x] = new Color32(255, 255, 255, alpha);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float border = radius + 2f;
            Sprite sprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, TextureSize, TextureSize),
                new Vector2(0.5f, 0.5f),
                100f,
                0,
                SpriteMeshType.FullRect,
                new Vector4(border, border, border, border));
            sprite.name = $"FAA Rounded Glass {radius}";
            sprite.hideFlags = HideFlags.HideAndDontSave;
            RoundedSprites[radius] = sprite;
            return sprite;
        }
    }

    /// <summary>
    /// Small physical response shared by mouse, touch, and XR-ray controls.
    /// It deliberately uses unscaled time so UI feedback remains responsive
    /// while the simulator is paused.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class FaaRadarButtonMotion : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        ISelectHandler,
        IDeselectHandler
    {
        private bool _hovered;
        private bool _pressed;
        private bool _selected;
        private bool _reducedMotion;
        private float _hoverScale = 1.025f;

        public void Configure(bool reducedMotion, float hoverScale = 1.025f)
        {
            _reducedMotion = reducedMotion;
            _hoverScale = Mathf.Max(1f, hoverScale);
        }

        private void OnDisable()
        {
            _hovered = false;
            _pressed = false;
            _selected = false;
            transform.localScale = Vector3.one;
        }

        private void Update()
        {
            float target = _pressed ? 0.975f : (_hovered || _selected ? _hoverScale : 1f);
            float current = transform.localScale.x;
            // Idle buttons must not dirty their canvas every frame.
            if (Mathf.Abs(current - target) < .0005f)
            {
                if (current != target) transform.localScale = Vector3.one * target;
                return;
            }
            if (_reducedMotion)
            {
                transform.localScale = Vector3.one * target;
                return;
            }

            float next = Mathf.Lerp(current, target, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
            transform.localScale = Vector3.one * next;
        }

        public void OnPointerEnter(PointerEventData eventData) => _hovered = true;
        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            _pressed = false;
        }
        public void OnPointerDown(PointerEventData eventData) => _pressed = true;
        public void OnPointerUp(PointerEventData eventData) => _pressed = false;
        public void OnSelect(BaseEventData eventData) => _selected = true;
        public void OnDeselect(BaseEventData eventData) => _selected = false;
    }
}
