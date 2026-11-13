using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    /// <summary>Preserve hierarchy inclusion, product order and partial failure results while isolating local storage.</summary>
    public sealed class FaaIndicatorAncestorAccumulationTests
    {
        [TestCase(1, 0, false)]
        [TestCase(6, 3, false)]
        [TestCase(6, -1, false)]
        [TestCase(6, -2, false)]
        [TestCase(128, 127, false)]
        [TestCase(129, 128, false)]
        [TestCase(160, 159, false)]
        [TestCase(160, -2, false)]
        [TestCase(6, 3, true)]
        public void AncestorWalkPreservesOrderedProductAndBoundedPartialResults(int length, int ancestorDepth, bool nullChild)
        {
            var nodes = new Transform[length];
            var steps = new Matrix4x4[length];
            GameObject missing = null;
            try
            {
                Transform parent = null;
                for (int depth = length - 1; depth >= 0; depth--)
                {
                    var node = new GameObject("Ancestor product step " + depth); node.transform.SetParent(parent, false);
                    node.transform.localPosition = new Vector3(.37f + depth * .002f, -.23f + depth * .003f, .19f - depth * .001f);
                    if (parent == null) node.transform.localPosition += new Vector3(20000, 37, -18000);
                    node.transform.localRotation = Quaternion.Euler((depth % 7) * .7f, (depth % 11) * .9f, (depth % 5) * -.8f);
                    node.transform.localScale = new Vector3(1f + depth % 7 * .001f, depth % 3 == 0 ? -.98f : 1.01f, .995f + depth % 5 * .001f);
                    nodes[depth] = node.transform; parent = node.transform;
                    steps[depth] = Matrix4x4.TRS(node.transform.localPosition, node.transform.localRotation, node.transform.localScale);
                }
                Transform ancestor = null;
                if (ancestorDepth >= 0) ancestor = nodes[ancestorDepth];
                else if (ancestorDepth == -2) { missing = new GameObject("Unrelated ancestor"); ancestor = missing.transform; }

                // Record the expected path independently of the method's byref/local storage strategy.
                bool expectedFound = !nullChild && ancestorDepth >= 0 && ancestorDepth < 128;
                int included = nullChild ? 0 : expectedFound ? ancestorDepth : Mathf.Min(length, 128);
                Matrix4x4 expected = Matrix4x4.identity;
                for (int i = 0; i < included; i++) expected = steps[i] * expected;
                if (!nullChild && length > 128)
                {
                    Matrix4x4 entirePath = Matrix4x4.identity;
                    foreach (Matrix4x4 step in steps) entirePath = step * entirePath;
                    Assert.That(Vector3.Distance(expected.GetColumn(3), entirePath.GetColumn(3)), Is.GreaterThan(1000f), "The depth-limit control must distinguish a partial product from the full path.");
                }

                var type = Type.GetType("IndicatorSystem.Core.IndicatorCanvasProjection, Assembly-CSharp", true);
                MethodInfo method = type.GetMethod("ToAncestor", BindingFlags.NonPublic | BindingFlags.Static);
                var sentinel = Matrix4x4.zero; sentinel.m03 = -999f;
                object[] args = { nullChild ? null : nodes[0], ancestor, sentinel };
                bool found = (bool)method.Invoke(null, args); var actual = (Matrix4x4)args[2];
                Assert.That(found, Is.EqualTo(expectedFound));
                for (int i = 0; i < 16; i++)
                    Assert.That(BitConverter.SingleToInt32Bits(actual[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(expected[i])), "Product entry " + i + " must retain the exact ordered float result, including false-return partial products.");
            }
            finally
            {
                if (nodes[length - 1] != null) UnityEngine.Object.DestroyImmediate(nodes[length - 1].gameObject);
                if (missing != null) UnityEngine.Object.DestroyImmediate(missing);
            }
        }
    }
}
