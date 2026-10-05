using System.Collections.Generic;
using FAA.XPlaneIntegration.Runtime;
using UnityEngine;
namespace FAA.Customization
{
    /// <summary>Result of <see cref="FaaInspectionFraming.Fit"/>: where to aim, how far to zoom, and what the pilot will see.</summary>
    public struct FaaInspectionFrame
    {
        /// <summary>Camera aim, x = yaw and y = elevation, degrees in the cockpit frame.</summary>
        public Vector2 Aim;
        /// <summary>Target vertical FOV in degrees (the base FOV when no zoom is wanted or allowed).</summary>
        public float VerticalFov;
        /// <summary>Predicted panel rectangle in screen pixels (bottom-left origin) once the turn and zoom have settled.</summary>
        public Rect ScreenRect;
        /// <summary>Rendered size of the panel's smallest text in reference pixels (1080-high screen); 0 when unknown.</summary>
        public float TextReferencePixels;
        /// <summary>The panel clears every protected HUD area and the screen edges.</summary>
        public bool Clear;
    }

    /// <summary>
    /// Camera aim and desktop zoom for inspecting a side panel, from the panel's visible angular bounds and its smallest text.
    /// The panel is placed where it clears every protected head-fixed awareness readout (IAS, ALT, TQ/NR, VS, FMA, heading scale
    /// and readout) and the pilot chrome, and as close to the screen centre as that allows. The vertical FOV is narrowed (never
    /// widened) until the smallest text reaches <see cref="FaaHudStyle.MinLabel"/> reference pixels, but only as far as the panel
    /// still fits in that clear area. Pure math for EditMode tests; runs once per inspection (no per-frame work).
    /// </summary>
    public static class FaaInspectionFraming
    {
        /// <summary>Band limits in reference-canvas pixels from the top of a 1080-high screen: below the bank-arc ends, above the heading scale.</summary>
        public const float TopLimitFromTop=245f, BottomLimitFromTop=780f, ReferenceHeight=1080f;
        public const float DefaultVerticalFov=60f;
        /// <summary>Narrowest inspection zoom (degrees of vertical FOV).</summary>
        public const float MinZoomFov=18f;
        /// <summary>Clearance kept between the framed panel and protected areas / screen edges (reference pixels).</summary>
        public const float ClearanceReference=8f;
        private const int GridX=29, GridY=17;
        private const float LevelRatio=.95f;

        /// <summary>Elevation (deg, relative to the view axis) of a screen row given in reference pixels from the top.</summary>
        public static float ScreenElevation(float fromTop,float verticalFov)
        {
            float fov=SafeFov(verticalFov);
            return Mathf.Atan((1f-2f*fromTop/ReferenceHeight)*Mathf.Tan(.5f*fov*Mathf.Deg2Rad))*Mathf.Rad2Deg;
        }
        /// <summary>Upper and lower edges (deg above the view axis) of the legacy fixed band (kept for compatibility).</summary>
        public static Vector2 Band(float verticalFov)=>new Vector2(ScreenElevation(BottomLimitFromTop,verticalFov),ScreenElevation(TopLimitFromTop,verticalFov));

        /// <summary>
        /// Legacy fixed-band aim (x = yaw, y = elevation) without keep-outs or zoom; <see cref="Fit"/> replaces it at runtime.
        /// </summary>
        public static Vector2 Aim(Vector2 yawRange,Vector2 elevationRange,float verticalFov)
        {
            Vector2 band=Band(verticalFov);
            float lo=Mathf.Min(elevationRange.x,elevationRange.y),hi=Mathf.Max(elevationRange.x,elevationRange.y);
            float half=(hi-lo)*.5f,mid=(hi+lo)*.5f;
            float centre=2f*half<=band.y-band.x?(band.x+band.y)*.5f:band.y-half;
            return new Vector2((yawRange.x+yawRange.y)*.5f,mid-centre);
        }

