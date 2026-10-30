using TMPro;
using System.Collections.Generic;
using TrafficRadar;
using WeatherRadar;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization
{
    public enum FaaRadarKind
    {
        Weather,
        Traffic
    }


    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform), typeof(CanvasRenderer), typeof(Image))]
    [AddComponentMenu("FAA/Customization/FAA Radar Interaction Surface")]
    public sealed class FaaRadarInteractionSurface : MonoBehaviour,
        IPointerClickHandler,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerDownHandler,
        IPointerUpHandler,
        IBeginDragHandler,
        IDragHandler,
        IEndDragHandler,
        IScrollHandler
    {
        public const string WeatherObjectName = "FAAWeatherRadarInteractionSurface";
        public const string TrafficObjectName = "FAATrafficRadarInteractionSurface";

        private static readonly Color WeatherAccent = new Color(0.20f, 1f, 0.45f, 1f);
        private static readonly Color TrafficAccent = new Color(0.20f, 0.92f, 0.88f, 1f);

        [SerializeField] private FaaRadarControlsOverlay owner;
        [SerializeField] private FaaRadarKind radarKind;
        [SerializeField] private bool reducedMotion;

        private RectTransform _rectTransform;
        private CanvasGroup _focusGroup;
        private RectTransform _focusFrame;
        private TMP_Text _hintText;
        private bool _hovered;
        private bool _pressed;
        private bool _open;
        private bool _interactionEnabled = true;
        private bool _dragging;
        private bool _suppressClick;
        private TrafficRadarDisplay _trafficDisplay;
        private TrafficRadarContextMenu _trafficContextMenu;
        private XPlaneOriginalWeatherRadarDisplay _weatherDisplay;
        private float _visualProgress;
        private float _nextFrameFit;
        private readonly Vector3[] _corners = new Vector3[4];

        public FaaRadarKind RadarKind => radarKind;
        public bool IsOpen => _open;

        public void CloseContextMenu() => _trafficContextMenu?.Close();

        public void Configure(FaaRadarControlsOverlay sourceOwner, FaaRadarKind kind, bool useReducedMotion)
        {
            owner = sourceOwner;
            radarKind = kind;
            reducedMotion = useReducedMotion;
            EnsureVisualTree();
            EnsureTrafficContextMenu();
            UpdateHint();
        }

        public void SetOpen(bool open)
        {
            _open = open;
            UpdateHint();
        }

        public void SetInteractionEnabled(bool value)
        {
            if (!value)
            {
                // Disabling the surface (for example while leaving XR focus)
                // is equivalent to cancelling the pointer gesture. Unity may
                // not emit IEndDrag in that path, so explicitly restore the
                // chart and own-ship overlay before clearing local state.
                FinishTrafficMapDrag(null);
                _trafficContextMenu?.Close(true);
            }

            _interactionEnabled = value;
            Image hitArea = GetComponent<Image>();
            if (hitArea != null)
            {
                hitArea.raycastTarget = value;
            }

            if (!value)
            {
                _hovered = false;
                _pressed = false;
                _open = false;
            }

            enabled = true;
        }

        private void Awake()
        {
            EnsureVisualTree();
        }

        private void OnDisable()
        {
            // Pointer cancellation, scene reloads, and XR canvas mode changes
            // can disable this component without a matching pointer-up event.
            // Never leave the traffic chart panned or its own-ship glyph
            // suppressed across that lifecycle boundary.
            FinishTrafficMapDrag(null);
            _trafficContextMenu?.Close(true);
        }

        private void Update()
        {
            // A faint always-on edge reads as a lightweight glass instrument;
            // hover/open states strengthen the same frame without adding a box.
            bool contextMenuOpen = _trafficContextMenu != null && _trafficContextMenu.IsOpen;
            float target = !_interactionEnabled ? 0f : (_open || contextMenuOpen) ? 1f : _hovered ? 0.62f : 0.06f;
            float duration = reducedMotion ? 0.08f : target > _visualProgress ? 0.22f : 0.14f;
            _visualProgress = Mathf.MoveTowards(
                _visualProgress,
                target,
                Time.unscaledDeltaTime / Mathf.Max(0.01f, duration));

            if (_focusGroup != null)
            {
                _focusGroup.alpha = FaaRadarConfigurationDrawer.EaseOutQuart(_visualProgress);
            }

            if (radarKind == FaaRadarKind.Weather && Time.unscaledTime >= _nextFrameFit)
            {
                _nextFrameFit = Time.unscaledTime + .25f;
                FitWeatherFocusFrame();
            }

            if (_focusFrame != null)
            {
                float pressScale = _pressed && !reducedMotion ? 0.985f : 1f;
                float revealScale = reducedMotion ? 1f : Mathf.Lerp(0.985f, 1f, _visualProgress);
                _focusFrame.localScale = new Vector3(revealScale * pressScale, revealScale * pressScale, 1f);
            }

            // The target action intentionally leaves the quick menu open.
            // Refresh the small surface hint while it is open so the pilot
            // gets immediate confirmation that a navigation target is active.
            if (radarKind == FaaRadarKind.Traffic && (_open || contextMenuOpen))
            {
                UpdateHint();
            }

        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_interactionEnabled || _suppressClick)
            {
                _suppressClick = false;
                return;
            }

            if (radarKind == FaaRadarKind.Traffic)
            {
                ResolveTrafficDisplay();
                EnsureTrafficContextMenu();

                // While the coordinate dialog is open, map taps update its
                // uncommitted preview. They must not close the menu or commit
                // a destination implicitly.
                if (_trafficContextMenu != null && _trafficContextMenu.IsTargetSetupOpen &&
                    eventData.button == PointerEventData.InputButton.Left)
                {
                    _trafficContextMenu.HandleMapTapFromScreenPoint(
                        eventData.position,
                        eventData.pressEventCamera);
                    eventData.Use();
                    return;
                }

                if (eventData.button != PointerEventData.InputButton.Left)
                {
                    _suppressClick = false;
                    return;
                }

                if (_trafficDisplay == null || !_trafficDisplay.IsFullscreen)
                {
                    // One tap opens ONE control surface with one terminology: the readable
                    // settings drawer (RDR-13). The quick menu is a full-map tool only.
                    _trafficContextMenu?.Close();
                    owner?.ToggleRadarConfiguration(FaaRadarKind.Traffic);
                    eventData.Use();
                    return;
                }

                if (_trafficContextMenu != null)
                {
                    // Full map: the toolbar stays docked (Restore HUD, source, range); a tap
                    // offers only map-anchored actions (target setup, range rings).
                    _trafficContextMenu.ToggleAtScreenPoint(eventData.position, eventData.pressEventCamera);
                    eventData.Use();
                    return;
                }
            }

            if (eventData.button != PointerEventData.InputButton.Left)
            {
                _suppressClick = false;
                return;
            }

            owner?.ToggleRadarConfiguration(radarKind);
            eventData.Use();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_interactionEnabled)
            {
                return;
            }

            _hovered = true;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            _pressed = false;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_interactionEnabled && eventData.button == PointerEventData.InputButton.Left)
            {
                _pressed = true;
                _dragging = false;
                _suppressClick = false;
            }
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _pressed = false;
            // OnPointerUp is a safety net for input modules that do not send
            // IEndDrag after the pointer leaves the interaction surface.
            if (_dragging)
            {
                FinishTrafficMapDrag(eventData);
            }
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (!_interactionEnabled || radarKind != FaaRadarKind.Traffic ||
                eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            ResolveTrafficDisplay();
            if (_trafficDisplay == null || !_trafficDisplay.IsFullscreen ||
                !_trafficDisplay.MapPanningEnabled)
            {
                return;
            }

            _dragging = true;
            _suppressClick = true;
            _trafficDisplay.BeginMapDrag();
            eventData.Use();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_interactionEnabled || !_dragging || _trafficDisplay == null)
            {
                return;
            }

            // Pointer deltas are screen pixels; the map expects its own local
            // canvas units. Convert both points so scaled and XR canvases pan
            // with the pointer instead of amplifying or rotating the gesture.
            var mapRect = _trafficDisplay.transform as RectTransform;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRect,
                    eventData.position, eventData.pressEventCamera, out Vector2 currentPoint) ||
                !RectTransformUtility.ScreenPointToLocalPointInRectangle(mapRect,
                    eventData.position - eventData.delta, eventData.pressEventCamera, out Vector2 previousPoint))
                return;
            Vector2 delta = currentPoint - previousPoint;
            if (delta.sqrMagnitude < 0.0001f)
            {
                return;
            }

            _trafficDisplay.PanMap(delta);
            eventData.Use();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            FinishTrafficMapDrag(eventData);
        }

        private void FinishTrafficMapDrag(PointerEventData eventData)
        {
            bool wasDragging = _dragging;
            _dragging = false;
            _pressed = false;

            if (wasDragging)
            {
                // EndMapDrag recenters immediately, then redraws the own-ship
                // glyph. This keeps the restored marker aligned with the
                // aircraft instead of briefly showing it over a stale pan.
                _trafficDisplay?.EndMapDrag();
                eventData?.Use();
            }
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (!_interactionEnabled || Mathf.Approximately(eventData.scrollDelta.y, 0f))
            {
                return;
            }

            owner?.AdjustRadarSize(radarKind, eventData.scrollDelta.y);
            eventData.Use();
        }

        /// <summary>Padding between a framed picture and the focus frame edge (reference units).</summary>
        public const float FocusFramePadding = 8f;
        /// <summary>The bracket strokes sit this far inside the frame edge (see <see cref="EnsureVisualTree"/>).</summary>
        public const float BracketInset = 8f, BracketLength = 22f;
        /// <summary>Minimum clearance between the frame edge and the header/footer plates (reference units).</summary>
        public const float PlateClearance = 2f;

        /// <summary>
        /// Focus-frame rect (surface-local) for the weather picture: the picture plus <see cref="FocusFramePadding"/>, with its
        /// top kept below the header plate and its bottom kept above the footer plate, so a bracket never cuts through text
        /// (M12). <paramref name="headerBottom"/>/<paramref name="footerTop"/> are NaN when that plate is absent.
        /// </summary>
        public static Rect FitFocusFrame(Rect picture, float headerBottom, float footerTop)
        {
            float xMin = picture.xMin - FocusFramePadding, xMax = picture.xMax + FocusFramePadding;
            float yMin = picture.yMin - FocusFramePadding, yMax = picture.yMax + FocusFramePadding;
            if (!float.IsNaN(footerTop)) yMin = Mathf.Max(yMin, footerTop + PlateClearance);
            if (!float.IsNaN(headerBottom)) yMax = Mathf.Min(yMax, headerBottom - PlateClearance);
            // Never collapse: keep room for both bracket legs even on a degenerate layout.
            float minHeight = 2f * (BracketInset + BracketLength);
            if (yMax - yMin < minHeight) { float mid = (yMin + yMax) * .5f; yMin = mid - minHeight * .5f; yMax = mid + minHeight * .5f; }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        /// <summary>
        /// The weather picture is aspect-fitted (724:512) inside a square root. Frame the actual sector instead of the empty square
        /// so the brackets never float below the picture (RDR-16), and clamp the frame between the header and footer plates so the
        /// corner brackets never land on the range or tilt text (M12). Falls back to the root square only while no picture exists.
        /// </summary>
        private void FitWeatherFocusFrame()
        {
            if (_focusFrame == null || _rectTransform == null) return;
            if (_weatherDisplay == null && transform.parent != null)
                _weatherDisplay = transform.parent.GetComponentInChildren<XPlaneOriginalWeatherRadarDisplay>(true);
            RectTransform picture = _weatherDisplay != null ? _weatherDisplay.PictureRect : null;
            if (picture == null || !picture.gameObject.activeInHierarchy || picture.rect.width < 16f)
            {
                Stretch(_focusFrame);
                return;
            }
            Rect pictureRect = LocalBounds(picture);
            Transform root = transform.parent;
            float headerBottom = float.NaN, footerTop = float.NaN;
            if (root != null)
            {
                if (root.Find("Radar Status Header") is RectTransform header && header.gameObject.activeInHierarchy) headerBottom = LocalBounds(header).yMin;
                if (root.Find("Radar Status Footer") is RectTransform footer && footer.gameObject.activeInHierarchy) footerTop = LocalBounds(footer).yMax;
            }
            Rect frame = FitFocusFrame(pictureRect, headerBottom, footerTop);
            Vector2 size = frame.size;
            Vector2 centre = frame.center - _rectTransform.rect.center;
            var middle = new Vector2(.5f, .5f);
            if (_focusFrame.anchorMin != middle) _focusFrame.anchorMin = middle;
            if (_focusFrame.anchorMax != middle) _focusFrame.anchorMax = middle;
            if (_focusFrame.pivot != middle) _focusFrame.pivot = middle;
            if ((_focusFrame.sizeDelta - size).sqrMagnitude > .25f) _focusFrame.sizeDelta = size;
            if ((_focusFrame.anchoredPosition - centre).sqrMagnitude > .25f) _focusFrame.anchoredPosition = centre;
        }

        /// <summary>Axis-aligned bounds of <paramref name="rect"/> in this surface's local space (allocation-free).</summary>
        private Rect LocalBounds(RectTransform rect)
        {
            rect.GetWorldCorners(_corners);
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            for (int i = 0; i < 4; i++)
            {
                Vector2 local = _rectTransform.InverseTransformPoint(_corners[i]);
                min = Vector2.Min(min, local); max = Vector2.Max(max, local);
            }
            return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
        }

        private void ResolveTrafficDisplay()
        {
            if (_trafficDisplay != null)
            {
                return;
            }

            // The interaction surface is a sibling of Radar Display beneath
            // the same Traffic Radar System root.  Resolving locally avoids
            // accidentally binding to the hidden legacy radar duplicate.
            Transform systemRoot = transform.parent;
            if (systemRoot != null)
            {
                TrafficRadarDisplay[] candidates =
                    systemRoot.GetComponentsInChildren<TrafficRadarDisplay>(true);
                for (int i = 0; i < candidates.Length; i++)
                {
                    if (candidates[i] != null && candidates[i].gameObject.activeInHierarchy)
                    {
                        _trafficDisplay = candidates[i];
                        break;
                    }
                }

                if (_trafficDisplay == null && candidates.Length > 0)
                {
                    _trafficDisplay = candidates[0];
                }
            }

            if (_trafficDisplay == null)
            {
                _trafficDisplay = FindAnyObjectByType<TrafficRadarDisplay>();
                if (_trafficDisplay == null)
                {
                    _trafficDisplay = FindAnyObjectByType<TrafficRadarDisplay>(FindObjectsInactive.Include);
                }
            }
        }

        private void EnsureTrafficContextMenu()
        {
            if (radarKind != FaaRadarKind.Traffic)
            {
                return;
            }

            ResolveTrafficDisplay();
            if (_trafficDisplay == null)
            {
                return;
            }

            _trafficContextMenu = GetComponent<TrafficRadarContextMenu>() ??
                                  gameObject.AddComponent<TrafficRadarContextMenu>();
            _trafficContextMenu.Configure(_trafficDisplay, reducedMotion);
        }

        private void EnsureVisualTree()
        {
            _rectTransform = GetComponent<RectTransform>();
            Image hitArea = GetComponent<Image>() ?? gameObject.AddComponent<Image>();
            hitArea.color = new Color(0f, 0f, 0f, 0.002f);
            hitArea.raycastTarget = _interactionEnabled;

            Transform existingFrame = transform.Find("RadarFocusFrame");
            GameObject frameObject = existingFrame != null
                ? existingFrame.gameObject
                : new GameObject("RadarFocusFrame", typeof(RectTransform), typeof(CanvasGroup));
            frameObject.transform.SetParent(transform, false);
            _focusFrame = frameObject.GetComponent<RectTransform>();
            // The weather frame hugs the sector picture; re-stretching it here (this runs on every structure refresh) used to
            // leave the brackets on the square root, right across the footer text, until the next fit.
            if (radarKind == FaaRadarKind.Weather) FitWeatherFocusFrame();
            else Stretch(_focusFrame);
            _focusGroup = frameObject.GetComponent<CanvasGroup>() ?? frameObject.AddComponent<CanvasGroup>();
            _focusGroup.alpha = 0f;
            _focusGroup.blocksRaycasts = false;
            _focusGroup.interactable = false;

            Color accent = radarKind == FaaRadarKind.Weather ? WeatherAccent : TrafficAccent;
            DisableLegacyEdge(_focusFrame, "TopEdge");
            DisableLegacyEdge(_focusFrame, "BottomEdge");
            DisableLegacyEdge(_focusFrame, "LeftEdge");
            DisableLegacyEdge(_focusFrame, "RightEdge");
            EnsureCornerSegment(_focusFrame, "TopLeftHorizontal", new Vector2(0f, 1f), new Vector2(19f, -8f), new Vector2(22f, 2f), accent);
            EnsureCornerSegment(_focusFrame, "TopLeftVertical", new Vector2(0f, 1f), new Vector2(8f, -19f), new Vector2(2f, 22f), accent);
            EnsureCornerSegment(_focusFrame, "TopRightHorizontal", new Vector2(1f, 1f), new Vector2(-19f, -8f), new Vector2(22f, 2f), accent);
            EnsureCornerSegment(_focusFrame, "TopRightVertical", new Vector2(1f, 1f), new Vector2(-8f, -19f), new Vector2(2f, 22f), accent);
            EnsureCornerSegment(_focusFrame, "BottomLeftHorizontal", Vector2.zero, new Vector2(19f, 8f), new Vector2(22f, 2f), accent);
            EnsureCornerSegment(_focusFrame, "BottomLeftVertical", Vector2.zero, new Vector2(8f, 19f), new Vector2(2f, 22f), accent);
            EnsureCornerSegment(_focusFrame, "BottomRightHorizontal", new Vector2(1f, 0f), new Vector2(-19f, 8f), new Vector2(22f, 2f), accent);
            EnsureCornerSegment(_focusFrame, "BottomRightVertical", new Vector2(1f, 0f), new Vector2(-8f, 19f), new Vector2(2f, 22f), accent);

            Transform existingHint = _focusFrame.Find("ConfigureHint");
            GameObject hintObject = existingHint != null
                ? existingHint.gameObject
                : new GameObject("ConfigureHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            hintObject.transform.SetParent(_focusFrame, false);
            // The animated outline is the interaction affordance. Keeping a rectangular
            // hint over the open radar obscures its mode and bearing symbology, while the
            // newly revealed configuration/conditions panels already communicate state.
            hintObject.SetActive(false);
            RectTransform hintRect = hintObject.GetComponent<RectTransform>();
            hintRect.anchorMin = new Vector2(0.5f, 1f);
            hintRect.anchorMax = new Vector2(0.5f, 1f);
            hintRect.pivot = new Vector2(0.5f, 1f);
            hintRect.anchoredPosition = new Vector2(0f, -8f);
            hintRect.sizeDelta = new Vector2(188f, 34f);
            Image hintBackground = hintObject.GetComponent<Image>() ?? hintObject.AddComponent<Image>();
            hintBackground.color = new Color(0.004f, 0.035f, 0.026f, 0.96f);
            hintBackground.raycastTarget = false;
            Outline outline = hintObject.GetComponent<Outline>() ?? hintObject.AddComponent<Outline>();
            outline.effectColor = new Color(accent.r, accent.g, accent.b, 0.88f);
            outline.effectDistance = new Vector2(1f, -1f);

            Transform existingText = hintRect.Find("Label");
            GameObject textObject = existingText != null
                ? existingText.gameObject
                : new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer));
            textObject.transform.SetParent(hintRect, false);
            RectTransform textRect = textObject.GetComponent<RectTransform>();
            Stretch(textRect);
            _hintText = textObject.GetComponent<TextMeshProUGUI>() ?? textObject.AddComponent<TextMeshProUGUI>();
            _hintText.fontSize = 14f;
            _hintText.fontStyle = FontStyles.Bold;
            _hintText.enableAutoSizing = false;
            _hintText.extraPadding = true;
            _hintText.alignment = TextAlignmentOptions.Center;
            _hintText.textWrappingMode = TextWrappingModes.NoWrap;
            _hintText.color = new Color(0.82f, 1f, 0.87f, 1f);
            _hintText.raycastTarget = false;
            UpdateHint();
        }

        private void UpdateHint()
        {
            if (_hintText == null)
            {
                return;
            }

            if (radarKind == FaaRadarKind.Weather)
            {
                _hintText.text = _open ? "WX CONDITIONS OPEN" : "CLICK · WX CONDITIONS";
                return;
            }

            bool targetActive = _trafficDisplay != null && _trafficDisplay.HasNavigationTarget;
            _hintText.text = _open
                ? targetActive
                    ? "TRAFFIC CONFIG · TARGET ACTIVE"
                    : "TRAFFIC CONFIG · SELECT ACTION"
                : "TAP · CONTROLS  ·  FULL MAP FOR TARGET";
        }

        private static void DisableLegacyEdge(RectTransform parent, string name)
        {
            Transform existing = parent.Find(name);
            if (existing != null)
            {
                existing.gameObject.SetActive(false);
            }
        }

        private static void EnsureCornerSegment(
            RectTransform parent,
            string name,
            Vector2 anchor,
            Vector2 anchoredPosition,
            Vector2 size,
            Color color)
        {
            Transform existing = parent.Find(name);
            GameObject edgeObject = existing != null
                ? existing.gameObject
                : new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            edgeObject.transform.SetParent(parent, false);
            edgeObject.SetActive(true);
            RectTransform edge = edgeObject.GetComponent<RectTransform>();
            edge.anchorMin = anchor;
            edge.anchorMax = anchor;
            edge.pivot = new Vector2(0.5f, 0.5f);
            edge.anchoredPosition = anchoredPosition;
            edge.sizeDelta = size;
            edge.localScale = Vector3.one;
            edge.localRotation = Quaternion.identity;
            Image image = edgeObject.GetComponent<Image>() ?? edgeObject.AddComponent<Image>();
            image.color = new Color(color.r, color.g, color.b, 0.82f);
            image.raycastTarget = false;
        }

        private static void Stretch(RectTransform rectTransform)
        {
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.offsetMin = Vector2.zero;
            rectTransform.offsetMax = Vector2.zero;
            rectTransform.localScale = Vector3.one;
            rectTransform.localRotation = Quaternion.identity;
        }
    }
}
