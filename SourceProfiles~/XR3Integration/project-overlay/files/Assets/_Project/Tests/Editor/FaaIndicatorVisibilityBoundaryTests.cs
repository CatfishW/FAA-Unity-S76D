using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public sealed class FaaIndicatorVisibilityBoundaryTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type T(string name) => Type.GetType(name + ", Assembly-CSharp", true);
        private static void Set(object value, string name, object field) => value.GetType().GetField(name, Any).SetValue(value, field);
        private static object Get(object value, string name) => value.GetType().GetField(name, Any).GetValue(value);
        private static object Call(object value, string name, params object[] args) =>
            (value is Type type ? type : value.GetType()).GetMethods(Any).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(value is Type ? null : value, args);

        private sealed class Fixture : IDisposable
        {
            public readonly GameObject Host, ViewHost, CanvasHost;
            public readonly Component Controller;
            public readonly Camera View;
            public readonly Canvas Canvas;
            public readonly ScriptableObject Settings;
            public readonly RenderTexture Texture;
            public readonly object Target;
            public Rect Region => (Rect)Call(T("IndicatorSystem.Controller.IndicatorSystemController"), "GetProjectionRect", Canvas, View);
            public Fixture(bool customViewport = false, bool asymmetric = false, bool worldHud = false)
            {
                ViewHost = new GameObject("Boundary projection camera", typeof(Camera)); View = ViewHost.GetComponent<Camera>(); View.enabled = false;
                Texture = new RenderTexture(1600, 900, 0); View.targetTexture = Texture;
                if (customViewport) View.rect = new Rect(.15f, .1f, .7f, .8f);
                if (asymmetric) { var projection = Matrix4x4.Perspective(65, 1600f / 900f, .1f, 1000); projection.m02 = .17f; projection.m12 = -.09f; View.projectionMatrix = projection; }
                CanvasHost = new GameObject("Actual cue physical HUD", typeof(RectTransform), typeof(Canvas)); Canvas = CanvasHost.GetComponent<Canvas>();
                Canvas.renderMode = worldHud ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay; Canvas.worldCamera = View;
                if (worldHud)
                {
                    ((RectTransform)Canvas.transform).sizeDelta = new Vector2(960, 600);
                    Canvas.transform.localScale = Vector3.one * .01f; Canvas.transform.position = new Vector3(0, 0, 10);
                }
                Host = new GameObject("Inactive actual indicator owner"); Host.SetActive(false);
                Controller = Host.AddComponent(T("IndicatorSystem.Controller.IndicatorSystemController"));
                Settings = ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings"));
                Set(Settings, "usePilotCueStyle", true); Set(Settings, "useCustomPrefabs", false); Set(Settings, "globalScale", 1f); Set(Settings, "globalOpacity", 1f);
                Set(Controller, "settings", Settings); Set(Controller, "targetCanvas", Canvas); Set(Controller, "targetCamera", View); Call(Controller, "Initialize");
                // The real controls restore pilot preferences during Initialize. Configure only this
                // temporary settings object afterwards; never write/delete the user's PlayerPrefs.
                Set(Settings, "enabled", true); Set(Settings, "showTrafficIndicators", true);
                Set(Settings, "minDisplayDistance", 0f); Set(Settings, "maxDisplayDistance", 100f);
                Target = Activator.CreateInstance(T("IndicatorSystem.Integration.TrafficIndicatorTarget"));
                Set(Target, "id", "XPL01"); Set(Target, "label", "XPL01"); Set(Target, "distanceNM", 19f);
            }
            public object Sample(float x, float y, float depth = 100f)
            {
                Set(Target, "worldPosition", View.ScreenToWorldPoint(new Vector3(x, y, depth)));
                Call(Controller, "AddOrUpdateTarget", Target); Call(Controller, "UpdateIndicators");
                var data = (IList)Get(Controller, "_indicatorDataList"); return data.Count == 0 ? null : data[0];
            }
            public string Visibility(float x, float y, float depth = 100f) => State(Sample(x, y, depth));
            public static string State(object data) => data == null ? "Inactive" : Get(data, "Visibility").ToString();
            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(Host); UnityEngine.Object.DestroyImmediate(CanvasHost); UnityEngine.Object.DestroyImmediate(Settings);
                View.targetTexture = null; UnityEngine.Object.DestroyImmediate(ViewHost); UnityEngine.Object.DestroyImmediate(Texture);
            }
        }

        [TestCase(false, false, false)] [TestCase(true, false, false)]
        [TestCase(false, true, false)] [TestCase(true, true, false)]
        [TestCase(false, false, true)] [TestCase(true, true, true)]
        public void ActualConsumerKeepsEdgePresentationThroughSmallBoundaryOscillation(bool customViewport, bool asymmetric, bool worldHud)
        {
            using (var f = new Fixture(customViewport, asymmetric, worldHud))
            {
                Rect region = f.Region; float x = region.xMin, y = region.center.y;
                Assert.That(f.Visibility(region.center.x, y), Is.EqualTo("OnScreen"), "Centered positive control: the fixture must be eligible before boundary checks.");
                Assert.That(f.Visibility(x - 24f, y), Is.EqualTo("OffScreen"), "Outside positive control: a real projected target must reach the controller.");
                Assert.That(f.Visibility(x - 1, y), Is.EqualTo("OffScreen"));
                for (int frame = 0; frame < 120; frame++)
                    Assert.That(f.Visibility(x + (frame % 2 == 0 ? 1f : -1f), y), Is.EqualTo("OffScreen"), "Tiny crossing must not repeatedly replace the edge glyph/readout.");
                Assert.That(f.CanvasHost.GetComponentsInChildren<Text>(true).Any(t => t.text.Contains("OUTSIDE VIEW")), Is.True, "The actual PilotIndicatorCue receives the stable state.");
                object acquired = f.Sample(x + 24f, y);
                Assert.That(Fixture.State(acquired), Is.EqualTo("OnScreen"));
                Assert.That(Vector2.Distance((Vector2)Get(acquired, "ScreenPosition"), new Vector2(x + 24f, y)), Is.LessThan(.01f), "Acquired target uses its exact projection.");
                Assert.That(f.Visibility(x - 1, y), Is.EqualTo("OffScreen"), "Leaving the real frame must be immediate.");
            }
        }

        [Test]
        public void ActualSevenFlipStyleSequenceDropsShortReentriesButPreservesARealCrossing()
        {
            using (var f = new Fixture())
            {
                float x = f.Region.xMin, y = f.Region.center.y;
                Assert.That(f.Visibility(f.Region.center.x, y), Is.EqualTo("OnScreen"), "Centered eligibility positive control.");
                Assert.That(f.Visibility(x - 24f, y), Is.EqualTo("OffScreen"), "Outside eligibility positive control.");
                float[] input = { -1, 1, 2, 1, -1, -1, 1, 4, 12, 16, 12, 4, -1, 1, 3, 1, -1, 1 };
                string previous = null; int changes = 0;
                foreach (float offset in input)
                {
                    string state = f.Visibility(x + offset, y);
                    if (previous != null && state != previous) changes++;
                    previous = state;
                }
                Assert.That(changes, Is.EqualTo(2), "Only the deliberate deep re-entry and its real exit should replace the edge presentation.");
            }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void EveryRealFrameEdgeLeavesImmediatelyAndReentersOnlyInsideTheBand(int edge)
        {
            using (var f = new Fixture(true, true))
            {
                Rect r = f.Region;
                Vector2 At(float inward) => edge == 0 ? new Vector2(r.xMin + inward, r.center.y) : edge == 1 ? new Vector2(r.xMax - inward, r.center.y) :
                    edge == 2 ? new Vector2(r.center.x, r.yMin + inward) : new Vector2(r.center.x, r.yMax - inward);
                foreach (var sample in new[] { (24f,"OnScreen"),(-1f,"OffScreen"),(1f,"OffScreen"),(24f,"OnScreen") })
                { Vector2 p = At(sample.Item1); Assert.That(f.Visibility(p.x,p.y),Is.EqualTo(sample.Item2)); }
            }
        }

        [Test]
        public void TargetRemovalProviderClearAndViewOwnershipChangesDoNotReuseOldHistory()
        {
            using (var f = new Fixture())
            {
                float x = f.Region.xMin, y = f.Region.center.y;
                void OffThenNear() { f.Sample(x - 1,y); Assert.That(f.Visibility(x + 1,y),Is.EqualTo("OffScreen")); }
                OffThenNear(); Call(f.Controller,"RemoveTarget","XPL01"); Assert.That(f.Visibility(x + 1,y),Is.EqualTo("OnScreen"));
                OffThenNear(); Call(f.Controller,"ClearAll"); Assert.That(f.Visibility(x + 1,y),Is.EqualTo("OnScreen"));
                OffThenNear(); Call(f.Controller,"SetTargetsForType",Enum.Parse(T("IndicatorSystem.Core.IndicatorType"),"Traffic"),null);
                Assert.That(f.Visibility(x + 1,y),Is.EqualTo("OnScreen"),"Provider disconnect/stale feed clears tracks through this existing contract.");
                OffThenNear(); Set(f.Target,"label","NEW IDENTITY"); Assert.That(f.Visibility(x + 1,y),Is.EqualTo("OnScreen"));
                OffThenNear(); var alternateHost=new GameObject("New view owner",typeof(Camera));var alternate=alternateHost.GetComponent<Camera>();alternate.enabled=false;alternate.targetTexture=f.Texture;
                try { Set(f.Controller,"targetCamera",alternate); Assert.That(f.Visibility(x + 1,y),Is.EqualTo("OnScreen")); }
                finally { Set(f.Controller,"targetCamera",f.View);alternate.targetTexture=null;UnityEngine.Object.DestroyImmediate(alternateHost); }
            }
        }

        [Test]
        public void BehindAndInvalidTargetsAreNeverHeldOnscreen()
        {
            using(var f=new Fixture(true,true))
            {
                Vector2 centre=f.Region.center;
                Assert.That(f.Visibility(centre.x,centre.y),Is.EqualTo("OnScreen"));
                Assert.That(f.Visibility(centre.x,centre.y,-100),Is.EqualTo("Behind"));
                Set(f.Target,"worldPosition",new Vector3(float.NaN,0,100));Call(f.Controller,"AddOrUpdateTarget",f.Target);Call(f.Controller,"UpdateIndicators");
                Assert.That(((IList)Get(f.Controller,"_indicatorDataList")).Count,Is.Zero);
                Assert.That(f.Visibility(centre.x,centre.y),Is.EqualTo("OnScreen"));
            }
        }
        [Test]
        public void ReentryInsetUsesActualViewportIntersectionAndRemainsBoundedForTinyFrames()
        {
            using(var f=new Fixture(true,true))
            {
                Type calculator=T("IndicatorSystem.Core.ScreenIndicatorCalculator");
                object history=Activator.CreateInstance(calculator.GetNestedType("VisibilityHistory"));
                object config=Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorEdgeConfig"));Set(config,"MaxDisplayDistance",80f);Set(config,"EdgePadding",5f);
                Rect view=f.View.pixelRect;Rect region=new Rect(view.xMin-20,view.center.y-50,60,100);
                object Stable(float x,float y,float inset)
                {
                    Set(f.Target,"worldPosition",f.View.ScreenToWorldPoint(new Vector3(x,y,100)));
                    return Call(calculator,"CalculateIndicatorWithVisibilityHistory",f.Target,f.View,config,region,history,inset);
                }
                Assert.That(Fixture.State(Stable(view.xMin-1,view.center.y,10000)),Is.EqualTo("OffScreen"));
                Assert.That(Fixture.State(Stable(view.xMin+2,view.center.y,10000)),Is.EqualTo("OffScreen"));
                Assert.That(Fixture.State(Stable(view.xMin+6,view.center.y,10000)),Is.EqualTo("OnScreen"),"10% of40px effective width is4px, not10% of the wider supplied rectangle.");
                region=new Rect(view.center.x,view.center.y,20,10);history=Activator.CreateInstance(calculator.GetNestedType("VisibilityHistory"));
                Assert.That(Fixture.State(Stable(region.xMin-1,region.center.y,10000)),Is.EqualTo("OffScreen"));
                Assert.That(Fixture.State(Stable(region.xMin+2,region.center.y,10000)),Is.EqualTo("OnScreen"),"A large requested inset must not make the whole small frame unreachable.");
            }
        }

        [Test]
        public void ExistingStatelessApiStillProjectsExactEdgeTargetsWithoutHistory()
        {
            using(var f=new Fixture(true,true,true))
            {
                Type calculator=T("IndicatorSystem.Core.ScreenIndicatorCalculator");object config=Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorEdgeConfig"));Set(config,"MaxDisplayDistance",80f);Set(config,"EdgePadding",30f);
                Rect region=f.Region;Vector2 point=new Vector2(region.xMin+1,region.center.y);
                Set(f.Target,"worldPosition",f.View.ScreenToWorldPoint(new Vector3(point.x,point.y,100)));
                object exact=Call(calculator,"CalculateIndicatorInRect",f.Target,f.View,config,region);
                Assert.That(Fixture.State(exact),Is.EqualTo("OnScreen"));
                Assert.That(Vector2.Distance((Vector2)Get(exact,"ScreenPosition"),point),Is.LessThan(.01f));
            }
        }

        [Test]
        public void ChangingCanvasRootOwnershipAndControllerDisableClearOldVisibility()
        {
            using(var f=new Fixture())
            {
                float x=f.Region.xMin,y=f.Region.center.y;f.Sample(x-1,y);Assert.That(f.Visibility(x+1,y),Is.EqualTo("OffScreen"));
                var parent=new GameObject("New canvas owner",typeof(RectTransform),typeof(Canvas));
                try
                {
                    f.CanvasHost.transform.SetParent(parent.transform,false);
                    Assert.That(f.Visibility(x+1,y),Is.EqualTo("OnScreen"));
                    f.Sample(x-1,y);Assert.That(f.Visibility(x+1,y),Is.EqualTo("OffScreen"));
                    Call(f.Controller,"OnDisable");Assert.That(f.Visibility(x+1,y),Is.EqualTo("OnScreen"));
                }
                finally { f.CanvasHost.transform.SetParent(null,false);UnityEngine.Object.DestroyImmediate(parent); }
            }
        }
    }
}
