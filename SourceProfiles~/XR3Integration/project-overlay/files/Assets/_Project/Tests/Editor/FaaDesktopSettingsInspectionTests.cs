using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;
using UnityEngine.TestTools;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Object = UnityEngine.Object;

namespace FAA.Customization.Tests
{
    public sealed class FaaDesktopSettingsInspectionTests
    {
        const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
        static Type T(string name,string assembly="Assembly-CSharp")=>Type.GetType(name+", "+assembly,true);
        static Type Preview=>T("FAA.Headset.XR3DesktopHudPreview");
        static Type Pointer=>T("FAA.Headset.XR3DesktopPointerInput");
        static Type Chrome=>T("FAA.Customization.FaaPilotChrome");
        static Type Workspace=>T("FAA.Customization.FaaSpatialWorkspace");
        static object Call(object owner,string method,params object[] args)=>(owner is Type type?type:owner.GetType()).GetMethod(method,Any).Invoke(owner is Type?null:owner,args);
        static void Set(object owner,string name,object value)=>(owner is Type type?type:owner.GetType()).GetField(name,Any).SetValue(owner is Type?null:owner,value);
        static V Get<V>(object owner,string name)=>(V)owner.GetType().GetProperty(name,Any).GetValue(owner);
        static bool MouseDispatch=>(bool)Pointer.GetProperty("IsDesktopMouseDispatch").GetValue(null);
        static void ClearEscape()=>Set(Chrome,"escapeConsumedFrame",-100);
        Mouse mouse;Keyboard keyboard;GameObject eventHost;EventSystem events;
        object inputManager;PropertyInfo runPlayerInEditor;bool originalRunPlayerInEditor;
        [SetUp]public void Setup()
        {
            if(!Application.isPlaying)EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            Call(Chrome,"ResetDeveloperMode");ClearEscape();
        }
        void EnsureInput()
        {
            if(events!=null)return;
            inputManager=typeof(InputSystem).GetField("s_Manager",Any).GetValue(null);
            runPlayerInEditor=inputManager.GetType().GetProperty("runPlayerUpdatesInEditMode",Any);
            originalRunPlayerInEditor=(bool)runPlayerInEditor.GetValue(inputManager);runPlayerInEditor.SetValue(inputManager,true);
            mouse=InputSystem.AddDevice<Mouse>();keyboard=InputSystem.AddDevice<Keyboard>();
            if(Application.isPlaying&&EventSystem.current!=null)events=EventSystem.current;
            else{eventHost=new GameObject("Settings input fixture",typeof(EventSystem));events=eventHost.GetComponent<EventSystem>();}
        }
        [TearDown]public void End()
        {
            if(mouse!=null)InputSystem.RemoveDevice(mouse);if(keyboard!=null)InputSystem.RemoveDevice(keyboard);
            if(inputManager!=null)runPlayerInEditor.SetValue(inputManager,originalRunPlayerInEditor);
            if(eventHost!=null)Object.DestroyImmediate(eventHost);ClearEscape();Call(Chrome,"ResetDeveloperMode");
        }
        ExtendedPointerEventData MouseEvent()
        {EnsureInput();return new ExtendedPointerEventData(events)
        {device=mouse,pointerType=UnityEngine.InputSystem.UI.UIPointerType.MouseOrPen,button=PointerEventData.InputButton.Left,pointerId=mouse.deviceId};}
        void PressKeys(Component workspace,params Key[] keys)
        {
            EnsureInput();bool observed=false,pressed=false;Exception failure=null;string diagnostics="";
            Action after=()=>
            {
                if(InputState.currentUpdateType!=InputUpdateType.Dynamic)return;
                keyboard.MakeCurrent();observed=true;pressed=keys.All(key=>keyboard[key].wasPressedThisFrame);
                diagnostics="device="+keyboard.deviceId+" enabled="+keyboard.enabled+" current="+(Keyboard.current==keyboard)+" update="+InputState.currentUpdateType+" keys="+
                    string.Join(",",keys.Select(key=>key+":"+keyboard[key].ReadValue()+"/pressed="+keyboard[key].wasPressedThisFrame));
                if(pressed)try{Call(workspace,"HandleKeyboard");}catch(Exception error){failure=error;}
            };
            InputSystem.onAfterUpdate+=after;
            try
            {
                InputSystem.QueueStateEvent(keyboard,new KeyboardState(keys));
                typeof(InputSystem).GetMethod("Update",Any,null,new[]{typeof(InputUpdateType)},null).Invoke(null,new object[]{InputUpdateType.Dynamic});
            }
            finally{InputSystem.onAfterUpdate-=after;}
            Assert.That(observed&&pressed,Is.True,"Owned keyboard must be observed in the actual Dynamic phase: "+diagnostics);
            Assert.That(failure,Is.Null,"Real keyboard handler exception: "+failure);
        }

