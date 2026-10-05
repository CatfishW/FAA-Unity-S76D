using System;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Side 3D panels and inspection flow: eased camera turns, framing, declutter, comfort clamp, panel controls.</summary>
    public class FaaSpatialPanelsInspectionTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type T(string name)=>Type.GetType("FAA.Customization."+name+", Assembly-CSharp",true);
        private static Type Camera=>Type.GetType("AircraftControl.Camera.AircraftCameraController, AircraftControl",true);
        private static object Static(Type t,string method,params object[] args)=>t.GetMethod(method,Any).Invoke(null,args);
        private static float F(object o,string field)=>(float)o.GetType().GetField(field,Any).GetValue(o);
        private static bool P(object o,string property)=>(bool)o.GetType().GetProperty(property,Any).GetValue(o);

        private static void WithCamera(Action<object,Action<float>> body)
        {
            var go=new GameObject("Inspection camera fixture",typeof(UnityEngine.Camera));go.SetActive(false);
            try
            {
                var controller=go.AddComponent(Camera);go.SetActive(true);
                var input=Camera.GetMethod("ProcessLookInput",Any);
                body(controller,dt=>input.Invoke(controller,new object[]{false,Vector2.zero,false,dt}));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        private static void Begin(object controller,float yaw,float elevation)=>
            Assert.That((bool)Camera.GetMethod("BeginPanelInspection").Invoke(controller,new object[]{yaw,elevation}),Is.True);

        [Test]
        public void InspectionTurnIsEasedSynchronisedAndShort()
        {
            WithCamera((c,step)=>
            {
                Begin(c,90f,-8f);
                step(.016f);
                Assert.That(F(c,"_currentYaw"),Is.GreaterThan(0f).And.LessThan(2f),"Ease-in: the first frame moves only slightly.");
                float previous=F(c,"_currentYaw"),yawDone=-1,pitchDone=-1,t=.016f;
                for(int i=0;i<60;i++)
                {
                    step(.016f);t+=.016f;
                    float yaw=F(c,"_currentYaw"),pitch=F(c,"_currentPitch");
                    Assert.That(yaw,Is.GreaterThanOrEqualTo(previous-1e-4f),"No reversal or overshoot.");previous=yaw;
                    // Same relative progress on both axes: each reaches 99.99 % of its own sweep on the same frame.
                    if(yawDone<0&&Mathf.Abs(yaw-90f)/90f<1e-4f)yawDone=t;
                    if(pitchDone<0&&Mathf.Abs(pitch-8f)/8f<1e-4f)pitchDone=t;
                }
                Assert.That(yawDone,Is.GreaterThan(.3f).And.LessThanOrEqualTo(.52f),"Turn lasts about 0.35-0.5 s.");
                Assert.That(pitchDone,Is.EqualTo(yawDone).Within(.001f),"Both axes share one progress parameter (no dog-leg).");
                Assert.That(P(c,"IsViewTransitioning"),Is.False);
            });
        }

        [Test]
        public void ChangingSidesTurnsThroughForwardNeverBehindTheSeat()
        {
            WithCamera((c,step)=>
            {
                Begin(c,-120.6f,-25f);for(int i=0;i<40;i++)step(.016f);
                Assert.That(F(c,"_currentYaw"),Is.EqualTo(-120.6f).Within(.01f));
                Begin(c,122.9f,-25f);
                float smallest=float.MaxValue;
                for(int i=0;i<40;i++)
                {
                    step(.016f);float yaw=F(c,"_currentYaw");
                    Assert.That(Mathf.Abs(yaw),Is.LessThanOrEqualTo(160f));smallest=Mathf.Min(smallest,Mathf.Abs(yaw));
                }
                Assert.That(smallest,Is.LessThan(10f),"The path crosses forward.");
                Assert.That(F(c,"_currentYaw"),Is.EqualTo(122.9f).Within(.01f));
            });
        }

        [Test]
        public void ForwardReturnIsEasedButModeChangeSnaps()
        {
            WithCamera((c,step)=>
            {
                Begin(c,90f,-8f);for(int i=0;i<40;i++)step(.016f);
                Camera.GetMethod("ResetView").Invoke(c,null);
                Assert.That(P(c,"IsPanelInspectionActive"),Is.False);
                step(.016f);
                Assert.That(F(c,"_currentYaw"),Is.GreaterThan(80f),"FORWARD is not an instant cut.");
                Assert.That(P(c,"IsViewTransitioning"),Is.True);
                for(int i=0;i<40;i++)step(.016f);
                Assert.That(F(c,"_currentYaw"),Is.EqualTo(0f).Within(.001f));
                Assert.That(F(c,"_currentPitch"),Is.EqualTo(0f).Within(.001f));
                Assert.That(P(c,"IsViewTransitioning"),Is.False);
                Begin(c,90f,-8f);for(int i=0;i<40;i++)step(.016f);
                Camera.GetMethod("ResetViewImmediate").Invoke(c,null);
                Assert.That(F(c,"_currentYaw"),Is.Zero);
            });
        }

        [Test]
        public void ReleasedFreeLookKeepsItsOwnReturnTimeAfterForward()
        {
            WithCamera((c,step)=>
            {
                var input=Camera.GetMethod("ProcessLookInput",Any);
                Camera.GetMethod("ResetView").Invoke(c,null);
                input.Invoke(c,new object[]{true,new Vector2(30,0),false,.016f});
                Assert.That(F(c,"_currentYaw"),Is.EqualTo(90f).Within(.001f));
                input.Invoke(c,new object[]{false,Vector2.zero,false,.4f});
                Assert.That(F(c,"_currentYaw"),Is.EqualTo(45f).Within(.01f),"Half of the 0.8 s free-look return, not the FORWARD time.");
            });
        }

        [TestCase(0f,.35f)] [TestCase(90f,.425f)] [TestCase(243f,.5f)]
        public void InspectionTurnDurationScalesWithSweep(float sweep,float expected)=>
            Assert.That((float)Static(Camera,"InspectionTurnSeconds",sweep,.35f,.5f),Is.EqualTo(expected).Within(.001f));

        [TestCase(45f,false,false)] [TestCase(55f,false,true)] [TestCase(45f,true,true)] [TestCase(35f,true,false)]
        [TestCase(-55f,false,true)] [TestCase(350f,false,false)] [TestCase(float.NaN,true,false)]
        public void OffAxisDeclutterUsesHysteresis(float yaw,bool was,bool expected)=>
            Assert.That((bool)Static(T("FaaInspectionDeclutter"),"OffAxis",yaw,was),Is.EqualTo(expected));

        [TestCase(4f)] [TestCase(8f)] [TestCase(10.5f)] [TestCase(14f)]
        public void FramingKeepsPanelsBetweenBankArcAndHeadingScale(float half)
        {
            var band=(Vector2)Static(T("FaaInspectionFraming"),"Band",60f);
            var aim=(Vector2)Static(T("FaaInspectionFraming"),"Aim",new Vector2(80f,100f),new Vector2(-20f-half,-20f+half),60f);
            Assert.That(aim.x,Is.EqualTo(90f).Within(.001f));
            Assert.That(-20f+half-aim.y,Is.LessThanOrEqualTo(band.y+.001f),"Top below the bank-arc ends.");
            Assert.That(-20f-half-aim.y,Is.GreaterThanOrEqualTo(band.x-.001f),"Bottom above the heading scale.");
        }

        [Test]
        public void FramingBandMatchesZonePlanAndTallPanelsKeepTheirHeader()
        {
            Type framing=T("FaaInspectionFraming");
            var band=(Vector2)Static(framing,"Band",60f);
            Assert.That(band.y,Is.LessThan((float)Static(framing,"ScreenElevation",235f,60f)),"Clear of the bank arc (y 60-235).");
            Assert.That(band.x,Is.GreaterThan((float)Static(framing,"ScreenElevation",790f,60f)),"Clear of the heading scale (y 790-850).");
            var aim=(Vector2)Static(framing,"Aim",new Vector2(-130f,-110f),new Vector2(-60f,-10f),60f);
            Assert.That(-10f-aim.y,Is.EqualTo(band.y).Within(.001f),"A panel taller than the band is top-aligned.");
        }

        [TestCase(90f,-60f)] [TestCase(-90f,40f)] [TestCase(86.3f,-35.7f)] [TestCase(20f,-8f)]
        public void UtilityPanelsStayInComfortableElevationAndClearTheCone(float yaw,float elevation)
        {
            Type layout=T("FaaPeripheralPanelLayout");
            var entry=Activator.CreateInstance(T("FaaSpatialLayoutEntry"));
            entry.GetType().GetField("id").SetValue(entry,"settings");entry.GetType().GetField("yaw").SetValue(entry,yaw);
            entry.GetType().GetField("elevation").SetValue(entry,elevation);entry.GetType().GetField("distance").SetValue(entry,1.5f);
            entry.GetType().GetField("scale").SetValue(entry,1f);
            Static(layout,"ProtectUtility",entry,.58f,.56f,Mathf.Sign(yaw));
            float e=F(entry,"elevation"),y=F(entry,"yaw");
            Assert.That(e,Is.InRange(-30f,15f));
            float radius=(float)Static(layout,"AngularRadius",.58f,.56f,1f,1.5f);
            float angle=Mathf.Acos(Mathf.Cos(y*Mathf.Deg2Rad)*Mathf.Cos(e*Mathf.Deg2Rad))*Mathf.Rad2Deg;
            Assert.That(angle-radius,Is.GreaterThanOrEqualTo(62.99f));
            Assert.That((bool)Static(layout,"ProtectUtility",entry,.58f,.56f,Mathf.Sign(yaw)),Is.False,"Idempotent, never gaze-chasing.");
        }

        [Test]
        public void DigitalGatesNeverDoubleDimAndPreviewStaysFull()
        {
            Type w=T("FaaSpatialWorkspace");
            foreach(var name in new[]{"Airspeed Indicator","Altimeter","Torque Panel","NR/ENG Ind","VSI","Bank Scale","Glidescope","Localizer Position Ind.","FAA Flight Mode Annunciator"})
                Assert.That((bool)Static(w,"GateDimsItself",name),Is.True,name);
            Assert.That((bool)Static(w,"GateDimsItself","Attitude"),Is.False);
            Assert.That((bool)Static(w,"GateDeclutteredInUnusualAttitude","Glidescope"),Is.True);
            Assert.That((bool)Static(w,"GateDeclutteredInUnusualAttitude","Airspeed Indicator"),Is.False);
            Assert.That((float)Static(w,"DigitalGateFactor",true,false,false,false,.16f),Is.EqualTo(1f));
            Assert.That((float)Static(w,"DigitalGateFactor",false,false,false,false,.16f),Is.EqualTo(.16f).Within(1e-5f));
            Assert.That((float)Static(w,"DigitalGateFactor",false,true,false,false,.16f),Is.EqualTo(1f));
            Assert.That((float)Static(w,"DigitalGateFactor",true,false,true,true,1f),Is.EqualTo(0f));
        }

        [Test]
        public void ClassicDimsItselfAndOnlyReceivesThePreviewAndDeclutterHooks()
        {
            Type w=T("FaaSpatialWorkspace");
            float aware=(float)T("FaaHudInspection").GetField("AwarenessIntensity").GetValue(null);
            Assert.That(aware,Is.EqualTo(.65f).Within(1e-5f),"Awareness readouts stay at 0.65 while a panel is inspected.");
            Assert.That((string)Static(w,"ClassicPreviewId",true,"torque"),Is.EqualTo("torque"));
            Assert.That((string)Static(w,"ClassicPreviewId",false,"torque"),Is.Null,"Digital previews never hold a Classic dial.");
            // The workspace no longer adds per-instrument Classic CanvasGroups (Classic applies the awareness policy per element).
            Assert.That(w.GetMethod("ClassicInstrumentAlpha",Any),Is.Null);
            var classic=T("FaaClassicAnalogHud");
            Assert.That(classic.GetProperty("PreviewInstrumentId"),Is.Not.Null);Assert.That(classic.GetProperty("DeclutterDeviation"),Is.Not.Null);
        }

        [Test]
        public void DigitalAwarenessGateKeepsTheAwarenessFloorWithoutDoubleDimming()
        {
            Type w=T("FaaSpatialWorkspace");
            Assert.That((bool)Static(w,"GateIsAwareness","Heading Panel"),Is.True);
            Assert.That((bool)Static(w,"GateIsAwareness","Attitude"),Is.False);
            // GateFactor(dimsItself, awareness, previewed, unusualDeclutter, unusualAttitude, forward, awareness intensity)
            Assert.That((float)Static(w,"GateFactor",false,true,false,false,false,.16f,.65f),Is.EqualTo(.65f).Within(1e-5f));
            Assert.That((float)Static(w,"GateFactor",false,false,false,false,false,.16f,.65f),Is.EqualTo(.16f).Within(1e-5f));
            Assert.That((float)Static(w,"GateFactor",true,true,false,false,false,.16f,.65f),Is.EqualTo(1f),"Self-dimming layers are never dimmed again.");
            Assert.That((float)Static(w,"GateFactor",false,true,true,false,false,.16f,.65f),Is.EqualTo(1f),"The previewed instrument stays at full intensity.");
            Assert.That((float)Static(w,"GateFactor",false,true,false,false,false,1f,1f),Is.EqualTo(1f),"Looking forward, nothing is dimmed.");
            // Smoothed awareness intensity never drops below the forward intensity during the fade.
            var inspection=T("FaaHudInspection");
            Assert.That((float)Static(inspection,"AwarenessFor",.3f),Is.GreaterThanOrEqualTo(.3f));
        }

        private static Rect Ref(float x,float y,float w,float h)=>new Rect(x,1080f-y-h,w,h);
        private static System.Collections.Generic.List<Rect> ZonePlan()
        {
            var list=new System.Collections.Generic.List<Rect>();
            Static(T("FaaInspectionFraming"),"DefaultKeepOuts",1920f,1080f,list);return list;
        }
        private static object Fit(float yawHalf,float elevationHalf,float textDegrees,bool zoom,System.Collections.Generic.List<Rect> keepOuts,float elevation=-8f)=>
            Static(T("FaaInspectionFraming"),"Fit",new Vector2(90f-yawHalf,90f+yawHalf),new Vector2(elevation-elevationHalf,elevation+elevationHalf),
                textDegrees,60f,1920f,1080f,keepOuts,zoom);
        private static T2 Field<T2>(object o,string name)=>(T2)o.GetType().GetField(name).GetValue(o);

        [Test]
        public void InspectionFramingClearsAwarenessReadoutsAndZoomsUntilTextIsLegible()
        {
            var keepOuts=ZonePlan();
            // Settings at 1.5 m: 0.58 m x 0.56 m (640 x 620 units), smallest text 16 units = 0.0145 m.
            float yawHalf=Mathf.Atan(.29f/1.5f)*Mathf.Rad2Deg,elevationHalf=Mathf.Atan(.28f/1.5f)*Mathf.Rad2Deg;
            float text=Mathf.Atan(.0145f/1.5f)*Mathf.Rad2Deg;
            var frame=Fit(yawHalf,elevationHalf,text,true,keepOuts);
            Assert.That(Field<bool>(frame,"Clear"),Is.True,"The panel clears IAS/ALT/TQ/NR/VS/FMA/heading/chrome.");
            var rect=Field<Rect>(frame,"ScreenRect");
            foreach(var k in keepOuts)Assert.That(rect.Overlaps(k),Is.False,"Overlaps a protected area "+k);
            float fov=Field<float>(frame,"VerticalFov");
            Assert.That(fov,Is.LessThan(60f),"Desktop inspection magnifies the panel.");
            Assert.That(fov,Is.GreaterThanOrEqualTo((float)T("FaaInspectionFraming").GetField("MinZoomFov").GetValue(null)));
            Assert.That(Field<float>(frame,"TextReferencePixels"),Is.GreaterThanOrEqualTo(15f-.01f),"The smallest text reaches FaaHudStyle.MinLabel.");
            // The camera aims so the panel centre lands where the rectangle is.
            var aim=Field<Vector2>(frame,"Aim");
            Assert.That(Mathf.Abs(aim.x-90f),Is.LessThan(10f));
        }

        [Test]
        public void InspectionFramingNeverWidensAndKeepsXrAtOneToOne()
        {
            var keepOuts=ZonePlan();
            var big=Fit(4f,3f,1.5f,true,keepOuts);
            Assert.That(Field<float>(big,"VerticalFov"),Is.EqualTo(60f).Within(.01f),"Text already legible: no zoom.");
            var xr=Fit(10f,10f,.3f,false,keepOuts);
            Assert.That(Field<float>(xr,"VerticalFov"),Is.EqualTo(60f).Within(.01f),"No zoom when zoom is not allowed (XR).");
            Assert.That(Field<bool>(xr,"Clear"),Is.True);
            // Text far too small for the clear area: zoom only as far as the panel still fits.
            var tiny=Fit(10f,10f,.05f,true,keepOuts);
            Assert.That(Field<bool>(tiny,"Clear"),Is.True,"Legibility never pushes the panel onto the HUD.");
            float fovText=(float)Static(T("FaaInspectionFraming"),"FovForText",.05f,15f,60f);
            Assert.That(Field<float>(tiny,"VerticalFov"),Is.GreaterThan(fovText));
        }

        [Test]
        public void NeighbourPanelsDimOrHideWhileAnotherPanelIsInspected()
        {
            Type w=T("FaaSpatialWorkspace");
            Assert.That((float)Static(w,"PanelFocusTarget",false,false,false),Is.EqualTo(1f));
            Assert.That((float)Static(w,"PanelFocusTarget",true,true,true),Is.EqualTo(1f),"The inspected panel stays at full intensity.");
            Assert.That((float)Static(w,"PanelFocusTarget",true,false,false),Is.EqualTo(.15f).Within(1e-5f));
            Assert.That((float)Static(w,"PanelFocusTarget",true,false,true),Is.EqualTo(0f));
            var keepOuts=ZonePlan();var inspected=Ref(660,120,600,620);
            Assert.That((bool)Static(w,"NeighbourShouldHide",true,Ref(1450,560,300,300),1920f,1080f,keepOuts,true,inspected),Is.True,"Meets the NR/VS columns.");
            Assert.That((bool)Static(w,"NeighbourShouldHide",true,Ref(1700,300,400,300),1920f,1080f,keepOuts,true,inspected),Is.True,"Clipped by the screen edge.");
            Assert.That((bool)Static(w,"NeighbourShouldHide",true,Ref(1200,150,200,200),1920f,1080f,keepOuts,true,inspected),Is.True,"Interleaves with the inspected panel.");
            Assert.That((bool)Static(w,"NeighbourShouldHide",true,Ref(1650,100,200,250),1920f,1080f,keepOuts,true,inspected),Is.False,"Clear neighbours stay faintly visible.");
            Assert.That((bool)Static(w,"NeighbourShouldHide",false,default(Rect),1920f,1080f,keepOuts,true,inspected),Is.True,"Behind the camera.");
        }

        private static object Sample(Action<object> set)
        {
            object s=Activator.CreateInstance(T("FaaAnalogFlightSample"));
            s.GetType().GetField("Fresh").SetValue(s,true);set(s);return s;
        }
        private static void Set(object s,string field,object value)=>s.GetType().GetField(field).SetValue(s,value);
        private static bool Alert(object sample)=>(bool)T("FaaSpatialWorkspace").GetMethod("SampleHasExceedance").Invoke(null,new[]{sample});

        [Test]
        public void ExceedanceAlertsCancelDeclutterOnlyForFreshValidData()
        {
            Assert.That(Alert(Sample(s=>{Set(s,"Engine1Valid",true);Set(s,"Engine1Torque",105f);})),Is.True);
            Assert.That(Alert(Sample(s=>{Set(s,"Engine1Valid",true);Set(s,"Engine1Torque",95f);})),Is.False);
            Assert.That(Alert(Sample(s=>{Set(s,"Engine1Valid",true);Set(s,"Engine1Torque",115f);Set(s,"Fresh",false);})),Is.False,"Stale data never alerts.");
            Assert.That(Alert(Sample(s=>{Set(s,"Engine1Valid",true);Set(s,"Engine1Torque",400f);})),Is.False,"Implausible data is invalid, not an alert.");
            Assert.That(Alert(Sample(s=>{Set(s,"RpmValid",true);Set(s,"Rpm",90f);Set(s,"SpeedValid",true);Set(s,"Speed",60f);})),Is.True);
            Assert.That(Alert(Sample(s=>{Set(s,"RpmValid",true);Set(s,"Rpm",90f);})),Is.False,"Low NR alerts only when airborne.");
            Assert.That(Alert(Sample(s=>{Set(s,"SpeedValid",true);Set(s,"Speed",160f);})),Is.True);
        }

        [Test]
        public void SizePresetsStayAboveTheLegibilityFloor()
        {
            var presets=(float[])T("FaaSpatialWorkspace").GetField("SizePresets").GetValue(null);
            Assert.That(presets.Length,Is.EqualTo(3));
            foreach(float p in presets)Assert.That(p,Is.GreaterThanOrEqualTo(.75f));
        }

        [Test]
        public void PanelTextFloorsTargetSizeAndSelectionState()
        {
            Type ui=T("FaaWorkspaceUi");
            var host=new GameObject("Panel UI fixture",typeof(RectTransform),typeof(Canvas));
            try
            {
                host.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
                var text=(TMP_Text)Static(ui,"Text","Tiny",host.transform,"x",0f,0f,100f,20f,8f,false);
                // 16-unit floor: at the inspection zoom (about 0.95 reference px per unit) it renders at or above MinLabel (15).
                Assert.That(text.fontSize,Is.GreaterThanOrEqualTo(16f));
                UnityEngine.Events.UnityAction none=()=>{};
                var button=(Button)Static(ui,"Button","B",host.transform,"FULL 100%",0f,0f,146f,46f,none);
                var caption=button.GetComponentInChildren<TMP_Text>();
                Assert.That(caption.enableAutoSizing,Is.True);
                Assert.That(caption.fontSizeMin,Is.GreaterThanOrEqualTo(16f));
                Assert.That(caption.fontSizeMax,Is.LessThanOrEqualTo(22f));
                Assert.That((float)ui.GetField("MinTargetHeight").GetValue(null),Is.GreaterThanOrEqualTo(46f));
                Static(ui,"SetSelected",button,true);Assert.That((bool)Static(ui,"IsSelected",button),Is.True);
                Static(ui,"SetSelected",button,false);Assert.That((bool)Static(ui,"IsSelected",button),Is.False);
            }
            finally{UnityEngine.Object.DestroyImmediate(host);}
        }

        [Test]
        public void ClassicOnlyOptionsAreDisabledAndLabelledWhileDigitalIsActive()
        {
            Type w=T("FaaSpatialWorkspace");Type version=T("FaaSymbologyVersion");
            Assert.That((bool)Static(w,"ClassicOptionsEnabled",Enum.Parse(version,"Digital")),Is.False);
            Assert.That((bool)Static(w,"ClassicOptionsEnabled",Enum.Parse(version,"ClassicAnalog")),Is.True);
            Assert.That((string)Static(w,"ClassicOptionsHeading",false),Is.EqualTo("CLASSIC ONLY"));
            Assert.That((string)Static(w,"ClassicOptionsHeading",true),Is.EqualTo("CLASSIC OPTIONS"));
        }

        [Test]
        public void GuardedActionNeedsTwoPressesWithinTheConfirmWindow()
        {
            Type ui=T("FaaWorkspaceUi");Type guardType=T("FaaWorkspaceUi+Guard");
            var host=new GameObject("Guard fixture",typeof(RectTransform),typeof(Canvas));
            try
            {
                int executed=0;UnityEngine.Events.UnityAction action=()=>executed++;
                var guard=Static(ui,"GuardedButton","Stop",host.transform,"STOP DATA","CONFIRM STOP DATA",0f,0f,260f,46f,action);
                var press=guardType.GetMethod("Press");var tick=guardType.GetMethod("Tick");
                var button=(Button)guardType.GetField("Button").GetValue(guard);
                Assert.That((bool)press.Invoke(guard,new object[]{0f}),Is.False);Assert.That(executed,Is.Zero);
                Assert.That(button.GetComponentInChildren<TMP_Text>().text,Is.EqualTo("CONFIRM STOP DATA"));
                Assert.That((bool)press.Invoke(guard,new object[]{1f}),Is.True);Assert.That(executed,Is.EqualTo(1));
                Assert.That(button.GetComponentInChildren<TMP_Text>().text,Is.EqualTo("STOP DATA"));
                press.Invoke(guard,new object[]{10f});tick.Invoke(guard,new object[]{14f});
                Assert.That((bool)guardType.GetProperty("Armed").GetValue(guard),Is.False,"The arm times out.");
                press.Invoke(guard,new object[]{20f});
                Assert.That((bool)press.Invoke(guard,new object[]{24f}),Is.False,"A late second press re-arms instead of executing.");
                Assert.That(executed,Is.EqualTo(1));
            }
            finally{UnityEngine.Object.DestroyImmediate(host);}
        }
    }
}
