using System.Collections.Generic;
using FAA.HUDToolkit;
using UnityEngine;

namespace FAA.Customization
{
    public sealed partial class FaaSpatialWorkspace
    {
        public FaaSymbologyVersion CurrentSymbology {get;private set;}=FaaSymbologyVersion.Digital;
        public FaaClassicAnalogHud ClassicHud {get;private set;}
        public bool ClassicInstrumentAttitude {get;private set;}=true;
        public bool ReducedSymbologyMotion {get;private set;}
        public bool UsePilotSymbologyColor {get;private set;}
        public bool UsesClassicAttitude => isActiveAndEnabled && CurrentSymbology==FaaSymbologyVersion.ClassicAnalog && ClassicInstrumentAttitude && ClassicHud!=null && ClassicHud.Visible;
        private readonly List<FaaNonConformalScaleTarget> digitalModules=new(),classicModules=new();
        private readonly List<StyleGate> digitalGates=new();
        private readonly Dictionary<string,Transform> classicSourceRoots=new();
        private readonly Dictionary<string,HUDControl.Core.HUDElementBase> classicSourceElements=new();
        private SymbologyColorManager colorManager;
        private string symbologyKey;
        private Transform digitalRoot;private int gatedChildCount=-1;
        // Classic dims itself per element (FaaClassicAnalogHud: awareness readouts at max(ForwardIntensityFor, AwarenessHudIntensity),
        // the rest at ForwardIntensityFor), so the workspace adds no Classic CanvasGroups (no double dimming). It only drives the
        // native hooks: PreviewInstrumentId (the instrument being resized from Settings) and DeclutterDeviation (unusual attitude).

        /// <summary>
        /// Digital HUD children that already multiply their own alpha by FaaHudInspection.ForwardHudIntensity (IAS/ALT with an
        /// awareness floor, engine/VSI columns, bank scale, deviation scales, FMA). Their style gate never dims them again.
        /// </summary>
        private static readonly HashSet<string> SelfDimmingGates=new(System.StringComparer.Ordinal)
            {"Airspeed Indicator","Altimeter","Torque Panel","NR/ENG Ind","VSI","Bank Scale","Glidescope","Localizer Position Ind.",FaaFlightModeAnnunciator.ObjectName};
        /// <summary>
        /// Digital HUD children that are essential awareness readouts but do NOT dim themselves (the legacy compass readout):
        /// their gate keeps max(forward intensity, FaaHudInspection.AwarenessHudIntensity) while a panel is inspected.
        /// </summary>
        private static readonly HashSet<string> AwarenessGates=new(System.StringComparer.Ordinal){"Heading Panel"};
        public static bool GateIsAwareness(string childName)=>childName!=null&&AwarenessGates.Contains(childName);
        /// <summary>Secondary navigation cues removed together in an unusual attitude (AC 25-11B 5.10.3.2; rule 2.7).</summary>
        private static readonly HashSet<string> UnusualAttitudeDeclutterGates=new(System.StringComparer.Ordinal){"Glidescope","Localizer Position Ind."};
        public static bool GateDimsItself(string childName)=>childName!=null&&SelfDimmingGates.Contains(childName);
        public static bool GateDeclutteredInUnusualAttitude(string childName)=>childName!=null&&UnusualAttitudeDeclutterGates.Contains(childName);

        /// <summary>Forward-HUD factor for a Digital style gate that is not an awareness readout (pure, for tests).</summary>
        public static float DigitalGateFactor(bool dimsItself,bool previewed,bool unusualDeclutter,bool unusualAttitude,float forwardIntensity)=>
            GateFactor(dimsItself,false,previewed,unusualDeclutter,unusualAttitude,forwardIntensity,forwardIntensity);
        /// <summary>
        /// Forward-HUD factor for a Digital style gate (pure, for tests). Layers that dim themselves are never dimmed again (1);
        /// the previewed instrument stays at 1; awareness readouts keep max(forward, awareness); everything else uses forward.
        /// </summary>
        public static float GateFactor(bool dimsItself,bool awareness,bool previewed,bool unusualDeclutter,bool unusualAttitude,float forwardIntensity,float awarenessIntensity)
        {
            if(unusualAttitude&&unusualDeclutter)return 0f;
            if(dimsItself||previewed)return 1f;
            float forward=Mathf.Clamp01(forwardIntensity);
            return awareness?Mathf.Max(forward,Mathf.Clamp01(awarenessIntensity)):forward;
        }
        /// <summary>Classic instrument held at full intensity by FaaClassicAnalogHud (pure, for tests): the previewed one, only in Classic.</summary>
        public static string ClassicPreviewId(bool classicActive,string previewId)=>classicActive?previewId:null;

