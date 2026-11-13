using UnityEngine;
using UnityEngine.UI;
namespace FAA.Customization
{
    public static class FaaAnalogVector
    {
        public static Vector2 Bearing(float degrees,float radius=1f)
        {float a=degrees*Mathf.Deg2Rad;return new Vector2(Mathf.Sin(a),Mathf.Cos(a))*radius;}
        public static void Line(VertexHelper vh,Vector2 a,Vector2 b,float width,Color tint)
        {
            Vector2 delta=b-a;if(delta.sqrMagnitude<.000001f)return;
            Vector2 side=new Vector2(-delta.y,delta.x).normalized*width*.5f;
            int n=vh.currentVertCount;vh.AddVert(a-side,tint,Vector2.zero);vh.AddVert(a+side,tint,Vector2.zero);
            vh.AddVert(b+side,tint,Vector2.zero);vh.AddVert(b-side,tint,Vector2.zero);vh.AddTriangle(n,n+1,n+2);vh.AddTriangle(n,n+2,n+3);
        }
        public static void Triangle(VertexHelper vh,Vector2 a,Vector2 b,Vector2 c,Color tint)
        {int n=vh.currentVertCount;vh.AddVert(a,tint,Vector2.zero);vh.AddVert(b,tint,Vector2.zero);vh.AddVert(c,tint,Vector2.zero);vh.AddTriangle(n,n+1,n+2);}
        public static void Ring(VertexHelper vh,Vector2 center,float radius,float width,Color tint,int segments=96)
        {for(int i=0;i<segments;i++)Line(vh,center+Bearing(i*360f/segments,radius),center+Bearing((i+1)*360f/segments,radius),width,tint);}
        public static void Disc(VertexHelper vh,Vector2 center,float radius,Color tint)
        {for(int i=0;i<24;i++)Triangle(vh,center,center+Bearing(i*15,radius),center+Bearing((i+1)*15,radius),tint);}
        public static void Diamond(VertexHelper vh,Vector2 p,float halfWidth,float halfHeight,Color tint,bool filled=true)
        {
            Vector2 a=p+Vector2.up*halfHeight,b=p+Vector2.right*halfWidth,c=p-Vector2.up*halfHeight,d=p-Vector2.right*halfWidth;
            if(filled){Triangle(vh,a,b,c,tint);Triangle(vh,a,c,d,tint);}else{Line(vh,a,b,1.7f,tint);Line(vh,b,c,1.7f,tint);Line(vh,c,d,1.7f,tint);Line(vh,d,a,1.7f,tint);}
        }
        public static void RoundedBox(VertexHelper vh,Rect box,float radius,Color tint)
        {
            Line(vh,new Vector2(box.xMin+radius,box.yMax),new Vector2(box.xMax-radius,box.yMax),1.8f,tint);
            Line(vh,new Vector2(box.xMin+radius,box.yMin),new Vector2(box.xMax-radius,box.yMin),1.8f,tint);
            Line(vh,new Vector2(box.xMin,box.yMin+radius),new Vector2(box.xMin,box.yMax-radius),1.8f,tint);
            Line(vh,new Vector2(box.xMax,box.yMin+radius),new Vector2(box.xMax,box.yMax-radius),1.8f,tint);
            for(int q=0;q<4;q++)
            {
                Vector2 c=new Vector2(q<2?box.xMax-radius:box.xMin+radius,q==0||q==3?box.yMax-radius:box.yMin+radius);
                for(int i=0;i<8;i++)Line(vh,c+Bearing(q*90+i*11.25f,radius),c+Bearing(q*90+(i+1)*11.25f,radius),1.8f,tint);
            }
        }
        /// <summary>Arc between two bearings (degrees clockwise from up).</summary>
        public static void Arc(VertexHelper vh,Vector2 center,float radius,float fromDegrees,float toDegrees,float width,Color tint,float stepDegrees=3f)
        {
            int n=Mathf.Max(1,Mathf.CeilToInt(Mathf.Abs(toDegrees-fromDegrees)/Mathf.Max(.5f,stepDegrees)));
            for(int i=0;i<n;i++)Line(vh,center+Bearing(Mathf.Lerp(fromDegrees,toDegrees,(float)i/n),radius),center+Bearing(Mathf.Lerp(fromDegrees,toDegrees,(float)(i+1)/n),radius),width,tint);
        }
        public static void HollowTriangle(VertexHelper vh,Vector2 a,Vector2 b,Vector2 c,float width,Color tint)
        {Line(vh,a,b,width,tint);Line(vh,b,c,width,tint);Line(vh,c,a,width,tint);}
        /// <summary>Parameter interval [t0,t1] of segment a-b that lies inside <paramref name="r"/> (Liang-Barsky). False when it misses.</summary>
        public static bool InsideInterval(Rect r,Vector2 a,Vector2 b,out float t0,out float t1)
        {
            t0=0;t1=1;Vector2 d=b-a;
            return Clip(-d.x,a.x-r.xMin,ref t0,ref t1)&&Clip(d.x,r.xMax-a.x,ref t0,ref t1)&&Clip(-d.y,a.y-r.yMin,ref t0,ref t1)&&Clip(d.y,r.yMax-a.y,ref t0,ref t1)&&t1>t0;
        }
        private static bool Clip(float p,float q,ref float t0,ref float t1)
        {
            if(Mathf.Abs(p)<1e-6f)return q>=0;
            float r=q/p;
            if(p<0){if(r>t1)return false;if(r>t0)t0=r;}else{if(r<t0)return false;if(r<t1)t1=r;}
            return true;
        }
        /// <summary>Draws only the parts of segment a-b outside <paramref name="exclude"/>, so a needle never strokes through a readout window.</summary>
        public static void LineOutside(VertexHelper vh,Vector2 a,Vector2 b,Rect exclude,float width,Color tint)
        {
            float pad=width*.5f;Rect r=Rect.MinMaxRect(exclude.xMin-pad,exclude.yMin-pad,exclude.xMax+pad,exclude.yMax+pad);
            if(!InsideInterval(r,a,b,out float t0,out float t1)){Line(vh,a,b,width,tint);return;}
            Vector2 d=b-a;
            if(t0>0)Line(vh,a,a+d*t0,width,tint);
            if(t1<1)Line(vh,a+d*t1,b,width,tint);
        }
    }
}