        /// <summary>Focal length in pixels for a vertical FOV on a screen <paramref name="height"/> pixels high.</summary>
        public static float FocalPixels(float verticalFov,float height)=>.5f*Mathf.Max(1f,height)/Mathf.Tan(.5f*SafeFov(verticalFov)*Mathf.Deg2Rad);
        /// <summary>Vertical FOV (degrees) for a focal length in pixels.</summary>
        public static float FovForFocal(float focal,float height)=>2f*Mathf.Atan(.5f*Mathf.Max(1f,height)/Mathf.Max(1f,focal))*Mathf.Rad2Deg;
        /// <summary>
        /// Vertical FOV at which text of angular height <paramref name="textDegrees"/> renders at <paramref name="referencePixels"/>
        /// on a 1080-reference screen (independent of the real screen height). Returns <paramref name="baseFov"/> when the text is unknown.
        /// </summary>
        public static float FovForText(float textDegrees,float referencePixels,float baseFov)
        {
            if(!(textDegrees>0f)||!(referencePixels>0f))return SafeFov(baseFov);
            return FovForFocal(referencePixels/Mathf.Tan(textDegrees*Mathf.Deg2Rad),ReferenceHeight);
        }

        /// <summary>
        /// Zone-plan protected areas (awareness readouts and chrome) for a screen of the given size, used when no module has
        /// registered its own keep-out yet. Reference rectangles (x, y from the top, width, height) on a 1920x1080 screen, measured
        /// from the Digital layout at 100 % with the same 8 px padding the module keep-outs add.
        /// </summary>
        public static void DefaultKeepOuts(float width,float height,List<Rect> into)
        {
            if(into==null)return;
            float k=Mathf.Max(1f,height)/ReferenceHeight,cx=.5f*Mathf.Max(1f,width);
            foreach(var r in DefaultReference)
                into.Add(new Rect(cx+(r.x-960f)*k,height-(r.y+r.height)*k,r.width*k,r.height*k));
        }
        private static readonly Rect[] DefaultReference=
        {
            new Rect(690,8,540,80),     // FMA (Z1)
            new Rect(491,510,155,76),   // IAS readout and caption
            new Rect(510,614,117,159),  // TQ digits and engine rails
            new Rect(1274,510,153,76),  // ALT readout and caption
            new Rect(1307,614,88,159),  // NR digits and N2 rails
            new Rect(1446,443,108,175), // VS scale and readout
            new Rect(770,780,380,90),   // heading scale, readout and bug (Z5)
            new Rect(0,1012,1920,68),   // pilot chrome bar (Z6)
        };

