using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
namespace FAA.Customization.Tests
{
    public sealed class FaaHeadlockedCanvasPrecisionTests
    {
        private static Type Presenter=>Type.GetType("FAA.Headset.XR3MixedRealityPresentation, Assembly-CSharp",true);
        [TestCase(15000f,-18000f)] [TestCase(20000f,-20000f)]
        public void ActualHeadlockedFitKeepsExactLocalPoseWhileAircraftMovesAtFlightCoordinates(float x,float z)
        {
            var eyeHost=new GameObject("Precision MR eye",typeof(Camera));var hudHost=new GameObject("Actual native headlocked canvas",typeof(RectTransform),typeof(Canvas));
            try
            {
                var eye=eyeHost.GetComponent<Camera>();var canvas=hudHost.GetComponent<Canvas>();var rect=(RectTransform)hudHost.transform;var projection=eye.projectionMatrix;
                for(int frame=0;frame<120;frame++)
                {
                    eye.transform.SetPositionAndRotation(new Vector3(x+frame*.003f,3+Mathf.Sin(frame*.1f)*.01f,z+frame*.005f),Quaternion.Euler(7+Mathf.Sin(frame*.03f),27+frame*.13f,2));
                    Presenter.GetMethod("FitHeadlockedCanvas").Invoke(null,new object[]{canvas,eye,null});
                    Assert.That(rect.parent,Is.SameAs(eye.transform));Assert.That(Vector3.Distance(rect.localPosition,new Vector3(0,0,1.5f)),Is.LessThan(.000001f),"Fitting must not round-trip a millimetre-scale UI offset through kilometre world coordinates.");
                    Assert.That(Quaternion.Angle(rect.localRotation,Quaternion.identity),Is.LessThan(.001f));Assert.That(eye.projectionMatrix,Is.EqualTo(projection));
                }
            }
            finally{UnityEngine.Object.DestroyImmediate(hudHost);UnityEngine.Object.DestroyImmediate(eyeHost);}
        }
        [TestCase(2f,.75f,1.3f)] [TestCase(.5f,2f,.8f)] [TestCase(1f,1f,-1f)]
        public void NonUnitCameraScaleRetainsTheSamePhysicalFitDistance(float x,float y,float z)
        {
            var eyeHost=new GameObject("Scaled MR eye",typeof(Camera));var hudHost=new GameObject("Scaled headlocked canvas",typeof(RectTransform),typeof(Canvas));
            try
            {
                var eye=eyeHost.GetComponent<Camera>();eye.transform.localScale=new Vector3(x,y,z);eye.transform.SetPositionAndRotation(new Vector3(20000,3,-20000),Quaternion.Euler(7,27,2));
                var canvas=hudHost.GetComponent<Canvas>();var rect=(RectTransform)hudHost.transform;Presenter.GetMethod("FitHeadlockedCanvas").Invoke(null,new object[]{canvas,eye,null});
                Assert.That(Vector3.Distance(eye.transform.TransformVector(rect.localPosition),eye.transform.forward*1.5f),Is.LessThan(.00001f),"The offset is still 1.5 physical metres, independent of inherited scale.");
                Assert.That(Quaternion.Angle(rect.rotation,eye.transform.rotation),Is.LessThan(.001f));
            }
            finally{UnityEngine.Object.DestroyImmediate(hudHost);UnityEngine.Object.DestroyImmediate(eyeHost);}
        }
    }
}
