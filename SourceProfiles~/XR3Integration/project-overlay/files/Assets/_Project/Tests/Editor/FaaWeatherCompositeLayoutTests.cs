using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Build the real settings/conditions factories together, as the Windows radar does.</summary>
    public sealed class FaaWeatherCompositeLayoutTests
    {
        private const BindingFlags Any=BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        private static Type Runtime(string name)=>Type.GetType("FAA.Customization."+name+", Assembly-CSharp",true);

        private sealed class Fixture:IDisposable
        {
            public readonly GameObject CanvasHost,WorkspaceHost,OverlayHost;
            public readonly RectTransform Radar,Drawer,Conditions,Header,Footer;
            public readonly Component Overlay,Info,DrawerController;
            private readonly Component workspace;
            private readonly object previousWorkspace;
            private readonly PropertyInfo current=Runtime("FaaSpatialWorkspace").GetProperty("Current",Any);

            public Fixture(float scale,float pivot)
            {
                CanvasHost=new GameObject("Actual weather canvas",typeof(RectTransform),typeof(Canvas));
                var canvasRect=(RectTransform)CanvasHost.transform;
                canvasRect.sizeDelta=new Vector2(1920f,1080f);
                canvasRect.localPosition=new Vector3(1000f,800f,20000f);
                canvasRect.localScale=Vector3.one*.001f;
                CanvasHost.GetComponent<Canvas>().renderMode=RenderMode.WorldSpace;
                var root=new GameObject("X-Plane Weather Radar System",typeof(RectTransform));
                Radar=(RectTransform)root.transform;Radar.SetParent(canvasRect,false);
                Radar.anchorMin=Radar.anchorMax=new Vector2(.5f,.5f);Radar.pivot=new Vector2(pivot,pivot);
                Radar.sizeDelta=new Vector2(296f,296f);Radar.anchoredPosition=new Vector2(-100f,-200f);
                var presentation=root.AddComponent(Runtime("FaaRadarPresentation"));
                Runtime("FaaRadarPresentation").GetMethod("Configure",Any).Invoke(presentation,
                    new object[]{Enum.Parse(Runtime("FaaRadarKind"),"Weather"),null});
                Runtime("FaaRadarPresentation").GetField("_scale",Any).SetValue(presentation,scale);
                Runtime("FaaRadarPresentation").GetMethod("LayoutChrome",Any).Invoke(presentation,null);
                Header=(RectTransform)Radar.Find("Radar Status Header");Footer=(RectTransform)Radar.Find("Radar Status Footer");

                WorkspaceHost=new GameObject("Inactive weather workspace");WorkspaceHost.SetActive(false);
                workspace=WorkspaceHost.AddComponent(Runtime("FaaSpatialWorkspace"));
                Runtime("FaaSpatialWorkspace").GetProperty("PersistChanges",Any).SetValue(workspace,false);
                previousWorkspace=current.GetValue(null);current.SetValue(null,workspace);WorkspaceHost.SetActive(true);
                object panel=Activator.CreateInstance(Runtime("FaaSpatialRadarPanel"),
                    new object[]{workspace,"weather",CanvasHost.GetComponent<Canvas>(),Radar,-95f,false,.42f});
                Runtime("FaaSpatialRadarPanel").GetMethod("SetSpatial",Any).Invoke(panel,new object[]{true});
                ((IList)Runtime("FaaSpatialWorkspace").GetField("panels",Any).GetValue(workspace)).Add(panel);

                OverlayHost=new GameObject("Inactive actual radar overlay");OverlayHost.SetActive(false);
                Overlay=OverlayHost.AddComponent(Runtime("FaaRadarControlsOverlay"));
                Set("_weatherRoot",Radar);
                Drawer=(RectTransform)Call("EnsureStrip",Radar,"WeatherControlStrip",new Vector2(476f,288f));
                Call("EnsureReadableWeatherControls",Drawer);
                DrawerController=Drawer.gameObject.AddComponent(Runtime("FaaRadarConfigurationDrawer"));
                Runtime("FaaRadarConfigurationDrawer").GetMethod("Configure",Any).Invoke(DrawerController,new object[]{false});
                Runtime("FaaRadarConfigurationDrawer").GetProperty("BaseScale",Any).SetValue(DrawerController,scale);
                Set("_weatherStrip",Drawer);Set("_weatherDrawer",DrawerController);
                Info=(Component)Call("EnsureWeatherConditionsStrip",Radar,Drawer);Conditions=(RectTransform)Info.transform;
                Set("_weatherConditionsStrip",Info);
                Runtime("XPlaneWeatherInfoStrip").GetMethod("SetExpanded",Any).Invoke(Info,new object[]{true,true});
                Canvas.ForceUpdateCanvases();
            }

            private void Set(string name,object value)=>Runtime("FaaRadarControlsOverlay").GetField(name,Any).SetValue(Overlay,value);
            public object Call(string name,params object[] args)=>Runtime("FaaRadarControlsOverlay").GetMethod(name,Any).Invoke(Overlay,args);
            public void Position()=>Runtime("XPlaneWeatherInfoStrip").GetMethod("PositionToRightOfRadar",Any).Invoke(Info,null);
            public Rect Bounds(RectTransform rect)
            {
                Bounds b=RectTransformUtility.CalculateRelativeRectTransformBounds(CanvasHost.transform,rect);
                return Rect.MinMaxRect(b.min.x,b.min.y,b.max.x,b.max.y);
            }
            public void AssertDisjoint()
            {
                Rect conditions=Bounds(Conditions),drawer=Bounds(Drawer),header=Bounds(Header),footer=Bounds(Footer),scope=Bounds(Radar);
                // Radar's descendant-inclusive bound also includes chrome, but not the sibling drawer/conditions.
                Assert.That(conditions.Overlaps(drawer),Is.False,"Conditions must not obscure gain, help or any settings card.");
                Assert.That(conditions.Overlaps(header),Is.False,"Conditions must clear the actual readability-scaled header.");
                Assert.That(conditions.Overlaps(footer),Is.False,"Conditions must clear the actual readability-scaled footer.");
                Assert.That(conditions.Overlaps(scope),Is.False,"Conditions must remain beside the original radar picture.");
                Assert.That(drawer.Overlaps(header),Is.False,"The original settings factory must keep its drawer above the shared header.");
                Assert.That(drawer.Overlaps(footer),Is.False);
            }
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(OverlayHost);
                // These panels and the canvas are test-owned; dispose them with the Editor's immediate path.
                ((IList)Runtime("FaaSpatialWorkspace").GetField("panels",Any).GetValue(workspace)).Clear();
                UnityEngine.Object.DestroyImmediate(WorkspaceHost);
                current.SetValue(null,previousWorkspace);
                UnityEngine.Object.DestroyImmediate(CanvasHost);
            }
        }

        [TestCase(1f,0f)]
        [TestCase(1f,.5f)]
        [TestCase(2f,0f)]
        [TestCase(2f,.5f)]
        public void RealWeatherFactoriesKeepConditionsSettingsAndScopeChromePairwiseDisjoint(float scale,float pivot)
        {
            using(var f=new Fixture(scale,pivot))
            {
                Vector2 position=f.Radar.anchoredPosition,size=f.Radar.sizeDelta;Vector3 localScale=f.Radar.localScale;
                f.Position();f.AssertDisjoint();
                Assert.That(f.Radar.anchoredPosition,Is.EqualTo(position));Assert.That(f.Radar.sizeDelta,Is.EqualTo(size));
                Assert.That(f.Radar.localScale,Is.EqualTo(localScale),"Only sibling UI is docked; the authored scope stays unchanged.");
            }
        }

        [Test]
        public void OpeningDrawerReservesItsFinalSizeWithoutMovingConditionsThroughSettings()
        {
            using(var f=new Fixture(2f,.5f))
            {
                Type drawer=Runtime("FaaRadarConfigurationDrawer");
                drawer.GetMethod("Apply",Any).Invoke(f.DrawerController,new object[]{.1f});
                f.Position();Vector2 opening=f.Conditions.anchoredPosition;
                drawer.GetMethod("Apply",Any).Invoke(f.DrawerController,new object[]{1f});
                f.Position();
                Assert.That(Vector2.Distance(opening,f.Conditions.anchoredPosition),Is.LessThan(.05f),"Reserve the expanded drawer throughout its opening animation.");
                f.AssertDisjoint();
            }
        }
    }
}
