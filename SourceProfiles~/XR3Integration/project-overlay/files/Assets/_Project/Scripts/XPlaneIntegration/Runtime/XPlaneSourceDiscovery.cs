using System;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.XPlaneIntegration.Runtime
{
    [DefaultExecutionOrder(-40),DisallowMultipleComponent]
    public sealed class XPlaneSourceDiscovery:MonoBehaviour
    {
        public enum SelectionMode{Automatic,LocalOnly,FallbackOnly,Stopped}
        public static XPlaneSourceDiscovery Active{get;private set;}
        public SelectionMode Mode{get;private set;}=SelectionMode.Automatic;
        public string SourceSummary{get;private set;}="SEARCHING FOR LOCAL X-PLANE";
        public string Details{get;private set;}="";
        public string SelectedSourceId{get;private set;}="";
        public bool UsingFallback{get;private set;}
        public bool HasValidatedSource=>!UsingFallback&&!string.IsNullOrEmpty(SelectedSourceId)&&bridge!=null&&bridge.IsFeedHealthy;
        public string ConfigurationPath{get;private set;}
        public string InventorySummary=>engine?.InventoryStatus??"Discovery stopped";
        public string DiscoveryDiagnostic=>engine?.LastDiagnostic??"";
        public string[] CandidateDescriptions=>selector.Describe(XPlaneDiscoveryData.Now);
        public string[] CandidateIds=>selector.ReadyIds(XPlaneDiscoveryData.Now);
        private XPlane12ApiHudBridge bridge;
        private XPlaneSourceConfig config;
        private XPlaneDiscoveryEngine engine;
        private readonly XPlaneSourceSelector selector=new();
        private double started,lastValid,lastApplied,nextUi;
        private bool initialized;
        private bool suspendedForBridge;
        private string preferred="",terrainSource="";
        private XPlaneTerrainStreamer terrainInstance;
        private bool restartOnEnable;
        private Canvas statusCanvas;
        private TMP_Text sourceText;

        public void Initialize(XPlane12ApiHudBridge target)
        {
            if(initialized)return;
            if(Active!=null&&Active!=this){enabled=false;return;}
            Active=this;bridge=target;initialized=true;
            ConfigurationPath=Path.Combine(Application.persistentDataPath,"DataSources.json");
            config=XPlaneSourceConfig.Load(Path.Combine(Application.streamingAssetsPath,"FAA","DataSources.json"),ConfigurationPath,out var error);
            if(config==null){Mode=SelectionMode.Stopped;SourceSummary="SOURCE CONFIGURATION ERROR";Details=error;bridge.SetDiscoveryWaiting("configuration-error");return;}
            preferred=config.preferredSource;
            int saved=PlayerPrefs.GetInt("FAA.SourceDiscovery.Mode.v1",(int)(config.enabled?SelectionMode.Automatic:SelectionMode.FallbackOnly));
            Mode=Enum.IsDefined(typeof(SelectionMode),saved)?(SelectionMode)saved:SelectionMode.Automatic;
            StartDiscovery();
        }
        private void StartDiscovery()
        {
            engine?.Dispose();engine=null;selector.Reset();lastApplied=0;SelectedSourceId="";UsingFallback=false;
            started=lastValid=XPlaneDiscoveryData.Now;
            SourceSummary="SEARCHING FOR LOCAL X-PLANE";Details="Checking process metadata, UDP, native Web API and configured MQTT brokers.";
            bridge.SetDiscoveryWaiting("discovering");
            if(Mode==SelectionMode.Stopped){bridge.StopBridge();SourceSummary="DATA SOURCE STOPPED";Details="Discovery and flight-data application stopped by the operator.";return;}
            if(Mode!=SelectionMode.FallbackOnly)
            {
                string home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string app=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                engine=new XPlaneDiscoveryEngine(config,home,app);
            }
            else SwitchToFallback();
        }
        public void SetMode(SelectionMode mode)
        {
            if(!Enum.IsDefined(typeof(SelectionMode),mode)||!initialized||config==null)return;
            Mode=mode;PlayerPrefs.SetInt("FAA.SourceDiscovery.Mode.v1",(int)mode);PlayerPrefs.Save();preferred="";StartDiscovery();
        }
        public void PreferSource(string id){preferred=id??"";}
        public void Rescan()
        {
            var updated=XPlaneSourceConfig.Load(Path.Combine(Application.streamingAssetsPath,"FAA","DataSources.json"),ConfigurationPath,out string error);
            if(updated==null){Details=error;return;}
            config=updated;preferred=config.preferredSource;StartDiscovery();
        }
        private void Update()
        {
            if(!initialized||bridge==null)return;
            if(!bridge.isActiveAndEnabled){engine?.Dispose();engine=null;suspendedForBridge=true;return;}
            if(suspendedForBridge){suspendedForBridge=false;StartDiscovery();}
            double now=XPlaneDiscoveryData.Now;
            if(XPlaneTerrainStreamer.Active!=null&&terrainInstance!=XPlaneTerrainStreamer.Active)
                RouteTerrain(!UsingFallback&&!string.IsNullOrEmpty(SelectedSourceId));
            if(engine!=null)foreach(var frame in engine.Drain())selector.Observe(frame);
            if(Mode!=SelectionMode.Stopped&&Mode!=SelectionMode.FallbackOnly&&config!=null)
            {
                var chosen=selector.Select(now,preferred);
                if(chosen!=null)
                {
                    lastValid=now;
                    if(UsingFallback||SelectedSourceId!=chosen.Id||chosen.Received>lastApplied)
                    {
                        bool changed=UsingFallback||SelectedSourceId!=chosen.Id;
                        bridge.ApplyDiscoveredFrame(chosen);UsingFallback=false;SelectedSourceId=chosen.Id;lastApplied=chosen.Received;
                        SourceSummary=chosen.Label;
                        Details=chosen.Transport+" | "+chosen.Values.Count+" mapped datarefs"+(chosen.Topic!=null?"\nTopic: "+XPlaneDiscoveryData.SafeLabel(chosen.Topic,130):"")+"\n"+
                            (chosen.LocalProcessVerified?"X-Plane 12 identity and live clock validated.":"Telemetry schema validated. Broker address does not prove simulator location.");
                        if(changed)RouteTerrain(true);
                    }
                }
                else if(!UsingFallback&&now-started>=config.startupGraceSeconds&&now-lastValid>=config.sourceLossSeconds)
                {
                    if(Mode==SelectionMode.Automatic&&config.allowFallback&&string.IsNullOrEmpty(preferred))SwitchToFallback();
                    else{bridge.SetDiscoveryWaiting("local-only-waiting");SourceSummary="NO VALID LOCAL SOURCE";Details="Local-only/pinned source mode: fallback is not used.";SelectedSourceId="";}
                }
            }
            if(UsingFallback)
            {
                SourceSummary=bridge.IsFeedHealthy?"FALLBACK FEED - NOT LOCAL SIMULATOR":"FALLBACK UNAVAILABLE - NO FLIGHT DATA";
                Details="Existing configured feed: "+config.fallbackUrl+"\n"+(bridge.IsFeedHealthy?"Local discovery continues. Fallback data may belong to another simulator.":"No reachable feed. Check local X-Plane, broker or authorized telemetry tunnel.");
            }
            if(now>=nextUi){nextUi=now+.25;RefreshStatus();}
        }
        private void SwitchToFallback()
        {
            if(config==null)return;
            UsingFallback=true;SelectedSourceId="";bridge.StartExistingFallback(config.fallbackUrl);RouteTerrain(false);
        }
        private void RouteTerrain(bool local)
        {
            if(config==null)return;
            string next=local?config.localTerrainUrl:"";
            // Do not show a previous simulator's terrain after changing flight-data origins.
            var terrain=XPlaneTerrainStreamer.Active;
            if(terrain!=null&&(terrainSource!=next||terrainInstance!=terrain))
            {terrain.UseDiscoveredSource(next);terrainInstance=terrain;terrainSource=next;}
        }
        /// <summary>
        /// Chip severity from the data path state (14 CFR 29.1322 / AC 25.1322-1 colour use): a validated live local feed
        /// is Nominal; searching inside the startup window is quiet Status; a healthy fallback feed is a Caution (data may
        /// belong to another simulator); stale, stopped, unavailable or misconfigured data is a Warning because every flight
        /// instrument then shows invalid data.
        /// </summary>
        public static FAA.Customization.FaaChromeSeverity ClassifySource(bool configError,SelectionMode mode,bool usingFallback,bool feedHealthy,bool hasSelected,bool searching)
        {
            if(configError||mode==SelectionMode.Stopped)return FAA.Customization.FaaChromeSeverity.Warning;
            if(usingFallback)return feedHealthy?FAA.Customization.FaaChromeSeverity.Caution:FAA.Customization.FaaChromeSeverity.Warning;
            if(hasSelected)return feedHealthy?FAA.Customization.FaaChromeSeverity.Nominal:FAA.Customization.FaaChromeSeverity.Warning;
            return searching?FAA.Customization.FaaChromeSeverity.Status:FAA.Customization.FaaChromeSeverity.Warning;
        }
        /// <summary>Two- or three-word chip wording for the same states (details stay on the settings DATA page).</summary>
        public static string ChipText(bool configError,SelectionMode mode,bool usingFallback,bool feedHealthy,bool hasSelected,bool searching)
        {
            if(configError)return "DATA CONFIG ERROR";
            if(mode==SelectionMode.Stopped)return "DATA STOPPED";
            if(usingFallback)return feedHealthy?"FALLBACK DATA":"NO FLIGHT DATA";
            if(hasSelected)return feedHealthy?"DATA LIVE":"DATA STALE";
            return searching?"DATA SEARCHING":"NO LOCAL SOURCE";
        }
        private string statusDetail,detailSummary,detailText,legacyText;
        private void RefreshStatus()
        {
            bool configError=config==null;
            bool healthy=bridge!=null&&bridge.IsFeedHealthy;
            bool selected=!UsingFallback&&!string.IsNullOrEmpty(SelectedSourceId);
            double now=XPlaneDiscoveryData.Now;
            bool searching=!configError&&!selected&&!UsingFallback&&Mode!=SelectionMode.Stopped&&
                (now-started<config.startupGraceSeconds||now-lastValid<config.sourceLossSeconds);
            var severity=ClassifySource(configError,Mode,UsingFallback,healthy,selected,searching);
            string chipText=ChipText(configError,Mode,UsingFallback,healthy,selected,searching);
            if(detailSummary!=SourceSummary||detailText!=Details)
            {detailSummary=SourceSummary;detailText=Details;statusDetail=SourceSummary+"\n"+Details;}
            // Screen-chrome unit: status lives in the single merged chip of the pilot chrome bar (click opens the DATA page).
            var chrome=FAA.Customization.FaaPilotChrome.Ensure();
            if(chrome!=null)
            {
                chrome.ReportStatus("data",severity,chipText,statusDetail,0);
                if(statusCanvas!=null){Destroy(statusCanvas.gameObject);statusCanvas=null;sourceText=null;legacyText=null;}
                return;
            }
            // Legacy chip for hosts without the chrome: same wording and severity colours, legible size.
            if(statusCanvas==null)
            {
                var root=new GameObject("FAA Data Source Status",typeof(RectTransform),typeof(Canvas),typeof(UnityEngine.UI.CanvasScaler),typeof(FAA.Customization.FaaCanvasPixelRaycaster));
                root.transform.SetParent(transform,false);statusCanvas=root.GetComponent<Canvas>();statusCanvas.sortingOrder=7210;
                var scaler=root.GetComponent<UnityEngine.UI.CanvasScaler>();scaler.uiScaleMode=UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
                var plate=new GameObject("Data Source",typeof(RectTransform),typeof(Image),typeof(Button));plate.transform.SetParent(root.transform,false);
                var rect=(RectTransform)plate.transform;rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,0);rect.anchoredPosition=new Vector2(18,18);rect.sizeDelta=new Vector2(300,34);
                plate.GetComponent<Image>().color=FAA.Customization.FaaHudStyle.ChromeButton;
                plate.AddComponent<Outline>().effectColor=FAA.Customization.FaaHudStyle.ChromeOutline;
                plate.GetComponent<Button>().onClick.AddListener(()=>{var w=FAA.Customization.FaaSpatialWorkspace.Current;if(w!=null)w.OpenDataSourceSettings();});
                var textObject=new GameObject("Active source label",typeof(RectTransform));textObject.transform.SetParent(rect,false);
                sourceText=textObject.AddComponent<TextMeshProUGUI>();sourceText.font=TMP_Settings.defaultFontAsset;sourceText.fontSize=FAA.Customization.FaaHudStyle.Chrome;sourceText.richText=false;sourceText.raycastTarget=false;
                sourceText.rectTransform.anchorMin=Vector2.zero;sourceText.rectTransform.anchorMax=Vector2.one;sourceText.rectTransform.offsetMin=new Vector2(10,1);sourceText.rectTransform.offsetMax=new Vector2(-8,-1);sourceText.alignment=TextAlignmentOptions.MidlineLeft;
            }
            Camera view=Camera.main;bool xr=view!=null&&view.stereoEnabled;
            var mode=xr?RenderMode.ScreenSpaceCamera:RenderMode.ScreenSpaceOverlay;
            if(statusCanvas.renderMode!=mode)statusCanvas.renderMode=mode;statusCanvas.worldCamera=xr?view:null;if(xr)statusCanvas.planeDistance=1.2f;
            if(!ReferenceEquals(legacyText,chipText)){legacyText=chipText;sourceText.text=chipText;} // a status, not a control: no chevron
            sourceText.color=severity==FAA.Customization.FaaChromeSeverity.Nominal?FAA.Customization.FaaHudStyle.White:FAA.Customization.FaaPilotChrome.ColorFor(severity);
        }
        private void OnDisable()
        {
            restartOnEnable=initialized;
            engine?.Dispose();engine=null;if(bridge!=null&&initialized)bridge.StopBridge();
            if(statusCanvas!=null)Destroy(statusCanvas.gameObject);statusCanvas=null;legacyText=null;
            FAA.Customization.FaaPilotChrome.Current?.ClearStatus("data"); // never leave a stale data state on the chip
            if(Active==this)Active=null;
        }
        private void OnDestroy(){engine?.Dispose();}
        private void OnEnable()
        {
            if(!restartOnEnable||!initialized||config==null||bridge==null)return;
            if(Active!=null&&Active!=this){enabled=false;return;}
            restartOnEnable=false;Active=this;StartDiscovery();
        }
    }
}
