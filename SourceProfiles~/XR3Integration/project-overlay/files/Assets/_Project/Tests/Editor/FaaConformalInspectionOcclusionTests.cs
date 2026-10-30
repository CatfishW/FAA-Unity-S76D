using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using TMPro;

namespace FAA.Customization.Tests
{
    public sealed class FaaConformalInspectionOcclusionTests
    {
        const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        Type layerType,workspaceType;Component layer,workspace;object previousWorkspace;
        GameObject layerHost,workspaceHost,eyeHost,canvasHost,aircraftHost;Camera eye;RenderTexture texture;
        Rect panel=new Rect(650,580,300,180);
        object Call(string name,params object[] args)=>layerType.GetMethod(name,Any).Invoke(layer,args);
        void Set(string name,object value)=>layerType.GetField(name,Any).SetValue(layer,value);
        T Get<T>(string name)=>(T)layerType.GetField(name,Any).GetValue(layer);
        Vector3 Ray(Vector2 point,float distance=1000f){object[] a={point,Vector3.zero};Assert.That((bool)Call("FromPixel",a),Is.True);return (Vector3)a[1]*distance;}
        Vector2 Pixel(Vector3 point){object[] a={point,Vector2.zero};Assert.That((bool)Call("ToPixel",a),Is.True);return (Vector2)a[1];}
        void Clear(){Get<List<Vector3>>("vertices").Clear();Get<List<Color>>("colors").Clear();Get<List<int>>("triangles").Clear();Get<List<int>>("haloTriangles").Clear();Get<List<Rect>>("placed").Clear();}
        void Panel(Rect rect,bool open=true)
        {
            workspaceType.GetProperty("InspectedPanelId",Any).SetValue(workspace,open?"weather":null);
            workspaceType.GetField("inspectedScreenRect",Any).SetValue(workspace,rect);
            workspaceType.GetField("inspectedScreenRectValid",Any).SetValue(workspace,open);
            Call("RefreshZones");
            // Exercise the fallback case directly: the attitude window still crosses the inspected panel.
            Set("windowPx",new Rect(0,0,1600,900));
        }
        [SetUp]public void Create()
        {
            layerType=Type.GetType("FAA.Customization.FaaRotorcraftConformalLayer, Assembly-CSharp");
            if(layerType==null)Assert.Ignore("Optional full-profile conformal mesh implementation.");
            workspaceType=Type.GetType("FAA.Customization.FaaSpatialWorkspace, Assembly-CSharp",true);
            previousWorkspace=workspaceType.GetProperty("Current",Any).GetValue(null);
            workspaceHost=new GameObject("Inactive inspection workspace");workspaceHost.SetActive(false);workspace=workspaceHost.AddComponent(workspaceType);
            workspaceType.GetProperty("Current",Any).SetValue(null,workspace);
            eyeHost=new GameObject("Inspection eye",typeof(Camera));eye=eyeHost.GetComponent<Camera>();
            texture=new RenderTexture(1600,900,0);eye.targetTexture=texture;eye.aspect=1600f/900f;eye.fieldOfView=60f;eye.farClipPlane=150000;
            canvasHost=new GameObject("Source HUD",typeof(RectTransform),typeof(Canvas));canvasHost.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;
            aircraftHost=new GameObject("Aircraft");layerHost=new GameObject("Inactive conformal fixture");layerHost.SetActive(false);layer=layerHost.AddComponent(layerType);
            Call("Bind",eye,aircraftHost.transform,canvasHost.GetComponent<Canvas>());
            var labels=new GameObject("Actual label pool");labels.transform.SetParent(layerHost.transform,false);Set("worldLabelRoot",labels.transform);
            Set("radius",1000f);Set("intensity",1f);Call("CacheView");Call("ComputeAngularSizes");Panel(panel);
        }
        [TearDown]public void Cleanup()
        {
            if(workspaceType!=null)workspaceType.GetProperty("Current",Any).SetValue(null,previousWorkspace);
            if(eye!=null)eye.targetTexture=null;
            if(layerHost!=null)UnityEngine.Object.DestroyImmediate(layerHost);
            if(workspaceHost!=null)UnityEngine.Object.DestroyImmediate(workspaceHost);
            if(canvasHost!=null)UnityEngine.Object.DestroyImmediate(canvasHost);
            if(aircraftHost!=null)UnityEngine.Object.DestroyImmediate(aircraftHost);
            if(eyeHost!=null)UnityEngine.Object.DestroyImmediate(eyeHost);
            if(texture!=null)UnityEngine.Object.DestroyImmediate(texture);
        }
        // A triangle can cross the panel without any vertex lying inside it: inspect actual projected triangles/halo.
        bool Overlaps(Vector2 a,Vector2 b,Vector2 c)
        {
            if(panel.Contains(a)||panel.Contains(b)||panel.Contains(c))return true;
            Vector2[] corners={panel.min,new Vector2(panel.xMin,panel.yMax),panel.max,new Vector2(panel.xMax,panel.yMin)};
            float Cross(Vector2 x,Vector2 y)=>x.x*y.y-x.y*y.x;
            foreach(var p in corners)
            {
                float x=Cross(b-a,p-a),y=Cross(c-b,p-b),z=Cross(a-c,p-c);
                if((x>=0&&y>=0&&z>=0)||(x<=0&&y<=0&&z<=0))return true;
            }
            Vector2[] ends={a,b,c,a};
            for(int i=0;i<3;i++)
            {
                Vector2 p=ends[i],d=ends[i+1]-p;float lo=0,hi=1;
                bool Clip(float v,float q){if(Mathf.Abs(v)<1e-6f)return q>=0;float t=q/v;if(v<0){if(t>hi)return false;lo=Mathf.Max(lo,t);}else{if(t<lo)return false;hi=Mathf.Min(hi,t);}return true;}
                if(Clip(-d.x,p.x-panel.xMin)&&Clip(d.x,panel.xMax-p.x)&&Clip(-d.y,p.y-panel.yMin)&&Clip(d.y,panel.yMax-p.y)&&hi-lo>1e-5f)return true;
            }
            return false;
        }
        bool MeshOverlaps()
        {
            var verts=Get<List<Vector3>>("vertices");
            foreach(string name in new[]{"triangles","haloTriangles"})
            {
                var indices=Get<List<int>>(name);
                for(int i=0;i<indices.Count;i+=3)if(Overlaps(Pixel(verts[indices[i]]),Pixel(verts[indices[i+1]]),Pixel(verts[indices[i+2]])))return true;
            }
            return false;
        }
        [TestCase(false)] [TestCase(true)]
        public void ActualThickStrokeAndHaloTrianglesAreSplitOutsideInspectedPanel(bool diagonal)
        {
            Vector2 a=new Vector2(300,diagonal?450:660),b=new Vector2(1300,diagonal?850:660);
            Call("Quad",Ray(a),Ray(b),Color.green,Color.cyan,2f,true);
            Assert.That(Get<List<Vector3>>("vertices").Count,Is.EqualTo(16),"Both visible pieces retain stroke+halo.");
            Assert.That(MeshOverlaps(),Is.False,"No triangle or halo may cross an opaque inspection panel, even when the attitude window falls back across it.");
        }
        [Test]public void ActualPitchLadderMeshAndNumeralSlotsAvoidPanel()
        {
            Call("DrawLadder",0f);
            Assert.That(Get<List<Vector3>>("vertices").Count,Is.GreaterThan(0));
            Assert.That((int)layerType.GetProperty("LastRungCount").GetValue(layer),Is.GreaterThan(0));
            Assert.That(MeshOverlaps(),Is.False);
            foreach(var label in layerHost.GetComponentsInChildren<TextMeshPro>(true))
            {
                if(!label.gameObject.activeSelf)continue;
                Assert.That(panel.Contains(Pixel(label.rectTransform.localPosition)),Is.False,label.text);
            }
        }
        [Test]public void ActualLabelCommitRejectsPanelOverlapButKeepsCriticalFlag()
        {
            Type zone=layerType.GetNestedType("Zone",Any);object window=Enum.Parse(zone,"Window"),flag=Enum.Parse(zone,"Flag");
            Assert.That((bool)Call("Label",0,"15",Ray(panel.center),Color.green,0f,window),Is.False);
            Assert.That(layerHost.GetComponentsInChildren<TextMeshPro>(true).Length,Is.Zero);
            Assert.That((bool)Call("Label",1,"10",Ray(new Vector2(350,660)),Color.green,0f,window),Is.True);
            Assert.That((bool)Call("Label",2,"ATT",Ray(panel.center),Color.red,0f,flag),Is.True);
            Assert.That(layerHost.GetComponentsInChildren<TextMeshPro>(true).Length,Is.EqualTo(2));
        }
        [Test]public void PanelMotionAndCloseInvalidateSignatureEvenWithinSameFrame()
        {
            Call("BuildSignature",new object[]{null});Assert.That((bool)Call("SignatureChanged"),Is.True);
            Call("BuildSignature",new object[]{null});Assert.That((bool)Call("SignatureChanged"),Is.False);
            Panel(new Rect(700,580,300,180));Call("BuildSignature",new object[]{null});Assert.That((bool)Call("SignatureChanged"),Is.True);
            Panel(panel,false);Call("BuildSignature",new object[]{null});Assert.That((bool)Call("SignatureChanged"),Is.True);
            Call("Quad",Ray(new Vector2(300,660)),Ray(new Vector2(1300,660)),Color.green,Color.green,.1f,true);
            Assert.That(MeshOverlaps(),Is.True,"Closing inspection restores the complete stroke immediately.");
        }
        [Test]public void NativeGeometryAndLabelsRetainTheirUnclippedRenderingPath()
        {
            Set("nativeXr",true);Panel(panel);
            Call("Quad",Ray(new Vector2(300,660)),Ray(new Vector2(1300,660)),Color.green,Color.green,.1f,true);
            Assert.That(Get<List<Vector3>>("vertices").Count,Is.EqualTo(8));Assert.That(MeshOverlaps(),Is.True);
            Type zone=layerType.GetNestedType("Zone",Any);
            Assert.That((bool)Call("Label",0,"15",Ray(panel.center),Color.green,0f,Enum.Parse(zone,"Window")),Is.True);
        }
        [Test]public void SplitGeographicStrokePreservesOriginalDepthSegmentAndAngularRay()
        {
            Vector3 a=Ray(new Vector2(300,660),700),b=Ray(new Vector2(1300,660),2200);
            Call("Quad",a,b,Color.green,Color.cyan,.5f,true);var v=Get<List<Vector3>>("vertices");
            Assert.That(v.Count,Is.EqualTo(16));Vector3 cut=(v[2]+v[3])*.5f;
            Assert.That(Vector3.Cross(b-a,cut-a).magnitude/(b-a).magnitude,Is.LessThan(.005f));
            Assert.That(MeshOverlaps(),Is.False);
        }
    }
}
