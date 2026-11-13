using UnityEngine;
using UnityEngine.UI;
using static FAA.Customization.FaaAnalogVector;
namespace FAA.Customization
{
    /// <summary>
    /// One clock for every Classic flash/box timer, so all flashing elements stay synchronised (same phase as FaaHudStyle.BlinkOn).
    /// <see cref="Override"/> exists only so EditMode tests can step time; leave it NaN at runtime.
    /// </summary>
    public static class FaaClassicClock
    {
        public static float Override=float.NaN;
        public static float Now=>float.IsNaN(Override)?Time.unscaledTime:Override;
        public static bool BlinkOn=>Mathf.Repeat(Now*FaaHudStyle.BlinkHz,1f)<.6f;
        /// <summary>Flashes for FaaHudStyle.BlinkSeconds after <paramref name="onset"/>, then steady.</summary>
        public static bool BlinkVisible(float onset)=>Now-onset>=FaaHudStyle.BlinkSeconds||BlinkOn;
    }

    /// <summary>Vector annunciation boxes (mode-change box, AP OFF box, flag boxes). Rebuilds its mesh only when a box changes.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FaaClassicBoxGraphic:MaskableGraphic
    {
        private struct Box { public bool on, doubled; public Rect rect; public Color color; }
        private Box[] boxes=new Box[0];
        public int Count=>boxes.Length;
        public void Allocate(int count){if(boxes.Length!=count){boxes=new Box[Mathf.Max(0,count)];SetVerticesDirty();}raycastTarget=false;}
        public bool IsOn(int index)=>index>=0&&index<boxes.Length&&boxes[index].on;
        public Rect RectOf(int index)=>index>=0&&index<boxes.Length?boxes[index].rect:default;
        public void Set(int index,bool on,Rect rect,Color tint,bool doubled=false)
        {
            if(index<0||index>=boxes.Length)return;
            var b=boxes[index];
            if(b.on==on&&(!on||b.rect==rect&&b.color==tint&&b.doubled==doubled))return;
            boxes[index]=new Box{on=on,rect=rect,color=tint,doubled=doubled};SetVerticesDirty();
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            foreach(var b in boxes)
            {
                if(!b.on)continue;
                RoundedBox(vh,b.rect,5,b.color);
                if(b.doubled)RoundedBox(vh,new Rect(b.rect.x-3,b.rect.y-3,b.rect.width+6,b.rect.height+6),7,b.color);
            }
        }
    }
}
