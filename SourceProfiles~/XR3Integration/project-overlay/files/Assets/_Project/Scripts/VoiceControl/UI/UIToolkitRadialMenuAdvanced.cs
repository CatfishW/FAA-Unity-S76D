using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using VoiceControl.Core;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace VoiceControl.UI
{
    /// <summary>
    /// Pilot COMMANDS menu. In Play Mode (pilot chrome present) it is the COMMANDS flyout of the chrome bar: the rows built
    /// by <see cref="BuildCommandRows"/> are drawn by FaaPilotChrome in the fixed left flyout slot above the bar, outside the
    /// attitude field and the IAS/TQ column, with each function's live state as ON/OFF segments. It never hides, dims or
    /// covers flight symbology (no full-screen backdrop) and closes with Tab, Esc, CLOSE or a click outside it.
    /// The UI Toolkit radial wheel below is only the edit-time preview / no-chrome fallback; it uses the same rows, chrome
    /// typography floors and ALL CAPS names, and also never touches flight symbology.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    [RequireComponent(typeof(UIDocument))]
    [AddComponentMenu("Voice Control/UI Toolkit/Advanced Radial Menu")]
    [ExecuteInEditMode]
    public class UIToolkitRadialMenuAdvanced : MonoBehaviour
    {
        [Header("UI Document")]
        [SerializeField] private UIDocument uiDocument;

        [Header("Menu Structure")]
        [SerializeField] private bool applyAviationHudPresetOnAwake = true;
        [SerializeField] private int mainSegmentCount = 4;
        [SerializeField] private float innerRadius = 128f;
        [SerializeField] private float middleRadius = 275f;
        [SerializeField] private float outerRadius = 390f;
        [SerializeField] private bool enableSubMenus = true;
        [SerializeField] private bool startCollapsed = true;
        [SerializeField] private float collapsedButtonSize = 58f;
        [SerializeField] private Vector2 collapsedButtonPosition = new Vector2(34f, 34f);
        [SerializeField] private bool collapsedButtonTopRight = true;
        [Tooltip("In Play Mode the launcher is a labelled COMMANDS button (key Tab) in the FAA pilot chrome bar instead of a floating disc in the top-right corner.")]
        [SerializeField] private bool dockLauncherInPilotChrome = true;
        [SerializeField, Range(4, 8)] private int maxSubSegmentCount = 6;

        [Header("Input")]
        [SerializeField] private KeyCode toggleKey = KeyCode.Tab;
        [SerializeField] private KeyCode closeKey = KeyCode.Escape;
        [SerializeField] private bool useMouseWheel = true;
        [SerializeField] private bool useGestures = true;
        [SerializeField] private float gestureSensitivity = 1.5f;

        [Header("Animation")]
        [SerializeField] private float openDuration = 0.28f;
        [SerializeField] private float closeDuration = 0.18f;
        [SerializeField] private float subMenuExpandDuration = 0.18f;
        [SerializeField] private AnimationCurve springCurve;
        [SerializeField] private AnimationCurve bounceCurve;
        [SerializeField] private bool reducedMotion = false;
        [SerializeField, Range(0.01f, 0.08f)] private float mainSegmentStagger = 0.025f;
        [SerializeField, Range(0.01f, 0.08f)] private float subSegmentStagger = 0.02f;
        [SerializeField, Range(0f, 0.14f)] private float hoverScaleBoost = 0.06f;

        [Header("Visual Effects")]
        [SerializeField] private bool useRippleEffect = true;
        [SerializeField] private bool usePulseAnimation = true;
        [SerializeField] private bool useGradientBackground = true;
        [SerializeField] private float rotationSpeed = 10f;
        [SerializeField, Range(0.3f, 1f)] private float menuTransparency = 0.98f;
        [SerializeField, Range(0.5f, 1f)] private float ringBackgroundTransparency = 0.78f;
        [SerializeField, Range(0.5f, 1f)] private float segmentTransparency = 0.98f;
        [SerializeField, Range(0.7f, 1f)] private float centerTransparency = 0.99f;

        [Header("Backdrop")]
        [SerializeField] private bool useBackdrop = true;
        [SerializeField, Range(0f, 0.65f)] private float backdropOpacity = 0.30f;
        [SerializeField] private bool closeOnBackdropClick = true;

        [Header("HUD Suppression (retired)")]
        [Tooltip("Ignored and forced off at runtime: the COMMANDS menu never hides flight symbology (AC 25-11B: primary flight information is never removed by a menu).")]
        [SerializeField] private bool hideHudWhileOpen = false;

        [Header("Audio Feedback")]
        [SerializeField] private AudioClip openSound;
        [SerializeField] private AudioClip closeSound;
        [SerializeField] private AudioClip selectSound;
        [SerializeField] private AudioClip executeSound;

        private const float MainSegmentWidth = 144f;
        private const float MainSegmentHeight = 76f;
        private const float SubSegmentWidth = 264f;
        private const float SubSegmentHeight = 54f;
        private const float MainIconContainerSize = 30f;
        private const float MainIconSize = 26f;
        private const float SubIconContainerSize = 32f;
        private const float SubIconSize = 24f;

        [Header("Typography (chrome reference units, 1920x1080; never below FaaHudStyle.Chrome)")]
        [SerializeField] private float mainLabelFontSize = 16f;
        [SerializeField] private float subLabelFontSize = 16f;
        [SerializeField] private float centerTitleFontSize = 18f;
        [SerializeField] private float centerSubtitleFontSize = 16f;
        // FAA-inspired night-cockpit palette: dark blue-green surfaces keep
        // outside-world contrast while cyan/emerald accents make the active
        // command obvious without a distracting glow.
        private static readonly Color PanelBackgroundColor = new Color(0.008f, 0.035f, 0.047f, 0.985f);
        private static readonly Color SegmentBackgroundColor = new Color(0.012f, 0.078f, 0.084f, 0.985f);
        private static readonly Color SegmentBorderColor = new Color(0.14f, 0.78f, 0.73f, 0.58f);
        private static readonly Color SubBorderBaseColor = new Color(0.20f, 0.72f, 0.72f, 0.38f);
        private const float CenterSizePadding = 54f;

        // Events
        public event Action<MenuCommand> OnCommandExecuted;
        public event Action<string> OnCategoryChanged;
        public event Action OnMenuOpened;
        public event Action OnMenuClosed;

        // UI Elements
        private VisualElement _root;
        private VisualElement _scrim;
        private VisualElement _menuRoot;
        private VisualElement _collapsedButton;  // Small circular button when collapsed
        private VisualElement _collapsedIcon;
        private VisualElement _ringBackground;
        private VisualElement _centerInfo;
        private Label _centerTitle;
        private Label _centerSubtitle;
        private Label _commandHeading;
        private Label _menuHint;
        private VisualElement _rippleContainer;
        private VisualElement _gestureIndicator;
        private VisualElement _builtRoot;

        // Menu state
        private List<MainSegment> _mainSegments = new List<MainSegment>();
        private List<SubSegment> _subSegments = new List<SubSegment>();
        private List<MenuCategory> _categories = new List<MenuCategory>();
        private int _selectedMainIndex = -1;
        private int _selectedSubIndex = -1;
        private bool _isOpen;
        private bool _isAnimating;
        private bool _subMenuOpen;
        private float _openProgress;
        private float _subMenuProgress;
        private float _rotationOffset;
        private Vector2 _lastMousePos;
        private float _gestureAccumulator;
        private bool _isLoadingCommands; // Prevent recursive LoadCommands calls
        private bool _uiBuilt;

        // Play Mode: the COMMANDS flyout of the pilot chrome is open (the UI Toolkit wheel stays hidden).
        private bool _dockedOpen;
        private MenuKeepOut _keepOut;
        // Scene references for command state, resolved once each time the menu opens (never per frame).
        private Canvas _weatherRadarCanvas, _trafficRadarCanvas;
        private IndicatorSystem.Controller.IndicatorSystemController _cueController;
        private FAA.Customization.SymbologyColorManager _symbologyColor;
        private FAA.Customization.FaaHudOpacityController _hudOpacity;

#if UNITY_EDITOR
        private bool _editorRefreshQueued;
#endif

        // Section names match the chrome bar and key list (CUES = SCREEN CUES).
        public const string SectionRadars = "RADARS", SectionCues = "SCREEN CUES", SectionHud = "HUD", SectionImageAnalysis = "AI IMAGE ANALYSIS";
        public const string WeatherRadarCanvasName = "XPlaneWeatherRadarCanvas", TrafficRadarCanvasName = "XPlaneTrafficRadarCanvas";
        private static readonly int[] BrightnessPresets = { 40, 60, 80, 100 };
        private static readonly string[] BrightnessLabels = { "40", "60", "80", "100" };

        // Category definitions for voice control commands - using FAA-styled icon paths
        private readonly Dictionary<string, (string iconPath, Color color)> _categoryDefs = new()
        {
            { "radar", (iconPath: "VoiceControl/IconsSvg/WeatherRadar", color: new Color(0.35f, 0.7f, 1f)) },
            { "indicator_system", (iconPath: "VoiceControl/IconsSvg/IndicatorSystem", color: new Color(0.3f, 0.8f, 0.5f)) },
            { "hud", (iconPath: "VoiceControl/IconsSvg/Symbology", color: new Color(0.35f, 0.9f, 0.55f)) },
            { "visionbriefing", (iconPath: "VoiceControl/IconsSvg/VisionBriefing", color: new Color(1f, 0.8f, 0.3f)) }
        };

        [Serializable]
        public class MenuCommand
        {
            public string Id;
            public string TargetId;
            public string CommandName;
            public string DisplayName;
            public string Description;
            public string Category;
            public string IconPath;
            public Color Color;
            public bool RequiresParams;
            public Dictionary<string, object> DefaultParams;
            /// <summary>Live state text shown after the name ("ON", "OFF", "80"); null for one-shot commands.</summary>
            [NonSerialized] public Func<string> StateText;
            /// <summary>Local action (toggle rows). When set, the menu stays open so the new state is visible.</summary>
            [NonSerialized] public Action Execute;
        }

        [Serializable]
        public class MenuCategory
        {
            public string Id;
            public string DisplayName;
            public string Icon;
            public Color Color;
            public List<MenuCommand> Commands = new List<MenuCommand>();
        }

        private class MainSegment
        {
            public VisualElement Container;
            public VisualElement Background;
            public VisualElement IconContainer;
            public VisualElement IconImage;  // Changed from Label to VisualElement for texture
            public Label NameLabel;
            public int Index;
            public float Angle;
            public MenuCategory Category;
            public bool IsHovered;
        }

        private class SubSegment
        {
            public VisualElement Container;
            public VisualElement Background;
            public VisualElement IconContainer;
            public VisualElement IconImage;
            public Label NameLabel;
            public int Index;
            public float Angle;
            public MenuCommand Command;
            public bool IsVisible;
        }

        private class Ripple
        {
            public VisualElement Element;
            public float Progress;
            public float Speed;
            public Color Color;
        }

        /// <summary>Chrome keep-out of the fallback wheel while it is open (conformal lines and edge cues avoid it).</summary>
        private sealed class MenuKeepOut : FAA.Customization.FaaHudKeepOut.IRegion
        {
            private readonly UIToolkitRadialMenuAdvanced owner;
            public MenuKeepOut(UIToolkitRadialMenuAdvanced menu) { owner = menu; }
            public string Id => "chrome:commands-wheel";
            public FAA.Customization.FaaKeepOutKind Kind => FAA.Customization.FaaKeepOutKind.Chrome;
            public bool TryGetScreenRect(out Rect rect) { rect = default; return owner != null && owner.TryGetWheelScreenRect(out rect); }
        }

        private List<Ripple> _ripples = new List<Ripple>();

        private void Awake()
        {
            if (applyAviationHudPresetOnAwake)
            {
                ApplyAviationHudPreset(false);
            }

            // Overrides the serialized scene value (ExperimentScene still stores 1): never hide flight symbology.
            hideHudWhileOpen = false;

            if (uiDocument == null)
                uiDocument = GetComponent<UIDocument>();

            InitializeCurves();
        }

        private void InitializeCurves()
        {
            if (springCurve == null || springCurve.length == 0)
            {
                springCurve = new AnimationCurve(
                    new Keyframe(0, 0, 0, 2),
                    new Keyframe(0.5f, 1.1f, 0, 0),
                    new Keyframe(1, 1, -0.5f, 0)
                );
            }

            if (bounceCurve == null || bounceCurve.length == 0)
            {
                bounceCurve = new AnimationCurve(
                    new Keyframe(0, 0, 0, 0),
                    new Keyframe(0.4f, 1.05f, 0, 0),
                    new Keyframe(0.7f, 0.95f, 0, 0),
                    new Keyframe(1, 1, 0, 0)
                );
            }
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            return new Color(color.r, color.g, color.b, alpha);
        }

        private static void SetBorderColor(VisualElement element, Color color)
        {
            if (element == null) return;

            element.style.borderTopColor = color;
            element.style.borderBottomColor = color;
            element.style.borderLeftColor = color;
            element.style.borderRightColor = color;
        }

        private static void SetBorderWidth(VisualElement element, float width)
        {
            if (element == null) return;

            element.style.borderTopWidth = width;
            element.style.borderBottomWidth = width;
            element.style.borderLeftWidth = width;
            element.style.borderRightWidth = width;
        }

        private static void SetRadius(VisualElement element, float radius)
        {
            if (element == null) return;

            element.style.borderTopLeftRadius = radius;
            element.style.borderTopRightRadius = radius;
            element.style.borderBottomLeftRadius = radius;
            element.style.borderBottomRightRadius = radius;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Hot-reload: refresh UI when properties change in editor
            if (!Application.isPlaying && uiDocument != null && !_editorRefreshQueued)
            {
                _editorRefreshQueued = true;
                EditorApplication.delayCall += () =>
                {
                    _editorRefreshQueued = false;
                    if (this != null && uiDocument != null)
                    {
                        RefreshUI();
                    }
                };
            }
        }

        /// <summary>
        /// Refreshes the UI tree without forcing the edit-mode preview open.
        /// </summary>
        [ContextMenu("Refresh UI")]
        public void RefreshUI()
        {
            RefreshUI(false);
        }

        public void RefreshUI(bool showPreview)
        {
            if (uiDocument == null)
                uiDocument = GetComponent<UIDocument>();

            // Clean up existing UI
            CleanupUI();

            // Rebuild UI
            SetupUI();

            if (!Application.isPlaying)
            {
                if (showPreview)
                {
                    ShowEditorPreview();
                }
                else
                {
                    HideEditorPreview();
                }
            }
        }

        private void CleanupUI()
        {
            if (_root == null) return;

            // Remove all dynamically created elements
            _menuRoot?.RemoveFromHierarchy();
            _collapsedButton?.RemoveFromHierarchy();
            _scrim?.RemoveFromHierarchy();

            _mainSegments.Clear();
            _subSegments.Clear();
            _ripples.Clear();
            _scrim = null;
            _uiBuilt = false;
            _builtRoot = null;
        }

        public void ShowEditorPreview()
        {
            if (_menuRoot == null)
            {
                SetupUI();
            }

            if (_menuRoot == null)
            {
                return;
            }

            // In edit mode, show the menu open for preview
            _menuRoot.style.display = DisplayStyle.Flex;
            _openProgress = 1f;
            _isOpen = true;

            // Position segments for preview
            UpdateAnimations();

            // Hide collapsed button when showing preview
            if (_collapsedButton != null)
            {
                _collapsedButton.style.display = DisplayStyle.None;
            }
        }

        public void HideEditorPreview()
        {
            _isOpen = false;
            _isAnimating = false;
            _subMenuOpen = false;
            _openProgress = 0f;
            _subMenuProgress = 0f;
            _selectedMainIndex = -1;
            _selectedSubIndex = -1;

            if (_menuRoot != null)
            {
                _menuRoot.style.display = DisplayStyle.None;
                _menuRoot.style.opacity = 0f;
            }

            if (_scrim != null)
            {
                _scrim.style.display = DisplayStyle.None;
                _scrim.style.opacity = 0f;
            }

            if (_collapsedButton != null)
            {
                _collapsedButton.style.display = DisplayStyle.None;
                _collapsedButton.style.opacity = 1f;
                _collapsedButton.style.scale = new Scale(Vector3.one);
            }
        }

        /// <summary>
        /// Toggles the editor preview on/off.
        /// </summary>
        [ContextMenu("Toggle Editor Preview")]
        public void ToggleEditorPreview()
        {
            if (_isOpen)
            {
                SetMenuOpen(false);
            }
            else
            {
                ShowEditorPreview();
            }
        }
#endif

        private void OnEnable()
        {
            hideHudWhileOpen = false;
            SetupUI();
            LoadCommands();

            var registry = VoiceCommandRegistry.Instance;
            if (registry != null)
                registry.OnRegistryUpdated += OnRegistryUpdated;

            _keepOut ??= new MenuKeepOut(this);
            FAA.Customization.FaaHudKeepOut.Register(_keepOut);
        }

        private void OnDisable()
        {
            var registry = VoiceCommandRegistry.Instance;
            if (registry != null)
                registry.OnRegistryUpdated -= OnRegistryUpdated;

            if (_keepOut != null) FAA.Customization.FaaHudKeepOut.Unregister(_keepOut);
            if (_dockedOpen)
            {
                var chrome = FAA.Customization.FaaPilotChrome.Current;
                if (chrome != null) chrome.SetCommandsVisible(false);
                _dockedOpen = false;
            }

            _ripples.Clear();
        }

        private void OnDestroy()
        {
            if (_keepOut != null) FAA.Customization.FaaHudKeepOut.Unregister(_keepOut);
        }

        private void Update()
        {
#if UNITY_EDITOR
            // In edit mode, just update animations for preview
            if (!Application.isPlaying)
            {
                if (_isOpen)
                {
                    UpdateAnimations();
                }
                return;
            }
#endif
            HandleInput();
            SyncChromeLauncher();
            UpdateAnimations();

            if (_isOpen)
            {
                UpdateGestureRecognition();
                UpdateRipples();

                if (usePulseAnimation && _selectedMainIndex >= 0)
                {
                    UpdatePulseEffect();
                }
            }
        }

        private void SetupUI()
        {
            if (uiDocument == null) return;

            _root = uiDocument.rootVisualElement;
            if (_root == null) return;

            if (_uiBuilt && _builtRoot == _root)
            {
                ApplyInlineStyles();
                return;
            }

            _mainSegments.Clear();
            _subSegments.Clear();
            _ripples.Clear();

            // Query for existing elements from UXML template or create new ones
            _collapsedButton = _root.Q<VisualElement>("CollapsedButton");
            if (_collapsedButton == null)
            {
                CreateCollapsedButton();
            }
            else
            {
                // Configure existing collapsed button from template
                ConfigureCollapsedButton();
            }

            _scrim = _root.Q<VisualElement>("MenuBackdrop");
            if (_scrim == null)
            {
                _scrim = new VisualElement();
                _scrim.name = "MenuBackdrop";
                _scrim.AddToClassList("adv-menu-backdrop");
                _root.Add(_scrim);
            }
            _scrim.pickingMode = closeOnBackdropClick ? PickingMode.Position : PickingMode.Ignore;
            _scrim.UnregisterCallback<ClickEvent>(OnBackdropClick);
            _scrim.RegisterCallback<ClickEvent>(OnBackdropClick);

            // Query for MenuRoot or create new one
            _menuRoot = _root.Q<VisualElement>("MenuRoot");
            if (_menuRoot == null)
            {
                _menuRoot = new VisualElement();
                _menuRoot.name = "MenuRoot";
                _menuRoot.AddToClassList("adv-menu-root");
                _root.Add(_menuRoot);
            }
            _menuRoot.pickingMode = PickingMode.Ignore;

            // Query for ring background or create
            _ringBackground = _menuRoot.Q<VisualElement>("RingBackground");
            if (_ringBackground == null)
            {
                _ringBackground = new VisualElement();
                _ringBackground.name = "RingBackground";
                _ringBackground.AddToClassList("adv-ring-background");
                _menuRoot.Add(_ringBackground);
            }

            // Query for ripple container or create
            _rippleContainer = _menuRoot.Q<VisualElement>("RippleContainer");
            if (_rippleContainer == null)
            {
                _rippleContainer = new VisualElement();
                _rippleContainer.name = "RippleContainer";
                _rippleContainer.AddToClassList("adv-ripple-container");
                _menuRoot.Add(_rippleContainer);
            }
            _rippleContainer.pickingMode = PickingMode.Ignore;

            // Query for gesture indicator or create
            if (useGestures)
            {
                _gestureIndicator = _menuRoot.Q<VisualElement>("GestureIndicator");
                if (_gestureIndicator == null)
                {
                    _gestureIndicator = new VisualElement();
                    _gestureIndicator.name = "GestureIndicator";
                    _gestureIndicator.AddToClassList("adv-gesture-indicator");
                    _menuRoot.Add(_gestureIndicator);
                }
                _gestureIndicator.pickingMode = PickingMode.Ignore;
            }

            // Create main segments
            CreateMainSegments();

            // Create sub segments (initially hidden)
            if (enableSubMenus)
            {
                CreateSubSegments();
            }

            // Create center info panel
            CreateCenterInfo();

            _commandHeading = new Label("COMMANDS") { name = "CommandHeading", pickingMode = PickingMode.Ignore };
            _menuHint = new Label("TAB / ESC / CLICK OUTSIDE TO CLOSE") { name = "MenuHint", pickingMode = PickingMode.Ignore };
            _menuRoot.Add(_commandHeading);
            _menuRoot.Add(_menuHint);
            _centerInfo.pickingMode = PickingMode.Position;
            _centerInfo.tooltip = "Close HUD controls";
            _centerInfo.RegisterCallback<ClickEvent>(evt => { SetMenuOpen(false); evt.StopPropagation(); });

            // Apply styles
            ApplyInlineStyles();
            _scrim.SendToBack();
            _menuRoot.BringToFront();
            _collapsedButton.BringToFront();

            // Start collapsed or closed based on setting. A docked launcher lives in the chrome bar, never the corner.
            if (startCollapsed)
            {
                _scrim.style.display = DisplayStyle.None;
                _menuRoot.style.display = DisplayStyle.None;
                _collapsedButton.style.display = LauncherDocked ? DisplayStyle.None : DisplayStyle.Flex;
            }
            else
            {
                _isOpen = false;
                _openProgress = 0f;
                _scrim.style.display = DisplayStyle.None;
                _menuRoot.style.display = DisplayStyle.None;
                if (_collapsedButton != null)
                {
                    _collapsedButton.style.display = DisplayStyle.None;
                }
            }

            _uiBuilt = true;
            _builtRoot = _root;
        }

        private void CreateCollapsedButton()
        {
            _collapsedButton = new VisualElement();
            _collapsedButton.name = "CollapsedButton";
            _collapsedButton.AddToClassList("adv-collapsed-button");
            _collapsedButton.pickingMode = PickingMode.Position;
            _collapsedButton.focusable = true;
            _collapsedButton.tabIndex = 0;
            _collapsedButton.tooltip = "Open HUD command menu";

            // Style the collapsed button
            _collapsedButton.style.position = Position.Absolute;
            ApplyCollapsedButtonAnchor();
            _collapsedButton.style.width = collapsedButtonSize;
            _collapsedButton.style.height = collapsedButtonSize;
            _collapsedButton.style.backgroundColor = PanelBackgroundColor;
            SetRadius(_collapsedButton, collapsedButtonSize / 2);
            SetBorderWidth(_collapsedButton, 2);
            SetBorderColor(_collapsedButton, SegmentBorderColor);
            _collapsedButton.style.alignItems = Align.Center;
            _collapsedButton.style.justifyContent = Justify.Center;
            _collapsedButton.style.transitionProperty = new List<StylePropertyName>
            {
                new StylePropertyName("scale"),
                new StylePropertyName("background-color"),
                new StylePropertyName("border-color")
            };
            _collapsedButton.style.transitionDuration = new List<TimeValue> { new TimeValue(0.15f) };
            _collapsedButton.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.EaseOut };

            // Create icon inside button (microphone/voice icon using SVG or fallback)
            _collapsedIcon = new VisualElement();
            _collapsedIcon.style.width = collapsedButtonSize * 0.5f;
            _collapsedIcon.style.height = collapsedButtonSize * 0.5f;
            _collapsedIcon.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;

            // Try to load SVG icon (VectorImage), fallback to PNG textures
            Texture2D buttonTexture = null;
            var svgIcon = Resources.Load<VectorImage>("VoiceControl/Icons/radial_menu");
            if (svgIcon != null)
            {
                _collapsedIcon.style.backgroundImage = new StyleBackground(Background.FromVectorImage(svgIcon));
            }
            else
            {
                // Try PNG from Resources
                buttonTexture = Resources.Load<Texture2D>("VoiceControl/Textures/WheelCenter");
                if (buttonTexture == null)
                {
                    // Try loading from project textures via AssetDatabase in editor
                    #if UNITY_EDITOR
                    buttonTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/_Project/Textures/480px_FAA_SYMBOLOLGY_OPTIONS/Weather_Radar_Base.png");
                    #endif
                }
                if (buttonTexture != null)
                {
                    _collapsedIcon.style.backgroundImage = new StyleBackground(buttonTexture);
                }
            }
            _collapsedIcon.style.unityBackgroundImageTintColor = new Color(0.4f, 0.8f, 1f, 1f);
            _collapsedButton.Add(_collapsedIcon);
            EnsureCollapsedLabel();

            // Hover effects
            _collapsedButton.RegisterCallback<MouseEnterEvent>(evt =>
            {
                _collapsedButton.style.scale = new Scale(new Vector3(1.1f, 1.1f, 1));
                _collapsedButton.style.backgroundColor = new Color(0.035f, 0.075f, 0.075f, 0.98f);
                SetBorderColor(_collapsedButton, new Color(0.25f, 1f, 0.72f, 0.75f));
            });

            _collapsedButton.RegisterCallback<MouseLeaveEvent>(evt =>
            {
                _collapsedButton.style.scale = new Scale(Vector3.one);
                _collapsedButton.style.backgroundColor = PanelBackgroundColor;
                SetBorderColor(_collapsedButton, SegmentBorderColor);
            });

            // Click to expand
            _collapsedButton.RegisterCallback<ClickEvent>(evt =>
            {
                // Animate button press
                _collapsedButton.style.scale = new Scale(new Vector3(0.9f, 0.9f, 1));
                _collapsedButton.schedule.Execute(() =>
                {
                    _collapsedButton.style.scale = new Scale(Vector3.one);
                    ExpandFromCollapsed();
                }).StartingIn(100);
            });
            _collapsedButton.RegisterCallback<KeyDownEvent>(OnCollapsedButtonKeyDown);

            _root.Add(_collapsedButton);
        }

        private void ConfigureCollapsedButton()
        {
            // Configure existing collapsed button from UXML template
            _collapsedButton.pickingMode = PickingMode.Position;
            _collapsedButton.focusable = true;
            _collapsedButton.tabIndex = 0;
            _collapsedButton.tooltip = "Open HUD command menu";

            // Update position and size from serialized fields
            ApplyCollapsedButtonAnchor();
            _collapsedButton.style.width = collapsedButtonSize;
            _collapsedButton.style.height = collapsedButtonSize;

            // Get or create icon element
            _collapsedIcon = _collapsedButton.Q<VisualElement>("CollapsedIcon");
            if (_collapsedIcon == null)
            {
                _collapsedIcon = new VisualElement();
                _collapsedIcon.name = "CollapsedIcon";
                _collapsedIcon.AddToClassList("adv-collapsed-icon");
                _collapsedButton.Add(_collapsedIcon);
            }

            // Load the command-menu icon (same vector as CreateCollapsedButton); the plain disc texture is only a fallback.
            var svgIcon = Resources.Load<VectorImage>("VoiceControl/Icons/radial_menu");
            if (svgIcon != null)
            {
                _collapsedIcon.style.backgroundImage = new StyleBackground(Background.FromVectorImage(svgIcon));
            }
            else
            {
                Texture2D buttonTexture = Resources.Load<Texture2D>("VoiceControl/Textures/WheelCenter");
                if (buttonTexture == null)
                {
                    #if UNITY_EDITOR
                    buttonTexture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(
                        "Assets/_Project/Textures/480px_FAA_SYMBOLOLGY_OPTIONS/Weather_Radar_Base.png");
                    #endif
                }
                if (buttonTexture != null)
                {
                    _collapsedIcon.style.backgroundImage = new StyleBackground(buttonTexture);
                }
            }
            EnsureCollapsedLabel();

            // Hover effects
            _collapsedButton.RegisterCallback<MouseEnterEvent>(evt =>
            {
                _collapsedButton.style.scale = new Scale(new Vector3(1.1f, 1.1f, 1));
            });

            _collapsedButton.RegisterCallback<MouseLeaveEvent>(evt =>
            {
                _collapsedButton.style.scale = new Scale(Vector3.one);
            });

            // Click to expand
            _collapsedButton.RegisterCallback<ClickEvent>(evt =>
            {
                _collapsedButton.style.scale = new Scale(new Vector3(0.9f, 0.9f, 1));
                _collapsedButton.schedule.Execute(() =>
                {
                    _collapsedButton.style.scale = new Scale(Vector3.one);
                    ExpandFromCollapsed();
                }).StartingIn(100);
            });
            _collapsedButton.UnregisterCallback<KeyDownEvent>(OnCollapsedButtonKeyDown);
            _collapsedButton.RegisterCallback<KeyDownEvent>(OnCollapsedButtonKeyDown);
        }

        /// <summary>Floating launcher (edit-time preview or no chrome): never an unlabeled disc; it names its function and key.</summary>
        private void EnsureCollapsedLabel()
        {
            if (_collapsedButton == null || _collapsedButton.Q<Label>("CollapsedLabel") != null) return;
            var label = new Label("COMMANDS  " + (KeyLabel(toggleKey) ?? string.Empty).ToUpperInvariant()) { name = "CollapsedLabel", pickingMode = PickingMode.Ignore };
            label.style.position = Position.Absolute;
            label.style.top = new Length(100, LengthUnit.Percent);
            label.style.marginTop = 4;
            label.style.left = new StyleLength(new Length(50, LengthUnit.Percent));
            label.style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
            label.style.unityTextAlign = TextAnchor.UpperCenter;
            label.style.width = 180;
            label.style.fontSize = PanelFont(FAA.Customization.FaaHudStyle.Chrome);
            label.style.color = new Color(199f / 255f, 235f / 255f, 244f / 255f, 1f);
            _collapsedButton.Add(label);
        }

        private void OnCollapsedButtonKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode != KeyCode.Return && evt.keyCode != KeyCode.KeypadEnter && evt.keyCode != KeyCode.Space)
            {
                return;
            }

            ExpandFromCollapsed();
            evt.StopPropagation();
        }

        private void ExpandFromCollapsed()
        {
            // Hide collapsed button with animation
            _collapsedButton.style.scale = new Scale(new Vector3(0.5f, 0.5f, 1));
            _collapsedButton.style.opacity = 0;

            _collapsedButton.schedule.Execute(() =>
            {
                _collapsedButton.style.display = DisplayStyle.None;
                _collapsedButton.style.scale = new Scale(Vector3.one);
                _collapsedButton.style.opacity = 1;
            }).StartingIn(150);

            // Open the radial menu
            SetMenuOpen(true);
        }

        private void CollapseToButton()
        {
            // Show collapsed button with animation
            _collapsedButton.style.display = DisplayStyle.Flex;
            _collapsedButton.style.scale = new Scale(new Vector3(0.5f, 0.5f, 1));
            _collapsedButton.style.opacity = 0;

            _collapsedButton.schedule.Execute(() =>
            {
                _collapsedButton.style.scale = new Scale(new Vector3(1.15f, 1.15f, 1));
                _collapsedButton.style.opacity = 1;
            }).StartingIn(50);

            _collapsedButton.schedule.Execute(() =>
            {
                _collapsedButton.style.scale = new Scale(Vector3.one);
            }).StartingIn(200);
        }

        private void CreateMainSegments()
        {
            float angleStep = 360f / mainSegmentCount;

            for (int i = 0; i < mainSegmentCount; i++)
            {
                float angle = i * angleStep - 90f; // Start from top (-90 degrees)
                var segment = new MainSegment
                {
                    Index = i,
                    Angle = angle,
                    Container = new VisualElement(),
                    Background = new VisualElement(),
                    IconContainer = new VisualElement(),
                    IconImage = new VisualElement(),  // VisualElement for texture
                    NameLabel = new Label()
                };

                segment.Container.AddToClassList("adv-main-segment");
                segment.Container.pickingMode = PickingMode.Position;

                segment.Background.AddToClassList("adv-main-bg");
                segment.Container.Add(segment.Background);

                segment.IconContainer.AddToClassList("adv-main-icon-container");
                segment.Container.Add(segment.IconContainer);

                segment.IconImage.AddToClassList("adv-main-icon");
                segment.IconContainer.Add(segment.IconImage);

                segment.NameLabel.AddToClassList("adv-main-name");
                segment.Container.Add(segment.NameLabel);

                int index = i;
                segment.Container.RegisterCallback<MouseEnterEvent>(evt => OnMainSegmentHover(index, true));
                segment.Container.RegisterCallback<MouseLeaveEvent>(evt => OnMainSegmentHover(index, false));
                segment.Container.RegisterCallback<ClickEvent>(evt =>
                {
                    OnMainSegmentClick(index);
                    evt.StopPropagation();
                });

                _menuRoot.Add(segment.Container);
                _mainSegments.Add(segment);
            }
        }

        private void CreateSubSegments()
        {
            int segmentLimit = Mathf.Clamp(maxSubSegmentCount, 4, 8);
            for (int i = 0; i < segmentLimit; i++)
            {
                var segment = new SubSegment
                {
                    Index = i,
                    Container = new VisualElement(),
                    Background = new VisualElement(),
                    IconContainer = new VisualElement(),
                    IconImage = new VisualElement(),
                    NameLabel = new Label(),
                    IsVisible = false
                };

                segment.Container.AddToClassList("adv-sub-segment");
                segment.Container.pickingMode = PickingMode.Position;

                segment.Background.AddToClassList("adv-sub-bg");
                segment.Container.Add(segment.Background);

                segment.IconContainer.AddToClassList("adv-sub-icon-container");
                segment.IconImage.AddToClassList("adv-sub-icon-img");
                segment.IconContainer.Add(segment.IconImage);
                segment.Container.Add(segment.IconContainer);

                segment.NameLabel.AddToClassList("adv-sub-name");
                segment.Container.Add(segment.NameLabel);

                int index = i;
                segment.Container.RegisterCallback<MouseEnterEvent>(evt => OnSubSegmentHover(index, true));
                segment.Container.RegisterCallback<MouseLeaveEvent>(evt => OnSubSegmentHover(index, false));
                segment.Container.RegisterCallback<ClickEvent>(evt =>
                {
                    OnSubSegmentClick(index);
                    evt.StopPropagation();
                });

                segment.Container.style.display = DisplayStyle.None;
                _menuRoot.Add(segment.Container);
                _subSegments.Add(segment);
            }
        }

        private void CreateCenterInfo()
        {
            _centerInfo = _menuRoot.Q<VisualElement>("CenterInfo");
            if (_centerInfo == null)
            {
                _centerInfo = new VisualElement();
                _centerInfo.name = "CenterInfo";
                _centerInfo.AddToClassList("adv-center-info");
                _menuRoot.Add(_centerInfo);
            }

            _centerTitle = _centerInfo.Q<Label>("CenterTitle");
            if (_centerTitle == null)
            {
                _centerTitle = new Label();
                _centerTitle.name = "CenterTitle";
                _centerTitle.AddToClassList("adv-center-title");
                _centerInfo.Add(_centerTitle);
            }
            _centerTitle.text = string.Empty;

            _centerSubtitle = _centerInfo.Q<Label>("CenterSubtitle");
            if (_centerSubtitle == null)
            {
                _centerSubtitle = new Label();
                _centerSubtitle.name = "CenterSubtitle";
                _centerSubtitle.AddToClassList("adv-center-subtitle");
                _centerInfo.Add(_centerSubtitle);
            }
            _centerSubtitle.text = string.Empty;
        }

        private void ApplyInlineStyles()
        {
            if (_scrim != null)
            {
                _scrim.style.position = Position.Absolute;
                _scrim.style.left = 0;
                _scrim.style.top = 0;
                _scrim.style.right = 0;
                _scrim.style.bottom = 0;
                _scrim.style.backgroundColor = new Color(0f, 0.014f, 0.018f, Mathf.Clamp01(backdropOpacity));
                _scrim.style.opacity = 0f;
                _scrim.style.display = DisplayStyle.None;
            }

            // Menu root - centered on screen
            _menuRoot.style.position = Position.Absolute;
            _menuRoot.style.left = new Length(50, LengthUnit.Percent);
            _menuRoot.style.top = new Length(50, LengthUnit.Percent);
            _menuRoot.style.width = 0;
            _menuRoot.style.height = 0;

            float ringSize = middleRadius * 2 + 32;
            _ringBackground.style.position = Position.Absolute;
            _ringBackground.style.width = ringSize;
            _ringBackground.style.height = ringSize;
            _ringBackground.style.left = -ringSize / 2;
            _ringBackground.style.top = -ringSize / 2;
            _ringBackground.style.backgroundColor = WithAlpha(PanelBackgroundColor, ringBackgroundTransparency);
            SetRadius(_ringBackground, ringSize / 2);
            SetBorderWidth(_ringBackground, 1f);
            SetBorderColor(_ringBackground, new Color(0.42f, 0.71f, 0.76f, 0.25f * menuTransparency));

            // Main segments - compact, readable buttons
            float segmentWidth = MainSegmentWidth;
            float segmentHeight = MainSegmentHeight;
            foreach (var seg in _mainSegments)
            {
                seg.Container.style.position = Position.Absolute;
                seg.Container.style.width = segmentWidth;
                seg.Container.style.height = segmentHeight;
                seg.Container.style.backgroundColor = WithAlpha(SegmentBackgroundColor, segmentTransparency);
                SetRadius(seg.Container, 2);
                SetBorderWidth(seg.Container, 1f);
                SetBorderColor(seg.Container, SegmentBorderColor);
                seg.Container.style.alignItems = Align.Center;
                seg.Container.style.justifyContent = Justify.Center;
                seg.Container.style.flexDirection = FlexDirection.Column;
                seg.Container.style.paddingTop = 7;
                seg.Container.style.paddingBottom = 7;

                seg.Background.style.position = Position.Absolute;
                seg.Background.style.width = new Length(100, LengthUnit.Percent);
                seg.Background.style.height = new Length(100, LengthUnit.Percent);
                seg.Background.style.left = 0;
                seg.Background.style.top = 0;
                SetRadius(seg.Background, 2);
                seg.Background.style.backgroundColor = new Color(0.04f, 0.18f, 0.16f, 0.10f);

                // Icon container - holds the image
                seg.IconContainer.style.position = Position.Relative;
                seg.IconContainer.style.width = MainIconContainerSize;
                seg.IconContainer.style.height = MainIconContainerSize;
                seg.IconContainer.style.flexShrink = 0;
                seg.IconContainer.style.alignItems = Align.Center;
                seg.IconContainer.style.justifyContent = Justify.Center;
                seg.IconContainer.style.marginBottom = 3;

                // Icon image - will display texture
                seg.IconImage.style.width = MainIconSize;
                seg.IconImage.style.height = MainIconSize;
                seg.IconImage.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;

                // Name label - LARGE and sharp text
                seg.NameLabel.style.position = Position.Relative;
                seg.NameLabel.style.fontSize = PanelFont(mainLabelFontSize);
                seg.NameLabel.style.color = new Color(0.92f, 0.95f, 0.98f, 1f);
                seg.NameLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                seg.NameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                seg.NameLabel.style.width = segmentWidth - 20;
                seg.NameLabel.style.whiteSpace = WhiteSpace.Normal;
                seg.NameLabel.style.letterSpacing = 0;
            }

            // Sub segments - compact secondary buttons
            float subWidth = SubSegmentWidth;
            float subHeight = SubSegmentHeight;
            foreach (var seg in _subSegments)
            {
                seg.Container.style.position = Position.Absolute;
                seg.Container.style.width = subWidth;
                seg.Container.style.height = subHeight;
                seg.Container.style.backgroundColor = WithAlpha(SegmentBackgroundColor, segmentTransparency);
                SetRadius(seg.Container, 2);
                SetBorderWidth(seg.Container, 1f);
                SetBorderColor(seg.Container, SubBorderBaseColor);
                seg.Container.style.alignItems = Align.Center;
                seg.Container.style.justifyContent = Justify.FlexStart;
                seg.Container.style.flexDirection = FlexDirection.Row;
                seg.Container.style.paddingLeft = 14;
                seg.Container.style.paddingRight = 14;
                seg.Container.style.paddingTop = 6;
                seg.Container.style.paddingBottom = 6;
                seg.Container.style.transitionProperty = new List<StylePropertyName>
                {
                    new StylePropertyName("scale"),
                    new StylePropertyName("opacity"),
                    new StylePropertyName("background-color"),
                    new StylePropertyName("border-color")
                };
                seg.Container.style.transitionDuration = new List<TimeValue> { new TimeValue(0.18f) };
                seg.Container.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.EaseOut };

                seg.Background.style.position = Position.Absolute;
                seg.Background.style.width = new Length(100, LengthUnit.Percent);
                seg.Background.style.height = new Length(100, LengthUnit.Percent);
                seg.Background.style.left = 0;
                seg.Background.style.top = 0;
                SetRadius(seg.Background, 2);

                seg.IconContainer.style.width = SubIconContainerSize;
                seg.IconContainer.style.height = SubIconContainerSize;
                seg.IconContainer.style.flexShrink = 0;
                seg.IconContainer.style.alignItems = Align.Center;
                seg.IconContainer.style.justifyContent = Justify.Center;
                seg.IconContainer.style.marginBottom = 0;
                seg.IconContainer.style.marginRight = 12;
                seg.IconContainer.style.transitionProperty = new List<StylePropertyName>
                {
                    new StylePropertyName("scale"),
                    new StylePropertyName("opacity")
                };
                seg.IconContainer.style.transitionDuration = new List<TimeValue> { new TimeValue(0.18f) };
                seg.IconContainer.style.transitionTimingFunction = new List<EasingFunction> { EasingMode.EaseOut };

                seg.IconImage.style.width = SubIconSize;
                seg.IconImage.style.height = SubIconSize;
                seg.IconImage.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;

                seg.NameLabel.style.fontSize = PanelFont(subLabelFontSize);
                seg.NameLabel.style.color = new Color(0.85f, 0.90f, 0.95f, 0.95f);
                seg.NameLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
                seg.NameLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                seg.NameLabel.style.width = subWidth - 74;
                seg.NameLabel.style.whiteSpace = WhiteSpace.Normal;
            }

            // Center info - LARGE prominent center hub
            float centerSize = innerRadius * 2 - CenterSizePadding;
            _centerInfo.style.position = Position.Absolute;
            _centerInfo.style.width = centerSize;
            _centerInfo.style.height = centerSize;
            _centerInfo.style.left = -centerSize / 2;
            _centerInfo.style.top = -centerSize / 2;
            _centerInfo.style.backgroundColor = WithAlpha(PanelBackgroundColor, centerTransparency);
            SetRadius(_centerInfo, centerSize / 2);
            SetBorderWidth(_centerInfo, 1);
            SetBorderColor(_centerInfo, new Color(0.42f, 0.71f, 0.76f, 0.42f * menuTransparency));
            _centerInfo.style.alignItems = Align.Center;
            _centerInfo.style.justifyContent = Justify.Center;

            // Center title - LARGE sharp text
            _centerTitle.style.fontSize = PanelFont(centerTitleFontSize);
            _centerTitle.style.color = new Color(0.86f, 0.96f, 0.98f, 1f);
            _centerTitle.style.unityFontStyleAndWeight = FontStyle.Bold;
            _centerTitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _centerTitle.style.letterSpacing = 0;

            // Center subtitle
            _centerSubtitle.style.fontSize = PanelFont(centerSubtitleFontSize);
            // The old "n commands" subtitle repeated what the column shows; the wheel names the category only.
            _centerSubtitle.style.display = DisplayStyle.None;
            _centerSubtitle.style.color = new Color(0.75f, 0.82f, 0.90f, 0.95f);
            _centerSubtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            _centerSubtitle.style.marginTop = 6;
            _centerTitle.style.whiteSpace = WhiteSpace.Normal;
            _centerTitle.style.width = centerSize - 16;
            _centerSubtitle.style.whiteSpace = WhiteSpace.Normal;
            _centerSubtitle.style.width = centerSize - 12;
            if (_commandHeading != null)
            {
                _commandHeading.style.position = Position.Absolute;
                _commandHeading.style.left = 274;
                _commandHeading.style.width = SubSegmentWidth;
                _commandHeading.style.fontSize = PanelFont(FAA.Customization.FaaHudStyle.Chrome);
                _commandHeading.style.unityFontStyleAndWeight = FontStyle.Bold;
                _commandHeading.style.color = new Color(0.70f, 0.87f, 0.89f, 1f);
                _menuHint.style.position = Position.Absolute;
                _menuHint.style.top = middleRadius + 40;
                _menuHint.style.left = -250;
                _menuHint.style.width = 500;
                _menuHint.style.fontSize = PanelFont(FAA.Customization.FaaHudStyle.Chrome);
                _menuHint.style.color = FAA.Customization.FaaHudStyle.White;
                _menuHint.style.backgroundColor = FAA.Customization.FaaHudStyle.ChromePlate;
                _menuHint.style.paddingTop = _menuHint.style.paddingBottom = 4;
                _menuHint.style.unityTextAlign = TextAnchor.MiddleCenter;
            }
        }

        private void LoadCommands()
        {
            // Prevent recursive calls (DiscoverTargets triggers OnRegistryUpdated)
            if (_isLoadingCommands) return;
            _isLoadingCommands = true;

            try
            {
                _categories.Clear();
                var registry = VoiceCommandRegistry.Instance;
                if (registry != null && registry.Targets.Count == 0) registry.DiscoverTargets();
                CategoriesFromRows(BuildCommandRows(registry, registry == null));
                AssignCategoriesToSegments();
            }
            finally
            {
                _isLoadingCommands = false;
            }
        }

        /// <summary>
        /// The pilot command set, one row per function with its live state (no separate Show / Hide items):
        /// RADARS (weather, traffic display), SCREEN CUES (traffic, weather markers), HUD (flight HUD, brightness) and,
        /// when present, AI IMAGE ANALYSIS. Rows appear only for functions that exist in the scene; a state that cannot
        /// be read is shown as unknown (no segment lit), never guessed. <paramref name="preview"/> (no registry, edit
        /// time) lists every row with inert actions. Scene references are resolved here, once per open.
        /// </summary>
        public List<FAA.Customization.FaaChromeCommandRow> BuildCommandRows(VoiceCommandRegistry registry, bool preview = false)
        {
            var rows = new List<FAA.Customization.FaaChromeCommandRow>();
            ResolveStateSources();

            if (preview || HasTarget(registry, "weather_radar"))
                rows.Add(FAA.Customization.FaaChromeCommandRow.Toggle(SectionRadars, "WEATHER RADAR",
                    () => preview ? null : CanvasShown(_weatherRadarCanvas),
                    on => RunRegistryCommand(registry, "weather_radar", on ? "show_panel" : "hide_panel", "WEATHER RADAR")));
            if (preview || HasTarget(registry, "traffic_radar"))
                rows.Add(FAA.Customization.FaaChromeCommandRow.Toggle(SectionRadars, "TRAFFIC RADAR",
                    () => preview ? null : CanvasShown(_trafficRadarCanvas),
                    on => RunRegistryCommand(registry, "traffic_radar", on ? "show_panel" : "hide_panel", "TRAFFIC RADAR")));

            if (preview || _cueController != null)
            {
                rows.Add(FAA.Customization.FaaChromeCommandRow.Toggle(SectionCues, "TRAFFIC CUES",
                    () => CueShown(IndicatorSystem.Core.IndicatorType.Traffic),
                    on => SetCue(IndicatorSystem.Core.IndicatorType.Traffic, on)));
                rows.Add(FAA.Customization.FaaChromeCommandRow.Toggle(SectionCues, "WEATHER CUES",
                    () => CueShown(IndicatorSystem.Core.IndicatorType.Weather),
                    on => SetCue(IndicatorSystem.Core.IndicatorType.Weather, on)));
            }

            if (preview || HasTarget(registry, "symbology"))
                rows.Add(FAA.Customization.FaaChromeCommandRow.Toggle(SectionHud, "FLIGHT HUD",
                    () => _symbologyColor != null ? _symbologyColor.CurrentOpacity > .01f : (bool?)null,
                    on => RunRegistryCommand(registry, "symbology", on ? "show" : "hide", "FLIGHT HUD")));
            if (preview || _hudOpacity != null)
                rows.Add(new FAA.Customization.FaaChromeCommandRow(SectionHud, "BRIGHTNESS %", BrightnessLabels,
                    () => _hudOpacity != null ? BrightnessIndex(_hudOpacity.OpacityPercent) : -1,
                    i => { if (_hudOpacity != null && i >= 0 && i < BrightnessPresets.Length) _hudOpacity.SetOpacityPercent(BrightnessPresets[i]); }));

            if (preview || HasTarget(registry, "visionbriefing"))
                rows.Add(new FAA.Customization.FaaChromeCommandRow(SectionImageAnalysis, "ANALYZE", new[] { "WX RADAR", "CHART", "HIDE" }, null,
                    i => RunRegistryCommand(registry, "visionbriefing", i == 0 ? "weather_briefing" : i == 1 ? "sectional_briefing" : "hide_briefing", "AI IMAGE ANALYSIS"),
                    true));
            return rows;
        }

        /// <summary>Index of the brightness preset within 2 % of <paramref name="percent"/>, else -1 (an off-preset value lights nothing).</summary>
        public static int BrightnessIndex(int percent)
        {
            for (int i = 0; i < BrightnessPresets.Length; i++) if (Mathf.Abs(BrightnessPresets[i] - percent) <= 2) return i;
            return -1;
        }

        private static bool HasTarget(VoiceCommandRegistry registry, string id) => registry != null && registry.HasTarget(id);

        private void ResolveStateSources()
        {
            _weatherRadarCanvas = _trafficRadarCanvas = null;
            foreach (Canvas canvas in FindObjectsByType<Canvas>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (canvas == null || !canvas.gameObject.scene.IsValid()) continue;
                if (_weatherRadarCanvas == null && canvas.gameObject.name == WeatherRadarCanvasName) _weatherRadarCanvas = canvas;
                else if (_trafficRadarCanvas == null && canvas.gameObject.name == TrafficRadarCanvasName) _trafficRadarCanvas = canvas;
            }
            _cueController = FindAnyObjectByType<IndicatorSystem.Controller.IndicatorSystemController>();
            _symbologyColor = FindAnyObjectByType<FAA.Customization.SymbologyColorManager>();
            _hudOpacity = FindAnyObjectByType<FAA.Customization.FaaHudOpacityController>();
        }

        /// <summary>A radar display is shown when its dedicated canvas is enabled, active and not faded out (the voice adapters hide it that way).</summary>
        public static bool? CanvasShown(Canvas canvas)
        {
            if (canvas == null) return null;
            if (!canvas.isActiveAndEnabled) return false;
            // Polled at 5 Hz while the flyout is open: TryGetComponent never allocates.
            return !canvas.TryGetComponent(out CanvasGroup group) || group.alpha > .01f;
        }

        private bool? CueShown(IndicatorSystem.Core.IndicatorType type) =>
            _cueController != null ? _cueController.IsTypeVisible(type) : (bool?)null;

        private void SetCue(IndicatorSystem.Core.IndicatorType type, bool on)
        {
            if (_cueController == null) return;
            IndicatorSystem.Display.IndicatorControlsPanel.SetCueVisible(_cueController, type, on);
            OnCommandExecuted?.Invoke(new MenuCommand { Id = "indicator_system_" + type, TargetId = "indicator_system", CommandName = (on ? "show_" : "hide_") + type.ToString().ToLowerInvariant(), DisplayName = type.ToString().ToUpperInvariant() + " CUES", Category = "indicator_system" });
        }

        private void RunRegistryCommand(VoiceCommandRegistry registry, string targetId, string commandName, string displayName)
        {
            var cmd = new MenuCommand { Id = targetId + "_" + commandName, TargetId = targetId, CommandName = commandName, DisplayName = displayName, Category = targetId };
            OnCommandExecuted?.Invoke(cmd);
            if (registry != null) registry.ExecuteCommand(targetId, commandName, null);
            if (string.Equals(targetId, "hud_opacity", StringComparison.OrdinalIgnoreCase)) ExecuteHudOpacityPreset(commandName);
        }

        /// <summary>UI Toolkit fallback: one category per section, one command per row (toggles flip, selectors cycle, action rows split).</summary>
        private void CategoriesFromRows(List<FAA.Customization.FaaChromeCommandRow> rows)
        {
            MenuCategory category = null;
            foreach (var row in rows)
            {
                if (category == null || category.DisplayName != row.Section)
                {
                    category = CreateCategory(SectionCategoryId(row.Section), row.Section);
                    _categories.Add(category);
                }
                var r = row;
                if (r.Actions)
                {
                    for (int k = 0; k < r.Segments.Length; k++)
                    {
                        int index = k;
                        AddRowCommand(category, r.Label + " " + r.Segments[k], () => r.Select?.Invoke(index), null);
                    }
                }
                else if (r.Segments.Length == 2)
                    AddRowCommand(category, r.Label, () => { int state = r.CurrentState(); r.Select?.Invoke(state == 0 ? 1 : 0); }, () => StateLabel(r));
                else
                    AddRowCommand(category, r.Label, () => { int state = r.CurrentState(); r.Select?.Invoke(state < 0 ? 0 : (state + 1) % r.Segments.Length); }, () => StateLabel(r));
            }
        }

        private static string StateLabel(FAA.Customization.FaaChromeCommandRow row)
        {
            int state = row.CurrentState();
            return state >= 0 ? row.Segments[state] : "--";
        }

        private static string SectionCategoryId(string section) =>
            section == SectionRadars ? "radar" : section == SectionCues ? "indicator_system" : section == SectionHud ? "hud" : section == SectionImageAnalysis ? "visionbriefing" : section.ToLowerInvariant();

        private void AddRowCommand(MenuCategory category, string displayName, Action execute, Func<string> stateText)
        {
            category.Commands.Add(new MenuCommand
            {
                Id = category.Id + "_" + displayName.Replace(' ', '_').ToLowerInvariant(),
                TargetId = category.Id,
                CommandName = displayName,
                DisplayName = displayName,
                Description = displayName,
                Category = category.Id,
                IconPath = GetCommandIconPath(category.Id, displayName, displayName),
                Color = category.Color,
                RequiresParams = false,
                Execute = execute,
                StateText = stateText
            });
        }

        private MenuCategory CreateCategory(string id, string displayName)
        {
            var def = _categoryDefs.GetValueOrDefault(id, (iconPath: string.Empty, color: Color.gray));
            return new MenuCategory
            {
                Id = id,
                DisplayName = displayName,
                Icon = def.iconPath,
                Color = def.color
            };
        }

        private void AssignCategoriesToSegments()
        {
            for (int i = 0; i < _mainSegments.Count; i++)
            {
                if (i < _categories.Count)
                {
                    _mainSegments[i].Category = _categories[i];
                    _mainSegments[i].Angle = i * 360f / Mathf.Min(_categories.Count, _mainSegments.Count) - 90f;
                    _mainSegments[i].Container.style.display = DisplayStyle.Flex;

                    // Load icon texture from path
                    bool iconLoaded = TrySetIcon(_mainSegments[i].IconImage, _categories[i].Icon, Color.white);

                    // Fallback: show category initial as a styled circle if no icon loaded
                    if (!iconLoaded)
                    {
                        // Create fallback initial display
                        string initial = !string.IsNullOrEmpty(_categories[i].DisplayName)
                            ? _categories[i].DisplayName.Substring(0, 1).ToUpper()
                            : "?";
                        _mainSegments[i].IconImage.style.backgroundColor = WithAlpha(_categories[i].Color, 0.32f);
                        SetRadius(_mainSegments[i].IconImage, 32);

                        // Add initial label if not present
                        var existingLabel = _mainSegments[i].IconImage.Q<Label>("fallback-initial");
                        if (existingLabel == null)
                        {
                            var fallbackLabel = new Label(initial);
                            fallbackLabel.name = "fallback-initial";
                            fallbackLabel.style.fontSize = 28;
                            fallbackLabel.style.color = Color.white;
                            fallbackLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
                            fallbackLabel.style.width = new Length(100, LengthUnit.Percent);
                            fallbackLabel.style.height = new Length(100, LengthUnit.Percent);
                            _mainSegments[i].IconImage.Add(fallbackLabel);
                        }
                        else
                        {
                            existingLabel.text = initial;
                        }
                    }

                    _mainSegments[i].NameLabel.text = _categories[i].DisplayName;
                    SetMainSegmentHover(_mainSegments[i], _selectedMainIndex == i);
                }
                else
                {
                    _mainSegments[i].Container.style.display = DisplayStyle.None;
                }
            }
        }

        private bool TrySetIcon(VisualElement target, string iconPath, Color tint)
        {
            if (target == null || string.IsNullOrEmpty(iconPath))
            {
                return false;
            }

            target.Clear();
            target.style.backgroundColor = Color.clear;

            VectorImage vector = Resources.Load<VectorImage>(iconPath);
            Sprite sprite = null;
            Texture2D texture = null;

            if (vector == null)
            {
                sprite = Resources.Load<Sprite>(iconPath);
            }

            if (vector == null && sprite == null)
            {
                texture = Resources.Load<Texture2D>(iconPath);
            }

            #if UNITY_EDITOR
            if (vector == null && sprite == null && texture == null)
            {
                vector = UnityEditor.AssetDatabase.LoadAssetAtPath<VectorImage>("Assets/" + iconPath + ".svg");
                if (vector == null)
                {
                    sprite = UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/" + iconPath + ".png");
                }
                if (vector == null && sprite == null)
                {
                    texture = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/" + iconPath + ".png");
                }
            }
            #endif

            if (vector != null)
            {
                target.style.backgroundImage = new StyleBackground(Background.FromVectorImage(vector));
            }
            else if (sprite != null)
            {
                target.style.backgroundImage = new StyleBackground(sprite);
            }
            else if (texture != null)
            {
                target.style.backgroundImage = new StyleBackground(texture);
            }
            else
            {
                return false;
            }

            target.style.unityBackgroundImageTintColor = tint;
            return true;
        }

        private void HandleInput()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                ToggleMenu();
            }

            if (_dockedOpen && Input.GetKeyDown(closeKey))
            {
                SetMenuOpen(false);
            }
            else if (_isOpen && Input.GetKeyDown(closeKey))
            {
                if (_subMenuOpen)
                {
                    CloseSubMenu();
                }
                else
                {
                    SetMenuOpen(false);
                }
            }

            // Mouse wheel navigation
            if (_isOpen && useMouseWheel)
            {
                float scroll = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    NavigateMenu(scroll > 0 ? 1 : -1);
                }
            }

            // Fallback wheel: a click outside it closes it (there is no full-screen backdrop to catch the click).
            if (_isOpen && closeOnBackdropClick && Input.GetMouseButtonDown(0) && !PointerOverWheel())
            {
                SetMenuOpen(false);
            }
        }

        private bool PointerOverWheel()
        {
            if (_root?.panel == null || _menuRoot == null) return true;
            Vector2 mouse = Input.mousePosition;
            Vector2 panelPoint = RuntimePanelUtils.ScreenToPanel(_root.panel, new Vector2(mouse.x, Screen.height - mouse.y));
            VisualElement picked = _root.panel.Pick(panelPoint);
            return picked != null && (picked == _menuRoot || _menuRoot.Contains(picked));
        }

        /// <summary>Screen rectangle (pixels, bottom-left origin) of the open fallback wheel and its command column.</summary>
        private bool TryGetWheelScreenRect(out Rect rect)
        {
            rect = default;
            if (!_isOpen || _ringBackground == null || _root == null) return false;
            Rect bounds = _ringBackground.worldBound;
            foreach (var seg in _subSegments)
                if (seg.IsVisible) bounds = Rect.MinMaxRect(Mathf.Min(bounds.xMin, seg.Container.worldBound.xMin), Mathf.Min(bounds.yMin, seg.Container.worldBound.yMin),
                    Mathf.Max(bounds.xMax, seg.Container.worldBound.xMax), Mathf.Max(bounds.yMax, seg.Container.worldBound.yMax));
            float rootWidth = _root.worldBound.width, rootHeight = _root.worldBound.height;
            if (float.IsNaN(bounds.width) || float.IsNaN(rootWidth) || rootWidth <= 1f || rootHeight <= 1f) return false;
            float k = Screen.width / rootWidth;
            rect = Rect.MinMaxRect(bounds.xMin * k, (rootHeight - bounds.yMax) * k, bounds.xMax * k, (rootHeight - bounds.yMin) * k);
            return rect.width > 0 && rect.height > 0;
        }

        /// <summary>
        /// Converts a chrome reference size (1920x1080 canvas, match 0.5) to this document's panel units so the fallback
        /// wheel meets the same type floor as the chrome. The scene's PanelSettings (reference 1559x2160) renders one panel
        /// unit at about 0.78 chrome units on 16:9 screens, so a raw 14 was drawn at about 11 chrome units.
        /// </summary>
        private float PanelFont(float referenceSize) => Mathf.Max(referenceSize, FAA.Customization.FaaHudStyle.Chrome) * ChromeToPanelScale();

        public float ChromeToPanelScale()
        {
            float rootWidth = _root != null ? _root.resolvedStyle.width : float.NaN;
            if (float.IsNaN(rootWidth) || rootWidth <= 1f || Screen.width <= 0 || Screen.height <= 0) return 1.29f;
            float panelPixelsPerUnit = Screen.width / rootWidth;
            float chromePixelsPerUnit = Mathf.Sqrt(Screen.width / 1920f * (Screen.height / 1080f));
            return Mathf.Max(1f, chromePixelsPerUnit / Mathf.Max(.01f, panelPixelsPerUnit));
        }

        private void UpdateAnimations()
        {
            if (!_isAnimating && !_isOpen) return;

            float targetOpen = _isOpen ? 1f : 0f;
            float openSpeed = _isOpen ? 1f / openDuration : 1f / closeDuration;

#if UNITY_EDITOR
            float deltaTime = Application.isPlaying ? Time.unscaledDeltaTime : 0.016f; // 60fps for editor preview
#else
            float deltaTime = Time.unscaledDeltaTime;
#endif
            _openProgress = Mathf.MoveTowards(_openProgress, targetOpen, deltaTime * openSpeed);

            float curvedOpen = reducedMotion ? _openProgress : springCurve.Evaluate(_openProgress);
            float clampedOpen = Mathf.Clamp01(curvedOpen);

            if (_scrim != null)
            {
                bool showBackdrop = useBackdrop && (_isOpen || _isAnimating);
                _scrim.style.display = showBackdrop ? DisplayStyle.Flex : DisplayStyle.None;
                _scrim.style.opacity = showBackdrop ? clampedOpen : 0f;
            }

            _menuRoot.style.opacity = clampedOpen;

            // Reserve space for a bounded command column instead of throwing
            // command cards outside the wheel/viewport. Scale from panel units,
            // so both 1080p and 4K panel settings keep the entire menu reachable.
            float availableWidth = _root.resolvedStyle.width;
            float availableHeight = _root.resolvedStyle.height;
            float contentWidth = _subMenuOpen ? 820f : 520f;
            float menuScale = CalculateMenuScale(availableWidth, availableHeight, contentWidth, 560f);
            _menuRoot.style.scale = new Scale(new Vector3(menuScale, menuScale, 1f));
            _menuRoot.style.marginLeft = _subMenuOpen ? -145f * menuScale : 0f;

            // Animate ring background
            float ringScale = 0.8f + 0.2f * curvedOpen;
            _ringBackground.style.scale = new Scale(new Vector3(ringScale, ringScale, 1));
            _ringBackground.style.opacity = clampedOpen;

            // Animate main segments - positioned on ring between inner and middle radius
            float segmentWidth = MainSegmentWidth;
            float segmentHeight = MainSegmentHeight;
            for (int i = 0; i < _mainSegments.Count; i++)
            {
                var seg = _mainSegments[i];
                if (seg.Category == null) continue;

                float stagger = reducedMotion ? 0f : i * mainSegmentStagger;
                float segProgress = Mathf.Clamp01((_openProgress - stagger) / (1f - stagger));
                float segCurved = reducedMotion ? segProgress : springCurve.Evaluate(segProgress);

                // Position in circle - center of the ring between inner and middle
                float angleRad = (seg.Angle + _rotationOffset) * Mathf.Deg2Rad;
                float radius = (innerRadius + middleRadius) / 2 * Mathf.Lerp(0.96f, 1f, segProgress);
                float x = Mathf.Cos(angleRad) * radius;
                float y = Mathf.Sin(angleRad) * radius;

                // Center the segment on the calculated position
                seg.Container.style.left = x - segmentWidth / 2;
                seg.Container.style.top = y - segmentHeight / 2;
                seg.Container.style.opacity = segProgress;

                // Scale based on selection with spring effect
                float baseScale = Mathf.Lerp(0.96f, 1f, segProgress);
                float selectionBoost = GetMainSegmentScaleBoost(seg, i);
                seg.Container.style.scale = new Scale(new Vector3(baseScale * selectionBoost, baseScale * selectionBoost, 1));
            }

            // Animate sub-menu
            float subMenuDelta = Application.isPlaying ? Time.unscaledDeltaTime : 0.016f;
            if (_subMenuOpen)
            {
                _subMenuProgress = Mathf.MoveTowards(_subMenuProgress, 1f, subMenuDelta / subMenuExpandDuration);
            }
            else
            {
                _subMenuProgress = Mathf.MoveTowards(_subMenuProgress, 0f, subMenuDelta / subMenuExpandDuration);
            }

            if (_subMenuProgress > 0)
            {
                float subWidth = SubSegmentWidth;
                float subHeight = SubSegmentHeight;
                int visibleCount = _subSegments.Count(s => s.IsVisible);

                for (int i = 0; i < _subSegments.Count; i++)
                {
                    var seg = _subSegments[i];
                    if (!seg.IsVisible) continue;

                    float stagger = reducedMotion ? 0f : i * subSegmentStagger;
                    float segProgress = Mathf.Clamp01((_subMenuProgress - stagger) / (1f - stagger));
                    segProgress = reducedMotion ? segProgress : bounceCurve.Evaluate(segProgress);

                    Rect commandRect = GetCommandRect(i, visibleCount);

                    seg.Container.style.display = DisplayStyle.Flex;
                    seg.Container.style.left = commandRect.x + 10f * (1f - segProgress);
                    seg.Container.style.top = commandRect.y;
                    seg.Container.style.opacity = segProgress;
                    float hoverBoost = _selectedSubIndex == i ? 1f + hoverScaleBoost : 1f;
                    seg.Container.style.scale = new Scale(new Vector3(hoverBoost, hoverBoost, 1));
                }
                _commandHeading.style.top = -(visibleCount * (subHeight + 8f)) / 2f - 28f;
                _commandHeading.style.opacity = _subMenuProgress;
                _commandHeading.text = _selectedMainIndex >= 0 ? _mainSegments[_selectedMainIndex].Category.DisplayName.ToUpperInvariant() : "COMMANDS";
            }
            else
            {
                foreach (var seg in _subSegments)
                {
                    seg.Container.style.display = DisplayStyle.None;
                }
                _commandHeading.style.opacity = 0f;
            }

            // Animate center info
            float centerScale = 0.9f + 0.1f * curvedOpen;
            _centerInfo.style.scale = new Scale(new Vector3(centerScale, centerScale, 1));
            _centerInfo.style.opacity = clampedOpen;

            // Check animation complete
            if (Mathf.Approximately(_openProgress, targetOpen) &&
                (!_subMenuOpen || Mathf.Approximately(_subMenuProgress, _subMenuOpen ? 1f : 0f)))
            {
                _isAnimating = false;
                if (!_isOpen)
                {
                    _menuRoot.style.display = DisplayStyle.None;
                    if (_scrim != null)
                    {
                        _scrim.style.display = DisplayStyle.None;
                        _scrim.style.opacity = 0f;
                    }
                }
            }
        }

        private float GetMainSegmentScaleBoost(MainSegment seg, int index)
        {
            float boost = 1f;
            if (_selectedMainIndex == index)
            {
                boost += hoverScaleBoost;
            }

            if (seg.IsHovered)
            {
                boost += hoverScaleBoost * 0.5f;
            }

            return boost;
        }

        private void UpdateGestureRecognition()
        {
            if (!useGestures) return;

            Vector2 currentMousePos = Input.mousePosition;

            // Circular gesture detection
            Vector2 center = new Vector2(Screen.width / 2, Screen.height / 2);
            Vector2 toCenter = currentMousePos - center;
            Vector2 prevToCenter = _lastMousePos - center;

            float currentAngle = Mathf.Atan2(toCenter.y, toCenter.x);
            float prevAngle = Mathf.Atan2(prevToCenter.y, prevToCenter.x);
            float angleDelta = Mathf.DeltaAngle(prevAngle * Mathf.Rad2Deg, currentAngle * Mathf.Rad2Deg);

            if (Mathf.Abs(angleDelta) > 1f && toCenter.magnitude > innerRadius && toCenter.magnitude < outerRadius * 1.5f)
            {
                _gestureAccumulator += angleDelta;

                // Update gesture indicator
                if (_gestureIndicator != null)
                {
                    float indicatorOpacity = Mathf.Clamp01(Mathf.Abs(_gestureAccumulator) / 30f);
                    _gestureIndicator.style.opacity = indicatorOpacity;
                    // Rotate appears to require different parameters in this Unity version
                    // For now, skip direct rotation assignment
                    // _gestureIndicator.style.rotate = new Rotate(Angle.Degrees(_gestureAccumulator));
                }

                // Apply rotation to menu
                if (Mathf.Abs(_gestureAccumulator) > 45f)
                {
                    int direction = (int)Mathf.Sign(_gestureAccumulator);
                    NavigateMenu(direction);
                    _gestureAccumulator = 0;
                }
            }

            _lastMousePos = currentMousePos;
        }

        private void UpdateRipples()
        {
            if (!useRippleEffect) return;

            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                var ripple = _ripples[i];
                ripple.Progress += Time.unscaledDeltaTime * ripple.Speed;

                if (ripple.Progress >= 1f)
                {
                    _rippleContainer.Remove(ripple.Element);
                    _ripples.RemoveAt(i);
                    continue;
                }

                float scale = 0.5f + ripple.Progress * 2f;
                float alpha = (1f - ripple.Progress) * 0.5f;

                ripple.Element.style.scale = new Scale(new Vector3(scale, scale, 1));
                ripple.Element.style.opacity = alpha;
                ripple.Element.style.backgroundColor = new Color(ripple.Color.r, ripple.Color.g, ripple.Color.b, alpha);
            }
        }

        private void UpdatePulseEffect()
        {
            if (_selectedMainIndex < 0 || _selectedMainIndex >= _mainSegments.Count) return;

            var seg = _mainSegments[_selectedMainIndex];
            if (seg.Category == null) return;

            float pulse = 0.30f + 0.08f * Mathf.Sin(Time.unscaledTime * 3f);
            seg.Background.style.backgroundColor = WithAlpha(seg.Category.Color, pulse);
        }

        private void CreateRipple(Vector2 position, Color color)
        {
            if (!useRippleEffect) return;

            var ripple = new VisualElement();
            ripple.style.position = Position.Absolute;
            ripple.style.width = 20;
            ripple.style.height = 20;
            ripple.style.left = position.x - 10;
            ripple.style.top = position.y - 10;
            SetRadius(ripple, 10);
            ripple.style.backgroundColor = WithAlpha(color, 0.28f);

            _rippleContainer.Add(ripple);

            _ripples.Add(new Ripple
            {
                Element = ripple,
                Progress = 0,
                Speed = 2f,
                Color = color
            });
        }

        private void NavigateMenu(int direction)
        {
            if (_categories.Count == 0) return;

            if (_subMenuOpen)
            {
                var visibleIndexes = _subSegments
                    .Where(s => s.IsVisible)
                    .Select(s => s.Index)
                    .ToList();
                if (visibleIndexes.Count == 0)
                {
                    return;
                }

                int currentPosition = visibleIndexes.IndexOf(_selectedSubIndex);
                if (currentPosition < 0)
                {
                    currentPosition = direction > 0 ? -1 : 0;
                }

                int newPosition = (currentPosition + direction + visibleIndexes.Count) % visibleIndexes.Count;
                SelectSubSegment(visibleIndexes[newPosition]);
            }
            else
            {
                int newIndex = (_selectedMainIndex + direction + _categories.Count) % _categories.Count;
                SelectMainSegment(newIndex);
            }

            PlaySound(selectSound);
        }

        private void OnMainSegmentHover(int index, bool hovered)
        {
            if (!_isOpen) return;

            _mainSegments[index].IsHovered = hovered;
            SetMainSegmentHover(_mainSegments[index], hovered || _selectedMainIndex == index);

            // Hover is only a highlight. A deliberate tap changes category;
            // crossing another card on the way to a command must not replace it.
        }

        private void OnMainSegmentClick(int index)
        {
            if (!_isOpen) return;

            SelectMainSegment(index);

            if (enableSubMenus && _mainSegments[index].Category != null)
            {
                OpenSubMenu(index);
            }
            else
            {
                ExecuteMainCommand(index);
            }

            PlaySound(executeSound);
        }

        private void OnSubSegmentHover(int index, bool hovered)
        {
            if (!_isOpen || !_subMenuOpen) return;

            if (hovered && _selectedSubIndex != index && _subSegments[index].IsVisible)
            {
                SelectSubSegment(index);
            }

            if (_subSegments[index].IsVisible)
            {
                SetSubSegmentHover(_subSegments[index], hovered);
            }
        }

        private void OnSubSegmentClick(int index)
        {
            if (!_isOpen || !_subMenuOpen) return;
            if (!_subSegments[index].IsVisible) return;

            ExecuteSubCommand(index);
        }

        private void SelectMainSegment(int index)
        {
            if (_selectedMainIndex == index) return;

            if (_subMenuOpen && _selectedMainIndex >= 0 && _selectedMainIndex != index)
            {
                _subMenuOpen = false;
                _subMenuProgress = 0f;
                _selectedSubIndex = -1;
                foreach (var subSeg in _subSegments)
                {
                    subSeg.IsVisible = false;
                    subSeg.Container.style.display = DisplayStyle.None;
                }
            }

            // Deselect previous
            if (_selectedMainIndex >= 0)
            {
                var prev = _mainSegments[_selectedMainIndex];
                prev.Container.RemoveFromClassList("adv-main-selected");
                SetMainSegmentHover(prev, prev.IsHovered);
            }

            _selectedMainIndex = index;
            var seg = _mainSegments[index];
            seg.Container.AddToClassList("adv-main-selected");
            SetMainSegmentHover(seg, true);

            // Update center info
            if (seg.Category != null)
            {
                _centerTitle.text = seg.Category.DisplayName;
                _centerTitle.style.color = seg.Category.Color;
                _centerSubtitle.text = $"{seg.Category.Commands.Count} commands";
            }

            CreateRipple(Vector2.zero, seg.Category?.Color ?? Color.white);
            OnCategoryChanged?.Invoke(seg.Category?.Id);
        }

        private void SelectSubSegment(int index)
        {
            if (_selectedSubIndex == index) return;

            if (_selectedSubIndex >= 0 && _selectedSubIndex < _subSegments.Count)
            {
                SetSubSegmentHover(_subSegments[_selectedSubIndex], false);
            }

            _selectedSubIndex = index;
            SetSubSegmentHover(_subSegments[index], true);

            var cmd = _subSegments[index].Command;
            if (cmd != null)
            {
                _centerSubtitle.text = cmd.DisplayName;
            }
        }

        private void SetMainSegmentHover(MainSegment seg, bool active)
        {
            if (seg == null || seg.Category == null) return;

            Color accent = seg.Category.Color;
            seg.Background.style.backgroundColor = WithAlpha(accent, active ? 0.22f : 0.08f);
            seg.Container.style.backgroundColor = active
                ? new Color(0.018f, 0.10f, 0.095f, segmentTransparency)
                : WithAlpha(SegmentBackgroundColor, segmentTransparency);
            SetBorderColor(seg.Container, active ? WithAlpha(accent, 0.82f) : SegmentBorderColor);
            seg.IconContainer.style.opacity = active ? 1f : 0.86f;
            seg.NameLabel.style.color = active
                ? new Color(0.95f, 1f, 0.96f, 1f)
                : new Color(0.86f, 0.92f, 0.90f, 0.94f);
        }

        private void SetSubSegmentHover(SubSegment seg, bool hovered)
        {
            if (seg == null || seg.Command == null) return;

            var baseColor = seg.Command.Color;
            float bgAlpha = hovered ? 0.28f : 0.10f;
            var borderColor = Color.Lerp(SubBorderBaseColor, baseColor, hovered ? 0.8f : 0.2f);

            float scale = hovered ? 1f + hoverScaleBoost : 1f;
            seg.Container.style.scale = new Scale(new Vector3(scale, scale, 1f));
            seg.IconContainer.style.scale = new Scale(new Vector3(hovered ? 1.08f : 1f, hovered ? 1.08f : 1f, 1f));
            seg.IconContainer.style.opacity = hovered ? 1f : 0.9f;

            SetBorderColor(seg.Container, borderColor);

            seg.Background.style.backgroundColor = WithAlpha(baseColor, bgAlpha);
            seg.NameLabel.style.color = hovered
                ? new Color(0.95f, 0.97f, 1f, 1f)
                : new Color(0.85f, 0.90f, 0.95f, 0.95f);
        }

        private void OpenSubMenu(int mainIndex)
        {
            var category = _mainSegments[mainIndex].Category;
            if (category == null) return;

            // If already showing sub-menu for different category, close it first
            // to prevent lingering items from previous hover
            if (_subMenuOpen && _selectedMainIndex != mainIndex)
            {
                // Hide all current sub-segments immediately
                foreach (var seg in _subSegments)
                {
                    seg.IsVisible = false;
                    seg.Container.style.display = DisplayStyle.None;
                }
                _subMenuOpen = false;
                _subMenuProgress = 0;
                _selectedSubIndex = -1;
            }

            _subMenuOpen = true;
            int visibleCommandCount = Mathf.Min(category.Commands.Count, _subSegments.Count);

            // Setup sub segments
            for (int i = 0; i < _subSegments.Count; i++)
            {
                var seg = _subSegments[i];
                if (i < visibleCommandCount)
                {
                    seg.Command = category.Commands[i];
                    TrySetIcon(seg.IconImage, seg.Command.IconPath, Color.white);
                    seg.NameLabel.text = CommandLabel(seg.Command);
                    seg.Background.style.backgroundColor = WithAlpha(category.Color, 0.10f);
                    seg.IsVisible = true;
                    SetSubSegmentHover(seg, false);
                }
                else
                {
                    seg.IsVisible = false;
                    seg.Command = null;
                    seg.Container.style.display = DisplayStyle.None;
                }
            }

            _selectedSubIndex = -1;
            _centerSubtitle.text = category.Commands.Count > visibleCommandCount
                ? $"{visibleCommandCount}/{category.Commands.Count} commands"
                : $"{category.Commands.Count} commands";
        }

        private static string CommandLabel(MenuCommand cmd)
        {
            if (cmd == null) return string.Empty;
            string state = cmd.StateText?.Invoke();
            return string.IsNullOrEmpty(state) ? cmd.DisplayName : cmd.DisplayName + "   " + state;
        }

        private void RefreshSubLabels()
        {
            foreach (var seg in _subSegments)
                if (seg.IsVisible && seg.Command != null) seg.NameLabel.text = CommandLabel(seg.Command);
        }

        private void CloseSubMenu()
        {
            _subMenuOpen = false;
            _selectedSubIndex = -1;

            if (_selectedMainIndex < 0 || _selectedMainIndex >= _mainSegments.Count)
            {
                return;
            }

            var seg = _mainSegments[_selectedMainIndex];
            if (seg.Category != null)
            {
                _centerSubtitle.text = $"{seg.Category.Commands.Count} commands";
            }
        }

        private void ExecuteMainCommand(int index)
        {
            var category = _mainSegments[index].Category;
            if (category?.Commands.Count > 0)
            {
                ExecuteCommand(category.Commands[0]);
            }
        }

        private void ExecuteSubCommand(int index)
        {
            var cmd = _subSegments[index].Command;
            if (cmd != null)
            {
                ExecuteCommand(cmd);
            }
        }

        private void ExecuteCommand(MenuCommand cmd)
        {
            if (cmd == null) return;
            if (cmd.Execute != null)
            {
                // Toggle rows: run, then show the new state in place (the menu stays open until Tab / Esc / click outside).
                cmd.Execute();
                OnCommandExecuted?.Invoke(cmd);
                RefreshSubLabels();
                return;
            }

            OnCommandExecuted?.Invoke(cmd);

            if (cmd != null && string.Equals(cmd.TargetId, "hud_opacity", StringComparison.OrdinalIgnoreCase))
            {
                ExecuteHudOpacityPreset(cmd.CommandName);
                SetMenuOpen(false);
                return;
            }

            var registry = VoiceCommandRegistry.Instance;
            if (registry != null)
            {
                registry.ExecuteCommand(cmd.TargetId, cmd.CommandName, cmd.DefaultParams);
            }

            SetMenuOpen(false);
        }

        private static void ExecuteHudOpacityPreset(string commandName)
        {
            FAA.Customization.FaaHudOpacityController controller =
                UnityEngine.Object.FindAnyObjectByType<FAA.Customization.FaaHudOpacityController>();
            if (controller == null)
            {
                return;
            }

            switch ((commandName ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "set_100": controller.SetFullOpacity(); break;
                case "set_80": controller.SetHighOpacity(); break;
                case "set_60": controller.SetMediumOpacity(); break;
                case "set_40": controller.SetLowOpacity(); break;
            }
        }

        private void PlaySound(AudioClip clip)
        {
            if (clip != null && Camera.main != null)
            {
                AudioSource.PlayClipAtPoint(clip, Camera.main.transform.position, 0.5f);
            }
        }

        public void ToggleMenu()
        {
            var chrome = Application.isPlaying ? DockedChrome : null;
            if (chrome != null) { SetChromeFlyoutOpen(chrome, !chrome.CommandsVisible); return; }
            SetMenuOpen(!_isOpen);
        }

        private FAA.Customization.FaaPilotChrome DockedChrome => LauncherDocked ? FAA.Customization.FaaPilotChrome.Current : null;

        /// <summary>
        /// Opens or closes the COMMANDS flyout of the pilot chrome (Play Mode path; public for tests). Opening rebuilds the
        /// rows from the live scene; flight symbology is never hidden, dimmed or covered.
        /// </summary>
        public void SetChromeFlyoutOpen(FAA.Customization.FaaPilotChrome chrome, bool open)
        {
            if (chrome == null) return;
            if (open)
            {
                var registry = VoiceCommandRegistry.Instance;
                chrome.SetCommandRows(BuildCommandRows(registry, false));
                chrome.SetCommandsVisible(true);
            }
            else chrome.SetCommandsVisible(false);
            SyncDockedState(chrome);
        }

        /// <summary>Follows the chrome flyout (it also closes on Esc, CLOSE, a click outside or another flyout opening).</summary>
        private void SyncDockedState(FAA.Customization.FaaPilotChrome chrome)
        {
            bool visible = chrome != null && chrome.CommandsVisible;
            if (_dockedOpen != visible)
            {
                _dockedOpen = visible;
                if (visible) { OnMenuOpened?.Invoke(); PlaySound(openSound); }
                else { OnMenuClosed?.Invoke(); PlaySound(closeSound); }
            }
            if (chrome != null) chrome.SetButtonState(ChromeCommandsId, chrome.IsButtonVisible(ChromeCommandsId), visible);
        }

        private void OnBackdropClick(ClickEvent evt)
        {
            if (!Application.isPlaying || !_isOpen || !closeOnBackdropClick)
            {
                return;
            }

            if (evt.target == _scrim)
            {
                SetMenuOpen(false);
                evt.StopPropagation();
            }
        }

        private void PrimeInitialSelection()
        {
            int selectableCount = Mathf.Min(_categories.Count, _mainSegments.Count);
            if (selectableCount <= 0)
            {
                _selectedMainIndex = -1;
                _selectedSubIndex = -1;
                if (_centerTitle != null)
                {
                    _centerTitle.text = "HUD";
                    _centerTitle.style.color = new Color(0.35f, 1f, 0.72f, 1f);
                }
                if (_centerSubtitle != null)
                {
                    _centerSubtitle.text = "No commands";
                }
                return;
            }

            _selectedMainIndex = -1;
            _selectedSubIndex = -1;
            int index = Mathf.Clamp(SelectedCategory, 0, selectableCount - 1);
            SelectMainSegment(index);

            if (enableSubMenus)
            {
                OpenSubMenu(index);
            }
        }

        /// <summary>
        /// Visibility commands own the state they set; the menu must never undo them when it closes. (The menu no longer
        /// hides or restores any HUD root at all; this predicate documents and tests that policy.)
        /// </summary>
        private static bool ShouldLetHudCommandOwnVisibility(MenuCommand cmd)
        {
            if (cmd == null ||
                string.IsNullOrWhiteSpace(cmd.TargetId) ||
                string.IsNullOrWhiteSpace(cmd.CommandName))
            {
                return false;
            }

            string targetId = cmd.TargetId.Trim();
            string commandName = cmd.CommandName.Trim();

            if (string.Equals(targetId, "symbology", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(commandName, "show", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(commandName, "hide", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(targetId, "weather_radar", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(targetId, "traffic_radar", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(commandName, "show_panel", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(commandName, "hide_panel", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(commandName, "toggle_panel", StringComparison.OrdinalIgnoreCase);
            }

            if (string.Equals(targetId, "indicator_system", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(commandName, "show_all_indicators", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(commandName, "hide_all_indicators", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        public void SetMenuOpen(bool open)
        {
            var chrome = Application.isPlaying ? DockedChrome : null;
            if (chrome != null) { SetChromeFlyoutOpen(chrome, open); return; }

            if (_menuRoot == null)
            {
                SetupUI();
            }

            if (_menuRoot == null) return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (open)
                {
                    LoadCommands();
                    ShowEditorPreview();
                }
                else
                {
                    HideEditorPreview();
                }

                return;
            }
#endif

            if (_isOpen == open) return;

            _isOpen = open;
            _isAnimating = true;

            if (_isOpen)
            {
                if (_scrim != null)
                {
                    _scrim.style.display = useBackdrop ? DisplayStyle.Flex : DisplayStyle.None;
                    _scrim.style.opacity = 0f;
                }
                _menuRoot.style.display = DisplayStyle.Flex;
                if (_collapsedButton != null)
                {
                    _collapsedButton.style.display = DisplayStyle.None;
                }

                _lastMousePos = Input.mousePosition;
                _gestureAccumulator = 0;
                LoadCommands();
                PrimeInitialSelection();
                OnMenuOpened?.Invoke();
                PlaySound(openSound);
            }
            else
            {
                _subMenuOpen = false;
                _subMenuProgress = 0;
                _selectedMainIndex = -1;
                _selectedSubIndex = -1;
                OnMenuClosed?.Invoke();
                PlaySound(closeSound);

                // Show collapsed button when menu closes (not when docked in the chrome bar)
                if (startCollapsed && !LauncherDocked && _collapsedButton != null)
                {
                    CollapseToButton();
                }
            }
        }

        private void OnRegistryUpdated()
        {
            if (_dockedOpen)
            {
                // A target registered or left while the flyout is open: rebuild its rows (never per frame).
                var chrome = FAA.Customization.FaaPilotChrome.Current;
                if (chrome != null) chrome.SetCommandRows(BuildCommandRows(VoiceCommandRegistry.Instance, false));
            }
            if (_isOpen)
            {
                LoadCommands();
            }
        }

        private string FormatCommandName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Command";
            }

            return string.Join(" ", name.Split('_')
                .Where(w => !string.IsNullOrEmpty(w))
                .Select(w => char.ToUpper(w[0]) + w.Substring(1)));
        }

        private string GetCommandIconPath(string targetId, string commandName, string displayName)
        {
            var lowerName = commandName.ToLowerInvariant();
            var lowerDisplay = displayName?.ToLowerInvariant() ?? string.Empty;

            if (lowerName.Contains("set_white")) return "VoiceControl/IconsSvg/ColorWhite";
            if (lowerName.Contains("set_green")) return "VoiceControl/IconsSvg/ColorGreen";
            if (lowerName.Contains("set_black")) return "VoiceControl/IconsSvg/ColorBlack";

            if (lowerDisplay.Contains("weather briefing") || lowerName.Contains("weather_briefing") || lowerName.Contains("analyze_weather"))
                return "VoiceControl/IconsSvg/WeatherBriefing";

            if (lowerDisplay.Contains("traffic briefing") || lowerName.Contains("sectional_briefing") || lowerName.Contains("analyze_sectional"))
                return "VoiceControl/IconsSvg/TrafficBriefing";

            if (lowerName.Contains("hide")) return "VoiceControl/IconsSvg/Hide";
            if (lowerName.Contains("show")) return "VoiceControl/IconsSvg/Show";

            return "VoiceControl/IconsSvg/Command";
        }

        // Public API
        /// <summary>True while the COMMANDS flyout (Play Mode) or the fallback wheel is open.</summary>
        public bool IsOpen => _isOpen || _dockedOpen;
        /// <summary>Always false at runtime: the COMMANDS menu never hides flight symbology, whatever the scene serialized.</summary>
        public bool HideHudWhileOpen => hideHudWhileOpen;
        public bool IsSubMenuOpen => _subMenuOpen;
        public int SelectedCategory => _selectedMainIndex;
        public float MenuTransparency => menuTransparency;

        public void SetCategoryEnabled(string categoryId, bool enabled)
        {
            var seg = _mainSegments.FirstOrDefault(s => s.Category?.Id == categoryId);
            if (seg != null)
            {
                seg.Container.SetEnabled(enabled);
            }
        }

        /// <summary>
        /// Adjusts the overall menu transparency at runtime.
        /// </summary>
        /// <param name="overall">Overall transparency multiplier (0.3-1.0)</param>
        /// <param name="ring">Ring background transparency (0.5-1.0)</param>
        /// <param name="segments">Segment transparency (0.5-1.0)</param>
        /// <param name="center">Center hub transparency (0.7-1.0)</param>
        public void SetTransparency(float overall = -1f, float ring = -1f, float segments = -1f, float center = -1f)
        {
            if (overall >= 0) menuTransparency = Mathf.Clamp(overall, 0.3f, 1f);
            if (ring >= 0) ringBackgroundTransparency = Mathf.Clamp(ring, 0.5f, 1f);
            if (segments >= 0) segmentTransparency = Mathf.Clamp(segments, 0.5f, 1f);
            if (center >= 0) centerTransparency = Mathf.Clamp(center, 0.7f, 1f);

            // Re-apply styles with new transparency values
            if (_menuRoot != null)
            {
                ApplyInlineStyles();
            }
        }

        #region Editor API

        // Properties for custom editor access
        public float InnerRadius => innerRadius;
        public float MiddleRadius => middleRadius;
        public float OuterRadius => outerRadius;
        public float CollapsedButtonSize => collapsedButtonSize;
        public Vector2 CollapsedButtonPosition => collapsedButtonPosition;
        public bool CollapsedButtonTopRight => collapsedButtonTopRight;
        public bool DockLauncherInPilotChrome => dockLauncherInPilotChrome;
        /// <summary>True when the floating launcher is replaced by the chrome bar's COMMANDS button.</summary>
        public bool LauncherDocked => dockLauncherInPilotChrome && Application.isPlaying && FAA.Customization.FaaPilotChrome.Ensure() != null;
        public const string ChromeCommandsId = "commands";
        private bool _chromeLauncherRegistered;

        /// <summary>Registers the COMMANDS bar button (idempotent; re-registers if the chrome was recreated).</summary>
        private void SyncChromeLauncher()
        {
            if (!LauncherDocked) { _chromeLauncherRegistered = false; return; }
            var chrome = FAA.Customization.FaaPilotChrome.Current;
            if (!_chromeLauncherRegistered || !chrome.HasButton(ChromeCommandsId))
            {
                chrome.AddButton(ChromeCommandsId, FAA.Customization.FaaChromeCluster.Left, 40, "COMMANDS", KeyLabel(toggleKey), ToggleMenu, "CMD");
                chrome.SetHelpEntry(KeyLabel(toggleKey).ToUpperInvariant(), HelpMeaning, 40);
                _chromeLauncherRegistered = true;
                if (_collapsedButton != null) _collapsedButton.style.display = DisplayStyle.None;
            }
            SyncDockedState(chrome);
        }
        /// <summary>Key-list line for the COMMANDS key: what the menu actually contains.</summary>
        public const string HelpMeaning = "Commands: radars, screen cues, HUD";
        private static string KeyLabel(KeyCode key) => key == KeyCode.Tab ? "Tab" : key == KeyCode.None ? null : key.ToString();
        public float OpenDuration => openDuration;
        public float CloseDuration => closeDuration;
        public float SubMenuExpandDuration => subMenuExpandDuration;
        public float RingTransparency => ringBackgroundTransparency;
        public float SegmentTransparency => segmentTransparency;
        public float CenterTransparency => centerTransparency;

        /// <summary>
        /// Updates radial dimensions and refreshes the UI.
        /// </summary>
        public void SetRadialDimensions(float inner, float middle, float outer)
        {
            innerRadius = Mathf.Max(90f, inner);
            middleRadius = Mathf.Max(innerRadius + 90f, middle);
            outerRadius = Mathf.Max(middleRadius + 80f, outer);

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                RefreshUI();
            }
#endif
        }

        /// <summary>
        /// Updates collapsed button settings and refreshes the UI.
        /// </summary>
        public void SetCollapsedButton(float size, Vector2 position)
        {
            collapsedButtonSize = Mathf.Clamp(size, 48f, 96f);
            collapsedButtonPosition = position;

            if (_collapsedButton != null)
            {
                _collapsedButton.style.width = collapsedButtonSize;
                _collapsedButton.style.height = collapsedButtonSize;
                ApplyCollapsedButtonAnchor();
            }
        }

        private void ApplyCollapsedButtonAnchor()
        {
            if (_collapsedButton == null)
            {
                return;
            }

            if (collapsedButtonTopRight)
            {
                _collapsedButton.style.left = new StyleLength(StyleKeyword.Auto);
                _collapsedButton.style.bottom = new StyleLength(StyleKeyword.Auto);
                _collapsedButton.style.right = collapsedButtonPosition.x;
                _collapsedButton.style.top = collapsedButtonPosition.y;
                return;
            }

            _collapsedButton.style.right = new StyleLength(StyleKeyword.Auto);
            _collapsedButton.style.top = new StyleLength(StyleKeyword.Auto);
            _collapsedButton.style.left = collapsedButtonPosition.x;
            _collapsedButton.style.bottom = collapsedButtonPosition.y;
        }

        /// <summary>
        /// Updates animation durations.
        /// </summary>
        public void SetAnimationDurations(float open, float close, float subMenu)
        {
            openDuration = Mathf.Clamp(open, 0.05f, 0.75f);
            closeDuration = Mathf.Clamp(close, 0.05f, 0.5f);
            subMenuExpandDuration = Mathf.Clamp(subMenu, 0.05f, 0.5f);
        }

        /// <summary>
        /// Applies the compact FAA cockpit preset used by ExperimentScene.
        /// </summary>
        public void ApplyAviationHudPreset(bool refresh = true)
        {
            innerRadius = 88f;
            middleRadius = 206f;
            outerRadius = 308f;
            collapsedButtonSize = 48f;
            collapsedButtonPosition = new Vector2(34f, 34f);
            collapsedButtonTopRight = true;
            maxSubSegmentCount = 6;
            openDuration = 0.28f;
            closeDuration = 0.18f;
            subMenuExpandDuration = 0.18f;
            mainSegmentStagger = 0.025f;
            subSegmentStagger = 0.02f;
            hoverScaleBoost = 0.015f;
            menuTransparency = 1f;
            ringBackgroundTransparency = 0.82f;
            segmentTransparency = 0.96f;
            centerTransparency = 1f;
            // No full-screen backdrop: the menu plate is its only footprint, so the outside view and the HUD stay visible.
            useBackdrop = false;
            backdropOpacity = 0f;
            closeOnBackdropClick = true;
            hideHudWhileOpen = false;
            mainLabelFontSize = 16f;
            subLabelFontSize = 16f;
            centerTitleFontSize = 18f;
            centerSubtitleFontSize = 16f;
            usePulseAnimation = false;
            useGestures = false;
            useRippleEffect = true;
            springCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);
            bounceCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

            if (refresh)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                {
                    RefreshUI();
                    return;
                }
#endif
                ApplyInlineStyles();
            }
        }

        public static float CalculateMenuScale(float width, float height, float contentWidth, float contentHeight)
        {
            if (float.IsNaN(width) || float.IsNaN(height) || width <= 0f || height <= 0f)
                return 1f;
            return Mathf.Clamp(Mathf.Min((width - 40f) / contentWidth, (height - 72f) / contentHeight), 0.2f, 1f);
        }

        public static Rect GetCommandRect(int index, int count) => new Rect(
            274f, (index - (count - 1) * .5f) * (SubSegmentHeight + 8f) - SubSegmentHeight * .5f,
            SubSegmentWidth, SubSegmentHeight);

        #endregion
    }
}
