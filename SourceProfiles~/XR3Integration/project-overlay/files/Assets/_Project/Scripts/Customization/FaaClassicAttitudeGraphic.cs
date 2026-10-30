using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaAnalogVector;
namespace FAA.Customization
{
    /// <summary>
    /// Head-fixed attitude instrument: a round, caged ball of radius <see cref="Radius"/> with a translucent sky/ground fill
    /// (<see cref="FaaClassicTintGraphic"/>) so it reads as an instrument, never as a second outside horizon (AC 25-11B: a non-conformal
    /// horizon looks distinctly different from a conformal one; sky and ground clearly separated).
    /// Pitch spacing is a compressed, NON-conformal scale: +/-25 deg at the ball edge with the aircraft symbol on the horizon
    /// (AC 23.1311-1C: at least +25/-15 deg, at most 50 deg in total). The ball's aircraft reference sits on the screen boresight,
    /// so the inset horizon and the outside horizon coincide in level flight; the registered (conformal) horizon is the SCENE CUES centre.
    /// The aircraft reference is the same gull-wing W as Digital. 10-deg rungs are labelled at both ends (labels only every 10 deg);
    /// 5-deg marks are short and unlabelled; there are no 2.5-deg minors. A rung whose label cannot be placed inside the ball is not drawn.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaClassicAttitudeGraphic:MaskableGraphic
    {
        /// <summary>Radius of the attitude ball (local units).</summary>
        public const float Radius=135f;
        /// <summary>Non-conformal pitch scale: the ball edge is 25 deg from the aircraft symbol.</summary>
        public const float UnitsPerDegree=Radius/25f;
        public const float HalfWidth=Radius,RungLimit=Radius;
        /// <summary>Gull-wing W unit: 6 x 1 units, the same proportions as the Digital waterline (72 x 12 local units).</summary>
        public const float WaterlineUnit=12f,WaterlineStroke=3f;
        public const float RungInner=WaterlineUnit*3f+12f,LongRungOuter=78f,ShortRungOuter=62f,LabelGap=5f;
        /// <summary>Nominal label centre for a two-digit label (labels are placed at the rung end from their real width).</summary>
        public const float LabelX=LongRungOuter+LabelGap+10f;
        /// <summary>Largest |offset| of a labelled rung at the default label size (legacy name; the real test is <see cref="RungFits"/>).</summary>
        public const float LabelLimit=72f;
        /// <summary>When the true horizon is closer than this to the ball edge it is held there, dashed (off-scale), so some sky and some ground stay visible.</summary>
        public const float OffScaleMargin=12f;
        public static readonly Vector2[] WaterlineShape={new(-3f,0f),new(-1.5f,0f),new(-.75f,-1f),new(0f,0f),new(.75f,-1f),new(1.5f,0f),new(3f,0f)};
        private const int LabelledRungs=6;
        private const float LabelHalfHeight=7f,DigitEm=.556f,HyphenEm=.333f;
        private static readonly string[] LabelTable=BuildLabels();
        private static readonly float[] ChevronOffsets={40f,70f};
        private readonly TMP_Text[] labels=new TMP_Text[LabelledRungs*2];
        private TMP_Text flag;
        private float pitch,roll,labelSize=FaaHudStyle.Secondary;
        private bool valid,unusual,suppress;
        private Color lastTint;
        /// <summary>Unusual-attitude mode (set by the HUD with hysteresis): adds red recovery chevrons when the horizon is off the inset.</summary>
        public bool Unusual {get;set;}
        /// <summary>True while one global flag replaces the per-instrument flags (stale feed).</summary>
        public bool SuppressFlag {get;set;}
        /// <summary>Translucent sky/ground fill drawn behind this graphic (a sibling created by the HUD).</summary>
        public FaaClassicTintGraphic Fill {get;set;}
        public bool Valid=>valid;
        public float Pitch=>pitch;
        /// <summary>Horizon offset from the ball centre along the rotated vertical (nose down = positive = horizon above the W).</summary>
        public float HorizonOffset=>-pitch*UnitsPerDegree;
        /// <summary>True while the horizon is held at the ball edge (dashed) because the true horizon is off scale.</summary>
        public bool HorizonOffScale=>Mathf.Abs(HorizonOffset)>Radius-OffScaleMargin;
        private static string[] BuildLabels(){var s=new string[19];for(int i=0;i<19;i++)s[i]=((i-9)*10).ToString(System.Globalization.CultureInfo.InvariantCulture);return s;}
        public static string LabelFor(int degrees)=>degrees%10==0&&Mathf.Abs(degrees)<=90?LabelTable[degrees/10+9]:"";
        /// <summary>Approximate half advance of a pitch label at <paramref name="size"/> (LiberationSans digit and hyphen advances).</summary>
        public static float LabelHalfWidth(int degrees,float size)
        {
            int a=Mathf.Abs(degrees);int digits=a>=10?2:1;
            return (digits*DigitEm+(degrees<0?HyphenEm:0f))*size*.5f;
        }
        /// <summary>True when a 10-deg rung at offset <paramref name="y"/> fits inside the ball together with its label.</summary>
        public static bool RungFits(int degrees,float y,float size=FaaHudStyle.Secondary)=>
            Inside(LongRungOuter+LabelGap+2f*LabelHalfWidth(degrees,size),Mathf.Abs(y)+LabelHalfHeight*size/FaaHudStyle.Secondary);
        private static bool Inside(float x,float y)=>x*x+y*y<=(Radius-2f)*(Radius-2f);

        public void Configure(Color tint)
        {
            color=tint;raycastTarget=false;
            for(int i=0;i<labels.Length;i++)
            {
                var go=new GameObject("Pitch "+i,typeof(RectTransform));go.transform.SetParent(transform,false);
                var t=go.AddComponent<TextMeshProUGUI>();t.font=TMP_Settings.defaultFontAsset;t.fontSize=FaaHudStyle.Secondary;t.alignment=TextAlignmentOptions.Center;
                t.color=tint;t.raycastTarget=false;t.richText=false;t.textWrappingMode=TextWrappingModes.NoWrap;t.rectTransform.sizeDelta=new Vector2(40,24);
                FaaHudStyle.ApplyHalo(t,FaaClassicAnalogHud.HaloStrength);labels[i]=t;go.SetActive(false);
            }
            var status=new GameObject("Attitude source",typeof(RectTransform));status.transform.SetParent(transform,false);
            flag=status.AddComponent<TextMeshProUGUI>();flag.font=TMP_Settings.defaultFontAsset;flag.fontSize=FaaHudStyle.Data;flag.alignment=TextAlignmentOptions.Center;
            flag.raycastTarget=false;flag.richText=false;flag.textWrappingMode=TextWrappingModes.NoWrap;
            flag.rectTransform.anchoredPosition=Vector2.zero;flag.rectTransform.sizeDelta=new Vector2(60,30);FaaHudStyle.ApplyHalo(flag,FaaClassicAnalogHud.HaloStrength);
        }
        public void Present(float newPitch,float newRoll,bool isValid,Color tint)
        {
            bool nowValid=isValid&&FaaAnalogFlightSample.Finite(newPitch)&&FaaAnalogFlightSample.Finite(newRoll);
            if(!nowValid){newPitch=0;newRoll=0;}
            // Labels may be enlarged by the HUD's legibility floor when the inset is shrunk; the fit test follows their real size.
            float size=labels[0]!=null?labels[0].fontSize:FaaHudStyle.Secondary;
            bool changed=nowValid!=valid||newPitch!=pitch||newRoll!=roll||tint!=lastTint||Unusual!=unusual||SuppressFlag!=suppress||size!=labelSize;
            pitch=newPitch;roll=newRoll;valid=nowValid;lastTint=tint;unusual=Unusual;suppress=SuppressFlag;color=tint;labelSize=size;
            if(Fill!=null)Fill.SetAttitude(HorizonOffset,roll,valid,tint.a);
            string f=valid||SuppressFlag?"":"ATT";if(flag.text!=f)flag.text=f;
            Color amber=FaaHudStyle.WithAlpha(FaaHudStyle.Amber,tint.a);if(flag.color!=amber)flag.color=amber;
            Quaternion rotation=Quaternion.Euler(0,0,roll);
            int start=Mathf.CeilToInt((pitch-RungLimit/UnitsPerDegree)/10f)*10;
            for(int k=0;k<LabelledRungs;k++)
            {
                int degrees=start+k*10;float y=(degrees-pitch)*UnitsPerDegree;
                bool visible=valid&&degrees!=0&&Mathf.Abs(degrees)<=90&&RungFits(degrees,y,size);
                for(int side=0;side<2;side++)
                {
                    var t=labels[k*2+side];
                    if(t.gameObject.activeSelf!=visible)t.gameObject.SetActive(visible);
                    if(!visible)continue;
                    string s=LabelFor(degrees);if(t.text!=s)t.text=s;
                    if(t.color!=tint)t.color=tint;
                    float x=LongRungOuter+LabelGap+LabelHalfWidth(degrees,size);
                    Vector2 p=rotation*new Vector3(side==0?-x:x,y,0);
                    if(t.rectTransform.anchoredPosition!=p)t.rectTransform.anchoredPosition=p;
                    if(t.rectTransform.localRotation!=rotation)t.rectTransform.localRotation=rotation;
                }
            }
            if(changed)SetVerticesDirty();
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if(!valid)
            {
                if(!SuppressFlag)RoundedBox(vh,new Rect(-32,-17,64,34),6,FaaHudStyle.WithAlpha(FaaHudStyle.Amber,color.a));
                return;
            }
            Quaternion rotation=Quaternion.Euler(0,0,roll);Color c=color;
            // Horizon: longer and heavier than every rung, gapped for the W. Off scale it is held at the edge and dashed.
            float h=HorizonOffset,edge=Radius-OffScaleMargin;
            if(Mathf.Abs(h)<=edge)
            {
                float chord=Mathf.Sqrt((Radius-1.5f)*(Radius-1.5f)-h*h),gap=WaterlineUnit*3f+8f;
                if(chord>gap)for(int side=-1;side<=1;side+=2)RotatedLine(vh,rotation,new Vector2(side*gap,h),new Vector2(side*chord,h),3f,c);
            }
            else
            {
                float held=Mathf.Sign(h)*edge,chord=Mathf.Sqrt((Radius-1.5f)*(Radius-1.5f)-held*held);
                for(float x=-chord;x<chord-2f;x+=16f)RotatedLine(vh,rotation,new Vector2(x,held),new Vector2(Mathf.Min(x+10f,chord),held),2.4f,c);
            }
            int center=Mathf.RoundToInt(pitch/5)*5;
            for(int i=-6;i<=6;i++)
            {
                int degrees=center+i*5;if(degrees==0||Mathf.Abs(degrees)>90)continue;
                float y=(degrees-pitch)*UnitsPerDegree;
                bool major=degrees%10==0;
                if(major?!RungFits(degrees,y,labelSize):!Inside(ShortRungOuter,Mathf.Abs(y)+1f))continue;
                for(int side=-1;side<=1;side+=2)
                {
                    if(!major){RotatedLine(vh,rotation,new Vector2(side*RungInner,y),new Vector2(side*ShortRungOuter,y),2f,c);continue;}
                    // Positive rungs solid, negative rungs dashed; rung ends turn toward the horizon.
                    if(degrees>0)RotatedLine(vh,rotation,new Vector2(side*RungInner,y),new Vector2(side*LongRungOuter,y),2f,c);
                    else
                    {
                        RotatedLine(vh,rotation,new Vector2(side*RungInner,y),new Vector2(side*(RungInner+12f),y),2f,c);
                        RotatedLine(vh,rotation,new Vector2(side*(LongRungOuter-12f),y),new Vector2(side*LongRungOuter,y),2f,c);
                    }
                    RotatedLine(vh,rotation,new Vector2(side*LongRungOuter,y),new Vector2(side*LongRungOuter,y+(degrees>0?-6:6)),1.8f,c);
                }
            }
            if(Unusual&&Mathf.Abs(pitch)>20f)
            {
                // Red recovery chevrons point toward the horizon (only in unusual-attitude mode).
                Color red=FaaHudStyle.WithAlpha(FaaHudStyle.Red,c.a);float dir=pitch>0?1:-1;
                foreach(float d in ChevronOffsets)
                {
                    Vector2 apex=new Vector2(0,dir*d),arm=new Vector2(20,dir*14);
                    RotatedLine(vh,rotation,apex+new Vector2(-arm.x,arm.y),apex,3f,red);RotatedLine(vh,rotation,apex,apex+arm,3f,red);
                }
            }
            // Fixed aircraft reference: the Digital gull-wing W (an instrument reference, not a flight-path vector).
            for(int i=1;i<WaterlineShape.Length;i++)Line(vh,WaterlineShape[i-1]*WaterlineUnit,WaterlineShape[i]*WaterlineUnit,WaterlineStroke,c);
        }
        private static void RotatedLine(VertexHelper vh,Quaternion rotation,Vector2 a,Vector2 b,float width,Color color)
        {Line(vh,rotation*(Vector3)a,rotation*(Vector3)b,width,color);}
    }

