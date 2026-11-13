using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaNearSpaceFastPathTests
    {
        private const BindingFlags Any = BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type Space => Type.GetType("AircraftControl.Camera.AircraftViewRenderSpace, AircraftControl",true);
        private static Type Projection => Type.GetType("IndicatorSystem.Core.IndicatorCanvasProjection, Assembly-CSharp",true);
        // The standard profile imports the shared registry, not the optional full canvas consumer.
        private static Type Geometry => Type.GetType("FAA.Customization.FaaCanvasLocalGeometry, Assembly-CSharp",false);
        private static object Call(Type type,string name,params object[] args)=>type.GetMethod(name,Any).Invoke(null,args);
        private sealed class Fixture : IDisposable
        {
            private readonly object native;
            public readonly GameObject Root,Child;
            public readonly Camera View;
            public readonly Component Rig;
            public Fixture(bool register=true)
            {
                Type t=typeof(FaaNearRenderSpaceIntegrationTests).GetNestedType("Fixture",BindingFlags.NonPublic);
                native=Activator.CreateInstance(t,true);View=(Camera)t.GetProperty("View").GetValue(native);Rig=(Component)t.GetField("Rig").GetValue(native);
                Near(true);
                Root=new GameObject("Explicit near canvas",typeof(RectTransform),typeof(Canvas));
                Root.transform.SetParent(View.transform,false);Root.transform.localPosition=new Vector3(0,0,1.5f);Root.transform.localScale=Vector3.one*.001f;
                ((RectTransform)Root.transform).sizeDelta=new Vector2(1920,1080);
                var canvas=Root.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;canvas.worldCamera=View;
                Child=new GameObject("Dynamic near graphic",typeof(RectTransform));Child.transform.SetParent(Root.transform,false);
                Child.transform.localPosition=new Vector3(80,-35,0);Child.transform.localRotation=Quaternion.Euler(0,0,13);Child.transform.localScale=new Vector3(.8f,1.1f,1);
                if(register)Register();
            }
            public void Near(bool active)=>Rig.GetType().GetMethod("SetNearRenderSpaceRequested",Any).Invoke(Rig,new object[]{active});
            public void Register()=>Call(Space,"RegisterRenderRoot",Root.transform,View);
            public void Dispose(){Object.DestroyImmediate(Root);((IDisposable)native).Dispose();}
        }
        [SetUp]public void Setup(){EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);Space.GetProperty("DiagnosticsEnabled").SetValue(null,true);Call(Space,"ResetGeometryDiagnostics");}
        [TearDown]public void End()=>Space.GetProperty("DiagnosticsEnabled").SetValue(null,false);
        private static bool NearView(Transform reference,Camera view){object[] a={reference,view,null};return(bool)Call(Space,"TryGetNearViewMatrix",a);}
        private static bool NearLocal(Transform child,Transform parent){object[] a={child,parent,null};return(bool)Call(Space,"TryGetNearLocalMatrix",a);}
        private static Vector3 Project(Type type,Transform reference,Vector3 point,Camera view){object[] a={reference,point,view,null};Assert.That((bool)Call(type,"TryProjectLocal",a),Is.True);return(Vector3)a[3];}
        private static Matrix4x4 Local(Type type,Transform child,Transform parent){object[] a={child,parent,null};Assert.That((bool)Call(type,"TryMatrix",a),Is.True);return(Matrix4x4)a[2];}
        private static void MatrixEqual(Matrix4x4 actual,Matrix4x4 expected,float tolerance=.003f){for(int r=0;r<4;r++)for(int c=0;c<4;c++)Assert.That(actual[r,c],Is.EqualTo(expected[r,c]).Within(tolerance),"matrix["+r+","+c+"]");}

        [TestCase(.001f)][TestCase(-.001f)][TestCase(.0001f)]
        public void OwnedSmallAndNegativeHudScalesActuallySelectBothFastPaths(float scale)
        {
            using(var f=new Fixture())
            {
                f.Root.transform.localScale=new Vector3(scale,Mathf.Abs(scale),Mathf.Abs(scale));
                Assert.That(NearView(f.Child.transform,f.View),Is.True);Assert.That(NearLocal(f.Child.transform,f.Root.transform),Is.True);
                if(Geometry!=null)MatrixEqual(Local(Geometry,f.Child.transform,f.Root.transform),Local(typeof(FAA.NearSpaceBaseline.FaaCanvasLocalGeometry),f.Child.transform,f.Root.transform));
                Assert.That((long)Space.GetProperty("NearViewMatrixHits").GetValue(null),Is.GreaterThan(0));
                Assert.That((long)Space.GetProperty("NearLocalMatrixHits").GetValue(null),Is.GreaterThan(0));
            }
        }
        [TestCase(0)][TestCase(1)][TestCase(2)]
        public void ActualCalibratedViewsRetainProjectionDensityAndInverseClicks(int custom)
        {
            using(var f=new Fixture())
            {
                Matrix4x4 p=Matrix4x4.Perspective(62,16f/9f,.03f,20000);p.m02=.17f;p.m12=-.04f;f.View.projectionMatrix=p;f.View.pixelRect=new Rect(20,30,1600,900);
                Matrix4x4 actual=f.View.worldToCameraMatrix;
                if(custom==1)actual=Matrix4x4.Translate(new Vector3(.03f,-.02f,.01f))*actual;
                if(custom==2)actual=Matrix4x4.Rotate(Quaternion.Euler(.03f,.1f,0))*actual;
                if(custom!=0)f.View.worldToCameraMatrix=actual;
                Vector3 point=new Vector3(27,-12,0),baseline=Project(typeof(FAA.NearSpaceBaseline.IndicatorCanvasProjection),f.Child.transform,point,f.View);
                Vector3 current=Project(Projection,f.Child.transform,point,f.View);
                Assert.That(Vector3.Distance(current,baseline),Is.LessThan(.04f));
                object[] b={f.Child.transform,f.View,null},n={f.Child.transform,f.View,null};
                Assert.That(FAA.NearSpaceBaseline.IndicatorCanvasProjection.TryPixelsPerReferenceUnit(f.Child.transform,f.View,out float density),Is.True);
                Assert.That((bool)Call(Projection,"TryPixelsPerReferenceUnit",n),Is.True);Assert.That((float)n[2],Is.EqualTo(density).Within(.0001f));
                object[] inverse={f.Child.GetComponent<RectTransform>(),(Vector2)current,f.View,null};Assert.That((bool)Call(Projection,"TryScreenToLocal",inverse),Is.True);
                Assert.That(Vector2.Distance((Vector2)inverse[3],(Vector2)point),Is.LessThan(.05f));
                Assert.That(f.View.projectionMatrix,Is.EqualTo(p));Assert.That(f.View.worldToCameraMatrix,Is.EqualTo(actual));
            }
        }
        [Test]
        public void UnregisteredAndFarReferencesRetainOriginalManualProjection()
        {
            using(var f=new Fixture(false))
            {
                Assert.That(NearView(f.Child.transform,f.View),Is.False);
                Assert.That(Project(Projection,f.Child.transform,Vector3.zero,f.View),Is.EqualTo(Project(typeof(FAA.NearSpaceBaseline.IndicatorCanvasProjection),f.Child.transform,Vector3.zero,f.View)));
                f.Register();f.Root.transform.position=new Vector3(129,0,1.5f);Assert.That(NearView(f.Child.transform,f.View),Is.False);
            }
        }
        [Test]
        public void UnrelatedDataWorldObjectAtSameCoordinateNeverAcquiresOwnership()
        {
            using(var f=new Fixture())
            {
                var unrelated=new GameObject("Data world object at matching numeric coordinates");
                try{unrelated.transform.position=f.Child.transform.position;Assert.That(NearView(unrelated.transform,f.View),Is.False);}finally{Object.DestroyImmediate(unrelated);}
            }
        }
        [Test]
        public void NonuniformCameraParentKeepsActualProjectionAndLocalHeadPose()
        {
            using(var f=new Fixture())
            {
                Transform parent=f.View.transform.parent;parent.localScale=new Vector3(1.2f,.8f,1.1f);
                Vector3 local=f.View.transform.localPosition;Quaternion rotation=f.View.transform.localRotation;
                Assert.That(NearView(f.Child.transform,f.View),Is.True);
                Vector3 point=new Vector3(20,-10,0);
                Assert.That(Vector3.Distance(Project(Projection,f.Child.transform,point,f.View),Project(typeof(FAA.NearSpaceBaseline.IndicatorCanvasProjection),f.Child.transform,point,f.View)),Is.LessThan(.04f));
                Assert.That(f.View.transform.localPosition,Is.EqualTo(local));Assert.That(f.View.transform.localRotation,Is.EqualTo(rotation));
                Assert.That(parent.localScale,Is.EqualTo(new Vector3(1.2f,.8f,1.1f)));
            }
        }
        [Test]
        public void DynamicChildReparentCannotCarryRootOwnershipOutsideCanvas()
        {
            using(var f=new Fixture())
            {
                Vector3 local=f.Child.transform.localPosition,scale=f.Child.transform.localScale;Quaternion rotation=f.Child.transform.localRotation;
                f.Child.transform.SetParent(null,true);Assert.That(NearView(f.Child.transform,f.View),Is.False);
                f.Child.transform.SetParent(f.Root.transform,true);
                // RectTransform's worldPositionStays across a .001 canvas can retain far UI coordinates;
                // this must fall back until the UI owner restores its valid local layout.
                if(f.Child.transform.position.magnitude>128)Assert.That(NearView(f.Child.transform,f.View),Is.False);
                f.Child.transform.localPosition=local;f.Child.transform.localScale=scale;f.Child.transform.localRotation=rotation;
                Assert.That(NearView(f.Child.transform,f.View),Is.True);
            }
        }
        [Test]
        public void NearReleaseReparentAndRebindRequireExplicitRegistrationAgain()
        {
            using(var f=new Fixture())
            {
                Assert.That(NearView(f.Child.transform,f.View),Is.True);f.Near(false);Assert.That(NearView(f.Child.transform,f.View),Is.False);
                f.Near(true);Assert.That(NearView(f.Child.transform,f.View),Is.False);f.Register();Assert.That(NearView(f.Child.transform,f.View),Is.True);
                var parent=new GameObject("Unannounced parent");
                try{f.Root.transform.SetParent(parent.transform,true);Assert.That(NearView(f.Child.transform,f.View),Is.False);f.Register();Assert.That(NearView(f.Child.transform,f.View),Is.True);}finally{f.Root.transform.SetParent(f.View.transform,true);Object.DestroyImmediate(parent);}
            }
        }
        [Test]
        public void ExplicitSpectatorSharesOnlyItsOwnerAndUnbindDoesNotReleaseNativeRoot()
        {
            using(var f=new Fixture())using(var other=new Fixture())
            {
                var spectator=new GameObject("Explicit shared spectator",typeof(Camera));
                try
                {
                    Camera preview=spectator.GetComponent<Camera>();preview.transform.SetPositionAndRotation(f.View.transform.position,f.View.transform.rotation);
                    Call(Space,"Bind",preview,f.Rig);Assert.That(NearView(f.Child.transform,preview),Is.True);
                    Call(Space,"Bind",preview,other.Rig);Assert.That(NearView(f.Child.transform,preview),Is.False);
                    Call(Space,"Bind",preview,null);Assert.That(NearView(f.Child.transform,f.View),Is.True);
                }finally{Object.DestroyImmediate(spectator);}
            }
        }
        [Test]
        public void BindingDisableAndOwnerReplacementCannotRetainOldRoots()
        {
            using(var f=new Fixture())using(var other=new Fixture())
            {
                var binding=(Behaviour)Call(Space,"ForCamera",f.View);binding.enabled=false;Assert.That(NearView(f.Child.transform,f.View),Is.False);
                // These are edit-mode objects; Unity does not deliver runtime OnDisable here.
                binding.GetType().GetMethod("OnDisable",Any).Invoke(binding,null);
                binding.enabled=true;Assert.That(NearView(f.Child.transform,f.View),Is.False);f.Register();Assert.That(NearView(f.Child.transform,f.View),Is.True);
                Call(Space,"Bind",f.View,other.Rig);Assert.That(NearView(f.Child.transform,f.View),Is.False);
            }
        }
        [Test]
        public void ZeroScaleAncestorRejectsNearPathAndRetainsBaselineIdentityContract()
        {
            using(var f=new Fixture())
            {
                f.Root.transform.localScale=Vector3.zero;Assert.That(NearLocal(f.Child.transform,f.Root.transform),Is.False);
                if(Geometry!=null)
                {
                    MatrixEqual(Local(Geometry,f.Child.transform,f.Root.transform),Local(typeof(FAA.NearSpaceBaseline.FaaCanvasLocalGeometry),f.Child.transform,f.Root.transform),0f);
                    MatrixEqual(Local(Geometry,f.Root.transform,f.Root.transform),Matrix4x4.identity,0f);
                }
                MatrixEqual(Local(typeof(FAA.NearSpaceBaseline.FaaCanvasLocalGeometry),f.Root.transform,f.Root.transform),Matrix4x4.identity,0f);
            }
        }
        [TestCase(127,true)][TestCase(128,false)]
        public void NativeCanvasBranchPreservesOriginal128AncestorLimit(int depth,bool expected)
        {
            using(var f=new Fixture())
            {
                Transform node=f.Root.transform;
                for(int i=0;i<depth;i++){var g=new GameObject("Depth "+i);g.transform.SetParent(node,false);node=g.transform;}
                Assert.That(NearLocal(node,f.Root.transform),Is.EqualTo(expected));
                object[] n={node,f.Root.transform,null},b={node,f.Root.transform,null};if(Geometry!=null)Assert.That((bool)Call(Geometry,"TryMatrix",n),Is.EqualTo(expected));Assert.That((bool)Call(typeof(FAA.NearSpaceBaseline.FaaCanvasLocalGeometry),"TryMatrix",b),Is.EqualTo(expected));
            }
        }
        [Test]
        public void NonDescendantAndNestedOtherOwnerCannotSelectCanvasFastPath()
        {
            using(var f=new Fixture())using(var other=new Fixture())
            {
                Assert.That(NearLocal(f.Child.transform,other.Root.transform),Is.False);
                var nested=new GameObject("Nested other owner's root",typeof(RectTransform));nested.transform.SetParent(f.Root.transform,false);
                try{Call(Space,"RegisterRenderRoot",nested.transform,other.View);Assert.That(NearView(nested.transform,f.View),Is.False);Assert.That(NearView(nested.transform,other.View),Is.True);Assert.That(NearLocal(nested.transform,f.Root.transform),Is.False);}finally{Object.DestroyImmediate(nested);}
            }
        }
    }
}
