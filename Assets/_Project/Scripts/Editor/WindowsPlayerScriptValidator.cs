using System;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace FAA.Build.Editor
{
    /// <summary>Reject script bindings that cannot be serialized safely into a Windows player.</summary>
    public sealed class WindowsPlayerScriptValidator : IProcessSceneWithReport
    {
        public int callbackOrder => -10000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || report.summary.platform != BuildTarget.StandaloneWindows64)
                return;

            ValidateScene(scene);
        }

        public static void ValidateScene(Scene scene)
        {
            string scenePath = string.IsNullOrEmpty(scene.path) ? scene.name : scene.path;
            ValidateSavedSceneBindings(scene);
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject gameObject = node.gameObject;
                int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(gameObject);
                if (missing != 0)
                    throw new BuildFailedException(
                        "Windows player scene '" + scenePath + "', object '" + HierarchyPath(node) +
                        "' contains " + missing + " missing script component(s). Restore the matching " +
                        ".cs and .meta assets and rebind m_Script before building.");

                foreach (MonoBehaviour component in gameObject.GetComponents<MonoBehaviour>())
                {
                    if (component == null)
                        throw new BuildFailedException(
                            "Windows player scene '" + scenePath + "', object '" + HierarchyPath(node) +
                            "' contains a null script component. Restore its script asset before building.");

                    using var serialized = new SerializedObject(component);
                    MonoScript script = serialized.FindProperty("m_Script")?.objectReferenceValue as MonoScript;
                    ValidateScriptAsset(component, script, scenePath);
                }
            }
        }

        private static void ValidateSavedSceneBindings(Scene scene)
        {
            string sourcePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), scene.path);
            if (EditorSceneManager.IsPreviewScene(scene) ||
                !string.Equals(Path.GetExtension(scene.path), ".unity", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(sourcePath))
                return;

            using var reader = new StreamReader(sourcePath);
            var prefix = new char[5];
            if (reader.ReadBlock(prefix, 0, prefix.Length) != prefix.Length || new string(prefix) != "%YAML")
                return; // Binary scene files have no YAML source records to inspect.

            reader.ReadLine(); // Consume the rest of the YAML version line.
            int lineNumber = 1;
            int embeddedScriptLine = 0;
            bool componentDocument = false;
            long ownerId = 0;
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                lineNumber++;
                if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                {
                    componentDocument = line.StartsWith("--- !u!114 ", StringComparison.Ordinal);
                    ownerId = 0;
                    if (embeddedScriptLine == 0 && line.StartsWith("--- !u!115 ", StringComparison.Ordinal))
                        embeddedScriptLine = lineNumber;
                }
                if (!componentDocument) continue;

                Match owner = Regex.Match(line, @"^\s+m_GameObject:\s*\{fileID:\s*(-?[0-9]+)\}");
                if (owner.Success) long.TryParse(owner.Groups[1].Value, out ownerId);
                if (Regex.IsMatch(line, @"^\s+m_Script:\s*\{") &&
                    !Regex.IsMatch(line, @"\bguid:\s*[0-9a-fA-F]{32}\b"))
                    throw new BuildFailedException(
                        "Windows player scene '" + scene.path + "', object '" + SourceObjectPath(scene, ownerId) +
                        "' has a scene-local or missing script reference at source line " + lineNumber +
                        ". Bind m_Script to a matching .cs/.dll asset GUID and remove embedded MonoScript records before building.");
            }

            if (embeddedScriptLine != 0)
                throw new BuildFailedException(
                    "Windows player scene '" + scene.path + "' contains an embedded MonoScript document at source line " +
                    embeddedScriptLine + ". Remove scene-local script records and bind components to matching .cs/.dll asset GUIDs before building.");
        }

        private static string SourceObjectPath(Scene scene, long ownerId)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            foreach (Transform node in root.GetComponentsInChildren<Transform>(true))
                if (unchecked((long)GlobalObjectId.GetGlobalObjectIdSlow(node.gameObject).targetObjectId) == ownerId)
                    return HierarchyPath(node);
            return "source GameObject fileID " + ownerId;
        }

        private static void ValidateScriptAsset(MonoBehaviour component, MonoScript script, string scenePath)
        {
            string assetPath = script == null ? string.Empty : AssetDatabase.GetAssetPath(script);
            string extension = Path.GetExtension(assetPath);
            bool importedScript = script != null && AssetDatabase.Contains(script) &&
                (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase));
            // UIDocument's MonoScript is provided by the engine rather than a .cs/.dll asset.
            // Type identity restricts this exception to Unity's own UIElements assembly.
            bool builtInDocument = component.GetType() == typeof(UIDocument) &&
                string.Equals(assetPath, "Library/unity default resources", StringComparison.Ordinal);
            // A scene-local MonoScript can appear to work in the Editor while losing
            // its serialized fields in the player, corrupting level0 during loading.
            if (script == null || !EditorUtility.IsPersistent(script) ||
                (!importedScript && !builtInDocument) ||
                script.GetClass() != component.GetType())
                throw new BuildFailedException(
                    "Windows player scene '" + scenePath + "', object '" + HierarchyPath(component.transform) +
                    "', component '" + component.GetType().FullName +
                    "' has no persistent matching MonoScript asset (script path: '" + assetPath +
                    "'). Put the component in a matching .cs file, preserve its .meta GUID, and rebind " +
                    "m_Script to that asset before building.");
        }

        private static string HierarchyPath(Transform node)
        {
            string path = node.name;
            while (node.parent != null)
            {
                node = node.parent;
                path = node.name + "/" + path;
            }
            return path;
        }
    }
}
