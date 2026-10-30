using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaDesktopSettingsRasterTests
    {
        const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        static object Call(object owner,string name,params object[] args)=>owner.GetType().GetMethod(name,Any).Invoke(owner,args);
        static Rect Project(RectTransform rect,Camera view)
        {
            var corners=new Vector3[4];rect.GetWorldCorners(corners);var min=Vector2.positiveInfinity;var max=Vector2.negativeInfinity;
            foreach(var corner in corners){Vector3 p=view.WorldToScreenPoint(corner);min=Vector2.Min(min,p);max=Vector2.Max(max,p);}
            return Rect.MinMaxRect(min.x,min.y,max.x,max.y);
        }
        static int White(Color32[] pixels,int width,int height,Rect area)
        {
            int count=0;for(int y=Mathf.Max(0,Mathf.FloorToInt(area.yMin));y<Mathf.Min(height,Mathf.CeilToInt(area.yMax));y++)
                for(int x=Mathf.Max(0,Mathf.FloorToInt(area.xMin));x<Mathf.Min(width,Mathf.CeilToInt(area.xMax));x++)
                {Color32 c=pixels[y*width+x];if(c.r>130&&c.g>130&&c.b>130)count++;}
            return count;
        }
        static int Glyphs(TMP_Text text,Camera view,Color32[] pixels,int width,int height)
        {
            int visible=0;
            for(int i=0;i<text.textInfo.characterCount;i++)
            {
                var ch=text.textInfo.characterInfo[i];if(!ch.isVisible)continue;
                Vector3 a=view.WorldToScreenPoint(text.transform.TransformPoint(ch.bottomLeft));
                Vector3 b=view.WorldToScreenPoint(text.transform.TransformPoint(ch.topRight));
                var box=Rect.MinMaxRect(Mathf.Min(a.x,b.x),Mathf.Min(a.y,b.y),Mathf.Max(a.x,b.x),Mathf.Max(a.y,b.y));
                Assert.That(White(pixels,width,height,box),Is.GreaterThan(0),"Actual visible glyph must rasterize: "+ch.character);visible++;
            }
            return visible;
        }
        [Serializable]sealed class Sample{public int frame,titleWhitePixels,titleGlyphs,closeGlyphs,cameraFrames;public Rect titleBounds;}
        [Serializable]sealed class Report{public string engine,graphics,method;public int width,height,normalCameraFrames;public Sample[] samples;public bool targetTextureStayedNull,authoredRestored;}

        [UnityTest,Explicit("Owned enabled display-camera Settings raster/restore; no Camera.Render or targetTexture capability bypass.")]
        public IEnumerator OwnedDisplaySettingsRenderAndRestoreFromOrdinaryCameraFrames()
        {
            if(SystemInfo.graphicsDeviceType==GraphicsDeviceType.Null)Assert.Ignore("Actual normal graphics required.");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            yield return new EnterPlayMode();
            // Create lambda-captured render state only after EnterPlayMode's domain reload.
            yield return CaptureOwnedSettingsAfterReload();
            yield return new ExitPlayMode();
        }

        static IEnumerator CaptureOwnedSettingsAfterReload()
        {
            var chromeType=Type.GetType("FAA.Customization.FaaPilotChrome, Assembly-CSharp",true);
            Assert.That(chromeType,Is.Not.Null,"Runtime Chrome type exists.");
            var reset=chromeType.GetMethod("ResetDeveloperMode",Any);
            Assert.That(reset,Is.Not.Null,"The normal play-session reset method exists.");
            reset.Invoke(null,null);
            FaaDesktopSettingsInspectionTests.Fixture fixture=null;
            try{fixture=new FaaDesktopSettingsInspectionTests.Fixture();}
            catch(Exception error){Assert.Fail("Settings raster fixture construction failed: "+error);}
            Assert.That(fixture,Is.Not.Null,"Constructed Settings fixture exists.");
            Camera camera=fixture.Desktop;Assert.That(camera,Is.Not.Null,"The owned display camera is available.");
            Canvas settings=fixture.Canvas;Assert.That(settings,Is.Not.Null,"The original Settings canvas is available.");
            Component owner=fixture.Owner,rig=fixture.Rig;
            int width=camera.pixelWidth,height=camera.pixelHeight,rendered=0;
            var capture=new RenderTexture(width,height,0,RenderTextureFormat.ARGB32);capture.Create();
            var previousTarget=RenderTexture.active;RenderTexture.active=capture;GL.Clear(false,true,Color.black);RenderTexture.active=previousTarget;
            var readback=new Texture2D(width,height,TextureFormat.RGB24,false);
            var command=new CommandBuffer{name="Private Settings display readback"};command.Blit(BuiltinRenderTextureType.CameraTarget,capture);
            Camera.CameraCallback completed=view=>{if(view==camera)rendered++;};Camera.onPostRender+=completed;camera.AddCommandBuffer(CameraEvent.AfterEverything,command);
            // Positive capture control: known SSCamera graphic on the same ordinary display pass.
            var markerHost=new GameObject("Private screen-camera capture control",typeof(RectTransform),typeof(Canvas));markerHost.layer=30;
            Canvas markerCanvas=markerHost.GetComponent<Canvas>();markerCanvas.renderMode=RenderMode.ScreenSpaceCamera;markerCanvas.worldCamera=camera;markerCanvas.planeDistance=.2f;markerCanvas.sortingOrder=32760;
            var marker=new GameObject("Known magenta pixel marker",typeof(RectTransform),typeof(Image));marker.layer=30;marker.transform.SetParent(markerHost.transform,false);
            var markerRect=(RectTransform)marker.transform;markerRect.anchorMin=markerRect.anchorMax=Vector2.zero;markerRect.pivot=Vector2.zero;markerRect.anchoredPosition=new Vector2(8,8);markerRect.sizeDelta=new Vector2(8,8);marker.GetComponent<Image>().color=Color.magenta;marker.GetComponent<Image>().raycastTarget=false;
            var samples=new List<Sample>();var aircraft=(Transform)owner.GetType().GetField("aircraft",Any).GetValue(owner);Vector3 initial=aircraft.position;
            string folder=Path.GetFullPath(Path.Combine(Application.dataPath,"../../settings-work/raster"));Directory.CreateDirectory(folder);
            try
            {
                fixture.Open();fixture.AssertOpen();
                TMP_Text title=settings.GetComponentsInChildren<TMP_Text>(true).Single(x=>x.name=="Workspace Title");
                TMP_Text close=settings.GetComponentsInChildren<Button>(true).Single(x=>x.name=="Stow Settings").GetComponentInChildren<TMP_Text>();
                for(int frame=0;frame<4;frame++)
                {
                    aircraft.SetPositionAndRotation(initial+new Vector3(frame*250,0,-frame*170),Quaternion.Euler(0,frame*3,0));
                    Call(rig,"UpdateNativeXrOrigin");Call(owner,"RefreshTransforms");Canvas.ForceUpdateCanvases();
                    yield return null;yield return null;fixture.AssertOpen();
                    Assert.That(camera.targetTexture,Is.Null,"The real owned-display capability remains in force.");
                    var prior=RenderTexture.active;RenderTexture.active=capture;readback.ReadPixels(new Rect(0,0,width,height),0,0);readback.Apply();RenderTexture.active=prior;
                    Color32[] pixels=readback.GetPixels32();int marked=0;
                    for(int y=0;y<32;y++)for(int x=0;x<32;x++){Color32 c=pixels[y*width+x];if(c.r>200&&c.b>200&&c.g<60)marked++;}
                    if(marked<16)Assert.Inconclusive("Capture setup did not include the known ScreenSpaceCamera marker; do not infer a product rendering failure.");
                    Assert.That(rendered,Is.GreaterThan(0),"Normal enabled camera must have rendered, not a manual Camera.Render call.");
                    Rect bounds=Project(title.rectTransform,camera);Assert.That(bounds.xMin,Is.GreaterThanOrEqualTo(0));Assert.That(bounds.yMin,Is.GreaterThanOrEqualTo(0));Assert.That(bounds.xMax,Is.LessThanOrEqualTo(width));Assert.That(bounds.yMax,Is.LessThanOrEqualTo(height));
                    var sample=new Sample{frame=frame,titleBounds=bounds,titleWhitePixels=White(pixels,width,height,bounds),titleGlyphs=Glyphs(title,camera,pixels,width,height),closeGlyphs=Glyphs(close,camera,pixels,width,height),cameraFrames=rendered};
                    Assert.That(sample.titleGlyphs,Is.EqualTo(15));Assert.That(sample.closeGlyphs,Is.EqualTo(5));samples.Add(sample);
                    File.WriteAllBytes(Path.Combine(folder,"settings-open-"+frame+".png"),readback.EncodeToPNG());
                }
                fixture.Close();fixture.AssertRestored();yield return null;yield return null;
                var saved=RenderTexture.active;RenderTexture.active=capture;readback.ReadPixels(new Rect(0,0,width,height),0,0);readback.Apply();RenderTexture.active=saved;
                File.WriteAllBytes(Path.Combine(folder,"settings-closed.png"),readback.EncodeToPNG());
                var report=new Report{engine=Application.unityVersion,graphics=SystemInfo.graphicsDeviceName,method="Same original Settings UI;4moving virtual-aircraft poses; normal enabled owned-display camera with AfterEverything readback and independent SSCamera marker. No Camera.Render, no targetTexture assignment, no optical/Windows-performance claim.",width=width,height=height,normalCameraFrames=rendered,samples=samples.ToArray(),targetTextureStayedNull=camera.targetTexture==null,authoredRestored=true};
                File.WriteAllText(Path.Combine(folder,"settings-raster.json"),JsonUtility.ToJson(report,true));
            }
            finally
            {
                Camera.onPostRender-=completed;if(camera!=null)camera.RemoveCommandBuffer(CameraEvent.AfterEverything,command);command.Release();
                Object.DestroyImmediate(markerHost);Object.DestroyImmediate(readback);capture.Release();Object.DestroyImmediate(capture);((IDisposable)fixture).Dispose();
            }
            yield return null;LogAssert.NoUnexpectedReceived();
        }
    }
}
