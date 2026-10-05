using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaWorkspaceUi;
namespace FAA.Customization
{
    /// <summary>
    /// Cockpit Settings side panel. Four tabs that show which page is open (INSTRUMENTS, PANELS, DISPLAY, DATA); each page edits
    /// one kind of object; every control names its action and shows its state; one layout-lock line. GameObject names are kept for
    /// the verification tools. Captions are rebuilt only when the value they show changes.
    /// </summary>
    public sealed partial class FaaSpatialWorkspace
    {
        public bool MenuOpen { get; private set; }
        public Canvas ControlsCanvas=>controlsCanvas;
        private Canvas controlsCanvas;
        private RectTransform menuRoot,selectionFrame,resizeGrip,hudPage,panelsPage;
        private FaaWorkspaceDragHandle resizeHandle;
        private TMP_Text selectedCaption,detailCaption,inputCaption,modeCaption,pairHint,scaleMinLabel,lockHint,sizeNote;
        private Slider scaleSlider;
        private Button editLockButton,resetSelectedButton,resetLayoutButton;
        private readonly Button[] tabButtons=new Button[4];
        private readonly GameObject[] tabUnderlines=new GameObject[4];
        private readonly Button[] presetButtons=new Button[3];
        private readonly Button[] panelSelectButtons=new Button[4];
        private readonly List<string> selectionIds=new();
        private readonly List<Button> editButtons=new();
        private bool refreshingUi;
        public const int PageInstruments=0,PagePanels=1,PageDisplay=2,PageData=3;
        /// <summary>Open Settings page (one of the Page constants).</summary>
        public int SettingsPage { get; private set; }
        /// <summary>Instrument size presets S / M / L; all at or above the 75% legibility floor of text-bearing modules.</summary>
        public static readonly float[] SizePresets={.75f,.9f,1f};
        private static readonly string[] PresetCaptions={"S   75%","M   90%","L   100%"};
        private static readonly string[] PresetNames={"Size 60","Size 80","Size 100"};
        private static readonly string[] PanelIds={"weather","traffic","settings","camera-controls"};
        private static readonly string[] PanelLabels={"WEATHER","TRAFFIC","SETTINGS","HAND STUDIO"};
        private string lastModuleId="airspeed",lastPanelId="settings";
        private string captionId,captionName;private int captionPercent=int.MinValue;
        private string detailId;private int detailYaw,detailElevation,detailDistance,detailScale;
        private string inputProfile,inputSource;
        private int lastEditMode=-1;

