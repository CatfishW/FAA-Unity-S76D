using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FAA.Customization.Tests
{
    /// <summary>Exercise the Settings factory's real slider and callback, not a direct size setter.</summary>
    public sealed class FaaInstrumentSliderTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type Workspace => Type.GetType("FAA.Customization.FaaSpatialWorkspace, Assembly-CSharp", true);

        [TestCase(0f)]
        [TestCase(20000f)]
        public void FactorySliderPointerDragChangesSelectedModuleAndCaption(float worldDistance)
        {
            var workspaceHost = new GameObject("Inactive slider workspace fixture");
            workspaceHost.SetActive(false);
            var canvasOwner = new GameObject("Active slider UI fixture");
            var cameraHost = new GameObject("Slider mono eye", typeof(Camera));
            var eventsHost = new GameObject("Slider pointer events", typeof(EventSystem));
            var moduleHost = new GameObject("Selected airspeed module", typeof(RectTransform));
            var texture = new RenderTexture(1600, 900, 0);
            Component workspace = null;
            try
            {
                Camera view = cameraHost.GetComponent<Camera>();
                view.transform.position = Vector3.forward * worldDistance;
                view.targetTexture = texture;
                view.fieldOfView = 60f;
                view.aspect = 1600f / 900f;
                workspace = workspaceHost.AddComponent(Workspace);
                Workspace.GetProperty("PersistChanges").SetValue(workspace, false);
                Workspace.GetProperty("View", Any).SetValue(workspace, view);
                Type targetType = Type.GetType("FAA.Customization.FaaNonConformalScaleTarget, Assembly-CSharp", true);
                object module = Activator.CreateInstance(targetType,
                    new object[] { "airspeed", "Airspeed", moduleHost.transform, 1f, 0f });
                ((IList)Workspace.GetField("modules", Any).GetValue(workspace)).Add(module);
                Workspace.GetMethod("BuildControls", Any).Invoke(workspace, null);
                Canvas canvas = (Canvas)Workspace.GetProperty("SettingsCanvas").GetValue(workspace);
                canvas.transform.SetParent(canvasOwner.transform, false);
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = view;
                var canvasRect = (RectTransform)canvas.transform;
                canvasRect.sizeDelta = new Vector2(1920f, 1080f);
                canvas.transform.position = view.transform.position + Vector3.forward * 2f;
                canvas.transform.localScale = Vector3.one * .001f;
                Workspace.GetMethod("OpenMenu").Invoke(workspace, null);
                Slider slider = (Slider)Workspace.GetField("scaleSlider", Any).GetValue(workspace);
                GraphicRaycaster raycaster = canvas.GetComponent<GraphicRaycaster>();
                typeof(GraphicRaycaster).GetMethod("OnEnable", Any).Invoke(raycaster, null);
                Canvas.ForceUpdateCanvases();
                view.Render();
                Assert.That(slider.IsActive() && slider.IsInteractable(), Is.True);
                Assert.That(slider.value, Is.EqualTo(1f).Within(.001f));
                RectTransform track = (RectTransform)slider.transform;
                Vector2 start = view.WorldToScreenPoint(slider.handleRect.position);
                Vector2 end = view.WorldToScreenPoint(track.TransformPoint(new Vector3(track.rect.xMin + track.rect.width * .7f, 0f, 0f)));
                var pointer = new PointerEventData(eventsHost.GetComponent<EventSystem>())
                { pointerId = 0, position = start, button = PointerEventData.InputButton.Left };
                var hits = new List<RaycastResult>();
                raycaster.Raycast(pointer, hits);
                Assert.That(hits.Count, Is.GreaterThan(0), "The real factory track must be pointer-hit-testable.");
                RaycastResult hit = hits.Find(h => h.gameObject == slider.gameObject);
                Assert.That(hit.gameObject, Is.SameAs(slider.gameObject));
                pointer.pointerCurrentRaycast = pointer.pointerPressRaycast = hit;
                slider.OnPointerDown(pointer);
                slider.OnInitializePotentialDrag(pointer);
                pointer.position = end;
                pointer.delta = end - start;
                slider.OnDrag(pointer);
                slider.OnPointerUp(pointer);
                object entry = Workspace.GetMethod("GetEntry").Invoke(workspace, new object[] { "airspeed" });
                float size = (float)entry.GetType().GetField("scale").GetValue(entry);
                Assert.That(size, Is.GreaterThan(1.15f), "Held pointer movement must reach the registered size callback.");
                Assert.That(slider.value, Is.EqualTo(size).Within(.001f));
                TMP_Text caption = (TMP_Text)Workspace.GetField("selectedCaption", Any).GetValue(workspace);
                Assert.That(caption.text, Does.Contain(Mathf.RoundToInt(size * 100f) + "%"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(canvasOwner);
                UnityEngine.Object.DestroyImmediate(workspaceHost);
                UnityEngine.Object.DestroyImmediate(cameraHost);
                UnityEngine.Object.DestroyImmediate(eventsHost);
                UnityEngine.Object.DestroyImmediate(moduleHost);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
