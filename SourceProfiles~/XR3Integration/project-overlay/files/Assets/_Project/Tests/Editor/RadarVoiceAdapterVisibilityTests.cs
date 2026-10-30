using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FAA.Customization.Tests
{
    public class RadarVoiceAdapterVisibilityTests
    {
        [TestCase(
            "VoiceControl.Adapters.WeatherRadarVoiceAdapter, Assembly-CSharp",
            "XPlaneWeatherRadarCanvas")]
        [TestCase(
            "VoiceControl.Adapters.TrafficRadarVoiceAdapter, Assembly-CSharp",
            "XPlaneTrafficRadarCanvas")]
        public void PanelCommands_HideDedicatedCanvasWithoutStoppingItsGameObject(
            string adapterAssemblyName,
            string canvasName)
        {
            Type adapterType = Type.GetType(adapterAssemblyName);
            Assert.That(adapterType, Is.Not.Null);

            GameObject canvasObject = new GameObject(canvasName, typeof(RectTransform), typeof(Canvas));
            GameObject legacyRoot = new GameObject("Inactive Legacy Radar Root");
            GameObject adapterObject = new GameObject("Radar Voice Adapter Test");

            try
            {
                Canvas canvas = canvasObject.GetComponent<Canvas>();
                Component adapter = adapterObject.AddComponent(adapterType);
                SetPrivateField(adapterType, adapter, "radarRoot", legacyRoot);
                SetPrivateField(adapterType, adapter, "_resolvedRadarCanvas", canvas);

                Assert.That(Execute(adapterType, adapter, "hide_panel"), Is.True);
                CanvasGroup group = canvasObject.GetComponent<CanvasGroup>();
                Assert.That(group, Is.Not.Null);
                Assert.That(canvasObject.activeSelf, Is.True,
                    "Hiding must leave data providers and controllers running.");
                Assert.That(canvas.enabled, Is.False);
                Assert.That(group.alpha, Is.Zero);
                Assert.That(group.interactable, Is.False);
                Assert.That(group.blocksRaycasts, Is.False);
                Assert.That(legacyRoot.activeSelf, Is.True,
                    "A stale serialized legacy root must not own live radar visibility.");

                Assert.That(Execute(adapterType, adapter, "show_panel"), Is.True);
                Assert.That(canvasObject.activeSelf, Is.True);
                Assert.That(canvas.enabled, Is.True);
                Assert.That(group.alpha, Is.GreaterThan(0f));
                Assert.That(group.interactable, Is.True);
                Assert.That(group.blocksRaycasts, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(adapterObject);
                UnityEngine.Object.DestroyImmediate(legacyRoot);
                UnityEngine.Object.DestroyImmediate(canvasObject);
            }
        }

        [Test]
        public void OwnAircraftColor_IsLimitedToWhiteCyanOrGreen()
        {
            Type adapterType = Type.GetType("VoiceControl.Adapters.TrafficRadarVoiceAdapter, Assembly-CSharp");
            Type displayType = Type.GetType("TrafficRadar.TrafficRadarDisplay, TrafficRadar");
            Assert.That(adapterType, Is.Not.Null);
            Assert.That(displayType, Is.Not.Null);
            MethodInfo allowed = adapterType.GetMethod("IsAllowedOwnAircraftColor", BindingFlags.Public | BindingFlags.Static);
            Assert.That(allowed, Is.Not.Null);
            foreach (string ok in new[] { "white", "cyan", "green" })
                Assert.That(allowed.Invoke(null, new object[] { ok }), Is.True, ok);
            // Amber/yellow/orange and red are reserved for traffic and resolution advisories (AC 20-172B).
            foreach (string reserved in new[] { "red", "orange", "yellow", "magenta", "blue", "black", "", null })
                Assert.That(allowed.Invoke(null, new object[] { reserved }), Is.False, reserved ?? "null");
            MethodInfo allowedFor = adapterType.GetMethod("IsAllowedColor", BindingFlags.Public | BindingFlags.Static);
            Assert.That(allowedFor, Is.Not.Null);
            foreach (string target in new[] { "range_ring", "compass", "background", "own_aircraft" })
                foreach (string alert in new[] { "red", "orange", "yellow" })
                    Assert.That(allowedFor.Invoke(null, new object[] { target, alert }), Is.False, target + " " + alert);
            Assert.That(allowedFor.Invoke(null, new object[] { "range_ring", "cyan" }), Is.True);
            Assert.That(allowedFor.Invoke(null, new object[] { "background", "black" }), Is.True);
            Assert.That(allowedFor.Invoke(null, new object[] { "background", "white" }), Is.False, "A light scope would wash out cyan traffic.");

            GameObject displayObject = new GameObject("Own Ship Colour Display", typeof(RectTransform));
            GameObject adapterObject = new GameObject("Own Ship Colour Adapter");
            try
            {
                Component display = displayObject.AddComponent(displayType);
                Component adapter = adapterObject.AddComponent(adapterType);
                SetPrivateField(adapterType, adapter, "display", display);
                PropertyInfo ownShip = displayType.GetProperty("OwnAircraftColor");
                Color before = (Color)ownShip.GetValue(display);
                Assert.That(Execute(adapterType, adapter, "set_own_aircraft_color", "red"), Is.False);
                Assert.That((Color)ownShip.GetValue(display), Is.EqualTo(before), "A refused colour must not be applied.");
                Assert.That(Execute(adapterType, adapter, "set_own_aircraft_color", "cyan"), Is.True);
                Assert.That((Color)ownShip.GetValue(display), Is.EqualTo(Color.cyan));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(adapterObject);
                UnityEngine.Object.DestroyImmediate(displayObject);
            }
        }

        private static bool Execute(Type adapterType, Component adapter, string commandName, string color)
        {
            MethodInfo execute = adapterType.GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.Public);
            return (bool)execute.Invoke(adapter, new object[] { commandName, new Dictionary<string, object> { ["color"] = color } });
        }

        private static bool Execute(Type adapterType, Component adapter, string commandName)
        {
            MethodInfo execute = adapterType.GetMethod("ExecuteCommand", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(execute, Is.Not.Null);
            return (bool)execute.Invoke(adapter, new object[]
            {
                commandName,
                new Dictionary<string, object>()
            });
        }

        private static void SetPrivateField(Type type, object target, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Could not locate {type.Name}.{fieldName}.");
            field.SetValue(target, value);
        }
    }
}
