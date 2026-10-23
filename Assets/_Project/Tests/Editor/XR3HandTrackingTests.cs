using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class XR3HandTrackingTests
    {
        private static Type Tracker => Type.GetType("FAA.Headset.XR3HandTracking, Assembly-CSharp", true);
        private static object Frame(bool both = true)
        {
            var type = Type.GetType("Leap.Frame, Ultraleap.Tracking.Core", true);
            object frame = Activator.CreateInstance(type, new object[] { 1u });
            type.GetField("Timestamp").SetValue(frame, 123L);
            var hands = (IList)type.GetField("Hands").GetValue(frame);
            foreach (bool left in both ? new[] { true, true, false } : new[] { true })
            {
                var handType = Type.GetType("Leap.Hand, Ultraleap.Tracking.Core", true);
                object hand = Activator.CreateInstance(handType);
                handType.GetField("IsLeft").SetValue(hand, left);
                handType.GetField("PalmPosition").SetValue(hand, new Vector3(.1f, .2f, .4f));
                hands.Add(hand);
            }
            return frame;
        }

        private static object[] Detect(object frame, bool connected, float age)
        {
            object[] args = { frame, connected, age, false, false };
            int count = (int)Tracker.GetMethod("CountFreshHands", BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
            return new[] { (object)count, args[3], args[4] };
        }

        [Test]
        public void FreshLiveFramesRecognizeBothHandsAndIgnoreDuplicateChirality()
        {
            Assert.That(Detect(Frame(), true, .01f), Is.EqualTo(new object[] { 2, true, true }));
            Assert.That(Detect(Frame(false), true, .1f), Is.EqualTo(new object[] { 1, true, false }));
        }

        [TestCase(false, 0f)]
        [TestCase(true, .21f)]
        [TestCase(true, -1f)]
        [TestCase(true, float.NaN)]
        [TestCase(true, float.PositiveInfinity)]
        public void LostConnectionAndStaleOrInvalidFramesNeverReportTrackedHands(bool connected, float age)
        {
            Assert.That(Detect(Frame(), connected, age), Is.EqualTo(new object[] { 0, false, false }));
        }

        [Test]
        public void MissingTimestampOrInvalidPalmNeverCountsAsRecognition()
        {
            Assert.That(Detect(null, true, 0), Is.EqualTo(new object[] { 0, false, false }));
            object frame = Frame(false);
            frame.GetType().GetField("Timestamp").SetValue(frame, 0L);
            Assert.That(Detect(frame, true, 0), Is.EqualTo(new object[] { 0, false, false }));
            frame.GetType().GetField("Timestamp").SetValue(frame, 123L);
            var hands = (IList)frame.GetType().GetField("Hands").GetValue(frame);
            hands[0].GetType().GetField("PalmPosition").SetValue(hands[0], new Vector3(float.NaN, 0, 0));
            Assert.That(Detect(frame, true, 0), Is.EqualTo(new object[] { 0, false, false }));
        }
    }
}