        /// <summary>
        /// Fits a panel whose visible bounds span <paramref name="yawRange"/> x <paramref name="elevationRange"/> (degrees, cockpit
        /// frame, x = min, y = max) and whose smallest text is <paramref name="textDegrees"/> high, on a screen of
        /// <paramref name="width"/> x <paramref name="height"/> pixels with protected rectangles <paramref name="keepOuts"/> (screen
        /// pixels, bottom-left origin). <paramref name="allowZoom"/> is false in XR (1:1 view): only the aim is solved.
        /// </summary>
        public static FaaInspectionFrame Fit(Vector2 yawRange,Vector2 elevationRange,float textDegrees,float baseFov,float width,float height,
            IReadOnlyList<Rect> keepOuts,bool allowZoom)
        {
            float w=Mathf.Max(16f,width),h=Mathf.Max(16f,height),k=h/ReferenceHeight;
            float yawLo=Mathf.Min(yawRange.x,yawRange.y),yawHi=Mathf.Max(yawRange.x,yawRange.y);
            float elLo=Mathf.Min(elevationRange.x,elevationRange.y),elHi=Mathf.Max(elevationRange.x,elevationRange.y);
            float yawC=.5f*(yawLo+yawHi),elC=Mathf.Clamp(.5f*(elLo+elHi),-80f,80f);
            float cosEl=Mathf.Max(.2f,Mathf.Cos(elC*Mathf.Deg2Rad));
            // Horizontal half-angle of the panel as seen (yaw spans shrink with elevation), and vertical half-angle.
            float alpha=Mathf.Atan(Mathf.Tan(Mathf.Clamp(.5f*(yawHi-yawLo),0f,80f)*Mathf.Deg2Rad)*cosEl)*Mathf.Rad2Deg;
            float beta=Mathf.Clamp(.5f*(elHi-elLo),0f,80f);
            float baseF=FocalPixels(baseFov,h);
            float maxF=allowZoom?Mathf.Max(baseF,FocalPixels(MinZoomFov,h)):baseF;
            float wantF=Mathf.Clamp(textDegrees>0f?FaaHudStyle.MinLabel*k/Mathf.Tan(textDegrees*Mathf.Deg2Rad):baseF,baseF,maxF);
            float m=ClearanceReference*k;
            var centre=new Vector2(.5f*w,.5f*h);
            // Zoom levels from the legibility target down to the base focal length: the first level with a clear placement wins.
            float f=wantF;Vector2 best=centre;bool clear=false;
            for(int guard=0;guard<64;guard++)
            {
                if(TryPlace(f,alpha,beta,w,h,m,keepOuts,centre,out best)){clear=true;break;}
                if(f<=baseF*1.0001f)break;
                f=Mathf.Max(baseF,f*LevelRatio);
            }
            if(clear&&f<wantF*.9999f)
            {
                // Tighten between the clear level and the next larger one, so the text gets every pixel the clear area allows.
                float lo=f,hi=Mathf.Min(wantF,f/LevelRatio);
                for(int i=0;i<6;i++){float mid=.5f*(lo+hi);if(TryPlace(mid,alpha,beta,w,h,m,keepOuts,centre,out var p)){lo=mid;best=p;}else hi=mid;}
                f=lo;
            }
            if(!clear){f=baseF;best=LeastOverlap(f,alpha,beta,w,h,m,keepOuts,centre);}
            float dx=best.x-centre.x,dy=best.y-centre.y;
            float pitchOffset=Mathf.Atan(dy/f);
            float elevation=elC-pitchOffset*Mathf.Rad2Deg;
            float yaw=yawC-Mathf.Atan(dx*Mathf.Cos(pitchOffset)/f)*Mathf.Rad2Deg/cosEl;
            float textPixels=textDegrees>0f?f*Mathf.Tan(textDegrees*Mathf.Deg2Rad)/k:0f;
            return new FaaInspectionFrame{Aim=new Vector2(yaw,elevation),VerticalFov=FovForFocal(f,h),ScreenRect=PanelRect(f,best,alpha,beta,w,h),
                TextReferencePixels=textPixels,Clear=clear};
        }

