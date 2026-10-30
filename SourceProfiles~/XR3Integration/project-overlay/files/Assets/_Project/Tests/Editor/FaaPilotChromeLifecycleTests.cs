using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace FAA.Customization.Tests
{
    /// <summary>Exercises real Brief disable/destroy callbacks after the chrome's owned view dies first.</summary>
    public sealed class FaaPilotChromeLifecycleTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static Type Chrome => Type.GetType("FAA.Customization.FaaPilotChrome, Assembly-CSharp", true);
        private static object Call(object target, string name, params object[] args) =>
            (target is Type type ? type : target.GetType()).GetMethods(Any).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(target is Type ? null : target, args);
        private static object Prop(object target, string name) => target.GetType().GetProperty(name, Any).GetValue(target);
        private static Component Create() => (Component)Call(Chrome, "CreateForTests");
        private static void Add(Component chrome, string id) => Call(chrome, "AddButton", id,
            Enum.Parse(Type.GetType("FAA.Customization.FaaChromeCluster, Assembly-CSharp", true), "Left"), 20f, id, null, null, null);

        [TearDown]
        public void ResetStaticQuitState() => Call(Chrome, "ResetDeveloperMode");

        [Test]
        public void DestroyedEntryIsPrunedBeforeStateChangesOrAnotherButtonsRelayout()
        {
            Component chrome = Create();
            try
            {
                Add(chrome, "dead"); Add(chrome, "other");
                UnityEngine.Object.DestroyImmediate(((RectTransform)Call(chrome, "ButtonRect", "dead")).gameObject);
                Assert.DoesNotThrow(() => Call(chrome, "SetButtonState", "dead", false, false, null, null));
                Assert.DoesNotThrow(() => Call(chrome, "RemoveButton", "other"));
                Assert.That(Call(chrome, "HasButton", "dead"), Is.False);
                Add(chrome, "dead");
                Assert.That(Call(chrome, "HasButton", "dead"), Is.True, "An individually removed button can be registered again.");
            }
            finally { UnityEngine.Object.DestroyImmediate(chrome.gameObject); }
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void QuitRejectsLateEnsureAndRetainedCallbacksWithoutRebuildingTheView()
        {
            Component chrome = Create();
            try
            {
                Add(chrome, "brief");
                Call(chrome, "OnApplicationQuit");
                Assert.That(Chrome.GetProperty("Current", Any).GetValue(null), Is.Null);
                Assert.That(Call(Chrome, "Ensure"), Is.Null);
                Assert.DoesNotThrow(() => Call(chrome, "SetButtonState", "brief", false, false, null, null));
                Assert.DoesNotThrow(() => Call(chrome, "RemoveButton", "brief"));
                Assert.That(Call(chrome, "AddButton", "late", Enum.Parse(Type.GetType("FAA.Customization.FaaChromeCluster, Assembly-CSharp", true), "Left"), 1f, "late", null, null, null), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(chrome.gameObject); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RetainedStatusHelpAndCommandsApisDoNothingAfterViewTeardown(bool quitting)
        {
            Component chrome = Create();
            int selections = 0;
            Type rowType = Type.GetType("FAA.Customization.FaaChromeCommandRow, Assembly-CSharp", true);
            Array rows = Array.CreateInstance(rowType, 1);
            rows.SetValue(Activator.CreateInstance(rowType, "Views", "HUD", new[] { "ON", "OFF" },
                (Func<int>)(() => 0), (Action<int>)(_ => selections++), false), 0);
            try
            {
                Call(chrome, "SetHelpVisible", true);
                Call(chrome, "SetCommandRows", rows);
                Call(chrome, "SetCommandsVisible", true);
                if (quitting) Call(chrome, "OnApplicationQuit");
                var canvas = (Canvas)Prop(chrome, "Canvas");
                UnityEngine.Object.DestroyImmediate(canvas.gameObject);
                Assert.DoesNotThrow(() =>
                {
                    Call(chrome, "ReportStatus", "late", Enum.Parse(Type.GetType("FAA.Customization.FaaChromeSeverity, Assembly-CSharp", true), "Warning"), "NO FLIGHT DATA", "late", 0, true);
                    Call(chrome, "ClearStatus", "late");
                    Call(chrome, "SetHelpEntry", "late", "late", 2, false);
                    Call(chrome, "RemoveHelpEntry", "late");
                    Call(chrome, "SetHelpVisible", true);
                    Call(chrome, "SetHelpVisible", false);
                    Call(chrome, "SetCommandRows", rows);
                    Call(chrome, "SetCommandsVisible", true);
                    Call(chrome, "PressCommand", 0, 0);
                });
                Assert.That(selections, Is.Zero, "A retained callback cannot invoke a command after its view was disposed.");
                Assert.That(chrome.transform.childCount, Is.Zero, "No Help/Commands/Status widgets may be recreated by late callbacks.");
                Assert.That(Chrome.GetProperty("Current", Any).GetValue(null), Is.Null);
                Assert.That(Call(Chrome, "Ensure"), Is.Null);
            }
            finally { UnityEngine.Object.DestroyImmediate(chrome.gameObject); }
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void DestroyedStatusChipDoesNotBreakReportsOrOtherButtonsLayout()
        {
            Component chrome = Create();
            try
            {
                Add(chrome, "other");
                UnityEngine.Object.DestroyImmediate(((RectTransform)Call(chrome, "ButtonRect", "status")).gameObject);
                Assert.DoesNotThrow(() =>
                {
                    Call(chrome, "ReportStatus", "late", Enum.Parse(Type.GetType("FAA.Customization.FaaChromeSeverity, Assembly-CSharp", true), "Warning"), "NO FLIGHT DATA", null, 0, true);
                    Call(chrome, "ClearStatus", "late");
                    Call(chrome, "SetButtonState", "other", false, false, null, null);
                    Call(chrome, "Relayout", 600f);
                });
                Assert.That(Prop(chrome, "StatusVisible"), Is.False);
                Assert.That(Call(chrome, "HasButton", "status"), Is.False, "The dead cached chip is not recreated by a report.");
                Assert.That(Call(chrome, "HasButton", "other"), Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(chrome.gameObject); }
            LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RealBriefBuildAndRefreshPreserveStaticFontWithoutEllipsisWarnings()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            Component chrome = Create();
            TMP_FontAsset font = TMP_Settings.defaultFontAsset;
            int glyphs = font.glyphTable.Count, characters = font.characterTable.Count;
            var host = new GameObject("Real Brief static-font regression");
            var panel = host.AddComponent(Type.GetType("FAA.Explanations.ExplanationAssistantPanel, Assembly-CSharp", true));
            for (int i = 0; i < 3; i++)
            {
                Call(panel, "RebuildView");
                Canvas.ForceUpdateCanvases();
                yield return null;
            }
            TMP_Text[] labels = host.GetComponentsInChildren<TMP_Text>(true);
            Assert.That(labels.Count(t => (t.fontStyle & FontStyles.Bold) != 0 && t.textWrappingMode == TextWrappingModes.NoWrap), Is.GreaterThanOrEqualTo(6));
            foreach (TMP_Text label in labels)
            {
                if ((label.fontStyle & FontStyles.Bold) != 0 && label.textWrappingMode == TextWrappingModes.NoWrap)
                    Assert.That(label.overflowMode, Is.EqualTo(TextOverflowModes.Truncate), label.name);
                if (label.textWrappingMode == TextWrappingModes.Normal)
                    Assert.That(label.overflowMode, label.name == "Streamed brief" ? Is.EqualTo(TextOverflowModes.Page) : Is.EqualTo(TextOverflowModes.Overflow), "Paragraph wrapping/pagination must remain intact: " + label.name);
            }
            Assert.That(labels.Any(t => t.fontStyle == FontStyles.Normal && t.overflowMode == TextOverflowModes.Ellipsis), Is.True, "Regular labels keep their supported ellipsis.");
            Assert.That(font.atlasPopulationMode, Is.EqualTo(AtlasPopulationMode.Static));
            Assert.That(font.glyphTable.Count, Is.EqualTo(glyphs));
            Assert.That(font.characterTable.Count, Is.EqualTo(characters));
            UnityEngine.Object.Destroy(host); yield return null;
            UnityEngine.Object.Destroy(chrome.gameObject); yield return null;
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator RealBriefDisableAndDestroyRemainSafeWhenChromeCanvasWasDestroyedFirst()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            Component chrome = Create();
            var host = new GameObject("Real Brief lifecycle consumer");
            var panel = host.AddComponent(Type.GetType("FAA.Explanations.ExplanationAssistantPanel, Assembly-CSharp", true));
            Assert.That(Prop(panel, "IsDocked"), Is.True);
            var canvas = (Canvas)Prop(chrome, "Canvas");
            UnityEngine.Object.Destroy(canvas.gameObject);
            yield return null;
            Assert.That(Chrome.GetProperty("Current", Any).GetValue(null), Is.Null, "A surviving MonoBehaviour does not make its destroyed view usable.");
            host.SetActive(false); // Actual ExplanationAssistantPanel.OnDisable.
            UnityEngine.Object.Destroy(host); // Actual OnDestroy -> RemoveButton -> Relayout path.
            yield return null;
            Assert.That(chrome != null, Is.True, "The chrome host deliberately outlives its owned Canvas in this regression.");
            UnityEngine.Object.Destroy(chrome.gameObject);
            yield return null;
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator OrdinaryBriefDisableAndReenableRestoreItsExistingChromeButton()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            yield return new EnterPlayMode();
            Component chrome = Create();
            var host = new GameObject("Reenabled real Brief");
            var panel = (Behaviour)host.AddComponent(Type.GetType("FAA.Explanations.ExplanationAssistantPanel, Assembly-CSharp", true));
            Assert.That(Prop(panel, "IsDocked"), Is.True);
            var before = (RectTransform)Call(chrome, "ButtonRect", "brief");
            Assert.That(before, Is.Not.Null);
            panel.enabled = false;
            Assert.That(Call(chrome, "IsButtonVisible", "brief"), Is.False);
            panel.enabled = true;
            yield return null;
            Assert.That(Call(chrome, "IsButtonVisible", "brief"), Is.True);
            Assert.That(Call(chrome, "ButtonRect", "brief"), Is.SameAs(before), "Disable is reversible; it is not disposal.");
            UnityEngine.Object.Destroy(host);
            yield return null;
            Assert.That(Call(chrome, "HasButton", "brief"), Is.False, "The actual consumer unregisters its button on final destruction.");
            UnityEngine.Object.Destroy(chrome.gameObject);
            yield return null;
            LogAssert.NoUnexpectedReceived();
            yield return new ExitPlayMode();
        }
    }
}
