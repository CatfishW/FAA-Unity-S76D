using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaWorkspaceUi;
namespace FAA.Customization
{
    /// <summary>
    /// Hand Studio side panel. Shows only what applies to the current camera state (progressive disclosure): no tracking or rate
    /// diagnostics while the camera is off, one START CAMERA that also asks macOS for access when needed, a camera-access line and
    /// OPEN CAMERA SETTINGS only when access is denied or restricted, one CLOSE in the header, the same layout-lock wording as
    /// Settings, and the shared panel palette and type scale. Rows stack from the top with no dead band; the panel's top edge stays
    /// fixed (FaaSpatialRadarPanel.TopAnchorHeight), so a state change never moves the control under the pointer. GameObject names
    /// are kept for the tools.
    /// </summary>
    public sealed partial class FaaLaptopCameraGestures
    {
        public Canvas CameraCanvas { get; private set; }
        private RectTransform panel;
        private RawImage previewImage;
        private FaaHandSkeletonGraphic skeleton;
        private TMP_Text statusText,deviceText,toggleText,previewPlaceholder,permissionText,fingerText,phaseText,rateText;
        private Button toggleButton,nextButton,permissionButton,privacyButton,editLockButton;
        private Button pinchModeButton,palmModeButton;
        private Image progress,progressTrack,surface,accentRail;
        private TMP_Text gestureLabel;
        private float nextPanelUpdate;
        // Change-only caches (the panel refreshes at 10 Hz; strings are rebuilt only when what they show changes).
        private int phaseKey=int.MinValue,rateKey=int.MinValue,deviceKey=int.MinValue,contextKey=-1,layoutKey=-1;
        private const string DefaultOffStatus="Camera OFF. Images stay on this laptop.",StoppedStatus="Camera OFF. No images recorded or uploaded.";
        /// <summary>
        /// Panel width and tallest height (panel units); the panel shrinks from the bottom when fewer rows are shown. Inspection
        /// framing reserves the tallest height, so the view never has to re-aim when rows appear, and a 640 x 700 panel still fits
        /// the clear column between the HUD columns at about 0.95 reference px per unit (16-unit text at or above MinLabel).
        /// </summary>
        public const float PanelWidth=640f,MaxPanelHeight=700f;
        // Fixed rows (y centres from the panel top): header, preview card (112-372), then START CAMERA / layout lock under it.
        private const float PreviewY=242f,PreviewHeight=260f,ToggleRowY=404f,LiveTop=438f,LiveBottom=526f;
        /// <summary>Camera-access line, shown only when macOS has denied or restricted camera access (pure, for tests).</summary>
        public static string AccessLine(FaaCameraPermissionState state)=>
            state==FaaCameraPermissionState.Denied?"CAMERA ACCESS DENIED":state==FaaCameraPermissionState.Restricted?"CAMERA ACCESS RESTRICTED":null;
        /// <summary>
        /// Panel height (panel units) for the rows shown (pure, for tests): live recognition rows, a status line, a context button
        /// row and a one-line note each add their own height; nothing leaves a gap.
        /// </summary>
        public static float PanelHeightFor(bool live,bool status,bool contextRow,bool note)
        {
            float y=GestureTop(live)+GestureBlock;
            if(status)y+=StatusBlock;if(contextRow)y+=ContextBlock;if(note)y+=NoteBlock;
            return Mathf.Clamp(y+16f,400f,MaxPanelHeight);
        }
        private static float GestureTop(bool live)=>live?LiveBottom+10f:ToggleRowY+MinTargetHeight*.5f+12f;
        private const float GestureBlock=22f+6f+MinTargetHeight+10f,StatusBlock=42f+8f,ContextBlock=MinTargetHeight+10f,NoteBlock=24f+4f;

        /// <summary>Opens the panel without toggling: a destination button never closes it or stops the camera.</summary>
        public void OpenPanel(){if(!PanelOpen)TogglePanel();}
        /// <summary>Header CLOSE: closes the panel, releases the camera and returns the view forward if the panel was in view.</summary>
        public void ClosePanel()
        {
            PanelOpen=false;StopCamera();
            owner?.CloseInspected("camera-controls");
            RefreshPanel();
        }

