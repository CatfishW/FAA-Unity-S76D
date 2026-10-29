using UnityEngine;

namespace TrafficRadar
{
    /// <summary>Marks the canonical local-layout host of an original canvas during temporary desktop presentation.</summary>
    [DisallowMultipleComponent]
    public sealed class DesktopCanvasLayoutRoot : MonoBehaviour
    {
        private Canvas ownerCanvas;
        public void Configure(Canvas owner) => ownerCanvas=owner;
        private bool Current => ownerCanvas!=null&&gameObject.activeInHierarchy&&transform.parent==ownerCanvas.transform;
        public static RectTransform Resolve(RectTransform child)
        {
            if(child==null)return null;
            Canvas canvas=child.GetComponentInParent<Canvas>();
            for(Transform parent=child.parent;parent!=null;parent=parent.parent)
            {
                var marker=parent.GetComponent<DesktopCanvasLayoutRoot>();
                if(marker!=null&&marker.Current&&marker.ownerCanvas==canvas)return parent as RectTransform;
                if(parent.GetComponent<Canvas>()!=null)break;
            }
            return null;
        }
        /// <summary>Saved fullscreen state must not retain a temporary frame that can be detached or destroyed.</summary>
        public static Transform AuthoredParent(Transform currentParent)
        {
            var marker=currentParent!=null?currentParent.GetComponent<DesktopCanvasLayoutRoot>():null;
            return marker!=null&&marker.ownerCanvas!=null?marker.ownerCanvas.transform:currentParent;
        }
        /// <summary>Restore inside the current frame while inspected, otherwise back to the original canvas.</summary>
        public static Transform CurrentParent(Transform authoredParent)
        {
            Canvas canvas=authoredParent!=null?authoredParent.GetComponent<Canvas>():null;
            if(canvas==null)return authoredParent;
            for(int i=0;i<authoredParent.childCount;i++)
            {
                var marker=authoredParent.GetChild(i).GetComponent<DesktopCanvasLayoutRoot>();
                if(marker!=null&&marker.Current&&marker.ownerCanvas==canvas)return marker.transform;
            }
            return authoredParent;
        }
    }
}
