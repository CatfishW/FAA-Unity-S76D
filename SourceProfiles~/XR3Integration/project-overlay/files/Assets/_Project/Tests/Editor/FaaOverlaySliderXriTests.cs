using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Real Overlay Settings factory, pixel raycaster, owned mouse, and XRI drag dispatch.</summary>
    public sealed class FaaOverlaySliderXriTests
    {
        private const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
        private static Type Workspace=>Type.GetType("FAA.Customization.FaaSpatialWorkspace, Assembly-CSharp",true);
        private readonly List<UnityEngine.Object> owned=new();
        private Mouse mouse,previousMouse;
        private InputSettings.UpdateMode previousMode;
        private XRUIInputModule input;
        private Component workspace,repair;
        private Slider slider;
        private int dragCount;
        private readonly List<Vector2> dragPoints=new();
        private GameObject Own(string name,params Type[] types){var go=new GameObject(name,types);owned.Add(go);return go;}
        private void Frame(bool down,Vector2 point)
        {
            var state=new MouseState{position=point};if(down)state=state.WithButton(MouseButton.Left);
            InputSystem.QueueStateEvent(mouse,state);InputSystem.Update();
            if(repair!=null)Assert.That((bool)repair.GetType().GetMethod("ProcessPendingTap").Invoke(repair,new object[]{input,true}),Is.False,"A held/moving drag must not be repaired as a collapsed tap.");
            typeof(XRUIInputModule).GetMethod("DoProcess",Any).Invoke(input,null);
        }
        private void Create(float eyeDistance,bool useTapRepair)
        {
            previousMouse=Mouse.current;previousMode=InputSystem.settings.updateMode;
            InputSystem.settings.updateMode=InputSettings.UpdateMode.ProcessEventsManually;
            mouse=InputSystem.AddDevice<Mouse>();mouse.MakeCurrent();
            var events=Own("Actual Overlay XRI events",typeof(EventSystem));input=events.AddComponent<XRUIInputModule>();
            typeof(XRUIInputModule).GetMethod("OnEnable",Any).Invoke(input,null);
            input.enableXRInput=false;input.enableTouchInput=false;input.enableGamepadInput=false;input.enableJoystickInput=false;input.enableMouseInput=true;
            var owner=Own("Inactive factory workspace");owner.SetActive(false);workspace=owner.AddComponent(Workspace);
            Workspace.GetProperty("PersistChanges").SetValue(workspace,false);
            var eye=Own("Overlay fixture eye",typeof(Camera)).GetComponent<Camera>();eye.transform.position=Vector3.forward*eyeDistance;
            var texture=new RenderTexture(1600,900,0);owned.Add(texture);eye.targetTexture=texture;eye.aspect=1600f/900f;
            Workspace.GetProperty("View",Any).SetValue(workspace,eye);
            var module=Own("Selected actual airspeed",typeof(RectTransform));
            var target=Type.GetType("FAA.Customization.FaaNonConformalScaleTarget, Assembly-CSharp",true);
            object scaleTarget=Activator.CreateInstance(target,new object[]{"airspeed","Airspeed",module.transform,1f,0f});
            ((IList)Workspace.GetField("modules",Any).GetValue(workspace)).Add(scaleTarget);
            Workspace.GetMethod("BuildControls",Any).Invoke(workspace,null);
            Canvas canvas=(Canvas)Workspace.GetProperty("SettingsCanvas").GetValue(workspace);
            canvas.transform.SetParent(Own("Active factory Overlay UI").transform,false);
            canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.worldCamera=null;canvas.transform.localScale=Vector3.one;
            var scaler=canvas.gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution=new Vector2(1600,900);scaler.matchWidthOrHeight=.5f;
            var menu=(RectTransform)Workspace.GetField("menuRoot",Any).GetValue(workspace);
            menu.anchorMin=menu.anchorMax=menu.pivot=new Vector2(.5f,.5f);menu.anchoredPosition=Vector2.zero;
            Workspace.GetMethod("OpenMenu").Invoke(workspace,null);
            slider=(Slider)Workspace.GetField("scaleSlider",Any).GetValue(workspace);
            var raycaster=canvas.GetComponent<GraphicRaycaster>();
            Assert.That(raycaster.GetType().Name,Is.EqualTo("FaaCanvasPixelRaycaster"));
            typeof(GraphicRaycaster).GetMethod("OnEnable",Any).Invoke(raycaster,null);
            Canvas.ForceUpdateCanvases();eye.Render();
            Assert.That(raycaster.eventCamera,Is.Null,"Overlay drag has no projection camera.");
            Assert.That(slider.IsActive()&&slider.IsInteractable(),Is.True);
            input.drag+=(go,data)=>
            {
                Assert.That(go,Is.SameAs(slider.gameObject));Assert.That(data.position,Is.EqualTo(mouse.position.ReadValue()));
                Assert.That(data.pressEventCamera,Is.Null);Assert.That(data.pointerPressRaycast.module,Is.SameAs(raycaster));
                dragCount++;dragPoints.Add(data.position);
            };
            if(useTapRepair)
            {
                Type repairType=Type.GetType("FAA.Headset.XR3CollapsedMouseTapRepair, Assembly-CSharp",true);
                repair=Own("Actual tap repair exclusion").AddComponent(repairType);
                repairType.GetMethod("OnEnable",Any).Invoke(repair,null);
            }
        }
        [TearDown]
        public void Cleanup()
        {
            if(repair!=null)repair.GetType().GetMethod("OnDisable",Any).Invoke(repair,null);
            if(input!=null)typeof(XRUIInputModule).GetMethod("OnDisable",Any).Invoke(input,null);
            foreach(var item in owned)if(item is GameObject go&&go!=null)
                foreach(var camera in go.GetComponentsInChildren<Camera>(true))camera.targetTexture=null;
            for(int i=owned.Count-1;i>=0;i--)if(owned[i]!=null)UnityEngine.Object.DestroyImmediate(owned[i]);owned.Clear();
            if(mouse!=null&&mouse.added)InputSystem.RemoveDevice(mouse);
            if(previousMouse!=null&&previousMouse.added)previousMouse.MakeCurrent();
            InputSystem.settings.updateMode=previousMode;
        }
        [Test]
        public void PositionArrivingOnlyWithReleaseDoesNotBecomeAHeldDragButTrackPressStillWorks()
        {
            Create(0f,false);dragCount=0;dragPoints.Clear();
            RectTransform track=(RectTransform)slider.transform;
            Vector2 start=RectTransformUtility.WorldToScreenPoint(null,slider.handleRect.position);
            Vector2 left=RectTransformUtility.WorldToScreenPoint(null,track.TransformPoint(new Vector3(track.rect.xMin,0,0)));
            Vector2 right=RectTransformUtility.WorldToScreenPoint(null,track.TransformPoint(new Vector3(track.rect.xMax,0,0)));
            Vector2 end=start+(right-left)*.2f;
            Frame(false,start);Frame(true,start);
            // Models what the existing two-endpoint runtime log cannot rule out: no intermediate held position samples.
            Frame(false,end);
            Assert.That(dragCount,Is.Zero);Assert.That(slider.value,Is.EqualTo(1f).Within(.01f));
            Frame(true,end);Frame(false,end);
            Assert.That(slider.value,Is.GreaterThan(1.15f),"A track press jumps immediately even when a held-motion path was absent.");
        }
        [TestCase(0f,false)] [TestCase(20000f,false)] [TestCase(0f,true)] [TestCase(20000f,true)]
        public void RealXriHeldMouseDragMovesAndRetainsFactoryOverlaySlider(float eyeDistance,bool useTapRepair)
        {
            Create(eyeDistance,useTapRepair);dragCount=0;dragPoints.Clear();
            RectTransform track=(RectTransform)slider.transform;
            Vector2 start=RectTransformUtility.WorldToScreenPoint(null,slider.handleRect.position);
            Vector2 left=RectTransformUtility.WorldToScreenPoint(null,track.TransformPoint(new Vector3(track.rect.xMin,0,0)));
            Vector2 right=RectTransformUtility.WorldToScreenPoint(null,track.TransformPoint(new Vector3(track.rect.xMax,0,0)));
            Vector2 end=start+(right-left)*.2f;
            Frame(false,start);
            Assert.That(input.GetCurrentGameObject(0),Is.SameAs(slider.gameObject),"Real Overlay/XRI hit test must establish the factory Slider target.");
            Assert.That(slider.value,Is.EqualTo(1f).Within(.001f));
            Frame(true,start);
            for(int n=1;n<=12;n++)
            {
                Vector2 position=Vector2.Lerp(start,end,n/12f);Frame(true,position);
                float expected=1f+(slider.maxValue-slider.minValue)*.2f*n/12f;
                Assert.That(slider.value,Is.EqualTo(expected).Within(.012f),"Every polled held frame should move the value in the drag direction.");
            }
            Frame(false,end);float retained=slider.value;Frame(false,end);
            Assert.That(dragCount,Is.GreaterThanOrEqualTo(12));Assert.That(dragPoints[dragPoints.Count-1].x,Is.EqualTo(end.x).Within(.01f));
            Assert.That(retained,Is.GreaterThan(1.15f));Assert.That(slider.value,Is.EqualTo(retained).Within(.001f));
            object entry=Workspace.GetMethod("GetEntry").Invoke(workspace,new object[]{"airspeed"});
            Assert.That((float)entry.GetType().GetField("scale").GetValue(entry),Is.EqualTo(retained).Within(.001f));
            var caption=(TMP_Text)Workspace.GetField("selectedCaption",Any).GetValue(workspace);
            Assert.That(caption.text,Does.Contain(Mathf.RoundToInt(retained*100f)+"%"));
            // Compare the same real module's direct track press (the path that worked on4090).
            slider.SetValueWithoutNotify(1f);Vector2 click=Vector2.Lerp(left,right,.8f);
            Frame(false,click);Frame(true,click);Frame(false,click);
            Assert.That(slider.value,Is.EqualTo(slider.minValue+(slider.maxValue-slider.minValue)*.8f).Within(.012f));
            if(repair!=null)Assert.That((int)repair.GetType().GetProperty("RecoveredTapCount").GetValue(repair),Is.Zero);
        }
    }
}
