using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
namespace FAA.Customization.Tests
{
    public sealed class FaaIndicatorProjectionPrecisionTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type T(string name)=>Type.GetType(name+", Assembly-CSharp",true);
        private static void Set(object obj,string name,object value)=>obj.GetType().GetField(name,Any).SetValue(obj,value);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject EyeHost,CanvasHost,PoolHost;
            public readonly Camera Eye;public readonly Canvas Canvas;public readonly RectTransform Container;public readonly RenderTexture Texture;
            public readonly ScriptableObject Settings;public readonly Component Pool,Element;public readonly object Data;public readonly Matrix4x4 Projection;
            public Fixture(bool asymmetric)
            {
                EyeHost=new GameObject("Precision eye",typeof(Camera));Eye=EyeHost.GetComponent<Camera>();Eye.nearClipPlane=.03f;Eye.farClipPlane=1000;Eye.fieldOfView=60;
                Texture=new RenderTexture(1600,900,0);Eye.targetTexture=Texture;Eye.aspect=1600f/900;
                if(asymmetric){var p=Matrix4x4.Perspective(60,1600f/900,.03f,1000);p.m02=.17f;p.m12=-.09f;Eye.projectionMatrix=p;}
                Projection=Eye.projectionMatrix;
                CanvasHost=new GameObject("Camera-parented XR style canvas",typeof(RectTransform),typeof(Canvas));Canvas=CanvasHost.GetComponent<Canvas>();Canvas.renderMode=RenderMode.WorldSpace;Canvas.worldCamera=Eye;
                var root=(RectTransform)CanvasHost.transform;root.SetParent(Eye.transform,false);root.localPosition=new Vector3(0,0,2.1f);root.localRotation=Quaternion.identity;root.localScale=Vector3.one*.001f;root.sizeDelta=new Vector2(1920,1080);
                var container=new GameObject("Actual cue pool container",typeof(RectTransform));Container=(RectTransform)container.transform;Container.SetParent(root,false);Container.anchorMin=Vector2.zero;Container.anchorMax=Vector2.one;Container.offsetMin=Container.offsetMax=Vector2.zero;
                PoolHost=new GameObject("Actual indicator pool",typeof(RectTransform));Pool=PoolHost.AddComponent(T("IndicatorSystem.Display.IndicatorPool"));Set(Pool,"initialPoolSize",0);
                Settings=ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings"));Set(Settings,"usePilotCueStyle",true);Set(Settings,"useCustomPrefabs",false);Set(Settings,"globalScale",1f);Set(Settings,"globalOpacity",1f);
                Pool.GetType().GetMethod("Initialize").Invoke(Pool,new object[]{Settings,Container});
                Data=Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));Set(Data,"Id","PRECISION");Set(Data,"IsActive",true);Set(Data,"Type",Enum.Parse(T("IndicatorSystem.Core.IndicatorType"),"Traffic"));Set(Data,"Visibility",Enum.Parse(T("IndicatorSystem.Core.IndicatorVisibility"),"OnScreen"));Set(Data,"ProjectionRect",new Rect(0,0,1600,900));Set(Data,"ScreenPosition",new Vector2(1250,500));Set(Data,"DistanceNM",12f);Set(Data,"Color",Color.cyan);
                Element=(Component)Pool.GetType().GetMethod("GetIndicator",new[]{typeof(string),Data.GetType()}).Invoke(Pool,new object[]{"PRECISION",Data});Canvas.ForceUpdateCanvases();
            }
            public float Density()=>(float)T("IndicatorSystem.Controller.IndicatorSystemController").GetMethod("PixelsPerReferenceUnit").Invoke(null,new object[]{Canvas,Eye});
            public Rect Region()=>(Rect)T("IndicatorSystem.Controller.IndicatorSystemController").GetMethod("GetProjectionRect").Invoke(null,new object[]{Canvas,Eye});
            public Vector2 Place(){Element.GetType().GetMethod("UpdateIndicator").Invoke(Element,new object[]{Data,Settings});return ((RectTransform)Element.transform).anchoredPosition;}
            public void Move(float x,float z,int frame){Eye.transform.SetPositionAndRotation(new Vector3(x+frame*.0003f,.0002f*frame,z+frame*.0007f),Quaternion.Euler(Mathf.Sin(frame*.1f)*3f,27+frame*.013f,2f));}
            public void AssertProjectionUnchanged(){for(int i=0;i<16;i++)Assert.That(Eye.projectionMatrix[i],Is.EqualTo(Projection[i]),"Measurement must not modify calibrated projection.");}
            public void Dispose(){Eye.targetTexture=null;UnityEngine.Object.DestroyImmediate(PoolHost);UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(EyeHost);UnityEngine.Object.DestroyImmediate(Texture);UnityEngine.Object.DestroyImmediate(Settings);}
        }
        [TestCase(15000f,-18000f,false)] [TestCase(20000f,-20000f,false)]
        [TestCase(15000f,-18000f,true)] [TestCase(20000f,-20000f,true)]
        public void PhysicalCanvasPixelDensityDoesNotCollapseOrDoubleAtFlightCoordinates(float x,float z,bool asymmetric)
        {
            using(var f=new Fixture(asymmetric))
            {
                f.Move(0,0,0);float expected=f.Density();Assert.That(expected,Is.GreaterThan(.1f));
                for(int frame=0;frame<60;frame++){f.Move(x,z,frame);Assert.That(f.Density(),Is.EqualTo(expected).Within(.002f),"One tiny canvas-unit world subtraction must not change cue padding or scale.");f.AssertProjectionUnchanged();}
            }
        }
        [TestCase(15000f,-18000f,false)] [TestCase(20000f,-20000f,false)]
        [TestCase(15000f,-18000f,true)] [TestCase(20000f,-20000f,true)]
        public void ActualPooledCueInversePlacementStaysInItsCanvasFrameAtFlightCoordinates(float x,float z,bool asymmetric)
        {
            using(var f=new Fixture(asymmetric))
            {
                f.Move(0,0,0);Vector2 expected=f.Place();
                for(int frame=0;frame<30;frame++){f.Move(x,z,frame);Assert.That(Vector2.Distance(f.Place(),expected),Is.LessThan(.03f),"The real pooled cue must not shake after the camera/canvas translate together.");f.AssertProjectionUnchanged();}
            }
        }
        [TestCase(false)] [TestCase(true)]
        public void PhysicalProjectionWindowTracksOnlyTheLocalCanvasPose(bool asymmetric)
        {
            using(var f=new Fixture(asymmetric))
            {
                f.Move(0,0,0);Rect expected=f.Region();
                for(int frame=0;frame<30;frame++){f.Move(20000,-20000,frame);Rect actual=f.Region();Assert.That(Vector2.Distance(actual.center,expected.center),Is.LessThan(.1f));Assert.That(Vector2.Distance(actual.size,expected.size),Is.LessThan(.1f));f.AssertProjectionUnchanged();}
            }
        }
        [Test]
        public void IndicatorProjectionRunsAfterCameraWorkspaceAndMixedRealityPresentation()
        {
            Type indicator=T("IndicatorSystem.Controller.IndicatorSystemController");
            int late=indicator.GetCustomAttribute<DefaultExecutionOrder>().order;
            int before=0;
            foreach(var method in indicator.GetMethods(Any))foreach(BeforeRenderOrderAttribute order in method.GetCustomAttributes(typeof(BeforeRenderOrderAttribute),false))before=Mathf.Max(before,order.order);
            foreach(string name in new[]{"AircraftControl.Camera.AircraftCameraController","FAA.Customization.FaaSpatialWorkspace","FAA.Headset.XR3MixedRealityPresentation"})
            {
                Type owner=Type.GetType(name+", Assembly-CSharp",false);if(owner==null)continue;
                var execution=owner.GetCustomAttribute<DefaultExecutionOrder>();if(execution!=null)Assert.That(late,Is.GreaterThan(execution.order),"Indicator projection must consume the final "+name+" layout.");
                foreach(var method in owner.GetMethods(Any))foreach(BeforeRenderOrderAttribute order in method.GetCustomAttributes(typeof(BeforeRenderOrderAttribute),false))Assert.That(before,Is.GreaterThan(order.order),"BeforeRender must follow "+name+".");
            }
        }
        [TestCase(0f,0f)] [TestCase(15000f,-18000f)] [TestCase(20000f,-20000f)]
        public void SharedOriginProjectionRetainsAnActualTranslatedViewMatrix(float x,float z)
        {
            using(var f=new Fixture(true))
            {
                f.Move(0,0,0);var corners=new Vector3[4];var root=(RectTransform)f.Canvas.transform;root.GetWorldCorners(corners);
                Vector2 OriginalCentre()
                {
                    Vector2 min=new Vector2(float.PositiveInfinity,float.PositiveInfinity),max=-min;
                    foreach(var p in corners){Vector2 screen=f.Eye.WorldToScreenPoint(p);min=Vector2.Min(min,screen);max=Vector2.Max(max,screen);}return (min+max)*.5f;
                }
                Vector2 baseline=OriginalCentre();Matrix4x4 standard=f.Eye.worldToCameraMatrix;
                f.Eye.worldToCameraMatrix=Matrix4x4.Translate(new Vector3(-.03f,.01f,0))*standard;
                Vector2 expectedShift=OriginalCentre()-baseline;Assert.That(expectedShift.magnitude,Is.GreaterThan(5f),"The view-offset control must be meaningful.");f.Eye.ResetWorldToCameraMatrix();
                float maxDefaultOriginDelta=0,maxWorldToLocalTranslation=0,maxTrsTranslation=0,maxWorldToLocalLinear=0,maxTrsLinear=0;
                for(int frame=0;frame<60;frame++)
                {
                    f.Move(x,z,frame);Vector3 actualOrigin=f.Eye.cameraToWorldMatrix.GetColumn(3);maxDefaultOriginDelta=Mathf.Max(maxDefaultOriginDelta,Vector3.Distance(actualOrigin,f.Eye.transform.position));
                    Matrix4x4 actual=f.Eye.worldToCameraMatrix,hand=Matrix4x4.Scale(new Vector3(1,1,-1));
                    Matrix4x4 local=hand*f.Eye.transform.worldToLocalMatrix,trs=hand*Matrix4x4.TRS(f.Eye.transform.position,f.Eye.transform.rotation,Vector3.one).inverse;
                    for(int row=0;row<3;row++){maxWorldToLocalTranslation=Mathf.Max(maxWorldToLocalTranslation,Mathf.Abs(actual[row,3]-local[row,3]));maxTrsTranslation=Mathf.Max(maxTrsTranslation,Mathf.Abs(actual[row,3]-trs[row,3]));for(int col=0;col<3;col++){maxWorldToLocalLinear=Mathf.Max(maxWorldToLocalLinear,Mathf.Abs(actual[row,col]-local[row,col]));maxTrsLinear=Mathf.Max(maxTrsLinear,Mathf.Abs(actual[row,col]-trs[row,col]));}}
                }
                Debug.Log("Default cameraToWorld origin delta at ("+x+","+z+") = "+maxDefaultOriginDelta.ToString("R")+"; worldToLocal translation="+maxWorldToLocalTranslation.ToString("R")+" linear="+maxWorldToLocalLinear.ToString("R")+"; TRS translation="+maxTrsTranslation.ToString("R")+" linear="+maxTrsLinear.ToString("R"));
                f.Move(x,z,0);Rect before=f.Region();standard=f.Eye.worldToCameraMatrix;
                f.Eye.worldToCameraMatrix=Matrix4x4.Translate(new Vector3(-.03f,.01f,0))*standard;Matrix4x4 custom=f.Eye.worldToCameraMatrix;
                Vector2 actualShift=f.Region().center-before.center;
                // A float custom view matrix at 20km can quantize the supplied offset by millimetres; never drop it entirely.
                Assert.That(Vector2.Distance(actualShift,expectedShift),Is.LessThan(x==0f?.02f:1.5f),"The shared-origin shortcut must retain the actual calibrated view translation.");
                for(int i=0;i<16;i++)Assert.That(f.Eye.worldToCameraMatrix[i],Is.EqualTo(custom[i]));f.Eye.ResetWorldToCameraMatrix();
            }
        }

        [TestCase(0f,0f)] [TestCase(20000f,-20000f)]
        public void ActualCustomViewRotationIsPreservedRatherThanTreatedAsAWorldOriginOffset(float x,float z)
        {
            using(var f=new Fixture(true))
            {
                f.Move(x,z,13);
                f.Eye.worldToCameraMatrix=Matrix4x4.Rotate(Quaternion.Euler(.0002f,.0003f,0))*f.Eye.worldToCameraMatrix;
                Vector3 expected=f.Eye.WorldToScreenPoint(f.Canvas.transform.position);
                object[] args={f.Canvas.transform,Vector3.zero,f.Eye,null};
                Assert.That((bool)T("IndicatorSystem.Core.IndicatorCanvasProjection").GetMethod("TryProjectLocal").Invoke(null,args),Is.True);
                Vector3 actual=(Vector3)args[3];
                Assert.That(Vector2.Distance(expected,actual),Is.LessThan(.02f),"A calibrated view basis must not be rewritten or guessed from a tolerance.");
                f.AssertProjectionUnchanged();
            }
        }
    }
}
