using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public sealed class FaaIndicatorTemporalStabilityTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type T(string name)=>Type.GetType(name+", Assembly-CSharp",true);
        private static object Get(object o,string name)=>o.GetType().GetField(name,Any).GetValue(o);
        private static void Set(object o,string name,object value)=>o.GetType().GetField(name,Any).SetValue(o,value);
        private GameObject host;private Component controller;private ScriptableObject settings;private List<Rect> occupied;
        [SetUp] public void Setup()
        {
            host=new GameObject("Inactive temporal cue fixture");host.SetActive(false);
            controller=host.AddComponent(T("IndicatorSystem.Controller.IndicatorSystemController"));
            settings=ScriptableObject.CreateInstance(T("IndicatorSystem.Core.IndicatorSettings"));Set(settings,"globalScale",1f);Set(controller,"settings",settings);
            occupied=(List<Rect>)Get(controller,"_occupiedCueBounds");
        }
        [TearDown] public void Cleanup(){UnityEngine.Object.DestroyImmediate(host);UnityEngine.Object.DestroyImmediate(settings);}
        private static object Data(Vector2 at,bool on=false,string id="TRAFFIC")
        {
            var data=Activator.CreateInstance(T("IndicatorSystem.Core.IndicatorData"));Set(data,"Id",id);
            Set(data,"ScreenPosition",at);Set(data,"ProjectionRect",new Rect(0,0,1920,1080));
            Set(data,"Type",Enum.Parse(T("IndicatorSystem.Core.IndicatorType"),"Traffic"));Set(data,"Visibility",Enum.Parse(T("IndicatorSystem.Core.IndicatorVisibility"),on?"OnScreen":"OffScreen"));
            Set(data,"ArrowRotation",90f);Set(data,"IsActive",true);Set(data,"DistanceNM",12f);return data;
        }
        private bool Layout(ref object data,out Rect bounds)
        {
            object[] args={data,1f,null};
            bool placed=(bool)controller.GetType().GetMethod("TryLayoutPilotCue",Any).Invoke(controller,args);
            data=args[0];bounds=(Rect)args[2];return placed;
        }
        private static bool Left(object data)=>(bool)T("IndicatorSystem.Display.PilotIndicatorCue").GetMethod("LabelOnLeft").Invoke(null,new[]{data});
        [Test] public void ABlockedEdgeLaneDoesNotAlternateAboveAndBelowForSubpixelInputNoise()
        {
            occupied.Add(new Rect(1500,450,420,100));float min=float.MaxValue,max=float.MinValue;
            for(int frame=0;frame<120;frame++)
            {
                object data=Data(new Vector2(1848,500+(frame%2==0?-.1f:.1f)));
                Assert.That(Layout(ref data,out Rect b),Is.True);float y=((Vector2)Get(data,"ScreenPosition")).y;min=Mathf.Min(min,y);max=Mathf.Max(max,y);
                Assert.That(b.Overlaps(occupied[0]),Is.False);Assert.That(Get(data,"ArrowRotation"),Is.EqualTo(90f));
            }
            Assert.That(max-min,Is.LessThan(.5f),"A subpixel target must not jump between distant clear lanes.");
        }
        [Test] public void AReadoutDoesNotFlipAcrossTheScreenWhenItsAnchorChattersAcrossCentre()
        {
            bool? initial=null;
            for(int frame=0;frame<120;frame++)
            {
                var at=new Vector2(960+(frame%2==0?-.1f:.1f),800);object data=Data(at,true);
                Assert.That(Layout(ref data,out _),Is.True);bool left=Left(data);if(initial==null)initial=left;
                Assert.That(left,Is.EqualTo(initial),"The 262-unit readout should keep its side through centre noise.");
                Assert.That(Get(data,"ScreenPosition"),Is.EqualTo(at),"A visible target anchor must remain exact.");
            }
        }
        [Test] public void SuppressedCueRequiresClearanceBeforeReappearingRatherThanBlinkingAtAnObstacleEdge()
        {
            object data=Data(new Vector2(960,500),true);object[] args={data,1f,settings};
            Rect b=(Rect)T("IndicatorSystem.Display.PilotIndicatorCue").GetMethod("ScreenBoundsForSettings").Invoke(null,args);
            occupied.Add(new Rect(b.xMax-.1f,b.yMin,100,b.height));Assert.That(Layout(ref data,out _),Is.False);
            for(int i=0;i<60;i++)
            {
                occupied[0]=new Rect(b.xMax+(i%2==0?.1f:-.1f),b.yMin,100,b.height);data=Data(new Vector2(960,500),true);
                Assert.That(Layout(ref data,out _),Is.False,"Subpixel clearance must not reacquire and release the whole cue.");
            }
            occupied[0]=new Rect(b.xMax+12f,b.yMin,100,b.height);data=Data(new Vector2(960,500),true);Assert.That(Layout(ref data,out _),Is.True);
        }
        [Test] public void TrueOnscreenAnchorsAndGeometricVisibilityAreNeverSmoothedOrReclassified()
        {
            foreach(Vector2 p in new[]{new Vector2(600,300),new Vector2(1400,700),new Vector2(800,500)})
            {object data=Data(p,true);Assert.That(Layout(ref data,out _),Is.True);Assert.That(Get(data,"ScreenPosition"),Is.EqualTo(p));Assert.That(Get(data,"Visibility").ToString(),Is.EqualTo("OnScreen"));}
        }
        [Test] public void CompletelyBlockedRegionSuppressesAndDoesNotMoveTheInputAnchor()
        {
            occupied.Add(new Rect(0,0,1920,1080));object data=Data(new Vector2(1848,500));Assert.That(Layout(ref data,out _),Is.False);Assert.That(Get(data,"ScreenPosition"),Is.EqualTo(new Vector2(1848,500)));
        }

        [Test] public void MovingObstacleCanInvalidateALaneAndMoveTheCueToAnotherSafeSlot()
        {
            occupied.Add(new Rect(1500,450,420,100));object data=Data(new Vector2(1848,500));Assert.That(Layout(ref data,out Rect first),Is.True);
            occupied.Add(first);data=Data(new Vector2(1848,500));Assert.That(Layout(ref data,out Rect next),Is.True);
            foreach(Rect obstacle in occupied)Assert.That(next.Overlaps(obstacle),Is.False);
            Assert.That(Get(data,"ArrowRotation"),Is.EqualTo(90f));
        }
        [Test] public void LabelAlwaysFacesInwardAtEitherViewportEdge()
        {
            object left=Data(new Vector2(72,500),false,"LEFT");Assert.That(Layout(ref left,out Rect a),Is.True);Assert.That(Left(left),Is.False);
            object right=Data(new Vector2(1848,500),false,"RIGHT");Assert.That(Layout(ref right,out Rect b),Is.True);Assert.That(Left(right),Is.True);
            Assert.That(a.xMin,Is.GreaterThanOrEqualTo(0));Assert.That(b.xMax,Is.LessThanOrEqualTo(1920));
        }
        [Test] public void RemovingATargetClearsItsPresentationHistory()
        {
            object right=Data(new Vector2(960.1f,800),true);Assert.That(Layout(ref right,out _),Is.True);Assert.That(Left(right),Is.True);
            controller.GetType().GetMethod("RemoveTarget").Invoke(controller,new object[]{"TRAFFIC"});
            object left=Data(new Vector2(959.9f,800),true);Assert.That(Layout(ref left,out _),Is.True);Assert.That(Left(left),Is.False);
        }
        [Test] public void ReturningFromAnEdgeCueUsesTheExactNewOnscreenAnchor()
        {
            occupied.Add(new Rect(1500,450,420,100));object edge=Data(new Vector2(1848,500));Assert.That(Layout(ref edge,out _),Is.True);
            occupied.Clear();var at=new Vector2(1010,520);object visible=Data(at,true);Assert.That(Layout(ref visible,out _),Is.True);
            Assert.That(Get(visible,"ScreenPosition"),Is.EqualTo(at));Assert.That(Get(visible,"Visibility").ToString(),Is.EqualTo("OnScreen"));
        }
        [Test] public void PresentationHistoryDoesNotLeakBetweenTargets()
        {
            object first=Data(new Vector2(960.1f,800),true,"A");Assert.That(Layout(ref first,out _),Is.True);
            object second=Data(new Vector2(959.9f,800),true,"B");Assert.That(Layout(ref second,out _),Is.True);
            Assert.That(Left(first),Is.True);Assert.That(Left(second),Is.False);
        }
        [Test] public void ACompletelySuppressedEdgeCueCanReacquireAClearLaneAfterTheObstacleShrinks()
        {
            occupied.Add(new Rect(0,0,1920,1080));object data=Data(new Vector2(1848,500));Assert.That(Layout(ref data,out _),Is.False);
            occupied.Clear();occupied.Add(new Rect(1500,450,420,100));data=Data(new Vector2(1848,500));
            Assert.That(Layout(ref data,out Rect recovered),Is.True,"Recovery solver must reserve its own reappearance clearance.");
            Assert.That(recovered.Overlaps(occupied[0]),Is.False);
            Assert.That(Mathf.Min(Mathf.Abs(recovered.yMax-occupied[0].yMin),Mathf.Abs(recovered.yMin-occupied[0].yMax)),Is.GreaterThanOrEqualTo(4f));
        }
    }
}
