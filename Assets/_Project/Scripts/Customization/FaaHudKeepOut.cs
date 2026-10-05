using System.Collections.Generic;
using UnityEngine;

namespace FAA.Customization
{
    public enum FaaKeepOutKind
    {
        /// <summary>A head-fixed flight instrument (readout, tape, FMA, heading scale) that conformal cues must not cross.</summary>
        Symbology=0,
        /// <summary>Screen chrome (status chip, control bar, brief) that conformal cues must not cross.</summary>
        Chrome=1,
        /// <summary>The central attitude field. The conformal pitch ladder is confined to this rectangle.</summary>
        AttitudeWindow=2,
    }

    /// <summary>
    /// Registry of protected screen areas (AC 25-11B: symbols must not overlap or obscure each other; protect critical areas).
    /// Head-fixed modules register their rectangles here. The conformal layer clips its ladder to the attitude window
    /// and breaks the horizon line around Symbology/Chrome rectangles. All rectangles are Unity screen pixels with origin at bottom-left.
    /// </summary>
    public static class FaaHudKeepOut
    {
        public interface IRegion
        {
            string Id {get;}
            FaaKeepOutKind Kind {get;}
            bool TryGetScreenRect(out Rect rect);
        }

        private static readonly List<IRegion> regions=new();
        public static IReadOnlyList<IRegion> Regions=>regions;

        public static void Register(IRegion region){if(region!=null&&!regions.Contains(region))regions.Add(region);}
        public static void Unregister(IRegion region){if(region!=null)regions.Remove(region);}

        /// <summary>Default attitude field as a viewport rectangle, used when no module registers an AttitudeWindow.
        /// It sits between the IAS and ALT columns, below the bank scale and above the heading scale. Its top (0.76 of the screen
        /// height, 346 px from the top at 1440p) stays below the roll-scale end marks (about 290-310 px), so no rung or numeral
        /// crosses them (review M7); the conformal layer may extend it downward and sideways toward the aircraft reference (then
        /// clears every protected rectangle), never upward.</summary>
        public static Rect DefaultAttitudeViewport=new Rect(.37f,.33f,.26f,.43f);

        /// <summary>Collects visible protected rectangles (excluding attitude windows) into <paramref name="into"/>. Returns the count added.</summary>
        public static int Collect(List<Rect> into,bool includeChrome=true)
        {
            if(into==null)return 0;int added=0;
            for(int i=regions.Count-1;i>=0;i--)
            {
                var r=regions[i];
                if(r==null||(r is Object o&&o==null)){regions.RemoveAt(i);continue;}
                if(r.Kind==FaaKeepOutKind.AttitudeWindow||(!includeChrome&&r.Kind==FaaKeepOutKind.Chrome))continue;
                if(r.TryGetScreenRect(out var rect)&&rect.width>0&&rect.height>0){into.Add(rect);added++;}
            }
            return added;
        }

        /// <summary>The attitude window in screen pixels: a registered AttitudeWindow region if visible, else the default viewport rectangle.</summary>
        public static Rect AttitudeWindow(float screenWidth,float screenHeight)
        {
            for(int i=regions.Count-1;i>=0;i--)
            {
                var r=regions[i];
                if(r==null||(r is Object o&&o==null)){regions.RemoveAt(i);continue;}
                if(r.Kind==FaaKeepOutKind.AttitudeWindow&&r.TryGetScreenRect(out var rect)&&rect.width>1&&rect.height>1)return rect;
            }
            var v=DefaultAttitudeViewport;
            return new Rect(v.x*screenWidth,v.y*screenHeight,v.width*screenWidth,v.height*screenHeight);
        }

        public static bool Contains(Vector2 screenPoint,float padding=0,bool includeChrome=true)
        {
            for(int i=regions.Count-1;i>=0;i--)
            {
                var r=regions[i];
                if(r==null||(r is Object o&&o==null)){regions.RemoveAt(i);continue;}
                if(r.Kind==FaaKeepOutKind.AttitudeWindow||(!includeChrome&&r.Kind==FaaKeepOutKind.Chrome))continue;
                if(!r.TryGetScreenRect(out var rect))continue;
                rect.xMin-=padding;rect.yMin-=padding;rect.xMax+=padding;rect.yMax+=padding;
                if(rect.Contains(screenPoint))return true;
            }
            return false;
        }

        /// <summary>
        /// Clips the segment a-b against a rectangle (Liang-Barsky). Returns false when no part of it lies inside.
        /// </summary>
        public static bool ClipSegment(Rect rect,ref Vector2 a,ref Vector2 b)
        {
            float t0=0,t1=1;Vector2 d=b-a;
            if(!Clip(-d.x,a.x-rect.xMin,ref t0,ref t1))return false;
            if(!Clip(d.x,rect.xMax-a.x,ref t0,ref t1))return false;
            if(!Clip(-d.y,a.y-rect.yMin,ref t0,ref t1))return false;
            if(!Clip(d.y,rect.yMax-a.y,ref t0,ref t1))return false;
            Vector2 na=a+d*t0,nb=a+d*t1;a=na;b=nb;return true;
        }
        private static bool Clip(float p,float q,ref float t0,ref float t1)
        {
            if(Mathf.Approximately(p,0))return q>=0;
            float r=q/p;
            if(p<0){if(r>t1)return false;if(r>t0)t0=r;}
            else{if(r<t0)return false;if(r<t1)t1=r;}
            return true;
        }
    }
}
