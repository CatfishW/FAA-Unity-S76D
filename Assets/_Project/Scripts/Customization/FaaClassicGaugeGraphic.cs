using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaAnalogVector;
namespace FAA.Customization
{
    /// <summary>
    /// Round Classic dial. Numerals stay upright (never rotate with the scale); the needle is clipped out of the digital window and,
    /// on the big dials, leaves the numeral band empty (shaft inside it, pointer tip over the ticks outside it), so it never covers the
    /// numeral it points at. Limit marks and exceedance coding come from <see cref="FaaRotorcraftLimits"/> (demonstrator values, not RFM data).
    /// Invalid data removes the needle and numerals and puts a boxed mnemonic (IAS/ALT/TQ/NR) in the window it replaces.
    /// The window (border, value, unit) is an awareness readout: its alpha is <see cref="ReadoutAlpha"/>, the rest of the dial uses the tint alpha.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaClassicGaugeGraphic:MaskableGraphic
    {
        public enum Gauge { Airspeed,Altitude,Torque,RotorRpm }
        public const float BigRadius=115f,SmallRadius=76f;
        public const float AirspeedScaleMax=240f;
        /// <summary>Big-dial needle: the shaft ends inside the numeral band and the pointer tip starts outside it, over the ticks.</summary>
        public const float BigShaftEnd=62f,BigTipBase=103f,BigTipRadius=114f,BigTipHalfWidth=4.5f,BigHubRadius=5f,SmallHubRadius=4f;
        public Gauge Kind {get;private set;}
        /// <summary>Alpha of the window border, value and unit (the awareness readout). Negative: same as the tint alpha.</summary>
        public float ReadoutAlpha {get;set;}=-1f;
        public float DisplayedValue {get;private set;}
        public bool DataValid {get;private set;}
        /// <summary>Limit marks (NaN = no mark). Configure fills them from FaaRotorcraftLimits for the gauge kind.</summary>
        public float CautionAbove=float.NaN,WarningAbove=float.NaN,CautionBelow=float.NaN,WarningBelow=float.NaN;
        /// <summary>Low-side rotor limits apply only while airborne (no red NR during start-up or shutdown on the ground).</summary>
        public bool Airborne {get;set;}=true;
        /// <summary>True while a single global flag replaces the per-instrument flags (stale feed).</summary>
        public bool SuppressFlag {get;set;}
        public FaaExceedance Exceedance {get;private set;}
        public float ExceedanceOnset {get;private set;}=-100f;
        public float NeedleAngle => AngleFor(Kind,DisplayedValue);
        public float ScaleMaximum => Kind==Gauge.Airspeed?AirspeedScaleMax:Kind==Gauge.Torque?FaaRotorcraftLimits.TorqueScaleMax:Kind==Gauge.RotorRpm?FaaRotorcraftLimits.NrScaleMax:1000f;
        private TMP_Text value,unit,secondary;
        private TMP_Text[] ticks;
        private Color tint;
        private int lastInt=int.MinValue;private string cachedValue="";private bool lastSuppress;private float readoutA=1f;

        public static bool IsSmall(Gauge kind)=>kind==Gauge.Torque||kind==Gauge.RotorRpm;
        public static float AngleFor(Gauge kind,float v)=>kind==Gauge.Airspeed?FaaAnalogAnimation.SpeedAngle(v):kind==Gauge.Altitude?FaaAnalogAnimation.AltitudeAngle(v):
            FaaAnalogAnimation.PercentAngle(v,kind==Gauge.Torque?FaaRotorcraftLimits.TorqueScaleMax:FaaRotorcraftLimits.NrScaleMax);
        /// <summary>Digital window in local units (shared by the drawing, the window plate and the needle clipping). Tall enough that the value
        /// and the unit below it never share a line box (26-pt value, 18-pt unit), clear of the hub above and the numerals below.</summary>
        public static Rect WindowRect(Gauge kind)=>kind==Gauge.Airspeed?new Rect(-30,-64,60,55):kind==Gauge.Altitude?new Rect(-42,-64,84,55):new Rect(-32,-64,64,57);
        /// <summary>Centre of the window value and of the unit below it (local units).</summary>
        public static float ValueY(Gauge kind)=>IsSmall(kind)?-21.5f:-23.5f;
        public static float UnitY(Gauge kind)=>IsSmall(kind)?-47.5f:-49.5f;
        /// <summary>Radius of the scale numerals (big dials inside the ticks, small dials outside the ring).</summary>
        public static float NumeralRadius(Gauge kind)=>kind==Gauge.Altitude?86f:kind==Gauge.Airspeed?84f:95f;
        public static string Mnemonic(Gauge kind)=>kind==Gauge.Airspeed?"IAS":kind==Gauge.Altitude?"ALT":kind==Gauge.Torque?"TQ":"NR";
        public static string UnitLabel(Gauge kind)=>kind==Gauge.Airspeed?"KT":kind==Gauge.Altitude?"FT":kind==Gauge.Torque?"TQ %":"NR %";

        public void Configure(Gauge kind,Color green)
        {
            Kind=kind;tint=green;color=green;raycastTarget=false;
            bool small=IsSmall(kind);
            CautionAbove=WarningAbove=CautionBelow=WarningBelow=float.NaN;
            if(kind==Gauge.Airspeed)WarningAbove=FaaRotorcraftLimits.VneKnots;
            else if(kind==Gauge.Torque){CautionAbove=FaaRotorcraftLimits.TorqueCautionAbove;WarningAbove=FaaRotorcraftLimits.TorqueWarningAbove;}
            else if(kind==Gauge.RotorRpm)
            {CautionAbove=FaaRotorcraftLimits.NrCautionAbove;WarningAbove=FaaRotorcraftLimits.NrWarningAbove;CautionBelow=FaaRotorcraftLimits.NrCautionBelow;WarningBelow=FaaRotorcraftLimits.NrWarningBelow;}
            if(kind==Gauge.Airspeed||kind==Gauge.Altitude)
            {
                int count=kind==Gauge.Altitude?10:12;ticks=new TMP_Text[count];
                for(int i=0;i<count;i++)
                {
                    float degree=i*360f/count;
                    // Upright numerals (HF-STD-001B): never rotated tangentially, so 6 can never read as 9.
                    ticks[i]=Text("Scale "+i,Bearing(degree,NumeralRadius(kind)),new Vector2(kind==Gauge.Altitude?22:44,24),FaaHudStyle.Secondary);
                    ticks[i].text=kind==Gauge.Altitude?i.ToString(CultureInfo.InvariantCulture):(i*20).ToString(CultureInfo.InvariantCulture);
                }
            }
            else
            {
                // Outside the ring at the four diagonals, so the needle never covers a scale numeral.
                ticks=new TMP_Text[4];
                for(int i=0;i<4;i++)
                {
                    float v=i*40f;
                    ticks[i]=Text("Scale "+i,Bearing(AngleFor(kind,v),NumeralRadius(kind)),new Vector2(38,24),FaaHudStyle.Secondary);
                    ticks[i].text=((int)v).ToString(CultureInfo.InvariantCulture);
                }
            }
            Rect w=WindowRect(kind);
            value=Text("Live Value",new Vector2(0,ValueY(kind)),new Vector2(w.width-6,28),26);
            value.enableAutoSizing=true;value.fontSizeMin=FaaHudStyle.Data;value.fontSizeMax=26;
            unit=Text("Units",new Vector2(0,UnitY(kind)),new Vector2(w.width-6,16),FaaHudStyle.Secondary);
            unit.text=UnitLabel(kind);
            secondary=Text("Source detail",new Vector2(0,small?-104:-128),new Vector2(small?170:230,24),FaaHudStyle.Secondary);
        }
        private TMP_Text Text(string name,Vector2 p,Vector2 size,float fontSize)
        {
            var go=new GameObject(name,typeof(RectTransform));go.transform.SetParent(transform,false);
            var text=go.AddComponent<TextMeshProUGUI>();text.font=TMP_Settings.defaultFontAsset;text.color=tint;text.fontSize=fontSize;
            text.alignment=TextAlignmentOptions.Center;text.raycastTarget=false;text.richText=false;text.textWrappingMode=TextWrappingModes.NoWrap;
            text.overflowMode=TextOverflowModes.Overflow;
            text.rectTransform.anchorMin=text.rectTransform.anchorMax=text.rectTransform.pivot=new Vector2(.5f,.5f);
            text.rectTransform.anchoredPosition=p;text.rectTransform.sizeDelta=size;FaaHudStyle.ApplyHalo(text,FaaClassicAnalogHud.HaloStrength);return text;
        }

        private FaaExceedance Classify(float v)
        {
            if(Kind==Gauge.Airspeed)v=Mathf.Max(0,v);
            var shared=Kind==Gauge.Torque?FaaRotorcraftLimits.Torque(v,true):Kind==Gauge.RotorRpm?FaaRotorcraftLimits.RotorNr(v,true,Airborne):
                Kind==Gauge.Airspeed?FaaRotorcraftLimits.Airspeed(v,true):FaaExceedance.Normal;
            if(shared==FaaExceedance.Invalid)return shared;
            bool lowSide=Kind!=Gauge.RotorRpm||Airborne;
            if(v>WarningAbove||lowSide&&v<WarningBelow)return FaaExceedance.Warning;
            if(v>CautionAbove||lowSide&&v<CautionBelow)return FaaExceedance.Caution;
            return FaaExceedance.Normal;
        }
        // Small hysteresis so a value riding a limit does not restart the flash every frame.
        private FaaExceedance Hold(FaaExceedance raw,float v)
        {
            if(raw>=Exceedance||Exceedance==FaaExceedance.Invalid)return raw;
            float m=Kind==Gauge.Airspeed?2f:1f;
            int a=(int)Classify(v+m),b=(int)Classify(v-m);
            if(a==(int)FaaExceedance.Invalid)a=0;if(b==(int)FaaExceedance.Invalid)b=0;
            return Mathf.Max(a,b)>=(int)Exceedance?Exceedance:raw;
        }
        public bool Flashing => DataValid&&Exceedance!=FaaExceedance.Normal&&!FaaClassicClock.BlinkVisible(ExceedanceOnset);

        public void Present(float reading,bool valid,string detail,Color green)
        {
            bool finite=FaaAnalogFlightSample.Finite(reading);
            valid&=finite;
            var raw=valid?Classify(reading):FaaExceedance.Invalid;
            if(raw==FaaExceedance.Invalid)valid=false;
            var severity=valid?Hold(raw,reading):FaaExceedance.Normal;
            if(valid&&severity!=FaaExceedance.Normal&&(int)severity>(int)Exceedance)ExceedanceOnset=FaaClassicClock.Now;
            float ra=ReadoutAlpha<0f?green.a:Mathf.Clamp01(ReadoutAlpha);
            bool changed=DataValid!=valid||Mathf.Abs(DisplayedValue-reading)>.0001f||green!=tint||severity!=Exceedance||lastSuppress!=SuppressFlag||ra!=readoutA;
            DisplayedValue=finite?reading:0;DataValid=valid;tint=green;Exceedance=severity;lastSuppress=SuppressFlag;readoutA=ra;
            color=green;
            Color readout=FaaHudStyle.WithAlpha(green,ra),alert=SeverityColor(readout);
            if(ticks!=null)foreach(var t in ticks){if(t.gameObject.activeSelf!=valid)t.gameObject.SetActive(valid);if(valid)Paint(t,green);}
            string text;
            if(valid)
            {
                int iv=Mathf.RoundToInt(Kind==Gauge.Airspeed?Mathf.Max(0,reading):reading);
                if(iv!=lastInt){lastInt=iv;cachedValue=Kind==Gauge.Altitude?iv.ToString("N0",CultureInfo.InvariantCulture):iv.ToString(CultureInfo.InvariantCulture);}
                text=cachedValue;
                Paint(value,FaaHudStyle.Dim(alert,Flashing?.3f:1f));
            }
            else
            {
                lastInt=int.MinValue;text=SuppressFlag?"":Mnemonic(Kind);
                Paint(value,FaaHudStyle.WithAlpha(FaaHudStyle.Amber,ra));
            }
            SetText(value,text);
            // Unit at full readout alpha: its contrast comes from the halo and the window plate, never from transparency.
            SetText(unit,valid?UnitLabel(Kind):"");Paint(unit,readout);
            bool offScale=valid&&(Kind==Gauge.Airspeed?reading>=AirspeedScaleMax:Kind!=Gauge.Altitude&&reading>ScaleMaximum);
            SetText(secondary,!valid&&SuppressFlag?"":offScale?"OFF SCALE":detail??"");Paint(secondary,readout);
            if(changed)SetVerticesDirty();
        }
        private Color SeverityColor(Color green)=>Exceedance==FaaExceedance.Warning?FaaHudStyle.WithAlpha(FaaHudStyle.Red,green.a):
            Exceedance==FaaExceedance.Caution?FaaHudStyle.WithAlpha(FaaHudStyle.Amber,green.a):green;
        private static void SetText(TMP_Text t,string s){if(t!=null&&t.text!=s)t.text=s;}
        private static void Paint(TMP_Text t,Color c){if(t!=null&&t.color!=c)t.color=c;}

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();bool small=IsSmall(Kind);float radius=small?SmallRadius:BigRadius;Color c=color;Color readout=FaaHudStyle.WithAlpha(c,readoutA);
            Color scale=DataValid?c:FaaHudStyle.Dim(c,.35f);
            Ring(vh,Vector2.zero,radius,2f,scale,small?96:128);
            if(small)
            {
                for(int i=0;i<=12;i++)
                {
                    float a=AngleFor(Kind,i*10f);bool major=i%4==0;
                    Line(vh,Bearing(a,radius-2),Bearing(a,major?63:68),major?1.8f:1.2f,major?scale:FaaHudStyle.Dim(scale,.7f));
                }
            }
            else
            {
                int steps=Kind==Gauge.Altitude?50:48;
                for(int i=0;i<steps;i++)
                {
                    float a=i*360f/steps;bool major=i%(Kind==Gauge.Altitude?5:4)==0;
                    Line(vh,Bearing(a,radius-2),Bearing(a,major?103:108),major?2f:1.2f,major?scale:FaaHudStyle.Dim(scale,.7f));
                }
            }
            DrawLimits(vh,radius,small,DataValid?1f:.35f);
            Rect window=WindowRect(Kind);
            Color alert=SeverityColor(c),readoutAlert=SeverityColor(readout);
            Color border=DataValid?readoutAlert:SuppressFlag?FaaHudStyle.Dim(readout,.35f):FaaHudStyle.WithAlpha(FaaHudStyle.Amber,readoutA);
            RoundedBox(vh,window,7,border);
            // Exceedance: second (inner) box, so a limit is coded by shape as well as colour (AC 25-11B: colour never the only code).
            if(DataValid&&Exceedance!=FaaExceedance.Normal)RoundedBox(vh,new Rect(window.x+3,window.y+3,window.width-6,window.height-6),5,readoutAlert);
            if(!DataValid)return;
            float angle=NeedleAngle,width=small?3.5f:4f;
            Rect keepOut=new Rect(window.x-3,window.y-3,window.width+6,window.height+6);
            if(small)
            {
                // Small dials carry their numerals outside the ring, so a full needle never covers them.
                LineOutside(vh,-Bearing(angle,7),Bearing(angle,55f),keepOut,width,alert);
                Vector2 baseCenter=Bearing(angle,55f),across=Bearing(angle+90,2.6f);
                Triangle(vh,baseCenter-across,Bearing(angle,66f),baseCenter+across,alert);
            }
            else
            {
                // Big dials: the numeral band (r about 68-102) stays empty; the shaft gives direction, the tip reads the scale.
                LineOutside(vh,-Bearing(angle,10),Bearing(angle,BigShaftEnd),keepOut,width,alert);
                Vector2 baseCenter=Bearing(angle,BigTipBase),across=Bearing(angle+90,BigTipHalfWidth);
                Triangle(vh,baseCenter-across,Bearing(angle,BigTipRadius),baseCenter+across,alert);
            }
            Disc(vh,Vector2.zero,small?SmallHubRadius:BigHubRadius,alert);
        }
        private void DrawLimits(VertexHelper vh,float radius,bool small,float alpha)
        {
            Color red=FaaHudStyle.Dim(FaaHudStyle.WithAlpha(FaaHudStyle.Red,color.a),alpha),amber=FaaHudStyle.Dim(FaaHudStyle.WithAlpha(FaaHudStyle.Amber,color.a),alpha);
            float inner=small?61f:101f,outer=small?84f:119f,w=small?3.5f:4f,band=radius+(small?4.5f:5.5f);
            if(FaaAnalogFlightSample.Finite(CautionAbove))
                Arc(vh,Vector2.zero,band,AngleFor(Kind,CautionAbove),AngleFor(Kind,FaaAnalogFlightSample.Finite(WarningAbove)?WarningAbove:ScaleMaximum),3f,amber);
            if(FaaAnalogFlightSample.Finite(CautionBelow))
                Arc(vh,Vector2.zero,band,AngleFor(Kind,FaaAnalogFlightSample.Finite(WarningBelow)?WarningBelow:0f),AngleFor(Kind,CautionBelow),3f,amber);
            if(FaaAnalogFlightSample.Finite(WarningAbove)){float a=AngleFor(Kind,WarningAbove);Line(vh,Bearing(a,inner),Bearing(a,outer),w,red);}
            if(FaaAnalogFlightSample.Finite(WarningBelow)){float a=AngleFor(Kind,WarningBelow);Line(vh,Bearing(a,inner),Bearing(a,outer),w,red);}
        }
    }
}
