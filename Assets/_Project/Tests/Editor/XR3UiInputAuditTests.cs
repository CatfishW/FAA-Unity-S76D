using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FAA.Customization.Tests
{
    public sealed class XR3UiInputAuditTests
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Type T(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var type = assembly.GetType(name); if (type != null) return type; }
            throw new InvalidOperationException(name);
        }
        private static Type Audit => T("FAA.Headset.XR3UiInputAudit");
        private static readonly string[] Events = { "pointerDown", "pointerUp", "pointerClick", "finalizeRaycastResults" };
        private static int Handlers(object module, object audit, string name)
        {
            var field = T("UnityEngine.XR.Interaction.Toolkit.UI.UIInputModule").GetField(name, Any);
            var listeners = field.GetValue(module) as Delegate; int count = 0;
            if (listeners != null) foreach (Delegate listener in listeners.GetInvocationList()) if (ReferenceEquals(listener.Target, audit)) count++;
            return count;
        }
        private static void Bind(Component audit, object module) => Audit.GetMethod("BindModule").Invoke(audit, new[] { module });
        private static Component Module(GameObject host)
        {
            host.SetActive(false); host.AddComponent<EventSystem>();
            return host.AddComponent(T("UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule"));
        }

        [TestCase("--xr3-ui-audit", true)]
        [TestCase("--XR3-UI-AUDIT", true)]
        [TestCase("--xr3-ui-audit=false", false)]
        [TestCase("xr3-ui-audit", false)]
        public void AuditRequiresItsExactOptInArgument(string argument, bool expected) =>
            Assert.That(Audit.GetMethod("RequestsAudit").Invoke(null, new object[] { new[] { argument } }), Is.EqualTo(expected));

        [Test]
        public void DefaultOffFactoryCreatesNoHostAndAnUnrequestedComponentCannotSubscribe()
        {
            int before = Resources.FindObjectsOfTypeAll(Audit).Length;
            Assert.That(Audit.GetMethod("CreateIfRequested").Invoke(null, new object[] { Array.Empty<string>() }), Is.Null);
            Assert.That(Audit.GetMethod("CreateIfRequested").Invoke(null, new object[] { null }), Is.Null);
            Assert.That(Resources.FindObjectsOfTypeAll(Audit).Length, Is.EqualTo(before));
            var host = new GameObject("Unrequested audit"); var moduleHost = new GameObject("Audit test module");
            try
            {
                Component audit = host.AddComponent(Audit), module = Module(moduleHost); Bind(audit, module);
                Assert.That(Audit.GetProperty("BoundModule").GetValue(audit), Is.Null);
                foreach (string name in Events) Assert.That(Handlers(module, audit, name), Is.Zero, name);
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(moduleHost); }
        }

        [Test]
        public void ChangingModulesAndDisablingDetachesAllGlobalObserversWithoutDuplicatingThem()
        {
            Component audit = (Component)Audit.GetMethod("CreateIfRequested").Invoke(null, new object[] { new[] { "--xr3-ui-audit" } });
            var a = new GameObject("UI module A"); var b = new GameObject("UI module B");
            try
            {
                Component first = Module(a), second = Module(b);
                Bind(audit, first); Bind(audit, first);
                foreach (string name in Events) Assert.That(Handlers(first, audit, name), Is.EqualTo(1), name);
                Bind(audit, second);
                foreach (string name in Events)
                {
                    Assert.That(Handlers(first, audit, name), Is.Zero, "old " + name);
                    Assert.That(Handlers(second, audit, name), Is.EqualTo(1), "new " + name);
                }
                ((Behaviour)audit).enabled = false;
                // EditMode does not run non-ExecuteAlways lifecycle callbacks consistently.
                Audit.GetMethod("OnDisable", Any).Invoke(audit, null);
                Assert.That(Audit.GetProperty("BoundModule").GetValue(audit), Is.Null);
                foreach (string name in Events) Assert.That(Handlers(second, audit, name), Is.Zero, name);
            }
            finally
            {
                Bind(audit, null); UnityEngine.Object.DestroyImmediate(audit.gameObject);
                UnityEngine.Object.DestroyImmediate(a); UnityEngine.Object.DestroyImmediate(b);
            }
        }
    }
}