    /// <summary>
    /// Translucent fills drawn BEHIND the Classic strokes: the attitude sky/ground and the dark dial faces and window plates that give
    /// numerals, needles and readouts local contrast over bright haze and terrain. They never carry the dark stroke outline (it would
    /// darken a fill four times over) and rebuild only when their inputs change. Sky/ground colours follow the AC 25-11B colour table
    /// (sky blue, earth brown) at low alpha so the outside view stays visible.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaClassicTintGraphic:MaskableGraphic
    {
        public enum Shape { SkyGround,DialFace }
        public static readonly Color Sky=new Color(.20f,.45f,.85f,.22f),Ground=new Color(.42f,.27f,.12f,.42f);
        public static readonly Color Face=new Color(0f,0f,0f,.30f),Plate=new Color(0f,0f,0f,.50f);
        private const int Segments=64;
        private static readonly Vector2[] Unit=BuildCircle();
        private readonly List<Vector2> polygon=new(Segments+4),clipped=new(Segments+4);
        private Shape shape;private float radius;private Rect window;private bool hasWindow;
        private float offset,rollDegrees,fillAlpha=1f,faceAlpha=1f,plateAlpha=1f;private bool attitudeValid;
        public Shape Kind=>shape;
        public float FillRadius=>radius;
        /// <summary>The sky/ground boundary actually drawn (the true horizon offset, held inside the ball when off scale).</summary>
        public float BoundaryOffset=>Mathf.Clamp(offset,-(radius-FaaClassicAttitudeGraphic.OffScaleMargin),radius-FaaClassicAttitudeGraphic.OffScaleMargin);
        public bool AttitudeShown=>shape==Shape.SkyGround&&attitudeValid;
        private static Vector2[] BuildCircle(){var p=new Vector2[Segments];for(int i=0;i<Segments;i++)p[i]=Bearing(i*360f/Segments);return p;}

        public void ConfigureSkyGround(float ballRadius){shape=Shape.SkyGround;radius=ballRadius;raycastTarget=false;color=Color.white;SetVerticesDirty();}
        public void ConfigureDialFace(float faceRadius,Rect windowRect){shape=Shape.DialFace;radius=faceRadius;window=windowRect;hasWindow=windowRect.width>0;raycastTarget=false;color=Color.white;SetVerticesDirty();}
        /// <summary>Sky/ground boundary at <paramref name="horizonOffset"/> along the vertical rotated by <paramref name="roll"/>; nothing when invalid.</summary>
        public void SetAttitude(float horizonOffset,float roll,bool valid,float alpha)
        {
            if(horizonOffset==offset&&roll==rollDegrees&&valid==attitudeValid&&alpha==fillAlpha)return;
            offset=horizonOffset;rollDegrees=roll;attitudeValid=valid;fillAlpha=alpha;SetVerticesDirty();
        }
        /// <summary>Dial face alpha multiplier (dial intensity) and window plate alpha multiplier (readout intensity).</summary>
        public void SetFace(float face,float plate)
        {
            if(face==faceAlpha&&plate==plateAlpha)return;
            faceAlpha=face;plateAlpha=plate;SetVerticesDirty();
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if(shape==Shape.DialFace)
            {
                polygon.Clear();for(int i=0;i<Segments;i++)polygon.Add(Unit[i]*radius);
                Fan(vh,polygon,FaaHudStyle.Dim(Face,faceAlpha));
                if(!hasWindow)return;
                polygon.Clear();polygon.Add(new Vector2(window.xMin,window.yMin));polygon.Add(new Vector2(window.xMin,window.yMax));
                polygon.Add(new Vector2(window.xMax,window.yMax));polygon.Add(new Vector2(window.xMax,window.yMin));
                Fan(vh,polygon,FaaHudStyle.Dim(Plate,plateAlpha));
                return;
            }
            if(!attitudeValid||fillAlpha<=.001f)return;
            Vector2 normal=Quaternion.Euler(0,0,rollDegrees)*Vector3.up;float boundary=BoundaryOffset;
            for(int side=0;side<2;side++)
            {
                polygon.Clear();for(int i=0;i<Segments;i++)polygon.Add(Unit[i]*radius);
                ClipHalfPlane(polygon,clipped,normal,boundary,side==0);
                Fan(vh,clipped,FaaHudStyle.Dim(side==0?Sky:Ground,fillAlpha));
            }
        }
        /// <summary>Sutherland-Hodgman clip of a convex polygon to dot(p,n) >= d (keepAbove) or dot(p,n) &lt;= d.</summary>
        private static void ClipHalfPlane(List<Vector2> input,List<Vector2> output,Vector2 n,float d,bool keepAbove)
        {
            output.Clear();int count=input.Count;if(count==0)return;
            float sign=keepAbove?1f:-1f;Vector2 prev=input[count-1];float sp=sign*(Vector2.Dot(prev,n)-d);
            for(int i=0;i<count;i++)
            {
                Vector2 cur=input[i];float sc=sign*(Vector2.Dot(cur,n)-d);
                if(sc>=0f){if(sp<0f)output.Add(Vector2.Lerp(prev,cur,sp/(sp-sc)));output.Add(cur);}
                else if(sp>=0f)output.Add(Vector2.Lerp(prev,cur,sp/(sp-sc)));
                prev=cur;sp=sc;
            }
        }
        private static void Fan(VertexHelper vh,List<Vector2> p,Color tint)
        {
            if(p.Count<3||tint.a<=.001f)return;
            int start=vh.currentVertCount;
            for(int i=0;i<p.Count;i++)vh.AddVert(p[i],tint,Vector2.zero);
            for(int i=1;i<p.Count-1;i++)vh.AddTriangle(start,start+i,start+i+1);
        }
    }

