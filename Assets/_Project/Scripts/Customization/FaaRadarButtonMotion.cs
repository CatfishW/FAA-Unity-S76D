using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization
{
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
            if (_reducedMotion)
            {
                transform.localScale = Vector3.one * target;
                return;
            }

            float current = transform.localScale.x;
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
