using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public sealed class FaaRadarTemporalFitTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type PanelType=>Type.GetType("FAA.Customization.FaaSpatialRadarPanel, Assembly-CSharp",true);
        private static readonly Rect Requested=new Rect(560,240,800,600);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject CameraHost,CanvasHost,CockpitHost;
            public readonly Camera View;public readonly RenderTexture Texture;
            public readonly RectTransform CanvasRect,Radar,Drawer;public readonly object Panel;
            public Fixture()
            {
                CameraHost=new GameObject("Temporal inspection eye",typeof(Camera));View=CameraHost.GetComponent<Camera>();
                View.fieldOfView=60;View.nearClipPlane=.03f;View.transform.SetPositionAndRotation(new Vector3(10,3,-4),Quaternion.Euler(9,21,6));
                Texture=new RenderTexture(1920,1080,0);View.targetTexture=Texture;View.aspect=1920f/1080;
                CockpitHost=new GameObject("Temporal cockpit reference");CockpitHost.transform.SetPositionAndRotation(View.transform.position,Quaternion.identity);
                CanvasHost=new GameObject("Temporal radar composite",typeof(RectTransform),typeof(Canvas));CanvasRect=(RectTransform)CanvasHost.transform;CanvasRect.sizeDelta=new Vector2(1920,1080);
                var canvas=CanvasHost.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=View;
                Radar=Graphic("Scope",CanvasRect,Vector2.zero,new Vector2(320,320));
                Graphic("Header",CanvasRect,new Vector2(0,230),new Vector2(400,50));
                Drawer=Graphic("Optional drawer",CanvasRect,new Vector2(80,460),new Vector2(480,250));Drawer.gameObject.SetActive(false);
                Panel=Activator.CreateInstance(PanelType,new object[]{null,"traffic",canvas,Radar,95f,false,.42f});Call("SetSpatial",true);ApplyNormal();Canvas.ForceUpdateCanvases();
            }
            private static RectTransform Graphic(string name,Transform parent,Vector2 position,Vector2 size)
            {
                var go=new GameObject(name,typeof(RectTransform),typeof(Image));var r=(RectTransform)go.transform;r.SetParent(parent,false);
                r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=position;r.sizeDelta=size;return r;
            }
            public object Call(string name,params object[] args)=>PanelType.GetMethod(name,Any).Invoke(Panel,args);
            public void ApplyNormal()=>Call("Apply",CockpitHost.transform,View,false);
            public Rect Inspect(IReadOnlyList<Rect> obstacles,Rect? target=null)
            {
                Assert.That((bool)Call("ApplyDesktopInspection",View,target??Requested,false,obstacles),Is.True);
                object[] args={View,default(Rect)};Assert.That((bool)PanelType.GetMethod("TryScreenRect",Any).Invoke(Panel,args),Is.True);return (Rect)args[1];
            }
            public float[] SavedLayout()
            {
                object layout=PanelType.GetField("Layout",Any).GetValue(Panel);
                return Array.ConvertAll(new[]{"yaw","elevation","distance","scale"},n=>(float)layout.GetType().GetField(n,Any).GetValue(layout));
            }
            public void Dispose(){View.targetTexture=null;UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(CameraHost);UnityEngine.Object.DestroyImmediate(CockpitHost);UnityEngine.Object.DestroyImmediate(Texture);}
        }
        private static Rect Blocker(float jitter)=>new Rect(920+jitter,0,80,1080);
        private static void SamePresentation(Rect expected,Rect actual,float tolerance=.05f)
        {
            Assert.That(Vector2.Distance(actual.center,expected.center),Is.LessThan(tolerance),"A still-clear inspected panel must not jump to another equally close slot.");
            Assert.That(Vector2.Distance(actual.size,expected.size),Is.LessThan(tolerance),"Moving readout bounds must not make the panel breathe.");
        }
        private static void Clear(Rect panel,IReadOnlyList<Rect> obstacles)
        {
            Assert.That(panel.xMin,Is.GreaterThanOrEqualTo(0));Assert.That(panel.xMax,Is.LessThanOrEqualTo(1920));
            Assert.That(panel.yMin,Is.GreaterThanOrEqualTo(50));Assert.That(panel.yMax,Is.LessThanOrEqualTo(1000));
            foreach(Rect obstacle in obstacles)Assert.That(panel.Overlaps(obstacle),Is.False,"Inspection must not cover protected flight readouts.");
        }
        [Test]
        public void AlternatingSubpixelReadoutBoundsDoNotTeleportTheInspectedPanel()
        {
            using(var f=new Fixture())
            {
                Rect first=f.Inspect(new[]{Blocker(-.02f)});float[] layout=f.SavedLayout();
                for(int frame=0;frame<180;frame++){var obstacles=new[]{Blocker(frame%2==0?.02f:-.02f)};f.ApplyNormal();Rect actual=f.Inspect(obstacles);SamePresentation(first,actual);Clear(actual,obstacles);}
                CollectionAssert.AreEqual(layout,f.SavedLayout());
            }
        }
        [Test]
        public void MovingEyeAndBeforeRenderRefreshKeepOneStableScreenPresentation()
        {
            using(var f=new Fixture())
            {
                Rect first=f.Inspect(new[]{Blocker(-.02f)});
                for(int frame=0;frame<120;frame++)
                {
                    f.View.transform.SetPositionAndRotation(new Vector3(10+frame*.04f,3+Mathf.Sin(frame*.05f),-4-frame*.03f),Quaternion.Euler(9+Mathf.Sin(frame*.1f),21+frame*.1f,6));
                    var obstacles=new[]{Blocker(frame%2==0?.02f:-.02f)};
                    for(int refresh=0;refresh<2;refresh++){f.ApplyNormal();SamePresentation(first,f.Inspect(obstacles),.1f);}
                }
            }
        }
        [Test]
        public void SmallProtectedBoundsNoiseCannotRepeatedlyGrowAndShrinkThePanel()
        {
            using(var f=new Fixture())
            {
                var obstacles=new[]{new Rect(0,0,720,1080),new Rect(1200,0,720,1080)};Rect first=f.Inspect(obstacles);
                for(int frame=0;frame<120;frame++)
                {
                    float jitter=frame%2==0?.02f:-.02f;obstacles[0]=new Rect(0,0,720+jitter,1080);obstacles[1]=new Rect(1200-jitter,0,720+jitter,1080);
                    SamePresentation(first,f.Inspect(obstacles));Clear(f.Inspect(obstacles),obstacles);
                }
            }
        }
        [Test]
        public void RealNewObstructionReflowsAwayFromItsCurrentSlot()
        {
            using(var f=new Fixture())
            {
                Rect first=f.Inspect(new[]{Blocker(0)});var blockers=new[]{Blocker(0),new Rect(first.xMin-5,0,first.width+10,1080)};
                Rect moved=f.Inspect(blockers);Clear(moved,blockers);
                Assert.That(Vector2.Distance(first.center,moved.center),Is.GreaterThan(200),"A real occlusion cannot be ignored for temporal stability.");
            }
        }
        [Test]
        public void DrawerOpenAndCloseReflowTheEntireCompositeWithoutChangingSavedLayout()
        {
            using(var f=new Fixture())
            {
                float[] layout=f.SavedLayout();Rect compact=f.Inspect(Array.Empty<Rect>());
                f.Drawer.gameObject.SetActive(true);Rect expanded=f.Inspect(Array.Empty<Rect>());Assert.That(expanded.size,Is.Not.EqualTo(compact.size));
                var corners=new Vector3[4];f.Drawer.GetWorldCorners(corners);foreach(var corner in corners){Vector3 point=f.View.WorldToScreenPoint(corner);Assert.That(point.x,Is.InRange(expanded.xMin-.1f,expanded.xMax+.1f));Assert.That(point.y,Is.InRange(expanded.yMin-.1f,expanded.yMax+.1f));}
                f.Drawer.gameObject.SetActive(false);SamePresentation(compact,f.Inspect(Array.Empty<Rect>()));CollectionAssert.AreEqual(layout,f.SavedLayout());
            }
        }
        [Test]
        public void ExplicitNewTargetStillMovesTheInspection()
        {
            using(var f=new Fixture())
            {
                Rect first=f.Inspect(Array.Empty<Rect>());Rect moved=f.Inspect(Array.Empty<Rect>(),new Rect(100,100,500,500));
                Assert.That(Vector2.Distance(first.center,moved.center),Is.GreaterThan(200));
            }
        }
        [Test]
        public void ResizingTheViewportReflowsTheOldPresentationInsideTheNewScreen()
        {
            using(var f=new Fixture())
            {
                f.Inspect(new[]{Blocker(0)});
                var smaller=new RenderTexture(1280,720,0);
                try
                {
                    f.View.targetTexture=smaller;f.View.aspect=1280f/720;
                    Rect actual=f.Inspect(Array.Empty<Rect>());
                    Assert.That(actual.xMin,Is.GreaterThanOrEqualTo(0));Assert.That(actual.xMax,Is.LessThanOrEqualTo(1280));
                    Assert.That(actual.yMin,Is.GreaterThanOrEqualTo(35));Assert.That(actual.yMax,Is.LessThanOrEqualTo(660));
                }
                finally{f.View.targetTexture=f.Texture;UnityEngine.Object.DestroyImmediate(smaller);}
            }
        }

        [Test]
        public void NativeRequestDoesNotApplyOrMutateTheDesktopCachePose()
        {
            using(var f=new Fixture())
            {
                f.Inspect(new[]{Blocker(0)});f.ApplyNormal();Vector3 at=f.CanvasRect.position,scale=f.CanvasRect.localScale;Quaternion rotation=f.CanvasRect.rotation;
                Assert.That((bool)f.Call("ApplyDesktopInspection",f.View,Requested,true,new[]{Blocker(10)}),Is.False);
                Assert.That(f.CanvasRect.position,Is.EqualTo(at));Assert.That(f.CanvasRect.localScale,Is.EqualTo(scale));Assert.That(f.CanvasRect.rotation,Is.EqualTo(rotation));
            }
        }
    }
}
