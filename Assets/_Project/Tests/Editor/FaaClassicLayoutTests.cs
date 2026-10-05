using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Classic Analog human-factors regressions: zones, overlap, text floors, halo, needles vs windows, sky pointer, FMA logic, flags and declutter.</summary>
    public sealed class FaaClassicLayoutTests
    {
        private static Type T(string name)=>Type.GetType("FAA.Customization."+name+", Assembly-CSharp",true);
        private GameObject canvasGo,source;
        private Component hud;
        private Type hudType;

        [SetUp]
        public void SetUp()
        {
            canvasGo=new GameObject("Classic layout test canvas",typeof(RectTransform),typeof(Canvas));
            var canvas=canvasGo.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;
            ((RectTransform)canvasGo.transform).sizeDelta=new Vector2(1920,1080);
            source=new GameObject("Source root",typeof(RectTransform));source.transform.SetParent(canvasGo.transform,false);
            T("FaaHudInspection").GetMethod("ResetImmediate").Invoke(null,null);
            Clock(100f);
            hudType=T("FaaClassicAnalogHud");
            var go=new GameObject("FAA Classic Analog Symbology",typeof(RectTransform));
            hud=go.AddComponent(hudType);
            hudType.GetMethod("Build").Invoke(hud,new object[]{canvas,source.transform});
        }
        [TearDown]
        public void TearDown()
        {
            Clock(float.NaN);Intensity(float.NaN);
            if(hud!=null)UnityEngine.Object.DestroyImmediate(hud.gameObject);
            if(canvasGo!=null)UnityEngine.Object.DestroyImmediate(canvasGo);
        }

        private static void Clock(float t)=>T("FaaClassicClock").GetField("Override").SetValue(null,t);
        private static void Intensity(float k)=>T("FaaClassicAnalogHud").GetField("ForwardIntensityOverride").SetValue(null,k);
        private static void Set(object o,string field,object v)=>o.GetType().GetField(field).SetValue(o,v);
        private static object Sample(Action<object> edit=null)
        {
            var s=Activator.CreateInstance(T("FaaAnalogFlightSample"));
            foreach(var f in new[]{"Fresh","AttitudeValid","SpeedValid","AltitudeValid","VerticalSpeedValid","TorqueValid","RpmValid","SlipValid","Engine1Valid","Engine2Valid","LocValid","GsValid","CouplingValid"})Set(s,f,true);
            Set(s,"Pitch",2f);Set(s,"Roll",0f);Set(s,"Heading",90f);Set(s,"Speed",120f);Set(s,"Altitude",8182f);Set(s,"VerticalSpeed",-664f);
            Set(s,"Torque",90f);Set(s,"Engine1Torque",80f);Set(s,"Engine2Torque",90f);Set(s,"Rpm",100f);Set(s,"Localizer",.4f);Set(s,"Glideslope",-.6f);
            Set(s,"Coupling","CPL");Set(s,"RollMode","HDG");Set(s,"RollArmed","NAV ARM");Set(s,"PitchMode","ALT");Set(s,"PitchArmed","G/S ARM");
            edit?.Invoke(s);return s;
        }
        private void Present(object sample){hudType.GetMethod("Present").Invoke(hud,new[]{sample,(object)0f,(object)true});}
        private void Layout()=>hudType.GetMethod("ApplyLayout").Invoke(hud,null);
        private RectTransform RootOf(string id)=>((Dictionary<string,RectTransform>)hudType.GetField("InstrumentRoots").GetValue(hud))[id];
        private static U Prop<U>(object o,string name)=>(U)o.GetType().GetProperty(name).GetValue(o);
        private TMP_Text Text(string rootId,string name)=>RootOf(rootId).GetComponentsInChildren<TMP_Text>(true).First(t=>t.name==name);
        private TMP_Text HudText(string name)=>hud.GetComponentsInChildren<TMP_Text>(true).First(t=>t.name==name);
        private Component Graphic(string rootId,string type)=>RootOf(rootId).GetComponentInChildren(T(type),true);
        private static int Vertices(Component graphic)
        {
            var populate=graphic.GetType().GetMethod("OnPopulateMesh",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(VertexHelper)},null);
            using(var vh=new VertexHelper()){populate.Invoke(graphic,new object[]{vh});return vh.currentVertCount;}
        }
        private static List<Vector3> Mesh(Component graphic)
        {
            var populate=graphic.GetType().GetMethod("OnPopulateMesh",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(VertexHelper)},null);
            var list=new List<Vector3>();
            using(var vh=new VertexHelper())
            {
                populate.Invoke(graphic,new object[]{vh});UIVertex v=default;
                for(int i=0;i<vh.currentVertCount;i++){vh.PopulateUIVertex(ref v,i);list.Add(v.position);}
            }
            return list;
        }
        /// <summary>Canvas-space rectangle (canvas centred at the origin with unit scale, so world = reference units).</summary>
        private static Rect WorldRect(RectTransform r)
        {
            var c=new Vector3[4];r.GetWorldCorners(c);
            return Rect.MinMaxRect(c.Min(p=>p.x),c.Min(p=>p.y),c.Max(p=>p.x),c.Max(p=>p.y));
        }
        private static Rect TextRect(TMP_Text t)
        {
            t.ForceMeshUpdate(true,true);var b=t.textBounds;
            var c=new[]{new Vector3(b.min.x,b.min.y),new Vector3(b.max.x,b.min.y),new Vector3(b.min.x,b.max.y),new Vector3(b.max.x,b.max.y)}.Select(p=>t.rectTransform.TransformPoint(p)).ToArray();
            return Rect.MinMaxRect(c.Min(p=>p.x),c.Min(p=>p.y),c.Max(p=>p.x),c.Max(p=>p.y));
        }
        private List<TMP_Text> VisibleTexts()=>hud.GetComponentsInChildren<TMP_Text>(false)
            .Where(t=>t.enabled&&!string.IsNullOrEmpty(t.text)&&t.color.a>.05f).ToList();
        private void AssertNoTextOverlap(string state)
        {
            Assume.That(TMP_Settings.defaultFontAsset,Is.Not.Null,"TMP default font is required to measure glyph bounds.");
            var texts=VisibleTexts();var rects=texts.Select(TextRect).ToList();
            for(int i=0;i<texts.Count;i++)for(int j=i+1;j<texts.Count;j++)
            {
                Rect a=rects[i],b=rects[j];
                bool overlap=a.xMin+.5f<b.xMax&&b.xMin+.5f<a.xMax&&a.yMin+.5f<b.yMax&&b.yMin+.5f<a.yMax;
                Assert.That(overlap,Is.False,$"{state}: '{texts[i].name}' ({texts[i].text}) overlaps '{texts[j].name}' ({texts[j].text})");
            }
        }

        [Test]
        public void ActiveTextNeverOverlapsInNormalFailedStaleAndApOffStates()
        {
            Present(Sample());Layout();AssertNoTextOverlap("normal");
            Present(Sample(s=>{foreach(var f in new[]{"AttitudeValid","SpeedValid","AltitudeValid","VerticalSpeedValid","TorqueValid","RpmValid","LocValid","GsValid","Engine2Valid"})Set(s,f,false);}));
            Layout();AssertNoTextOverlap("failed sensors");
            Present(Sample(s=>Set(s,"Fresh",false)));Layout();AssertNoTextOverlap("stale feed");
            Present(Sample());Clock(101f);Present(Sample(s=>Set(s,"Coupling","AP OFF")));Layout();AssertNoTextOverlap("AP OFF");
            Present(Sample(s=>{Set(s,"Speed",300f);Set(s,"Torque",130f);Set(s,"Rpm",125f);Set(s,"VerticalSpeed",-3000f);}));Layout();AssertNoTextOverlap("off scale");
            // Every attitude within the ball: labelled rungs never collide with each other or with the W/flags.
            foreach(float pitch in new[]{-24f,-13f,-7f,0f,8f,17f,24f})
            {Present(Sample(s=>{Set(s,"Pitch",pitch);Set(s,"Roll",pitch*2f);}));Layout();AssertNoTextOverlap("pitch "+pitch);}
        }

        [Test]
        public void TextMeetsFaaSizeFloorsAndCarriesAWorkingHalo()
        {
            Present(Sample(s=>{Set(s,"Engine1Torque",70f);}));Layout();
            Assume.That(TMP_Settings.defaultFontAsset,Is.Not.Null);
            var secondary=new[]{"Scale ","VSI ","Pitch ","Units","Signal","Value","Source detail"};
            foreach(var t in VisibleTexts())
            {
                t.ForceMeshUpdate(true,true);
                float size=t.fontSize*t.rectTransform.lossyScale.y/canvasGo.transform.lossyScale.y;
                // Scale numerals, units, captions and the VS readout at the Secondary floor; window values at the Data floor; the rest at MinLabel.
                float floor=t.name=="Live Value"?24f:secondary.Any(p=>t.name.StartsWith(p,StringComparison.Ordinal))?18f:15f;
                Assert.That(size,Is.GreaterThanOrEqualTo(floor-.01f),$"{t.name} '{t.text}' renders at {size:0.0} reference units");
                Assert.That(t.fontSharedMaterial!=null&&t.fontSharedMaterial.IsKeywordEnabled("UNDERLAY_ON"),Is.True,$"{t.name} has no halo");
                if(!t.name.EndsWith(" prefix",StringComparison.Ordinal))Assert.That(t.color.a,Is.GreaterThanOrEqualTo(.85f-.001f),$"{t.name} buys contrast with transparency");
            }
            Assert.That(Text("vertical-speed","VSI 1").fontSize,Is.GreaterThanOrEqualTo(18f),"VSI numerals at the Secondary floor");
            Assert.That(Text("vertical-speed","Value").fontSize,Is.GreaterThanOrEqualTo(24f),"VS readout at the Data floor");
        }

        [Test]
        public void ShrunkModulesKeepTextAtTheRenderedFloor()
        {
            foreach(var id in new[]{"airspeed","torque","vertical-speed","attitude","heading"})RootOf(id).localScale=Vector3.one*.6f;
            Present(Sample());Layout();Present(Sample());
            foreach(var t in VisibleTexts())
            {
                float size=(t.enableAutoSizing?Mathf.Max(t.fontSize,t.fontSizeMin):t.fontSize)*t.rectTransform.lossyScale.y/canvasGo.transform.lossyScale.y;
                Assert.That(size,Is.GreaterThanOrEqualTo(15f-.01f),$"{t.name} '{t.text}' renders at {size:0.0} reference units after shrinking its module");
            }
            foreach(var id in new[]{"airspeed","torque","vertical-speed","attitude","heading"})RootOf(id).localScale=Vector3.one;
            Layout();
            Assert.That(Text("airspeed","Units").fontSize,Is.EqualTo(18f).Within(.01f),"Restored modules return to their nominal size");
        }

        [Test]
        public void LayoutKeepsZonesConcentricRollScaleAndBasicT()
        {
            Present(Sample());Layout();
            Rect fma=WorldRect(RootOf("heading"));
            Assert.That(540f-fma.yMax,Is.GreaterThanOrEqualTo(13.5f),"Mode row top stays in Z1");
            Assert.That(540f-fma.yMin,Is.LessThanOrEqualTo(80.5f),"Mode row bottom stays in Z1");
            Assert.That(fma.xMin,Is.GreaterThanOrEqualTo(650f-960f-.5f));Assert.That(fma.xMax,Is.LessThanOrEqualTo(1270f-960f+.5f));
            Vector3 att=RootOf("attitude").position;
            Assert.That(Vector2.Distance(att,Vector2.zero),Is.LessThan(.5f),"Attitude reference at the screen centre");
            Assert.That(Vector3.Distance(RootOf("bank").position,att),Is.LessThan(.01f),"Roll scale is concentric with the attitude");
            Assert.That(RootOf("airspeed").position.y,Is.EqualTo(att.y).Within(.5f),"Airspeed on the attitude reference");
            Assert.That(RootOf("altitude").position.y,Is.EqualTo(att.y).Within(.5f),"Altitude on the attitude reference");
            Assert.That(RootOf("airspeed").position.x+RootOf("altitude").position.x,Is.EqualTo(2*att.x).Within(.5f),"Symmetric T");
            // The shared heading slot (Z5, x 700-1220, y 787-853 from the top) stays free of Classic dials.
            Rect slot=Rect.MinMaxRect(700-960,540-853,1220-960,540-787);
            foreach(var id in new[]{"airspeed","altitude","torque","nr","glideslope","vertical-speed","localizer"})
                Assert.That(WorldRect(RootOf(id)).Overlaps(slot),Is.False,id+" intrudes on the heading slot");
            foreach(var pair in (Dictionary<string,RectTransform>)hudType.GetField("InstrumentRoots").GetValue(hud))
            {
                Rect r=WorldRect(pair.Value);
                Assert.That(540f-r.yMin,Is.LessThanOrEqualTo(990f),pair.Key+" enters the bottom chrome bar");
                if(pair.Key!="heading")Assert.That(540f-r.yMax,Is.GreaterThanOrEqualTo(90f),pair.Key+" enters the FMA row");
            }
        }

        [Test]
        public void DialNumeralsStayUprightAndAltitudeWindowShowsFullFeet()
        {
            Present(Sample());Layout();
            foreach(var id in new[]{"airspeed","altitude","torque","nr"})
                foreach(var t in RootOf(id).GetComponentsInChildren<TMP_Text>(true).Where(t=>t.name.StartsWith("Scale ",StringComparison.Ordinal)))
                    Assert.That(Quaternion.Angle(t.rectTransform.rotation,canvasGo.transform.rotation),Is.LessThan(.01f),id+" "+t.name);
            Assert.That(Text("altitude","Live Value").text,Is.EqualTo("8,182"));
            Assert.That(hud.GetComponentsInChildren<Transform>(true).Any(t=>t.name=="Thousands"),Is.False);
            Assert.That(Text("airspeed","Units").text,Is.EqualTo("KT"));Assert.That(Text("altitude","Units").text,Is.EqualTo("FT"));
        }

        [Test]
        public void NeedlesNeverEnterTheDigitalWindows()
        {
            var gaugeType=T("FaaClassicGaugeGraphic");var kinds=gaugeType.GetNestedType("Gauge");
            foreach(var name in new[]{"Airspeed","Altitude","Torque","RotorRpm"})
            {
                var go=new GameObject("Needle sweep "+name,typeof(RectTransform));
                try
                {
                    var g=go.AddComponent(gaugeType);var kind=Enum.Parse(kinds,name);
                    gaugeType.GetMethod("Configure").Invoke(g,new[]{kind,(object)Color.green});
                    Rect w=(Rect)gaugeType.GetMethod("WindowRect").Invoke(null,new[]{kind});
                    Rect inner=new Rect(w.x+6,w.y+6,w.width-12,w.height-12);
                    float max=name=="Airspeed"?250:name=="Altitude"?1000:125;
                    for(float v=0;v<=max;v+=max/250f)
                    {
                        gaugeType.GetMethod("Present").Invoke(g,new object[]{v,true,"",Color.green});
                        foreach(var p in Mesh(g))Assert.That(inner.Contains(p),Is.False,$"{name} needle at {v} strokes through the window");
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(go);}
            }
        }

        [Test]
        public void BigDialNeedlesNeverCoverTheNumeralsTheyPointAt()
        {
            Assume.That(TMP_Settings.defaultFontAsset,Is.Not.Null,"TMP default font is required to measure glyph quads.");
            var gaugeType=T("FaaClassicGaugeGraphic");var kinds=gaugeType.GetNestedType("Gauge");
            float shaft=(float)gaugeType.GetField("BigShaftEnd").GetValue(null),tip=(float)gaugeType.GetField("BigTipBase").GetValue(null);
            foreach(var name in new[]{"Airspeed","Altitude"})
            {
                var go=new GameObject("Numeral band "+name,typeof(RectTransform));
                try
                {
                    var g=go.AddComponent(gaugeType);var kind=Enum.Parse(kinds,name);
                    gaugeType.GetMethod("Configure").Invoke(g,new[]{kind,(object)Color.green});
                    gaugeType.GetMethod("Present").Invoke(g,new object[]{10f,true,"",Color.green});
                    // Every numeral glyph quad (halo padding included) lies inside the empty band between the shaft end and the tip.
                    foreach(var t in go.GetComponentsInChildren<TMP_Text>().Where(t=>t.name.StartsWith("Scale ",StringComparison.Ordinal)))
                    {
                        t.ForceMeshUpdate(true,true);var info=t.textInfo;
                        for(int i=0;i<info.characterCount;i++)
                        {
                            var ch=info.characterInfo[i];if(!ch.isVisible)continue;
                            Vector2 a=go.transform.InverseTransformPoint(t.rectTransform.TransformPoint(ch.bottomLeft)),b=go.transform.InverseTransformPoint(t.rectTransform.TransformPoint(ch.topRight));
                            Rect r=Rect.MinMaxRect(Mathf.Min(a.x,b.x),Mathf.Min(a.y,b.y),Mathf.Max(a.x,b.x),Mathf.Max(a.y,b.y));
                            Vector2 nearest=new Vector2(Mathf.Clamp(0,r.xMin,r.xMax),Mathf.Clamp(0,r.yMin,r.yMax));
                            float far=Mathf.Max(new Vector2(r.xMin,r.yMin).magnitude,new Vector2(r.xMax,r.yMin).magnitude,new Vector2(r.xMin,r.yMax).magnitude,new Vector2(r.xMax,r.yMax).magnitude);
                            Assert.That(nearest.magnitude,Is.GreaterThan(shaft+2f),$"{name} numeral '{t.text}' reaches into the needle shaft");
                            Assert.That(far,Is.LessThan(tip),$"{name} numeral '{t.text}' reaches under the pointer tip");
                        }
                    }
                    // The needle draws nothing in that band at any reading (normal range, so no exceedance box is added).
                    float max=name=="Airspeed"?150f:1000f;
                    gaugeType.GetMethod("Present").Invoke(g,new object[]{0f,false,"",Color.green});int stationary=Mesh(g).Count;
                    for(float v=0;v<=max;v+=max/180f)
                    {
                        gaugeType.GetMethod("Present").Invoke(g,new object[]{v,true,"",Color.green});
                        foreach(var p in Mesh(g).Skip(stationary))
                        {
                            float r=new Vector2(p.x,p.y).magnitude;
                            Assert.That(r<=shaft+2.5f||r>=tip-.5f,Is.True,$"{name} needle at {v} draws at radius {r:0.0}, inside the numeral band");
                        }
                    }
                }
                finally{UnityEngine.Object.DestroyImmediate(go);}
            }
        }

        [Test]
        public void RollScaleUsesTheDigitalSkyPointerAtOneToOne()
        {
            var bankType=T("FaaClassicBankGraphic");
            Assert.That((float[])bankType.GetField("TickAngles").GetValue(null),Is.EqualTo(new[]{10f,20f,30f,45f,60f}));
            float bearing=(float)bankType.GetMethod("PointerBearing").Invoke(null,new object[]{30f});
            Assert.That(bearing,Is.EqualTo(-30f).Within(.001f),"1:1 scale, counter-clockwise for a right bank (sky pointer)");
            var bank=Graphic("bank","FaaClassicBankGraphic");
            bankType.GetMethod("Present").Invoke(bank,new object[]{0f,0f,true,false,Color.green});var level=Mesh(bank);
            bankType.GetMethod("Present").Invoke(bank,new object[]{30f,0f,true,false,Color.green});var right=Mesh(bank);
            Assert.That(right.Count,Is.EqualTo(level.Count));
            // Only the pointer moves: its vertices are the tail of the mesh.
            var pointer=right.Skip(right.Count-3).ToList();
            Assert.That(pointer.Average(p=>p.x),Is.LessThan(-20f),"Right bank moves the sky pointer to the left, like the horizon's high side");
        }

        [Test]
        public void ModeRowShowsNothingWithGuidanceOffAndArmedOnlyWhenDistinct()
        {
            Present(Sample());
            // Same format as the Digital row: the bold mode alone, its axis prefix as a separate small quiet label outside the change box.
            Assert.That(Text("heading","Roll mode").text,Is.EqualTo("HDG"));Assert.That(Text("heading","Pitch mode").text,Is.EqualTo("ALT"));
            Assert.That(Text("heading","Roll mode").fontStyle&FontStyles.Bold,Is.EqualTo(FontStyles.Bold),"Active modes are bold, as on Digital");
            var prefix=Text("heading","Roll prefix");
            Assert.That(prefix.gameObject.activeSelf,Is.True);Assert.That(prefix.text,Is.EqualTo("R:"));Assert.That(Text("heading","Pitch prefix").text,Is.EqualTo("P:"));
            Assert.That(prefix.color.a,Is.LessThan(Text("heading","Roll mode").color.a),"Prefix is quieter than the mode");
            Assert.That(TextRect(prefix).xMax,Is.LessThan(TextRect(Text("heading","Roll mode")).xMin),"Prefix sits left of the mode");
            Assert.That(Text("heading","Lateral armed").text,Is.EqualTo("NAV ARM"));Assert.That(Text("heading","Coupling").text,Is.EqualTo("CPL"));
            Assert.That(Text("heading","Collective mode").gameObject.activeSelf,Is.False,"No collective source: column hidden");
            Assert.That(Text("heading","Collective prefix").gameObject.activeSelf,Is.False,"No collective source: no prefix either");
            Color armed=Text("heading","Lateral armed").color;Assert.That(armed.b,Is.GreaterThan(armed.r),"Armed modes are cyan");
            Present(Sample(s=>{Set(s,"RollMode","NAV");}));
            Assert.That(Text("heading","Lateral armed").text,Is.Empty,"NAV ARM is not shown under an active NAV");
            Clock(101f);Present(Sample(s=>Set(s,"Coupling","AP OFF")));
            foreach(var n in new[]{"Roll mode","Pitch mode","Lateral armed","Vertical armed"})Assert.That(Text("heading",n).text,Is.Empty,n+" must blank with AP/FD off");
            Assert.That(Text("heading","Roll prefix").gameObject.activeSelf,Is.False,"No prefix without a mode");
            Assert.That(Text("heading","Coupling").text,Is.EqualTo("AP OFF"));
        }

        [Test]
        public void ApOffFlashesThenSteadiesThenClearsAndModeChangesAreBoxedForTenSeconds()
        {
            var boxes=RootOf("heading").GetComponentInChildren(T("FaaClassicBoxGraphic"),true);
            Func<int,bool> on=i=>(bool)boxes.GetType().GetMethod("IsOn").Invoke(boxes,new object[]{i});
            Clock(100f);Present(Sample());
            Assert.That(on(1),Is.True,"New lateral mode boxed");
            Clock(109f);Present(Sample());Assert.That(on(1),Is.True);
            Clock(110.5f);Present(Sample());Assert.That(on(1),Is.False,"Box removed after 10 s");
            Clock(120f);Present(Sample(s=>Set(s,"Coupling","AP OFF")));
            Assert.That(Text("heading","Coupling").text,Is.EqualTo("AP OFF"));Assert.That(on(3),Is.True);
            Clock(120.35f);Present(Sample(s=>Set(s,"Coupling","AP OFF")));
            Assert.That(Text("heading","Coupling").color.a,Is.LessThan(.05f),"Flashing during the first 5 s");
            Clock(126.35f);Present(Sample(s=>Set(s,"Coupling","AP OFF")));
            Assert.That(Text("heading","Coupling").color.a,Is.GreaterThan(.5f),"Steady after 5 s");
            Clock(130.5f);Present(Sample(s=>Set(s,"Coupling","AP OFF")));
            Assert.That(Text("heading","Coupling").text,Is.Empty,"Removed after 10 s");Assert.That(on(3),Is.False);
        }

        [Test]
        public void DeviationScalesAppearOnlyWithGuidance()
        {
            var locG=Graphic("localizer","FaaClassicDeviationGraphic");var gsG=Graphic("glideslope","FaaClassicDeviationGraphic");
            Present(Sample(s=>{Set(s,"LocValid",false);Set(s,"GsValid",false);Set(s,"RollArmed","");Set(s,"PitchArmed","");}));
            Assert.That(Vertices(locG),Is.Zero,"No LOC clutter without guidance");Assert.That(Vertices(gsG),Is.Zero,"No G/S clutter without guidance");
            Assert.That(((Behaviour)locG).enabled,Is.False);Assert.That(Text("localizer","Signal").enabled,Is.False);
            Present(Sample(s=>{Set(s,"LocValid",false);Set(s,"GsValid",false);}));
            Assert.That(Text("localizer","Value").text,Is.EqualTo("LOC"),"Expected (armed) but lost: boxed flag");
            Assert.That(Text("glideslope","Value").text,Is.EqualTo("G/S"));Assert.That(Vertices(locG),Is.GreaterThan(0));
            Present(Sample());Assert.That(Text("localizer","Value").text,Is.Empty);Assert.That(Text("localizer","Signal").text,Is.EqualTo("LOC"));
        }

        [Test]
        public void LimitCodingIsAmberThenRedWithDoubleBoxFlashThenSteady()
        {
            var tq=(Component)Graphic("torque","FaaClassicGaugeGraphic");
            Clock(100f);Present(Sample());int normal=Vertices(tq);
            Assert.That(Prop<object>(tq,"Exceedance").ToString(),Is.EqualTo("Normal"));
            Present(Sample(s=>Set(s,"Torque",105f)));
            Assert.That(Prop<object>(tq,"Exceedance").ToString(),Is.EqualTo("Caution"));
            Color amber=(Color)T("FaaHudStyle").GetField("Amber").GetValue(null);
            Assert.That(Text("torque","Live Value").color.r,Is.EqualTo(amber.r).Within(.01f));Assert.That(Text("torque","Live Value").color.g,Is.EqualTo(amber.g).Within(.01f));
            Present(Sample(s=>Set(s,"Torque",115f)));
            Assert.That(Prop<object>(tq,"Exceedance").ToString(),Is.EqualTo("Warning"));Assert.That(Vertices(tq),Is.GreaterThan(normal),"Exceedance adds the second box");
            Clock(100.35f);Present(Sample(s=>Set(s,"Torque",115f)));Assert.That(Text("torque","Live Value").color.a,Is.LessThan(.5f),"Flashing");
            Clock(106f);Present(Sample(s=>Set(s,"Torque",115f)));Assert.That(Text("torque","Live Value").color.a,Is.GreaterThan(.9f),"Steady after 5 s");
            Present(Sample(s=>Set(s,"Torque",109.5f)));Assert.That(Prop<object>(tq,"Exceedance").ToString(),Is.EqualTo("Warning"),"Hysteresis near the red line");
            Present(Sample(s=>Set(s,"Torque",107f)));Assert.That(Prop<object>(tq,"Exceedance").ToString(),Is.EqualTo("Caution"));
            var nr=(Component)Graphic("nr","FaaClassicGaugeGraphic");
            Present(Sample(s=>{Set(s,"Rpm",80f);Set(s,"SpeedValid",false);}));
            Assert.That(Prop<object>(nr,"Exceedance").ToString(),Is.EqualTo("Normal"),"No low-NR warning on the ground");
            Present(Sample(s=>{Set(s,"Rpm",80f);Set(s,"Speed",60f);}));
            Assert.That(Prop<object>(nr,"Exceedance").ToString(),Is.EqualTo("Warning"),"Low NR in flight");
        }

        [Test]
        public void FailedAndStaleDataAreFlaggedNeverShownAsValues()
        {
            Present(Sample(s=>{Set(s,"SpeedValid",false);Set(s,"AttitudeValid",false);}));
            Assert.That(Text("airspeed","Live Value").text,Is.EqualTo("IAS"));Assert.That(Text("airspeed","Units").text,Is.Empty);
            Assert.That(RootOf("airspeed").GetComponentsInChildren<TMP_Text>(false).Any(t=>t.name.StartsWith("Scale ",StringComparison.Ordinal)),Is.False,"Numerals removed with the needle");
            Assert.That(Text("attitude","Attitude source").text,Is.EqualTo("ATT"));
            Present(Sample(s=>Set(s,"Fresh",false)));
            Assert.That(HudText("Data validity").text,Is.EqualTo("NO FLIGHT DATA"));
            Assert.That(Text("airspeed","Live Value").text,Is.Empty,"One global flag replaces the per-instrument flags");
            Assert.That(Text("attitude","Attitude source").text,Is.Empty);
            Assert.That(Text("vertical-speed","Value").text,Is.EqualTo("NO DATA"));
            Assert.That(Text("heading","Coupling").text,Is.EqualTo("FMA --"));
            Present(Sample());Assert.That(HudText("Data validity").text,Is.Empty);
        }

        [Test]
        public void UnusualAttitudeDeclutterUsesHysteresis()
        {
            Func<bool> unusual=()=>Prop<bool>(hud,"UnusualAttitude");
            Present(Sample(s=>Set(s,"Roll",65f)));Assert.That(unusual(),Is.True);
            Assert.That(Prop<bool>(Graphic("localizer","FaaClassicDeviationGraphic"),"Shown"),Is.False,"LOC decluttered");
            Assert.That(Text("heading","Lateral armed").text,Is.Empty,"Armed modes decluttered");
            Present(Sample(s=>Set(s,"Roll",58f)));Assert.That(unusual(),Is.True,"Hysteresis holds");
            Present(Sample(s=>Set(s,"Roll",50f)));Assert.That(unusual(),Is.False);
            Present(Sample(s=>Set(s,"Pitch",26f)));Assert.That(unusual(),Is.False);
            Present(Sample(s=>Set(s,"Pitch",31f)));Assert.That(unusual(),Is.True);
            Present(Sample(s=>Set(s,"Pitch",24f)));Assert.That(unusual(),Is.False);
        }

        [Test]
        public void PitchScaleHasDistinctHorizonAndLabelsOffTheBoresight()
        {
            var att=T("FaaClassicAttitudeGraphic");float F(string n)=>(float)att.GetField(n).GetValue(null);
            float radius=F("Radius"),scale=F("UnitsPerDegree");
            Assert.That(radius/scale,Is.GreaterThanOrEqualTo(25f-.001f),"At least +25/-15 deg visible with the aircraft symbol on the horizon (AC 23.1311-1C)");
            Assert.That(2f*radius/scale,Is.LessThanOrEqualTo(50f+.001f),"At most 50 deg in total");
            foreach(float pitch in new[]{0f,-7f,13f,-22f,24f})
            {
                Present(Sample(s=>Set(s,"Pitch",pitch)));
                var labels=RootOf("attitude").GetComponentsInChildren<TMP_Text>(false).Where(t=>t.name.StartsWith("Pitch ",StringComparison.Ordinal)).ToList();
                Assert.That(labels.Count,Is.GreaterThanOrEqualTo(2),$"At pitch {pitch} at least one labelled rung stays in the ball");
                foreach(var t in labels)
                {
                    Assert.That(Mathf.Abs(t.rectTransform.anchoredPosition.x),Is.GreaterThan(F("LongRungOuter")),"Labels sit at the rung ends");
                    Assert.That(t.text,Is.Not.EqualTo("0"),"The horizon carries no label");
                    Assert.That(int.Parse(t.text)%10,Is.Zero,"Labels only every 10 deg");
                    Assert.That(t.rectTransform.anchoredPosition.magnitude+12f,Is.LessThanOrEqualTo(radius),$"Label {t.text} at pitch {pitch} leaves the ball");
                }
            }
        }

        [Test]
        public void AttitudeInsetReadsAsAnInstrumentWithTheDigitalWaterline()
        {
            var att=T("FaaClassicAttitudeGraphic");float F(string n)=>(float)att.GetField(n).GetValue(null);
            // Same gull-wing W as Digital (6 x 1 units), at least about 60 units wide, rungs clear of it.
            var shape=(Vector2[])att.GetField("WaterlineShape").GetValue(null);
            Assert.That(shape.Max(p=>p.x)-shape.Min(p=>p.x),Is.EqualTo(6f));Assert.That(shape.Min(p=>p.y),Is.EqualTo(-1f));
            Assert.That(6f*F("WaterlineUnit"),Is.GreaterThanOrEqualTo(60f));
            Assert.That(F("RungInner"),Is.GreaterThanOrEqualTo(3f*F("WaterlineUnit")+12f));
            // Sky/ground fill: a translucent graphic drawn behind the strokes, never outlined.
            var fill=RootOf("attitude").GetComponentInChildren(T("FaaClassicTintGraphic"),true);var strokes=Graphic("attitude","FaaClassicAttitudeGraphic");
            Assert.That(fill,Is.Not.Null);Assert.That(fill.transform.GetSiblingIndex(),Is.LessThan(strokes.transform.GetSiblingIndex()),"Fill drawn behind the strokes");
            Assert.That(fill.GetComponent<Outline>(),Is.Null,"No dark outline on a fill");
            Present(Sample(s=>{Set(s,"Pitch",0f);Set(s,"Roll",0f);}));
            var mesh=Mesh(fill);Assert.That(mesh.Count,Is.GreaterThan(6),"Sky and ground drawn");
            var colors=new List<Color>();
            using(var vh=new VertexHelper())
            {
                fill.GetType().GetMethod("OnPopulateMesh",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(VertexHelper)},null).Invoke(fill,new object[]{vh});
                UIVertex v=default;for(int i=0;i<vh.currentVertCount;i++){vh.PopulateUIVertex(ref v,i);colors.Add(v.color);}
            }
            Assert.That(colors.Any(c=>c.b>c.r)&&colors.Any(c=>c.r>c.b),Is.True,"Blue sky and brown ground");
            Assert.That(colors.Max(c=>c.a),Is.LessThanOrEqualTo(.5f),"Translucent: the outside view stays visible");
            Assert.That(mesh.Max(p=>new Vector2(p.x,p.y).magnitude),Is.LessThanOrEqualTo(F("Radius")+.01f),"Fill stays inside the ball");
            // Off scale: the horizon is held at the edge (dashed) so a sliver of sky remains.
            Present(Sample(s=>Set(s,"Pitch",-30f)));
            Assert.That(Prop<bool>(strokes,"HorizonOffScale"),Is.True);
            Assert.That(Prop<float>(fill,"BoundaryOffset"),Is.EqualTo(F("Radius")-F("OffScaleMargin")).Within(.01f),"Some sky stays visible");
            Present(Sample(s=>Set(s,"AttitudeValid",false)));
            Assert.That(Vertices(fill),Is.Zero,"No sky/ground without valid attitude");
        }

        [Test]
        public void LegacyPanelOpacityAndWholeHudDeclutterApplyToTheRoot()
        {
            hudType.GetMethod("SetVisible").Invoke(hud,new object[]{true});
            Present(Sample());Layout();
            var group=hud.GetComponent<CanvasGroup>();
            Assert.That(group.alpha,Is.EqualTo(1f).Within(.001f));
            hudType.GetProperty("PanelInspectionOpacity").SetValue(hud,.12f);Layout();
            Assert.That(group.alpha,Is.EqualTo(.12f).Within(.001f));
            hudType.GetProperty("PanelInspectionOpacity").SetValue(hud,1f);source.SetActive(false);Layout();
            Assert.That(group.alpha,Is.Zero,"Whole-HUD declutter hides Classic");
        }

        [Test]
        public void AwarenessReadoutsKeepTheFloorWhileInspectingAndTheRestDims()
        {
            hudType.GetMethod("SetVisible").Invoke(hud,new object[]{true});
            float aware=(float)hudType.GetMethod("AwarenessFor").Invoke(null,new object[]{.16f});
            Assert.That(aware,Is.EqualTo(.65f).Within(.001f));
            Intensity(.16f);Present(Sample());Layout();
            Assert.That(hud.GetComponent<CanvasGroup>().alpha,Is.EqualTo(1f).Within(.001f),"The root only carries visibility; dimming is per element");
            Assert.That(Prop<float>(hud,"ForwardIntensity"),Is.EqualTo(.16f).Within(.001f));Assert.That(Prop<float>(hud,"AwarenessIntensity"),Is.EqualTo(aware).Within(.001f));
            foreach(var id in new[]{"airspeed","altitude","torque","nr"})
            {
                Assert.That(Text(id,"Live Value").color.a,Is.EqualTo(aware).Within(.01f),id+" value keeps the awareness floor");
                Assert.That(Text(id,"Units").color.a,Is.EqualTo(aware).Within(.01f),id+" unit keeps the awareness floor");
                Assert.That(Text(id,"Scale 0").color.a,Is.EqualTo(.16f).Within(.01f),id+" scale numerals dim with the forward HUD");
            }
            Assert.That(Text("heading","Roll mode").color.a,Is.EqualTo(aware).Within(.01f),"FMA keeps the awareness floor");
            Assert.That(Text("vertical-speed","Value").color.a,Is.EqualTo(aware).Within(.01f),"VS readout keeps the awareness floor");
            Assert.That(Text("vertical-speed","VSI 1").color.a,Is.EqualTo(.16f).Within(.01f),"VSI scale dims");
            Assert.That(RootOf("attitude").GetComponentsInChildren<TMP_Text>(false).First(t=>t.name.StartsWith("Pitch ",StringComparison.Ordinal)).color.a,Is.EqualTo(.16f).Within(.01f),"Ladder dims");
            hudType.GetProperty("PreviewInstrumentId").SetValue(hud,"torque");Present(Sample());
            Assert.That(Text("torque","Scale 0").color.a,Is.EqualTo(1f).Within(.01f),"The previewed instrument stays at full intensity");
            hudType.GetProperty("PreviewInstrumentId").SetValue(hud,null);
            hudType.GetProperty("AwarenessFloor").SetValue(hud,.55f);Present(Sample());
            Assert.That(Text("airspeed","Live Value").color.a,Is.EqualTo(.55f).Within(.01f),"An explicit awareness floor replaces the shared one");
            hudType.GetProperty("AwarenessFloor").SetValue(hud,float.NaN);Intensity(float.NaN);Present(Sample());
            Assert.That(Text("airspeed","Scale 0").color.a,Is.EqualTo(1f).Within(.01f),"Forward view restores full intensity");
        }

        [Test]
        public void RollScaleEndsAndPointerAreProtectedAndTheWindowStaysBelowTheArcEnds()
        {
            Present(Sample(s=>Set(s,"Roll",30f)));Layout();
            var regionType=T("FaaHudKeepOutRegion");
            var regions=RootOf("bank").GetComponentsInChildren(regionType,true).ToDictionary(r=>Prop<string>(r,"Id"));
            foreach(var id in new[]{"classic-roll-end-left","classic-roll-end-right","classic-roll-index","classic-roll-pointer"})
            {
                Assert.That(regions.ContainsKey(id),Is.True,id);Assert.That(Prop<object>(regions[id],"Kind").ToString(),Is.EqualTo("Symbology"));
            }
            var pointer=(RectTransform)regions["classic-roll-pointer"].transform;
            Assert.That(pointer.gameObject.activeSelf,Is.True);Assert.That(pointer.localPosition.x,Is.LessThan(-50f),"Pointer region follows the sky pointer");
            Present(Sample(s=>Set(s,"AttitudeValid",false)));Assert.That(pointer.gameObject.activeSelf,Is.False,"No pointer, no region");
            float arc=(float)T("FaaClassicBankGraphic").GetField("Radius").GetValue(null);
            var window=hud.GetComponentsInChildren(regionType,true).First(r=>Prop<string>(r,"Id")=="classic-attitude-window");
            Rect w=WorldRect((RectTransform)window.transform);Vector3 centre=RootOf("attitude").position;
            Assert.That(w.yMax-centre.y,Is.LessThan(arc*Mathf.Cos(60f*Mathf.Deg2Rad)-1f),"Attitude window top below the roll-scale end marks");
        }

        [Test]
        public void LegacyReferenceGreenSelectsTheBrighterDefault()
        {
            var legacy=(Color)hudType.GetField("LegacyReferenceGreen").GetValue(null);var bright=(Color)hudType.GetField("DefaultReferenceGreen").GetValue(null);
            hudType.GetProperty("ReferenceGreen").SetValue(hud,legacy);
            Assert.That((Color)hudType.GetProperty("ReferenceGreen").GetValue(hud),Is.EqualTo(bright));
            float L(Color c){float f(float x)=>x<=.04045f?x/12.92f:Mathf.Pow((x+.055f)/1.055f,2.4f);return .2126f*f(c.r)+.7152f*f(c.g)+.0722f*f(c.b);}
            Assert.That(L(bright),Is.GreaterThan(L(legacy)+.2f));
            hudType.GetProperty("ReferenceGreen").SetValue(hud,Color.cyan);
            Assert.That((Color)hudType.GetProperty("ReferenceGreen").GetValue(hud),Is.EqualTo(Color.cyan),"Pilot palette colours pass through");
        }

        [Test]
        public void ControlDataFilterStaysInsideTheLatencyBudget()
        {
            var type=T("FaaAnalogAnimation");var a=Activator.CreateInstance(type);var step=type.GetMethod("Step");
            step.Invoke(a,new[]{Sample(s=>{Set(s,"Roll",0f);Set(s,"Torque",50f);}),(object)0f,false});
            step.Invoke(a,new[]{Sample(s=>{Set(s,"Roll",10f);Set(s,"Torque",60f);}),(object).04f,false});
            var d=type.GetProperty("Display").GetValue(a);
            Assert.That((float)d.GetType().GetField("Roll").GetValue(d),Is.GreaterThan(6.2f),"Roll reaches 63% within 40 ms");
            Assert.That((float)d.GetType().GetField("Torque").GetValue(d),Is.GreaterThan(56.2f),"Torque reaches 63% within 40 ms");
        }

        [Test]
        public void HeightAboveGroundIsReadOnlyWhenPublished()
        {
            var dataType=Type.GetType("AviationUI.AviationFlightData, Assembly-CSharp",true);var data=Activator.CreateInstance(dataType);
            var from=T("FaaAnalogFlightSample").GetMethod("From");
            var s=from.Invoke(null,new object[]{data,null,null,true});
            Assert.That((bool)s.GetType().GetField("HeightAboveGroundValid").GetValue(s),Is.False);
            dataType.GetField("altitudeAGL").SetValue(data,42f);dataType.GetField("altitudeAGLValid").SetValue(data,true);
            s=from.Invoke(null,new object[]{data,null,null,true});
            Assert.That((bool)s.GetType().GetField("HeightAboveGroundValid").GetValue(s),Is.True);
            Assert.That((float)s.GetType().GetField("HeightAboveGround").GetValue(s),Is.EqualTo(42f));
        }
    }
}
