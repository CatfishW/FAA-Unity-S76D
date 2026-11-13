using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaAnalogVector;

namespace FAA.Customization
{
    /// <summary>
    /// Procedural Digital roll scale. It replaces the 'Bank Scale 2' bitmap, whose ticks were baked at non-standard angles and whose
    /// pointer pivoted about a point above the arc's centre of curvature, so it under-read bank by about a third.
    /// One centre and one radius serve the arc, the sky pointer and the slip brick: the pointer's bearing about the arc centre equals the
    /// bank angle 1:1. Convention identical to Classic (<see cref="FaaClassicBankGraphic"/>): the filled sky pointer moves with the horizon
    /// (counter-clockwise for a right bank) under a fixed hollow index at the top; the slip brick rides under the pointer.
    /// Ticks at 10, 20, 30 and 60 deg (long at 30 and 60) plus a short 45 deg mark. The arc ends at 60 deg, which is also the pointer's peg:
    /// beyond it the pointer stays pegged at the arc end, hollow and amber (flashing for 5 s, then steady), so the clamp is never silent.
    /// Invalid attitude removes the pointer and brick; the scale itself stays.
    /// Local units are reference pixels at module scale 1, and the local origin is the arc centre. Lane (zone plan): arc top 112 ref from
    /// the screen top (index 91-109, clear of the FMA lane that ends at 80), arc ends at about 207 ref, above the protected attitude field.
    /// The two 60 deg end marks are registered as small keep-outs so the conformal ladder and horizon never stroke through them.
    /// </summary>
    [DisallowMultipleComponent, DefaultExecutionOrder(12495)]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaDigitalBankGraphic : MaskableGraphic
    {
        public const string ObjectName="FAA Digital Bank Scale";
        public const float Radius=190f, ScaleEndDegrees=60f;
        /// <summary>Centre line of the arc stroke at its top, reference px from the top of the screen.</summary>
        public const float ArcTopFromScreenTop=112f;
        public const float ArcStroke=2.6f, HaloExtra=2.4f;
        public const float PointerGap=3f, PointerLength=18f, PointerHalfWidth=10f;
        public const float IndexGap=3f, IndexLength=18f, IndexHalfWidth=10f;
        public const float BrickCenterInset=27f, BrickHalfWidth=13f, BrickHalfHeight=2.6f;
        public const float MajorTickLength=22f, MinorTickLength=12f, ShortTickLength=9f;
        /// <summary>Tick bearings either side of the index. 45 deg is a short mark; 30 and 60 are long.</summary>
        public static readonly float[] TickAngles={10f,20f,30f,45f,60f};
        /// <summary>Dark underlay that keeps the symbols at 3:1 or better over bright sky and haze.</summary>
        public static readonly Color Halo=new Color(0f,0f,0f,.5f);
        private static readonly string[] EndMarkNames={"Bank End Mark L","Bank End Mark R"};
        public const string EndMarkLeftId="bank-end-left", EndMarkRightId="bank-end-right";
        /// <summary>Margin around each end mark, reference units (the conformal layer adds its own keep-out padding).</summary>
        public const float EndMarkPadding=3f;
        private readonly EndMarkRegion[] endMarks={new EndMarkRegion(EndMarkLeftId),new EndMarkRegion(EndMarkRightId)};

        private float bank,slip;
        private bool valid,slipValid,flashOn=true,initialized;
        private Color tint=FaaHudStyle.Green;
        private Canvas canvasCache;

        /// <summary>Bearing of the sky pointer (degrees clockwise from up) currently drawn; 0 when no pointer is drawn.</summary>
        public float PointerBearingDegrees=>valid?BearingFor(bank):0f;
        public bool PointerDrawn=>valid&&(!OffScale||flashOn);
        /// <summary>The brick follows the pointer's bearing but is separate data: it is not blanked by the off-scale flash.</summary>
        public bool BrickDrawn=>valid&&slipValid;
        /// <summary>True while |bank| exceeds the arc end: the pointer is pegged and drawn hollow and amber.</summary>
        public bool OffScale=>valid&&Mathf.Abs(Mathf.DeltaAngle(0f,bank))>ScaleEndDegrees;
        public Color Tint=>tint;
        /// <summary>The two protected end-mark regions (registered with <see cref="FaaHudKeepOut"/> while the scale is enabled).</summary>
        public FaaHudKeepOut.IRegion GetEndMarkRegion(int side)=>side>=0&&side<2?endMarks[side]:null;
        /// <summary>End-mark rectangle (left = 0, right = 1); its local position is pivot-relative, i.e. about the arc centre.</summary>
        public RectTransform EndMark(int side)=>side>=0&&side<2?endMarks[side].Target:null;

        /// <summary>Sky-pointer bearing (degrees clockwise from up) for a bank angle (right wing down positive), pegged at the arc end.</summary>
        public static float BearingFor(float bankDegrees)
        {
            if(!FaaAnalogFlightSample.Finite(bankDegrees))return 0f;
            return -Mathf.Clamp(Mathf.DeltaAngle(0f,bankDegrees),-ScaleEndDegrees,ScaleEndDegrees);
        }
        /// <summary>Pointer tip in local units (arc centre at the origin), on the arc's radius less the pointer gap.</summary>
        public static Vector2 PointerTip(float bankDegrees)=>Bearing(BearingFor(bankDegrees),Radius-PointerGap);
        /// <summary>Slip brick centre for a bank and a slip (g_side, clamped to +/-1): one brick width of travel at full scale, same sign as Classic.</summary>
        public static Vector2 BrickCenter(float bankDegrees,float slipValue)
        {
            float angle=BearingFor(bankDegrees);
            float s=FaaAnalogFlightSample.Finite(slipValue)?Mathf.Clamp(slipValue,-1f,1f):0f;
            return Bearing(angle,Radius-BrickCenterInset)+Bearing(angle+90f,s*2f*BrickHalfWidth);
        }

        /// <summary>Finds or creates the scale under <paramref name="parent"/> (the 'Bank Scale' module root, so the Digital style gate,
        /// module scaling and the inspection dimming all apply to it).</summary>
        public static FaaDigitalBankGraphic Ensure(Transform parent)
        {
            if(parent==null)return null;
            Transform existing=parent.Find(ObjectName);
            GameObject go=existing!=null?existing.gameObject:new GameObject(ObjectName,typeof(RectTransform),typeof(CanvasRenderer));
            if(go.transform.parent!=parent)go.transform.SetParent(parent,false);
            if(!go.activeSelf)go.SetActive(true);
            var graphic=go.GetComponent<FaaDigitalBankGraphic>();
            if(graphic==null)graphic=go.AddComponent<FaaDigitalBankGraphic>();
            graphic.Configure();
            return graphic;
        }

        private void Configure()
        {
            raycastTarget=false;
            var rt=rectTransform;
            rt.anchorMin=rt.anchorMax=new Vector2(.5f,.5f);
            // The rect covers the drawn extent (x +/-195, y +66..+214 above the centre) so layout bounds and inspection framing are right.
            rt.sizeDelta=new Vector2(390f,148f);rt.pivot=new Vector2(.5f,-66f/148f);rt.localRotation=Quaternion.identity;
            // 1 local unit = 1 reference pixel at module scale 1: undo the HUD root's 540x scale but keep the module scale on top of it.
            float hud=RelativeScale(transform.parent!=null?transform.parent.parent:null);
            rt.localScale=Vector3.one/Mathf.Max(.0001f,hud);
            for(int side=0;side<2;side++)
            {
                Transform found=transform.Find(EndMarkNames[side]);
                GameObject mark=found!=null?found.gameObject:new GameObject(EndMarkNames[side],typeof(RectTransform));
                if(mark.transform.parent!=transform)mark.transform.SetParent(transform,false);
                var r=(RectTransform)mark.transform;
                r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.localScale=Vector3.one;r.localRotation=Quaternion.identity;
                // Pivot-relative: the arc centre is the parent's pivot, so the mark sits on the 60 deg tick (R..R+22).
                Vector2 c=Bearing((side==0?-1f:1f)*ScaleEndDegrees,Radius+MajorTickLength*.5f);
                r.anchoredPosition=c+PivotOffset(rt);r.sizeDelta=new Vector2(26f,18f);
                endMarks[side].Bind(this,r);
            }
            SetVerticesDirty();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            foreach(var mark in endMarks)FaaHudKeepOut.Register(mark);
        }

        protected override void OnDisable()
        {
            foreach(var mark in endMarks)FaaHudKeepOut.Unregister(mark);
            base.OnDisable();
        }

        /// <summary>
        /// Keep-out for one 60 deg end mark. Visibility is read live from the renderer's inherited alpha, so the Digital style gate
        /// (alpha 0 in Classic) removes it without any cached parent list.
        /// </summary>
        private sealed class EndMarkRegion:FaaHudKeepOut.IRegion
        {
            private readonly Vector3[] corners=new Vector3[4];
            private FaaDigitalBankGraphic owner;
            public EndMarkRegion(string id){Id=id;}
            public string Id {get;}
            public FaaKeepOutKind Kind=>FaaKeepOutKind.Symbology;
            public RectTransform Target {get;private set;}
            public void Bind(FaaDigitalBankGraphic graphic,RectTransform target){owner=graphic;Target=target;}
            public bool TryGetScreenRect(out Rect rect)
            {
                rect=default;
                if(owner==null||Target==null||!owner.isActiveAndEnabled||owner.canvasRenderer.GetInheritedAlpha()<.05f)return false;
                var canvas=owner.canvas;
                if(canvas==null||!canvas.isActiveAndEnabled)return false;
                var root=canvas.rootCanvas;
                Camera cam=root.renderMode==RenderMode.ScreenSpaceOverlay?null:(root.worldCamera!=null?root.worldCamera:Camera.main);
                Target.GetWorldCorners(corners);
                float xMin=float.MaxValue,yMin=float.MaxValue,xMax=float.MinValue,yMax=float.MinValue;
                for(int i=0;i<4;i++)
                {
                    if(cam!=null&&cam.WorldToViewportPoint(corners[i]).z<0)return false;
                    Vector2 p=cam==null?(Vector2)corners[i]:RectTransformUtility.WorldToScreenPoint(cam,corners[i]);
                    xMin=Mathf.Min(xMin,p.x);yMin=Mathf.Min(yMin,p.y);xMax=Mathf.Max(xMax,p.x);yMax=Mathf.Max(yMax,p.y);
                }
                float pad=EndMarkPadding*Mathf.Max(.01f,root.scaleFactor);
                rect=Rect.MinMaxRect(xMin-pad,yMin-pad,xMax+pad,yMax+pad);
                return rect.width>0&&rect.height>0;
            }
        }

        /// <summary>Anchored positions are measured from the anchor (rect centre here), the mesh from the pivot (the arc centre):
        /// a child at pivot-relative position c has anchoredPosition c + (pivot - 0.5) * size.</summary>
        private static Vector2 PivotOffset(RectTransform rt)=>new Vector2((rt.pivot.x-.5f)*rt.sizeDelta.x,(rt.pivot.y-.5f)*rt.sizeDelta.y);

        /// <summary>Scale of <paramref name="t"/> relative to the root canvas (falls back to the local scale chain without a canvas).</summary>
        private static float RelativeScale(Transform t)
        {
            if(t==null)return 1f;
            var c=t.GetComponentInParent<Canvas>();
            if(c!=null)
            {
                float root=Mathf.Abs(c.rootCanvas.transform.lossyScale.y),own=Mathf.Abs(t.lossyScale.y);
                if(root>1e-6f&&own>1e-6f)return own/root;
            }
            float s=1f;for(Transform p=t;p!=null&&p.GetComponent<Canvas>()==null;p=p.parent)s*=Mathf.Abs(p.localScale.y);
            return s>1e-6f?s:1f;
        }

        /// <summary>
        /// Feeds one frame. <paramref name="bankDegrees"/> is the raw bank (right wing down positive, no easing on flight data);
        /// <paramref name="drawOffScalePointer"/> is the blink phase of the off-scale cue. The mesh is rebuilt only when something visible changes.
        /// </summary>
        public void Present(float bankDegrees,bool attitudeValid,float slipValue,bool slipMeasured,Color pilotTint,bool drawOffScalePointer)
        {
            raycastTarget=false;
            bool nowValid=attitudeValid&&FaaAnalogFlightSample.Finite(bankDegrees);
            bool nowSlip=slipMeasured&&FaaAnalogFlightSample.Finite(slipValue);
            float b=nowValid?bankDegrees:0f,s=nowSlip?Mathf.Clamp(slipValue,-1f,1f):0f;
            Color c=FaaHudStyle.WithAlpha(pilotTint,1f);
            if(initialized&&nowValid==valid&&nowSlip==slipValid&&drawOffScalePointer==flashOn&&c==tint&&
               Mathf.Abs(Mathf.DeltaAngle(b,bank))<.01f&&Mathf.Abs(s-slip)<.0005f)return;
            bank=b;slip=s;valid=nowValid;slipValid=nowSlip;flashOn=drawOffScalePointer;tint=c;initialized=true;
            SetVerticesDirty();
        }

        private void LateUpdate()
        {
            if(Application.isPlaying)Place();
        }

        /// <summary>Pins the arc top in the bank lane in root-canvas space, so the module scale grows the scale downward, never into the FMA.</summary>
        private void Place()
        {
            if(canvasCache==null)canvasCache=GetComponentInParent<Canvas>();
            if(canvasCache==null)return;
            var root=canvasCache.rootCanvas.transform as RectTransform;
            if(root==null)return;
            Rect r=root.rect;
            if(r.height<100f||r.width<100f)return;
            float rootScale=Mathf.Abs(root.lossyScale.y),own=Mathf.Abs(transform.lossyScale.y);
            float scale=rootScale>1e-6f&&own>1e-6f?own/rootScale:1f;
            var target=new Vector3(r.center.x,r.yMax-ArcTopFromScreenTop-Radius*scale,0f);
            var rt=rectTransform;
            Vector3 local=root.InverseTransformPoint(rt.position);
            if((new Vector2(local.x,local.y)-new Vector2(target.x,target.y)).sqrMagnitude>.0001f)rt.position=root.TransformPoint(target);
            if(Quaternion.Angle(rt.rotation,root.rotation)>.01f)rt.rotation=root.rotation;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Color c=tint;
            // Halo pass first, then the symbols, so every stroke keeps a dark edge over bright sky.
            for(int pass=0;pass<2;pass++)
            {
                bool halo=pass==0;Color k=halo?Halo:c;float extra=halo?HaloExtra:0f;
                Arc(vh,Vector2.zero,Radius,-ScaleEndDegrees,ScaleEndDegrees,ArcStroke+extra,k,2f);
                foreach(float a in TickAngles)
                {
                    bool major=a==30f||a==60f;
                    float length=major?MajorTickLength:a==45f?ShortTickLength:MinorTickLength,width=(major?2.6f:2f)+extra;
                    for(int s=-1;s<=1;s+=2)Line(vh,Bearing(s*a,Radius-(halo?1f:0f)),Bearing(s*a,Radius+length+(halo?1f:0f)),width,k);
                }
                // Fixed hollow index above the arc, apex on the arc.
                HollowTriangle(vh,new Vector2(0,Radius+IndexGap),new Vector2(-IndexHalfWidth,Radius+IndexGap+IndexLength),
                    new Vector2(IndexHalfWidth,Radius+IndexGap+IndexLength),2.2f+extra,k);
                if(!valid)continue;
                float angle=BearingFor(bank);
                if(PointerDrawn)
                {
                    bool beyond=OffScale;
                    Color pointerColor=halo?Halo:beyond?FaaHudStyle.Amber:c;
                    Vector2 tip=Bearing(angle,Radius-PointerGap),baseCenter=Bearing(angle,Radius-PointerGap-PointerLength),across=Bearing(angle+90f,PointerHalfWidth);
                    if(halo||beyond)HollowTriangle(vh,tip,baseCenter-across,baseCenter+across,(beyond?2.4f:1.2f)+extra,pointerColor);
                    if(!halo&&!beyond)Triangle(vh,tip,baseCenter-across,baseCenter+across,pointerColor);
                }
                if(!slipValid)continue;
                Vector2 brick=BrickCenter(bank,slip),w=Bearing(angle+90f,BrickHalfWidth+extra*.5f),h=Bearing(angle,BrickHalfHeight+extra*.5f);
                Triangle(vh,brick-w-h,brick-w+h,brick+w+h,k);Triangle(vh,brick-w-h,brick+w+h,brick+w-h,k);
            }
        }
    }
}