        public void ToggleMenu(){if(MenuOpen)CloseSettingsMenu();else OpenMenu();}
        public void OpenMenu(){MenuOpen=true;RefreshControls();}
        /// <summary>
        /// F9 / SETTINGS: opens the settings panel and turns the desktop view to it, so the press always has a visible
        /// result; pressing again while it is in view closes it and returns forward. Native XR opens or closes the panel
        /// in place (the pilot turns their head). If the camera cannot turn (chase/free camera) the key simply toggles closed.
        /// </summary>
        public void ToggleSettingsInspection()
        {
            if(MenuOpen&&(NativeXr||InspectedPanelId=="settings")){CloseSettingsMenu();return;}
            bool wasOpen=MenuOpen;
            InspectUtility("settings");
            if(wasOpen&&!NativeXr&&InspectedPanelId!="settings")CloseSettingsMenu();
        }
        private void CloseSettingsMenu()
        {
            MenuOpen=false;DisarmDataGuards();
            CloseInspected("settings");
            RefreshControls();
        }
        private void BuildControls()
        {
            controlsCanvas=FaaWorkspaceUi.Canvas("FAA Spatial Layout Controls",transform,7250);
            var scaler=controlsCanvas.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
            // Screen-chrome unit: the view controls live in the single FaaPilotChrome bar (zone Z6) whenever it exists.
            // The legacy recovery dock is kept only for hosts without the chrome (it is never built in Play Mode).
            var chrome=FaaPilotChrome.Ensure();
            if(chrome!=null)BuildChromeDock(chrome);
            else
            {
                var dock=Rect("Workspace Recovery Dock",controlsCanvas.transform,0,0,648,38);
                dock.anchorMin=dock.anchorMax=dock.pivot=new Vector2(1,0);dock.anchoredPosition=new Vector2(-16,16);
                Box("Recovery background",dock,324,19,648,38,Background);
                Button("Menu",dock,"SETTINGS / F9",70,19,132,32,ToggleMenu);
                Button("Inspect Left",dock,"LOOK LEFT",188,19,94,32,()=>InspectUtility("camera-controls"));
                Button("Inspect Right",dock,"LOOK RIGHT",288,19,94,32,()=>InspectUtility("settings"));
                Button("Forward View",dock,"FORWARD / R",402,19,120,32,ReturnToForwardView);
                Button("Inspect Weather",dock,"WEATHER",508,19,82,32,()=>InspectPanel("weather"));
                Button("Inspect Traffic",dock,"TRAFFIC",600,19,88,32,()=>InspectPanel("traffic"));
            }
            SettingsCanvas=FaaWorkspaceUi.Canvas("FAA Cockpit Settings Panel",transform,7220);
            menuRoot=Rect("Cockpit Settings",SettingsCanvas.transform,960,540,640,620);
            Box("Settings Surface",menuRoot,320,310,640,620,Background,true);
            Box("Accent Rail",menuRoot,4,310,4,620,Accent);
            Text("Workspace Title",menuRoot,"COCKPIT SETTINGS",230,52,418,44,28,true);
            // CLOSE closes the panel and, when it is in view, returns the view forward.
            Button("Stow Settings",menuRoot,"CLOSE",560,52,120,MinTargetHeight,CloseSettingsMenu);
            string[] tabNames={"HUD Tab","Panels Tab","Symbology Tab","Data Sources Tab"};
            string[] tabCaptions={"INSTRUMENTS","PANELS","DISPLAY","DATA"};
            for(int i=0;i<4;i++)
            {
                int page=i;tabButtons[i]=Button(tabNames[i],menuRoot,tabCaptions[i],86+154*i,108,146,MinTargetHeight,()=>SetPage(page));
                tabUnderlines[i]=Box("Tab Underline",tabButtons[i].transform,73,MinTargetHeight-1.5f,146,3,Accent).gameObject;
            }
            // ---- INSTRUMENTS: size of the selected head-fixed instrument (paired IAS/ALT and TQ/NR in Digital). ----
            hudPage=Rect("Instrument Settings Page",menuRoot,320,352,640,354);
            Glyph(Button("Previous Module",hudPage,"<",40,30,56,48,()=>CycleSelection(-1)));
            selectedCaption=Text("Selected Module",hudPage,"AIRSPEED",320,30,440,44,26);
            selectedCaption.enableAutoSizing=true;selectedCaption.fontSizeMin=18;selectedCaption.fontSizeMax=26;selectedCaption.textWrappingMode=TextWrappingModes.NoWrap;
            Glyph(Button("Next Module",hudPage,">",600,30,56,48,()=>CycleSelection(1)));
            pairHint=Text("Size Pairing Hint",hudPage,"",320,72,592,24,16);pairHint.color=Muted;
            Glyph(Button("Smaller",hudPage,"-",40,124,56,48,()=>AdjustSelectedScale(-.05f)));
            Glyph(Button("Larger",hudPage,"+",600,124,56,48,()=>AdjustSelectedScale(.05f)));
            // Transparent 46-unit hit area around an 8-unit bar; 26x40 thumb.
            var track=Box("Module Size Slider",hudPage,320,124,464,MinTargetHeight,new Color(0,0,0,0),true).rectTransform;
            Box("Size Bar",track,232,MinTargetHeight*.5f,464,8,Card);
            scaleSlider=track.gameObject.AddComponent<Slider>();scaleSlider.minValue=FaaSpatialLayoutMath.MinScale;scaleSlider.maxValue=FaaSpatialLayoutMath.MaxScale;
            var thumb=Box("Size Thumb",track,0,0,26,-6,Accent).rectTransform;scaleSlider.handleRect=thumb;scaleSlider.targetGraphic=thumb.GetComponent<Image>();
            scaleSlider.onValueChanged.AddListener(v=>{if(!refreshingUi)SetScale(SelectedId,v);});
            scaleMinLabel=Text("Scale Min",hudPage,"40%",148,160,120,24,16,true);scaleMinLabel.color=Muted;
            var scaleMax=Text("Scale Max",hudPage,(Mathf.RoundToInt(FaaSpatialLayoutMath.MaxScale*100))+"%",492,160,120,24,16);
            scaleMax.alignment=TextAlignmentOptions.MidlineRight;scaleMax.color=Muted;
            for(int i=0;i<3;i++){float v=SizePresets[i];presetButtons[i]=Button(PresetNames[i],hudPage,PresetCaptions[i],116+204*i,210,188,MinTargetHeight,()=>SetScale(SelectedId,v));}
            Button("All HUD Smaller",hudPage,"ALL INSTRUMENTS  SMALLER",166,264,292,MinTargetHeight,()=>ScaleAllModules(-.05f));
            Button("All HUD Larger",hudPage,"ALL INSTRUMENTS  LARGER",474,264,292,MinTargetHeight,()=>ScaleAllModules(.05f));
            resetSelectedButton=Button("Reset Selected",hudPage,"RESET SIZE",320,318,596,MinTargetHeight,ResetSelected);
            // ---- PANELS: where the side panels live and how big they are. ----
            panelsPage=Rect("Spatial Panel Settings Page",menuRoot,320,352,640,354);
            for(int i=0;i<4;i++){string id=PanelIds[i];panelSelectButtons[i]=Button("Select "+id,panelsPage,PanelLabels[i],88+154*i,26,144,MinTargetHeight,()=>Select(id));}
            detailCaption=Text("Layout Details",panelsPage,"",320,64,592,24,16);detailCaption.color=Muted;
            Button("Settings Panel Smaller",panelsPage,"PANEL SMALLER",166,104,292,MinTargetHeight,()=>{if(GetPanel(SelectedId)!=null)AdjustSelectedScale(-.1f);});
            Button("Settings Panel Larger",panelsPage,"PANEL LARGER",474,104,292,MinTargetHeight,()=>{if(GetPanel(SelectedId)!=null)AdjustSelectedScale(.1f);});
            editButtons.Add(Button("Nearer",panelsPage,"NEARER",166,154,292,MinTargetHeight,()=>{if(EditMode)AdjustDistance(-.1f);}));
            editButtons.Add(Button("Farther",panelsPage,"FARTHER",474,154,292,MinTargetHeight,()=>{if(EditMode)AdjustDistance(.1f);}));
            float[] headings={-75,0,75,180};string[] placements={"LEFT","DOWN","RIGHT","BEHIND"};
            for(int i=0;i<4;i++){int n=i;editButtons.Add(Button("Place "+placements[i],panelsPage,placements[i],88+154*i,204,144,MinTargetHeight,()=>{if(EditMode)PlaceSelected(headings[n],n==1?-55:5);}));}
            lockHint=Text("Spatial Instruction",panelsPage,"",320,240,592,24,16);lockHint.color=Muted;
            Button("Seat Recenter",panelsPage,"RECENTER SEAT",166,280,292,MinTargetHeight,RecenterSeat);
            Button("3D Panels",panelsPage,"RESET RADAR POSITIONS (F10)",474,280,292,MinTargetHeight,RecallPanels);
            Button("Laptop Camera",panelsPage,"OPEN HAND STUDIO",166,330,292,MinTargetHeight,()=>InspectUtility("camera-controls"));
            resetLayoutButton=Button("Reset Layout",panelsPage,"RESET ALL POSITIONS + SIZES",474,330,292,MinTargetHeight,()=>{if(EditMode)ResetAll();});
            // ---- One layout-lock line (INSTRUMENTS and PANELS only). ----
            modeCaption=Text("Layout Mode",menuRoot,"LAYOUT LOCKED",200,555,354,28,18,true);
            editLockButton=Button("Edit Lock",menuRoot,"UNLOCK LAYOUT",535,555,166,MinTargetHeight,()=>SetEditMode(!EditMode));
            // Two lines fit (profile status + input source can be long at the 16-unit floor).
            inputCaption=Text("Input Source",menuRoot,"",320,599,600,38,16);inputCaption.color=Muted;
            // Size persistence belongs with the size controls (INSTRUMENTS), in the slot the input line uses on PANELS.
            sizeNote=Text("Style Persistence Note",menuRoot,"Sizes are saved per display style.",320,599,600,24,16);sizeNote.color=Muted;
            RegisterUtility("settings",SettingsCanvas,menuRoot,90,.58f);
            BuildSymbologyPage();
            BuildDataSourcePage();
            RefreshInstrumentPicker();
            SetPage(PageInstruments);
            selectionFrame=Rect("Selected HUD Bounds",controlsCanvas.transform,0,0,0,0);selectionFrame.anchorMin=selectionFrame.anchorMax=new Vector2(.5f,.5f);
            var frame=selectionFrame.gameObject.AddComponent<Image>();frame.color=new Color(.2f,.8f,.72f,.03f);frame.raycastTarget=false;
            var outline=selectionFrame.gameObject.AddComponent<Outline>();outline.effectColor=Accent;
            resizeGrip=Rect("Resize Selected HUD",selectionFrame,0,0,28,28);resizeGrip.anchorMin=resizeGrip.anchorMax=new Vector2(1,0);
            resizeGrip.gameObject.AddComponent<Image>().color=Accent;resizeHandle=resizeGrip.gameObject.AddComponent<FaaWorkspaceDragHandle>();
        }
        public void SetSettingsPage(bool spatial)=>SetPage(spatial?PagePanels:PageInstruments);
        /// <summary>Shows one Settings page and marks its tab (fill plus underline).</summary>
        public void SetPage(int page)
        {
            if(menuRoot==null)return;
            SettingsPage=Mathf.Clamp(page,PageInstruments,PageData);
            SetActive(hudPage,SettingsPage==PageInstruments);SetActive(panelsPage,SettingsPage==PagePanels);
            SetActive(symbologyPage,SettingsPage==PageDisplay);SetActive(dataSourcePage,SettingsPage==PageData);
            if(SettingsPage!=PageData)DisarmDataGuards();
            for(int i=0;i<tabButtons.Length;i++)
            {
                SetSelected(tabButtons[i],i==SettingsPage);
                if(tabUnderlines[i]!=null&&tabUnderlines[i].activeSelf!=(i==SettingsPage))tabUnderlines[i].SetActive(i==SettingsPage);
            }
            // Each page edits its own kind of object: instruments on INSTRUMENTS, side panels on PANELS.
            bool panelSelected=GetPanel(SelectedId)!=null;
            captionId=null;detailId=null;
            if(SettingsPage==PageInstruments&&panelSelected)Select(GetEntry(lastModuleId)!=null?lastModuleId:selectionIds.Count>0?selectionIds[0]:"airspeed");
            else if(SettingsPage==PagePanels&&!panelSelected)Select(GetPanel(lastPanelId)!=null?lastPanelId:"settings");
            else RefreshControls();
        }
        private bool PairedSizes=>CurrentSymbology==FaaSymbologyVersion.Digital;
        private static string PairPartner(string id)=>id=="altitude"?"airspeed":id=="nr"?"torque":id;
        private void CycleSelection(int delta)
        {
            if(selectionIds.Count==0)return;
            int index=selectionIds.IndexOf(SelectedId);
            if(index<0&&PairedSizes)index=selectionIds.IndexOf(PairPartner(SelectedId));
            if(index<0)index=delta>0?-1:0;
            Select(selectionIds[(index+delta+selectionIds.Count)%selectionIds.Count]);
        }
        private void UpdateControlCanvasPose()
        {
            SyncInspectionState();
            if(controlsCanvas==null||View==null)return;
            RenderMode mode=NativeXr?RenderMode.ScreenSpaceCamera:RenderMode.ScreenSpaceOverlay;
            if(controlsCanvas.renderMode!=mode)controlsCanvas.renderMode=mode;
            Camera camera=NativeXr?View:null;if(controlsCanvas.worldCamera!=camera)controlsCanvas.worldCamera=camera;
            if(NativeXr)controlsCanvas.planeDistance=Mathf.Max(.75f,View.nearClipPlane+.15f);
        }
        private void RefreshControls()
        {
            RefreshChromeDock();
            RefreshDataSourceControls();
            RefreshSymbologyControls();
            if(menuRoot==null)return;
            if(menuRoot.gameObject.activeSelf!=MenuOpen)menuRoot.gameObject.SetActive(MenuOpen);
            if(!MenuOpen){DisarmDataGuards();return;} // Smoothness: no caption work at 10 Hz while the panel is closed.
            refreshingUi=true;
            bool layoutPage=SettingsPage==PageInstruments||SettingsPage==PagePanels;
            SetActive(modeCaption,layoutPage);SetActive(editLockButton,layoutPage);SetActive(inputCaption,SettingsPage==PagePanels);
            SetActive(sizeNote,SettingsPage==PageInstruments);
            int edit=EditMode?1:0;
            if(lastEditMode!=edit)
            {
                lastEditMode=edit;
                // One state line: what the lock does is shown, not a list of what still works.
                modeCaption.text=EditMode?"LAYOUT UNLOCKED  |  DRAG + GESTURES ON":"LAYOUT LOCKED";modeCaption.color=EditMode?Caution:Muted;
                SetCaption(editLockButton,EditMode?"LOCK LAYOUT":"UNLOCK LAYOUT");
                lockHint.text=EditMode?"Right-drag a panel, or pinch its top handle, to move it.":"Right-drag moves a panel. Unlock for placement and reset.";
            }
            if(SettingsPage==PageInstruments)RefreshInstrumentControls();
            else if(SettingsPage==PagePanels)RefreshPanelControls();
            refreshingUi=false;
        }
        private void RefreshInstrumentControls()
        {
            FaaNonConformalScaleTarget module=null;foreach(var m in modules)if(m.Id==SelectedId){module=m;break;}
            var entry=GetEntry(SelectedId);
            float shown=module!=null?module.EffectiveScale:entry!=null?entry.scale:1f;
            if(captionId!=SelectedId)
            {
                captionId=SelectedId;captionPercent=int.MinValue;captionName=SizeCaption(SelectedId,module);
                float minimum=module!=null?module.MinimumLegibleScale:FaaSpatialLayoutMath.MinScale;
                int minimumPercent=Mathf.RoundToInt(minimum*100);
                bool pairedReadouts=PairedSizes&&(SelectedId=="airspeed"||SelectedId=="altitude"),pairedEngines=PairedSizes&&(SelectedId=="torque"||SelectedId=="nr");
                pairHint.text=pairedReadouts?"IAS and ALT always share one size. Minimum "+minimumPercent+"%.":
                    pairedEngines?"TQ and NR always share one size. Minimum "+minimumPercent+"%.":
                    minimum>FaaSpatialLayoutMath.MinScale+.001f?"Minimum "+minimumPercent+"% keeps the flight text legible.":"";
                scaleSlider.minValue=minimum;scaleMinLabel.text=minimumPercent+"%";
                SetCaption(resetSelectedButton,"RESET "+captionName+" TO "+Mathf.RoundToInt((module!=null?module.DefaultScale:1f)*100)+"%");
            }
            int percent=Mathf.RoundToInt(shown*100);
            if(percent!=captionPercent){captionPercent=percent;selectedCaption.text=captionName+"   "+percent+"%";}
            if(entry!=null&&!Mathf.Approximately(scaleSlider.value,shown))scaleSlider.SetValueWithoutNotify(shown);
            if(scaleSlider.interactable!=(entry!=null))scaleSlider.interactable=entry!=null;
            for(int i=0;i<presetButtons.Length;i++)SetSelected(presetButtons[i],entry!=null&&Mathf.Abs(shown-SizePresets[i])<.005f);
        }
        private string SizeCaption(string id,FaaNonConformalScaleTarget module)
        {
            if(PairedSizes)
            {
                if(id=="airspeed"||id=="altitude")return "AIRSPEED + ALTITUDE";
                if(id=="torque"||id=="nr")return "TORQUE + ROTOR RPM";
            }
            return (module!=null?module.Caption:id??"").ToUpperInvariant();
        }
        private void RefreshPanelControls()
        {
            var p=GetPanel(SelectedId);
            for(int i=0;i<panelSelectButtons.Length;i++)
            {
                var b=panelSelectButtons[i];if(b==null)continue;
                SetSelected(b,PanelIds[i]==SelectedId);bool exists=GetPanel(PanelIds[i])!=null;if(b.interactable!=exists)b.interactable=exists;
            }
            foreach(var button in editButtons){bool on=EditMode&&p!=null;if(button.interactable!=on)button.interactable=on;}
            if(resetLayoutButton!=null&&resetLayoutButton.interactable!=EditMode)resetLayoutButton.interactable=EditMode;
            if(p==null){if(detailId!=""){detailId="";detailCaption.text="Select a panel above.";}}
            else
            {
                int yaw=Mathf.RoundToInt(Mathf.DeltaAngle(0,p.Layout.yaw)),elevation=Mathf.RoundToInt(p.Layout.elevation);
                int distance=Mathf.RoundToInt(p.Layout.distance*10),scale=Mathf.RoundToInt(p.Layout.scale*100);
                if(detailId!=p.Id||yaw!=detailYaw||elevation!=detailElevation||distance!=detailDistance||scale!=detailScale)
                {
                    detailId=p.Id;detailYaw=yaw;detailElevation=elevation;detailDistance=distance;detailScale=scale;
                    int index=System.Array.IndexOf(PanelIds,p.Id);string label=index>=0?PanelLabels[index]:p.Id.ToUpperInvariant();
                    detailCaption.text=label+"   "+Mathf.Abs(yaw)+"\u00B0 "+(yaw<0?"LEFT":"RIGHT")+"   "+Mathf.Abs(elevation)+"\u00B0 "+(elevation<0?"DOWN":"UP")+
                        "   "+(distance/10f).ToString("0.0",System.Globalization.CultureInfo.InvariantCulture)+" m   SIZE "+scale+"%";
                }
            }
            if(!ReferenceEquals(inputProfile,ProfileStatus)||!ReferenceEquals(inputSource,InputStatus))
            {inputProfile=ProfileStatus;inputSource=InputStatus;inputCaption.text=ProfileStatus+"  |  "+InputStatus;}
        }
        private void UpdateSelectionFrame()
        {
            if(selectionFrame==null||controlsCanvas==null)return;bool visible=false;
            // Shown while the layout is unlocked, and while an instrument is being resized from Settings with the HUD dimmed.
            bool preview=InspectionPreviewTarget!=null;
            if(EditMode||preview)foreach(var m in modules)if(m.Id==SelectedId&&m.TryScreenBounds(View,out var b))
            {
                var root=(RectTransform)controlsCanvas.transform;Camera ui=NativeXr?View:null;
                if(RectTransformUtility.ScreenPointToLocalPointInRectangle(root,b.min,ui,out var a)&&RectTransformUtility.ScreenPointToLocalPointInRectangle(root,b.max,ui,out var z))
                {
                    selectionFrame.anchoredPosition=(a+z)*.5f;selectionFrame.sizeDelta=z-a;
                    if(resizeHandle!=null)resizeHandle.Configure(this,m.Id,true);
                    if(resizeGrip.gameObject.activeSelf!=EditMode)resizeGrip.gameObject.SetActive(EditMode);
                    visible=true;
                }
            }
            if(selectionFrame.gameObject.activeSelf!=visible)selectionFrame.gameObject.SetActive(visible);
        }
        private void DestroyControls()
        {
            DestroyChromeDock();
            // Never leave the desktop view zoomed when the workspace unbinds mid-inspection (eased back to the exact base FOV).
            if(InspectedPanelId!=null&&desktopView!=null)desktopView.ReleaseFieldOfView();
            InspectedPanelId=null;if(FaaHudInspection.Active)FaaHudInspection.End();
            if(SettingsCanvas!=null)Destroy(SettingsCanvas.gameObject);SettingsCanvas=null;
            if(controlsCanvas!=null)Destroy(controlsCanvas.gameObject);controlsCanvas=null;editButtons.Clear();selectionIds.Clear();
            dataGuards.Clear();captionId=null;detailId=null;lastEditMode=-1;inputProfile=inputSource=null;resizeHandle=null;
        }