        /// <summary>Screen rectangle of a panel whose angular centre projects to <paramref name="at"/> (exact tangent projection).</summary>
        public static Rect PanelRect(float focal,Vector2 at,float alphaDegrees,float betaDegrees,float width,float height)
        {
            float a=alphaDegrees*Mathf.Deg2Rad,b=betaDegrees*Mathf.Deg2Rad;
            float ox=Mathf.Atan((at.x-.5f*width)/focal),oy=Mathf.Atan((at.y-.5f*height)/focal);
            float x0=.5f*width+focal*Mathf.Tan(Mathf.Clamp(ox-a,-1.5f,1.5f)),x1=.5f*width+focal*Mathf.Tan(Mathf.Clamp(ox+a,-1.5f,1.5f));
            float y0=.5f*height+focal*Mathf.Tan(Mathf.Clamp(oy-b,-1.5f,1.5f)),y1=.5f*height+focal*Mathf.Tan(Mathf.Clamp(oy+b,-1.5f,1.5f));
            return Rect.MinMaxRect(x0,y0,x1,y1);
        }
        private static bool Clear(Rect r,float w,float h,float m,IReadOnlyList<Rect> keepOuts)
        {
            if(r.xMin<m||r.yMin<m||r.xMax>w-m||r.yMax>h-m)return false;
            if(keepOuts==null)return true;
            for(int i=0;i<keepOuts.Count;i++)
            {
                Rect o=keepOuts[i];
                if(r.xMin<o.xMax+m&&r.xMax>o.xMin-m&&r.yMin<o.yMax+m&&r.yMax>o.yMin-m)return false;
            }
            return true;
        }
        /// <summary>
        /// Clear placement closest to the screen centre at focal length <paramref name="f"/>. The closest clear centre is either the
        /// screen centre or a position where a panel edge touches a protected area or the screen margin, so those candidates are
        /// solved exactly (tangent projection) and tested in pairs. No allocation after the first call.
        /// </summary>
        private static bool TryPlace(float f,float alpha,float beta,float w,float h,float m,IReadOnlyList<Rect> keepOuts,Vector2 centre,out Vector2 best)
        {
            best=centre;
            if(Clear(PanelRect(f,centre,alpha,beta,w,h),w,h,m,keepOuts))return true;
            float a=alpha*Mathf.Deg2Rad,b=beta*Mathf.Deg2Rad;
            int count=keepOuts!=null?keepOuts.Count:0,size=2*count+3;
            if(xs.Length<size){xs=new float[size];ys=new float[size];}
            int nx=0,ny=0;const float e=.01f;
            xs[nx++]=centre.x;ys[ny++]=centre.y;
            xs[nx++]=Edge(m+e,f,a,w);xs[nx++]=Edge(w-m-e,f,-a,w);
            ys[ny++]=Edge(m+e,f,b,h);ys[ny++]=Edge(h-m-e,f,-b,h);
            for(int i=0;i<count;i++)
            {
                Rect o=keepOuts[i];
                xs[nx++]=Edge(o.xMax+m+e,f,a,w);xs[nx++]=Edge(o.xMin-m-e,f,-a,w);
                ys[ny++]=Edge(o.yMax+m+e,f,b,h);ys[ny++]=Edge(o.yMin-m-e,f,-b,h);
            }
            float bestDistance=float.PositiveInfinity;
            for(int ix=0;ix<nx;ix++)for(int iy=0;iy<ny;iy++)
            {
                var p=new Vector2(xs[ix],ys[iy]);float d=(p-centre).sqrMagnitude;
                if(d>=bestDistance||!Clear(PanelRect(f,p,alpha,beta,w,h),w,h,m,keepOuts))continue;
                best=p;bestDistance=d;
            }
            return !float.IsInfinity(bestDistance);
        }
        private static float[] xs=new float[24],ys=new float[24];
        /// <summary>
        /// Screen coordinate of the panel's angular centre that puts one panel edge exactly on <paramref name="edge"/>: the near
        /// (low) edge for a positive <paramref name="halfRadians"/>, the far (high) edge for a negative one.
        /// </summary>
        private static float Edge(float edge,float f,float halfRadians,float size)=>
            .5f*size+f*Mathf.Tan(Mathf.Clamp(Mathf.Atan((edge-.5f*size)/f)+halfRadians,-1.5f,1.5f));
        /// <summary>When nothing is clear even at the base FOV: the grid placement with the least protected area covered.</summary>
        private static Vector2 LeastOverlap(float f,float alpha,float beta,float w,float h,float m,IReadOnlyList<Rect> keepOuts,Vector2 centre)
        {
            Vector2 best=centre;float bestCost=float.PositiveInfinity;
            for(int iy=0;iy<GridY;iy++)for(int ix=0;ix<GridX;ix++)
            {
                var p=new Vector2(Mathf.Lerp(m,w-m,(ix+.5f)/GridX),Mathf.Lerp(m,h-m,(iy+.5f)/GridY));
                Rect r=PanelRect(f,p,alpha,beta,w,h);float cost=0f;
                if(keepOuts!=null)for(int i=0;i<keepOuts.Count;i++)cost+=Overlap(r,keepOuts[i]);
                cost+=1e-4f*(p-centre).sqrMagnitude;
                if(cost<bestCost){bestCost=cost;best=p;}
            }
            return best;
        }
        private static float Overlap(Rect a,Rect b)=>Mathf.Max(0f,Mathf.Min(a.xMax,b.xMax)-Mathf.Max(a.xMin,b.xMin))*Mathf.Max(0f,Mathf.Min(a.yMax,b.yMax)-Mathf.Max(a.yMin,b.yMin));
        private static float SafeFov(float fov)=>float.IsNaN(fov)||float.IsInfinity(fov)?DefaultVerticalFov:Mathf.Clamp(fov,10f,120f);
    }

