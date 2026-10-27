using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.XR.Management;

namespace FAA.Customization.Tests
{
    /// <summary>Managed-only provider: records lifecycle calls and never creates a native XR subsystem.</summary>
    public sealed class FaaSimulatorOverrideTestLoader : XRLoader
    {
        public readonly List<string> Calls = new List<string>();
        public override bool Initialize() { Calls.Add("Initialize"); return true; }
        public override bool Stop() { Calls.Add("Stop"); return true; }
        public override bool Deinitialize() { Calls.Add("Deinitialize"); return true; }
        public override T GetLoadedSubsystem<T>() => null;
    }

    public sealed class FaaExplicitSimulatorOverrideTests
    {
        private static Type Compatibility => Type.GetType("FAA.Headset.XR3HeadsetCompatibility, Assembly-CSharp", true);
        private static Type Mode => Compatibility.GetNestedType("ActivationMode");
        private static object EnumMode(string mode) => Enum.Parse(Mode, mode);
        private static object Invoke(string name, params object[] args) => Compatibility.GetMethod(name, BindingFlags.Public | BindingFlags.Static).Invoke(null, args);
        private static string Resolve(string configured, bool autoDetect, bool nativeActive, bool editorSimulator,
            bool playerSimulator, bool editor, string[] args) => Invoke("ResolveActivationMode", EnumMode(configured), autoDetect,
                nativeActive, editorSimulator, playerSimulator, editor, args).ToString();

        [TestCase("--xr3-sim", true)]
        [TestCase("-xr3-sim", true)]
        [TestCase("--XR3-SIM", true)]
        [TestCase("--xr3", false)]
        [TestCase("--xr3-vr", false)]
        [TestCase("--xr-simulator", false)]
        [TestCase("--varjo-sim", false)]
        [TestCase("--xr3-sim=false", false)]
        [TestCase("xr3-sim", false)]
        public void OnlyCanonicalExplicitFlagsRequestPerApplicationDesktopOverride(string argument, bool expected) =>
            Assert.That(Invoke("RequestsExplicitSimulator", (object)new[] { argument }), Is.EqualTo(expected));

        [Test]
        public void MissingArgumentsDoNotRequestAnExplicitOverride()
        {
            Assert.That(Invoke("RequestsExplicitSimulator", new object[] { null }), Is.False);
            Assert.That(Invoke("RequestsExplicitSimulator", (object)Array.Empty<string>()), Is.False);
        }

        [TestCase("Auto")]
        [TestCase("VarjoXR3")]
        [TestCase("UnitySimulator")]
        public void ExplicitSimulatorWinsOverDetectedNativeAndSerializedMode(string configured)
        {
            Assert.That(Resolve(configured, true, true, false, false, false, new[] { "--xr3-sim" }), Is.EqualTo("UnitySimulator"));
            Assert.That(Resolve(configured, true, true, false, false, false, new[] { "--xr3", "--xr3-sim" }), Is.EqualTo("UnitySimulator"));
        }

        [TestCase("Auto", true, true, false, false, false, "VarjoXR3")]
        [TestCase("Auto", false, true, false, false, false, "Auto")]
        [TestCase("VarjoXR3", false, false, false, false, false, "VarjoXR3")]
        [TestCase("UnitySimulator", false, false, false, false, false, "UnitySimulator")]
        [TestCase("UnitySimulator", true, true, false, false, false, "VarjoXR3")]
        [TestCase("Auto", false, false, true, false, true, "UnitySimulator")]
        [TestCase("Auto", false, false, true, false, false, "Auto")]
        [TestCase("Auto", false, false, false, true, false, "UnitySimulator")]
        [TestCase("Auto", false, false, false, true, true, "Auto")]
        public void NoOverridePreservesNormalActivationPolicy(string configured, bool autoDetect, bool nativeActive,
            bool editorSimulator, bool playerSimulator, bool editor, string expected) =>
            Assert.That(Resolve(configured, autoDetect, nativeActive, editorSimulator, playerSimulator, editor, Array.Empty<string>()), Is.EqualTo(expected));

        [Test]
        public void NormalExplicitNativeFlagStillSelectsNativeMode()
        {
            Assert.That(Resolve("Auto", false, false, false, false, false, new[] { "--xr3" }), Is.EqualTo("VarjoXR3"));
        }

        [TestCase(true, false, true, true, true, true)]
        [TestCase(false, true, true, true, true, true)]
        [TestCase(false, false, true, true, true, false)]
        [TestCase(true, false, false, true, true, false)]
        [TestCase(true, false, true, false, true, true)]
        [TestCase(true, false, true, true, false, true)]
        [TestCase(true, false, true, false, false, true)]
        public void ExplicitDesktopPointerOverridesSerializedDefaultsWhileNoFlagPolicyIsUnchanged(bool explicitSimulator, bool editor,
            bool simulator, bool overlay, bool preferPointer, bool expected) =>
            Assert.That(Invoke("PreferSimulatorDesktopPointer", explicitSimulator, editor, simulator, overlay, preferPointer), Is.EqualTo(expected));

