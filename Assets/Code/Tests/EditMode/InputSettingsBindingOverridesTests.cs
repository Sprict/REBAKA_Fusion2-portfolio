using MyFolder.Scripts.Settings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Tests.EditMode
{
    public sealed class InputSettingsBindingOverridesTests
    {
        [Test]
        public void Restore_RestoresOverriddenJumpBinding()
        {
            var source = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionAsset target = null;

            try
            {
                InputAction sourceJump = source.AddActionMap("Player").AddAction("Jump", InputActionType.Button);
                sourceJump.AddBinding("<Keyboard>/space");
                string actionAssetJson = source.ToJson();

                sourceJump.ApplyBindingOverride(0, "<Keyboard>/j");
                string overridesJson = InputSettingsBindingOverrides.Serialize(source);

                target = InputActionAsset.FromJson(actionAssetJson);
                InputAction targetJump = target.FindAction("Player/Jump", throwIfNotFound: true);
                InputSettingsBindingOverrides.Restore(target, overridesJson);

                Assert.That(targetJump.bindings[0].effectivePath, Is.EqualTo("<Keyboard>/j"));
            }
            finally
            {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
            }
        }
    }
}