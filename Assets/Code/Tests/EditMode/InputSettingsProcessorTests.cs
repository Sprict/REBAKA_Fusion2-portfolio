using MyFolder.Scripts.Settings;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Tests.EditMode
{
    public sealed class InputSettingsProcessorTests
    {
        [Test]
        public void NewSettings_EnableBothGameplayDeviceGroups()
        {
            var settings = new InputSettingsData();

            Assert.That(settings.KeyboardMouseEnabled, Is.True);
            Assert.That(settings.GamepadEnabled, Is.True);
        }

        [Test]
        public void Normalize_WhenBothDeviceGroupsAreDisabled_RecoversBoth()
        {
            var settings = new InputSettingsData
            {
                KeyboardMouseEnabled = false,
                GamepadEnabled = false,
            };

            settings.Normalize();

            Assert.That(settings.KeyboardMouseEnabled, Is.True);
            Assert.That(settings.GamepadEnabled, Is.True);
        }

        [Test]
        public void TrySetDeviceEnabled_RejectsDisablingTheLastDeviceGroup()
        {
            var settings = new InputSettingsData
            {
                KeyboardMouseEnabled = true,
                GamepadEnabled = false,
            };

            bool changed = settings.TrySetDeviceEnabled(GameplayDeviceGroup.KeyboardMouse, false);

            Assert.That(changed, Is.False);
            Assert.That(settings.KeyboardMouseEnabled, Is.True);
            Assert.That(settings.GamepadEnabled, Is.False);
        }
        [Test]
        public void SensitivityRange_SliderIsCoarseSubsetOfAbsoluteRange()
        {
            // スライダー=常用域（0.1〜10.0）、数値入力=全域（0.01〜10.00）。
            // レートベース化後は感度1.0が旧8.0相当のため上限10で十分（2026-07-12）。
            Assert.That(InputSettingsData.MinimumSensitivity, Is.EqualTo(0.01f));
            Assert.That(InputSettingsData.MaximumSensitivity, Is.EqualTo(10f));
            Assert.That(InputSettingsData.SliderMinimumSensitivity, Is.EqualTo(0.1f));
            Assert.That(InputSettingsData.SliderMaximumSensitivity, Is.EqualTo(10f));
            Assert.That(InputSettingsData.SliderMinimumSensitivity,
                Is.GreaterThanOrEqualTo(InputSettingsData.MinimumSensitivity));
            Assert.That(InputSettingsData.SliderMaximumSensitivity,
                Is.LessThanOrEqualTo(InputSettingsData.MaximumSensitivity));
        }

        [Test]
        public void SensitivitySliderUnits_MapOneGamepadStepToPointOneSensitivity()
        {
            // Unity Slider は wholeNumbers 時に±1単位。内部を0.1刻み整数にすれば左右1回=感度0.1。
            Assert.That(SettingsMenuController.ToSensitivitySliderUnits(0.1f), Is.EqualTo(1f));
            Assert.That(SettingsMenuController.ToSensitivitySliderUnits(1.0f), Is.EqualTo(10f));
            Assert.That(SettingsMenuController.ToSensitivitySliderUnits(10f), Is.EqualTo(100f));
            Assert.That(SettingsMenuController.FromSensitivitySliderUnits(1f), Is.EqualTo(0.1f).Within(0.0001f));
            Assert.That(SettingsMenuController.FromSensitivitySliderUnits(11f), Is.EqualTo(1.1f).Within(0.0001f));
        }

        [Test]
        public void Normalize_ClampsEveryAxisAndReplacesNonFiniteValues()
        {
            var settings = new InputSettingsData
            {
                MouseLookX = -5f,
                MouseLookY = float.PositiveInfinity,
                GamepadLookX = 99f,
                GamepadLookY = float.NaN,
                GamepadMoveX = 0f,
                GamepadMoveY = 1.5f,
            };

            settings.Normalize();

            Assert.That(settings.MouseLookX, Is.EqualTo(InputSettingsData.MinimumSensitivity));
            Assert.That(settings.MouseLookY, Is.EqualTo(InputSettingsData.DefaultSensitivity));
            Assert.That(settings.GamepadLookX, Is.EqualTo(InputSettingsData.MaximumSensitivity));
            Assert.That(settings.GamepadLookY, Is.EqualTo(InputSettingsData.DefaultSensitivity));
            Assert.That(settings.GamepadMoveX, Is.EqualTo(InputSettingsData.MinimumSensitivity));
            Assert.That(settings.GamepadMoveY, Is.EqualTo(1.5f));
        }

        [Test]
        public void Apply_SetsPlayerBindingMaskAccordingToEnabledDeviceGroups()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                InputActionMap player = asset.AddActionMap("Player");
                player.AddAction("Look", InputActionType.Value);

                var settings = new InputSettingsData
                {
                    KeyboardMouseEnabled = true,
                    GamepadEnabled = false,
                };
                InputSettingsProcessor.Apply(asset, settings);
                Assert.That(player.bindingMask?.groups, Is.EqualTo("Keyboard&Mouse"));

                settings.KeyboardMouseEnabled = false;
                settings.GamepadEnabled = true;
                InputSettingsProcessor.Apply(asset, settings);
                Assert.That(player.bindingMask?.groups, Is.EqualTo("Gamepad"));

                settings.KeyboardMouseEnabled = true;
                settings.GamepadEnabled = true;
                InputSettingsProcessor.Apply(asset, settings);
                Assert.That(player.bindingMask, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ApplyProcessors_UsesIndependentAxesForMouseAndGamepadBindings()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                InputActionMap player = asset.AddActionMap("Player");
                InputAction look = player.AddAction("Look", InputActionType.Value);
                look.AddBinding("<Pointer>/delta");
                look.AddBinding("<Gamepad>/rightStick");

                InputAction move = player.AddAction("Move", InputActionType.Value);
                move.AddBinding("<Gamepad>/leftStick");
                move.AddBinding("<Keyboard>/w");

                var settings = new InputSettingsData
                {
                    MouseLookX = 1.25f,
                    MouseLookY = 0.75f,
                    GamepadLookX = 1.5f,
                    GamepadLookY = 0.5f,
                    GamepadMoveX = 0.8f,
                    GamepadMoveY = 1.2f,
                };

                InputSettingsProcessor.Apply(asset, settings);

                Assert.That(look.bindings[0].overrideProcessors, Is.EqualTo("scaleVector2(x=1.25,y=0.75)"));
                Assert.That(look.bindings[1].overrideProcessors,
                    Is.EqualTo("stickToLookDelta(unitsPerSecond=480),scaleVector2(x=1.5,y=0.5)"));
                Assert.That(move.bindings[0].overrideProcessors, Is.EqualTo("scaleVector2(x=0.8,y=1.2)"));
                Assert.That(move.bindings[1].overrideProcessors, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void StickToLookDelta_IsFrameRateIndependent()
        {
            // 同じ0.5秒・フル倒しなら、60fps相当でも144fps相当でも合計デルタが一致する
            Vector2 sum60 = Vector2.zero;
            for (int i = 0; i < 30; i++)
                sum60 += StickToLookDeltaProcessor.Convert(Vector2.right, 480f, 1f / 60f);
            Vector2 sum144 = Vector2.zero;
            for (int i = 0; i < 72; i++)
                sum144 += StickToLookDeltaProcessor.Convert(Vector2.right, 480f, 1f / 144f);

            Assert.That(sum60.x, Is.EqualTo(240f).Within(0.001f));
            Assert.That(sum144.x, Is.EqualTo(sum60.x).Within(0.001f));
        }
    }
}
