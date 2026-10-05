using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Customization
{
    /// <summary>Severity of a chrome status report. Colour follows 14 CFR 29.1322: red warning, amber caution, quiet status, green normal.</summary>
    public enum FaaChromeSeverity { Nominal=0, Status=1, Caution=2, Warning=3 }
    public enum FaaChromeCluster { Left=0, Right=1 }

    /// <summary>
    /// The single owner of non-flight screen chrome (zone Z6): one slim bar at the bottom of the forward view
    /// (reference y 1018-1068 from the top), one plate style and one typography. Left cluster: merged status chip,
    /// BRIEF, CUES, COMMANDS, help. Right cluster: view destinations in panel-yaw order plus SETTINGS. Flyouts
    /// (brief, cue controls, commands, key list) open one at a time in a fixed slot above the left end of the bar, outside
    /// the boresight column and the flight columns; the slot width is taken from the registered IAS/TQ keep-outs so a
    /// flyout never touches the left flight column. Every flyout closes with Esc, its own CLOSE button or a click outside
    /// it (commands and key list). Every chrome element registers a <see cref="FaaKeepOutKind.Chrome"/> keep-out so
    /// conformal cues and edge arrows avoid it. Chrome is never dimmed by side-panel inspection; open flyouts are hidden
    /// while an unusual attitude is annunciated (AC 25-11B 5.10.3.2: the whole display declutters together).
    /// Basis: AC 25-11B information management (consistent location, minimal persistent chrome), HF-STD-001B and
    /// DOT/FAA/TC-13/44 (labelled controls with key bindings, 8 mm targets, visible feedback within 250 ms).
    /// </summary>
    [DefaultExecutionOrder(12400), DisallowMultipleComponent]
    public sealed partial class FaaPilotChrome : MonoBehaviour
    {
        // ---- Geometry in reference units (1920x1080 canvas, match 0.5) ----
        public const float BarBottom=12f, BarHeight=50f, ButtonHeight=34f, Gutter=16f, Gap=8f, Inset=8f;
        /// <summary>Bottom edge of every flyout: 8 ref above the bar.</summary>
        public const float FlyoutBottom=BarBottom+BarHeight+8f;
        /// <summary>Left edge of the flyout slot; the slot stays left of the IAS/TQ column (x ~500+ at default size).</summary>
        public const float FlyoutLeft=Gutter;
        /// <summary>Widest flyout (right edge at 484 ref, clear of the IAS box at ~500 and the TQ column at ~520).
        /// <see cref="FlyoutWidth"/> narrows it further when the registered IAS/TQ keep-outs move left.</summary>
        public const float FlyoutMaxWidth=468f;
        /// <summary>Narrowest flyout; below this the text would wrap into an unreadable column.</summary>
        public const float MinFlyoutWidth=380f;
        /// <summary>Clear gap between a flyout's right edge and the nearest flight-column keep-out.</summary>
        public const float FlyoutColumnGap=16f;
        public const float CloseButtonHeight=28f;
        public const float MinButtonWidth=56f, CaptionPadding=12f, KeycapPadding=6f, KeycapHeight=24f, MinCenterGap=24f;
        public const int CanvasOrder=7240, FlyoutOrder=7230;
        /// <summary>Camera-space depth in native XR; matches XR3HeadsetCompatibility.overlayDistanceMeters.</summary>
        public const float XrPlaneDistance=1.2f;
        public const string HostName="FAA Pilot Chrome", CanvasName="FAA Pilot Chrome Canvas", BarName="Pilot chrome bar";

        private sealed class Entry
        {
            public string id, fullCaption, shortCaption, keycap, shown, badge;
            public FaaChromeCluster cluster; public float order; public int seq;
            public RectTransform rect, keyBox; public Image plate, underline; public Button button;
            public TMP_Text caption, keyText, badgeText; public UnityAction action;
            public bool visible=true, active, interactable=true, badgeOn; public float width;
        }

        private static FaaPilotChrome current;
        /// <summary>The live chrome, or null (Unity-null safe).</summary>
        public static FaaPilotChrome Current=>current!=null?current:null;

        private readonly List<Entry> entries=new();
        private readonly Dictionary<string,Action> flyoutClosers=new();
        private readonly List<Rect> keepOutScratch=new();
        private Canvas canvas;
        private RectTransform root, bar, flyouts;
        private CanvasGroup flyoutGroup;
        private Image barPlate;
        private bool built, layoutDirty=true, compact, lastXr, flyoutsDecluttered;
        private float lastCanvasWidth=-1;
        private int sequence;
        private string openFlyout;

        public Canvas Canvas=>canvas;
        public RectTransform Bar=>bar;
        public RectTransform FlyoutRoot=>flyouts;
        public bool Compact=>compact;
        public string OpenFlyoutId=>openFlyout??"";
        /// <summary>True while open flyouts are hidden because an unusual attitude is annunciated.</summary>
        public bool FlyoutsDecluttered=>flyoutsDecluttered;

        // ---- Developer mode (Ctrl+Shift+D): developer key bindings and tools are hidden from the pilot until it is on ----
        public const string DeveloperKeys="CTRL+SHIFT+D";
        private static bool developerMode;
        /// <summary>Developer mode: the key list shows developer bindings and developer-only hotkeys (renderer swap, XR simulator UI) work.</summary>
        public static bool DeveloperMode=>developerMode;
        public static event Action DeveloperModeChanged;
        /// <summary>Every play session starts in pilot mode, also with domain reload disabled.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetDeveloperMode(){developerMode=false;}
        /// <summary>Sets developer mode (Ctrl+Shift+D toggles it). The status chip shows a quiet DEVELOPER MODE while it is on.</summary>
        public static void SetDeveloperMode(bool on)
        {
            if(developerMode==on)return;
            developerMode=on;
            var c=Current;
            if(c!=null)
            {
                if(on)c.ReportStatus("developer",FaaChromeSeverity.Status,"DEVELOPER MODE",null,9,true);else c.ClearStatus("developer");
                c.helpDirty=true;c.RefreshHelpIfVisible();
            }
            DeveloperModeChanged?.Invoke();
        }

        /// <summary>Returns the chrome, creating it in Play Mode. Returns null in Edit Mode so edit-time tools keep their legacy layout.</summary>
        public static FaaPilotChrome Ensure()
        {
            if(current!=null)return current;
            if(!Application.isPlaying)return null;
            var existing=FindAnyObjectByType<FaaPilotChrome>();
            if(existing!=null){existing.Initialize();return existing;}
            var host=new GameObject(HostName);DontDestroyOnLoad(host);
            var chrome=host.AddComponent<FaaPilotChrome>();chrome.Initialize();return chrome;
        }
        /// <summary>Edit-mode test hook: builds a chrome without DontDestroyOnLoad. Destroy its GameObject afterwards.</summary>
        public static FaaPilotChrome CreateForTests()
        {
            var host=new GameObject(HostName+" (test)");
            var chrome=host.AddComponent<FaaPilotChrome>();chrome.Initialize();return chrome;
        }

        private void Awake()=>Initialize();
        private void Initialize()
        {
            if(current!=null&&current!=this){if(Application.isPlaying)Destroy(this);return;}
            current=this;Build();
        }
        private void OnDestroy()
        {
            if(current==this)current=null;
            if(canvas!=null){if(Application.isPlaying)Destroy(canvas.gameObject);else DestroyImmediate(canvas.gameObject);}
        }

        private void Build()
        {
            if(built)return;built=true;
            var go=new GameObject(CanvasName,typeof(RectTransform),typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(FaaCanvasPixelRaycaster),typeof(TrackedDeviceGraphicRaycaster));
            go.transform.SetParent(transform,false);
            canvas=go.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=CanvasOrder;
            var scaler=go.GetComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
            root=(RectTransform)go.transform;
            bar=NewRect(BarName,root);bar.anchorMin=Vector2.zero;bar.anchorMax=new Vector2(1,0);bar.pivot=new Vector2(.5f,0);
            bar.offsetMin=new Vector2(Gutter,BarBottom);bar.offsetMax=new Vector2(-Gutter,BarBottom+BarHeight);
            barPlate=bar.gameObject.AddComponent<Image>();barPlate.color=FaaHudStyle.ChromePlate;barPlate.raycastTarget=true;
            var edge=NewRect("Bar top edge",bar);edge.anchorMin=new Vector2(0,1);edge.anchorMax=Vector2.one;edge.pivot=new Vector2(.5f,1);
            edge.offsetMin=new Vector2(0,-1);edge.offsetMax=Vector2.zero;
            var edgeImage=edge.gameObject.AddComponent<Image>();edgeImage.color=FaaHudStyle.ChromeOutline;edgeImage.raycastTarget=false;
            FaaHudKeepOutRegion.Ensure(bar.gameObject,"chrome:bar",FaaKeepOutKind.Chrome,4f);
            flyouts=NewRect("Pilot chrome flyouts",root);flyouts.anchorMin=Vector2.zero;flyouts.anchorMax=Vector2.one;flyouts.offsetMin=flyouts.offsetMax=Vector2.zero;
            // Created before any flyout so every flyout keep-out sees it: alpha 0 also releases their keep-outs.
            flyoutGroup=flyouts.gameObject.AddComponent<CanvasGroup>();
            BuildStatusChip();
            RegisterBuiltInHelp();
            RegisterCommandsFlyout();
            if(developerMode)ReportStatus("developer",FaaChromeSeverity.Status,"DEVELOPER MODE",null,9,true);
            Relayout();
        }

        // ---------------------------------------------------------------- buttons

        /// <summary>
        /// Adds (or rebinds) a labelled bar button. <paramref name="order"/> sorts left-to-right within the cluster.
        /// <paramref name="keycap"/> shows the key binding in a small key box. <paramref name="shortCaption"/> is used when the bar is narrow.
        /// </summary>
        public Button AddButton(string id,FaaChromeCluster cluster,float order,string caption,string keycap,UnityAction action,string shortCaption=null)
        {
            if(string.IsNullOrEmpty(id))return null;
            Build();
            var e=Find(id);
            if(e==null){e=MakeEntry(id,cluster);entries.Add(e);}
            e.cluster=cluster;e.order=order;e.seq=sequence++;e.fullCaption=caption??"";e.shortCaption=string.IsNullOrEmpty(shortCaption)?e.fullCaption:shortCaption;
            e.action=action;e.button.onClick.RemoveAllListeners();if(action!=null)e.button.onClick.AddListener(action);
            SetKeycap(e,keycap);
            SetAnchors(e);e.shown=null;layoutDirty=true;Relayout();
            return e.button;
        }
        public bool HasButton(string id)=>Find(id)!=null;
        public void RemoveButton(string id)
        {
            var e=Find(id);if(e==null)return;
            entries.Remove(e);if(e.rect!=null){if(Application.isPlaying)Destroy(e.rect.gameObject);else DestroyImmediate(e.rect.gameObject);}
            layoutDirty=true;Relayout();
        }
        /// <summary>Visibility, active (highlighted destination / open flyout) state and optional caption. Cheap when nothing changes.</summary>
        public void SetButtonState(string id,bool visible,bool active,string caption=null,string shortCaption=null)
        {
            var e=Find(id);if(e==null)return;
            bool relayout=false;
            if(e.visible!=visible){e.visible=visible;e.rect.gameObject.SetActive(visible);relayout=true;}
            if(caption!=null&&caption!=e.fullCaption){e.fullCaption=caption;e.shortCaption=shortCaption??caption;relayout=true;}
            else if(shortCaption!=null&&shortCaption!=e.shortCaption){e.shortCaption=shortCaption;relayout=true;}
            if(e.active!=active){e.active=active;ApplyActive(e);}
            if(relayout){layoutDirty=true;Relayout();}
        }
        public void SetButtonInteractable(string id,bool interactable)
        {
            var e=Find(id);if(e==null||e.interactable==interactable)return;
            e.interactable=interactable;e.button.interactable=interactable;
            e.caption.color=interactable?FaaHudStyle.White:FaaHudStyle.WithAlpha(FaaHudStyle.ChromeQuiet,.55f);
        }
        /// <summary>
        /// A state indicator inside a button, separate from its action caption (for example CUES [ON]). Green text when
        /// <paramref name="on"/>, quiet text when off; null or empty removes it. Cheap when nothing changes.
        /// </summary>
        public void SetButtonBadge(string id,string badge,bool on)=>SetButtonBadge(id,badge,on,on?FaaHudStyle.Green:FaaHudStyle.ChromeQuiet);
        /// <summary>Badge with an explicit colour (for example amber for a caution state such as CHECK or FAILED).</summary>
        public void SetButtonBadge(string id,string badge,bool on,Color color)
        {
            var e=Find(id);if(e==null)return;
            if(string.IsNullOrEmpty(badge))badge=null;
            if(e.badge==badge&&e.badgeOn==on&&(e.badgeText==null||e.badgeText.color==color))return;
            bool relayout=e.badge!=badge;
            e.badge=badge;e.badgeOn=on;
            if(badge!=null&&e.badgeText==null)
            {
                e.badgeText=NewText("State badge",e.rect,FaaHudStyle.Chrome,FaaHudStyle.ChromeQuiet);e.badgeText.fontStyle=FontStyles.Bold;
                e.badgeText.alignment=TextAlignmentOptions.MidlineLeft;
                var bt=e.badgeText.rectTransform;bt.anchorMin=new Vector2(0,0);bt.anchorMax=new Vector2(0,1);bt.pivot=new Vector2(0,.5f);bt.sizeDelta=new Vector2(10,0);
            }
            if(e.badgeText!=null)
            {
                e.badgeText.gameObject.SetActive(badge!=null);
                if(badge!=null){e.badgeText.text=badge;e.badgeText.color=color;}
            }
            if(relayout){layoutDirty=true;Relayout();}
        }
        public string ButtonBadge(string id)=>Find(id)?.badge;
        public bool ButtonBadgeOn(string id){var e=Find(id);return e!=null&&e.badgeOn;}
        /// <summary>Re-sorts a button inside its cluster (used to keep view destinations in panel-yaw order).</summary>
        public void SetButtonOrder(string id,float order)
        {
            var e=Find(id);if(e==null||Mathf.Approximately(e.order,order))return;
            e.order=order;layoutDirty=true;Relayout();
        }
        public bool IsButtonVisible(string id){var e=Find(id);return e!=null&&e.visible;}
        public bool IsButtonActive(string id){var e=Find(id);return e!=null&&e.active;}
        public string ButtonCaption(string id){var e=Find(id);return e?.shown??e?.fullCaption;}
        public string ButtonKeycap(string id)=>Find(id)?.keycap;
        public RectTransform ButtonRect(string id)=>Find(id)?.rect;
        public Button ButtonOf(string id)=>Find(id)?.button;
        /// <summary>Visible button ids of a cluster in left-to-right screen order.</summary>
        public List<string> VisibleOrder(FaaChromeCluster cluster)
        {
            var list=new List<string>();
            foreach(var e in Sorted(cluster))if(e.visible)list.Add(e.id);
            return list;
        }
        /// <summary>Invokes a button as if clicked (keyboard routes, tests).</summary>
        public void Press(string id){var e=Find(id);if(e!=null&&e.visible&&e.interactable)e.action?.Invoke();}

        private Entry Find(string id){for(int i=0;i<entries.Count;i++)if(entries[i].id==id)return entries[i];return null;}

        private Entry MakeEntry(string id,FaaChromeCluster cluster)
        {
            var e=new Entry{id=id,cluster=cluster};
            e.rect=NewRect("Chrome "+id,bar);e.rect.sizeDelta=new Vector2(MinButtonWidth,ButtonHeight);
            e.plate=e.rect.gameObject.AddComponent<Image>();e.plate.color=FaaHudStyle.ChromeButton;e.plate.raycastTarget=true;
            var outline=e.rect.gameObject.AddComponent<Outline>();outline.effectColor=FaaHudStyle.ChromeOutline;outline.effectDistance=new Vector2(1,1);
            e.button=e.rect.gameObject.AddComponent<Button>();e.button.targetGraphic=e.plate;
            StyleButton(e.button);
            e.caption=NewText("Caption",e.rect,FaaHudStyle.Chrome,FaaHudStyle.White);
            e.caption.alignment=TextAlignmentOptions.MidlineLeft;
            var ct=e.caption.rectTransform;ct.anchorMin=new Vector2(0,0);ct.anchorMax=new Vector2(0,1);ct.pivot=new Vector2(0,.5f);
            ct.anchoredPosition=new Vector2(CaptionPadding,0);ct.sizeDelta=new Vector2(10,0);
            e.underline=NewRect("Active marker",e.rect).gameObject.AddComponent<Image>();
            var ur=e.underline.rectTransform;ur.anchorMin=new Vector2(0,0);ur.anchorMax=new Vector2(1,0);ur.pivot=new Vector2(.5f,0);
            ur.offsetMin=new Vector2(4,2);ur.offsetMax=new Vector2(-4,5);e.underline.color=FaaHudStyle.Cyan;e.underline.raycastTarget=false;
            e.underline.enabled=false;
            return e;
        }
        private void SetKeycap(Entry e,string keycap)
        {
            e.keycap=string.IsNullOrEmpty(keycap)?null:keycap;
            if(e.keycap==null){if(e.keyBox!=null)e.keyBox.gameObject.SetActive(false);return;}
            if(e.keyBox==null)
            {
                e.keyBox=NewRect("Keycap",e.rect);e.keyBox.anchorMin=e.keyBox.anchorMax=new Vector2(1,.5f);e.keyBox.pivot=new Vector2(1,.5f);
                var img=e.keyBox.gameObject.AddComponent<Image>();img.color=new Color(0,0,0,.28f);img.raycastTarget=false;
                var o=e.keyBox.gameObject.AddComponent<Outline>();o.effectColor=FaaHudStyle.ChromeOutline;o.effectDistance=new Vector2(1,1);
                e.keyText=NewText("Key",e.keyBox,FaaHudStyle.Chrome,FaaHudStyle.ChromeQuiet);e.keyText.alignment=TextAlignmentOptions.Center;
                var kt=e.keyText.rectTransform;kt.anchorMin=Vector2.zero;kt.anchorMax=Vector2.one;kt.offsetMin=kt.offsetMax=Vector2.zero;
            }
            e.keyBox.gameObject.SetActive(true);e.keyText.text=e.keycap;
        }
        private static void SetAnchors(Entry e)
        {
            float x=e.cluster==FaaChromeCluster.Left?0:1;
            e.rect.anchorMin=e.rect.anchorMax=new Vector2(x,.5f);e.rect.pivot=new Vector2(x,.5f);
        }
        private static void ApplyActive(Entry e)
        {
            if(e.id==StatusId)return;
            e.plate.color=e.active?FaaHudStyle.ChromeButtonActive:FaaHudStyle.ChromeButton;
            e.underline.enabled=e.active;
        }

        private readonly List<Entry> sortScratch=new();
        private List<Entry> Sorted(FaaChromeCluster cluster)
        {
            sortScratch.Clear();
            foreach(var e in entries)if(e.cluster==cluster)sortScratch.Add(e);
            sortScratch.Sort((a,b)=>{int c=a.order.CompareTo(b.order);return c!=0?c:a.seq.CompareTo(b.seq);});
            return sortScratch;
        }

        /// <summary>Measures captions and positions both clusters. Runs only when something changed.</summary>
        public void Relayout(){if(built)Relayout(CanvasWidth());}
        public void Relayout(float canvasWidth)
        {
            if(!built)return;
            layoutDirty=false;lastCanvasWidth=canvasWidth;
            float barWidth=Mathf.Max(0,canvasWidth-2*Gutter);
            // Full captions first; fall back to short captions only when the clusters would meet.
            float need=ClusterWidth(FaaChromeCluster.Left,false)+ClusterWidth(FaaChromeCluster.Right,false)+2*Inset+MinCenterGap;
            compact=need>barWidth;
            Place(FaaChromeCluster.Left);Place(FaaChromeCluster.Right);
        }
        /// <summary>Total width of a cluster's visible buttons including gaps.</summary>
        public float ClusterExtent(FaaChromeCluster cluster)=>ClusterWidth(cluster,compact);
        private float ClusterWidth(FaaChromeCluster cluster,bool shortCaptions)
        {
            float total=0;int count=0;
            foreach(var e in entries){if(e.cluster!=cluster||!e.visible)continue;total+=Measure(e,shortCaptions);count++;}
            return total+Mathf.Max(0,count-1)*Gap;
        }
        private float Measure(Entry e,bool shortCaption)
        {
            if(e.id==StatusId)return statusWidth;
            string text=shortCaption?e.shortCaption:e.fullCaption;
            float caption=TextWidth(e.caption,text);
            float badge=e.badge!=null?Gap+TextWidth(e.badgeText,e.badge):0;
            float key=e.keycap!=null?TextWidth(e.keyText,e.keycap)+2*KeycapPadding:0;
            return Mathf.Max(MinButtonWidth,Mathf.Ceil(CaptionPadding+caption+badge+(e.keycap!=null?Gap+Mathf.Max(KeycapHeight,key):0)+CaptionPadding));
        }
        private void Place(FaaChromeCluster cluster)
        {
            float x=Inset;bool left=cluster==FaaChromeCluster.Left;
            var list=Sorted(cluster);
            if(!left)list.Reverse();
            foreach(var e in list)
            {
                if(!e.visible)continue;
                float w=Measure(e,compact);e.width=w;
                if(e.id!=StatusId)
                {
                    string text=compact?e.shortCaption:e.fullCaption;
                    if(e.shown!=text){e.shown=text;e.caption.text=text;}
                    float cw=TextWidth(e.caption,text)+2;
                    e.caption.rectTransform.sizeDelta=new Vector2(cw,0);
                    if(e.badge!=null&&e.badgeText!=null)
                    {
                        var bt=e.badgeText.rectTransform;bt.anchoredPosition=new Vector2(CaptionPadding+cw-2+Gap,0);
                        bt.sizeDelta=new Vector2(TextWidth(e.badgeText,e.badge)+2,0);
                    }
                    if(e.keycap!=null)
                    {
                        float kw=Mathf.Max(KeycapHeight,TextWidth(e.keyText,e.keycap)+2*KeycapPadding);
                        e.keyBox.sizeDelta=new Vector2(kw,KeycapHeight);e.keyBox.anchoredPosition=new Vector2(-CaptionPadding,0);
                    }
                }
                e.rect.sizeDelta=new Vector2(w,ButtonHeight);
                e.rect.anchoredPosition=new Vector2(left?x:-x,0);
                x+=w+Gap;
            }
        }
        private float CanvasWidth()
        {
            float w=root!=null?root.rect.width:0;
            return w>1?w:1920f;
        }
        private static float TextWidth(TMP_Text text,string value)
        {
            if(text==null||string.IsNullOrEmpty(value))return 0;
            if(text.font==null)return value.Length*text.fontSize*.62f;
            return text.GetPreferredValues(value,4000,ButtonHeight).x;
        }

        // ---------------------------------------------------------------- flyouts

        /// <summary>Registers a flyout and the action that closes it. Only one flyout is open at a time.</summary>
        public void RegisterFlyout(string id,Action close){if(!string.IsNullOrEmpty(id))flyoutClosers[id]=close;}
        public void UnregisterFlyout(string id){flyoutClosers.Remove(id);if(openFlyout==id)openFlyout=null;}
        /// <summary>Call when a flyout opens; closes whichever other flyout was open.</summary>
        public void NotifyFlyoutOpened(string id)
        {
            if(string.IsNullOrEmpty(id)||openFlyout==id)return;
            string previous=openFlyout;openFlyout=id;
            if(previous!=null&&flyoutClosers.TryGetValue(previous,out var close))close?.Invoke();
        }
        public void NotifyFlyoutClosed(string id){if(openFlyout==id)openFlyout=null;}
        /// <summary>Parents a flyout into the fixed slot above the bar (bottom-left anchored, reference units).</summary>
        public void AttachFlyout(RectTransform flyout)
        {
            if(flyout==null)return;Build();
            flyout.SetParent(flyouts,false);
            flyout.anchorMin=flyout.anchorMax=flyout.pivot=Vector2.zero;
            flyout.anchoredPosition=new Vector2(FlyoutLeft,FlyoutBottom);
        }
        /// <summary>Closes whichever flyout is open (Esc, tests). Returns true when one was open.</summary>
        public bool CloseOpenFlyout()
        {
            if(openFlyout==null)return false;
            string id=openFlyout;
            if(flyoutClosers.TryGetValue(id,out var close)&&close!=null)close();
            if(openFlyout==id)openFlyout=null;
            return true;
        }

        /// <summary>
        /// Width of a flyout in the left slot, in reference units: at most <paramref name="preferred"/> and
        /// <see cref="FlyoutMaxWidth"/>, and narrow enough to stay <see cref="FlyoutColumnGap"/> clear of every registered
        /// flight keep-out (IAS, TQ, Classic dials...) on the left half of the screen whose height overlaps the flyout
        /// band [<see cref="FlyoutBottom"/>, FlyoutBottom + <paramref name="height"/>]. Never below <see cref="MinFlyoutWidth"/>.
        /// </summary>
        public float FlyoutWidth(float preferred,float height)
        {
            float width=Mathf.Min(preferred>0?preferred:FlyoutMaxWidth,FlyoutMaxWidth);
            float scale=canvas!=null?Mathf.Max(.01f,canvas.scaleFactor):1f;
            keepOutScratch.Clear();FaaHudKeepOut.Collect(keepOutScratch,false); // Symbology regions only (no chrome, no attitude window)
            float limit=FlyoutAvailableWidth(keepOutScratch,scale,Screen.width,height);
            return Mathf.Max(MinFlyoutWidth,Mathf.Min(width,limit));
        }
        /// <summary>
        /// Pure helper of <see cref="FlyoutWidth"/>: <paramref name="flightRects"/> are flight keep-outs in screen pixels
        /// (bottom-left origin), <paramref name="scale"/> is reference units to pixels. Returns the widest flyout (reference
        /// units) that stays <see cref="FlyoutColumnGap"/> left of every rectangle on the left half of the screen whose
        /// height overlaps the flyout band, or float.MaxValue when nothing limits it.
        /// </summary>
        public static float FlyoutAvailableWidth(IReadOnlyList<Rect> flightRects,float scale,float screenWidth,float height)
        {
            float limit=float.MaxValue;
            if(flightRects==null)return limit;
            scale=Mathf.Max(.01f,scale);
            float bandBottom=FlyoutBottom*scale,bandTop=(FlyoutBottom+Mathf.Max(0,height))*scale,left=(FlyoutLeft+MinFlyoutWidth*.5f)*scale;
            for(int i=0;i<flightRects.Count;i++)
            {
                var rect=flightRects[i];
                if(rect.width<=0||rect.height<=0)continue;
                if(rect.yMax<bandBottom||rect.yMin>bandTop)continue;       // not beside the flyout
                if(rect.center.x>screenWidth*.5f||rect.xMin<left)continue; // right half, or something inside the slot itself
                limit=Mathf.Min(limit,rect.xMin/scale-FlyoutColumnGap-FlyoutLeft);
            }
            return limit;
        }

        /// <summary>
        /// The one dismissal control every flyout uses: a square chrome button "CLOSE [Esc]" anchored to the top-right of
        /// <paramref name="parent"/> (AC 25-11B consistency: same label, same place, same key on every surface).
        /// </summary>
        public static Button CreateCloseButton(RectTransform parent,UnityAction close,float top=8f,float right=8f)
        {
            if(parent==null)return null;
            var rt=NewRect("Close flyout",parent);rt.anchorMin=rt.anchorMax=rt.pivot=Vector2.one;rt.anchoredPosition=new Vector2(-right,-top);
            var plate=rt.gameObject.AddComponent<Image>();plate.color=FaaHudStyle.ChromeButton;plate.raycastTarget=true;
            var o=rt.gameObject.AddComponent<Outline>();o.effectColor=FaaHudStyle.ChromeOutline;o.effectDistance=new Vector2(1,1);
            var button=rt.gameObject.AddComponent<Button>();button.targetGraphic=plate;StyleButton(button);
            if(close!=null)button.onClick.AddListener(close);
            var caption=NewText("Caption",rt,FaaHudStyle.Chrome,FaaHudStyle.White);caption.text="CLOSE";caption.alignment=TextAlignmentOptions.MidlineLeft;
            float cw=TextWidth(caption,"CLOSE")+2;
            var ct=caption.rectTransform;ct.anchorMin=new Vector2(0,0);ct.anchorMax=new Vector2(0,1);ct.pivot=new Vector2(0,.5f);ct.anchoredPosition=new Vector2(CaptionPadding,0);ct.sizeDelta=new Vector2(cw,0);
            var key=NewRect("Keycap",rt);key.anchorMin=key.anchorMax=key.pivot=new Vector2(1,.5f);
            var ki=key.gameObject.AddComponent<Image>();ki.color=new Color(0,0,0,.28f);ki.raycastTarget=false;
            var ko=key.gameObject.AddComponent<Outline>();ko.effectColor=FaaHudStyle.ChromeOutline;ko.effectDistance=new Vector2(1,1);
            var kt=NewText("Key",key,FaaHudStyle.Chrome,FaaHudStyle.ChromeQuiet);kt.text="Esc";kt.alignment=TextAlignmentOptions.Center;
            var ktr=kt.rectTransform;ktr.anchorMin=Vector2.zero;ktr.anchorMax=Vector2.one;ktr.offsetMin=ktr.offsetMax=Vector2.zero;
            float kw=Mathf.Max(KeycapHeight-4,TextWidth(kt,"Esc")+2*KeycapPadding);
            key.sizeDelta=new Vector2(kw,KeycapHeight-4);key.anchoredPosition=new Vector2(-CaptionPadding+4,0);
            rt.sizeDelta=new Vector2(Mathf.Ceil(CaptionPadding+cw+Gap+kw+CaptionPadding-4),CloseButtonHeight);
            return button;
        }
        /// <summary>Shared hover/pressed tints: tints above 1 brighten the dark plate, so feedback shows on the next frame (&lt; 100 ms).</summary>
        internal static void StyleButton(Button button)
        {
            if(button==null)return;
            button.navigation=new Navigation{mode=Navigation.Mode.None};
            var colors=button.colors;
            colors.normalColor=Color.white;colors.selectedColor=Color.white;
            colors.highlightedColor=new Color(1.6f,1.6f,1.6f,1);colors.pressedColor=new Color(2.6f,2.6f,2.6f,1);
            colors.disabledColor=new Color(1,1,1,.45f);colors.colorMultiplier=1;colors.fadeDuration=.05f;button.colors=colors;
        }

        // ---------------------------------------------------------------- occupied areas

        /// <summary>
        /// Adds every visible chrome keep-out (bar, open flyouts, key list) in Unity screen pixels, bottom-left origin.
        /// Used by edge cue arrows; the conformal layer reads the same regions from <see cref="FaaHudKeepOut"/>.
        /// </summary>
        public static int CollectOccupiedScreenRects(List<Rect> into)
        {
            if(into==null)return 0;int added=0;
            var regions=FaaHudKeepOut.Regions;
            for(int i=0;i<regions.Count;i++)
            {
                var r=regions[i];
                if(r==null||(r is UnityEngine.Object o&&o==null)||r.Kind!=FaaKeepOutKind.Chrome)continue;
                if(r.TryGetScreenRect(out var rect)&&rect.width>0&&rect.height>0){into.Add(rect);added++;}
            }
            return added;
        }

        // ---------------------------------------------------------------- frame

        private void Update()
        {
            if(!Application.isPlaying)return;
            var kb=Keyboard.current;
            if(kb!=null)
            {
                bool shift=kb.leftShiftKey.isPressed||kb.rightShiftKey.isPressed,ctrl=kb.leftCtrlKey.isPressed||kb.rightCtrlKey.isPressed;
                bool question=kb.slashKey.wasPressedThisFrame&&shift;
                if(kb.dKey.wasPressedThisFrame&&ctrl&&shift)SetDeveloperMode(!developerMode);
                if(kb.f1Key.wasPressedThisFrame||question)ToggleHelp();
                else if(kb.escapeKey.wasPressedThisFrame)
                {
                    // The brief handles Esc itself (card first, then dock). Only close chrome-owned flyouts here.
                    if(HelpVisible)SetHelpVisible(false);
                    else if(CommandsVisible)SetCommandsVisible(false);
                    else if(openFlyout=="cues"&&flyoutClosers.TryGetValue("cues",out var close))close?.Invoke();
                }
            }
            var mouse=Mouse.current;
            if(mouse!=null&&mouse.leftButton.wasPressedThisFrame)CloseOnClickOutside(mouse.position.ReadValue());
        }

        /// <summary>
        /// A click anywhere outside the commands flyout or key list (and outside its own bar button) closes it. The click
        /// still reaches whatever is under it, so the next control works with one click.
        /// </summary>
        public void CloseOnClickOutside(Vector2 screenPoint)
        {
            if(CommandsVisible&&!Hit(commandsRoot,screenPoint)&&!Hit(ButtonRect(CommandsId),screenPoint))SetCommandsVisible(false);
            if(HelpVisible&&!Hit(helpRoot,screenPoint)&&!Hit(ButtonRect(HelpId),screenPoint))SetHelpVisible(false);
        }
        private bool Hit(RectTransform rt,Vector2 screenPoint)
        {
            if(rt==null||!rt.gameObject.activeInHierarchy||canvas==null)return false;
            var cam=canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:canvas.worldCamera;
            return RectTransformUtility.RectangleContainsScreenPoint(rt,screenPoint,cam);
        }

        private void LateUpdate()
        {
            if(!built)return;
            SyncRenderMode();
            TickStatus();
            TickCommands();
            ApplyFlyoutDeclutter(FaaRotorcraftConformalLayer.UnusualAttitudeActive);
            float w=CanvasWidth();
            if(layoutDirty||!Mathf.Approximately(w,lastCanvasWidth))Relayout(w);
        }

        /// <summary>
        /// Unusual attitude: open flyouts (commands, cue controls, key list) disappear with the rest of the secondary
        /// display and come back unchanged on recovery. The bar stays (it is outside the attitude field and holds FORWARD).
        /// </summary>
        public void ApplyFlyoutDeclutter(bool declutter)
        {
            if(flyoutGroup==null||flyoutsDecluttered==declutter)return;
            flyoutsDecluttered=declutter;
            flyoutGroup.alpha=declutter?0:1;flyoutGroup.blocksRaycasts=flyoutGroup.interactable=!declutter;
        }

        /// <summary>Overlay canvases are not drawn into XR eye buffers, so native XR uses camera space at the HUD overlay depth.</summary>
        private void SyncRenderMode()
        {
            var cam=Camera.main;
            bool xr=cam!=null&&(cam.stereoEnabled||XRSettings.isDeviceActive);
            if(xr==lastXr&&(!xr||canvas.worldCamera==cam))return;
            lastXr=xr;
            canvas.renderMode=xr?RenderMode.ScreenSpaceCamera:RenderMode.ScreenSpaceOverlay;
            canvas.worldCamera=xr?cam:null;
            if(xr)canvas.planeDistance=Mathf.Max(cam.nearClipPlane+.05f,XrPlaneDistance);
        }

        // ---------------------------------------------------------------- helpers

        private static RectTransform NewRect(string name,Transform parent)
        {
            var go=new GameObject(name,typeof(RectTransform));go.layer=parent!=null?parent.gameObject.layer:go.layer;
            var rt=(RectTransform)go.transform;rt.SetParent(parent,false);return rt;
        }
        internal static TMP_Text NewText(string name,Transform parent,float size,Color color)
        {
            var rt=NewRect(name,parent);var t=rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.font=TMP_Settings.defaultFontAsset;t.richText=false;t.raycastTarget=false;
            t.textWrappingMode=TextWrappingModes.NoWrap;t.overflowMode=TextOverflowModes.Overflow;
            FaaHudStyle.StyleText(t,size,FaaHudStyle.Chrome,color,FaaHudStyle.MinTextAlpha,false);
            return t;
        }
    }
}