        internal sealed class Fixture:IDisposable
        {
            readonly IDisposable native;readonly GameObject workspaceHost,previewHost,presentationHost;
            readonly Component previewOwner,presentation;
            GameObject sharedAncestor;
            readonly bool mirror;
            public readonly Component Owner,ChromeOwner,Rig;
            public readonly Camera Head,Desktop;
            public readonly Canvas Canvas;
            public readonly RectTransform Root;
            public readonly Transform OriginalParent;
            public readonly Transform[] OriginalChildren;
            public readonly string Profile;
            public readonly Matrix4x4 Projection;
            public readonly Vector3 LocalHeadPosition;
            public readonly Quaternion LocalHeadRotation;
            public Fixture()
            {
                if(Application.isPlaying)
                {
                    var existing=(Component)Workspace.GetProperty("Current").GetValue(null);
                    if(existing!=null)Object.DestroyImmediate(existing.gameObject);
                    var chrome=(Component)Chrome.GetProperty("Current").GetValue(null);
                    if(chrome!=null)Object.DestroyImmediate(chrome.gameObject);
                }
                Type fixture=typeof(FaaNearRenderSpaceIntegrationTests).GetNestedType("Fixture",BindingFlags.NonPublic);
                object state=Activator.CreateInstance(fixture,true);native=(IDisposable)state;
                Head=(Camera)fixture.GetProperty("View").GetValue(state);Rig=(Component)fixture.GetField("Rig").GetValue(state);
                Call(Rig,"SetNearRenderSpaceRequested",true);
                var projection=Head.projectionMatrix;projection.m02=.13f;projection.m12=-.04f;Head.projectionMatrix=projection;Projection=projection;
                LocalHeadPosition=Head.transform.localPosition;LocalHeadRotation=Head.transform.localRotation;
                mirror=UnityEngine.XR.XRSettings.showDeviceView;
                presentationHost=new GameObject("Settings MR binding fixture");presentationHost.SetActive(false);
                presentation=presentationHost.AddComponent(T("FAA.Headset.XR3MixedRealityPresentation"));Set(presentation,"<PassthroughActive>k__BackingField",true);
                previewHost=new GameObject("Settings owned spectator fixture");previewOwner=previewHost.AddComponent(Preview);
                var cameraHost=new GameObject("Owned mono display",typeof(Camera));cameraHost.transform.SetParent(previewHost.transform,false);Desktop=cameraHost.GetComponent<Camera>();
                Set(previewOwner,"preview",Desktop);Set(previewOwner,"capturedMirror",true);Set(previewOwner,"originalMirror",mirror);Set(previewOwner,"presentation",presentation);Set(Preview,"active",previewOwner);
                Call(Preview,"ConfigureSpectatorCamera",Desktop,Head,new Vector2(Screen.width,Screen.height));
                ChromeOwner=(Component)Call(Chrome,"CreateForTests");
                workspaceHost=new GameObject("Actual Settings workspace fixture");Owner=workspaceHost.AddComponent(Workspace);
                Set(Workspace,"<Current>k__BackingField",Owner);Set(Owner,"<View>k__BackingField",Head);
                Set(Owner,"aircraft",((GameObject)fixture.GetField("Aircraft").GetValue(state)).transform);
                Set(Owner,"<CockpitFrame>k__BackingField",new GameObject("Stable cockpit fixture").transform);
                Set(Owner,"trackedView",Head.GetComponent<UnityEngine.InputSystem.XR.TrackedPoseDriver>());Set(Owner,"desktopView",Rig);Set(Owner,"nativeDefaults",true);
                Workspace.GetProperty("PersistChanges").SetValue(Owner,false);Set(Owner,"<Initialized>k__BackingField",true);
                if(!Application.isPlaying)Call(Owner,"OnEnable");Call(Owner,"BuildControls");Call(Owner,"RefreshTransforms");
                // Match the real HUD-layer ownership before checking the actual canvas raycaster.
                Set(presentation,"view",Head);Call(presentation,"DiscoverPresentation");Call(presentation,"ApplyPresentation");
                Canvas=Get<Canvas>(Owner,"SettingsCanvas");Root=Canvas.transform as RectTransform;OriginalParent=Root.parent;
                OriginalChildren=Enumerable.Range(0,Root.childCount).Select(Root.GetChild).ToArray();Profile=(string)Call(Owner,"ExportProfile");
            }
            public void Open()=>Call(Owner,"ToggleDesktopSettingsInspection");
            public void Close()=>Call(Owner,"CloseSettingsMenu");
            public void AssertOpen()
            {
                Assert.That(Get<bool>(Owner,"MenuOpen"),Is.True);Assert.That(Get<bool>(Owner,"DesktopSettingsInspectionActive"),Is.True);
                Assert.That(Canvas.renderMode,Is.EqualTo(RenderMode.ScreenSpaceCamera));Assert.That(Canvas.worldCamera,Is.SameAs(Desktop));
                Assert.That(OriginalChildren.All(x=>x.parent!=Root),Is.True,"The original children move into the temporary host; UI is not duplicated.");
                AssertHeadUnchanged();
            }
            public void AssertRestored()
            {
                Assert.That(Get<bool>(Owner,"DesktopSettingsInspectionActive"),Is.False);Assert.That(Get<bool>(Owner,"MenuOpen"),Is.False);
                Assert.That(Canvas.renderMode,Is.EqualTo(RenderMode.WorldSpace));Assert.That(Canvas.worldCamera,Is.SameAs(Head));Assert.That(Root.parent,Is.SameAs(OriginalParent));
                Assert.That(Enumerable.Range(0,Root.childCount).Select(Root.GetChild),Is.EqualTo(OriginalChildren));
                Assert.That((string)Call(Owner,"ExportProfile"),Is.EqualTo(Profile));AssertHeadUnchanged();
            }
            public void AssertHeadUnchanged()
            {Assert.That(Head.projectionMatrix,Is.EqualTo(Projection));Assert.That(Head.transform.localPosition,Is.EqualTo(LocalHeadPosition));Assert.That(Head.transform.localRotation,Is.EqualTo(LocalHeadRotation));}
            public void Invalidate(string cause)
            {
                if(cause=="preview-disabled")Desktop.enabled=false;
                if(cause=="provider-inactive")Set(presentation,"<PassthroughActive>k__BackingField",false);
                if(cause=="view-rebound")Set(Owner,"desktopSettingsHeadset",null);
                if(cause=="owner-rebound")Set(Owner,"desktopSettingsOwner",null);
                Call(Owner,"RefreshTransforms");
            }
            public void Suspend()=>Call(previewOwner,"SuspendPreview");
            public void SetSpectatorActive(bool active)=>previewHost.SetActive(active);
            public void SetWorkspaceActive(bool active)=>workspaceHost.SetActive(active);
            public void SetSharedAncestorActive(bool active)
            {
                if(sharedAncestor==null)
                {
                    sharedAncestor=new GameObject("Actual shared settings ancestor");
                    previewHost.transform.SetParent(sharedAncestor.transform,true);
                    workspaceHost.transform.SetParent(sharedAncestor.transform,true);
                }
                sharedAncestor.SetActive(active);
            }
            public void Dispose()
            {
                Call(Owner,"ReleaseDesktopSettingsInspection");
                // Runtime-only MonoBehaviours do not receive their lifecycle automatically in edit-mode fixtures.
                // Destroy owned views first so the explicit OnDisable tests cleanup without edit-mode Destroy warnings.
                if(Canvas!=null)Object.DestroyImmediate(Canvas.gameObject);var controls=Get<Canvas>(Owner,"ControlsCanvas");if(controls!=null)Object.DestroyImmediate(controls.gameObject);
                var cockpit=Get<Transform>(Owner,"CockpitFrame");if(cockpit!=null)Object.DestroyImmediate(cockpit.gameObject);
                Call(Owner,"OnDisable");Set(Workspace,"<Current>k__BackingField",null);Object.DestroyImmediate(workspaceHost);
                Call(previewOwner,"OnDisable");Object.DestroyImmediate(previewHost);Object.DestroyImmediate(presentationHost);
                if(sharedAncestor!=null)Object.DestroyImmediate(sharedAncestor);
                if(ChromeOwner!=null)Object.DestroyImmediate(ChromeOwner.gameObject);native.Dispose();UnityEngine.XR.XRSettings.showDeviceView=mirror;
            }
        }

