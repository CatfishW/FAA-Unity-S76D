using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    public sealed class BriefImageSharingCaptionTests
    {
        private const string Preference = "FAA.Explanations.IncludeImages";
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static Type PanelType => Type.GetType("FAA.Explanations.ExplanationAssistantPanel, Assembly-CSharp", true);
        private GameObject owner;
        private Component panel;
        private bool hadPreference;
        private int priorPreference;

        [SetUp]
        public void PreservePreference()
        {
            hadPreference = PlayerPrefs.HasKey(Preference); priorPreference = PlayerPrefs.GetInt(Preference);
            PlayerPrefs.SetInt(Preference, 1);
        }

        [TearDown]
        public void Cleanup()
        {
            if (owner != null) UnityEngine.Object.DestroyImmediate(owner);
            if (hadPreference) PlayerPrefs.SetInt(Preference, priorPreference); else PlayerPrefs.DeleteKey(Preference);
            PlayerPrefs.Save();
        }

        private object Value(string name) => PanelType.GetProperty(name).GetValue(panel);
        private void Call(string name, params object[] args) => PanelType.GetMethod(name, Any).Invoke(panel, args);

        private void Create(float width)
        {
            owner = new GameObject("Brief image caption fixture"); panel = owner.AddComponent(PanelType);
            // Non-ExecuteAlways runtime components do not receive the same lifecycle in every EditMode host.
            Call("EnsureView"); Call("SetTab", 3);
            var result = (RectTransform)Value("ResultRect"); result.sizeDelta = new Vector2(width, result.sizeDelta.y);
            Call("Refresh"); Canvas.ForceUpdateCanvases();
        }

        private Button SharingButton() => owner.GetComponentsInChildren<Button>(true).Single(x => x.name == "Image sharing");
        private TMP_Text Caption() => SharingButton().GetComponentInChildren<TMP_Text>(true);
        private bool SharesImages()
        {
            object controller = Value("Controller");
            return (bool)controller.GetType().GetProperty("IncludeImages").GetValue(controller);
        }
        private static void AssertFits(TMP_Text caption)
        {
            Assert.That(caption.GetPreferredValues(caption.text).x,
                Is.LessThanOrEqualTo(caption.rectTransform.rect.width + .5f), "The privacy state must remain visible without shrinking text.");
            Assert.That(caption.GetPreferredValues(caption.text).y,
                Is.LessThanOrEqualTo(caption.rectTransform.rect.height + .5f));
        }

        [TestCase(440f)]
        [TestCase(320f)]
        public void ImagePrivacyCaptionFitsExistingBoundsAndRealSelectionRestoresFromPreference(float width)
        {
            Create(width);
            TMP_Text initial = Caption(); float fontSize = initial.fontSize; Vector2 bounds = initial.rectTransform.rect.size;
            Assert.That(initial.text, Is.EqualTo("IMAGES ON")); Assert.That(SharesImages(), Is.True); AssertFits(initial);

            SharingButton().onClick.Invoke(); Call("Refresh"); Canvas.ForceUpdateCanvases();
            TMP_Text disabled = Caption();
            Assert.That(disabled.text, Is.EqualTo("IMAGES OFF")); Assert.That(SharesImages(), Is.False);
            Assert.That(PlayerPrefs.GetInt(Preference), Is.Zero); Assert.That(disabled.fontSize, Is.EqualTo(fontSize));
            Assert.That(disabled.rectTransform.rect.size, Is.EqualTo(bounds)); AssertFits(disabled);

            UnityEngine.Object.DestroyImmediate(owner); owner = null; Create(width);
            Assert.That(SharesImages(), Is.False); Assert.That(Caption().text, Is.EqualTo("IMAGES OFF")); AssertFits(Caption());
            SharingButton().onClick.Invoke(); Call("Refresh"); Canvas.ForceUpdateCanvases();
            Assert.That(Caption().text, Is.EqualTo("IMAGES ON")); Assert.That(PlayerPrefs.GetInt(Preference), Is.EqualTo(1));
            Assert.That(Caption().fontSize, Is.EqualTo(fontSize)); Assert.That(Caption().rectTransform.rect.size, Is.EqualTo(bounds)); AssertFits(Caption());

            object controller = Value("Controller");
            Assert.That(controller.GetType().GetProperty("Snapshot").GetValue(controller), Is.Null,
                "Opening options and choosing image privacy must not send a brief request.");
        }
    }
}