    /// <summary>Off-axis head-fixed HUD declutter with hysteresis (enter above 50 deg of view yaw, leave below 40 deg).</summary>
    public static class FaaInspectionDeclutter
    {
        public const float EnterYaw=50f, ExitYaw=40f;
        public static bool OffAxis(float viewYaw,bool wasOffAxis)
        {
            if(float.IsNaN(viewYaw)||float.IsInfinity(viewYaw))return false;
            float a=Mathf.Abs(Mathf.DeltaAngle(0f,viewYaw));
            return wasOffAxis?a>ExitYaw:a>EnterYaw;
        }
    }

    /// <summary>
    /// Inspection declutter. While a side panel is inspected (desktop camera turn), or the view is turned well away from the cockpit
    /// boresight (XR head turn or desktop free look), the forward head-fixed HUD dims through <see cref="FaaHudInspection"/>:
    /// essential awareness readouts keep <see cref="FaaHudInspection.AwarenessHudIntensity"/>, the instrument being resized from
    /// Settings stays at full intensity, alerts cancel the dimming. Every other panel (utilities and radars) is de-emphasised, and
    /// hidden when it would sit on a protected HUD area, the inspected panel or the screen edge. On desktop the view also zooms so
    /// the inspected panel's text is legible. One owner decides the FaaHudInspection state each frame.
    /// </summary>
    public sealed partial class FaaSpatialWorkspace
    {
        public const string OffAxisInspectionId="off-axis";
        /// <summary>Alpha of panels that are not the inspected one (when they are clear of the HUD and the inspected panel).</summary>
        public const float NeighbourPanelAlpha=.15f;
        /// <summary>View yaw from the cockpit boresight, degrees (positive right).</summary>
        public float ViewYawOffset { get; private set; }
        /// <summary>The view is turned far enough from the boresight that forward-referenced symbology would mislead.</summary>
        public bool ViewOffAxis { get; private set; }
        /// <summary>Head-fixed module kept at full intensity while the pilot resizes it from the Settings INSTRUMENTS page.</summary>
        public Transform InspectionPreviewTarget { get; private set; }
        public string InspectionPreviewId { get; private set; }
        /// <summary>
        /// An engine torque, rotor NR or airspeed exceedance is active (fresh data, shared FaaRotorcraftLimits). Declutter never
        /// suppresses alerting (AC 25.1322-1): while this is true the forward HUD stays at full intensity even with a panel in view.
        /// </summary>
        public bool ExceedanceAlertActive { get; private set; }
        public const float AlertClearSeconds=1f;
        /// <summary>The last inspection framing (aim, zoom, predicted rectangle, rendered text size), for tools and tests.</summary>
        public FaaInspectionFrame LastInspectionFrame { get; private set; }
        private XPlane12ApiHudBridge alertBridge;
        private float nextAlertSample,nextAlertBridgeSearch,alertSeenAt=float.NegativeInfinity;
        private Vector2 inspectedSpan;private float nextReframe,nextNeighbourCheck;
        private Rect inspectedScreenRect;private bool inspectedScreenRectValid;
        /// <summary>
        /// Screen rectangle (pixels, bottom-left origin) of the inspected panel, refreshed at 10 Hz while a panel is inspected on
        /// desktop. Head-fixed layers that draw over the centre (conformal ladder, FPV, bank scale) can leave this area empty so
        /// HUD and panel text never interleave. False when no panel is inspected.
        /// </summary>
        public bool TryGetInspectedPanelRect(out Rect rect){rect=inspectedScreenRect;return InspectedPanelId!=null&&inspectedScreenRectValid;}
        private readonly List<Rect> framingKeepOuts=new();
        /// <summary>Protected areas that do not constrain inspection framing: deviation scales are secondary and dimmed while inspecting.</summary>
        private static readonly HashSet<string> FramingIgnoredKeepOuts=new(System.StringComparer.Ordinal)
            {"module:glideslope","module:localizer","gs-scale","loc-scale"};