        // ---- Screen-chrome unit (wave 1): view-control cluster of the pilot chrome bar. ----
        // Buttons follow panel yaw (left to right), name their destination, show their key, and highlight the
        // destination currently in view. Native XR hides camera-turn destinations and offers RECENTER instead.
        public const string ChromeHands="view.hands", ChromeWeather="view.weather", ChromeForward="view.forward",
            ChromeTraffic="view.traffic", ChromeSettings="settings", ChromeRecenter="view.recenter";
        private static readonly string[] ChromeDockIds={ChromeHands,ChromeWeather,ChromeForward,ChromeTraffic,ChromeSettings,ChromeRecenter};
        // Captions are precomputed per side so refreshing at 10 Hz never builds strings.
        private static readonly string[] HandsCaption={"‹ HAND STUDIO","HAND STUDIO ›"}, HandsShort={"‹ HANDS","HANDS ›"};
        private static readonly string[] WeatherCaption={"‹ WEATHER","WEATHER ›"}, WeatherShort={"‹ WX","WX ›"};
        private static readonly string[] TrafficCaption={"‹ TRAFFIC","TRAFFIC ›"}, TrafficShort={"‹ TFC","TFC ›"};
        private static readonly string[] SettingsCaption={"‹ SETTINGS","SETTINGS ›"}, SettingsShort={"‹ SET","SET ›"};
        private static readonly string[] SettingsXrCaption={"‹ LOOK LEFT · SETTINGS","SETTINGS · LOOK RIGHT ›"};

