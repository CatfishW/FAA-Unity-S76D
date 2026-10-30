using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
namespace FAA.Customization.Tests
{
    public sealed class FaaWorldCanvasKeepOutPrecisionTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type T(string name)=>Type.GetType("FAA.Customization."+name+", Assembly-CSharp",true);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject EyeHost,CanvasHost,GraphicHost;public readonly Camera Eye;public readonly Canvas Canvas;public readonly RectTransform Graphic;
            public readonly RenderTexture Texture;public readonly Component Region;public readonly object Module;public readonly Matrix4x4 Projection;
            public Fixture(bool asymmetric,bool related=true)
            {
                EyeHost=new GameObject("World keep-out precision eye",typeof(Camera));Eye=EyeHost.GetComponent<Camera>();Eye.nearClipPlane=.03f;Eye.farClipPlane=1000;Eye.fieldOfView=60;
                Texture=new RenderTexture(1600,900,0);Eye.targetTexture=Texture;Eye.aspect=1600f/900;
                if(asymmetric){var p=Matrix4x4.Perspective(60,1600f/900,.03f,1000);p.m02=.17f;p.m12=-.09f;Eye.projectionMatrix=p;}Projection=Eye.projectionMatrix;
                CanvasHost=new GameObject("Physical HUD keep-out canvas",typeof(RectTransform),typeof(Canvas));Canvas=CanvasHost.GetComponent<Canvas>();Canvas.renderMode=RenderMode.WorldSpace;Canvas.worldCamera=Eye;
                var root=(RectTransform)CanvasHost.transform;if(related)root.SetParent(Eye.transform,false);root.localPosition=new Vector3(0,0,2.1f);root.localRotation=Quaternion.identity;root.localScale=Vector3.one*.001f;root.sizeDelta=new Vector2(1920,1080);
                GraphicHost=new GameObject("Protected readout",typeof(RectTransform),typeof(Image));Graphic=(RectTransform)GraphicHost.transform;Graphic.SetParent(root,false);Graphic.anchorMin=Graphic.anchorMax=Graphic.pivot=new Vector2(.5f,.5f);Graphic.anchoredPosition=new Vector2(-300,25);Graphic.sizeDelta=new Vector2(220,80);
                Region=GraphicHost.AddComponent(T("FaaHudKeepOutRegion"));var kind=Enum.Parse(T("FaaKeepOutKind"),"Symbology");Region.GetType().GetMethod("Configure").Invoke(Region,new object[]{"precision-region",kind,0f,Graphic});
                Module=Activator.CreateInstance(T("FaaNonConformalScaleTarget"),"airspeed","Airspeed",Graphic,1f,0f);Canvas.ForceUpdateCanvases();
            }
            public Rect RegionBounds(){object[] args={default(Rect)};Assert.That((bool)Region.GetType().GetMethod("TryGetScreenRect").Invoke(Region,args),Is.True);return (Rect)args[0];}
            public Rect ModuleBounds(){object[] args={Eye,default(Rect)};Assert.That((bool)Module.GetType().GetMethod("TryScreenBounds").Invoke(Module,args),Is.True);return (Rect)args[1];}
            public void Move(float x,float z,int frame){Eye.transform.SetPositionAndRotation(new Vector3(x+frame*.0003f,frame*.0002f,z+frame*.0007f),Quaternion.Euler(Mathf.Sin(frame*.1f)*3f,27+frame*.013f,2f));}
            public void ProjectionUnchanged(){for(int i=0;i<16;i++)Assert.That(Eye.projectionMatrix[i],Is.EqualTo(Projection[i]));}
            public void Dispose(){Eye.targetTexture=null;UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(EyeHost);UnityEngine.Object.DestroyImmediate(Texture);}
        }
        private static void Same(Rect a,Rect b){Assert.That(a.xMin,Is.EqualTo(b.xMin).Within(.05f));Assert.That(a.yMin,Is.EqualTo(b.yMin).Within(.05f));Assert.That(a.xMax,Is.EqualTo(b.xMax).Within(.05f));Assert.That(a.yMax,Is.EqualTo(b.yMax).Within(.05f));}
        [TestCase(15000f,-18000f,false)] [TestCase(20000f,-20000f,false)]
        [TestCase(15000f,-18000f,true)] [TestCase(20000f,-20000f,true)]
        public void RegisteredPhysicalReadoutBoundsRemainSteadyAtFlightCoordinates(float x,float z,bool asymmetric)
        {
            using(var f=new Fixture(asymmetric))
            {f.Move(0,0,0);Rect expected=f.RegionBounds();for(int frame=0;frame<60;frame++){f.Move(x,z,frame);Same(f.RegionBounds(),expected);f.ProjectionUnchanged();}}
        }
        [TestCase(15000f,-18000f,false)] [TestCase(20000f,-20000f,false)]
        [TestCase(15000f,-18000f,true)] [TestCase(20000f,-20000f,true)]
        public void PhysicalModuleBoundsRemainSteadyAtFlightCoordinates(float x,float z,bool asymmetric)
        {
            using(var f=new Fixture(asymmetric))
            {f.Move(0,0,0);Rect expected=f.ModuleBounds();for(int frame=0;frame<60;frame++){f.Move(x,z,frame);Same(f.ModuleBounds(),expected);f.ProjectionUnchanged();}}
        }
        [TestCase(false)] [TestCase(true)]
        public void UnrelatedPhysicalCanvasRetainsTheExistingCameraProjection(bool asymmetric)
        {
            using(var f=new Fixture(asymmetric,false))
            {
                f.Eye.transform.SetPositionAndRotation(Vector3.zero,Quaternion.identity);var corners=new Vector3[4];f.Graphic.GetWorldCorners(corners);
                Vector2 min=new Vector2(float.PositiveInfinity,float.PositiveInfinity),max=-min;
                foreach(var corner in corners){Vector2 p=f.Eye.WorldToScreenPoint(corner);min=Vector2.Min(min,p);max=Vector2.Max(max,p);}
                Same(f.RegionBounds(),Rect.MinMaxRect(min.x,min.y,max.x,max.y));Same(f.ModuleBounds(),Rect.MinMaxRect(min.x-8,min.y-8,max.x+8,max.y+8));f.ProjectionUnchanged();
            }
        }
    }
}
