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
    /// Keeps the action-to-radar relationship discoverable for mouse, touch,
    /// keyboard, and XR ray pointers without making every connector equally
    /// prominent all the time.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class TrafficRadarActionFocus : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        ISelectHandler,
        IDeselectHandler
    {
        private TrafficRadarContextMenu _owner;
        private int _actionIndex;

        public void Configure(TrafficRadarContextMenu owner, int actionIndex)
        {
            _owner = owner;
            _actionIndex = actionIndex;
        }

        private void OnDisable()
        {
            _owner?.SetFocusedAction(_actionIndex, false);
        }

        public void OnPointerEnter(PointerEventData eventData) =>
            _owner?.SetFocusedAction(_actionIndex, true);

        public void OnPointerExit(PointerEventData eventData) =>
            _owner?.SetFocusedAction(_actionIndex, false);

        public void OnSelect(BaseEventData eventData) =>
            _owner?.SetFocusedAction(_actionIndex, true);

        public void OnDeselect(BaseEventData eventData) =>
            _owner?.SetFocusedAction(_actionIndex, false);
    }
}