        /// <summary>True when a fresh sample shows a caution or warning exceedance (invalid data never alerts).</summary>
        public static bool SampleHasExceedance(in FaaAnalogFlightSample s)
        {
            if(!s.Fresh)return false;
            bool airborne=FaaRotorcraftLimits.LikelyAirborne(s.SpeedValid?s.Speed:0f,s.HeightAboveGround,s.HeightAboveGroundValid);
            return Alerting(FaaRotorcraftLimits.Torque(s.Engine1Torque,s.Engine1Valid))||Alerting(FaaRotorcraftLimits.Torque(s.Engine2Torque,s.Engine2Valid))||
                Alerting(FaaRotorcraftLimits.Torque(s.Torque,s.TorqueValid))||Alerting(FaaRotorcraftLimits.RotorNr(s.Rpm,s.RpmValid,airborne))||
                FaaRotorcraftLimits.Airspeed(s.Speed,s.SpeedValid)==FaaExceedance.Warning;
        }
        private static bool Alerting(FaaExceedance e)=>e==FaaExceedance.Caution||e==FaaExceedance.Warning;

        /// <summary>
        /// Forward-HUD intensity for a head-fixed element: 1 for the instrument previewed from Settings, else
        /// <see cref="FaaHudInspection.ForwardHudIntensity"/>. Every non-awareness layer uses this.
        /// </summary>
        public static float ForwardIntensityFor(Transform element)
        {
            var w=Current;
            if(w!=null&&element!=null&&w.InspectionPreviewTarget!=null&&(element==w.InspectionPreviewTarget||element.IsChildOf(w.InspectionPreviewTarget)))return 1f;
            return FaaHudInspection.ForwardHudIntensity;
        }
        /// <summary>
        /// Intensity for an essential awareness readout (IAS, ALT, TQ/NR digits, VS readout, FMA, heading readout):
        /// max(<see cref="ForwardIntensityFor"/>, <see cref="FaaHudInspection.AwarenessHudIntensity"/>), i.e. 0.65 while inspecting.
        /// </summary>
        public static float AwarenessIntensityFor(Transform element)=>Mathf.Max(ForwardIntensityFor(element),FaaHudInspection.AwarenessHudIntensity);

        /// <summary>
        /// The camera's vertical FOV without the desktop inspection zoom (the value restored afterwards). Size or readability
        /// measures that should not react to the temporary zoom (for example radar text scaling) use this.
        /// </summary>
        public static float UnzoomedFieldOfView(Camera camera)
        {
            if(camera==null)return FaaInspectionFraming.DefaultVerticalFov;
            var controller=camera.GetComponent<AircraftControl.Camera.AircraftCameraController>();
            return controller!=null?controller.BaseFieldOfView:camera.fieldOfView;
        }

        /// <summary>Desktop only: native XR and XR-3 compatibility keep a 1:1 view, and a tracked head is never zoomed.</summary>
        public bool InspectionZoomAllowed=>!NativeXr&&!nativeDefaults&&desktopView!=null&&desktopView.isActiveAndEnabled&&
            (trackedView==null||!trackedView.isActiveAndEnabled);

