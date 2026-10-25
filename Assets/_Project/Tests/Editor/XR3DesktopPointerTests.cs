using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System;

public class XR3DesktopPointerTests
{
    private GameObject head, desk, hud, events, button;
    private RenderTexture headTexture, deskTexture;
    private Camera h, d;
    private GraphicRaycaster original;
    private Component adapter;
    private static Type Adapter => Type.GetType("FAA.Headset.XR3DesktopCanvasRaycaster, Assembly-CSharp", true);
    private void Raycast(PointerEventData p) => Adapter.GetMethod("Raycast").Invoke(adapter, new object[] { p, results });
    private readonly List<RaycastResult> results = new List<RaycastResult>();

    [SetUp] public void SetUp()
    {
        head = new GameObject("Headset", typeof(Camera)); h = head.GetComponent<Camera>();
        desk = new GameObject("Desktop", typeof(Camera)); d = desk.GetComponent<Camera>();
        headTexture = new RenderTexture(3840, 2160, 0); h.targetTexture = headTexture;
        deskTexture = new RenderTexture(1280, 720, 0); d.targetTexture = deskTexture;
        h.fieldOfView = 96; h.aspect = 1.3f;
        Matrix4x4 projection = h.projectionMatrix; projection.m02 = .35f; h.projectionMatrix = projection;
        Type.GetType("FAA.Headset.XR3DesktopHudPreview, Assembly-CSharp", true).GetMethod("ConfigureCamera").Invoke(null, new object[] { d, h, new Vector2(1280, 720) });
        hud = new GameObject("HUD", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        Type.GetType("FAA.Headset.XR3MixedRealityPresentation, Assembly-CSharp", true).GetMethod("FitHeadlockedCanvas").Invoke(null, new object[] { hud.GetComponent<Canvas>(), h, null });
        original = hud.GetComponent<GraphicRaycaster>();
        adapter = hud.AddComponent(Adapter); Adapter.GetMethod("Bind").Invoke(adapter, new object[] { original, d });
        button = new GameObject("Button", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        var rect = (RectTransform)button.transform; rect.SetParent(hud.transform, false);
        rect.anchorMin = rect.anchorMax = new Vector2(.15f, .05f); rect.sizeDelta = new Vector2(140, 40);
        events = new GameObject("Events", typeof(EventSystem));
        hud.layer = button.layer = 30;
        Canvas.ForceUpdateCanvases(); d.Render();
    }
    [TearDown] public void TearDown()
    {
        UnityEngine.Object.DestroyImmediate(hud); UnityEngine.Object.DestroyImmediate(head); UnityEngine.Object.DestroyImmediate(desk);
        UnityEngine.Object.DestroyImmediate(events); UnityEngine.Object.DestroyImmediate(headTexture); UnityEngine.Object.DestroyImmediate(deskTexture);
    }
    private PointerEventData Pointer(Camera camera, int id = 1) => new PointerEventData(events.GetComponent<EventSystem>())
        { position = camera.WorldToScreenPoint(button.transform.position), pointerId = id };

    [Test] public void VisibleDesktopButtonUsesDesktopProjectionAndPreservesHeadsetCamera()
    {
        Raycast(Pointer(d));
        Assert.That(results.Exists(r => r.gameObject == button), Is.True);
        Assert.That(Adapter.GetProperty("eventCamera").GetValue(adapter), Is.SameAs(d));
        Assert.That(hud.GetComponent<Canvas>().worldCamera, Is.SameAs(h));
        Assert.That(original.enabled, Is.False, "Avoid a second hit using the headset projection.");
        results.Clear(); original.Raycast(Pointer(d), results);
        Assert.That(results.Exists(r => r.gameObject == button), Is.False, "Regression reproduces the old projection mismatch.");
    }
    [Test] public void CanvasGroupAndRaycastTargetFiltersRemainEffective()
    {
        var group = button.AddComponent<CanvasGroup>(); group.blocksRaycasts = false;
        Raycast(Pointer(d)); Assert.That(results, Is.Empty);
        group.blocksRaycasts = true; button.GetComponent<Image>().raycastTarget = false;
        Raycast(Pointer(d)); Assert.That(results, Is.Empty);
    }
    [Test] public void WorkspaceHandPointerRetainsItsOriginalHeadsetRaycaster()
    {
        Raycast(Pointer(h, -2200));
        Assert.That(results.Exists(r => r.gameObject == button && r.module == original), Is.True);
        Assert.That(results[0].module.eventCamera, Is.SameAs(h));
    }
    [Test] public void RestoringAdapterRestoresOriginalRaycaster()
    {
        Adapter.GetMethod("Restore").Invoke(adapter, null); Assert.That(original.enabled, Is.True);
    }
    [Test] public void DesktopClickDispatchesTheRealButtonActionOnce()
    {
        int clicks = 0; button.GetComponent<Button>().onClick.AddListener(() => clicks++);
        var pointer = Pointer(d); Raycast(pointer);
        Assert.That(results.Count, Is.EqualTo(1));
        pointer.button = PointerEventData.InputButton.Left;
        ExecuteEvents.ExecuteHierarchy(results[0].gameObject, pointer, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.ExecuteHierarchy(results[0].gameObject, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.ExecuteHierarchy(results[0].gameObject, pointer, ExecuteEvents.pointerClickHandler);
        Assert.That(clicks, Is.EqualTo(1));
    }
}