        /// <summary>Whether a dock destination is offered in the current mode (camera-turn destinations are desktop only).</summary>
        public static bool ChromeViewButtonVisible(string id,bool nativeXr)=>
            id==ChromeSettings||(id==ChromeRecenter?nativeXr:!nativeXr);
        /// <summary>Default left-to-right order of the view cluster for the given panel yaws (forward is yaw 0).</summary>
        public static string[] ChromeViewOrder(float handsYaw,float weatherYaw,float trafficYaw,float settingsYaw)
        {
            var ids=new[]{ChromeHands,ChromeWeather,ChromeForward,ChromeTraffic,ChromeSettings};
            var yaws=new[]{handsYaw,weatherYaw,0f,trafficYaw,settingsYaw};
            System.Array.Sort(yaws,ids);return ids;
        }
        private float ChromeYaw(string panelId,float fallback)
        {
            var p=GetPanel(panelId);float yaw=p!=null?Mathf.DeltaAngle(0,p.Layout.yaw):fallback;
            return Mathf.Abs(yaw)<1f?Mathf.Sign(fallback)*1f:yaw; // never tie with FORWARD
        }
        private void BuildChromeDock(FaaPilotChrome chrome)
        {
            chrome.AddButton(ChromeHands,FaaChromeCluster.Right,-90,HandsCaption[0],null,()=>InspectUtility("camera-controls"),HandsShort[0]);
            chrome.AddButton(ChromeWeather,FaaChromeCluster.Right,-55,WeatherCaption[0],null,()=>InspectPanel("weather"),WeatherShort[0]);
            chrome.AddButton(ChromeForward,FaaChromeCluster.Right,0,"FORWARD","R",ReturnToForwardView,"FWD");
            chrome.AddButton(ChromeRecenter,FaaChromeCluster.Right,.5f,"RECENTER",null,RecenterSeat);
            chrome.AddButton(ChromeTraffic,FaaChromeCluster.Right,55,TrafficCaption[1],null,()=>InspectPanel("traffic"),TrafficShort[1]);
            chrome.AddButton(ChromeSettings,FaaChromeCluster.Right,90,SettingsCaption[1],"F9",ToggleSettingsInspection,SettingsShort[1]);
            chrome.SetHelpEntry("R","Forward view (return from a side panel)",10);
            chrome.SetHelpEntry("F9","Settings panel: open and turn to it / close",20);
            chrome.SetHelpEntry("F10","Reset both radar panel positions",30);
            RefreshChromeDock();
        }
        private void RefreshChromeDock()
        {
            var chrome=FaaPilotChrome.Current;if(chrome==null||!chrome.HasButton(ChromeForward))return;
            bool xr=NativeXr;string active=InspectedPanelId;
            float hands=ChromeYaw("camera-controls",-90),weather=ChromeYaw("weather",-55),traffic=ChromeYaw("traffic",55),settings=ChromeYaw("settings",90);
            chrome.SetButtonOrder(ChromeHands,hands);chrome.SetButtonOrder(ChromeWeather,weather);
            chrome.SetButtonOrder(ChromeTraffic,traffic);chrome.SetButtonOrder(ChromeSettings,settings);
            int hs=hands<0?0:1,ws=weather<0?0:1,ts=traffic<0?0:1,ss=settings<0?0:1;
            chrome.SetButtonState(ChromeHands,!xr&&GetPanel("camera-controls")!=null,active=="camera-controls",HandsCaption[hs],HandsShort[hs]);
            chrome.SetButtonState(ChromeWeather,!xr&&GetPanel("weather")!=null,active=="weather",WeatherCaption[ws],WeatherShort[ws]);
            chrome.SetButtonState(ChromeForward,!xr,!xr&&active==null);
            chrome.SetButtonState(ChromeTraffic,!xr&&GetPanel("traffic")!=null,active=="traffic",TrafficCaption[ts],TrafficShort[ts]);
            chrome.SetButtonState(ChromeRecenter,xr,false);
            chrome.SetButtonState(ChromeSettings,true,MenuOpen&&(xr||active=="settings"),xr&&MenuOpen?SettingsXrCaption[ss]:SettingsCaption[ss],SettingsShort[ss]);
        }
        private void DestroyChromeDock()
        {
            var chrome=FaaPilotChrome.Current;if(chrome==null)return;
            foreach(var id in ChromeDockIds)chrome.RemoveButton(id);
            chrome.RemoveHelpEntry("R");chrome.RemoveHelpEntry("F9");chrome.RemoveHelpEntry("F10");
        }
    }
}
