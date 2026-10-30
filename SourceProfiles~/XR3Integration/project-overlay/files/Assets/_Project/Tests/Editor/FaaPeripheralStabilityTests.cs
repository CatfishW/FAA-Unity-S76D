using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class FaaPeripheralStabilityTests
    {
        private static Type T(string name)=>Type.GetType("FAA.Customization."+name+", Assembly-CSharp",true);
        private static object Invoke(string type,string method,params object[] args)=>T(type).GetMethod(method).Invoke(null,args);
        private static void Set(object entry,string field,object value)=>entry.GetType().GetField(field).SetValue(entry,value);
        private static float Get(object entry,string field)=>(float)entry.GetType().GetField(field).GetValue(entry);
        private static object Entry(float yaw,float elevation,float distance,float scale)
        {
            var entry=Activator.CreateInstance(T("FaaSpatialLayoutEntry"));Set(entry,"id","settings");
            Set(entry,"yaw",yaw);Set(entry,"elevation",elevation);Set(entry,"distance",distance);Set(entry,"scale",scale);return entry;
        }

        [TestCase(24f,0f,.55f,.4f)] [TestCase(-24f,0f,.55f,1.6f)]
        [TestCase(0f,-8f,1.5f,1f)] [TestCase(0f,80f,.55f,1.6f)]
        [TestCase(0f,-80f,3f,.4f)] [TestCase(45f,45f,1.2f,1.6f)]
        [TestCase(-45f,-45f,1.2f,.8f)] [TestCase(90f,-8f,1.5f,1f)]
        [TestCase(-90f,-8f,1.5f,1f)] [TestCase(180f,0f,.55f,1.6f)]
        [TestCase(7f,10f,3f,1.6f)] [TestCase(-7f,-10f,3f,1.6f)]
        public void FullPanelCornersClearForwardCone(float yaw,float elevation,float distance,float scale)
        {
            const float width=.58f,height=.68f;
            var entry=Entry(yaw,elevation,distance,scale);
            Invoke("FaaPeripheralPanelLayout","Protect",entry,width,height,1f);
            Vector3 center=(Vector3)Invoke("FaaSpatialLayoutMath","Position",entry);
            Quaternion rotation=Quaternion.LookRotation(center.normalized,Vector3.up);
            for(int x=-1;x<=1;x+=2)for(int y=-1;y<=1;y+=2)
            {
                Vector3 corner=center+rotation*new Vector3(x*width*Get(entry,"scale")*.5f,y*height*Get(entry,"scale")*.5f,0);
                Assert.That(Vector3.Angle(Vector3.forward,corner),Is.GreaterThanOrEqualTo(60f));
            }
        }
        [Test] public void ProtectedPlacementIsIdempotentNotGazeChasing()
        {
            var entry=Entry(24,10,.8f,1.4f);Invoke("FaaPeripheralPanelLayout","Protect",entry,.58f,.68f,1f);
            float yaw=Get(entry,"yaw");
            for(int i=0;i<300;i++)Assert.That((bool)Invoke("FaaPeripheralPanelLayout","Protect",entry,.58f,.68f,1f),Is.False);
            Assert.That(Get(entry,"yaw"),Is.EqualTo(yaw));
        }
        [Test] public void ValidPeripheralPositionIsPreserved()
        {
            var entry=Entry(-135,-12,2.4f,.7f);
            Assert.That((bool)Invoke("FaaPeripheralPanelLayout","Protect",entry,.58f,.68f,-1f),Is.False);
            Assert.That(Get(entry,"yaw"),Is.EqualTo(-135));
        }
        [Test] public void CorruptLegacyPositionRecoversToCorrectSide()
        {
            var entry=Entry(float.NaN,0,float.PositiveInfinity,0);
            Assert.That((bool)Invoke("FaaPeripheralPanelLayout","Protect",entry,.58f,.68f,-1f),Is.True);
            Assert.That(Get(entry,"yaw"),Is.LessThan(0));Assert.That(Get(entry,"distance"),Is.EqualTo(1.5f));
        }
        [Test] public void CanvasLocalBoundsIgnoreRootWorldPositionRotationAndScale()
        {
            var root=new GameObject("Root",typeof(RectTransform));
            var parent=new GameObject("Instrument group",typeof(RectTransform));
            var child=new GameObject("Readout",typeof(RectTransform));
            try
            {
                parent.transform.SetParent(root.transform,false);child.transform.SetParent(parent.transform,false);
                parent.transform.localScale=Vector3.one*540f;parent.transform.localPosition=new Vector3(.6f,0,0);
                child.transform.localPosition=new Vector3(.2f,-.1f,0);child.transform.localScale=Vector3.one/540;
                var rect=(RectTransform)child.transform;rect.sizeDelta=new Vector2(160,52);
                object[] args={child.transform,root.transform,null};Assert.That((bool)Invoke("FaaCanvasLocalGeometry","TryMatrix",args),Is.True);
                Rect baseline=(Rect)Invoke("FaaCanvasLocalGeometry","TransformRect",rect.rect,args[2]);
                for(int i=0;i<60;i++)
                {
                    root.transform.SetPositionAndRotation(new Vector3(10000000+i*35,9000000,5000000),Quaternion.Euler(i*2,i*5,i));
                    root.transform.localScale=Vector3.one*(1+i*.02f);
                    object[] changed={child.transform,root.transform,null};Invoke("FaaCanvasLocalGeometry","TryMatrix",changed);
                    Assert.That((Rect)Invoke("FaaCanvasLocalGeometry","TransformRect",rect.rect,changed[2]),Is.EqualTo(baseline));
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(root);}
        }
        [Test] public void LocalMatrixRejectsUnrelatedAncestor()
        {
            var a=new GameObject("A");var b=new GameObject("B");
            try{Assert.That((bool)Invoke("FaaCanvasLocalGeometry","TryMatrix",new object[]{a.transform,b.transform,null}),Is.False);}
            finally{UnityEngine.Object.DestroyImmediate(a);UnityEngine.Object.DestroyImmediate(b);}
        }
        [Test] public void ExplicitDesktopInspectionReturnsOnResetWithoutChangingFieldOfView()
        {
            Type type=Type.GetType("AircraftControl.Camera.AircraftCameraController, AircraftControl",true);
            var go=new GameObject("Inactive inspection fixture",typeof(Camera));go.SetActive(false);
            try
            {
                var controller=go.AddComponent(type);go.SetActive(true);
                float fov=go.GetComponent<Camera>().fieldOfView;
                Assert.That((bool)type.GetMethod("BeginPanelInspection").Invoke(controller,new object[]{90f,-8f}),Is.True);
                Assert.That((bool)type.GetProperty("IsPanelInspectionActive").GetValue(controller),Is.True);
                var input=type.GetMethod("ProcessLookInput",BindingFlags.NonPublic|BindingFlags.Instance);
                input.Invoke(controller,new object[]{true,Vector2.zero,true,.016f});
                input.Invoke(controller,new object[]{true,Vector2.zero,false,.016f});
                Assert.That((bool)type.GetProperty("IsPanelInspectionActive").GetValue(controller),Is.True,"A drag beginning on a control must not seize the camera after leaving it.");
                type.GetMethod("ResetView").Invoke(controller,null);
                Assert.That((bool)type.GetProperty("IsPanelInspectionActive").GetValue(controller),Is.False);
                Assert.That(go.GetComponent<Camera>().fieldOfView,Is.EqualTo(fov));
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [Test] public void DesktopInspectionZoomIsCriticallyDampedNeverWiderAndRestoresTheExactFieldOfView()
        {
            Type type=Type.GetType("AircraftControl.Camera.AircraftCameraController, AircraftControl",true);
            var go=new GameObject("Inspection zoom fixture",typeof(Camera));go.SetActive(false);
            try
            {
                var controller=go.AddComponent(type);go.SetActive(true);
                var camera=go.GetComponent<Camera>();camera.fieldOfView=57.3f;
                var set=type.GetMethod("SetInspectionFieldOfView");var begin=type.GetMethod("BeginPanelInspection");
                var step=type.GetMethod("StepFieldOfView",BindingFlags.NonPublic|BindingFlags.Instance);
                Assert.That((bool)set.Invoke(controller,new object[]{30f}),Is.False,"Zoom belongs to a panel inspection.");
                Assert.That((bool)begin.Invoke(controller,new object[]{90f,-8f}),Is.True);
                Assert.That((bool)set.Invoke(controller,new object[]{30f}),Is.True);
                float previous=camera.fieldOfView,t=0f,settledAt=-1f;
                for(int i=0;i<70;i++)
                {
                    step.Invoke(controller,new object[]{.01f});t+=.01f;float f=camera.fieldOfView;
                    Assert.That(f,Is.LessThanOrEqualTo(previous+1e-4f),"Monotonic: critically damped, no overshoot.");
                    Assert.That(f,Is.GreaterThanOrEqualTo(30f-1e-3f));previous=f;
                    if(settledAt<0f&&Mathf.Abs(f-30f)<.1f)settledAt=t;
                }
                Assert.That(settledAt,Is.InRange(.3f,.5f),"Settles in about 0.35-0.5 s.");
                Assert.That(camera.fieldOfView,Is.EqualTo(30f).Within(.01f));
                Assert.That((float)type.GetProperty("BaseFieldOfView").GetValue(controller),Is.EqualTo(57.3f));
                type.GetMethod("ResetView").Invoke(controller,null);
                for(int i=0;i<100;i++)step.Invoke(controller,new object[]{.01f});
                Assert.That(camera.fieldOfView,Is.EqualTo(57.3f),"FORWARD restores the exact base FOV.");
                Assert.That((bool)type.GetProperty("IsFieldOfViewTransitioning").GetValue(controller),Is.False);
                begin.Invoke(controller,new object[]{-90f,-8f});set.Invoke(controller,new object[]{90f});
                Assert.That((float)type.GetProperty("FieldOfViewTarget").GetValue(controller),Is.EqualTo(57.3f),"Never wider than the base FOV.");
                set.Invoke(controller,new object[]{25f});step.Invoke(controller,new object[]{.05f});
                Assert.That(camera.fieldOfView,Is.LessThan(57.3f));
                type.GetMethod("ResetViewImmediate").Invoke(controller,null);
                Assert.That(camera.fieldOfView,Is.EqualTo(57.3f),"A mode change restores at once.");
            }
            finally{UnityEngine.Object.DestroyImmediate(go);}
        }
        [TestCase(KeyCode.R,KeyCode.F,KeyCode.PageUp,KeyCode.PageDown,true)]
        [TestCase(KeyCode.PageUp,KeyCode.PageDown,KeyCode.PageUp,KeyCode.PageDown,false)]
        [TestCase(KeyCode.Y,KeyCode.R,KeyCode.Y,KeyCode.PageDown,true)]
        public void CollectiveNeverSharesTheForwardViewKey(KeyCode up,KeyCode down,KeyCode expectedUp,KeyCode expectedDown,bool changed)
        {
            Type type=Type.GetType("AircraftControl.Core.AircraftController, AircraftControl",true);
            object[] args={up,down};
            Assert.That((bool)type.GetMethod("ResolveCollectiveKeys").Invoke(null,args),Is.EqualTo(changed));
            Assert.That((KeyCode)args[0],Is.EqualTo(expectedUp));Assert.That((KeyCode)args[1],Is.EqualTo(expectedDown));
            Assert.That((KeyCode)type.GetField("ViewResetKey").GetValue(null),Is.EqualTo(KeyCode.R));
        }
    }
}