        [Test]public void ExactMousePointerDispatchOpensSameSettingsAndF9ClosesWithoutChangingHead()
        {
            using(var f=new Fixture())
            {
                var button=(RectTransform)Call(f.ChromeOwner,"ButtonRect","settings");
                Assert.That(button.GetComponent(Pointer),Is.Not.Null,"The approved chrome factory supplies exact callback provenance.");
                ExecuteEvents.Execute(button.gameObject,MouseEvent(),ExecuteEvents.pointerClickHandler);f.AssertOpen();
                Assert.That(MouseDispatch,Is.False,"No pointer provenance leaks beyond dispatch.");
                PressKeys(f.Owner,Key.F9);f.AssertRestored();
            }
        }
        [Test]public void RealKeyboardF9OpensAndEscapeClosesTheDesktopSettings()
        {
            using(var f=new Fixture()){PressKeys(f.Owner,Key.F9);f.AssertOpen();PressKeys(f.Owner);ClearEscape();PressKeys(f.Owner,Key.Escape);f.AssertRestored();}
        }
        [UnityTest]public IEnumerator SettingsCloseButtonHitsThroughTheActualMonoCanvas()
        {
            yield return new EnterPlayMode();
            using(var f=new Fixture())
            {
                f.Open();f.AssertOpen();yield return null;yield return null;f.AssertOpen();Canvas.ForceUpdateCanvases();
                var close=f.Canvas.GetComponentsInChildren<Button>(true).Single(x=>x.name=="Stow Settings");
                var data=MouseEvent();data.position=RectTransformUtility.WorldToScreenPoint(f.Desktop,close.transform.position);
                var hits=new List<RaycastResult>();events.RaycastAll(data,hits);
                bool hit=hits.Any(x=>ExecuteEvents.GetEventHandler<IPointerClickHandler>(x.gameObject)==close.gameObject);
                Assert.That(hit,Is.True,"Original mono CLOSE must hit. depth="+close.targetGraphic.depth+" cull="+close.targetGraphic.canvasRenderer.cull+
                    " layer="+close.gameObject.layer+" point="+data.position+" view="+f.Desktop.pixelRect+" canvas="+f.Canvas.pixelRect+
                    " hits="+string.Join(",",hits.Select(x=>x.gameObject.name)));
                ExecuteEvents.Execute(close.gameObject,data,ExecuteEvents.pointerClickHandler);f.AssertRestored();
            }
            yield return null;LogAssert.NoUnexpectedReceived();yield return new ExitPlayMode();
        }
        [Test]public void DesktopStatusChipOpensDataSourcesIdempotentlyWhileProgrammaticRouteStaysNative()
        {
            using(var f=new Fixture())
            {
                Call(f.ChromeOwner,"ReportStatus","flight",Enum.Parse(T("FAA.Customization.FaaChromeSeverity"),"Nominal"),"DATA LIVE","Known live fixture",0,true);
                Assert.That(Get<bool>(f.ChromeOwner,"StatusVisible"),Is.True);
                string id=(string)Chrome.GetField("StatusId",Any).GetValue(null);
                var button=(RectTransform)Call(f.ChromeOwner,"ButtonRect",id);Assert.That(button.GetComponent(Pointer),Is.Not.Null);
                ExecuteEvents.Execute(button.gameObject,MouseEvent(),ExecuteEvents.pointerClickHandler);f.AssertOpen();
                Assert.That(Get<int>(f.Owner,"SettingsPage"),Is.EqualTo(3));
                var originalHost=f.OriginalChildren[0].parent;
                ExecuteEvents.Execute(button.gameObject,MouseEvent(),ExecuteEvents.pointerClickHandler);f.AssertOpen();
                Assert.That(f.OriginalChildren[0].parent,Is.SameAs(originalHost),"Status opens its page; it never toggles the already visible Settings closed.");
                f.Close();Call(f.Owner,"OpenDataSourceSettings");
                Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);Assert.That(Get<int>(f.Owner,"SettingsPage"),Is.EqualTo(3));f.AssertHeadUnchanged();
            }
        }
        [Test]public void EscapeRespectsPopupBeforeAndAfterChromeConsumesTheSamePress()
        {
            using(var f=new Fixture())
            {
                f.Open();Call(f.ChromeOwner,"SetCommandsVisible",true);
                Assert.That(Call(f.Owner,"HandleSettingsEscape"),Is.False);f.AssertOpen();
                Call(Chrome,"MarkEscapeConsumed");Call(f.ChromeOwner,"SetCommandsVisible",false);
                Assert.That(Call(f.Owner,"HandleSettingsEscape"),Is.False);f.AssertOpen();
                ClearEscape();Assert.That(Call(f.Owner,"HandleSettingsEscape"),Is.True);f.AssertRestored();
            }
        }
        [TestCase("preview-disabled")][TestCase("provider-inactive")][TestCase("view-rebound")][TestCase("owner-rebound")]
        public void LossOfExactCameraBindingRestoresAuthoredState(string cause)
        {using(var f=new Fixture()){f.Open();f.AssertOpen();f.Invalidate(cause);f.AssertRestored();}}
        [Test]public void SpectatorSuspensionRestoresImmediatelyBeforeAnotherWorkspaceFrame()
        {using(var f=new Fixture()){f.Open();f.AssertOpen();f.Suspend();f.AssertRestored();}}
        [Test]public void UnownedMonoCameraCannotBecomeASettingsHost()
        {
            using(var f=new Fixture())
            {
                var other=new GameObject("Unowned mono",typeof(Camera));try
                {Assert.That(Call(Preview,"IsDesktopInspectionCamera",other.GetComponent<Camera>(),f.Head),Is.False);}
                finally{Object.DestroyImmediate(other);}
                Assert.That(Call(Preview,"IsDesktopInspectionCamera",f.Head,f.Head),Is.False);
            }
        }
        [Test]public void TrackedAndProgrammaticActionsKeepTheNativeSideRoute()
        {
            EnsureInput();
            using(var f=new Fixture())
            {
                var button=(RectTransform)Call(f.ChromeOwner,"ButtonRect","settings");
                ExecuteEvents.Execute(button.gameObject,new TrackedDeviceEventData(events){pointerId=0,button=PointerEventData.InputButton.Left},ExecuteEvents.pointerClickHandler);
                Assert.That(Get<bool>(f.Owner,"MenuOpen"),Is.True);Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);Assert.That(f.Canvas.renderMode,Is.EqualTo(RenderMode.WorldSpace));f.AssertHeadUnchanged();
                f.Close();Call(f.ChromeOwner,"Press","settings");Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);f.AssertHeadUnchanged();
            }
        }
        [Test]public void ProgrammaticAndSubmitCallbacksDoNotInheritMouseContext()
        {
            EnsureInput();
            var host=new GameObject("Provenance button",typeof(RectTransform));var button=(Button)host.AddComponent(Pointer);var seen=new List<bool>();button.onClick.AddListener(()=>seen.Add(MouseDispatch));
            try{button.onClick.Invoke();ExecuteEvents.Execute(host,new BaseEventData(events),ExecuteEvents.submitHandler);ExecuteEvents.Execute(host,MouseEvent(),ExecuteEvents.pointerClickHandler);Assert.That(seen,Is.EqualTo(new[]{false,false,true}));Assert.That(MouseDispatch,Is.False);}
            finally{Object.DestroyImmediate(host);}
        }
        [Test]public void NestedDispatchRestoresContextEvenWhenListenerThrows()
        {
            EnsureInput();
            var outer=new GameObject("Outer mouse",typeof(RectTransform));var inner=new GameObject("Inner tracked",typeof(RectTransform));var a=(Button)outer.AddComponent(Pointer);var b=(Button)inner.AddComponent(Pointer);
            b.onClick.AddListener(()=>{Assert.That(MouseDispatch,Is.False);throw new InvalidOperationException("owned fixture");});
            a.onClick.AddListener(()=>{Assert.That(MouseDispatch,Is.True);Assert.Throws<InvalidOperationException>(()=>b.OnPointerClick(new TrackedDeviceEventData(events)));Assert.That(MouseDispatch,Is.True);});
            try{ExecuteEvents.Execute(outer,MouseEvent(),ExecuteEvents.pointerClickHandler);Assert.That(MouseDispatch,Is.False);}
            finally{Object.DestroyImmediate(outer);Object.DestroyImmediate(inner);}
        }
        [Test]public void HandUnknownAndNonMouseTypedPointersAreRejectedBeforeIdHeuristics()
        {
            EnsureInput();
            Assert.That(Call(Pointer,"IsDesktopMousePointer",new PointerEventData(events){pointerId=-2200}),Is.False);
            Assert.That(Call(Pointer,"IsDesktopMousePointer",new PointerEventData(events){pointerId=0}),Is.False);
            var wrong=MouseEvent();wrong.pointerType=UnityEngine.InputSystem.UI.UIPointerType.Touch;
            Assert.That(Call(Pointer,"IsDesktopMousePointer",wrong),Is.False);
            Assert.That(Call(Pointer,"IsDesktopMousePointer",new TrackedDeviceEventData(events){pointerId=0}),Is.False);
        }
        [Test]public void PinnedXriBuiltInMouseModelIsAcceptedButCustomActionsAreNot()
        {
            EnsureInput();
            var module=eventHost.AddComponent<XRUIInputModule>();
            typeof(EventSystem).GetField("m_CurrentInputModule",Any).SetValue(events,module);
            var field=typeof(XRUIInputModule).GetField("m_UseBuiltInInputSystemActions",Any);field.SetValue(module,true);
            var data=new PointerEventData(events){pointerId=0,button=PointerEventData.InputButton.Left};
            Assert.That(Call(Pointer,"IsDesktopMousePointer",data),Is.True);
            field.SetValue(module,false);Assert.That(Call(Pointer,"IsDesktopMousePointer",data),Is.False);
            field.SetValue(module,true);data.pointerId=1;Assert.That(Call(Pointer,"IsDesktopMousePointer",data),Is.False);
        }
        [UnityTest]public IEnumerator ActualSpectatorDisableAndReenableRestoreWithoutReopeningSettings()
        {
            yield return new EnterPlayMode();
            using(var f=new Fixture())
            {
                f.Open();f.AssertOpen();f.SetSpectatorActive(false);f.AssertRestored();
                f.SetSpectatorActive(true);Assert.That(Get<bool>(f.Owner,"MenuOpen"),Is.False);Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);
            }
            yield return null;LogAssert.NoUnexpectedReceived();yield return new ExitPlayMode();
        }
        [UnityTest]public IEnumerator ActualWorkspaceComponentDisableRestoresOriginalChildrenBeforeTeardown()
        {
            yield return new EnterPlayMode();
            using(var f=new Fixture())
            {
                f.Open();f.AssertOpen();((Behaviour)f.Owner).enabled=false;
                Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);
                Assert.That(f.OriginalChildren.All(x=>x.parent==f.Root),Is.True);f.AssertHeadUnchanged();
                ((Behaviour)f.Owner).enabled=true;Assert.That(Get<bool>(f.Owner,"MenuOpen"),Is.False);
            }
            yield return null;LogAssert.NoUnexpectedReceived();yield return new ExitPlayMode();
        }
        [UnityTest]public IEnumerator ActualWorkspaceAncestorDisableDoesNotMutateADeactivatingHierarchy()
        {
            yield return new EnterPlayMode();
            using(var f=new Fixture())
            {
                f.Open();f.AssertOpen();f.SetWorkspaceActive(false);
                Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);f.AssertHeadUnchanged();
                f.SetWorkspaceActive(true);Assert.That(Get<bool>(f.Owner,"MenuOpen"),Is.False);
            }
            yield return null;LogAssert.NoUnexpectedReceived();yield return new ExitPlayMode();
        }
        [UnityTest]public IEnumerator ActualSharedAncestorDisableOrdersPreviewBeforeWorkspaceWithoutReactivation()
        {
            yield return new EnterPlayMode();
            using(var f=new Fixture())
            {
                f.Open();f.AssertOpen();f.SetSharedAncestorActive(false);
                Assert.That(Get<bool>(f.Owner,"DesktopSettingsInspectionActive"),Is.False);
                Assert.That(Get<bool>(f.Owner,"MenuOpen"),Is.False);f.AssertHeadUnchanged();
                f.SetSharedAncestorActive(true);Assert.That(Get<bool>(f.Owner,"MenuOpen"),Is.False);
            }
            yield return null;LogAssert.NoUnexpectedReceived();yield return new ExitPlayMode();
        }
    }
}