        /// <summary>Once per frame after <see cref="RefreshTransforms"/>. No per-frame allocations (the alert sample runs at 4 Hz, neighbour checks at 10 Hz, the radar re-frame check at 2 Hz).</summary>
        private void UpdateInspection(float dt)
        {
            if(View!=null&&CockpitFrame!=null)
            {
                Vector3 f=Quaternion.Inverse(CockpitFrame.rotation)*View.transform.forward;
                ViewYawOffset=new Vector2(f.x,f.z).sqrMagnitude>1e-6f?Mathf.Atan2(f.x,f.z)*Mathf.Rad2Deg:0f;
                ViewOffAxis=FaaInspectionDeclutter.OffAxis(ViewYawOffset,ViewOffAxis);
            }
            else ViewOffAxis=false;
            SampleExceedanceAlert();
            ApplyHudInspection();
            UpdatePanelFocus(dt);
            ReframeGrownPanel();
        }
        private void SampleExceedanceAlert()
        {
            float now=Time.unscaledTime;
            if(now<nextAlertSample)return;
            nextAlertSample=now+.25f;
            if(alertBridge==null&&now>=nextAlertBridgeSearch){nextAlertBridgeSearch=now+1f;alertBridge=FindFirstObjectByType<XPlane12ApiHudBridge>();}
            if(alertBridge!=null&&SampleHasExceedance(FaaAnalogFlightSample.Capture(alertBridge)))alertSeenAt=now;
            ExceedanceAlertActive=now-alertSeenAt<AlertClearSeconds;
        }
        /// <summary>A radar drawer opened (or panel content grew) while it is inspected: re-frame once if its visible bounds grew by more than 3 deg.</summary>
        private void ReframeGrownPanel()
        {
            if(InspectedPanelId==null||Time.unscaledTime<nextReframe)return;
            nextReframe=Time.unscaledTime+.5f;
            var panel=GetPanel(InspectedPanelId);
            if(panel==null||CockpitFrame==null||desktopView==null||desktopView.IsViewTransitioning)return;
            if(!panel.TryVisibleAngularBounds(CockpitFrame,out var yaw,out var elevation))return;
            if(yaw.y-yaw.x>inspectedSpan.x+3f||elevation.y-elevation.x>inspectedSpan.y+3f)
            {if(panel.IsUtility)InspectUtility(InspectedPanelId);else InspectPanel(InspectedPanelId);}
        }

        /// <summary>The single writer of FaaHudInspection: the inspected panel, else the off-axis state, else forward.</summary>
        private void ApplyHudInspection()
        {
            string desired=InspectedPanelId??(Initialized&&ViewOffAxis?OffAxisInspectionId:null);
            if(ExceedanceAlertActive)desired=null; // alerts are never dimmed
            if(desired!=null){if(!FaaHudInspection.Active||FaaHudInspection.TargetId!=desired)FaaHudInspection.Begin(desired);}
            else if(FaaHudInspection.Active)FaaHudInspection.End();
        }

        /// <summary>Focus target of a panel (pure, for tests): 1 when nothing or this panel is inspected, else the neighbour alpha or 0 when hidden.</summary>
        public static float PanelFocusTarget(bool anyInspected,bool inspected,bool hidden)=>!anyInspected||inspected?1f:hidden?0f:NeighbourPanelAlpha;
        /// <summary>
        /// A neighbour is hidden (pure, for tests) when its screen rectangle is unknown, is not fully on screen, or meets a protected
        /// area or the inspected panel: panel text never interleaves with HUD or panel text.
        /// </summary>
        public static bool NeighbourShouldHide(bool projected,Rect rect,float width,float height,IReadOnlyList<Rect> protectedRects,bool hasInspected,Rect inspected)
        {
            if(!projected||rect.xMin<0f||rect.yMin<0f||rect.xMax>width||rect.yMax>height)return true;
            if(hasInspected&&rect.Overlaps(inspected))return true;
            if(protectedRects!=null)for(int i=0;i<protectedRects.Count;i++)if(rect.Overlaps(protectedRects[i]))return true;
            return false;
        }

