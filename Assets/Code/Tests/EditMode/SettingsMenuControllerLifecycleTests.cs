using System;
using System.Reflection;
using Rebaka.Settings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Rebaka.Tests.EditMode
{
    public sealed class SettingsMenuControllerLifecycleTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        [Test]
        public void OnEnable_EnablesUiActionMap()
        {
            object actions = CreateActions(out InputActionAsset asset, out InputActionMap ui);
            SettingsMenuController controller = CreateController(actions, out GameObject owner);

            try
            {
                InvokeLifecycle(controller, "OnEnable");

                Assert.That(ui.enabled, Is.True);
            }
            finally
            {
                Cleanup(controller, owner, asset, ui);
            }
        }

        [Test]
        public void OnDisable_DisablesUiActionMap()
        {
            object actions = CreateActions(out InputActionAsset asset, out InputActionMap ui);
            ui.Enable();
            SettingsMenuController controller = CreateController(actions, out GameObject owner);

            try
            {
                InvokeLifecycle(controller, "OnDisable");

                Assert.That(ui.enabled, Is.False);
            }
            finally
            {
                Cleanup(controller, owner, asset, ui);
            }
        }

        private static object CreateActions(out InputActionAsset asset, out InputActionMap ui)
        {
            FieldInfo actionsField = GetActionsField();
            object actions = Activator.CreateInstance(actionsField.FieldType);
            PropertyInfo assetProperty = actionsField.FieldType.GetProperty("asset");
            Assert.That(assetProperty, Is.Not.Null, "Generated input actions asset property was not found.");
            asset = (InputActionAsset)assetProperty.GetValue(actions);
            ui = asset.FindActionMap("UI", throwIfNotFound: true);
            return actions;
        }

        private static SettingsMenuController CreateController(
            object actions,
            out GameObject owner)
        {
            owner = new GameObject("SettingsMenuControllerLifecycleTests");
            owner.SetActive(false);
            var controller = owner.AddComponent<SettingsMenuController>();
            GetActionsField().SetValue(controller, actions);
            return controller;
        }

        private static FieldInfo GetActionsField()
        {
            FieldInfo field = typeof(SettingsMenuController).GetField("_actions", PrivateInstance);
            Assert.That(field, Is.Not.Null, "_actions field was not found.");
            return field;
        }

        private static void InvokeLifecycle(SettingsMenuController controller, string methodName)
        {
            MethodInfo method = typeof(SettingsMenuController).GetMethod(methodName, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"{methodName} was not found.");
            method.Invoke(controller, null);
        }

        private static void Cleanup(
            SettingsMenuController controller,
            GameObject owner,
            InputActionAsset asset,
            InputActionMap ui)
        {
            ui.Disable();
            GetActionsField().SetValue(controller, null);
            UnityEngine.Object.DestroyImmediate(asset);
            UnityEngine.Object.DestroyImmediate(owner);
        }
    }
}
