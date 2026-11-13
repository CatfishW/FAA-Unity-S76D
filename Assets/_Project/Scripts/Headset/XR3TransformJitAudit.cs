using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;

namespace FAA.Headset
{
    /// <summary>Opt-in, one-shot evidence for the already-used transform helper's Mono entry point. No control endpoint.</summary>
    [UnityEngine.Scripting.Preserve]
    public sealed class XR3TransformJitAudit : MonoBehaviour
    {
        public const string Argument = "--xr3-transform-jit-audit";
        public const int MaximumCodeBytes = 256, MinimumFrames = 100;
        public const double MinimumSeconds = 15;
        private bool requested;
        private double startedAt;
        private int startedFrame;

        public static bool RequestsAudit(string[] arguments) => arguments != null && Array.Exists(arguments,
            argument => string.Equals(argument, Argument, StringComparison.OrdinalIgnoreCase));

        public static bool IsSupportedPlayer
        {
            get
            {
#if UNITY_STANDALONE_WIN && ENABLE_MONO && !UNITY_EDITOR && !ENABLE_IL2CPP
                return true;
#else
                return false;
#endif
            }
        }

        public static bool CaptureReady(double elapsedSeconds, int elapsedFrames) =>
            elapsedSeconds >= MinimumSeconds && elapsedFrames >= MinimumFrames;

        public static XR3TransformJitAudit CreateIfRequested(string[] arguments)
        {
            if (!IsSupportedPlayer || !RequestsAudit(arguments)) return null;
            var existing = FindAnyObjectByType<XR3TransformJitAudit>();
            if (existing != null) return existing;
            var host = new GameObject("FAA Transform JIT Audit");
            DontDestroyOnLoad(host);
            var audit = host.AddComponent<XR3TransformJitAudit>();
            audit.startedAt = Time.realtimeSinceStartupAsDouble; audit.startedFrame = Time.frameCount;
            audit.requested = true;
            return audit;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (IsSupportedPlayer) CreateIfRequested(Environment.GetCommandLineArgs());
        }

        private void Update()
        {
            if (!requested || !IsSupportedPlayer) { enabled = false; return; }
            if (!CaptureReady(Time.realtimeSinceStartupAsDouble - startedAt, Time.frameCount - startedFrame)) return;
            requested = false; enabled = false;
            try { CaptureEntry(); }
            finally { Destroy(gameObject); }
        }

#if UNITY_STANDALONE_WIN && ENABLE_MONO && !UNITY_EDITOR && !ENABLE_IL2CPP
        [Serializable]
        private sealed class Snapshot
        {
            public string utc, assembly, moduleMvid, method, metadataToken, implementationFlags, entryAddress;
            public string binaryFile, codeSha256, codeHex, error;
            public int processId, pointerSize, frameCount, elapsedFrames, implementationFlagValue, requestedBytes, capturedBytes, pageSize;
            public double elapsedSeconds;
            public bool noInlining, noOptimization, windowsMonoPlayer, followedJump;
        }
#endif

        private void CaptureEntry()
        {
#if UNITY_STANDALONE_WIN && ENABLE_MONO && !UNITY_EDITOR && !ENABLE_IL2CPP
            var snapshot = new Snapshot {
                utc = DateTime.UtcNow.ToString("O"), processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                pointerSize = IntPtr.Size, frameCount = Time.frameCount, elapsedFrames = Time.frameCount - startedFrame,
                elapsedSeconds = Time.realtimeSinceStartupAsDouble - startedAt, requestedBytes = MaximumCodeBytes,
                windowsMonoPlayer = true, followedJump = false
            };
            string prefix = "XR3-transform-jit-audit-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + snapshot.processId;
            string folder = Application.persistentDataPath;
            try
            {
                Type projection = typeof(IndicatorSystem.Core.IndicatorCanvasProjection);
                MethodInfo method = projection.GetMethod("ToAncestor", BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(Transform), typeof(Transform), typeof(Matrix4x4).MakeByRefType() }, null);
                if (method == null) throw new MissingMethodException(projection.FullName, "ToAncestor");
                snapshot.assembly = method.Module.Assembly.FullName; snapshot.moduleMvid = method.Module.ModuleVersionId.ToString("D");
                snapshot.method = method.ToString(); snapshot.metadataToken = "0x" + method.MetadataToken.ToString("x8");
                MethodImplAttributes flags = method.GetMethodImplementationFlags();
                snapshot.implementationFlags = flags.ToString(); snapshot.implementationFlagValue = (int)flags;
                snapshot.noInlining = (flags & MethodImplAttributes.NoInlining) != 0;
                snapshot.noOptimization = (flags & MethodImplAttributes.NoOptimization) != 0;

                // No PrepareMethod, invocation, trampoline following, or patching. Preserve the raw entry for offline analysis.
                IntPtr entry = method.MethodHandle.GetFunctionPointer();
                if (entry == IntPtr.Zero) throw new InvalidOperationException("Method entry point is null");
                ulong address = unchecked((ulong)entry.ToInt64());
                snapshot.entryAddress = "0x" + address.ToString("x"); snapshot.pageSize = Environment.SystemPageSize;
                // Do not cross the current code page even if a trampoline happens to sit near its end.
                int remaining = snapshot.pageSize - (int)(address % (ulong)snapshot.pageSize);
                byte[] bytes = new byte[Math.Min(MaximumCodeBytes, remaining)];
                Marshal.Copy(entry, bytes, 0, bytes.Length); snapshot.capturedBytes = bytes.Length;
                snapshot.codeHex = Hex(bytes);
                using (SHA256 sha = SHA256.Create()) snapshot.codeSha256 = Hex(sha.ComputeHash(bytes));
                snapshot.binaryFile = prefix + ".bin";
                Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, snapshot.binaryFile), bytes);
            }
            catch (Exception error) { snapshot.error = error.ToString(); }
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, prefix + ".json");
                File.WriteAllText(path, JsonUtility.ToJson(snapshot, true));
                Debug.Log("[FAA TRANSFORM JIT AUDIT] " + path + " bytes=" + snapshot.capturedBytes +
                    " flags=" + snapshot.implementationFlags + " error=" + (snapshot.error ?? "none"));
            }
            catch (Exception error) { Debug.LogWarning("[FAA TRANSFORM JIT AUDIT] Could not save local evidence: " + error); }
#endif
        }

#if UNITY_STANDALONE_WIN && ENABLE_MONO && !UNITY_EDITOR && !ENABLE_IL2CPP
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
#endif
    }
}
