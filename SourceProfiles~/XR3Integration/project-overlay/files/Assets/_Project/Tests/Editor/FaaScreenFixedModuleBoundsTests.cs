using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
namespace FAA.Customization.Tests
{
    public sealed class FaaScreenFixedModuleBoundsTests
    {
        private static Type TargetType=>Type.GetType("FAA.Customization.FaaNonConformalScaleTarget, Assembly-CSharp",true);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject CameraHost,CanvasHost;
            public readonly Camera View;
            public readonly Canvas Canvas;
            public readonly RectTransform Root,Module;
            public readonly RenderTexture Texture;
            public readonly object Target;
            public Fixture(RenderMode mode=RenderMode.ScreenSpaceCamera)
            {
                CameraHost=new GameObject("Bounds eye",typeof(Camera));View=CameraHost.GetComponent<Camera>();
                View.nearClipPlane=.03f;View.farClipPlane=1000;View.fieldOfView=60;
                Texture=new RenderTexture(1600,900,0);View.targetTexture=Texture;
                CanvasHost=new GameObject("Screen-fixed bounds fixture",typeof(RectTransform),typeof(Canvas));
                Canvas=CanvasHost.GetComponent<Canvas>();Canvas.renderMode=mode;Canvas.worldCamera=View;Canvas.planeDistance=1.2f;
                Root=(RectTransform)CanvasHost.transform;
                if(mode==RenderMode.WorldSpace){Root.sizeDelta=new Vector2(1600,900);Root.position=new Vector3(0,0,4);Root.localScale=Vector3.one*.002f;}
                var go=new GameObject("Selected dial",typeof(RectTransform),typeof(Image));Module=(RectTransform)go.transform;
                Module.SetParent(Root,false);Module.anchorMin=Module.anchorMax=Module.pivot=new Vector2(.5f,.5f);
                Module.anchoredPosition=new Vector2(-300,20);Module.sizeDelta=new Vector2(240,220);
                Canvas.ForceUpdateCanvases();
                Target=Activator.CreateInstance(TargetType,"airspeed","Airspeed",Module,1f,0f);
            }
            public bool Bounds(out Rect rect){object[] args={View,default(Rect)};bool result=(bool)TargetType.GetMethod("TryScreenBounds").Invoke(Target,args);rect=(Rect)args[1];return result;}
            public Rect Bounds(){Assert.That(Bounds(out var rect),Is.True);return rect;}
            public void Dispose(){View.targetTexture=null;UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(CameraHost);UnityEngine.Object.DestroyImmediate(Texture);}
        }
        private static void Same(Rect actual,Rect expected)
        {
            Assert.That(actual.xMin,Is.EqualTo(expected.xMin).Within(.1f));Assert.That(actual.yMin,Is.EqualTo(expected.yMin).Within(.1f));
            Assert.That(actual.xMax,Is.EqualTo(expected.xMax).Within(.1f));Assert.That(actual.yMax,Is.EqualTo(expected.yMax).Within(.1f));
        }
        [TestCase(15f)] [TestCase(80f)] [TestCase(170f)]
        public void CameraCanvasSelectionBoundsStayFixedBeforeMovingCanvasRefresh(float yaw)
        {
            using(var f=new Fixture())
            {
                Rect before=f.Bounds();Vector3 canvasPosition=f.Root.position;Quaternion canvasRotation=f.Root.rotation;
                // Camera moves before Unity's canvas pass: a valid screen-fixed selection must not project the old world pose.
                f.View.transform.SetPositionAndRotation(new Vector3(1200,300,-1800),Quaternion.Euler(12,yaw,4));
                Assert.That(f.Root.position,Is.EqualTo(canvasPosition),"Fixture captures the stale canvas window.");
                Assert.That(Quaternion.Angle(f.Root.rotation,canvasRotation),Is.LessThan(.001f));
                Same(f.Bounds(),before);
            }
        }
        [Test]
        public void ScreenFixedBoundsStillRespectHiddenCanvasGroups()
        {
            using(var f=new Fixture())
            {
                // Visibility is cached at target creation; create a fresh module target after adding the group.
                f.CanvasHost.AddComponent<CanvasGroup>().alpha=0;
                object t=Activator.CreateInstance(TargetType,"airspeed","Airspeed",f.Module,1f,0f);
                object[] args={f.View,default(Rect)};
                Assert.That((bool)TargetType.GetMethod("TryScreenBounds").Invoke(t,args),Is.False);
            }
        }
        [Test]
        public void WorldSpaceModuleKeepsItsCameraProjection()
        {
            using(var f=new Fixture(RenderMode.WorldSpace))
            {
                Rect before=f.Bounds();f.View.transform.position=new Vector3(.3f,.1f,0);
                Rect after=f.Bounds();Assert.That(after.center.x,Is.LessThan(before.center.x-20));
                var corners=new Vector3[4];f.Module.GetWorldCorners(corners);
                Vector2 a=f.View.WorldToScreenPoint(corners[0]),b=f.View.WorldToScreenPoint(corners[2]);
                Same(after,Rect.MinMaxRect(a.x-8,a.y-8,b.x+8,b.y+8));
            }
        }
    }
}