        private sealed class StyleGate
        {
            public CanvasGroup group;public bool created;public float alpha;public bool interactable,blocks;
            public HUDControl.Core.HUDElementBase source;
            public bool dimsItself,awareness,unusualDeclutter;
            public void Set(bool visible,float factor=1f)
            {
                if(group==null)return;
                visible&=source==null||source.IsEnabled;
                float k=Mathf.Clamp01(factor),a=visible?alpha*k:0;bool live=visible&&k>.5f;
                if(Mathf.Abs(group.alpha-a)>.0005f)group.alpha=a;
                if(group.interactable!=(live&&interactable))group.interactable=live&&interactable;
                if(group.blocksRaycasts!=(live&&blocks))group.blocksRaycasts=live&&blocks;
            }
            public void Restore(){Set(true);if(created&&group!=null)Destroy(group);}
        }
        private void BindSymbologyVersions(Canvas flight,Transform originalRoot,Canvas headingCanvas)
        {
            digitalModules.Clear();digitalModules.AddRange(modules);
            foreach(var m in digitalModules)
            {
                classicSourceRoots[m.Id]=m.Target;
                classicSourceElements[m.Id]=m.Target.GetComponent<HUDControl.Core.HUDElementBase>();
            }
            digitalRoot=originalRoot;SyncStyleGates();
            // The shared heading tape is NOT gated: it is the one heading reference in both styles (zone Z5). Classic lays out
            // around it and shows its own 'HDG' fallback only while the tape is hidden.
            var go=new GameObject("FAA Classic Analog Symbology",typeof(RectTransform));
            ClassicHud=go.AddComponent<FaaClassicAnalogHud>();ClassicHud.Build(flight,originalRoot);
            foreach(var pair in ClassicHud.InstrumentRoots)
                classicModules.Add(new FaaNonConformalScaleTarget(pair.Key,ClassicHud.Captions[pair.Key],pair.Value,1f));
            colorManager=FindFirstObjectByType<SymbologyColorManager>();
            symbologyKey=profileKey+".Symbology.v1";
            var selected=FaaSymbologyVersion.Digital;
            ClassicInstrumentAttitude=true;ReducedSymbologyMotion=false;UsePilotSymbologyColor=false;
            if(PersistChanges&&PlayerPrefs.HasKey(symbologyKey)&&FaaSymbologyPreferences.TryParse(PlayerPrefs.GetString(symbologyKey),out var saved))
            {
                // Existing v1 workspace remains the canonical Digital profile; classic scales are separate.
                foreach(var item in saved.classic)foreach(var target in classicModules)if(item.id==target.Id)target.Layout.scale=item.scale;
                selected=saved.selected;ClassicInstrumentAttitude=saved.localAttitude;
                ReducedSymbologyMotion=saved.reducedMotion;UsePilotSymbologyColor=saved.pilotColor;
            }
            CurrentSymbology=selected;
            modules.Clear();modules.AddRange(selected==FaaSymbologyVersion.Digital?digitalModules:classicModules);
            ApplySymbologyPresentation();
        }
        private void AddStyleGate(Transform root)
        {
            // Unity can return a destroyed/native-null wrapper; CLR ?? does not honor its null check.
            var existing=root.GetComponent<CanvasGroup>();var g=existing!=null?existing:root.gameObject.AddComponent<CanvasGroup>();
            digitalGates.Add(new StyleGate{group=g,created=existing==null,alpha=g.alpha,interactable=g.interactable,blocks=g.blocksRaycasts,
                source=root.GetComponent<HUDControl.Core.HUDElementBase>(),dimsItself=GateDimsItself(root.name),awareness=GateIsAwareness(root.name),
                unusualDeclutter=GateDeclutteredInUnusualAttitude(root.name)});
        }
        /// <summary>Gates children created after binding (for example the runtime FMA) so Classic still hides every Digital item. Runs only when the child count changes.</summary>
        private void SyncStyleGates()
        {
            if(digitalRoot==null||digitalRoot.childCount==gatedChildCount)return;
            gatedChildCount=digitalRoot.childCount;
            foreach(Transform child in digitalRoot)
            {
                bool gated=false;
                foreach(var gate in digitalGates)if(gate.group!=null&&gate.group.transform==child){gated=true;break;}
                if(!gated)AddStyleGate(child);
            }
        }
        /// <summary>The selected instrument while the Settings INSTRUMENTS page is open and the forward HUD is dimmed.</summary>
        private void ResolveInspectionPreview()
        {
            Transform target=null;string id=null;
            if(MenuOpen&&FaaHudInspection.Active&&hudPage!=null&&hudPage.gameObject.activeSelf)
                foreach(var m in modules)if(m.Id==SelectedId){target=m.Target;id=m.Id;break;}
            InspectionPreviewTarget=target;InspectionPreviewId=id;
        }
        public bool SetSymbologyVersion(FaaSymbologyVersion version)
        {
            if(!System.Enum.IsDefined(typeof(FaaSymbologyVersion),version)||ClassicHud==null)return false;
            LaptopCamera?.CancelSizing();CancelManipulation();gestures?.CancelPointers();
            // An explicit style choice returns from the separate developer UI Toolkit renderer.
            var backend=FindFirstObjectByType<FaaHudModeSwitcher>();
            if(backend!=null&&backend.ActiveMode!=FaaHudModeSwitcher.HudMode.LegacyUGUI)backend.UseLegacyHud();
            CurrentSymbology=version;
            modules.Clear();modules.AddRange(version==FaaSymbologyVersion.Digital?digitalModules:classicModules);
            if(GetPanel(SelectedId)==null&&!modules.Exists(m=>m.Id==SelectedId))SelectedId="airspeed";
            ApplySymbologyPresentation();RefreshInstrumentPicker();
            MarkChanged();RefreshTransforms();RefreshControls();SaveNow();return true;
        }
        public void SetClassicInstrumentAttitude(bool enabled)
        {ClassicInstrumentAttitude=enabled;ApplySymbologyPresentation();RefreshInstrumentPicker();MarkChanged();RefreshControls();SaveNow();}
        public void SetReducedSymbologyMotion(bool enabled)
        {ReducedSymbologyMotion=enabled;ApplySymbologyPresentation();MarkChanged();RefreshControls();SaveNow();}
        public void SetPilotSymbologyColor(bool enabled)
        {UsePilotSymbologyColor=enabled;ApplySymbologyPresentation();MarkChanged();RefreshControls();SaveNow();}
        private void ApplySymbologyPresentation()
        {
            if(ClassicHud==null)return;
            bool classic=CurrentSymbology==FaaSymbologyVersion.ClassicAnalog;
            SyncStyleGates();ResolveInspectionPreview();
            // Inspection declutter: both styles dim through FaaHudInspection (side panel in view, or the view turned off-axis).
            // Gates of layers that dim themselves are left alone (no double dimming, IAS/ALT keep their awareness floor);
            // the instrument being resized from Settings stays at full intensity.
            float forward=FaaHudInspection.ForwardHudIntensity,awareness=FaaHudInspection.AwarenessHudIntensity;
            bool unusual=FaaRotorcraftConformalLayer.UnusualAttitudeActive;
            Transform preview=InspectionPreviewTarget;
            foreach(var gate in digitalGates)
                gate.Set(!classic,GateFactor(gate.dimsItself,gate.awareness,preview!=null&&gate.group!=null&&gate.group.transform==preview,gate.unusualDeclutter,unusual,forward,awareness));
            ClassicHud.LocalAttitude=ClassicInstrumentAttitude;
            // The shared FaaHudInspection fade (with the awareness floor) is applied by Classic per element; the legacy uniform
            // multiplier stays at 1. Native hooks: the previewed dial stays at full intensity; deviation scales follow the shared
            // unusual-attitude declutter.
            ClassicHud.PanelInspectionOpacity=1f;
            string classicPreview=ClassicPreviewId(classic,InspectionPreviewId);
            if(!string.Equals(ClassicHud.PreviewInstrumentId,classicPreview))ClassicHud.PreviewInstrumentId=classicPreview;
            if(ClassicHud.DeclutterDeviation!=unusual)ClassicHud.DeclutterDeviation=unusual;
            ClassicHud.ReducedMotion=ReducedSymbologyMotion;
            ClassicHud.ReferenceGreen=UsePilotSymbologyColor&&colorManager!=null?colorManager.CurrentColor:FaaClassicAnalogHud.DefaultReferenceGreen;
            ClassicHud.SetVisible(classic);ClassicHud.ApplyLayout();
            foreach(var pair in ClassicHud.InstrumentRoots)
                // Classic 'heading' is the mode annunciator only; hiding the Digital heading tape must not hide it.
                if(pair.Key!="heading"&&classicSourceRoots.TryGetValue(pair.Key,out var source)&&source!=null)
                {
                    bool visible=source.gameObject.activeInHierarchy;
                    if(classicSourceElements.TryGetValue(pair.Key,out var element)&&element!=null)visible&=element.IsEnabled;
                    if(pair.Value.gameObject.activeSelf!=visible)pair.Value.gameObject.SetActive(visible);
                }
        }
        private void RefreshInstrumentPicker()
        {
            selectionIds.Clear();
            bool paired=CurrentSymbology==FaaSymbologyVersion.Digital;
            foreach(var m in modules)
            {
                if(!m.TryScreenBounds(View,out _))continue;
                // Digital IAS/ALT and TQ/NR always share one size: one picker entry per pair.
                if(paired&&(m.Id=="altitude"&&modules.Exists(o=>o.Id=="airspeed")||m.Id=="nr"&&modules.Exists(o=>o.Id=="torque")))continue;
                selectionIds.Add(m.Id);
            }
            captionId=null;
        }
        private IEnumerable<FaaNonConformalScaleTarget> DigitalProfileModules => digitalModules.Count>0?digitalModules:modules;
        public string ExportSymbologyPreferences()
        {
            var preferences=new FaaSymbologyPreferences{selected=CurrentSymbology,localAttitude=ClassicInstrumentAttitude,
                reducedMotion=ReducedSymbologyMotion,pilotColor=UsePilotSymbologyColor};
            foreach(var m in digitalModules)preferences.digital.Add(new FaaSymbologyScale{id=m.Id,scale=m.Layout.scale});
            foreach(var m in classicModules)preferences.classic.Add(new FaaSymbologyScale{id=m.Id,scale=m.Layout.scale});
            return JsonUtility.ToJson(preferences);
        }
        private void SaveSymbologyPreferences()
        {if(!string.IsNullOrEmpty(symbologyKey)&&ClassicHud!=null)PlayerPrefs.SetString(symbologyKey,ExportSymbologyPreferences());}
        private void ReleaseSymbologyVersions()
        {
            foreach(var gate in digitalGates)gate.Restore();digitalGates.Clear();digitalRoot=null;gatedChildCount=-1;
            if(ClassicHud!=null){ClassicHud.SetVisible(false);Destroy(ClassicHud.gameObject);}ClassicHud=null;
            InspectionPreviewTarget=null;InspectionPreviewId=null;
            modules.Clear();modules.AddRange(digitalModules);classicModules.Clear();digitalModules.Clear();
            CurrentSymbology=FaaSymbologyVersion.Digital;symbologyKey=null;
            classicSourceRoots.Clear();classicSourceElements.Clear();
        }
    }
}
