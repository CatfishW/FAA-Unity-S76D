using System.Collections.Generic;
using System.Globalization;
using FAA.XPlaneIntegration.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// Classic Analog flight display (head-fixed, non-conformal). Layout in "frame" units (y down, 1 frame unit = 1 reference-canvas unit at 1920x1080):
    /// - Basic-T: attitude ball at the screen centre, airspeed left and altitude right with their centres on the attitude reference,
    ///   torque/rotor-NR dials below them, outboard of the shared heading-scale slot (Z5) that stays free for the heading tape;
    /// - roll scale concentric with the attitude (sky pointer, like Digital); G/S between attitude and altimeter and LOC above the
    ///   heading slot, both only while guidance is expected; VSI right of the altimeter;
    /// - mode annunciator (FMA) in the shared top-centre row (Z1) with the Digital geometry: fixed C|R|P|STATUS columns, bold active
    ///   modes, small dim axis prefixes outside the change box, armed modes in cyan below.
    /// Failure flags are boxed amber mnemonics at the data they replace; a stale feed gives one centred NO FLIGHT DATA flag.
    /// Inspection intensity: essential awareness readouts (dial windows, VS readout, FMA, heading readout, flags) use
    /// max(ForwardIntensityFor(t), AwarenessHudIntensity); everything else uses ForwardIntensityFor(t). The dimming is applied to the
    /// colours, so the root CanvasGroup only carries visibility and the legacy <see cref="PanelInspectionOpacity"/>.
    /// </summary>
    [DefaultExecutionOrder(12480),DisallowMultipleComponent]
    public sealed class FaaClassicAnalogHud:MonoBehaviour
    {
        /// <summary>Default Classic green: same 94 deg hue as the supplied reference with higher luminance (about 0.67 vs 0.40) for contrast over terrain and haze.</summary>
        public static readonly Color DefaultReferenceGreen=new Color(150f/255f,235f/255f,90f/255f,1);
        /// <summary>The original reference literal. Assigning it selects <see cref="DefaultReferenceGreen"/> (kept for callers that still pass the literal).</summary>
        public static readonly Color LegacyReferenceGreen=new Color(117f/255f,187f/255f,64f/255f,1);
        public const float AttitudeX=420f,AttitudeY=385f,DialOffset=330f,BigDialRadius=115f,SmallDialRadius=76f;
        /// <summary>Underlay (halo) strength for every Classic text: stronger than the shared default because the pale green sits over haze.</summary>
        public const float HaloStrength=.9f;
        /// <summary>Top edge of the mode row in reference units from the top of the screen (zone Z1, y 16-80, same as Digital).</summary>
        public const float FmaTopReference=16f,FmaRowHeight=64f;
        public static readonly float[] FmaColumnX={-225f,-75f,75f,225f};
        public const float FmaColumnWidth=150f,FmaBoxHeight=32f,FmaActiveSize=28f,FmaArmedSize=20f;
        /// <summary>Active and armed row centres in mode-root local units (Digital: 17 and 48 below the row top).</summary>
        public const float FmaActiveY=FmaRowHeight*.5f-17f,FmaArmedY=FmaRowHeight*.5f-48f;
        /// <summary>Axis prefix: small, quiet, right-aligned just outside the change box (Digital geometry).</summary>
        public const float FmaPrefixSize=18f,FmaPrefixWidth=36f,FmaPrefixGap=4f;
        /// <summary>Axis prefixes for the C|R|P columns. The Digital mode row reads the same strings (trimmed), so both styles match.</summary>
        public static readonly string[] FmaPrefixes={"C: ","R: ","P: "};
        /// <summary>Reserved heading-scale slot (Z5) in frame units; the small dials stay outboard of it even when the tape is hidden.</summary>
        public static readonly Rect DefaultHeadingSlot=Rect.MinMaxRect(160,632,680,698);
        /// <summary>The published attitude window stays below the roll-scale end marks (arc end height 87.5 x bank scale), so the conformal
        /// ladder of the scene-cue centre never reaches them.</summary>
        public const float WindowTopAboveCentre=85f;
        /// <summary>EditMode test hook only: replaces FaaHudInspection's forward intensity when not NaN. Leave NaN at runtime.</summary>
        public static float ForwardIntensityOverride=float.NaN;
        public readonly Dictionary<string,RectTransform> InstrumentRoots=new();
        public readonly Dictionary<string,string> Captions=new();
        public FaaAnalogAnimation Animation {get;}=new();
        public FaaAnalogFlightSample LastSample {get;private set;}
        public bool ReducedMotion {get;set;}
        public bool LocalAttitude {get;set;}=true;
        public bool Visible {get;private set;}
        /// <summary>Legacy desktop inspection opacity: a uniform multiplier on the whole Classic root. The workspace leaves it at 1 because
        /// the shared FaaHudInspection fade (with the awareness floor) is applied per element.</summary>
        public float PanelInspectionOpacity {get;set;}=1f;
        /// <summary>Optional floor for the awareness readouts while dimmed. NaN (default) follows FaaHudInspection.AwarenessHudIntensity.</summary>
        public float AwarenessFloor {get;set;}=float.NaN;
        /// <summary>Instrument id held at full intensity (for example the one being resized from Settings), in addition to FaaSpatialWorkspace.ForwardIntensityFor.</summary>
        public string PreviewInstrumentId {get;set;}
        /// <summary>External declutter of the LOC/G/S scales (for example the shared unusual-attitude state); Classic's own declutter still applies.</summary>
        public bool DeclutterDeviation {get;set;}
        /// <summary>Forward intensity currently applied to non-awareness symbology (1 when looking forward).</summary>
        public float ForwardIntensity {get;private set;}=1f;
        /// <summary>Intensity currently applied to the awareness readouts (at least the awareness floor while inspecting).</summary>
        public float AwarenessIntensity {get;private set;}=1f;
        private Color referenceGreen=DefaultReferenceGreen;
        public Color ReferenceGreen {get=>referenceGreen;set=>referenceGreen=Same(value,LegacyReferenceGreen)?DefaultReferenceGreen:value;}
        public RectTransform Root {get;private set;}
        /// <summary>Unusual-attitude declutter state (engage |bank|>60, pitch>+30 or pitch<-20; release |bank|<55 and -15<pitch<+25).</summary>
        public bool UnusualAttitude {get;private set;}
        public FaaFma Fma {get;private set;}
        public bool HeadingTapeVisible {get;private set;}
        private Canvas flightCanvas;
        private Transform sourceRoot;
        private XPlane12ApiHudBridge bridge;
        private CanvasGroup group;
        private FaaClassicGaugeGraphic speed,altitude,torque,rpm;
        private FaaClassicTintGraphic speedFace,altitudeFace,torqueFace,rpmFace,attitudeFill;
        private FaaClassicAttitudeGraphic attitude;
        private FaaClassicBankGraphic bank;
        private FaaClassicDeviationGraphic loc,gs,vsi;
        private RectTransform rootSpeed,rootAltitude,rootTorque,rootNr,rootBank,rootAttitude,rootLoc,rootGs,rootVsi,rootModes;
        private TMP_Text collectiveMode,rollMode,pitchMode,coupling,collectiveArmed,rollArmed,pitchArmed,heading,dataFlag;
        private TMP_Text[] modeActive,modeArmed,modePrefix;
        private readonly float[] boxWidth={60f,60f,60f,60f};
        private FaaClassicBoxGraphic modeBoxes,flagBox;
        private RectTransform attitudeWindow;private FaaHudKeepOutRegion attitudeWindowRegion;
        private RectTransform rollPointer;
        private float nextSourceSearch,nextTapeSearch;
        private readonly float[] layoutScales=new float[16];
        private readonly FaaFmaChangeTracker fmaTracker=new();
        private readonly List<CanvasGroup> sourceGroups=new(),tapeGroups=new();
        private readonly Vector3[] corners=new Vector3[4];
        private RectTransform tape;private Canvas tapeCanvas;
        // Legibility floor at the rendered size (module scale x composition fit): texts grow only when their module is shrunk.
        private readonly List<TMP_Text> floorTexts=new();private readonly List<float> floorNominal=new(),floorNominalMin=new();
        private readonly List<int> floorOwner=new();private readonly List<RectTransform> floorRoots=new();private readonly List<float> floorScale=new();
        // Allocation-free presentation caches (text is assigned only when its content changes).
        private bool fmaCached,lastFresh;private string lastCoupling,lastRoll,lastPitch,lastRollArmed,lastPitchArmed;
        private readonly string[] activeSource=new string[3];private string statusSource;
        private int lastHeading=int.MinValue;private string headingText="";
        private int lastE1=int.MinValue,lastE2=int.MinValue;private string splitText="";

        private static bool Same(Color a,Color b)=>Mathf.Abs(a.r-b.r)<.002f&&Mathf.Abs(a.g-b.g)<.002f&&Mathf.Abs(a.b-b.b)<.002f&&Mathf.Abs(a.a-b.a)<.002f;
        /// <summary>Awareness intensity for a forward intensity, the same mapping as FaaHudInspection.AwarenessHudIntensity (pure, for tests).</summary>
        public static float AwarenessFor(float forward)=>Mathf.Lerp(FaaHudInspection.AwarenessIntensity,1f,Mathf.InverseLerp(FaaHudInspection.InspectionIntensity,1f,forward));
        /// <summary>Prefix text drawn for a mode column (trimmed; a blank entry falls back to the column letter, as on Digital).</summary>
        public static string PrefixFor(int column)
        {
            string p=FmaPrefixes!=null&&column>=0&&column<FmaPrefixes.Length?FmaPrefixes[column]:null;
            return string.IsNullOrWhiteSpace(p)?(column==0?"C:":column==1?"R:":"P:"):p.Trim();
        }

        public void Build(Canvas canvas,Transform originalRoot)
        {
            flightCanvas=canvas;sourceRoot=originalRoot;
            Root=(RectTransform)transform;Root.SetParent(canvas.transform,false);Root.anchorMin=Root.anchorMax=Root.pivot=new Vector2(.5f,.5f);
            Root.sizeDelta=new Vector2(960,820);Root.anchoredPosition=Vector2.zero;
            group=gameObject.AddComponent<CanvasGroup>();group.interactable=false;group.blocksRaycasts=false;
            // The reference's black is treated as transparency, never an opaque cockpit-blocking card; translucent faces and plates only.
            speed=Gauge("airspeed","Airspeed dial",AttitudeX-DialOffset,AttitudeY,FaaClassicGaugeGraphic.Gauge.Airspeed,out speedFace,out rootSpeed);
            altitude=Gauge("altitude","Altitude dial",AttitudeX+DialOffset,AttitudeY,FaaClassicGaugeGraphic.Gauge.Altitude,out altitudeFace,out rootAltitude);
            torque=Gauge("torque","Engine torque dial",AttitudeX-DialOffset,603,FaaClassicGaugeGraphic.Gauge.Torque,out torqueFace,out rootTorque);
            rpm=Gauge("nr","Rotor RPM dial",AttitudeX+DialOffset,603,FaaClassicGaugeGraphic.Gauge.RotorRpm,out rpmFace,out rootNr);
            // Pivot at the attitude centre, rect over the arc band only: scaling keeps the roll scale concentric.
            rootBank=Instrument("bank","Bank and slip references",AttitudeX,AttitudeY,400,150);rootBank.pivot=new Vector2(.5f,-55f/150f);
            bank=rootBank.gameObject.AddComponent<FaaClassicBankGraphic>();
            // Protected areas around the roll-scale end marks, the fixed index and the moving pointer: the scene-cue (conformal)
            // horizon and ladder gap around them instead of striking through the arc.
            float arc=FaaClassicBankGraphic.Radius;
            KeepOutChild(rootBank,"Roll scale left end","classic-roll-end-left",FaaAnalogVector.Bearing(-60f,arc+11f),new Vector2(28,24));
            KeepOutChild(rootBank,"Roll scale right end","classic-roll-end-right",FaaAnalogVector.Bearing(60f,arc+11f),new Vector2(28,24));
            KeepOutChild(rootBank,"Roll scale index","classic-roll-index",new Vector2(0,arc+13f),new Vector2(26,26));
            rollPointer=KeepOutChild(rootBank,"Roll pointer","classic-roll-pointer",new Vector2(0,arc-17f),new Vector2(42,42));
            float ball=FaaClassicAttitudeGraphic.Radius;
            rootAttitude=Instrument("attitude","Non-conformal attitude",AttitudeX,AttitudeY,ball*2f,ball*2f);
            rootAttitude.gameObject.AddComponent<RectMask2D>();
            // Sky/ground fill first (drawn behind), then the strokes.
            var fillGo=new GameObject("Classic attitude sky-ground",typeof(RectTransform));fillGo.transform.SetParent(rootAttitude,false);
            ((RectTransform)fillGo.transform).sizeDelta=rootAttitude.sizeDelta;
            attitudeFill=fillGo.AddComponent<FaaClassicTintGraphic>();attitudeFill.ConfigureSkyGround(ball);
            var draw=new GameObject("Classic attitude geometry",typeof(RectTransform));draw.transform.SetParent(rootAttitude,false);
            var rt=(RectTransform)draw.transform;rt.sizeDelta=rootAttitude.sizeDelta;
            attitude=draw.AddComponent<FaaClassicAttitudeGraphic>();attitude.Configure(ReferenceGreen);attitude.Fill=attitudeFill;
            loc=Deviation("localizer","Localizer dots",AttitudeX,548,300,40,FaaClassicDeviationGraphic.Scale.Localizer,out rootLoc);
            gs=Deviation("glideslope","Glideslope dots",AttitudeX+180,AttitudeY,44,260,FaaClassicDeviationGraphic.Scale.Glideslope,out rootGs);
            vsi=Deviation("vertical-speed","Vertical speed scale",AttitudeX+DialOffset+146,AttitudeY,130,320,FaaClassicDeviationGraphic.Scale.VerticalSpeed,out rootVsi);
            // The module id stays "heading" for saved size preferences; it is the mode annunciator only (heading lives in the shared tape).
            rootModes=Instrument("heading","Mode annunciation",AttitudeX,-108,600,FmaRowHeight);
            var boxGo=new GameObject("Mode change boxes",typeof(RectTransform));boxGo.transform.SetParent(rootModes,false);
            ((RectTransform)boxGo.transform).sizeDelta=rootModes.sizeDelta;modeBoxes=boxGo.AddComponent<FaaClassicBoxGraphic>();modeBoxes.Allocate(4);
            collectiveMode=ModeText(rootModes,"Collective mode",0,true);rollMode=ModeText(rootModes,"Roll mode",1,true);
            pitchMode=ModeText(rootModes,"Pitch mode",2,true);coupling=ModeText(rootModes,"Coupling",3,true);
            collectiveArmed=ModeText(rootModes,"Collective armed",0,false);rollArmed=ModeText(rootModes,"Lateral armed",1,false);pitchArmed=ModeText(rootModes,"Vertical armed",2,false);
            modeActive=new[]{collectiveMode,rollMode,pitchMode,coupling};modeArmed=new[]{collectiveArmed,rollArmed,pitchArmed};
            modePrefix=new TMP_Text[3];
            for(int i=0;i<3;i++)
            {
                var p=Text(rootModes,(i==0?"Collective":i==1?"Roll":"Pitch")+" prefix",Vector2.zero,new Vector2(FmaPrefixWidth,FmaBoxHeight),FmaPrefixSize);
                p.alignment=TextAlignmentOptions.Right;p.text=PrefixFor(i);modePrefix[i]=p;p.gameObject.SetActive(false);LayoutColumn(i);
            }
            LayoutColumn(3);
            heading=Text(Root,"Heading fallback",new Vector2(AttitudeX-480,390-DefaultHeadingSlot.center.y),new Vector2(200,34),28);
            var flagGo=new GameObject("Data validity box",typeof(RectTransform));flagGo.transform.SetParent(Root,false);
            var flagRect=(RectTransform)flagGo.transform;flagRect.anchoredPosition=new Vector2(AttitudeX-480,390-AttitudeY);flagRect.sizeDelta=new Vector2(280,60);
            flagBox=flagGo.AddComponent<FaaClassicBoxGraphic>();flagBox.Allocate(1);
            dataFlag=Text(Root,"Data validity",new Vector2(AttitudeX-480,390-AttitudeY),new Vector2(250,40),FaaHudStyle.Data);
            // The clear attitude field between the dials, published so conformal cues stay inside it (scene-cue centre mode).
            var windowGo=new GameObject("Classic attitude window",typeof(RectTransform));windowGo.transform.SetParent(Root,false);
            attitudeWindow=(RectTransform)windowGo.transform;attitudeWindow.anchoredPosition=new Vector2(AttitudeX-480,390-AttitudeY);attitudeWindow.sizeDelta=new Vector2(300,330);
            attitudeWindowRegion=FaaHudKeepOutRegion.Ensure(windowGo,"classic-attitude-window",FaaKeepOutKind.AttitudeWindow,0f);
            // Reference green needs local contrast against a bright outside scene. Dark outlines on strokes (never on the translucent fills,
            // which they would darken four times over), not an opaque backdrop, preserve both readability and the unobstructed view.
            foreach(var graphic in Root.GetComponentsInChildren<MaskableGraphic>(true))
                if(!(graphic is TMP_Text)&&!(graphic is FaaClassicTintGraphic))
                {
                    var outline=graphic.gameObject.AddComponent<Outline>();
                    outline.effectColor=new Color(0,0,0,.85f);outline.effectDistance=new Vector2(1.2f,-1.2f);
                }
            // Text contrast: TMP underlay halo that survives SymbologyColorManager tinting (the old outline keyword was never enabled).
            foreach(var text in Root.GetComponentsInChildren<TMP_Text>(true))FaaHudStyle.ApplyHalo(text,HaloStrength);
            RecordTextFloors();
            Present(default,0,true);SetVisible(false);
        }
        private RectTransform Instrument(string id,string caption,float x,float y,float w,float h)
        {
            var go=new GameObject("Classic "+caption,typeof(RectTransform));var t=(RectTransform)go.transform;t.SetParent(Root,false);
            t.anchorMin=t.anchorMax=t.pivot=new Vector2(.5f,.5f);t.anchoredPosition=new Vector2(x-480,390-y);t.sizeDelta=new Vector2(w,h);
            InstrumentRoots.Add(id,t);Captions.Add(id,caption);return t;
        }
        private static RectTransform Stretch(RectTransform parent,string name)
        {
            var go=new GameObject(name,typeof(RectTransform));var t=(RectTransform)go.transform;t.SetParent(parent,false);
            t.anchorMin=Vector2.zero;t.anchorMax=Vector2.one;t.offsetMin=t.offsetMax=Vector2.zero;t.pivot=new Vector2(.5f,.5f);return t;
        }
        private FaaClassicGaugeGraphic Gauge(string id,string caption,float x,float y,FaaClassicGaugeGraphic.Gauge kind,out FaaClassicTintGraphic face,out RectTransform root)
        {
            bool small=FaaClassicGaugeGraphic.IsSmall(kind);
            root=Instrument(id,caption,x,y,small?180:250,small?230:290);
            // Dark translucent dial face and window plate behind the dial strokes: local contrast >= 3:1 over haze (AC 25-11B, HF-STD-001B).
            face=Stretch(root,"Dial face").gameObject.AddComponent<FaaClassicTintGraphic>();
            face.ConfigureDialFace((small?SmallDialRadius:BigDialRadius)+1f,FaaClassicGaugeGraphic.WindowRect(kind));
            var g=Stretch(root,"Dial").gameObject.AddComponent<FaaClassicGaugeGraphic>();g.Configure(kind,ReferenceGreen);return g;
        }
        private FaaClassicDeviationGraphic Deviation(string id,string caption,float x,float y,float width,float height,FaaClassicDeviationGraphic.Scale kind,out RectTransform root)
        {root=Instrument(id,caption,x,y,width,height);var d=root.gameObject.AddComponent<FaaClassicDeviationGraphic>();d.Configure(kind,ReferenceGreen);return d;}
        private static RectTransform KeepOutChild(RectTransform parent,string name,string id,Vector2 local,Vector2 size)
        {
            var go=new GameObject(name,typeof(RectTransform));var t=(RectTransform)go.transform;t.SetParent(parent,false);
            t.anchorMin=t.anchorMax=t.pivot=new Vector2(.5f,.5f);t.sizeDelta=size;t.localPosition=new Vector3(local.x,local.y,0);
            FaaHudKeepOutRegion.Ensure(go,id,FaaKeepOutKind.Symbology,4f);return t;
        }
        private TMP_Text ModeText(Transform parent,string name,int column,bool active)
        {
            var t=Text(parent,name,new Vector2(FmaColumnX[column],active?FmaActiveY:FmaArmedY),new Vector2(FmaColumnWidth-4f,active?FmaBoxHeight:24f),active?FmaActiveSize:FmaArmedSize);
            t.fontStyle=active?FontStyles.Bold:FontStyles.Normal;
            if(active){t.enableAutoSizing=true;t.fontSizeMin=FaaHudStyle.Secondary;t.fontSizeMax=FmaActiveSize;}
            return t;
        }
        private TMP_Text Text(Transform parent,string name,Vector2 position,Vector2 size,float font)
        {
            var go=new GameObject(name,typeof(RectTransform));go.transform.SetParent(parent,false);var t=go.AddComponent<TextMeshProUGUI>();
            t.font=TMP_Settings.defaultFontAsset;t.fontSize=FaaHudStyle.Legible(font);t.color=ReferenceGreen;t.raycastTarget=false;t.richText=false;
            t.alignment=TextAlignmentOptions.Center;t.textWrappingMode=TextWrappingModes.NoWrap;
            t.rectTransform.anchoredPosition=position;t.rectTransform.sizeDelta=size;FaaHudStyle.ApplyHalo(t,HaloStrength);return t;
        }
        /// <summary>Mode-row box for a column in mode-root local units (full column width).</summary>
        public static Rect FmaBoxRect(int column)=>FmaBoxRect(column,FmaColumnWidth-10f);
        /// <summary>Mode-row box of <paramref name="width"/> centred on a column's active mode (mode-root local units).</summary>
        public static Rect FmaBoxRect(int column,float width)=>new Rect(FmaColumnX[column]-width*.5f,FmaActiveY-FmaBoxHeight*.5f,width,FmaBoxHeight);
        /// <summary>Change box snug around the active text (Digital: preferred width + 14), prefix right edge just outside it.</summary>
        private void LayoutColumn(int column)
        {
            var t=modeActive[column];
            float w=t==null||string.IsNullOrEmpty(t.text)?60f:Mathf.Clamp(t.GetPreferredValues(t.text).x+14f,40f,FmaColumnWidth-2f);
            boxWidth[column]=w;
            if(column>=3||modePrefix==null||modePrefix[column]==null)return;
            var at=new Vector2(FmaColumnX[column]-w*.5f-FmaPrefixGap-FmaPrefixWidth*.5f,FmaActiveY);
            if(modePrefix[column].rectTransform.anchoredPosition!=at)modePrefix[column].rectTransform.anchoredPosition=at;
        }
        private void RecordTextFloors()
        {
            floorTexts.Clear();floorNominal.Clear();floorNominalMin.Clear();floorOwner.Clear();floorRoots.Clear();floorScale.Clear();
            foreach(var pair in InstrumentRoots){floorRoots.Add(pair.Value);floorScale.Add(1f);}
            floorRoots.Add(null);floorScale.Add(1f);
            foreach(var t in Root.GetComponentsInChildren<TMP_Text>(true))
            {
                int owner=floorRoots.Count-1;
                for(int i=0;i<floorRoots.Count-1;i++)if(t.transform.IsChildOf(floorRoots[i])){owner=i;break;}
                floorTexts.Add(t);floorNominal.Add(t.fontSize);floorNominalMin.Add(t.enableAutoSizing?t.fontSizeMin:t.fontSize);floorOwner.Add(owner);
            }
        }
        /// <summary>Raises text whose module is drawn below 1 reference unit per local unit, so it never renders under FaaHudStyle.MinLabel. Runs only when a scale changes.</summary>
        private void ApplyTextFloors(float fit,float k)
        {
            for(int o=0;o<floorRoots.Count;o++)
            {
                var r=floorRoots[o];float s=(r!=null?Mathf.Abs(r.localScale.x):1f)*fit/Mathf.Max(.0001f,k);
                if(!(s>.05f))s=1f;
                if(Mathf.Abs(s-floorScale[o])<.005f)continue;
                floorScale[o]=s;float floor=FaaHudStyle.MinLabel/s;
                for(int i=0;i<floorTexts.Count;i++)
                {
                    if(floorOwner[i]!=o)continue;var t=floorTexts[i];if(t==null)continue;
                    if(t.enableAutoSizing){float min=Mathf.Max(floorNominalMin[i],floor);if(t.fontSizeMin!=min){t.fontSizeMin=min;t.fontSizeMax=Mathf.Max(floorNominal[i],min);}}
                    else{float size=Mathf.Max(floorNominal[i],floor);if(t.fontSize!=size)t.fontSize=size;}
                }
            }
        }

        public void SetVisible(bool visible)
        {
            bool changed=Visible!=visible;
            if(changed){Animation.Reset();fmaTracker.Reset();}Visible=visible;
            if(group!=null&&(changed||!visible))group.alpha=visible?Mathf.Clamp01(PanelInspectionOpacity):0;
            if(enabled!=visible)enabled=visible;
            if(changed&&visible)
            {
                if(bridge==null)bridge=FindFirstObjectByType<XPlane12ApiHudBridge>();
                Present(FaaAnalogFlightSample.Capture(bridge),0,true);
            }
        }
        public void ApplyLayout()
        {
            if(Root==null||flightCanvas==null)return;
            // Preserve the composition at 100%; enlarged individual instruments get additional separation instead of running into neighbours.
            int n=0;foreach(var p in InstrumentRoots)if(n<layoutScales.Length)layoutScales[n++]=p.Value.localScale.x;
            System.Array.Sort(layoutScales,0,n);
            float common=n>0?layoutScales[n/2]:1f;
            float air=S("airspeed"),alt=S("altitude"),att=S("attitude"),tor=S("torque"),rot=S("nr");
            float bankScale=S("bank"),modeScale=S("heading"),locScale=S("localizer"),g=S("glideslope"),v=S("vertical-speed");
            float ball=FaaClassicAttitudeGraphic.Radius;
            // Symmetric T: both big dials at the same offset; the gap right of the attitude also holds the G/S scale.
            float off=Mathf.Max(DialOffset*common,Mathf.Max(115*air,115*alt)+ball*att+80*common);
            float left=AttitudeX-off,right=AttitudeX+off,mainY=AttitudeY;
            Place("airspeed",left,mainY);Place("altitude",right,mainY);
            // Small dials directly under the big dials, moved outboard only as far as the heading-scale slot requires.
            Rect slot=HeadingSlot();
            float smallScale=Mathf.Max(tor,rot),smallY=mainY+115*Mathf.Max(air,alt)+24*common+79*smallScale;
            float smallOff=off;
            bool rowsOverlap=smallY+105*smallScale>slot.yMin-8*common&&smallY-79*smallScale<slot.yMax+8*common;
            if(rowsOverlap)smallOff=Mathf.Min(off+140*common,Mathf.Max(off,Mathf.Max(AttitudeX-(slot.xMin-8*common-84*tor),slot.xMax+8*common+84*rot-AttitudeX)));
            Place("torque",AttitudeX-smallOff,smallY);Place("nr",AttitudeX+smallOff,smallY);
            Place("bank",AttitudeX,AttitudeY);Place("attitude",AttitudeX,AttitudeY);
            float locY=AttitudeY+ball*att+16*common+12*locScale;
            Place("localizer",AttitudeX,locY);
            float gsX=Mathf.Max(AttitudeX+(ball*att+off-115*alt)*.5f,AttitudeX+172*bankScale+8*g);
            gsX=Mathf.Min(gsX,right-115*alt-14*g);
            Place("glideslope",gsX,AttitudeY);
            Place("vertical-speed",right+115*alt+18*common+13*v,AttitudeY);
            // Fit: 1 frame unit = 1 reference unit at 1920x1080, shrunk only if the composition would leave y 95..985 or the screen width.
            var rect=((RectTransform)flightCanvas.transform).rect;
            float k=Mathf.Max(.01f,rect.height/1080f),up=1,down=1,side=1;
            foreach(var pair in InstrumentRoots)
            {
                if(pair.Key=="heading")continue;
                var r=pair.Value;r.GetLocalCorners(corners);
                for(int i=0;i<4;i++)
                {
                    float dx=r.anchoredPosition.x+corners[i].x*r.localScale.x-(AttitudeX-480),dy=r.anchoredPosition.y+corners[i].y*r.localScale.y-(390-AttitudeY);
                    up=Mathf.Max(up,dy);down=Mathf.Max(down,-dy);side=Mathf.Max(side,Mathf.Abs(dx));
                }
            }
            float half=rect.height*.5f-95f*k;
            float fit=Mathf.Min(k,half/up,half/down,(rect.width*.5f-20f*k)/side);
            fit=Mathf.Max(.05f,fit);
            Vector3 size=Vector3.one*fit;Vector2 position=new Vector2((480-AttitudeX)*fit,(AttitudeY-390)*fit);
            if(Root.localScale!=size)Root.localScale=size;
            if(Root.anchoredPosition!=position)Root.anchoredPosition=position;
            // Mode row in the shared top-centre FMA zone (Z1), independent of the dial fit.
            float fmaCanvasY=rect.height*.5f-(FmaTopReference*k+FmaRowHeight*.5f*modeScale*fit);
            Place("heading",AttitudeX,AttitudeY-fmaCanvasY/fit);
            SetAnchored(heading.rectTransform,new Vector2(AttitudeX-480,390-slot.center.y));
            // Clear attitude field: between the dials, below the roll-scale end marks, above LOC/the heading slot.
            float bottom=Mathf.Min(slot.yMin-10*common,loc.Shown?locY-14*locScale:float.MaxValue);
            float halfWidth=Mathf.Max(ball*att,Mathf.Min(150*common,off-115*Mathf.Max(air,alt)-12*common)),top=AttitudeY-WindowTopAboveCentre*bankScale;
            SetAnchored(attitudeWindow,new Vector2(AttitudeX-480,390-(top+bottom)*.5f));
            Vector2 windowSize=new Vector2(halfWidth*2,Mathf.Max(10,bottom-top));if(attitudeWindow.sizeDelta!=windowSize)attitudeWindow.sizeDelta=windowSize;
            ApplyTextFloors(fit,k);
            bool valid=Visible&&flightCanvas.isActiveAndEnabled&&sourceRoot!=null&&sourceRoot.gameObject.activeInHierarchy;
            if(valid){sourceRoot.GetComponentsInParent(true,sourceGroups);foreach(var cg in sourceGroups)if(cg.alpha<=.001f)valid=false;}
            float alpha=valid?Mathf.Clamp01(PanelInspectionOpacity):0;
            if(group!=null&&Mathf.Abs(group.alpha-alpha)>.0005f)group.alpha=alpha;
            if(InstrumentRoots.TryGetValue("attitude",out var center)&&center.gameObject.activeSelf!=LocalAttitude)center.gameObject.SetActive(LocalAttitude);
        }
        private float S(string id)=>InstrumentRoots.TryGetValue(id,out var r)?Mathf.Abs(r.localScale.x):1f;
        private static void SetAnchored(RectTransform r,Vector2 p){if(r!=null&&r.anchoredPosition!=p)r.anchoredPosition=p;}
        private void Place(string id,float x,float y)
        {
            Vector2 p=new Vector2(x-480,390-y);var r=InstrumentRoots[id];
            if(r.anchoredPosition!=p)r.anchoredPosition=p;
        }
        /// <summary>Union of the reserved heading slot and the shared heading tape's actual rectangle (frame units).</summary>
        private Rect HeadingSlot()
        {
            Rect slot=DefaultHeadingSlot;
            bool usable=TryHeadingTapeFrameRect(out var tapeRect,out bool visible);HeadingTapeVisible=visible;
            if(usable)slot=Rect.MinMaxRect(Mathf.Min(slot.xMin,tapeRect.xMin),Mathf.Min(slot.yMin,tapeRect.yMin),Mathf.Max(slot.xMax,tapeRect.xMax),Mathf.Max(slot.yMax,tapeRect.yMax));
            return slot;
        }
        private bool TryHeadingTapeFrameRect(out Rect frame,out bool visible)
        {
            frame=default;visible=false;
            // Classic is a play-mode view; edit-mode fixtures must not bind to whatever scene happens to be open.
            if(!Application.isPlaying)return false;
            if(tape==null&&Time.unscaledTime>=nextTapeSearch)
            {
                nextTapeSearch=Time.unscaledTime+1f;var canvasGo=GameObject.Find("FAAHeadingTapeCanvas");
                var t=canvasGo!=null?canvasGo.transform.Find("FAA Heading Tape Overlay"):null;
                if(t is RectTransform found){tape=found;tapeCanvas=found.GetComponentInParent<Canvas>(true);tape.GetComponentsInParent(true,tapeGroups);}
            }
            if(tape==null||!tape.gameObject.activeInHierarchy||tapeCanvas==null||!tapeCanvas.isActiveAndEnabled)return false;
            float a=1;foreach(var cg in tapeGroups){if(cg==null||!cg.isActiveAndEnabled)continue;a*=cg.alpha;if(cg.ignoreParentGroups)break;}
            if(a<.05f)return false;
            visible=true;
            var tapeRoot=tapeCanvas.rootCanvas;var myRoot=flightCanvas.rootCanvas;
            Camera tapeCamera=tapeRoot.renderMode==RenderMode.ScreenSpaceOverlay?null:tapeRoot.worldCamera;
            Camera myCamera=myRoot.renderMode==RenderMode.ScreenSpaceOverlay?null:myRoot.worldCamera;
            tape.GetWorldCorners(corners);
            float xMin=float.MaxValue,yMin=float.MaxValue,xMax=float.MinValue,yMax=float.MinValue;
            for(int i=0;i<4;i++)
            {
                Vector2 screen=RectTransformUtility.WorldToScreenPoint(tapeCamera,corners[i]);
                if(!RectTransformUtility.ScreenPointToLocalPointInRectangle(Root,screen,myCamera,out var local))return false;
                float fx=local.x+480,fy=390-local.y;
                xMin=Mathf.Min(xMin,fx);xMax=Mathf.Max(xMax,fx);yMin=Mathf.Min(yMin,fy);yMax=Mathf.Max(yMax,fy);
            }
            frame=Rect.MinMaxRect(xMin,yMin,xMax,yMax);
            // A rectangle far from the expected slot is a projection artefact (for example a world-space XR canvas); ignore it.
            return frame.width>1&&frame.height>1&&frame.width<900&&frame.yMin>AttitudeY&&frame.yMax<AttitudeY+520;
        }
        /// <summary>Screen rectangle of the clear attitude field (Unity screen px, bottom-left origin) while Classic is shown.</summary>
        public bool TryGetAttitudeWindowScreenRect(out Rect rect)
        {
            rect=default;
            return Visible&&attitudeWindowRegion!=null&&attitudeWindowRegion.TryGetScreenRect(out rect);
        }
        /// <summary>Centre and inscribed radius (screen px) of the clear attitude field, for cues that use a circular clear zone.</summary>
        public bool TryGetAttitudeClearZone(out Vector3 centreScreen,out float radiusPx)
        {
            centreScreen=default;radiusPx=0;
            if(!TryGetAttitudeWindowScreenRect(out var rect))return false;
            centreScreen=new Vector3(rect.center.x,rect.center.y,0);radiusPx=Mathf.Min(rect.width,rect.height)*.5f;return radiusPx>1;
        }
        private void LateUpdate()
        {
            if(bridge==null&&Time.unscaledTime>=nextSourceSearch){nextSourceSearch=Time.unscaledTime+1;bridge=FindFirstObjectByType<XPlane12ApiHudBridge>();}
            Present(FaaAnalogFlightSample.Capture(bridge),Time.unscaledDeltaTime,ReducedMotion);ApplyLayout();
        }
        /// <summary>Forward intensity of one instrument: 1 while it is previewed, else the shared inspection fade.</summary>
        private float Intensity(RectTransform root,string id)
        {
            if(PreviewInstrumentId!=null&&id!=null&&string.Equals(PreviewInstrumentId,id))return 1f;
            float k=float.IsNaN(ForwardIntensityOverride)?FaaSpatialWorkspace.ForwardIntensityFor(root):ForwardIntensityOverride;
            return Mathf.Clamp01(k);
        }
        private float awarenessFloorNow=1f;
        private float Awareness(float forward)=>Mathf.Max(forward,awarenessFloorNow);
        private static Color Dim(Color c,float k)=>FaaHudStyle.Dim(c,k);
        public void Present(FaaAnalogFlightSample sample,float deltaTime,bool snap=false)
        {
            LastSample=sample;Animation.Step(sample,deltaTime,snap);var d=Animation.Display;Color c=ReferenceGreen;
            awarenessFloorNow=!float.IsNaN(AwarenessFloor)?Mathf.Clamp01(AwarenessFloor):
                float.IsNaN(ForwardIntensityOverride)?FaaHudInspection.AwarenessHudIntensity:AwarenessFor(ForwardIntensityOverride);
            ForwardIntensity=Intensity(Root,null);AwarenessIntensity=Awareness(ForwardIntensity);
            Color aware=Dim(c,AwarenessIntensity),amber=FaaHudStyle.WithAlpha(FaaHudStyle.Amber,aware.a);
            bool fresh=d.Fresh;
            UpdateUnusual(d);
            // One stale-feed flag replaces every per-instrument flag (the VSI keeps its documented NO DATA).
            speed.SuppressFlag=altitude.SuppressFlag=torque.SuppressFlag=rpm.SuppressFlag=attitude.SuppressFlag=!fresh;
            rpm.Airborne=FaaRotorcraftLimits.LikelyAirborne(fresh&&d.SpeedValid?d.Speed:0f,d.HeightAboveGround,fresh&&d.HeightAboveGroundValid);
            Dial(speed,speedFace,rootSpeed,"airspeed",c,d.Speed,fresh&&d.SpeedValid,"");
            Dial(altitude,altitudeFace,rootAltitude,"altitude",c,d.Altitude,fresh&&d.AltitudeValid,"");
            Dial(torque,torqueFace,rootTorque,"torque",c,d.Torque,fresh&&d.TorqueValid,fresh&&!UnusualAttitude?EngineSplit(d):"");
            Dial(rpm,rpmFace,rootNr,"nr",c,d.Rpm,fresh&&d.RpmValid,"");
            attitude.Unusual=UnusualAttitude;attitude.Present(d.Pitch,d.Roll,fresh&&d.AttitudeValid,Dim(c,Intensity(rootAttitude,"attitude")));
            bool pointer=fresh&&d.AttitudeValid&&FaaAnalogFlightSample.Finite(d.Roll);
            bank.Present(d.Roll,d.Slip,fresh&&d.AttitudeValid,fresh&&d.SlipValid,Dim(c,Intensity(rootBank,"bank")));
            if(rollPointer.gameObject.activeSelf!=pointer)rollPointer.gameObject.SetActive(pointer);
            if(pointer){Vector2 b=FaaAnalogVector.Bearing(FaaClassicBankGraphic.PointerBearing(d.Roll),FaaClassicBankGraphic.Radius-17f);var lp=new Vector3(b.x,b.y,0);if(rollPointer.localPosition!=lp)rollPointer.localPosition=lp;}
            // LOC/G/S only when guidance is expected; decluttered in unusual attitude.
            bool guidance=fresh&&!UnusualAttitude&&!DeclutterDeviation;
            loc.Expected=guidance&&(d.LocValid||d.RollMode=="NAV"||d.RollArmed=="NAV ARM");
            gs.Expected=guidance&&(d.GsValid||d.PitchMode=="G/S"||d.PitchArmed=="G/S ARM");
            loc.Present(d.Localizer,guidance&&d.LocValid,Dim(c,Intensity(rootLoc,"localizer")));gs.Present(-d.Glideslope,guidance&&d.GsValid,Dim(c,Intensity(rootGs,"glideslope")));
            float kVsi=Intensity(rootVsi,"vertical-speed");vsi.ReadoutAlpha=c.a*Awareness(kVsi);
            vsi.Present(d.VerticalSpeed,fresh&&d.VerticalSpeedValid,Dim(c,kVsi));
            float kModes=Awareness(Intensity(rootModes,"heading"));
            PresentModes(d,Dim(c,kModes),FaaHudStyle.WithAlpha(FaaHudStyle.Amber,c.a*kModes));
            PresentHeading(d,aware,amber);
            SetText(dataFlag,fresh?"":"NO FLIGHT DATA");Paint(dataFlag,amber);
            flagBox.Set(0,!fresh,new Rect(-128,-22,256,44),amber);
        }
        /// <summary>One dial: ring, ticks, numerals and needle at the instrument's forward intensity; the window (border, plate, value, unit) at the awareness intensity.</summary>
        private void Dial(FaaClassicGaugeGraphic gauge,FaaClassicTintGraphic face,RectTransform root,string id,Color c,float value,bool valid,string detail)
        {
            float k=Intensity(root,id),readout=Awareness(k);
            gauge.ReadoutAlpha=c.a*readout;gauge.Present(value,valid,detail,Dim(c,k));
            if(face!=null)face.SetFace(c.a*k,c.a*readout);
        }
        private void UpdateUnusual(FaaAnalogFlightSample d)
        {
            if(!d.Fresh||!d.AttitudeValid){UnusualAttitude=false;return;}
            float bankAngle=Mathf.Abs(Mathf.DeltaAngle(0,d.Roll)),p=d.Pitch;
            if(!UnusualAttitude)UnusualAttitude=bankAngle>60f||p>30f||p<-20f;
            else if(bankAngle<55f&&p>-15f&&p<25f)UnusualAttitude=false;
        }
        private string EngineSplit(FaaAnalogFlightSample d)
        {
            // Per-engine torque only when it adds information: a split of 5 points or more, or one expected engine lost.
            bool e1=d.Engine1Valid&&FaaAnalogFlightSample.Finite(d.Engine1Torque),e2=d.Engine2Valid&&FaaAnalogFlightSample.Finite(d.Engine2Torque);
            int a=e1?Mathf.RoundToInt(d.Engine1Torque):int.MinValue,b=e2?Mathf.RoundToInt(d.Engine2Torque):int.MinValue;
            bool show=e1&&e2?Mathf.Abs(a-b)>=5:!d.TorqueValid&&(e1||e2);
            if(!show){lastE1=lastE2=int.MaxValue;return "";}
            if(a!=lastE1||b!=lastE2)
            {
                lastE1=a;lastE2=b;
                splitText="L "+(e1?a.ToString(CultureInfo.InvariantCulture):"--")+"   R "+(e2?b.ToString(CultureInfo.InvariantCulture):"--");
            }
            return splitText;
        }
        private FaaFma ComposeFma(FaaAnalogFlightSample d)
        {
            if(fmaCached&&d.Fresh==lastFresh&&ReferenceEquals(d.Coupling,lastCoupling)&&ReferenceEquals(d.RollMode,lastRoll)&&ReferenceEquals(d.PitchMode,lastPitch)&&
               ReferenceEquals(d.RollArmed,lastRollArmed)&&ReferenceEquals(d.PitchArmed,lastPitchArmed))return Fma;
            fmaCached=true;lastFresh=d.Fresh;lastCoupling=d.Coupling;lastRoll=d.RollMode;lastPitch=d.PitchMode;lastRollArmed=d.RollArmed;lastPitchArmed=d.PitchArmed;
            return FaaFlightModeAnnunciation.Compose(d);
        }
        private void PresentModes(FaaAnalogFlightSample d,Color green,Color amber)
        {
            float now=FaaClassicClock.Now;
            var fma=ComposeFma(d);Fma=fma;fmaTracker.Update(fma,now);
            Color cyan=FaaHudStyle.WithAlpha(FaaHudStyle.Cyan,green.a),quiet=FaaHudStyle.Dim(green,FaaHudStyle.MinQuietAlpha);
            for(int col=0;col<3;col++)
            {
                string active=fma.Active(col),armed=UnusualAttitude?"":fma.Armed(col);
                if(!ReferenceEquals(active,activeSource[col])){activeSource[col]=active;SetText(modeActive[col],active);LayoutColumn(col);}
                // The collective column stays hidden until a real collective-mode source exists.
                if(col==0){bool any=active.Length>0||armed.Length>0;if(collectiveMode.gameObject.activeSelf!=any)collectiveMode.gameObject.SetActive(any);if(collectiveArmed.gameObject.activeSelf!=any)collectiveArmed.gameObject.SetActive(any);}
                bool prefix=active.Length>0;if(modePrefix[col].gameObject.activeSelf!=prefix)modePrefix[col].gameObject.SetActive(prefix);
                Paint(modeActive[col],green);Paint(modePrefix[col],quiet);
                SetText(modeArmed[col],armed);Paint(modeArmed[col],cyan);
                modeBoxes.Set(col,active.Length>0&&fmaTracker.BoxVisible(col,now),FmaBoxRect(col,boxWidth[col]),green);
            }
            string status;Color statusColor;bool box;
            if(fmaTracker.ApOffVisible(now))
            {
                // Autopilot/FD disengagement: boxed AP OFF, flashing for 5 s (box and text together, as on Digital), steady to 10 s, then removed.
                status="AP OFF";statusColor=FaaHudStyle.Dim(amber,fmaTracker.ApOffDrawn(now)?1f:0f);box=true;
            }
            else
            {
                bool stale=!fma.Valid;
                status=fma.Status;statusColor=stale?amber:green;box=!stale&&fma.Status.Length>0&&fmaTracker.BoxVisible(3,now);
            }
            if(!ReferenceEquals(status,statusSource)){statusSource=status;SetText(coupling,status);LayoutColumn(3);}
            Paint(coupling,statusColor);
            modeBoxes.Set(3,box,FmaBoxRect(3,boxWidth[3]),statusColor);
        }
        private void PresentHeading(FaaAnalogFlightSample d,Color green,Color amber)
        {
            // Heading normally comes from the shared heading tape (same place as Digital). This readout appears only when that tape is hidden.
            string s="";Color col=green;
            if(!HeadingTapeVisible&&d.Fresh)
            {
                if(d.AttitudeValid&&FaaAnalogFlightSample.Finite(d.Heading))
                {
                    int h=Mathf.RoundToInt(Mathf.Repeat(d.Heading,360f));if(h==0)h=360;
                    if(h!=lastHeading){lastHeading=h;headingText="HDG "+h.ToString("000",CultureInfo.InvariantCulture)+"°";}
                    s=headingText;
                }
                else{s="HDG";col=amber;}
            }
            SetText(heading,s);Paint(heading,col);
        }
        private static void SetText(TMP_Text t,string s){if(t!=null&&t.text!=s)t.text=s;}
        private static void Paint(TMP_Text t,Color c){if(t!=null&&t.color!=c)t.color=c;}
    }
}
