using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Customization.Tests
{
    public sealed class FaaRadarFullscreenPresentationTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type PanelType=>Type.GetType("FAA.Customization.FaaSpatialRadarPanel, Assembly-CSharp",true);
        private static Type DisplayType=>Type.GetType("TrafficRadar.TrafficRadarDisplay, TrafficRadar",true);
        private static Type OverlayType=>Type.GetType("FAA.Customization.FaaRadarControlsOverlay, Assembly-CSharp",true);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject EyeHost,CanvasHost,CockpitHost,OverlayHost;
            public readonly Camera Eye;public readonly Canvas Canvas;public readonly RectTransform Root,Scope;public RectTransform Strip;
            public readonly Component Display,Overlay;public readonly object Panel;private readonly RenderTexture texture;private readonly Action<bool> listener;
            public Fixture()
            {
                EyeHost=new GameObject("Real fullscreen consumer eye",typeof(Camera));Eye=EyeHost.GetComponent<Camera>();Eye.fieldOfView=60;texture=new RenderTexture(1600,900,0);Eye.targetTexture=texture;
                CockpitHost=new GameObject("Real fullscreen consumer cockpit");
                CanvasHost=new GameObject("Real fullscreen consumer canvas",typeof(RectTransform),typeof(Canvas),typeof(TrackedDeviceGraphicRaycaster));Root=(RectTransform)CanvasHost.transform;Root.sizeDelta=new Vector2(1920,1080);Canvas=CanvasHost.GetComponent<Canvas>();Canvas.renderMode=RenderMode.WorldSpace;Canvas.worldCamera=Eye;
                Scope=Rect("Original traffic system",Root,new Vector2(320,320));Scope.anchoredPosition=new Vector2(500,-260);
                RectTransform displayRect=Rect("Original traffic display",Scope,new Vector2(320,320));displayRect.gameObject.SetActive(false);Display=displayRect.gameObject.AddComponent(DisplayType);Set(Display,"rectTransform",displayRect);Set(Display,"preferXPlaneTrafficTexture",true);Set(Display,"animateFullscreenTransition",false);
                OverlayHost=new GameObject("Inactive real overlay consumer");OverlayHost.SetActive(false);Overlay=OverlayHost.AddComponent(OverlayType);Set(Overlay,"_trafficRoot",Scope);Set(Overlay,"_trafficDisplay",Display);Set(Overlay,"enableWeatherControls",false);
                Strip=EnsureStrip();
                Panel=Activator.CreateInstance(PanelType,new object[]{null,"traffic",Canvas,Scope,95f,false,.42f});Call("SetSpatial",true);Call("Apply",CockpitHost.transform,Eye,false);UnityEngine.Canvas.ForceUpdateCanvases();
                listener=_=>Strip=EnsureStrip();DisplayType.GetEvent("FullscreenChanged").AddEventHandler(Display,listener);
            }
            private static RectTransform Rect(string name,Transform parent,Vector2 size)
            {var go=new GameObject(name,typeof(RectTransform));var r=(RectTransform)go.transform;r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.sizeDelta=size;return r;}
            private static void Set(object component,string field,object value)=>component.GetType().GetField(field,Any).SetValue(component,value);
            private RectTransform EnsureStrip()=>(RectTransform)OverlayType.GetMethod("EnsureStrip",Any).Invoke(Overlay,new object[]{Scope,"TrafficControlStrip",new Vector2(796,110)});
            public object Call(string name,params object[] args)=>PanelType.GetMethod(name,Any).Invoke(Panel,args);
            public void Inspect(){Assert.That((bool)Call("ApplyDesktopInspection",Eye,new Rect(80,90,450,700),false,null),Is.True);UnityEngine.Canvas.ForceUpdateCanvases();}
            public void Full(bool value)=>DisplayType.GetMethod("SetFullscreen").Invoke(Display,new object[]{value,false});
            public int StripCount()=>Canvas.GetComponentsInChildren<Transform>(true).Count(x=>x.name=="TrafficControlStrip");
            public void AssertInside()
            {
                Inspect();object[] args={Eye,default(Rect)};Assert.That((bool)PanelType.GetMethod("TryScreenRect",Any).Invoke(Panel,args),Is.True);Rect bounds=(Rect)args[1];
                var corners=new Vector3[4];foreach(var element in new[]{Scope,Strip}){element.GetWorldCorners(corners);foreach(var corner in corners){Vector3 p=Eye.WorldToScreenPoint(corner);Assert.That(p.x,Is.InRange(bounds.xMin-1,bounds.xMax+1));Assert.That(p.y,Is.InRange(bounds.yMin-1,bounds.yMax+1));}}
            }
            public void Dispose()
            {
                DisplayType.GetEvent("FullscreenChanged").RemoveEventHandler(Display,listener);Full(false);Call("ApplyAuthoredPose",CockpitHost.transform,Eye,false);Call("SetSpatial",false);
                var frame=PanelType.GetField("desktopRoot",Any).GetValue(Panel) as RectTransform;if(frame!=null)UnityEngine.Object.DestroyImmediate(frame.gameObject);
                Eye.targetTexture=null;UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(EyeHost);UnityEngine.Object.DestroyImmediate(CockpitHost);UnityEngine.Object.DestroyImmediate(OverlayHost);UnityEngine.Object.DestroyImmediate(texture);
            }
        }
        [Test]
        public void RealFullMapKeepsTheOriginalRootAndStripInsideTheDesktopLayoutHost()
        {
            using(var f=new Fixture())
            {
                f.Inspect();Transform host=f.Scope.parent;RectTransform originalStrip=f.Strip;
                f.Full(true);Assert.That(f.Scope.parent,Is.EqualTo(host),"Full-map cannot escape the temporary presentation frame.");
                Assert.That(f.Strip,Is.SameAs(originalStrip),"The actual EnsureStrip consumer must find its original sibling, not create a duplicate drawer.");Assert.That(f.StripCount(),Is.EqualTo(1));f.AssertInside();
            }
        }
        [Test]
        public void RealFullMapExitWhileInspectedRestoresCompactLayoutInsideTheSameFrame()
        {
            using(var f=new Fixture())
            {
                Vector2 compact=f.Scope.sizeDelta;f.Inspect();Transform host=f.Scope.parent;
                f.Full(true);f.AssertInside();f.Full(false);Assert.That(f.Scope.parent,Is.EqualTo(host));Assert.That(f.Scope.sizeDelta,Is.EqualTo(compact));Assert.That(f.StripCount(),Is.EqualTo(1));f.AssertInside();
            }
        }
        [Test]
        public void ClosingInspectionBeforeRestoreHudDoesNotRetainAnInactiveTemporaryParent()
        {
            using(var f=new Fixture())
            {
                Vector2 compact=f.Scope.sizeDelta;f.Inspect();f.Full(true);f.Call("ApplyAuthoredPose",f.CockpitHost.transform,f.Eye,false);
                Assert.That(f.Scope.parent,Is.EqualTo(f.Root));f.Full(false);Assert.That((bool)DisplayType.GetProperty("IsFullscreen").GetValue(f.Display),Is.False);
                Assert.That(f.Scope.parent,Is.EqualTo(f.Root));Assert.That(f.Scope.sizeDelta,Is.EqualTo(compact));Assert.That(f.StripCount(),Is.EqualTo(1));
            }
        }
        [Test]
        public void LateNativeRestoreAndRepeatedFullMapCyclesDoNotDuplicateOrLoseControls()
        {
            using(var f=new Fixture())
            {
                for(int cycle=0;cycle<3;cycle++){f.Inspect();f.Full(true);f.AssertInside();f.Full(false);Assert.That(f.StripCount(),Is.EqualTo(1));}
                f.Inspect();f.Full(true);Assert.That((bool)f.Call("ApplyDesktopInspection",f.Eye,new Rect(80,90,450,700),true,null),Is.False);
                f.Full(false);Assert.That(f.Scope.parent,Is.EqualTo(f.Root));Assert.That(f.StripCount(),Is.EqualTo(1));
            }
        }
    }
}
