using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Use the actual Commands row action and its CanvasGroup opacity owner, not the color-manager shortcut.</summary>
    public sealed class FaaClassicCommandOpacityTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type T(string name)=>Type.GetType(name+", Assembly-CSharp",true);
        private static Type C(string name)=>T("FAA.Customization."+name);
        private static void Property(object target,string name,object value)=>target.GetType().GetProperty(name,Any).SetValue(target,value);
        private static void Field(object target,string name,object value)=>target.GetType().GetField(name,Any).SetValue(target,value);
        private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Any).Invoke(target,args);

        private sealed class Fixture:IDisposable
        {
            public readonly GameObject CanvasHost,CameraHost,Source,WorkspaceHost,ManagerHost,OpacityHost,MenuHost;
            public readonly Component Hud,Workspace,Manager,Opacity,Menu;
            public readonly Action<int> Select;
            public readonly object Row;
            public readonly string Layout;
            public Fixture(string layout,bool pilot)
            {
                Layout=layout;
                C("FaaHudInspection").GetMethod("ResetImmediate").Invoke(null,null);
                C("FaaClassicClock").GetField("Override").SetValue(null,100f);
                CameraHost=new GameObject("Commands opacity eye",typeof(Camera));
                CanvasHost=new GameObject("Commands opacity source canvas",typeof(RectTransform),typeof(Canvas));
                var canvas=CanvasHost.GetComponent<Canvas>();canvas.renderMode=layout=="Desktop"?RenderMode.ScreenSpaceOverlay:RenderMode.WorldSpace;
                canvas.worldCamera=CameraHost.GetComponent<Camera>();((RectTransform)CanvasHost.transform).sizeDelta=new Vector2(1920,1080);
                Source=new GameObject("Second Interation GUI",typeof(RectTransform));Source.transform.SetParent(CanvasHost.transform,false);
                var hudGo=new GameObject("FAA Classic Analog Symbology",typeof(RectTransform));Hud=hudGo.AddComponent(C("FaaClassicAnalogHud"));
                Call(Hud,"Build",canvas,Source.transform);
                WorkspaceHost=new GameObject("Inactive Commands opacity workspace");WorkspaceHost.SetActive(false);
                Workspace=WorkspaceHost.AddComponent(C("FaaSpatialWorkspace"));Property(Workspace,"PersistChanges",false);
                Property(Workspace,"ClassicHud",Hud);Property(Workspace,"CurrentSymbology",Enum.Parse(C("FaaSymbologyVersion"),"ClassicAnalog"));
                Property(Workspace,"UsePilotSymbologyColor",pilot);
                ManagerHost=new GameObject("Inactive Commands palette manager");ManagerHost.SetActive(false);Manager=ManagerHost.AddComponent(C("SymbologyColorManager"));
                Call(Manager,"Initialize");Call(Manager,"ApplyColorImmediate",new Color(.2f,.75f,1f,1f));Field(Workspace,"colorManager",Manager);
                OpacityHost=new GameObject("Actual Commands opacity owner");OpacityHost.SetActive(false);Opacity=OpacityHost.AddComponent(C("FaaHudOpacityController"));
                Field(Opacity,"persistPreference",false);
                Transform controlled=layout=="Inherited"||layout=="IgnoreBoundary"?CanvasHost.transform:layout=="Self"?Hud.transform:Source.transform;
                Field(Opacity,"hudRoots",new List<Transform>{controlled});OpacityHost.SetActive(true);Call(Opacity,"RefreshTargets");
                if(layout=="IgnoreBoundary")
                {
                    var boundary=new GameObject("Explicit parent-group boundary",typeof(RectTransform),typeof(CanvasGroup));
                    boundary.transform.SetParent(CanvasHost.transform,false);boundary.GetComponent<CanvasGroup>().ignoreParentGroups=true;
                    Hud.transform.SetParent(boundary.transform,false);
                }
                Field(Workspace,"pilotOpacityController",Opacity);
                MenuHost=new GameObject("Inactive actual Commands builder");MenuHost.SetActive(false);
                Menu=MenuHost.AddComponent(T("VoiceControl.UI.UIToolkitRadialMenuAdvanced"));
                var rows=(IList)Call(Menu,"BuildCommandRows",null,false);
                Row=rows.Cast<object>().Single(row=>(string)row.GetType().GetField("Label").GetValue(row)=="BRIGHTNESS %");
                Assert.That(Menu.GetType().GetField("_hudOpacity",Any).GetValue(Menu),Is.SameAs(Opacity),"Test must drive the controller used by the actual Commands row.");
                Select=(Action<int>)Row.GetType().GetField("Select").GetValue(Row);
                Canvas.ForceUpdateCanvases();
            }
            public void Apply(float intensity)
            {
                C("FaaClassicAnalogHud").GetField("ForwardIntensityOverride").SetValue(null,intensity);
                Call(Workspace,"ApplySymbologyPresentation");
                var sample=Activator.CreateInstance(C("FaaAnalogFlightSample"));
                foreach(string field in new[]{"Fresh","AttitudeValid","SpeedValid","AltitudeValid","VerticalSpeedValid","TorqueValid","RpmValid","Engine1Valid","Engine2Valid","CouplingValid"})Field(sample,field,true);
                Field(sample,"Speed",100f);Field(sample,"Altitude",8200f);Field(sample,"Torque",30f);Field(sample,"Engine1Torque",30f);Field(sample,"Engine2Torque",30f);Field(sample,"Rpm",100f);
                Field(sample,"Coupling","CPL");Field(sample,"RollMode","HDG");Field(sample,"PitchMode","ALT");Field(sample,"RollArmed","");Field(sample,"PitchArmed","");
                Call(Hud,"Present",sample,0f,true);Call(Hud,"ApplyLayout");
            }
            public float AncestorAlpha(Transform target)
            {
                float alpha=1f;
                for(var cursor=target;cursor!=null;cursor=cursor.parent)
                {
                    var group=cursor.GetComponent<CanvasGroup>();if(group==null||!group.isActiveAndEnabled)continue;
                    alpha*=group.alpha;if(group.ignoreParentGroups)break;
                }
                return alpha;
            }
            public void Dispose()
            {
                C("FaaClassicAnalogHud").GetField("ForwardIntensityOverride").SetValue(null,float.NaN);
                C("FaaClassicClock").GetField("Override").SetValue(null,float.NaN);
                Property(Workspace,"ClassicHud",null);
                UnityEngine.Object.DestroyImmediate(MenuHost);UnityEngine.Object.DestroyImmediate(WorkspaceHost);
                UnityEngine.Object.DestroyImmediate(ManagerHost);UnityEngine.Object.DestroyImmediate(OpacityHost);
                if(Hud!=null)UnityEngine.Object.DestroyImmediate(Hud.gameObject);
                UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(CameraHost);
            }
        }

        [TestCase("Desktop",false)]
        [TestCase("Desktop",true)]
        [TestCase("World",false)]
        [TestCase("World",true)]
        [TestCase("Inherited",false)]
        [TestCase("Inherited",true)]
        [TestCase("IgnoreBoundary",false)]
        [TestCase("Self",false)]
        public void ActualBrightnessCommandDimsClassicExactlyOnceAcrossCanvasOwnership(string layout,bool pilotPalette)
        {
            using(var f=new Fixture(layout,pilotPalette))
            {
                int[] presets={40,60,80,100};
                for(int segment=0;segment<presets.Length;segment++)
                {
                    f.Select(segment);float opacity=presets[segment]/100f;
                    Assert.That((int)f.Row.GetType().GetMethod("CurrentState").Invoke(f.Row,null),Is.EqualTo(segment));
                    Assert.That((float)f.Opacity.GetType().GetProperty("Opacity").GetValue(f.Opacity),Is.EqualTo(opacity).Within(.001f));
                    Assert.That((float)f.Manager.GetType().GetProperty("CurrentOpacity").GetValue(f.Manager),Is.EqualTo(1f),"Commands opacity does not rewrite the palette/color alpha.");
                    foreach(float forward in new[]{1f,.16f})for(int refresh=0;refresh<3;refresh++)
                    {
                        f.Apply(forward);Canvas.ForceUpdateCanvases();float aware=forward==1f?1f:.65f;
                        float inherited=f.AncestorAlpha(f.Hud.transform);
                        Assert.That(inherited,Is.EqualTo(opacity).Within(.001f),"Native inheritance and detached desktop presentation must not apply pilot opacity twice.");
                        foreach(var text in f.Hud.GetComponentsInChildren<TMP_Text>(true).Where(t=>t.name=="Live Value"||t.name=="Roll mode"))
                            Assert.That(text.color.a*f.AncestorAlpha(text.transform),Is.EqualTo(opacity*aware).Within(.002f),text.name+" effective readout/FMA opacity");
                        var gauges=f.Hud.GetComponentsInChildren(C("FaaClassicGaugeGraphic"),true);
                        Assert.That(gauges.Length,Is.EqualTo(4));
                        foreach(Component gauge in gauges)
                        {
                            var graphic=(Graphic)gauge;
                            Assert.That(graphic.canvasRenderer.GetInheritedAlpha(),Is.EqualTo(opacity).Within(.002f),"Unity renderer must inherit pilot opacity exactly once.");
                            Assert.That(graphic.color.a*f.AncestorAlpha(graphic.transform),Is.EqualTo(opacity*forward).Within(.002f),"Effective vector opacity");
                        }
                        var attitude=(Graphic)f.Hud.GetComponentInChildren(C("FaaClassicAttitudeGraphic"),true);
                        Assert.That(attitude.color.a*f.AncestorAlpha(attitude.transform),Is.EqualTo(opacity*forward).Within(.002f),"Effective inset opacity");
                        Color color=(Color)f.Hud.GetType().GetProperty("ReferenceGreen").GetValue(f.Hud);
                        Color expected=pilotPalette?new Color(.2f,.75f,1f,1f):(Color)C("FaaClassicAnalogHud").GetField("DefaultReferenceGreen").GetValue(null);
                        Assert.That(color.r,Is.EqualTo(expected.r).Within(.001f));Assert.That(color.g,Is.EqualTo(expected.g).Within(.001f));Assert.That(color.b,Is.EqualTo(expected.b).Within(.001f));
                    }
                }
            }
        }
    }
}