        private void UpdatePanelFocus(float dt)
        {
            float step=Mathf.Max(0f,dt)/Mathf.Max(.01f,FaaHudStyle.FadeSeconds);
            string inspected=InspectedPanelId;
            if(inspected==null)inspectedScreenRectValid=false;
            else if(View!=null&&Time.unscaledTime>=nextNeighbourCheck){nextNeighbourCheck=Time.unscaledTime+.1f;EvaluateNeighbours(inspected);}
            foreach(var p in utilityPanels)p.StepFocus(PanelFocusTarget(inspected!=null,p.Id==inspected,p.NeighbourHidden),step);
            foreach(var p in panels)p.StepFocus(PanelFocusTarget(inspected!=null,p.Id==inspected,p.NeighbourHidden),step);
        }
        private void EvaluateNeighbours(string inspected)
        {
            float w=View.pixelWidth,h=View.pixelHeight;
            CollectProtectedRects(w,h);
            var target=GetPanel(inspected);
            Rect targetRect=default;
            bool hasTarget=target!=null&&target.TryScreenRect(View,out targetRect);
            inspectedScreenRect=targetRect;inspectedScreenRectValid=hasTarget;
            foreach(var p in utilityPanels)EvaluateNeighbour(p,inspected,w,h,hasTarget,targetRect);
            foreach(var p in panels)EvaluateNeighbour(p,inspected,w,h,hasTarget,targetRect);
        }
        private void EvaluateNeighbour(FaaSpatialRadarPanel p,string inspected,float w,float h,bool hasTarget,Rect targetRect)
        {
            if(p.Id==inspected){p.NeighbourHidden=false;return;}
            bool projected=p.TryScreenRect(View,out var r);
            p.NeighbourHidden=NeighbourShouldHide(projected,r,w,h,framingKeepOuts,hasTarget,targetRect);
        }
        /// <summary>Protected screen areas for framing and neighbour checks: registered keep-outs (awareness readouts, chrome), or the zone plan when none is registered yet.</summary>
        private void CollectProtectedRects(float width,float height)
        {
            framingKeepOuts.Clear();int symbology=0;
            var regions=FaaHudKeepOut.Regions;
            for(int i=0;i<regions.Count;i++)
            {
                var r=regions[i];
                if(r==null||r is Object o&&o==null||r.Kind==FaaKeepOutKind.AttitudeWindow||r.Id!=null&&FramingIgnoredKeepOuts.Contains(r.Id))continue;
                if(!r.TryGetScreenRect(out var rect)||rect.width<=0f||rect.height<=0f)continue;
                framingKeepOuts.Add(rect);if(r.Kind==FaaKeepOutKind.Symbology)symbology++;
            }
            if(symbology==0)FaaInspectionFraming.DefaultKeepOuts(width,height,framingKeepOuts);
        }

        /// <summary>Closing a panel that is in view returns the view forward.</summary>
        public void CloseInspected(string id){if(id!=null&&InspectedPanelId==id)ReturnToForwardView();}

        /// <summary>
        /// Framing for a panel: its visible angular bounds (else its layout pose with a nominal size), its smallest text, and the
        /// protected HUD areas. Runs once per inspection.
        /// </summary>
        private FaaInspectionFrame InspectionFrame(FaaSpatialRadarPanel panel)
        {
            float fov=desktopView!=null?desktopView.BaseFieldOfView:View!=null?View.fieldOfView:FaaInspectionFraming.DefaultVerticalFov;
            float w=View!=null?View.pixelWidth:1920f,h=View!=null?View.pixelHeight:1080f;
            Vector2 yaw,elevation;
            if(CockpitFrame==null||!panel.TryVisibleAngularBounds(CockpitFrame,out yaw,out elevation))
            {
                float half=panel.IsUtility?10f:6f;
                yaw=new Vector2(panel.Layout.yaw-half,panel.Layout.yaw+half);elevation=new Vector2(panel.Layout.elevation-half,panel.Layout.elevation+half);
            }
            inspectedSpan=new Vector2(yaw.y-yaw.x,elevation.y-elevation.x);
            float text=View!=null?panel.SmallestTextDegrees(View.transform.position):0f;
            CollectProtectedRects(w,h);
            var frame=FaaInspectionFraming.Fit(yaw,elevation,text,fov,w,h,framingKeepOuts,InspectionZoomAllowed);
            LastInspectionFrame=frame;
            return frame;
        }
        /// <summary>Turns (and on desktop zooms) the camera to a panel. Returns true when the camera accepted the inspection.</summary>
        private bool TurnToPanel(FaaSpatialRadarPanel panel)
        {
            var camera=View!=null?View.GetComponent<AircraftControl.Camera.AircraftCameraController>():null;
            if(camera==null)return false;
            var frame=InspectionFrame(panel);
            if(!camera.BeginPanelInspection(frame.Aim.x,frame.Aim.y))return false;
            if(InspectionZoomAllowed&&frame.VerticalFov<camera.BaseFieldOfView-.05f)camera.SetInspectionFieldOfView(frame.VerticalFov);
            return true;
        }
    }
}