        private void BuildPanel()
        {
            if(CameraCanvas!=null)Destroy(CameraCanvas.gameObject);
            if(owner==null)return;
            CameraCanvas=FaaWorkspaceUi.Canvas("FAA Spatial Hand Studio",owner.transform,7230);
            panel=Rect("FAA Laptop Camera Gestures",CameraCanvas.transform,960,540,PanelWidth,MaxPanelHeight);
            surface=Box("Hand Studio Surface",panel,320,MaxPanelHeight*.5f,PanelWidth,MaxPanelHeight,Background,true);
            accentRail=Box("Camera Accent Rail",panel,4,MaxPanelHeight*.5f,4,MaxPanelHeight,Accent);
            Text("Webcam Heading",panel,"HAND STUDIO",210,52,370,44,28,true);
            // The only privacy statement on the panel (not repeated in the placeholder).
            Text("Hand Studio Subtitle",panel,"LOCAL CAMERA  |  NO RECORDING",210,88,370,24,16,true).color=Muted;
            Button("Stow Camera",panel,"CLOSE",560,52,120,MinTargetHeight,ClosePanel);
            // Panel size lives on the Settings PANELS page; these stay for compatibility but are not shown.
            SetActive(Button("Camera Panel Smaller",panel,"PANEL SMALLER",462,104,100,40,()=>owner.ResizeUtility("camera-controls",-.1f)),false);
            SetActive(Button("Camera Panel Larger",panel,"PANEL LARGER",570,104,100,40,()=>owner.ResizeUtility("camera-controls",.1f)),false);
            Box("Preview background",panel,320,PreviewY,592,PreviewHeight,Card);
            previewPlaceholder=Text("Camera Off Placeholder",panel,"CAMERA OFF\n\nStart the camera to see your hands.",320,PreviewY,550,160,20);
            var image=Rect("Local Camera Preview",panel,320,PreviewY,PreviewHeight*4f/3f,PreviewHeight-2f);
            previewImage=image.gameObject.AddComponent<RawImage>();previewImage.raycastTarget=false;previewImage.uvRect=new Rect(1,0,-1,1);
            image.gameObject.AddComponent<RectMask2D>();
            var bones=Rect("All Finger Landmarks",image,PreviewHeight*2f/3f,PreviewHeight*.5f-1f,PreviewHeight*4f/3f,PreviewHeight-2f);
            bones.anchorMin=Vector2.zero;bones.anchorMax=Vector2.one;bones.offsetMin=bones.offsetMax=Vector2.zero;
            skeleton=bones.gameObject.AddComponent<FaaHandSkeletonGraphic>();skeleton.raycastTarget=false;
            // One START CAMERA (it also asks macOS for camera access when needed) next to the layout lock, right under the preview.
            toggleButton=Button("Webcam Start Stop",panel,"START CAMERA",174,ToggleRowY,294,MinTargetHeight,()=>
                {if(CameraActive||Starting||RecognitionReady||PermissionPending)StopCamera();else StartCameraByUser();});
            toggleText=toggleButton.GetComponentInChildren<TMP_Text>();
            // Same control and wording as the Settings layout lock: gestures resize the HUD only while the layout is unlocked.
            editLockButton=Button("Webcam Edit Lock",panel,"UNLOCK LAYOUT",478,ToggleRowY,294,MinTargetHeight,()=>owner.SetEditMode(!owner.EditMode));
            // Live recognition lines: shown only while recognition runs, directly below START/STOP.
            phaseText=Text("Gesture Phase",panel,"",180,LiveTop+15f,310,30,20,true);phaseText.color=Accent;
            rateText=Text("Gesture Quality",panel,"",488,LiveTop+15f,260,30,16);rateText.color=Muted;
            progressTrack=Box("Progress Track",panel,320,LiveTop+38f,592,4,Card);
            progress=Box("Hold To Resize Progress",panel,24,LiveTop+38f,592,4,Accent);progress.rectTransform.pivot=new Vector2(0,.5f);
            fingerText=Text("Finger Feedback",panel,"",320,LiveTop+64f,592,32,16);
            gestureLabel=Text("Resize Gesture Label",panel,"RESIZE GESTURE",320,0,592,22,16);gestureLabel.color=Muted;
            pinchModeButton=Button("Multi Finger Mode",panel,"THUMB + ANY FINGER",174,0,294,MinTargetHeight,()=>SetGestureMode(FaaMultiFingerResize.GestureMode.MultiFingerPinch));
            palmModeButton=Button("Open Palms Mode",panel,"OPEN PALMS",478,0,294,MinTargetHeight,()=>SetGestureMode(FaaMultiFingerResize.GestureMode.OpenPalms));
            statusText=Text("Webcam Status",panel,Status,320,0,594,42,16);
            // Kept for the verification tools; START CAMERA already requests access, so this second button is never shown.
            permissionButton=Button("Request Camera Permission",panel,"ALLOW CAMERA",174,0,294,MinTargetHeight,RequestPermissionByUser);SetActive(permissionButton,false);
            privacyButton=Button("Camera Privacy Settings",panel,"OPEN CAMERA SETTINGS",174,0,294,MinTargetHeight,OpenCameraSettingsByUser);
            nextButton=Button("Webcam Next Device",panel,"NEXT CAMERA",478,0,294,MinTargetHeight,NextCamera);
            SetActive(Button("Webcam Hide",panel,"CLOSE",526,0,188,40,ClosePanel),false);
            deviceText=Text("Webcam Device",panel,"",320,0,594,24,16);deviceText.color=Muted;
            permissionText=Text("Webcam Permission",panel,"",477,0,294,24,16);permissionText.color=Muted;SetActive(permissionText,false);
            phaseKey=rateKey=deviceKey=int.MinValue;contextKey=-1;layoutKey=-1;
            SetActive(privacyButton,false);SetActive(nextButton,false);SetActive(statusText,false);SetActive(deviceText,false);
            LayoutRows(false,false,false,false); // the camera-off layout from the first frame
            var utility=owner.RegisterUtility("camera-controls",CameraCanvas,panel,-90,.58f);
            if(utility!=null)utility.TopAnchorHeight=MaxPanelHeight;
            RefreshPanel();
        }
        private static void PlaceY(Component c,float y)
        {
            if(c==null)return;var r=(RectTransform)c.transform;var p=new Vector2(r.anchoredPosition.x,-y);
            if(r.anchoredPosition!=p)r.anchoredPosition=p;
        }
        /// <summary>Stacks the optional rows below the fixed toggle row and fits the panel height to them (runs only when the set of rows changes).</summary>
        private void LayoutRows(bool live,bool status,bool contextRow,bool note)
        {
            int key=(live?1:0)|(status?2:0)|(contextRow?4:0)|(note?8:0);
            if(key==layoutKey)return;layoutKey=key;
            float y=GestureTop(live);
            PlaceY(gestureLabel,y+11f);y+=28f;
            PlaceY(pinchModeButton,y+MinTargetHeight*.5f);PlaceY(palmModeButton,y+MinTargetHeight*.5f);y+=MinTargetHeight+10f;
            if(status){PlaceY(statusText,y+21f);y+=StatusBlock;}
            if(contextRow)
            {
                float slot=174f;
                foreach(var b in new[]{privacyButton,nextButton})
                    if(b!=null&&b.gameObject.activeSelf){var r=(RectTransform)b.transform;r.anchoredPosition=new Vector2(slot,-(y+MinTargetHeight*.5f));slot+=304f;}
                y+=ContextBlock;
            }
            if(note){PlaceY(deviceText,y+12f);y+=NoteBlock;}
            float height=PanelHeightFor(live,status,contextRow,note);
            panel.sizeDelta=new Vector2(PanelWidth,height);
            if(surface!=null){surface.rectTransform.sizeDelta=new Vector2(PanelWidth,height);PlaceY(surface,height*.5f);}
            if(accentRail!=null){accentRail.rectTransform.sizeDelta=new Vector2(4,height);PlaceY(accentRail,height*.5f);}
        }
        private void RefreshPanel()
        {
            if(panel==null)return;
            bool visible=PanelOpen&&owner!=null&&owner.Initialized;
            if(panel.gameObject.activeSelf!=visible)panel.gameObject.SetActive(visible);
            if(!visible)return;
            if(previewImage.texture!=previewTexture)previewImage.texture=previewTexture;
            if(previewImage.enabled!=(previewTexture!=null))previewImage.enabled=previewTexture!=null;
            SetActive(previewPlaceholder,previewTexture==null);
            if(previewTexture!=null)
            {
                float ratio=Mathf.Min(590f/previewTexture.width,(PreviewHeight-2f)/previewTexture.height);
                previewImage.rectTransform.sizeDelta=new Vector2(previewTexture.width,previewTexture.height)*ratio;
            }
            skeleton.SetFrame(previewTexture!=null&&Time.unscaledTime-lastResultAt<.22f?LatestResult:null);
            if(RecognitionReady)progress.rectTransform.sizeDelta=new Vector2(592*(gesture.State==FaaMultiFingerResize.Phase.Resizing?1:gesture.HoldProgress),4);
            if(Time.unscaledTime<nextPanelUpdate)return;nextPanelUpdate=Time.unscaledTime+.1f;
            bool on=CameraActive||Starting||RecognitionReady||PermissionPending;
            // Progressive disclosure: recognition diagnostics only while recognition runs.
            // While the recognizer starts, the status line says so; the live rows appear once recognition runs.
            SetActive(phaseText,RecognitionReady);SetActive(rateText,RecognitionReady);SetActive(fingerText,RecognitionReady);
            SetActive(progress,RecognitionReady);SetActive(progressTrack,RecognitionReady);
            // The normal off state shows no status line; a permission description is shown only as the access line below.
            bool quietStatus=!on&&(Status==DefaultOffStatus||Status==StoppedStatus||Status==FaaMacCameraPermission.Description(PermissionState));
            SetActive(statusText,!quietStatus);if(!quietStatus)SetText(statusText,Status);
            SetSelected(pinchModeButton,GestureMode==FaaMultiFingerResize.GestureMode.MultiFingerPinch);
            SetSelected(palmModeButton,GestureMode==FaaMultiFingerResize.GestureMode.OpenPalms);
            int phase=!RecognitionReady?(Starting?-2:-1):!owner.EditMode?-3:(int)gesture.State*1000+Mathf.RoundToInt(displayedFactor*100);
            if(phase!=phaseKey)
            {
                phaseKey=phase;
                phaseText.text=phase==-2?"STARTING":phase==-1?"":phase==-3?"LAYOUT LOCKED":gesture.State.ToString().ToUpperInvariant()+"   "+Mathf.RoundToInt(displayedFactor*100)+"%";
            }
            if(RecognitionReady)
            {
                int rate=Mathf.RoundToInt(SampleRate)*10000+Mathf.Clamp(Mathf.RoundToInt(LatestResult?.inferenceMs??0),0,9999);
                if(rate!=rateKey){rateKey=rate;rateText.text=Mathf.RoundToInt(SampleRate)+" samples/s   "+Mathf.RoundToInt(LatestResult?.inferenceMs??0)+" ms CPU";}
                SetText(fingerText,HandSummary(0)+"       "+HandSummary(1));
            }
            SetText(toggleText,CameraActive||RecognitionReady?"STOP CAMERA":Starting||PermissionPending?"CANCEL":"START CAMERA");
            if(toggleButton.interactable!=!owner.NativeXr)toggleButton.interactable=!owner.NativeXr;
            SetCaption(editLockButton,owner.EditMode?"LOCK LAYOUT":"UNLOCK LAYOUT");
            SetText(previewPlaceholder,Starting||RecognitionReady||PermissionPending?"WAITING FOR CAMERA\n\nAllow camera access if macOS asks.":"CAMERA OFF\n\nStart the camera to see your hands.");
            RefreshContextControls(on,!quietStatus);
        }
        /// <summary>
        /// Device and access controls appear only when they can act (OPEN CAMERA SETTINGS only when access is denied or restricted,
        /// NEXT CAMERA only with two or more cameras), with one line naming the problem or the selected camera; visible buttons
        /// re-flow into two slots and the rows re-stack without gaps.
        /// </summary>
        private void RefreshContextControls(bool on,bool status)
        {
            var state=PermissionState;
            string access=AccessLine(state);
            bool settings=access!=null;
            bool next=!on&&devices.Length>1;
            int key=(settings?2:0)|(next?4:0);
            if(key!=contextKey)
            {
                contextKey=key;
                SetActive(permissionButton,false);SetActive(privacyButton,settings);SetActive(nextButton,next);layoutKey=-1;
            }
            bool showLine=!RecognitionReady&&(settings||next);
            int device=showLine?((int)state+8)*100000+(next?deviceIndex+1:0)*100+devices.Length:int.MinValue+1;
            if(device!=deviceKey)
            {
                deviceKey=device;SetActive(deviceText,showLine);
                if(showLine)
                {
                    deviceText.text=settings?access:next&&deviceIndex<devices.Length?"CAMERA: "+devices[deviceIndex].name.ToUpperInvariant():"";
                    deviceText.color=settings?Caution:Muted;
                }
            }
            LayoutRows(RecognitionReady,status,settings||next,showLine);
        }
        private string HandSummary(int index)
        {
            string side=index==0?"LEFT":"RIGHT";
            if(LatestResult?.hands==null||LatestResult.hands.Length<=index||Time.unscaledTime-lastResultAt>.22f)return side+": --";
            var hand=LatestResult.hands[index];if(!FaaMultiFingerResize.Valid(hand))return side+": uncertain";
            string[] names={"INDEX","MIDDLE","RING","LITTLE"};int finger=FaaMultiFingerResize.PinchFinger(hand);
            return side+": "+hand.fingers+" extended / "+(finger>=0?names[finger]+" PINCH":"OPEN");
        }
    }
}
