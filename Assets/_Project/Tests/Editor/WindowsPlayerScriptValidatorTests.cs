using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace FAA.Customization.Tests
{
    public class WindowsPlayerScriptValidatorTests
    {
        private const string MainScene = "Assets/_Project/Scenes/ExperimentScene.unity";
        private const string ProviderPath = "Assets/_Project/Scripts/HUD/Core/AviationFlightDataProvider.cs";
        private const string ProviderType = "AviationUI.AviationFlightDataProvider, Assembly-CSharp";
        private const string EmbeddedProviderScript =
            "\n--- !u!115 &987654321\nMonoScript:\n" +
            "  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n" +
            "  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n" +
            "  m_Name: \n  serializedVersion: 7\n  m_DefaultReferences: {}\n" +
            "  m_Icon: {fileID: 0}\n  m_Type: 0\n  m_ExecutionOrder: 0\n" +
            "  m_ClassName: AviationFlightDataProvider\n  m_Namespace: AviationUI\n" +
            "  m_AssemblyName: Assembly-CSharp\n";
        private static Type Validator => Type.GetType(
            "FAA.Build.Editor.WindowsPlayerScriptValidator, Assembly-CSharp-Editor", true);

        private static readonly string[] RepairedTypes =
        {
            ProviderType,
            "FAA.Customization.WeatherMetricIconGraphic, Assembly-CSharp",
            "FAA.Customization.RadarContextLeaderGraphic, Assembly-CSharp",
            "FAA.Customization.TrafficRadarActionFocus, Assembly-CSharp",
            "TrafficRadar.RadarNavigationTargetGraphic, TrafficRadar",
            "FAA.Customization.FaaRadarButtonMotion, Assembly-CSharp",
            "FAA.Customization.FaaRadarConfigurationDrawer, Assembly-CSharp",
        };

        [TestCase(ProviderPath, ProviderType)]
        [TestCase("Assets/_Project/Scripts/Customization/WeatherMetricIconGraphic.cs",
            "FAA.Customization.WeatherMetricIconGraphic, Assembly-CSharp")]
        [TestCase("Assets/_Project/Scripts/Customization/RadarContextLeaderGraphic.cs",
            "FAA.Customization.RadarContextLeaderGraphic, Assembly-CSharp")]
        [TestCase("Assets/_Project/Scripts/Customization/TrafficRadarActionFocus.cs",
            "FAA.Customization.TrafficRadarActionFocus, Assembly-CSharp")]
        [TestCase("Assets/_Project/Scripts/TrafficRadar/Display/RadarNavigationTargetGraphic.cs",
            "TrafficRadar.RadarNavigationTargetGraphic, TrafficRadar")]
        [TestCase("Assets/_Project/Scripts/Customization/FaaRadarButtonMotion.cs",
            "FAA.Customization.FaaRadarButtonMotion, Assembly-CSharp")]
        [TestCase("Assets/_Project/Scripts/Customization/FaaRadarConfigurationDrawer.cs",
            "FAA.Customization.FaaRadarConfigurationDrawer, Assembly-CSharp")]
        public void RepairedComponent_HasMatchingPersistentScriptAsset(string path, string typeName)
        {
            Type type = Type.GetType(typeName, true);
            MonoScript asset = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
            Assert.That(asset, Is.Not.Null, path);
            Assert.That(asset.GetClass(), Is.EqualTo(type));
            Assert.That(EditorUtility.IsPersistent(asset), Is.True);
            string guid = AssetDatabase.AssetPathToGUID(path);
            Assert.That(guid, Is.Not.Empty);
            Assert.That(AssetDatabase.GUIDToAssetPath(guid), Is.EqualTo(path));

            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var host = new GameObject("Script asset binding", typeof(RectTransform));
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                var component = (MonoBehaviour)host.AddComponent(type);
                Assert.That(MonoScript.FromMonoBehaviour(component), Is.EqualTo(asset));
                Assert.DoesNotThrow(() => ValidateScene(scene));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void EngineUIDocument_HasAnAcceptedPersistentBuiltInScript()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var host = new GameObject("Built-in UI document");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                var document = host.AddComponent<UIDocument>();
                using var serialized = new SerializedObject(document);
                var script = serialized.FindProperty("m_Script").objectReferenceValue as MonoScript;
                Assert.That(script, Is.Not.Null);
                Assert.That(EditorUtility.IsPersistent(script), Is.True);
                Assert.That(script.GetClass(), Is.EqualTo(typeof(UIDocument)));
                Assert.That(script.GetClass().Assembly, Is.EqualTo(typeof(UIDocument).Assembly));
                Assert.That(AssetDatabase.GetAssetPath(script), Is.EqualTo("Library/unity default resources"));
                Assert.DoesNotThrow(() => ValidateScene(scene));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void ExperimentScene_UsesPersistentBindingsForEveryRepairedComponent()
        {
            string yaml = File.ReadAllText(MainScene);
            Assert.That(Regex.IsMatch(yaml, @"^--- !u!115 ", RegexOptions.Multiline), Is.False,
                "Scene-local MonoScript records must not return to the source scene.");
            Assert.That(Regex.IsMatch(yaml, @"^  m_Script: \{fileID: [1-9][0-9]*\}$", RegexOptions.Multiline), Is.False,
                "Every script reference must point to its persistent asset GUID.");

            Scene scene = SceneManager.GetSceneByPath(MainScene);
            bool opened = !scene.isLoaded;
            if (opened) scene = EditorSceneManager.OpenScene(MainScene, OpenSceneMode.Additive);
            try
            {
                Assert.DoesNotThrow(() => ValidateScene(scene));
                foreach (string typeName in RepairedTypes)
                {
                    Type type = Type.GetType(typeName, true);
                    int count = 0;
                    foreach (GameObject root in scene.GetRootGameObjects())
                        count += root.GetComponentsInChildren(type, true).Length;
                    Assert.That(count, Is.GreaterThan(0), typeName + " must remain bound in ExperimentScene.");
                }
            }
            finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
        }

        [Test]
        public void EnabledBuildScenes_HavePersistentScriptBindings()
        {
            int enabledScenes = 0;
            foreach (EditorBuildSettingsScene buildScene in EditorBuildSettings.scenes)
            {
                if (!buildScene.enabled) continue;
                enabledScenes++;
                Scene scene = SceneManager.GetSceneByPath(buildScene.path);
                bool opened = !scene.isLoaded;
                if (opened) scene = EditorSceneManager.OpenScene(buildScene.path, OpenSceneMode.Additive);
                try
                {
                    Assert.DoesNotThrow(() => ValidateScene(scene),
                        "The configured Windows player scene must pass the same script binding guard: " + buildScene.path);
                }
                finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
            }
            Assert.That(enabledScenes, Is.GreaterThan(0), "At least one configured build scene must be verified.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void MissingScript_InInactiveChildFailsWithSceneAndObjectDiagnostics(bool missingGuid)
        {
            WithSceneAsset((scenePath, yaml) =>
            {
                string reference = missingGuid
                    ? "{fileID: 11500000, guid: ffffffffffffffffffffffffffffffff, type: 3}"
                    : "{fileID: 0}";
                return ReplaceProviderReference(yaml, reference);
            }, scene =>
            {
                Transform child = scene.GetRootGameObjects()[0].transform.Find("Inactive child");
                Assert.That(child.gameObject.activeSelf, Is.False);
                Assert.That(GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(child.gameObject), Is.EqualTo(1));
                Exception error = ValidationFailure("ValidateScene", new object[] { scene });
                Assert.That(error.Message, Does.Contain(scene.path));
                Assert.That(error.Message, Does.Contain("Script binding fixture/Inactive child"));
                Assert.That(error.Message, Does.Contain("missing script"));
            });
        }

        [Test]
        public void SceneLocalMonoScript_IsRejectedBeforePlayerSerialization()
        {
            WithSceneAsset((scenePath, yaml) => ReplaceProviderReference(yaml, "{fileID: 987654321}") + EmbeddedProviderScript,
                scene =>
                {
                    Transform child = scene.GetRootGameObjects()[0].transform.Find("Inactive child");
                    Assert.That(child.GetComponent(Type.GetType(ProviderType, true)), Is.Not.Null,
                        "The scene-local script must load its class before its asset binding is checked.");
                    Exception error = ValidationFailure("ValidateScene", new object[] { scene });
                    Assert.That(error.Message, Does.Contain(scene.path));
                    Assert.That(error.Message, Does.Contain("Script binding fixture/Inactive child"));
                });
        }

        [Test]
        public void EmbeddedMonoScriptDocument_IsRejectedEvenWithPersistentComponentBindings()
        {
            WithSceneAsset((scenePath, yaml) => yaml + EmbeddedProviderScript, scene =>
            {
                Transform child = scene.GetRootGameObjects()[0].transform.Find("Inactive child");
                var component = (MonoBehaviour)child.GetComponent(Type.GetType(ProviderType, true));
                Assert.That(component, Is.Not.Null);
                Assert.That(EditorUtility.IsPersistent(MonoScript.FromMonoBehaviour(component)), Is.True);
                Exception error = ValidationFailure("ValidateScene", new object[] { scene });
                Assert.That(error.Message, Does.Contain(scene.path));
                Assert.That(error.Message, Does.Contain("embedded MonoScript document"));
            });
        }

        [Test]
        public void MismatchedPersistentScript_IsRejectedWithComponentDiagnostics()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var host = new GameObject("Mismatched script fixture");
                host.SetActive(false);
                SceneManager.MoveGameObjectToScene(host, scene);
                var component = (MonoBehaviour)host.AddComponent(Type.GetType(ProviderType, true));
                MonoScript wrongScript = AssetDatabase.LoadAssetAtPath<MonoScript>(
                    "Assets/_Project/Scripts/Customization/FaaRadarButtonMotion.cs");
                Assert.That(wrongScript, Is.Not.Null);
                Exception error = ValidationFailure("ValidateScriptAsset", new object[] { component, wrongScript, "Mismatch.unity" });
                Assert.That(error.Message, Does.Contain("Mismatch.unity"));
                Assert.That(error.Message, Does.Contain("Mismatched script fixture"));
                Assert.That(error.Message, Does.Contain("AviationUI.AviationFlightDataProvider"));
                Assert.That(error.Message, Does.Contain("persistent matching MonoScript"));
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void SceneProcessingWithoutBuildReport_DoesNotValidateEditorScenes()
        {
            var processor = (IProcessSceneWithReport)Activator.CreateInstance(Validator);
            Assert.DoesNotThrow(() => processor.OnProcessScene(default, null));
        }

        private static void ValidateScene(Scene scene) => Validator.GetMethod("ValidateScene")
            .Invoke(null, new object[] { scene });

        private static Exception ValidationFailure(string method, object[] arguments)
        {
            MethodInfo target = Validator.GetMethod(method,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(target, Is.Not.Null);
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(() => target.Invoke(null, arguments));
            Assert.That(error.InnerException, Is.TypeOf<BuildFailedException>());
            return error.InnerException;
        }

        private static string ReplaceProviderReference(string yaml, string reference)
        {
            string guid = AssetDatabase.AssetPathToGUID(ProviderPath);
            string original = "{fileID: 11500000, guid: " + guid + ", type: 3}";
            Assert.That(yaml, Does.Contain(original));
            return yaml.Replace(original, reference);
        }

        private static void WithSceneAsset(Func<string, string, string> corrupt, Action<Scene> verify)
        {
            string folderName = "__WindowsPlayerScriptValidatorTests_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            string path = folder + "/ScriptBinding.unity";
            Scene scene = default;
            try
            {
                string yaml = ScriptBindingSceneYaml();
                File.WriteAllText(path, yaml);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                Assert.That(scene.GetRootGameObjects().Length, Is.EqualTo(1));
                Transform child = scene.GetRootGameObjects()[0].transform.Find("Inactive child");
                Assert.That(child.GetComponent(Type.GetType(ProviderType, true)), Is.Not.Null);
                Assert.DoesNotThrow(() => ValidateScene(scene), "The fixture must be valid before deliberate corruption.");
                EditorSceneManager.CloseScene(scene, true);
                scene = default;
                File.WriteAllText(path, corrupt(path, yaml));
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                verify(scene);
            }
            finally
            {
                if (scene.IsValid() && scene.isLoaded) EditorSceneManager.CloseScene(scene, true);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static string ScriptBindingSceneYaml() =>
            @"%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!1 &1000
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  serializedVersion: 6
  m_Component:
  - component: {fileID: 1001}
  m_Layer: 0
  m_Name: Script binding fixture
  m_TagString: Untagged
  m_Icon: {fileID: 0}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 1
--- !u!4 &1001
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 1000}
  serializedVersion: 2
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_ConstrainProportionsScale: 0
  m_Children:
  - {fileID: 2001}
  m_Father: {fileID: 0}
  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}
--- !u!1 &2000
GameObject:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  serializedVersion: 6
  m_Component:
  - component: {fileID: 2001}
  - component: {fileID: 2002}
  m_Layer: 0
  m_Name: Inactive child
  m_TagString: Untagged
  m_Icon: {fileID: 0}
  m_NavMeshLayer: 0
  m_StaticEditorFlags: 0
  m_IsActive: 0
--- !u!4 &2001
Transform:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 2000}
  serializedVersion: 2
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_ConstrainProportionsScale: 0
  m_Children: []
  m_Father: {fileID: 1001}
  m_LocalEulerAnglesHint: {x: 0, y: 0, z: 0}
--- !u!114 &2002
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {fileID: 0}
  m_PrefabInstance: {fileID: 0}
  m_PrefabAsset: {fileID: 0}
  m_GameObject: {fileID: 2000}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {fileID: 11500000, guid: " + AssetDatabase.AssetPathToGUID(ProviderPath) + @", type: 3}
  m_Name:
  m_EditorClassIdentifier:
--- !u!1660057539 &9223372036854775807
SceneRoots:
  m_ObjectHideFlags: 0
  m_Roots:
  - {fileID: 1001}
";
    }
}
