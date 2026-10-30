using FAA.XPlaneIntegration.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization
{
    /// <summary>
    /// One shared, rate-limited read of the X-Plane bridge for the head-fixed Digital symbology (FMA, heading tape).
    /// Capturing once per 1/15 s for every consumer keeps the per-frame cost and allocations out of the render loop.
    /// Returns false when no bridge exists: callers then show a flagged, not a plausible, state.
    /// </summary>
    public static class FaaHudLiveSample
    {
        public const float SampleInterval=1f/15f;
        private static XPlane12ApiHudBridge bridge;
        private static float nextBridgeSearch=float.NegativeInfinity,nextSample=float.NegativeInfinity;
        private static FaaAnalogFlightSample latest;
        private static bool variationValid;

        /// <summary>True when the latest sample carries a measured magnetic heading, so TRUE/MAG conversion is real, not a zero default.</summary>
        public static bool MagneticVariationValid=>variationValid&&latest.Fresh;

        public static bool TryGet(out FaaAnalogFlightSample sample)
        {
            float now=Time.unscaledTime;
            if(bridge==null&&now>=nextBridgeSearch){nextBridgeSearch=now+1f;bridge=Object.FindAnyObjectByType<XPlane12ApiHudBridge>();}
            // A disabled bridge is not the data source: callers fall back to their local sources (or flag the data).
            if(bridge==null||!bridge.isActiveAndEnabled){latest=default;variationValid=false;sample=default;return false;}
            if(now>=nextSample||nextSample-now>1f)
            {
                nextSample=now+SampleInterval;
                latest=FaaAnalogFlightSample.Capture(bridge);
                var aircraft=bridge.LatestSnapshot?.Aircraft;
                variationValid=FaaAnalogFlightSample.Try(aircraft,"sim/flightmodel/position/mag_psi",out _);
            }
            sample=latest;return true;
        }

        /// <summary>Test hook: forget the cached bridge and sample.</summary>
        public static void Reset(){bridge=null;latest=default;variationValid=false;nextBridgeSearch=nextSample=float.NegativeInfinity;}
    }

    /// <summary>The pilot-selected symbology colour (SymbologyColorManager), so generated symbology matches the rest of the HUD.</summary>
    public static class FaaHudPilotColor
    {
        private static SymbologyColorManager manager;
        private static float nextSearch=float.NegativeInfinity;
        public static Color Resolve(Color fallback)
        {
            if(!Application.isPlaying)return fallback;
            float now=Time.unscaledTime;
            if(manager==null&&now>=nextSearch){nextSearch=now+2f;manager=Object.FindAnyObjectByType<SymbologyColorManager>();}
            if(manager==null)return fallback;
            Color c=manager.CurrentColor;
            return c.a<.05f?fallback:new Color(c.r,c.g,c.b,1f);
        }
    }

    /// <summary>
    /// Live Digital flight mode annunciator (zone Z1, top centre). Replaces the static 'Bank Scale/CRP' artwork, which showed fixed,
    /// axis-wrong modes. Fixed columns C | R | P | STATUS: active mode on top (green, 28 ref), armed mode below (cyan, 20 ref) only when it
    /// differs from the active mode. A newly engaged mode is boxed for 10 s (AC 25.1329-1C). An AP/FD disengagement shows a boxed "AP OFF"
    /// that flashes for 5 s, stays steady to 10 s, then is removed. Stale data shows the "FMA --" flag and no modes.
    /// Each non-empty mode column carries the same small, dim axis prefix as Classic (<see cref="FaaClassicAnalogHud.FmaPrefixes"/>,
    /// e.g. "R:" and "P:"), drawn left of the mode and outside its change box, so both styles read the same.
    /// Armed modes are removed in an unusual attitude (AC 25-11B declutter; same rule as Classic).
    /// The C column stays empty: no collective-mode source exists and modes are never inferred.
    /// Intensity: an essential awareness readout, so it keeps at least <see cref="FaaHudInspection.AwarenessHudIntensity"/> while a side
    /// panel is inspected.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(12490)]
    [AddComponentMenu("FAA/Customization/FAA Flight Mode Annunciator")]
    public sealed class FaaFlightModeAnnunciator : MonoBehaviour
    {
        public const string ObjectName="FAA Flight Mode Annunciator";
        public const string ApOffText="AP OFF";
        /// <summary>Zone Z1 in reference pixels: x 650-1270, y 14-80 from the top.</summary>
        public const float TopFromScreenTop=16f, Width=600f, Height=64f, ColumnWidth=150f;
        public const float ActiveSize=28f, ArmedSize=20f, BoxStroke=2f, BoxHeight=32f;
        /// <summary>Axis prefix: small and dim (quiet alpha), clear of the change box by <see cref="PrefixGap"/>.</summary>
        public const float PrefixSize=18f, PrefixWidth=36f, PrefixGap=4f;
        public const float ActiveY=-17f, ArmedY=-48f;
        private static readonly float[] ColumnX={-225f,-75f,75f,225f};
        private static readonly string[] ColumnNames={"C","R","P","Status"};

        private readonly TMP_Text[] active=new TMP_Text[4],armed=new TMP_Text[4],prefix=new TMP_Text[3];
        private readonly string[] prefixText=new string[3];
        private readonly string[] prefixShown=new string[3];
        private readonly Image[] boxes=new Image[16];
        private readonly bool[] boxShown=new bool[4];
        private readonly float[] boxWidth=new float[4];
        private readonly FaaFmaChangeTracker tracker=new FaaFmaChangeTracker();
        private RectTransform content;
        private CanvasGroup contentGroup;
        private Canvas canvas;
        private Material halo;
        private FaaFma current=new FaaFma(false,false,"","","","","","",FaaFlightModeAnnunciation.StaleStatus);
        private float nextSample=float.NegativeInfinity,appliedScale=-1f;
        private bool built;

        public FaaFma Current=>current;
        public string ActiveText(int column)=>column>=0&&column<4&&active[column]!=null?active[column].text:"";
        public string ArmedText(int column)=>column>=0&&column<4&&armed[column]!=null?armed[column].text:"";
        /// <summary>Axis prefix shown for a mode column ("" when the column is empty or for STATUS).</summary>
        public string PrefixText(int column)=>column>=0&&column<3&&prefix[column]!=null&&prefix[column].gameObject.activeSelf?prefix[column].text:"";
        public bool BoxShown(int column)=>column>=0&&column<4&&boxShown[column];
        /// <summary>Renderer alpha of the status text (0 during the dark phase of the AP OFF flash).</summary>
        public float StatusAlpha=>active[3]!=null?active[3].canvasRenderer.GetAlpha():0f;

        /// <summary>Finds or creates the annunciator as a direct child of 'Second Interation GUI' so the Digital style gate also hides it in Classic.</summary>
        public static FaaFlightModeAnnunciator Ensure(Transform hudRoot)
        {
            if(hudRoot==null)return null;
            Transform existing=hudRoot.Find(ObjectName);
            GameObject go=existing!=null?existing.gameObject:new GameObject(ObjectName,typeof(RectTransform));
            if(go.transform.parent!=hudRoot)go.transform.SetParent(hudRoot,false);
            if(!go.activeSelf)go.SetActive(true);
            var fma=go.GetComponent<FaaFlightModeAnnunciator>();
            if(fma==null)fma=go.AddComponent<FaaFlightModeAnnunciator>();
            fma.Build();
            return fma;
        }

        private void Awake()=>Build();
        private void OnEnable(){Build();tracker.Reset();nextSample=float.NegativeInfinity;}

        public void Build()
        {
            var rt=transform as RectTransform;
            if(rt==null)return;
            rt.anchorMin=rt.anchorMax=new Vector2(.5f,.5f);rt.pivot=new Vector2(.5f,1f);
            rt.sizeDelta=new Vector2(Width,Height);rt.localRotation=Quaternion.identity;
            if(!built)
            {
                // 1 local unit = 1 reference pixel under the 540x-scaled flight HUD root. Set once: a workspace module may scale it later.
                float parentScale=transform.parent!=null?Mathf.Abs(transform.parent.localScale.x):1f;
                rt.localScale=Vector3.one/Mathf.Max(.0001f,parentScale);
            }
            content=Child(transform,"FMA Content");
            content.anchorMin=Vector2.zero;content.anchorMax=Vector2.one;content.offsetMin=content.offsetMax=Vector2.zero;
            content.pivot=new Vector2(.5f,1f);content.localScale=Vector3.one;content.localRotation=Quaternion.identity;
            contentGroup=content.GetComponent<CanvasGroup>();
            if(contentGroup==null)contentGroup=content.gameObject.AddComponent<CanvasGroup>();
            contentGroup.interactable=false;contentGroup.blocksRaycasts=false;
            for(int i=0;i<4;i++)
            {
                active[i]=Text(ColumnNames[i]+" Active",new Vector2(ColumnX[i],ActiveY),new Vector2(ColumnWidth-4f,BoxHeight),FontStyles.Bold);
                armed[i]=Text(ColumnNames[i]+" Armed",new Vector2(ColumnX[i],ArmedY),new Vector2(ColumnWidth-4f,24f),FontStyles.Normal);
                for(int s=0;s<4;s++)
                {
                    var r=Child(content,ColumnNames[i]+" Box "+s);
                    var img=r.GetComponent<Image>();
                    if(img==null)img=r.gameObject.AddComponent<Image>();
                    img.raycastTarget=false;img.sprite=null;img.enabled=boxShown[i];
                    r.anchorMin=r.anchorMax=new Vector2(.5f,1f);r.pivot=new Vector2(.5f,.5f);r.localScale=Vector3.one;
                    boxes[i*4+s]=img;
                }
                if(boxWidth[i]<=0f)boxWidth[i]=60f;
                LayoutBox(i);
            }
            for(int i=0;i<3;i++)
            {
                // Same prefix strings as Classic, trimmed: the gap replaces the trailing space. Rich text so a Classic-side markup change renders.
                string p=FaaClassicAnalogHud.FmaPrefixes!=null&&i<FaaClassicAnalogHud.FmaPrefixes.Length?FaaClassicAnalogHud.FmaPrefixes[i]:null;
                prefixText[i]=string.IsNullOrWhiteSpace(p)?ColumnNames[i]+":":p.Trim();
                prefix[i]=Text(ColumnNames[i]+" Prefix",new Vector2(ColumnX[i],ActiveY),new Vector2(PrefixWidth,BoxHeight),FontStyles.Normal);
                prefix[i].alignment=TextAlignmentOptions.MidlineRight;prefix[i].richText=true;
                if(prefix[i].text!=prefixText[i])prefix[i].text=prefixText[i];
                prefixShown[i]=null;
                LayoutPrefix(i);
                ShowPrefix(i,current.Active(i).Length>0);
            }
            ApplyFontFloors(true);
            FaaHudKeepOutRegion.Ensure(gameObject,"fma",FaaKeepOutKind.Symbology,6f);
            built=true;
            Place();
        }

        private TMP_Text Text(string name,Vector2 at,Vector2 size,FontStyles style)
        {
            var rt=Child(content,name);
            var t=rt.GetComponent<TextMeshProUGUI>();
            bool created=t==null;
            if(created)t=rt.gameObject.AddComponent<TextMeshProUGUI>();
            if(t.font==null&&TMP_Settings.defaultFontAsset!=null)t.font=TMP_Settings.defaultFontAsset;
            rt.anchorMin=rt.anchorMax=new Vector2(.5f,1f);rt.pivot=new Vector2(.5f,.5f);
            rt.anchoredPosition=at;rt.sizeDelta=size;rt.localScale=Vector3.one;
            t.fontStyle=style;t.alignment=TextAlignmentOptions.Midline;t.enableAutoSizing=false;
            t.textWrappingMode=TextWrappingModes.NoWrap;t.overflowMode=TextOverflowModes.Overflow;
            t.raycastTarget=false;t.richText=false;t.enableVertexGradient=false;
            if(created)t.text="";
            return t;
        }

        private static RectTransform Child(Transform parent,string name)
        {
            Transform found=parent.Find(name);
            GameObject go=found!=null?found.gameObject:new GameObject(name,typeof(RectTransform));
            if(go.transform.parent!=parent)go.transform.SetParent(parent,false);
            return (RectTransform)go.transform;
        }

        /// <summary>Text floors in on-screen reference pixels, so a reduced module scale cannot shrink text below the legibility minimum.</summary>
        private void ApplyFontFloors(bool force)
        {
            float s=EffectiveScale();
            if(!force&&Mathf.Abs(s-appliedScale)<.01f)return;
            appliedScale=s;
            for(int i=0;i<4;i++)
            {
                if(active[i]!=null)active[i].fontSize=FaaHudStyle.Legible(ActiveSize,FaaHudStyle.Secondary/s);
                if(armed[i]!=null)armed[i].fontSize=FaaHudStyle.Legible(ArmedSize,FaaHudStyle.MinLabel/s);
                if(i<3&&prefix[i]!=null)prefix[i].fontSize=FaaHudStyle.Legible(PrefixSize,FaaHudStyle.MinLabel/s);
            }
            if(active[0]!=null&&active[0].font!=null)halo=FaaHudStyle.HaloMaterial(active[0].font);
            for(int i=0;i<4;i++)RefreshBoxWidth(i);
        }

        private float EffectiveScale()
        {
            if(canvas==null)canvas=GetComponentInParent<Canvas>();
            if(canvas==null)return 1f;
            float root=Mathf.Abs(canvas.rootCanvas.transform.lossyScale.y);
            float own=Mathf.Abs(transform.lossyScale.y);
            float s=root>1e-6f?own/root:1f;
            return float.IsNaN(s)||s<=.05f?1f:s;
        }

        private void LateUpdate()
        {
            float now=Time.unscaledTime;
            if(now>=nextSample||nextSample-now>1f)
            {
                nextSample=now+FaaHudLiveSample.SampleInterval;
                FaaHudLiveSample.TryGet(out var sample);
                Present(FaaFlightModeAnnunciation.Compose(sample),now);
            }
            else Render(now);
            ApplyFontFloors(false);
            Place();
            // Essential awareness readout: never dimmed below the awareness floor while a side panel is inspected.
            float k=Mathf.Max(FaaSpatialWorkspace.ForwardIntensityFor(transform),FaaHudInspection.AwarenessHudIntensity);
            if(contentGroup!=null&&Mathf.Abs(contentGroup.alpha-k)>.002f)contentGroup.alpha=k;
        }

        /// <summary>Feeds one composed FMA sample. Public for deterministic tests.</summary>
        public void Present(FaaFma fma,float now)
        {
            if(!built)Build();
            current=fma;tracker.Update(fma,now);Render(now);
        }

        /// <summary>Applies text, colour and box state. Text and colour are assigned only when they change.</summary>
        public void Render(float now)=>Render(now,FaaRotorcraftConformalLayer.UnusualAttitudeActive);

        /// <summary>As <see cref="Render(float)"/>, with the unusual-attitude declutter state passed in (armed modes removed while it is set).</summary>
        public void Render(float now,bool unusualAttitude)
        {
            if(!built)return;
            Color green=FaaHudStyle.WithAlpha(FaaHudPilotColor.Resolve(FaaHudStyle.Green),1f);
            Color cyan=FaaHudStyle.WithAlpha(FaaHudStyle.Cyan,.92f);
            Color quiet=FaaHudStyle.WithAlpha(green,FaaHudStyle.MinQuietAlpha);
            for(int i=0;i<3;i++)
            {
                string a=current.Active(i);
                SetText(i,active[i],a);SetText(-1,armed[i],unusualAttitude?"":current.Armed(i));
                Style(active[i],green,1f);Style(armed[i],cyan,1f);
                ShowPrefix(i,a.Length>0);Style(prefix[i],quiet,1f);
                SetBox(i,current.Valid&&a.Length>0&&tracker.BoxVisible(i,now),green,1f);
            }
            string status;Color statusColor;bool box;float alpha=1f;
            if(!current.Valid){status=FaaFlightModeAnnunciation.StaleStatus;statusColor=FaaHudStyle.Amber;box=false;}
            else if(tracker.ApOffVisible(now))
            {
                status=ApOffText;statusColor=FaaHudStyle.Amber;box=true;
                alpha=tracker.ApOffDrawn(now)?1f:0f;
            }
            else{status=current.Status;statusColor=green;box=status.Length>0&&tracker.BoxVisible(3,now);}
            SetText(3,active[3],status);SetText(-1,armed[3],"");
            Style(active[3],statusColor,alpha);Style(armed[3],cyan,1f);
            SetBox(3,box,statusColor,alpha);
        }

        private void SetText(int boxColumn,TMP_Text t,string value)
        {
            if(t==null)return;
            value??="";
            if(t.text==value)return;
            t.text=value;
            if(boxColumn>=0)RefreshBoxWidth(boxColumn);
        }

        private void RefreshBoxWidth(int column)
        {
            var t=active[column];
            if(t==null)return;
            float w=string.IsNullOrEmpty(t.text)?60f:t.GetPreferredValues(t.text).x+14f;
            w=Mathf.Clamp(w,40f,ColumnWidth-2f);
            if(Mathf.Abs(w-boxWidth[column])<.25f)return;
            boxWidth[column]=w;LayoutBox(column);
            if(column<3)LayoutPrefix(column);
        }

        /// <summary>Right edge of the prefix sits <see cref="PrefixGap"/> outside the mode's change box, so the box frames the mode only.</summary>
        private void LayoutPrefix(int column)
        {
            if(column<0||column>=3||prefix[column]==null)return;
            float right=ColumnX[column]-boxWidth[column]*.5f-PrefixGap;
            var at=new Vector2(right-PrefixWidth*.5f,ActiveY);
            if(prefix[column].rectTransform.anchoredPosition!=at)prefix[column].rectTransform.anchoredPosition=at;
        }

        private void ShowPrefix(int column,bool visible)
        {
            var t=prefix[column];
            if(t==null)return;
            if(t.gameObject.activeSelf!=visible)t.gameObject.SetActive(visible);
            string s=visible?prefixText[column]:"";
            if(!ReferenceEquals(prefixShown[column],s)){prefixShown[column]=s;if(visible&&t.text!=s)t.text=s;}
        }

        private void LayoutBox(int column)
        {
            float w=boxWidth[column],h=BoxHeight,x=ColumnX[column],y=ActiveY;
            Place(boxes[column*4],new Vector2(x,y+h*.5f),new Vector2(w,BoxStroke));
            Place(boxes[column*4+1],new Vector2(x,y-h*.5f),new Vector2(w,BoxStroke));
            Place(boxes[column*4+2],new Vector2(x-w*.5f,y),new Vector2(BoxStroke,h));
            Place(boxes[column*4+3],new Vector2(x+w*.5f,y),new Vector2(BoxStroke,h));
        }
        private static void Place(Image img,Vector2 at,Vector2 size)
        {
            if(img==null)return;
            img.rectTransform.anchoredPosition=at;img.rectTransform.sizeDelta=size;
        }

        private void SetBox(int column,bool visible,Color color,float alpha)
        {
            boxShown[column]=visible;
            for(int s=0;s<4;s++)
            {
                var img=boxes[column*4+s];
                if(img==null)continue;
                if(img.enabled!=visible)img.enabled=visible;
                if(!visible)continue;
                if(img.color!=color)img.color=color;
                var cr=img.canvasRenderer;
                if(!Mathf.Approximately(cr.GetAlpha(),alpha))cr.SetAlpha(alpha);
            }
        }

        /// <summary>
        /// Keeps the colour, halo and renderer tint. A palette refresh in SymbologyColorManager may rewrite TMP colour, face colour or
        /// renderer tint; those are restored here with cheap comparisons instead of rebuilding text every frame.
        /// </summary>
        private void Style(TMP_Text t,Color color,float rendererAlpha)
        {
            if(t==null)return;
            if(t.color!=color)t.color=color;
            if(halo!=null&&t.fontSharedMaterial!=halo){t.fontSharedMaterial=halo;t.UpdateMeshPadding();}
            var cr=t.canvasRenderer;Color rc=cr.GetColor();
            if(rc.r<.999f||rc.g<.999f||rc.b<.999f||!Mathf.Approximately(rc.a,rendererAlpha))cr.SetColor(new Color(1f,1f,1f,rendererAlpha));
        }

        /// <summary>Pins the top edge in zone Z1, independent of the flight HUD root's authored position or any projection rotation.</summary>
        private void Place()
        {
            if(canvas==null)canvas=GetComponentInParent<Canvas>();
            if(canvas==null)return;
            var root=canvas.rootCanvas.transform as RectTransform;
            if(root==null)return;
            Rect r=root.rect;
            if(r.height<100f||r.width<100f)return;
            var target=new Vector3(r.center.x,r.yMax-TopFromScreenTop,0f);
            var rt=(RectTransform)transform;
            Vector3 local=root.InverseTransformPoint(rt.position);
            if((new Vector2(local.x,local.y)-new Vector2(target.x,target.y)).sqrMagnitude>.0001f)rt.position=root.TransformPoint(target);
            if(Quaternion.Angle(rt.rotation,root.rotation)>.01f)rt.rotation=root.rotation;
        }
    }
}
