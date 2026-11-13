using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.TestTools;
using UnityEditor.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Customization.Tests
{
    public sealed class FaaRadarScreenPresentationTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type PanelType=>Type.GetType("FAA.Customization.FaaSpatialRadarPanel, Assembly-CSharp",true);
        private sealed class Fixture:IDisposable
        {
            public readonly GameObject CameraHost,CanvasHost,CockpitHost;public readonly Camera View;public readonly RenderTexture Texture;
            public readonly Canvas Canvas;public readonly RectTransform Root,Scope,Header;public readonly object Panel;
            public Fixture(bool utility=false)
            {
                CameraHost=new GameObject("Pixel-stability eye",typeof(Camera));View=CameraHost.GetComponent<Camera>();View.fieldOfView=60;View.nearClipPlane=.03f;
                View.clearFlags=CameraClearFlags.SolidColor;View.backgroundColor=Color.black;View.cullingMask=1<<31;View.allowHDR=false;View.allowMSAA=false;
                View.transform.SetPositionAndRotation(new Vector3(30000,10000,-20000),Quaternion.Euler(3,20,2));Texture=new RenderTexture(1920,1080,24);Texture.Create();View.targetTexture=Texture;View.aspect=1920f/1080;
                CockpitHost=new GameObject("Pixel-stability cockpit");CockpitHost.transform.SetPositionAndRotation(View.transform.position,Quaternion.identity);
                CanvasHost=new GameObject("Pixel-stability original canvas",typeof(RectTransform),typeof(Canvas),typeof(TrackedDeviceGraphicRaycaster));CanvasHost.layer=31;
                Root=(RectTransform)CanvasHost.transform;Root.sizeDelta=new Vector2(1920,1080);Canvas=CanvasHost.GetComponent<Canvas>();Canvas.renderMode=RenderMode.WorldSpace;Canvas.worldCamera=View;Canvas.scaleFactor=1;
                Scope=ImageRect("Original scope",Root,Vector2.zero,new Vector2(320,320));Header=ImageRect("Original sibling header",Root,new Vector2(0,230),new Vector2(400,50));
                Panel=Activator.CreateInstance(PanelType,new object[]{null,utility?"settings":"traffic",Canvas,Scope,95f,utility,.42f});Call("SetSpatial",true);Call("Apply",CockpitHost.transform,View,false);UnityEngine.Canvas.ForceUpdateCanvases();
            }
            private static RectTransform ImageRect(string name,Transform parent,Vector2 position,Vector2 size)
            {
                var go=new GameObject(name,typeof(RectTransform),typeof(Image));go.layer=31;var r=(RectTransform)go.transform;r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=position;r.sizeDelta=size;go.GetComponent<Image>().color=Color.magenta;return r;
            }
            public object Call(string name,params object[] args)=>PanelType.GetMethod(name,Any).Invoke(Panel,args);
            public void Inspect(bool native=false)=>Assert.That((bool)Call("ApplyDesktopInspection",View,new Rect(500,200,800,650),native,null),Is.EqualTo(!native));
            public void Dispose()
            {
                Call("ApplyAuthoredPose",CockpitHost.transform,View,false);Call("SetSpatial",false);
                if(Application.isPlaying)Call("Dispose");else
                {
                    var frame=PanelType.GetField("desktopRoot",Any).GetValue(Panel) as RectTransform;
                    if(frame!=null)UnityEngine.Object.DestroyImmediate(frame.gameObject);
                }
                View.targetTexture=null;UnityEngine.Object.DestroyImmediate(CanvasHost);UnityEngine.Object.DestroyImmediate(CameraHost);UnityEngine.Object.DestroyImmediate(CockpitHost);UnityEngine.Object.DestroyImmediate(Texture);
            }
        }
        [TestCase(false)] [TestCase(true)]
        public void CameraPresentationUsesOriginalObjectsAndRestoresTheirAuthoredHierarchyAndPose(bool utility)
        {
            using(var f=new Fixture(utility))
            {
                Vector3 position=f.Root.localPosition,scale=f.Root.localScale;Quaternion rotation=f.Root.localRotation;
                Vector2 scopeAt=f.Scope.anchoredPosition,headerAt=f.Header.anchoredPosition;int scopeSibling=f.Scope.GetSiblingIndex(),headerSibling=f.Header.GetSiblingIndex(),children=f.Root.childCount;
                f.Inspect();Assert.That(f.Canvas.renderMode,Is.EqualTo(RenderMode.ScreenSpaceCamera));Assert.That(f.Scope.parent,Is.EqualTo(f.Header.parent));Assert.That(f.Scope.parent,Is.Not.EqualTo(f.Root));
                Assert.That(f.Canvas.GetComponentsInChildren<Image>(true).Length,Is.EqualTo(3),"Original scope/header plus the existing layout grip; no UI copies.");
                f.Call("ApplyAuthoredPose",f.CockpitHost.transform,f.View,false);
                Assert.That(f.Canvas.renderMode,Is.EqualTo(RenderMode.WorldSpace));Assert.That(f.Scope.parent,Is.EqualTo(f.Root));Assert.That(f.Header.parent,Is.EqualTo(f.Root));
                Assert.That(f.Root.childCount,Is.EqualTo(children));Assert.That(f.Scope.anchoredPosition,Is.EqualTo(scopeAt));Assert.That(f.Header.anchoredPosition,Is.EqualTo(headerAt));
                Assert.That(f.Scope.GetSiblingIndex(),Is.EqualTo(scopeSibling));Assert.That(f.Header.GetSiblingIndex(),Is.EqualTo(headerSibling));
                Assert.That(f.Root.localPosition,Is.EqualTo(position));Assert.That(f.Root.localScale,Is.EqualTo(scale));Assert.That(Quaternion.Angle(f.Root.localRotation,rotation),Is.LessThan(.001f));
            }
        }
        [Test]
        public void LateNativeAndTrackedAdmissionRestoreTemporaryDesktopHierarchyBeforeRejecting()
        {
            using(var f=new Fixture())
            {
                f.Inspect();f.Inspect(true);Assert.That(f.Canvas.renderMode,Is.EqualTo(RenderMode.WorldSpace));Assert.That(f.Scope.parent,Is.EqualTo(f.Root));
                f.Inspect();var tracking=f.CameraHost.AddComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>();
                Assert.That((bool)f.Call("ApplyDesktopInspection",f.View,new Rect(500,200,800,650),false,null),Is.False);
                Assert.That(f.Canvas.renderMode,Is.EqualTo(RenderMode.WorldSpace));Assert.That(f.Scope.parent,Is.EqualTo(f.Root));UnityEngine.Object.DestroyImmediate(tracking);
            }
        }
        [Test]
        public void DisablingSpatialModeRestoresOriginalCanvasAndHierarchy()
        {
            using(var f=new Fixture())
            {
                f.Inspect();f.Call("SetSpatial",false);Assert.That(f.Canvas.renderMode,Is.EqualTo(RenderMode.WorldSpace));Assert.That(f.Scope.parent,Is.EqualTo(f.Root));Assert.That(f.Header.parent,Is.EqualTo(f.Root));
            }
        }
        [UnityTest,Explicit("Dedicated normal-render GPU regression; run this exact method.")]
        public IEnumerator RenderedPixelsStayStillAsCameraCrossesLargeWorldCoordinates()
        {
            if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)Assert.Ignore("A graphics device is required.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            yield return new EnterPlayMode();
            foreach(var script in Resources.FindObjectsOfTypeAll<MonoBehaviour>())if(script!=null&&script.GetType().FullName=="FAA.Headset.XR3HeadsetCompatibility")script.enabled=false;
            var f=new Fixture();var readback=new Texture2D(1920,1080,TextureFormat.RGB24,false);RectInt? first=null;int frames=0;
            try
            {
                for(int frame=0;frame<36;frame++)
                {
                    f.View.transform.SetPositionAndRotation(new Vector3(30000+frame*.017f,10000+Mathf.Sin(frame*.1f)*.25f,-20000-frame*.013f),Quaternion.Euler(3+Mathf.Sin(frame*.1f),20+frame*.03f,2));
                    f.Call("Apply",f.CockpitHost.transform,f.View,false);f.Inspect();
                    yield return null;yield return null;
                    RenderTexture previous=RenderTexture.active;RenderTexture.active=f.Texture;readback.ReadPixels(new Rect(0,0,1920,1080),0,0);readback.Apply();RenderTexture.active=previous;
                    Color32[] pixels=readback.GetPixels32();int x0=1920,y0=1080,x1=-1,y1=-1;
                    for(int y=0;y<1080;y++)for(int x=0;x<1920;x++){Color32 p=pixels[y*1920+x];if(p.r<180||p.b<180||p.g>80)continue;x0=Mathf.Min(x0,x);x1=Mathf.Max(x1,x);y0=Mathf.Min(y0,y);y1=Mathf.Max(y1,y);}
                    Assert.That(x1,Is.GreaterThan(x0),"The enabled camera must render the original magenta UI; no Camera.Render call is used.");
                    var actual=new RectInt(x0,y0,x1-x0+1,y1-y0+1);if(first==null)first=actual;
                    Assert.That(Mathf.Abs(actual.xMin-first.Value.xMin),Is.LessThanOrEqualTo(1));Assert.That(Mathf.Abs(actual.yMin-first.Value.yMin),Is.LessThanOrEqualTo(1));
                    Assert.That(Mathf.Abs(actual.xMax-first.Value.xMax),Is.LessThanOrEqualTo(1));Assert.That(Mathf.Abs(actual.yMax-first.Value.yMax),Is.LessThanOrEqualTo(1));frames++;
                }
                TestContext.WriteLine("Normal camera rendering kept "+frames+" large-origin UI frames within one pixel.");
            }
            finally{UnityEngine.Object.DestroyImmediate(readback);f.Dispose();}
            yield return new ExitPlayMode();
        }
    }
}
