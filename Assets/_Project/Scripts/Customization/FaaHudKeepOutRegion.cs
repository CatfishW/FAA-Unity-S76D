using System.Collections.Generic;
using UnityEngine;

namespace FAA.Customization
{
    /// <summary>
    /// Registers a RectTransform as a protected area while it is active and visible (CanvasGroup alpha above ~0).
    /// Add it to the root of any head-fixed instrument or chrome element.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FaaHudKeepOutRegion : MonoBehaviour, FaaHudKeepOut.IRegion
    {
        [SerializeField] private string id="";
        [SerializeField] private FaaKeepOutKind kind=FaaKeepOutKind.Symbology;
        [Tooltip("Extra margin around the rectangle, in reference-canvas units.")]
        [SerializeField] private float padding=6f;
        [SerializeField] private RectTransform target;
        private readonly Vector3[] corners=new Vector3[4];
        private readonly List<CanvasGroup> groups=new();
        private Canvas canvas;

        public string Id=>string.IsNullOrEmpty(id)?name:id;
        public FaaKeepOutKind Kind=>kind;
        public RectTransform Target=>target!=null?target:transform as RectTransform;

        public FaaHudKeepOutRegion Configure(string regionId,FaaKeepOutKind regionKind,float regionPadding=6f,RectTransform rect=null)
        {id=regionId;kind=regionKind;padding=regionPadding;if(rect!=null)target=rect;RefreshParents();return this;}

        /// <summary>Adds (or reuses) a region on <paramref name="go"/>.</summary>
        public static FaaHudKeepOutRegion Ensure(GameObject go,string regionId,FaaKeepOutKind regionKind,float regionPadding=6f)
        {
            if(go==null)return null;
            var r=go.GetComponent<FaaHudKeepOutRegion>();if(r==null)r=go.AddComponent<FaaHudKeepOutRegion>();
            return r.Configure(regionId,regionKind,regionPadding);
        }

        private void OnEnable(){RefreshParents();FaaHudKeepOut.Register(this);}
        private void OnDisable()=>FaaHudKeepOut.Unregister(this);
        private void OnTransformParentChanged()=>RefreshParents();
        private void RefreshParents(){groups.Clear();GetComponentsInParent(true,groups);canvas=GetComponentInParent<Canvas>();}

        public bool TryGetScreenRect(out Rect rect)
        {
            rect=default;
            var rt=Target;
            if(!isActiveAndEnabled||rt==null)return false;
            float alpha=1;foreach(var g in groups){if(g==null||!g.isActiveAndEnabled)continue;alpha*=g.alpha;if(g.ignoreParentGroups)break;}
            if(alpha<.05f)return false;
            if(canvas==null)canvas=GetComponentInParent<Canvas>();
            if(canvas==null||!canvas.isActiveAndEnabled)return false;
            var root=canvas.rootCanvas;
            rt.GetWorldCorners(corners);
            Camera cam=root.renderMode==RenderMode.ScreenSpaceOverlay?null:(root.worldCamera!=null?root.worldCamera:Camera.main);
            float xMin=float.MaxValue,yMin=float.MaxValue,xMax=float.MinValue,yMax=float.MinValue;
            for(int i=0;i<4;i++)
            {
                Vector2 p=cam==null?(Vector2)corners[i]:RectTransformUtility.WorldToScreenPoint(cam,corners[i]);
                if(cam!=null&&cam.WorldToViewportPoint(corners[i]).z<0)return false;
                xMin=Mathf.Min(xMin,p.x);yMin=Mathf.Min(yMin,p.y);xMax=Mathf.Max(xMax,p.x);yMax=Mathf.Max(yMax,p.y);
            }
            float pad=padding*Mathf.Max(.01f,root.scaleFactor);
            rect=Rect.MinMaxRect(xMin-pad,yMin-pad,xMax+pad,yMax+pad);
            return rect.width>0&&rect.height>0;
        }
    }
}
