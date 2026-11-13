using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public sealed class FaaLegacyConformalRenderOriginTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type HudType => Type.GetType("FAA.Customization.FaaConformalHudController, Assembly-CSharp", true);
        private static Type RigType => Type.GetType("AircraftControl.Camera.AircraftCameraController, AircraftControl", true);
        private static Type SpaceType => Type.GetType("AircraftControl.Camera.AircraftViewRenderSpace, AircraftControl", true);
        private static object Get(object o,string name)=>o.GetType().GetField(name,Any).GetValue(o);
        private static void Set(object o,string name,object value)=>o.GetType().GetField(name,Any).SetValue(o,value);
        private static object Call(object o,string name,params object[] args)=>o.GetType().GetMethod(name,Any).Invoke(o,args);

        private sealed class Fixture : IDisposable
        {
            public readonly GameObject Aircraft = new GameObject("Logical aircraft");
            public readonly GameObject Head = new GameObject("Native view",typeof(Camera));
            public readonly GameObject CanvasHost = new GameObject("Optional legacy conformal canvas",typeof(RectTransform),typeof(Canvas));
            public readonly GameObject Host = new GameObject("Legacy conformal owner");
            public readonly Component Rig,Hud;
            public Camera View=>Head.GetComponent<Camera>();
            public Canvas Canvas=>CanvasHost.GetComponent<Canvas>();
            public readonly Vector3 Local = new Vector3(2f,3f,4f);
            public readonly Quaternion LocalRotation = Quaternion.Euler(2f,4f,1f);
            public readonly Vector3 LocalScale = new Vector3(.02f,.025f,.03f);

            public Fixture(bool near,bool align=true)
            {
                Aircraft.transform.SetPositionAndRotation(new Vector3(12466.819f,-3.7f,-4424.2754f),Quaternion.Euler(-7f,54f,-3f));
                CanvasHost.transform.SetParent(Aircraft.transform,false);
                CanvasHost.transform.SetLocalPositionAndRotation(Local,LocalRotation);
                CanvasHost.transform.localScale=LocalScale;
                Head.transform.SetLocalPositionAndRotation(new Vector3(.1f,1.65f,-.2f),Quaternion.Euler(-4f,13f,2f));
                Head.AddComponent(Type.GetType("UnityEngine.InputSystem.XR.TrackedPoseDriver, Unity.InputSystem",true));
                Rig=Head.AddComponent(RigType);Set(Rig,"aircraftTransform",Aircraft.transform);
                Set(Rig,"cockpitOffset",Vector3.zero);
                object origin=Get(Rig,"_nativeXrOrigin");
                Call(origin,"Follow",Head.transform,near?Vector3.zero:Aircraft.transform.position,Aircraft.transform.rotation,Vector3.zero,false,false,false);
                SpaceType.GetMethod("Bind").Invoke(null,new object[]{View,Rig});
                if(near)Call(Rig,"SetNearRenderSpaceRequested",true);
                Hud=Host.AddComponent(HudType);
                Set(Hud,"_conformalCanvas",Canvas);Set(Hud,"aircraftTransform",Aircraft.transform);Set(Hud,"projectionCamera",View);
                Set(Hud,"useWorldSpaceConformalCanvas",true);Set(Hud,"useRotorcraftSceneLayer",false);Set(Hud,"alignToAircraftReference",align);
            }
            public void Configure()=>Call(Hud,"ConfigureConformalCanvas");
            public Transform Anchor=>Get(Hud,"_legacyRenderAnchor") as Transform;
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Host);
                UnityEngine.Object.DestroyImmediate(CanvasHost);
                UnityEngine.Object.DestroyImmediate(Head);
                UnityEngine.Object.DestroyImmediate(Aircraft);
            }
        }

        [Test]
        public void NativeLegacyPlaneStaysNearTheEyeWithItsDistanceScaleAndTrackedPoseUnchanged()
        {
            using(var f=new Fixture(true))
            {
                Vector3 headLocal=f.Head.transform.localPosition;Quaternion headRotation=f.Head.transform.localRotation;
                Matrix4x4 projection=f.View.projectionMatrix;
                f.Configure();
                Assert.That(f.Anchor,Is.Not.Null);
                Assert.That(f.Anchor.parent,Is.Null,"The disposable tracking origin must not own this canvas.");
                Assert.That(f.CanvasHost.transform.parent,Is.SameAs(f.Anchor));
                Assert.That(f.Anchor.position.magnitude,Is.LessThan(.0001f));
                Assert.That(Vector3.Distance(f.CanvasHost.transform.position,f.Aircraft.transform.rotation*Vector3.forward*175.6f),Is.LessThan(.001f));
                Assert.That(Quaternion.Angle(f.CanvasHost.transform.rotation,f.Aircraft.transform.rotation),Is.LessThan(.001f));
                Assert.That(f.CanvasHost.transform.localScale,Is.EqualTo(f.LocalScale));
                Assert.That(f.Head.transform.localPosition,Is.EqualTo(headLocal));
                Assert.That(f.Head.transform.localRotation,Is.EqualTo(headRotation));
                Assert.That(f.View.projectionMatrix,Is.EqualTo(projection));
            }
        }

        [Test]
        public void NormalDesktopLegacyPlaneKeepsItsOriginalAircraftParentAndWorldPose()
        {
            using(var f=new Fixture(false))
            {
                f.Configure();
                Assert.That(f.Anchor,Is.Null);
                Assert.That(f.CanvasHost.transform.parent,Is.SameAs(f.Aircraft.transform));
                Vector3 expected=f.Aircraft.transform.position+f.Aircraft.transform.rotation*Vector3.forward*175.6f;
                Assert.That(Vector3.Distance(f.CanvasHost.transform.position,expected),Is.LessThan(.005f));
                Assert.That(f.CanvasHost.transform.localScale,Is.EqualTo(f.LocalScale));
            }
        }

        [Test]
        public void NativeReleaseRestoresParentAndCurrentAircraftPoseBeforeAnotherHudUpdate()
        {
            using(var f=new Fixture(true))
            {
                f.Configure();Transform oldAnchor=f.Anchor;
                f.Aircraft.transform.SetPositionAndRotation(new Vector3(20000f,40f,-18000f),Quaternion.Euler(-5f,68f,-2f));
                Call(f.Rig,"SetNearRenderSpaceRequested",false); // actual owner's release event, no manual HUD refresh
                Assert.That(f.CanvasHost.transform.parent,Is.SameAs(f.Aircraft.transform));
                Assert.That(f.Anchor,Is.Null);Assert.That(oldAnchor==null,Is.True);
                Quaternion reference=(Quaternion)RigType.GetProperty("AircraftReferenceRotation").GetValue(f.Rig);
                Vector3 expected=f.Aircraft.transform.position+reference*Vector3.forward*175.6f;
                Assert.That(Vector3.Distance(f.CanvasHost.transform.position,expected),Is.LessThan(.005f));
                Assert.That(f.CanvasHost.transform.localScale,Is.EqualTo(f.LocalScale));
            }
        }

        [Test]
        public void RigTeardownDoesNotDestroyTheOptionalCanvas()
        {
            using(var f=new Fixture(true))
            {
                f.Configure();
                Call(f.Rig,"OnDisable");
                Assert.That(f.Canvas,Is.Not.Null);
                Assert.That(f.CanvasHost.transform.parent,Is.SameAs(f.Aircraft.transform));
                Assert.That(f.Anchor,Is.Null);
                Assert.That(RigType.GetProperty("NativeXrTrackingOrigin").GetValue(f.Rig),Is.Null);
            }
        }

        [TestCase("head-fixed")]
        [TestCase("legacy-off")]
        [TestCase("disable")]
        public void LeavingTheLegacyRouteRestoresItsCapturedLocalPose(string exit)
        {
            using(var f=new Fixture(true))
            {
                f.Configure();
                if(exit=="head-fixed")
                {
                    Type mode=HudType.GetNestedType("HudPresentationMode");
                    Call(f.Hud,"SetPresentationMode",Enum.Parse(mode,"HeadFixed"));
                }
                else if(exit=="legacy-off")
                {Set(f.Hud,"useWorldSpaceConformalCanvas",false);Call(f.Hud,"EnforceCanvasState");}
                else Call(f.Hud,"OnDisable");
                Assert.That(f.Anchor,Is.Null);
                Assert.That(f.CanvasHost.transform.parent,Is.SameAs(f.Aircraft.transform));
                Assert.That(f.CanvasHost.transform.localPosition,Is.EqualTo(f.Local));
                Assert.That(f.CanvasHost.transform.localRotation,Is.EqualTo(f.LocalRotation));
                Assert.That(f.CanvasHost.transform.localScale,Is.EqualTo(f.LocalScale));
            }
        }

        [Test]
        public void UnalignedAuthoredPlaneFollowsItsOriginalAircraftLocalPose()
        {
            using(var f=new Fixture(true,false))
            {
                f.Configure();
                for(int i=0;i<20;i++)
                {
                    f.Aircraft.transform.SetPositionAndRotation(new Vector3(12466+i*.003f,-3.7f,-4424+i*.005f),Quaternion.Euler(-7f,54f+i,-3f));
                    Call(f.Rig,"UpdateNativeXrOrigin"); // Commit the camera pose and its cached render offset together.
                    f.Configure();
                    Vector3 expected=f.Aircraft.transform.rotation*f.Local;
                    Assert.That(Vector3.Distance(f.CanvasHost.transform.position,expected),Is.LessThan(.001f));
                    Assert.That(Quaternion.Angle(f.CanvasHost.transform.rotation,f.Aircraft.transform.rotation*f.LocalRotation),Is.LessThan(.001f));
                    Assert.That(f.CanvasHost.transform.localScale,Is.EqualTo(f.LocalScale));
                }
            }
        }

        [Test]
        public void BeforeRenderPicksUpLateMrActivationAndFollowsTheNativeOriginOrder()
        {
            using(var f=new Fixture(false))
            {
                f.Configure();Assert.That(f.Anchor,Is.Null);
                Call(f.Rig,"SetNearRenderSpaceRequested",true);
                Call(f.Hud,"RefreshRenderProjection");
                Assert.That(f.Anchor,Is.Not.Null,"Late MR activation must be mapped before rendering, not one LateUpdate later.");
                var origin=RigType.GetMethod("RefreshNativeXrOrigin",Any).GetCustomAttribute<BeforeRenderOrderAttribute>();
                var legacy=HudType.GetMethod("RefreshRenderProjection",Any).GetCustomAttribute<BeforeRenderOrderAttribute>();
                Assert.That(legacy.order,Is.GreaterThan(origin.order));
            }
        }
    }
}
