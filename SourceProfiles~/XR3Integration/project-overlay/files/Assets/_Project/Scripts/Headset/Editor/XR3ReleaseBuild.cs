#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;
using UnityEngine.XR.Management;

namespace FAA.Headset.Editor
{
    /// <summary>
    /// Reproducible native Varjo XR-3 release. Mono (the default) is cross-buildable on macOS.
    /// Pass <c>-xr3Backend il2cpp</c> on a Windows host with the Windows IL2CPP module and the MSVC/Windows SDK
    /// toolchain to compile the scripts ahead of time instead of running them on the Mono JIT.
    /// </summary>
    public static class XR3ReleaseBuild
    {
        // Ship the integration scene that contains the currently validated HUD and terrain bridge.
        public const string MainScene = "Assets/_Project/Scenes/ExperimentScene.unity";
        public static bool IsBuildingXR3 { get; private set; }

        [Serializable]
        private sealed class ReleaseReport
        {
            public string result;
            public string utc;
            public string unity;
            public string target = "StandaloneWindows64";
            public string graphicsApi = "Direct3D11";
            public string scriptingBackend;
            public string varjoPlugin;
            public string executable;
            public string[] scenes;
            public ulong bytes;
            public double seconds;
            public int errors;
            public int warnings;
            public bool development = false;
            public bool hardwareTested = false;
            public string[] buildErrors;
            public string[] ignoredEditorToolErrors;
        }

        // Editor-only tooling that may log while the build reloads domains (for example the MCP for Unity bridge
        // failing to reach its local server). These messages never come from the player build itself.
        private const string EditorToolLogPrefix = "<b><color=#cc3333>MCP-FOR-UNITY</color></b>:";

        public static bool IsEditorToolMessage(string content) =>
            content != null && content.StartsWith(EditorToolLogPrefix, StringComparison.Ordinal);

