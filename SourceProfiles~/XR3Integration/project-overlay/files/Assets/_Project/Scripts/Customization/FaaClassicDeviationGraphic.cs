using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaAnalogVector;
namespace FAA.Customization
{
    /// <summary>
    /// LOC / G/S dots and the reference VSI. LOC and G/S are drawn only while guidance is expected or valid (no permanent
    /// empty scales); when expected but lost, a boxed amber mnemonic replaces the diamond. The mesh rebuilds only when its inputs change.
    /// The VSI uses the Digital caption and number format: caption "VS FPM" above the scale, signed readout below it at 50 FPM
    /// resolution with a true minus sign (<see cref="FaaEngineInstrumentGraphic.FormatVerticalSpeed"/>). The readout is an awareness
    /// readout (alpha <see cref="ReadoutAlpha"/>); the scale and pointer use the tint alpha.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaClassicDeviationGraphic:MaskableGraphic
    {
        public enum Scale { Localizer,Glideslope,VerticalSpeed }
        public const float DotSpacing=36f,DotRadius=6f,FullScaleDots=2.5f;
        private Scale kind;private float value;private bool valid;
        public const float VsiRightRailX = FaaClassicVsiGeometry.RightRailX;
        public const float VsiLabelLeftX = FaaClassicVsiGeometry.LabelLeftX;
        public const float VsiLabelWidth = FaaClassicVsiGeometry.LabelWidth;
        private TMP_Text label,detail;private TMP_Text[] scaleLabels;
        private bool initialized,lastExpected;private Color lastTint;private string lastMode;
        private int lastRate=int.MinValue;private bool lastOffScale;private string cachedRate="";
        public const string VsiCaption="VS FPM";
        /// <summary>Alpha of the VSI readout (awareness readout). Negative: same as the tint alpha.</summary>
        public float ReadoutAlpha {get;set;}=-1f;
        /// <summary>LOC/G/S only: true when the pilot expects guidance (valid signal, or the mode is engaged/armed). Ignored by the VSI.</summary>
        public bool Expected {get;set;}=true;
        public Scale Kind=>kind;
        public bool Valid=>valid;
        /// <summary>False when the LOC/G/S scale is decluttered (no guidance expected and no valid signal).</summary>
        public bool Shown=>kind==Scale.VerticalSpeed||valid||Expected;
        public bool FlagShown=>kind!=Scale.VerticalSpeed&&!valid&&Expected;
        /// <summary>Readout rounding, shared with Digital (50 FPM, so the last digit never flickers).</summary>
        public static float RoundedRate(float fpm)=>FaaEngineInstrumentGraphic.RoundVerticalSpeed(fpm);
        /// <summary>Readout text: Digital's signed format, under a smaller OFF SCALE line beyond the +/-2,000 FPM scale (relative size, so it
        /// follows the legibility floor and stays as narrow as the neighbouring NR dial allows).</summary>
        public static string FormatRate(float fpm)=>(Mathf.Abs(fpm)>FaaClassicVsiGeometry.FullScaleFpm?"<size=85%>OFF SCALE</size>\n":"")+FaaEngineInstrumentGraphic.FormatVerticalSpeed(RoundedRate(fpm),true);

        public void Configure(Scale which,Color tint)
        {
            kind=which;color=tint;raycastTarget=false;
            if(which==Scale.Localizer)
            {
                label=Text("Signal",new Vector2(-125,0),40,FaaHudStyle.Secondary,tint);
                detail=Text("Value",Vector2.zero,56,FaaHudStyle.Secondary,tint);
            }
            else if(which==Scale.Glideslope)
            {
                label=Text("Signal",new Vector2(0,-118),40,FaaHudStyle.Secondary,tint);
                detail=Text("Value",Vector2.zero,40,FaaHudStyle.Secondary,tint);
            }
            else
            {
                // Caption above the scale and signed readout below it, as on Digital ("VS FPM", then "+1350" / "\u22121350").
                label=Text("Signal",new Vector2(12,158),84,FaaHudStyle.Secondary,tint);
                detail=Text("Value",new Vector2(13,-162),110,FaaHudStyle.Data,tint);detail.richText=true;
                scaleLabels=new TMP_Text[4];int n=0;
                foreach(int i in new[]{-2,-1,1,2})
                {
                    // Reference design: labels live between tick ends and the right rail.
                    // The pointer follows the outside contour rather than passing over digits.
                    var t=Text("VSI "+i,new Vector2(VsiLabelLeftX+VsiLabelWidth*.5f,i*60),VsiLabelWidth,FaaHudStyle.Secondary,tint);
                    // 0.8-unit left margin centres the 18-pt glyph (halo padding included) between the tick ends and the rail.
                    t.alignment=TextAlignmentOptions.Center;t.margin=new Vector4(.8f,0,0,0);t.text=Mathf.Abs(i).ToString();scaleLabels[n++]=t;
                }
            }
        }
        private TMP_Text Text(string name,Vector2 position,float width,float size,Color tint)
        {
            var go=new GameObject(name,typeof(RectTransform));go.transform.SetParent(transform,false);var t=go.AddComponent<TextMeshProUGUI>();
            t.font=TMP_Settings.defaultFontAsset;t.fontSize=size;t.color=tint;t.alignment=TextAlignmentOptions.Center;t.raycastTarget=false;
            t.richText=false;t.textWrappingMode=TextWrappingModes.NoWrap;t.rectTransform.anchoredPosition=position;t.rectTransform.sizeDelta=new Vector2(width,25);
            FaaHudStyle.ApplyHalo(t,FaaClassicAnalogHud.HaloStrength);return t;
        }
        public void Present(float input,bool available,Color tint,string mode=null)
        {
            float v=FaaAnalogFlightSample.Finite(input)?input:0;bool ok=available&&FaaAnalogFlightSample.Finite(input);
            bool changed=!initialized||v!=value||ok!=valid||tint!=lastTint||Expected!=lastExpected;
            value=v;valid=ok;lastTint=tint;lastExpected=Expected;initialized=true;color=tint;
            bool shown=Shown;
            float ra=ReadoutAlpha<0f?tint.a:Mathf.Clamp01(ReadoutAlpha);
            Color scaleColor=valid?tint:FaaHudStyle.Dim(tint,.5f),amber=FaaHudStyle.WithAlpha(FaaHudStyle.Amber,ra),readout=FaaHudStyle.WithAlpha(tint,ra);
            if(scaleLabels!=null)foreach(var t in scaleLabels)Paint(t,scaleColor);
            if(label.enabled!=shown)label.enabled=shown;if(detail.enabled!=shown)detail.enabled=shown;
            // A decluttered scale also leaves the module bounds/keep-out (nothing is drawn there).
            if(kind!=Scale.VerticalSpeed&&enabled!=shown)enabled=shown;
            if(mode!=lastMode||changed)
            {
                lastMode=mode;
                SetText(label,!shown||FlagShown?"":mode??(kind==Scale.Localizer?"LOC":kind==Scale.Glideslope?"G/S":VsiCaption));
            }
            Paint(label,kind==Scale.VerticalSpeed?readout:FaaHudStyle.Dim(tint,FaaHudStyle.MinTextAlpha));
            if(kind==Scale.VerticalSpeed)
            {
                if(valid)
                {
                    int rate=(int)RoundedRate(value);bool offScale=Mathf.Abs(value)>FaaClassicVsiGeometry.FullScaleFpm;
                    if(rate!=lastRate||offScale!=lastOffScale){lastRate=rate;lastOffScale=offScale;cachedRate=FormatRate(value);}
                    SetText(detail,cachedRate);Paint(detail,readout);
                }
                else{lastRate=int.MinValue;SetText(detail,"NO DATA");Paint(detail,amber);}
            }
            else
            {
                SetText(detail,FlagShown?(kind==Scale.Localizer?"LOC":"G/S"):"");Paint(detail,amber);
            }
            if(changed)SetVerticesDirty();
        }
        private static void SetText(TMP_Text t,string s){if(t!=null&&t.text!=s)t.text=s;}
        private static void Paint(TMP_Text t,Color c){if(t!=null&&t.color!=c)t.color=c;}
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();Color tint=color;Color scaleColor=valid?tint:FaaHudStyle.Dim(tint,.5f);
            if(kind==Scale.VerticalSpeed)
            {
                FaaClassicVsiGeometry.DrawScale(vh,scaleColor);
                if(valid)FaaClassicVsiGeometry.DrawPointer(vh,value,tint);
                return;
            }
            if(!Shown)return;
            Vector2 axis=kind==Scale.Localizer?Vector2.right:Vector2.up;
            for(int i=-2;i<=2;i++)if(i!=0)Ring(vh,axis*i*DotSpacing,DotRadius,2,scaleColor,24);
            if(valid)
            {
                // Pegged beyond full scale: hollow diamond (motion-limited cue changes appearance, AC 25-11B).
                bool pegged=Mathf.Abs(value)>FullScaleDots;
                Vector2 pos=axis*Mathf.Clamp(value,-FullScaleDots,FullScaleDots)*DotSpacing;
                Diamond(vh,pos,kind==Scale.Localizer?8:9,kind==Scale.Localizer?9:8,tint,!pegged);
            }
            else if(FlagShown)
                RoundedBox(vh,kind==Scale.Localizer?new Rect(-28,-14,56,28):new Rect(-20,-14,40,28),5,FaaHudStyle.WithAlpha(FaaHudStyle.Amber,tint.a));
        }
    }
}