    /// <summary>
    /// Roll scale concentric with the attitude. Sky-pointer convention identical to Digital (the filled pointer moves with the sky,
    /// counter-clockwise for a right bank), 1:1 scale with indices at 10, 20, 30, 45 and 60 deg, fixed hollow index at top, slip brick under the pointer.
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaClassicBankGraphic:MaskableGraphic
    {
        public const float Radius=175f;
        public static readonly float[] TickAngles={10f,20f,30f,45f,60f};
        /// <summary>Bearing (degrees clockwise from up) of the moving sky pointer for a bank angle (right bank positive).</summary>
        public static float PointerBearing(float bank)=>-Mathf.Clamp(Mathf.DeltaAngle(0,bank),-90f,90f);
        private float bank,slip;private bool valid,slipValid;private Color lastTint;private bool initialized;
        public bool PointerShown=>valid;
        public float Bank=>bank;
        public void Present(float angle,float side,bool attitudeValid,bool validSlip,Color tint)
        {
            raycastTarget=false;
            if(initialized&&angle==bank&&side==slip&&attitudeValid==valid&&validSlip==slipValid&&tint==lastTint)return;
            bank=angle;slip=side;valid=attitudeValid&&FaaAnalogFlightSample.Finite(angle);slipValid=validSlip&&FaaAnalogFlightSample.Finite(side);
            lastTint=tint;color=tint;initialized=true;SetVerticesDirty();
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();Color tint=valid?color:FaaHudStyle.Dim(color,.35f);
            Arc(vh,Vector2.zero,Radius,-60,60,2.2f,tint,2f);
            foreach(float a in TickAngles)
            {
                bool major=a==30f||a==60f;float length=major?22f:a==45f?15f:11f,width=major?2.2f:1.6f;
                for(int s=-1;s<=1;s+=2)Line(vh,Bearing(s*a,Radius),Bearing(s*a,Radius+length),width,tint);
            }
            HollowTriangle(vh,new Vector2(0,Radius+3),new Vector2(-10,Radius+23),new Vector2(10,Radius+23),2f,tint);
            if(!valid)return;
            float angle=PointerBearing(bank);bool beyond=Mathf.Abs(Mathf.DeltaAngle(0,bank))>60f;
            Vector2 tip=Bearing(angle,Radius-3),baseCenter=Bearing(angle,Radius-23),across=Bearing(angle+90,10);
            if(beyond)HollowTriangle(vh,tip,baseCenter-across,baseCenter+across,2f,tint);
            else Triangle(vh,tip,baseCenter-across,baseCenter+across,tint);
            if(!slipValid)return;
            Vector2 c=Bearing(angle,Radius-30)+Bearing(angle+90,Mathf.Clamp(slip,-1f,1f)*19f),w=Bearing(angle+90,13),h=Bearing(angle,2.5f);
            Triangle(vh,c-w-h,c-w+h,c+w+h,tint);Triangle(vh,c-w-h,c+w+h,c+w-h,tint);
        }
    }
}
