using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public sealed class XR3TransformJitAuditTests
    {
        private static Type Audit => Type.GetType("FAA.Headset.XR3TransformJitAudit, Assembly-CSharp", true);
        private static object Call(string name, params object[] arguments) => Audit.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments);

        [TestCase("--xr3-transform-jit-audit", true)]
        [TestCase("--XR3-TRANSFORM-JIT-AUDIT", true)]
        [TestCase("--xr3-transform-jit-audit=false", false)]
        [TestCase("--xr3-transform-jit-audit=true", false)]
        [TestCase("xr3-transform-jit-audit", false)]
        [TestCase("--xr3-ui-audit", false)]
        public void ExactOptInIsRequired(string argument, bool expected) =>
            Assert.That(Call("RequestsAudit", (object)new[] { argument }), Is.EqualTo(expected));

        [TestCase(14.99, 500, false)]
        [TestCase(30, 99, false)]
        [TestCase(15, 100, true)]
        [TestCase(30, 500, true)]
        [TestCase(-1, -1, false)]
        public void BothWarmupTimeAndFramesMustHaveElapsed(double seconds, int frames, bool expected) =>
            Assert.That(Call("CaptureReady", seconds, frames), Is.EqualTo(expected));

        [Test]
        public void DefaultAndUnsupportedRuntimeCreateNoHostOrCapture()
        {
            int before = Resources.FindObjectsOfTypeAll(Audit).Length;
            Assert.That(Call("RequestsAudit", new object[] { null }), Is.False);
            Assert.That(Call("RequestsAudit", (object)Array.Empty<string>()), Is.False);
            Assert.That(Audit.GetProperty("IsSupportedPlayer").GetValue(null), Is.False, "Editor must never read executable code");
            Assert.That(Call("CreateIfRequested", new object[] { null }), Is.Null);
            Assert.That(Call("CreateIfRequested", (object)Array.Empty<string>()), Is.Null);
            Assert.That(Call("CreateIfRequested", (object)new[] { "--xr3-transform-jit-audit" }), Is.Null, "Even explicit opt-in does not bypass the player/platform guard");
            Assert.That(Resources.FindObjectsOfTypeAll(Audit).Length, Is.EqualTo(before));
        }
    }
}
