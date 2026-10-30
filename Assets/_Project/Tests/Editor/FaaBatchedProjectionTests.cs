using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using Object=UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaBatchedProjectionTests
    {
        const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        delegate bool Bounds(Camera view,out Rect bounds);
        static Type T(string name,string assembly="Assembly-CSharp")=>Type.GetType(name+", "+assembly,true);
        static Type Optional(string name)=>Type.GetType(name+", Assembly-CSharp",false);
        static Type Projection=>T("IndicatorSystem.Core.IndicatorCanvasProjection");
        static Type Space=>T("AircraftControl.Camera.AircraftViewRenderSpace","AircraftControl");
        static bool Batch(RectTransform rect,Camera view,out Rect bounds,bool reject=false)
        {object[] args={rect,rect.rect,view,null,reject};bool ok=(bool)Projection.GetMethod("TryProjectLocalBounds").Invoke(null,args);bounds=(Rect)args[3];return ok;}
        static Bounds Module(Transform target)
        {object module=Activator.CreateInstance(T("FAA.Customization.FaaNonConformalScaleTarget"),new object[]{"probe","probe",target,1f,0f});return(Bounds)Delegate.CreateDelegate(typeof(Bounds),module,T("FAA.Customization.FaaNonConformalScaleTarget").GetMethod("TryScreenBounds"));}
        static Rect Truth(RectTransform rect,Camera view)
        {var points=new Vector3[4];rect.GetLocalCorners(points);Vector2 min=Vector2.positiveInfinity,max=Vector2.negativeInfinity;foreach(var p in points){Vector3 s=view.WorldToScreenPoint(rect.TransformPoint(p));min=Vector2.Min(min,s);max=Vector2.Max(max,s);}return Rect.MinMaxRect(min.x,min.y,max.x,max.y);}
        static void Equal(Rect a,Rect b,float epsilon=.04f)
        {Assert.That(a.xMin,Is.EqualTo(b.xMin).Within(epsilon));Assert.That(a.yMin,Is.EqualTo(b.yMin).Within(epsilon));Assert.That(a.xMax,Is.EqualTo(b.xMax).Within(epsilon));Assert.That(a.yMax,Is.EqualTo(b.yMax).Within(epsilon));}
        static Rect Pad(Rect r,float p)=>Rect.MinMaxRect(r.xMin-p,r.yMin-p,r.xMax+p,r.yMax+p);
        sealed class Fixture:IDisposable
        {
            readonly IDisposable native;
            public readonly GameObject Root,Child;public readonly Camera View;
            public Fixture()
            {Type type=typeof(FaaNearSpaceFastPathTests).GetNestedType("Fixture",BindingFlags.NonPublic);object instance=Activator.CreateInstance(type,new object[]{true});native=(IDisposable)instance;Root=(GameObject)type.GetField("Root").GetValue(instance);Child=(GameObject)type.GetField("Child").GetValue(instance);View=(Camera)type.GetField("View").GetValue(instance);Child.transform.localPosition=Vector3.zero;Child.transform.localScale=Vector3.one;Child.transform.localRotation=Quaternion.Euler(0,0,45);((RectTransform)Child.transform).sizeDelta=new Vector2(200,200);Child.AddComponent<Image>().color=Color.green;}
            public void Dispose()=>native.Dispose();
        }
        [SetUp]public void Empty(){if(!Application.isPlaying)EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);}
        [TestCase(0)][TestCase(1)][TestCase(2)]
        public void FourCornerBoundsCoverDiagonalCollapseShearNegativeScaleAndAsymmetricLens(int kind)
        {
            using(var f=new Fixture())
            {
                if(kind==1)f.Root.transform.localScale=new Vector3(-.001f,.001f,.001f);
                if(kind==2)
                {var parent=new GameObject("Sheared UI ancestry",typeof(RectTransform));parent.transform.SetParent(f.Root.transform,false);parent.transform.localScale=new Vector3(1.4f,.6f,1);parent.transform.localRotation=Quaternion.Euler(0,0,37);f.Child.transform.SetParent(parent.transform,false);f.Child.transform.localRotation=Quaternion.Euler(0,0,-20);Matrix4x4 p=f.View.projectionMatrix;p.m02=.17f;p.m12=-.08f;f.View.projectionMatrix=p;}
                var rt=(RectTransform)f.Child.transform;Assert.That(Batch(rt,f.View,out Rect body),Is.True);Equal(body,Truth(rt,f.View));
                if(Optional("FAA.Customization.FaaNonConformalScaleTarget")!=null)
                {Assert.That(Module(rt)(f.View,out Rect bounds),Is.True);Equal(bounds,Pad(body,8));}
                if(kind==0)
                {var corners=new Vector3[4];rt.GetLocalCorners(corners);FAA.NearSpaceBaseline.IndicatorCanvasProjection.TryProjectLocal(rt,corners[0],f.View,out var a);FAA.NearSpaceBaseline.IndicatorCanvasProjection.TryProjectLocal(rt,corners[2],f.View,out var b);Assert.That(body.width,Is.GreaterThan(Mathf.Abs(b.x-a.x)+30),"The previous diagonal pair underestimates this45-degree square.");}
            }
        }
        [Test]
        public void RegionUsesFourCornerBatchWithExistingPaddingAndBehindPolicy()
        {
            if(Optional("FAA.Customization.FaaHudKeepOutRegion")==null)Assert.Ignore("The optional full-profile keep-out region is not imported by the standard profile.");
            using(var f=new Fixture())
            {
                var rt=(RectTransform)f.Child.transform;Component region=f.Child.AddComponent(T("FAA.Customization.FaaHudKeepOutRegion"));region.GetType().GetField("padding",Any).SetValue(region,6f);region.GetType().GetMethod("RefreshParents",Any).Invoke(region,null);
                object[] args={null};Assert.That((bool)region.GetType().GetMethod("TryGetScreenRect").Invoke(region,args),Is.True);Equal((Rect)args[0],Pad(Truth(rt,f.View),6));
                f.Root.transform.localPosition=new Vector3(0,0,-1.5f);Assert.That(Batch(rt,f.View,out _,false),Is.True);Assert.That(Batch(rt,f.View,out _,true),Is.False);
                args[0]=null;Assert.That((bool)region.GetType().GetMethod("TryGetScreenRect").Invoke(region,args),Is.False);
            }
        }
        [Test]
        public void FarFallbackRetainsItsContractWithOptionalModuleVisibility()
        {
            using(var f=new Fixture())
            {
                var group=f.Child.AddComponent<CanvasGroup>();Bounds module=Optional("FAA.Customization.FaaNonConformalScaleTarget")!=null?Module(f.Child.transform):null;
                if(module!=null){group.alpha=0;Assert.That(module(f.View,out _),Is.False);group.alpha=1;}
                Space.GetMethod("ReleaseRenderRoot").Invoke(null,new object[]{f.Root.transform,f.View});
                Assert.That(Batch((RectTransform)f.Child.transform,f.View,out var actual),Is.True);Equal(actual,Truth((RectTransform)f.Child.transform,f.View));
                f.Child.SetActive(false);if(module!=null)Assert.That(module(f.View,out _),Is.False);
            }
        }

        // Exact legacy two-corner loop and a correctness-only four-corner variant,
        // both using the archived manual projection helper. No reflection inside timings.
        sealed class LegacyBounds
        {
            readonly Transform target;Graphic[] graphics;int hierarchyCount;readonly CanvasGroup[] visibility;readonly bool four;readonly Vector3[] corners=new Vector3[4];
            public LegacyBounds(Transform root,bool allFour){target=root;graphics=root.GetComponentsInChildren<Graphic>(true);hierarchyCount=root.hierarchyCount;visibility=root.GetComponentsInParent<CanvasGroup>(true);four=allFour;}
            static bool FixedCamera(Graphic graphic,Camera view,out Rect bounds)
            {
                bounds=default;if(view.stereoEnabled||UnityEngine.XR.XRSettings.isDeviceActive)return false;
                Canvas root=graphic.canvas.rootCanvas;if(root==null||root.renderMode!=RenderMode.ScreenSpaceCamera||!(root.transform is RectTransform frame))return false;
                Rect logical=frame.rect,pixels=root.pixelRect;
                if(logical.width<=0||logical.height<=0||pixels.width<=0||pixels.height<=0||!FAA.NearSpaceBaseline.FaaCanvasLocalGeometry.TryMatrix(graphic.transform,frame,out var matrix))return false;
                Rect local=FAA.NearSpaceBaseline.FaaCanvasLocalGeometry.TransformRect(graphic.rectTransform.rect,matrix);
                bounds=Rect.MinMaxRect(pixels.xMin+(local.xMin-logical.xMin)*pixels.width/logical.width,pixels.yMin+(local.yMin-logical.yMin)*pixels.height/logical.height,pixels.xMin+(local.xMax-logical.xMin)*pixels.width/logical.width,pixels.yMin+(local.yMax-logical.yMin)*pixels.height/logical.height);return true;
            }
            public bool Get(Camera view,out Rect bounds)
            {
                bounds=default;if(target==null||!target.gameObject.activeInHierarchy||view==null)return false;
                int count=target.hierarchyCount;if(count!=hierarchyCount){hierarchyCount=count;graphics=target.GetComponentsInChildren<Graphic>(true);}
                foreach(var group in visibility)if(group!=null&&group.isActiveAndEnabled&&group.alpha<=.001f)return false;
                bool any=false;Vector2 min=Vector2.positiveInfinity,max=Vector2.negativeInfinity;
                foreach(var g in graphics)
                {
                    if(g==null||!g.isActiveAndEnabled||g.color.a<=.001f||g.canvas==null||!g.canvas.isActiveAndEnabled)continue;
                    if(FixedCamera(g,view,out Rect fixedBounds))throw new InvalidOperationException("Benchmark must bypass desktop fixed-camera path");
                    Camera camera=g.canvas.renderMode==RenderMode.ScreenSpaceOverlay?null:g.canvas.worldCamera;
                    AssertWorld(g.canvas);g.rectTransform.GetLocalCorners(corners);
                    Vector2 a=Vector2.positiveInfinity,b=Vector2.negativeInfinity;bool valid=true;
                    for(int n=0;n<(four?4:2);n++)
                    {int i=four?n:n*2;if(!FAA.NearSpaceBaseline.IndicatorCanvasProjection.TryProjectLocal(g.rectTransform,corners[i],camera,out var p)){valid=false;break;}a=Vector2.Min(a,p);b=Vector2.Max(b,p);}
                    if(!valid||Mathf.Abs(b.x-a.x)>view.pixelWidth||Mathf.Abs(b.y-a.y)>view.pixelHeight)continue;
                    min=Vector2.Min(min,a);max=Vector2.Max(max,b);any=true;
                }
                if(!any)return false;bounds=Rect.MinMaxRect(min.x-8,min.y-8,max.x+8,max.y+8);return bounds.width>1&&bounds.height>1;
            }
            static void AssertWorld(Canvas canvas){if(canvas.rootCanvas.renderMode!=RenderMode.WorldSpace)throw new InvalidOperationException("Benchmark must execute actualMRWorldSpace branch");}
        }
        [Serializable]sealed class Sample{public int frame;public double legacyTwo,legacyFour,batchedFour;}
        [Serializable]sealed class Report{public string engine,graphics,qualification;public int registeredRoots,frames,graphicsPerModule,repetitions;public double legacyTwoMedian,legacyFourMedian,batchedFourMedian;public long proofViewCaptures;public Sample[] samples;}
        static float sink;
        static double Time(Bounds method,Camera view,int repetitions)
        {long start=Stopwatch.GetTimestamp();for(int i=0;i<repetitions;i++){if(!method(view,out var b))throw new Exception("Bound measurement failed");sink=b.xMin+b.width;}return(Stopwatch.GetTimestamp()-start)*1000000.0/Stopwatch.Frequency/repetitions;}
        static double Median(List<Sample> rows,int arm){var values=new List<double>();foreach(var row in rows)values.Add(arm==0?row.legacyTwo:arm==1?row.legacyFour:row.batchedFour);values.Sort();return values[values.Count/2];}
        [UnityTest,Explicit("Representative24-root actualMRbounds three-arm dynamic benchmark")]
        public IEnumerator TwentyFourRootsCompareLegacyTwoCorrectFourAndActualBatchedModuleBounds()
        {
            if(Optional("FAA.Customization.FaaNonConformalScaleTarget")==null)Assert.Ignore("The actual-module benchmark requires the full XR integration consumer; shared geometry is measured by the separate 24-root benchmark.");
            if(SystemInfo.graphicsDeviceType==UnityEngine.Rendering.GraphicsDeviceType.Null)Assert.Ignore("Enabled-camera fixture requires graphics");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);yield return new EnterPlayMode();
            Type sceneType=typeof(FaaNearSpaceFastPathGpuTests).GetNestedType("Scene",BindingFlags.NonPublic);object scene=Activator.CreateInstance(sceneType,true);
            Camera view=(Camera)sceneType.GetField("View").GetValue(scene);GameObject root=(GameObject)sceneType.GetField("CanvasHost").GetValue(scene);
            var extra=new List<GameObject>();var rows=new List<Sample>();const int frames=24,repeats=4,count=24;long captures=0;
            var group=new GameObject("Measured module",typeof(RectTransform),typeof(CanvasGroup));group.layer=30;group.transform.SetParent(root.transform,false);
            var rects=new RectTransform[count];
            try
            {
                for(int n=0;n<count;n++){var g=new GameObject("Dynamic graphic "+n,typeof(RectTransform),typeof(CanvasRenderer),typeof(Image));g.layer=30;g.transform.SetParent(group.transform,false);rects[n]=(RectTransform)g.transform;rects[n].sizeDelta=new Vector2(90,36);}
                Space.GetMethod("ReleaseRenderRoot").Invoke(null,new object[]{root.transform,view});
                for(int n=0;n<23;n++){var g=new GameObject("Other registered canvas "+n,typeof(RectTransform),typeof(Canvas));g.transform.SetParent(view.transform,false);g.transform.localPosition=new Vector3(0,0,1.5f);g.transform.localScale=Vector3.one*.001f;g.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;Space.GetMethod("RegisterRenderRoot").Invoke(null,new object[]{g.transform,view});extra.Add(g);}
                Space.GetMethod("RegisterRenderRoot").Invoke(null,new object[]{root.transform,view});
                Assert.That(((System.Collections.IDictionary)Space.GetField("renderRoots",Any).GetValue(null)).Count,Is.EqualTo(24));
                Bounds two=new LegacyBounds(group.transform,false).Get,four=new LegacyBounds(group.transform,true).Get,batch=Module(group.transform);
                Space.GetProperty("DiagnosticsEnabled").SetValue(null,false);
                for(int frame=-3;frame<frames;frame++)
                {
                    sceneType.GetMethod("Move").Invoke(scene,new object[]{frame});float t=frame*.13f;
                    group.transform.localScale=Vector3.one;group.transform.localRotation=Quaternion.identity;
                    for(int n=0;n<count;n++){rects[n].anchoredPosition=new Vector2((n%6-2.5f)*130+Mathf.Sin(t+n)*7,(n/6-1.5f)*90+Mathf.Cos(t+n)*5);rects[n].localRotation=Quaternion.Euler(0,0,frame==0?0:Mathf.Sin(t+n)*35);rects[n].localScale=new Vector3(1+Mathf.Sin(t+n)*.03f,1,1);}
                    Canvas.ForceUpdateCanvases();yield return null;yield return null;
                    Assert.That(four(view,out var correct),Is.True);Assert.That(batch(view,out var current),Is.True);Equal(current,correct);
                    if(frame==0){Assert.That(two(view,out var old),Is.True);Equal(current,old);}
                    var row=new Sample{frame=frame};
                    if((frame&1)==0){row.legacyTwo=Time(two,view,repeats);row.legacyFour=Time(four,view,repeats);row.batchedFour=Time(batch,view,repeats);}else{row.batchedFour=Time(batch,view,repeats);row.legacyFour=Time(four,view,repeats);row.legacyTwo=Time(two,view,repeats);}
                    Space.GetProperty("DiagnosticsEnabled").SetValue(null,true);Space.GetMethod("ResetGeometryDiagnostics").Invoke(null,null);Assert.That(batch(view,out _),Is.True);captures+=(long)Space.GetProperty("NearViewMatrixHits").GetValue(null);Space.GetProperty("DiagnosticsEnabled").SetValue(null,false);
                    if(frame>=0)rows.Add(row);
                }
                var report=new Report{engine=Application.unityVersion,graphics=SystemInfo.graphicsDeviceName,qualification="MacEditor actualWorldSpace modulebounds;24registeredroots/24dynamicGraphics, normalenabledcamera; legacy2mayunderestimate rotatedbounds, correct4andbatched4equivalent; noWindowsFPS/crashclaim.",registeredRoots=24,frames=frames,graphicsPerModule=count,repetitions=repeats,legacyTwoMedian=Median(rows,0),legacyFourMedian=Median(rows,1),batchedFourMedian=Median(rows,2),proofViewCaptures=captures,samples=rows.ToArray()};
                string path=Path.GetFullPath(Path.Combine(Application.dataPath,"../../validation-near-fastpath"));Directory.CreateDirectory(path);File.WriteAllText(Path.Combine(path,"actual-module-bounds-24roots.json"),JsonUtility.ToJson(report,true));
                Assert.That(captures,Is.EqualTo(27*count),"One captured helper view perGraphic, not one percorner");TestContext.WriteLine("Module microseconds legacy2="+report.legacyTwoMedian+" correct4="+report.legacyFourMedian+" batch4="+report.batchedFourMedian);
            }
            finally{Space.GetProperty("DiagnosticsEnabled").SetValue(null,false);foreach(var g in extra)Object.DestroyImmediate(g);Object.DestroyImmediate(group);((IDisposable)scene).Dispose();}
            yield return new ExitPlayMode();
        }
    }
}
