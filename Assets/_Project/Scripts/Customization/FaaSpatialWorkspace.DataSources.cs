using System.Collections.Generic;
using FAA.XPlaneIntegration.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaWorkspaceUi;

namespace FAA.Customization
{
    public sealed partial class FaaSpatialWorkspace
    {
        private RectTransform dataSourcePage;
        private TMP_Text sourceDetails,sourceCandidates,sourceMode,limitsNote;
        private Button autoSourceButton,localSourceButton;
        private Guard fallbackGuard,stopGuard;
        private readonly List<Guard> dataGuards=new();
        private float nextSourceRefresh;
        /// <summary>Shown while the exceedance limits are the demonstrator set (FaaRotorcraftLimits.Configured == false).</summary>
        public const string DemonstratorLimitsNote="LIMITS: DEMONSTRATOR VALUES (NOT RFM)";
        private void BuildDataSourcePage()
        {
            dataSourcePage=Rect("Data Sources Page",menuRoot,320,352,640,354);
            sourceMode=Text("Source Selection Mode",dataSourcePage,"AUTOMATIC LOCAL DISCOVERY",320,20,600,30,18);
            sourceDetails=Text("Source Details",dataSourcePage,"",320,66,594,60,16);
            // Mode selector: the active mode is filled. Modes that can remove live data are two-step (arm, then confirm).
            autoSourceButton=Button("Auto Sources",dataSourcePage,"AUTO + FALLBACK",116,122,188,MinTargetHeight,()=>XPlaneSourceDiscovery.Active?.SetMode(XPlaneSourceDiscovery.SelectionMode.Automatic));
            localSourceButton=Button("Local Sources Only",dataSourcePage,"LOCAL ONLY",320,122,188,MinTargetHeight,()=>XPlaneSourceDiscovery.Active?.SetMode(XPlaneSourceDiscovery.SelectionMode.LocalOnly));
            fallbackGuard=GuardedButton("Fallback Source Only",dataSourcePage,"FALLBACK ONLY","CONFIRM FALLBACK",524,122,188,MinTargetHeight,
                ()=>XPlaneSourceDiscovery.Active?.SetMode(XPlaneSourceDiscovery.SelectionMode.FallbackOnly));
            Button("Rescan Sources",dataSourcePage,"RESCAN",166,172,292,MinTargetHeight,()=>XPlaneSourceDiscovery.Active?.Rescan());
            Button("Cycle Valid Source",dataSourcePage,"NEXT VALID SOURCE",474,172,292,MinTargetHeight,()=>
            {
                var source=XPlaneSourceDiscovery.Active;if(source==null)return;var ids=source.CandidateIds;
                if(ids.Length>0)source.PreferSource(ids[(System.Array.IndexOf(ids,source.SelectedSourceId)+1)%ids.Length]);
            });
            sourceCandidates=Text("Source Candidates",dataSourcePage,"",320,216,594,40,16);sourceCandidates.color=Muted;
            Text("Read Only Sources",dataSourcePage,"Fallback data is labelled and may come from another simulator.",320,250,594,24,16).color=Muted;
            limitsNote=Text("Limits Source Note",dataSourcePage,DemonstratorLimitsNote,320,276,594,24,16);limitsNote.color=Caution;
            // STOP DATA removes the live flight data feeding the HUD: its own row, well apart from the routine controls.
            stopGuard=GuardedButton("Stop Data Sources",dataSourcePage,"STOP DATA","CONFIRM STOP DATA",320,326,260,MinTargetHeight,
                ()=>XPlaneSourceDiscovery.Active?.SetMode(XPlaneSourceDiscovery.SelectionMode.Stopped));
            dataGuards.Clear();dataGuards.Add(fallbackGuard);dataGuards.Add(stopGuard);
            foreach(var g in dataGuards)g.Button.onClick.AddListener(()=>nextSourceRefresh=0f); // show the new mode at the next 10 Hz refresh
            dataSourcePage.gameObject.SetActive(false);
        }
        public void OpenDataSourceSettings()
        {
            OpenMenu();SetPage(PageData);
            // The 'DATA:' status plate turns the view to Settings; no second turn when it is already in view.
            if(InspectedPanelId!="settings")InspectUtility("settings");
            nextSourceRefresh=0f;RefreshDataSourceControls();
        }
        private void DisarmDataGuards(){foreach(var g in dataGuards)if(g!=null&&g.Armed)g.Disarm();}
        private void RefreshDataSourceControls()
        {
            if(dataSourcePage==null||!dataSourcePage.gameObject.activeInHierarchy)return;
            float now=Time.unscaledTime;
            foreach(var g in dataGuards)g?.Tick(now);
            if(now<nextSourceRefresh)return;
            nextSourceRefresh=now+.5f; // Source descriptions are rebuilt at 2 Hz only while this page is visible.
            var source=XPlaneSourceDiscovery.Active;
            SetText(sourceMode,source==null?"SOURCE DISCOVERY NOT RUNNING":ModeCaption(source.Mode));
            SetText(sourceDetails,source==null?"Start the live FAA bridge to detect data sources.":source.SourceSummary+"\n"+source.Details);
            string candidates="";
            if(source!=null)
            {
                var descriptions=source.CandidateDescriptions;
                candidates=descriptions.Length>0?descriptions[0]+(descriptions.Length>1?"  (+"+(descriptions.Length-1)+" more)":""):source.DiscoveryDiagnostic;
            }
            SetText(sourceCandidates,candidates);
            SetActive(limitsNote,!FaaRotorcraftLimits.Configured);
            var mode=source!=null?source.Mode:(XPlaneSourceDiscovery.SelectionMode)(-1);
            SetSelected(autoSourceButton,mode==XPlaneSourceDiscovery.SelectionMode.Automatic);
            SetSelected(localSourceButton,mode==XPlaneSourceDiscovery.SelectionMode.LocalOnly);
            if(!fallbackGuard.Armed)SetSelected(fallbackGuard.Button,mode==XPlaneSourceDiscovery.SelectionMode.FallbackOnly);
            if(!stopGuard.Armed)SetSelected(stopGuard.Button,mode==XPlaneSourceDiscovery.SelectionMode.Stopped);
        }
        private static string ModeCaption(XPlaneSourceDiscovery.SelectionMode mode)
        {
            switch(mode)
            {
                case XPlaneSourceDiscovery.SelectionMode.Automatic:return "MODE: AUTOMATIC (LOCAL FIRST, THEN FALLBACK)";
                case XPlaneSourceDiscovery.SelectionMode.LocalOnly:return "MODE: LOCAL X-PLANE ONLY";
                case XPlaneSourceDiscovery.SelectionMode.FallbackOnly:return "MODE: FALLBACK ONLY";
                case XPlaneSourceDiscovery.SelectionMode.Stopped:return "MODE: STOPPED - NO LIVE DATA";
                default:return "MODE: "+mode;
            }
        }
    }
}
