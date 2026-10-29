using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
namespace FAA.Customization.Tests
{
    public sealed class FaaClassicHeadingSlotStabilityTests
    {
        private static Type HudType=>Type.GetType("FAA.Customization.FaaClassicAnalogHud, Assembly-CSharp",true);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject EyeHost,FlightHost,HudHost,TapeHost;
            public readonly Camera Eye;
            public readonly RenderTexture Texture;
            public readonly Canvas TapeCanvas;
            public readonly RectTransform Tape,Frame;
            public readonly Component Hud;
            public Fixture(bool world=false)
            {
                EyeHost=new GameObject("Heading-slot moving eye",typeof(Camera));Eye=EyeHost.GetComponent<Camera>();
                Eye.nearClipPlane=.03f;Eye.farClipPlane=10000;Eye.fieldOfView=60;
                Texture=new RenderTexture(1600,900,0);Eye.targetTexture=Texture;
                FlightHost=new GameObject("Heading-slot flight canvas",typeof(RectTransform),typeof(Canvas));
                var flight=FlightHost.GetComponent<Canvas>();flight.renderMode=world?RenderMode.WorldSpace:RenderMode.ScreenSpaceOverlay;flight.worldCamera=Eye;
                if(world){((RectTransform)FlightHost.transform).sizeDelta=new Vector2(1920,1080);FlightHost.transform.position=new Vector3(0,0,4);FlightHost.transform.localScale=Vector3.one*.002f;}
                var source=new GameObject("Heading-slot original root",typeof(RectTransform));source.transform.SetParent(FlightHost.transform,false);
                HudHost=new GameObject("Heading-slot Classic",typeof(RectTransform));Hud=HudHost.AddComponent(HudType);
                HudType.GetMethod("Build").Invoke(Hud,new object[]{flight,source.transform});
                Frame=(RectTransform)HudType.GetProperty("Root").GetValue(Hud);
                TapeHost=new GameObject("Heading-slot overlay tape",typeof(RectTransform),typeof(Canvas));TapeCanvas=TapeHost.GetComponent<Canvas>();TapeCanvas.renderMode=RenderMode.ScreenSpaceOverlay;
                var tapeGo=new GameObject("Heading tape band",typeof(RectTransform));Tape=(RectTransform)tapeGo.transform;Tape.SetParent(TapeHost.transform,false);
                Tape.anchorMin=new Vector2(.37f,.23f);Tape.anchorMax=new Vector2(.63f,.27f);Tape.offsetMin=Tape.offsetMax=Vector2.zero;
                Canvas.ForceUpdateCanvases();HudType.GetMethod("ApplyLayout").Invoke(Hud,null);Canvas.ForceUpdateCanvases();
            }
            public bool Project(out Rect rect){object[] args={Tape,TapeCanvas,default(Rect)};bool ok=(bool)HudType.GetMethod("TryProjectHeadingTapeFrameRect",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(Hud,args);rect=(Rect)args[2];return ok;}
            public void Dispose(){Eye.targetTexture=null;UnityEngine.Object.DestroyImmediate(HudHost);UnityEngine.Object.DestroyImmediate(TapeHost);UnityEngine.Object.DestroyImmediate(FlightHost);UnityEngine.Object.DestroyImmediate(EyeHost);UnityEngine.Object.DestroyImmediate(Texture);}
        }
        private static void Same(Rect actual,Rect expected)
        {
            Assert.That(actual.xMin,Is.EqualTo(expected.xMin).Within(.02f));Assert.That(actual.yMin,Is.EqualTo(expected.yMin).Within(.02f));
            Assert.That(actual.xMax,Is.EqualTo(expected.xMax).Within(.02f));Assert.That(actual.yMax,Is.EqualTo(expected.yMax).Within(.02f));
        }
        [TestCase(15f)] [TestCase(80f)] [TestCase(170f)]
        public void OverlayHeadingSlotStaysFixedBeforeClassicCanvasFollowsCamera(float yaw)
        {
            using(var f=new Fixture())
            {
                Assert.That(f.Project(out Rect before),Is.True,"The normal tape must be usable before camera movement.");
                var canvas=(RectTransform)f.HudHost.transform;Vector3 stalePosition=canvas.position;Quaternion staleRotation=canvas.rotation;
                for(int i=0;i<12;i++)
                {
                    // The aircraft/view moves in LateUpdate before Unity refreshes screen-space canvas world poses.
                    f.Eye.transform.SetPositionAndRotation(new Vector3(i*31f,i*.5f,-i*43f),Quaternion.Euler(i*.1f,yaw+i*.2f,i*.03f));
                    Assert.That(canvas.position,Is.EqualTo(stalePosition));Assert.That(Quaternion.Angle(canvas.rotation,staleRotation),Is.LessThan(.001f));
                    Assert.That(f.Project(out Rect after),Is.True,"A stale canvas pose must not make the live tape disappear from layout.");Same(after,before);
                }
            }
        }
        [Test]
        public void WorldSpaceHeadingSlotRetainsThePhysicalCameraProjection()
        {
            using(var f=new Fixture(true))
            {
                f.Project(out Rect before);f.Eye.transform.position=new Vector3(.25f,.1f,0);f.Project(out Rect after);
                Assert.That(Vector2.Distance(after.center,before.center),Is.GreaterThan(1f));
            }
        }
    }
}