        [MenuItem("FAA/Headset/Build XR-3 Windows x64 Release")]
        public static void BuildWindows64()
        {
            const BuildTarget target = BuildTarget.StandaloneWindows64;
            var namedTarget = NamedBuildTarget.Standalone;
            if (EditorUserBuildSettings.activeBuildTarget != target)
                throw new BuildFailedException("Select Windows x64 first, or pass -buildTarget Win64 to the Unity command line.");
            if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Standalone, target))
                throw new BuildFailedException("Install this Unity editor's Windows Build Support (Mono) module.");
            if (!File.Exists(MainScene))
                throw new BuildFailedException("FAA ExperimentScene is missing.");
            if (!EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(
                XRGeneralSettings.k_SettingsKey, out var perTarget))
                throw new BuildFailedException("Standalone XR settings are missing.");
            var general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Standalone);
            if (general == null || !general.InitManagerOnStart || general.Manager == null ||
                general.Manager.activeLoaders.Count == 0 ||
                general.Manager.activeLoaders[0].GetType().FullName != "Varjo.XR.VarjoLoader")
                throw new BuildFailedException("Varjo must be the first Standalone XR loader, with Initialize XR on Startup enabled.");

            bool il2cpp = UseIl2Cpp(GetArgument("-xr3Backend"));
            if (il2cpp && Application.platform != RuntimePlatform.WindowsEditor)
                throw new BuildFailedException("Windows IL2CPP players must be built by a Windows Editor; use the Mono backend on macOS.");
            if (il2cpp && !Directory.Exists(Path.Combine(EditorApplication.applicationContentsPath,
                    "PlaybackEngines", "windowsstandalonesupport", "Variations", "win64_player_nondevelopment_il2cpp")))
                throw new BuildFailedException("Install this Unity editor's Windows Build Support (IL2CPP) module.");

            string output = GetArgument("-xr3Output") ?? "Builds/FAA-XR3-Windows-x64/FAA-XR3.exe";
            output = Path.GetFullPath(output);
            if (!string.Equals(Path.GetExtension(output), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("-xr3Output must name an .exe file.");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            // Restore the user's editor/player preferences even on a failed build.
            bool autoApi = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(target);
            ScriptingImplementation backend = PlayerSettings.GetScriptingBackend(namedTarget);
            Il2CppCompilerConfiguration il2cppConfiguration = PlayerSettings.GetIl2CppCompilerConfiguration(namedTarget);
            ManagedStrippingLevel stripping = PlayerSettings.GetManagedStrippingLevel(namedTarget);
            ColorSpace colorSpace = PlayerSettings.colorSpace;
            bool background = PlayerSettings.runInBackground;
            bool graphicsJobs = PlayerSettings.graphicsJobs;
            bool development = EditorUserBuildSettings.development;
            bool allowDebugging = EditorUserBuildSettings.allowDebugging;
            try
            {
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, false);
                PlayerSettings.SetGraphicsAPIs(target, new[] { GraphicsDeviceType.Direct3D11 });
                PlayerSettings.SetScriptingBackend(namedTarget, il2cpp ? ScriptingImplementation.IL2CPP : ScriptingImplementation.Mono2x);
                if (il2cpp)
                {
                    // Release C++ optimization; Minimal stripping keeps reflection-reached user code intact.
                    PlayerSettings.SetIl2CppCompilerConfiguration(namedTarget, Il2CppCompilerConfiguration.Release);
                    PlayerSettings.SetManagedStrippingLevel(namedTarget, ManagedStrippingLevel.Minimal);
                }
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.runInBackground = true;
                PlayerSettings.graphicsJobs = false;
                EditorUserBuildSettings.development = false;
                EditorUserBuildSettings.allowDebugging = false;
                IsBuildingXR3 = true;

                Debug.Log("[FAA XR3 BUILD] Building native Varjo Windows x64 release (" + (il2cpp ? "IL2CPP" : "Mono") + "): " + output);
                BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = new[] { MainScene },
                    locationPathName = output,
                    target = target,
                    options = BuildOptions.CompressWithLz4 | BuildOptions.CleanBuildCache,
                });
                var summary = report.summary;
                string[] stepErrors = report.steps.SelectMany(step => step.messages)
                    .Where(message => message.type == LogType.Error || message.type == LogType.Exception)
                    .Select(message => message.content).ToArray();
                string[] ignored = stepErrors.Where(IsEditorToolMessage).ToArray();
                string[] buildErrors = stepErrors.Where(message => !IsEditorToolMessage(message)).ToArray();
                int errors = Math.Max(0, summary.totalErrors - ignored.Length);
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Varjo.XR.VarjoLoader).Assembly);
                var evidence = new ReleaseReport
                {
                    result = summary.result.ToString(),
                    utc = DateTime.UtcNow.ToString("O"),
                    unity = Application.unityVersion,
                    scriptingBackend = il2cpp ? "IL2CPP" : "Mono",
                    varjoPlugin = package != null ? package.version : "unknown",
                    executable = Path.GetFileName(output),
                    scenes = new[] { MainScene },
                    bytes = summary.totalSize,
                    seconds = summary.totalTime.TotalSeconds,
                    errors = errors,
                    warnings = summary.totalWarnings,
                    buildErrors = buildErrors,
                    ignoredEditorToolErrors = ignored,
                };
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "build-report.json"), JsonUtility.ToJson(evidence, true));
                // Unity can return Succeeded even when individual shaders fail.
                // Never package a release with pink/error shader variants.
                if (summary.result != BuildResult.Succeeded || errors != 0 || buildErrors.Length != 0 || !File.Exists(output))
                    throw new BuildFailedException("XR-3 player build failed; see build-report.json and the Unity log.");
                Debug.Log("[FAA XR3 BUILD] SUCCESS: " + output + " (" + summary.totalSize + " bytes)");
            }
            finally
            {
                IsBuildingXR3 = false;
                PlayerSettings.SetGraphicsAPIs(target, apis);
                PlayerSettings.SetUseDefaultGraphicsAPIs(target, autoApi);
                PlayerSettings.SetScriptingBackend(namedTarget, backend);
                PlayerSettings.SetIl2CppCompilerConfiguration(namedTarget, il2cppConfiguration);
                PlayerSettings.SetManagedStrippingLevel(namedTarget, stripping);
                PlayerSettings.colorSpace = colorSpace;
                PlayerSettings.runInBackground = background;
                PlayerSettings.graphicsJobs = graphicsJobs;
                EditorUserBuildSettings.development = development;
                EditorUserBuildSettings.allowDebugging = allowDebugging;
            }
        }

        /// <summary>Selects the scripting backend from <c>-xr3Backend</c>: absent or <c>mono</c> = Mono, <c>il2cpp</c> = IL2CPP.</summary>
        public static bool UseIl2Cpp(string value)
        {
            if (string.IsNullOrEmpty(value) || string.Equals(value, "mono", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(value, "il2cpp", StringComparison.OrdinalIgnoreCase)) return true;
            throw new BuildFailedException("-xr3Backend must be mono or il2cpp, not '" + value + "'.");
        }

        private static string GetArgument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
                if (args[i] == name)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("-", StringComparison.Ordinal))
                        throw new BuildFailedException("Missing value for " + name);
                    return args[i + 1];
                }
            return null;
        }
    }

    /// <summary>Modify only the build's scene copy, never the user's scene asset.</summary>
    internal sealed class XR3ReleaseSceneProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => 10000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!XR3ReleaseBuild.IsBuildingXR3 || report == null ||
                report.summary.platform != BuildTarget.StandaloneWindows64) return;

            ValidateScriptAssets(scene);

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                // XRI 3.x's XRInteractionSimulator is not an XRDeviceSimulator
                // subclass. Strip both implementations before any player Awake.
                foreach (XRInteractionSimulator simulator in root.GetComponentsInChildren<XRInteractionSimulator>(true))
                    if (simulator != null) UnityEngine.Object.DestroyImmediate(simulator.gameObject);
                if (root == null) continue;
                foreach (XRDeviceSimulator simulator in root.GetComponentsInChildren<XRDeviceSimulator>(true))
                    if (simulator != null) UnityEngine.Object.DestroyImmediate(simulator.gameObject);
                if (root == null) continue;
                // Post Processing Stack v1's Graphics.Blit intermediates are
                // not a validated texture-array/quad-view path. Keep native
                // headset rendering free of these legacy camera effects.
                foreach (Behaviour component in root.GetComponentsInChildren<Behaviour>(true))
                {
                    if (component != null && component.enabled &&
                        component.GetType().FullName == "UnityEngine.PostProcessing.PostProcessingBehaviour")
                    {
                        component.enabled = false;
                        Debug.Log("[FAA XR3 BUILD] Disabled legacy Post Processing Stack v1 on " + component.gameObject.name);
                    }
                }
                foreach (XR3HeadsetCompatibility bridge in root.GetComponentsInChildren<XR3HeadsetCompatibility>(true))
                {
                    var settings = new SerializedObject(bridge);
                    settings.FindProperty("activationMode").enumValueIndex = (int)XR3HeadsetCompatibility.ActivationMode.Auto;
                    settings.FindProperty("autoDetectNativeXr").boolValue = true;
                    settings.FindProperty("enableEditorSimulator").boolValue = false;
                    settings.FindProperty("enableSimulatorInPlayer").boolValue = false;
                    settings.ApplyModifiedPropertiesWithoutUndo();
                }
            }
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.GetComponentInChildren<XRInteractionSimulator>(true) != null ||
                    root.GetComponentInChildren<XRDeviceSimulator>(true) != null)
                    throw new BuildFailedException("A desktop XR simulator remains in the native XR-3 scene.");
            }
            Debug.Log("[FAA XR3 BUILD] Native-only scene prepared: " + scene.path);
        }

        private static void ValidateScriptAssets(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                MonoScript script = MonoScript.FromMonoBehaviour(component);
                // A scene-embedded MonoScript can work in Editor but lose its
                // serialized fields in the player, crashing level0 on load.
                if (script == null || !EditorUtility.IsPersistent(script) ||
                    script.GetClass() != component.GetType())
                    throw new BuildFailedException(
                        "Scene component " + component.GetType().FullName + " on " + component.name +
                        " has no persistent matching MonoScript asset. Put the component in a matching " +
                        ".cs file and rebind its m_Script before building: " + scene.path);
            }
        }
    }
}
#endif