        [TestCase(true, true, true, true, false)]
        [TestCase(false, true, false, true, false)]
        [TestCase(true, false, true, false, true)]
        [TestCase(false, true, false, false, true)]
        [TestCase(false, false, true, false, true)]
        [TestCase(false, false, false, false, false)]
        public void ExplicitSimulatorUsesDesktopLayoutWithoutChangingLegacyNoFlagDefaults(bool native, bool compatibility,
            bool nativeVarjoMode, bool explicitSimulator, bool expected)
        {
            Assert.That(Invoke("UseNativeLayoutDefaults", native, compatibility, nativeVarjoMode, explicitSimulator), Is.EqualTo(expected));
        }

        [Test]
        public void ExplicitSimulatorCameraIsMonoAndDropsStaleXrProjectionAndViewOverrides()
        {
            var host = new GameObject("Explicit desktop camera fixture", typeof(Camera));
            try
            {
                Camera camera = host.GetComponent<Camera>(); camera.aspect = 1.5f; camera.targetDisplay = 2;
                host.transform.SetPositionAndRotation(new Vector3(1, 2, 3), Quaternion.Euler(10, 20, 0));
                Matrix4x4 normalProjection = camera.projectionMatrix, normalView = camera.worldToCameraMatrix;
                Matrix4x4 asymmetric = normalProjection; asymmetric.m02 += .27f; camera.projectionMatrix = asymmetric;
                camera.worldToCameraMatrix = Matrix4x4.Translate(Vector3.right * 20f) * normalView;
                Invoke("ConfigureOutputCamera", camera, true);
                Assert.That(camera.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.None)); Assert.That(camera.targetDisplay, Is.Zero);
                Assert.That(camera.projectionMatrix, Is.EqualTo(normalProjection)); Assert.That(camera.worldToCameraMatrix, Is.EqualTo(normalView));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void NativeCameraPolicyKeepsItsCalibratedProjectionAndViewMatrices()
        {
            var host = new GameObject("Native camera policy fixture", typeof(Camera));
            try
            {
                Camera camera = host.GetComponent<Camera>(); camera.stereoTargetEye = StereoTargetEyeMask.None;
                Matrix4x4 calibrated = Matrix4x4.Frustum(-.09f, .12f, -.08f, .10f, .1f, 2000f);
                Matrix4x4 view = Matrix4x4.Translate(new Vector3(3, 4, 5)); camera.projectionMatrix = calibrated; camera.worldToCameraMatrix = view;
                Invoke("ConfigureOutputCamera", camera, false);
                Assert.That(camera.stereoTargetEye, Is.EqualTo(StereoTargetEyeMask.Both)); Assert.That(camera.targetDisplay, Is.Zero);
                Assert.That(camera.projectionMatrix, Is.EqualTo(calibrated)); Assert.That(camera.worldToCameraMatrix, Is.EqualTo(view));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void NullAndUninitializedManagersAreNoOps()
        {
            Assert.That(Invoke("StopApplicationXr", new object[] { null }), Is.False);
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            try
            {
                Assert.That(Invoke("StopApplicationXr", manager), Is.False);
                Assert.That(Invoke("StopApplicationXr", manager), Is.False);
                Assert.That(manager.activeLoader, Is.Null); Assert.That(manager.isInitializationComplete, Is.False);
            }
            finally { UnityEngine.Object.DestroyImmediate(manager); }
        }

        [Test]
        public void ActiveManagerStopsThenDeinitializesExactlyOnceAndLeavesOtherManagersAlone()
        {
            var manager = ScriptableObject.CreateInstance<XRManagerSettings>();
            var otherManager = ScriptableObject.CreateInstance<XRManagerSettings>();
            var loader = ScriptableObject.CreateInstance<FaaSimulatorOverrideTestLoader>();
            var otherLoader = ScriptableObject.CreateInstance<FaaSimulatorOverrideTestLoader>();
            try
            {
                Assert.That(manager.TryAddLoader(loader), Is.True); Assert.That(otherManager.TryAddLoader(otherLoader), Is.True);
                manager.InitializeLoaderSync(); otherManager.InitializeLoaderSync();
                Assert.That(manager.activeLoader, Is.SameAs(loader)); Assert.That(otherManager.activeLoader, Is.SameAs(otherLoader));
                loader.Calls.Clear(); otherLoader.Calls.Clear();
                Assert.That(Invoke("StopApplicationXr", manager), Is.True);
                CollectionAssert.AreEqual(new[] { "Stop", "Deinitialize" }, loader.Calls);
                Assert.That(manager.activeLoader, Is.Null); Assert.That(manager.isInitializationComplete, Is.False);
                Assert.That(otherManager.activeLoader, Is.SameAs(otherLoader)); Assert.That(otherLoader.Calls, Is.Empty);
                Assert.That(Invoke("StopApplicationXr", manager), Is.False);
                CollectionAssert.AreEqual(new[] { "Stop", "Deinitialize" }, loader.Calls);
            }
            finally
            {
                if (manager.activeLoader != null) manager.DeinitializeLoader();
                if (otherManager.activeLoader != null) otherManager.DeinitializeLoader();
                UnityEngine.Object.DestroyImmediate(manager); UnityEngine.Object.DestroyImmediate(otherManager);
                UnityEngine.Object.DestroyImmediate(loader); UnityEngine.Object.DestroyImmediate(otherLoader);
            }
        }
    }
}
