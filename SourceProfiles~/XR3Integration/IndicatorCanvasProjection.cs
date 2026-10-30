using UnityEngine;

namespace IndicatorSystem.Core
{
    /// <summary>HUD projection in camera-relative coordinates, before small UI units encounter a large world origin.</summary>
    public static class IndicatorCanvasProjection
    {
        private static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
        private static bool ToAncestor(Transform child,Transform ancestor,out Matrix4x4 matrix)
        {
            matrix=Matrix4x4.identity;
            for(int depth=0;child!=null&&depth<128;depth++,child=child.parent)
            {
                if(child==ancestor)return true;
                matrix=Matrix4x4.TRS(child.localPosition,child.localRotation,child.localScale)*matrix;
            }
            return false;
        }
        private static bool TryRelative(Transform reference,Transform view,out Matrix4x4 matrix)
        {
            matrix=Matrix4x4.identity;
            for(Transform common=reference;common!=null;common=common.parent)
            {
                if(!ToAncestor(view,common,out var cameraToCommon))continue;
                if(!ToAncestor(reference,common,out var referenceToCommon))return false;
                matrix=cameraToCommon.inverse*referenceToCommon;return true;
            }
            return false;
        }
        private static bool TryViewMatrix(Transform reference,Camera view,out Matrix4x4 matrix,out bool originalProjection)
        {
            matrix=Matrix4x4.identity;originalProjection=false;
            if(reference==null||view==null)return false;
            if(AircraftControl.Camera.AircraftViewRenderSpace.TryGetNearViewMatrix(reference,view,out matrix))return true;
            if(TryRelative(reference,view.transform,out var relative))
            {
                // Use the camera's actual view rotation/handedness and ignore only the cancelled world translation.
                var rotation=view.worldToCameraMatrix;rotation.SetColumn(3,new Vector4(0,0,0,1));
                var cameraLinear=view.transform.localToWorldMatrix;cameraLinear.SetColumn(3,new Vector4(0,0,0,1));
                matrix=rotation*cameraLinear*relative;
                // A runtime/custom view can have a calibrated eye offset relative to the tracked Transform.
                // Keep that offset; only the shared kilometre-scale origin was removed above.
                var actual=view.worldToCameraMatrix;
                var nominal=Matrix4x4.Scale(new Vector3(1,1,-1))*view.transform.worldToLocalMatrix;
                bool sameBasis=true;
                for(int row=0;row<3;row++)for(int column=0;column<3;column++)
                    if(actual[row,column]!=nominal[row,column])sameBasis=false;
                if(sameBasis)
                {
                    Vector4 origin=matrix.GetColumn(3);
                    origin.x+=actual.m03-nominal.m03;origin.y+=actual.m13-nominal.m13;origin.z+=actual.m23-nominal.m23;
                    matrix.SetColumn(3,origin);
                }
                else
                {
                    // A custom rotated/scaled view has no transform-relative nominal basis. Preserve its actual origin.
                    originalProjection=true;
                    Vector3 origin=actual.MultiplyPoint3x4(reference.position);
                    matrix.SetColumn(3,new Vector4(origin.x,origin.y,origin.z,1));
                }
                return true;
            }
            originalProjection=true;
            // Independent roots have no small shared origin. Directions still retain their small local-unit precision;
            // measure only the origin through world coordinates, rather than subtracting two almost equal projected points.
            Vector3 p=view.worldToCameraMatrix.MultiplyPoint3x4(reference.position);
            Vector3 x=view.worldToCameraMatrix.MultiplyVector(reference.TransformVector(Vector3.right));
            Vector3 y=view.worldToCameraMatrix.MultiplyVector(reference.TransformVector(Vector3.up));
            Vector3 z=view.worldToCameraMatrix.MultiplyVector(reference.TransformVector(Vector3.forward));
            matrix.SetColumn(0,new Vector4(x.x,x.y,x.z,0));matrix.SetColumn(1,new Vector4(y.x,y.y,y.z,0));
            matrix.SetColumn(2,new Vector4(z.x,z.y,z.z,0));matrix.SetColumn(3,new Vector4(p.x,p.y,p.z,1));return true;
        }
        public static bool TryProjectLocal(Transform reference,Vector3 localPoint,Camera view,out Vector3 screen)
        {
            screen=default;if(!TryViewMatrix(reference,view,out var relative,out bool originalProjection))return false;
            if(originalProjection)
            {
                screen=view.WorldToScreenPoint(reference.TransformPoint(localPoint));
                return Finite(screen.x)&&Finite(screen.y)&&Finite(screen.z);
            }
            Vector3 eye=relative.MultiplyPoint3x4(localPoint);Vector4 clip=view.projectionMatrix*new Vector4(eye.x,eye.y,eye.z,1);
            if(!Finite(clip.w)||Mathf.Abs(clip.w)<1e-8f)return false;
            Rect pixels=view.pixelRect;
            screen=new Vector3(pixels.x+(clip.x/clip.w*.5f+.5f)*pixels.width,
                pixels.y+(clip.y/clip.w*.5f+.5f)*pixels.height,-eye.z);
            return Finite(screen.x)&&Finite(screen.y)&&Finite(screen.z);
        }
        /// <summary>One per-call projection capture for all four corners; no cross-frame geometry cache.</summary>
        public static bool TryProjectLocalBounds(Transform reference,Rect localRect,Camera view,out Rect bounds,bool rejectBehind=false)
        {
            bounds=default;
            if(!TryViewMatrix(reference,view,out var relative,out bool originalProjection))return false;
            Matrix4x4 projection=originalProjection?Matrix4x4.identity:view.projectionMatrix;
            Rect pixels=originalProjection?default:view.pixelRect;
            Vector2 minimum=new Vector2(float.PositiveInfinity,float.PositiveInfinity),maximum=-minimum;
            for(int i=0;i<4;i++)
            {
                Vector3 local=new Vector3(i<2?localRect.xMin:localRect.xMax,(i==0||i==3)?localRect.yMin:localRect.yMax,0);
                Vector3 screen;
                if(originalProjection)
                {
                    // Retain the exact engine fallback used by single-point projection for
                    // independent roots/custom legacy views; only the expensive helper lookup is shared.
                    screen=view.WorldToScreenPoint(reference.TransformPoint(local));
                }
                else
                {
                    Vector3 eye=relative.MultiplyPoint3x4(local);
                    // Scalar projection avoids passing additional Matrix4x4 arguments by value.
                    float x=projection.m00*eye.x+projection.m01*eye.y+projection.m02*eye.z+projection.m03;
                    float y=projection.m10*eye.x+projection.m11*eye.y+projection.m12*eye.z+projection.m13;
                    float w=projection.m30*eye.x+projection.m31*eye.y+projection.m32*eye.z+projection.m33;
                    if(!Finite(w)||Mathf.Abs(w)<1e-8f)return false;
                    screen=new Vector3(pixels.x+(x/w*.5f+.5f)*pixels.width,pixels.y+(y/w*.5f+.5f)*pixels.height,-eye.z);
                }
                if(!Finite(screen.x)||!Finite(screen.y)||!Finite(screen.z)||(rejectBehind&&screen.z<0f))return false;
                minimum=Vector2.Min(minimum,screen);maximum=Vector2.Max(maximum,screen);
            }
            bounds=Rect.MinMaxRect(minimum.x,minimum.y,maximum.x,maximum.y);return true;
        }
        public static bool TryPixelsPerReferenceUnit(Transform reference,Camera view,out float pixels)
        {
            pixels=0;if(!TryViewMatrix(reference,view,out var relative,out _))return false;
            var projection=view.projectionMatrix*relative;
            Vector4 origin=projection.GetColumn(3),x=projection.GetColumn(0),y=projection.GetColumn(1);
            if(!Finite(origin.w)||Mathf.Abs(origin.w)<1e-8f||relative.m23>=0)return false;
            float inverse=1f/(origin.w*origin.w);Rect viewport=view.pixelRect;
            Vector2 dx=new Vector2((x.x*origin.w-origin.x*x.w)*inverse*viewport.width*.5f,
                (x.y*origin.w-origin.y*x.w)*inverse*viewport.height*.5f);
            Vector2 dy=new Vector2((y.x*origin.w-origin.x*y.w)*inverse*viewport.width*.5f,
                (y.y*origin.w-origin.y*y.w)*inverse*viewport.height*.5f);
            pixels=Mathf.Max(dx.magnitude,dy.magnitude);return Finite(pixels)&&pixels>.001f;
        }
        public static bool TryScreenToLocal(RectTransform reference,Vector2 screen,Camera view,out Vector2 local)
        {
            local=default;if(!TryViewMatrix(reference,view,out var relative,out bool originalProjection)||originalProjection)return false;
            Rect viewport=view.pixelRect;if(viewport.width<=0||viewport.height<=0)return false;
            float x=(screen.x-viewport.x)/viewport.width*2f-1f,y=(screen.y-viewport.y)/viewport.height*2f-1f;
            Matrix4x4 inverseProjection=view.projectionMatrix.inverse,inverseReference=relative.inverse;
            Vector4 a=inverseProjection*new Vector4(x,y,-1,1),b=inverseProjection*new Vector4(x,y,0,1);
            if(Mathf.Abs(a.w)<1e-8f||Mathf.Abs(b.w)<1e-8f)return false;
            Vector3 first=inverseReference.MultiplyPoint3x4(new Vector3(a.x/a.w,a.y/a.w,a.z/a.w));
            Vector3 second=inverseReference.MultiplyPoint3x4(new Vector3(b.x/b.w,b.y/b.w,b.z/b.w));
            Vector3 direction=second-first;if(Mathf.Abs(direction.z)<1e-8f)return false;
            float t=-first.z/direction.z;if(t<0||!Finite(t))return false;
            Vector3 hit=first+direction*t;local=new Vector2(hit.x,hit.y);return Finite(local.x)&&Finite(local.y);
        }
    }
}
