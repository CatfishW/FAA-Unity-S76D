using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaWorkspaceUi;
namespace FAA.Customization
{
    public sealed partial class FaaSpatialWorkspace
    {
        private RectTransform symbologyPage;
        private Button digitalVersionButton,classicVersionButton;
        // Two-segment selectors: each option is its own button and the active one is filled, so the control never shows a
        // state as its label (AC 25-11B / HF-STD-001B unambiguous control state). Left segments keep the original names.
        private Button attitudeInsetButton,attitudeSceneButton,motionSmoothButton,motionDirectButton,paletteReferenceButton,palettePilotButton;
        private TMP_Text classicOptionsHeading,classicOnlyNote;
        private int symbologyStateKey=-1;
        /// <summary>Heading of the Classic option rows: names them as Classic-only while Digital is the active style.</summary>
        public static string ClassicOptionsHeading(bool classicActive)=>classicActive?"CLASSIC OPTIONS":"CLASSIC ONLY";
        /// <summary>Classic option rows act only while Classic Analog is the active style (they change nothing in Digital).</summary>
        public static bool ClassicOptionsEnabled(FaaSymbologyVersion version)=>version==FaaSymbologyVersion.ClassicAnalog;
        private void BuildSymbologyPage()
        {
            symbologyPage=Rect("Symbology Versions Page",menuRoot,320,352,640,354);
            Text("Symbology Section",symbologyPage,"FLIGHT DISPLAY STYLE",320,22,592,30,20);
            digitalVersionButton=Button("Digital Symbology",symbologyPage,"DIGITAL",167,72,284,56,()=>SetSymbologyVersion(FaaSymbologyVersion.Digital));
            classicVersionButton=Button("Classic Analog Symbology",symbologyPage,"CLASSIC ANALOG",473,72,284,56,()=>SetSymbologyVersion(FaaSymbologyVersion.ClassicAnalog));
            Text("Digital Description",symbologyPage,"Numeric readouts, linear engine scales",167,118,284,26,16).color=Muted;
            Text("Classic Description",symbologyPage,"Round dials with needles",473,118,284,26,16).color=Muted;
            classicOptionsHeading=Text("Classic Options Heading",symbologyPage,"CLASSIC OPTIONS",320,152,592,24,16);classicOptionsHeading.color=Muted;
            SegmentRow("CENTER",184,"Classic Attitude Choice","ATTITUDE INSET",()=>SetClassicInstrumentAttitude(true),
                "Classic Attitude Scene Cues","SCENE CUES",()=>SetClassicInstrumentAttitude(false),out attitudeInsetButton,out attitudeSceneButton);
            SegmentRow("NEEDLES",232,"Symbology Motion","SMOOTH",()=>SetReducedSymbologyMotion(false),
                "Symbology Motion Direct","DIRECT",()=>SetReducedSymbologyMotion(true),out motionSmoothButton,out motionDirectButton);
            SegmentRow("COLOR",280,"Symbology Palette","REFERENCE",()=>SetPilotSymbologyColor(false),
                "Symbology Palette Pilot","PILOT",()=>SetPilotSymbologyColor(true),out paletteReferenceButton,out palettePilotButton);
            // Shown only while Digital is active: why the rows above are disabled. (The size note lives on INSTRUMENTS.)
            classicOnlyNote=Text("Classic Only Note",symbologyPage,"Choose CLASSIC ANALOG to change these options.",320,328,592,24,16);classicOnlyNote.color=Muted;
            symbologyPage.gameObject.SetActive(false);symbologyStateKey=-1;RefreshSymbologyControls();
        }
        private void SegmentRow(string label,float y,string leftName,string leftCaption,UnityEngine.Events.UnityAction left,
            string rightName,string rightCaption,UnityEngine.Events.UnityAction right,out Button leftButton,out Button rightButton)
        {
            Text(label+" Label",symbologyPage,label,96,y,160,MinTargetHeight,16,true).color=Muted;
            leftButton=Button(leftName,symbologyPage,leftCaption,291,y,210,MinTargetHeight,left);
            rightButton=Button(rightName,symbologyPage,rightCaption,509,y,210,MinTargetHeight,right);
        }
        /// <summary>A Classic option segment: filled when selected and Classic is active; disabled, unfilled and muted in Digital.</summary>
        private static void SetClassicSegment(Button button,bool enabled,bool selected)
        {
            if(button==null)return;
            if(button.interactable!=enabled)button.interactable=enabled;
            SetSelected(button,enabled&&selected);
            var caption=CaptionOf(button);if(caption!=null)SetColor(caption,enabled?Color.white:Muted);
        }
        public void OpenSymbologySettings(){OpenMenu();SetPage(PageDisplay);}
        private void RefreshSymbologyControls()
        {
            if(symbologyPage==null)return;
            bool classic=CurrentSymbology==FaaSymbologyVersion.ClassicAnalog;
            int key=(classic?1:0)|(ClassicInstrumentAttitude?2:0)|(ReducedSymbologyMotion?4:0)|(UsePilotSymbologyColor?8:0);
            if(key==symbologyStateKey)return; // State is shown by fill only; nothing to do at rest.
            symbologyStateKey=key;
            SetSelected(digitalVersionButton,!classic);SetSelected(classicVersionButton,classic);
            // Digital active: the Classic rows change nothing, so they are disabled, unfilled and headed CLASSIC ONLY.
            bool enabled=ClassicOptionsEnabled(CurrentSymbology);
            SetClassicSegment(attitudeInsetButton,enabled,ClassicInstrumentAttitude);SetClassicSegment(attitudeSceneButton,enabled,!ClassicInstrumentAttitude);
            SetClassicSegment(motionSmoothButton,enabled,!ReducedSymbologyMotion);SetClassicSegment(motionDirectButton,enabled,ReducedSymbologyMotion);
            SetClassicSegment(paletteReferenceButton,enabled,!UsePilotSymbologyColor);SetClassicSegment(palettePilotButton,enabled,UsePilotSymbologyColor);
            SetText(classicOptionsHeading,ClassicOptionsHeading(enabled)); // informational, not a caution: stays muted
            SetActive(classicOnlyNote,!enabled);
        }
    }
}
