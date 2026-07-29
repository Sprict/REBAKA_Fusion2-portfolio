using System;
using System.Globalization;
using UnityEngine.InputSystem;

namespace MyFolder.Scripts.Settings
{
    public enum GameplayDeviceGroup
    {
        KeyboardMouse,
        Gamepad,
    }

    /// <summary>
    /// 入力設定のデータを保持するクラス
    /// マウスとゲームパッドの感度（ルック・移動）を保存・管理します。
    /// </summary>
    [Serializable]
    public sealed class InputSettingsData
    {
        // スライダーは常用域（0.1〜10.0、0.1刻み）、数値入力は全域（0.01〜10.00、0.01刻み）。
        // レートベース化後は感度1.0が旧8.0相当のため、上限10で十分（2026-07-12）。
        public const float MinimumSensitivity = 0.01f;
        public const float MaximumSensitivity = 10f;
        public const float SliderMinimumSensitivity = 0.1f;
        public const float SliderMaximumSensitivity = 10f;
        public const float DefaultSensitivity = 1f;

        public float MouseLookX = DefaultSensitivity;
        public float MouseLookY = DefaultSensitivity;
        public float GamepadLookX = DefaultSensitivity;
        public float GamepadLookY = DefaultSensitivity;
        public float GamepadMoveX = DefaultSensitivity;
        public float GamepadMoveY = DefaultSensitivity;
        public bool KeyboardMouseEnabled = true;
        public bool GamepadEnabled = true;

        public void Normalize()
        {
            MouseLookX = NormalizeAxis(MouseLookX);
            MouseLookY = NormalizeAxis(MouseLookY);
            GamepadLookX = NormalizeAxis(GamepadLookX);
            GamepadLookY = NormalizeAxis(GamepadLookY);
            GamepadMoveX = NormalizeAxis(GamepadMoveX);
            GamepadMoveY = NormalizeAxis(GamepadMoveY);

            if (!KeyboardMouseEnabled && !GamepadEnabled)
            {
                KeyboardMouseEnabled = true;
                GamepadEnabled = true;
            }
        }

        public bool TrySetDeviceEnabled(GameplayDeviceGroup group, bool enabled)
        {
            bool keyboardMouse = group == GameplayDeviceGroup.KeyboardMouse ? enabled : KeyboardMouseEnabled;
            bool gamepad = group == GameplayDeviceGroup.Gamepad ? enabled : GamepadEnabled;

            if (!keyboardMouse && !gamepad)
                return false;

            switch (group)
            {
                case GameplayDeviceGroup.KeyboardMouse:
                    KeyboardMouseEnabled = enabled;
                    break;
                case GameplayDeviceGroup.Gamepad:
                    GamepadEnabled = enabled;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(group), group, null);
            }

            return true;
        }
        private static float NormalizeAxis(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                return DefaultSensitivity;

            return UnityEngine.Mathf.Clamp(value, MinimumSensitivity, MaximumSensitivity);
        }
    }

    /// <summary>
    /// 入力設定（感度）を実際にInput Systemに適用するためのユーティリティ
    /// </summary>
    public static class InputSettingsProcessor
    {
        private const string PlayerLookAction = "Player/Look";
        private const string PlayerMoveAction = "Player/Move";
        private const string MouseDeltaPath = "<Pointer>/delta";
        private const string GamepadRightStickPath = "<Gamepad>/rightStick";
        private const string GamepadLeftStickPath = "<Gamepad>/leftStick";

        public static void Apply(InputActionAsset inputActions, InputSettingsData settings)
        {
            if (inputActions == null)
                throw new ArgumentNullException(nameof(inputActions));

            settings ??= new InputSettingsData();
            settings.Normalize();
            InputActionMap player = inputActions.FindActionMap("Player", throwIfNotFound: false);
            if (player != null)
            {
                if (settings.KeyboardMouseEnabled && settings.GamepadEnabled)
                    player.bindingMask = null;
                else
                    player.bindingMask = new InputBinding
                    {
                        groups = settings.KeyboardMouseEnabled ? "Keyboard&Mouse" : "Gamepad",
                    };
            }

            ApplyScale(inputActions.FindAction(PlayerLookAction, throwIfNotFound: false),
                MouseDeltaPath, settings.MouseLookX, settings.MouseLookY);
            // overrideProcessors は authored を置き換えるため、レート→デルタ変換を必ずチェーンする。
            ApplyScale(inputActions.FindAction(PlayerLookAction, throwIfNotFound: false),
                GamepadRightStickPath, settings.GamepadLookX, settings.GamepadLookY,
                prependProcessors: CreateStickToLookDeltaProcessor());
            ApplyScale(inputActions.FindAction(PlayerMoveAction, throwIfNotFound: false),
                GamepadLeftStickPath, settings.GamepadMoveX, settings.GamepadMoveY);
        }

        private static void ApplyScale(
            InputAction action,
            string bindingPath,
            float x,
            float y,
            string prependProcessors = null)
        {
            if (action == null)
                return;

            for (int index = 0; index < action.bindings.Count; index++)
            {
                InputBinding binding = action.bindings[index];
                if (!string.Equals(binding.path, bindingPath, StringComparison.Ordinal))
                    continue;

                string scale = CreateScaleVector2Processor(x, y);
                binding.overrideProcessors = string.IsNullOrEmpty(prependProcessors)
                    ? scale
                    : prependProcessors + "," + scale;
                action.ApplyBindingOverride(index, binding);
            }
        }

        private static string CreateStickToLookDeltaProcessor()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "stickToLookDelta(unitsPerSecond={0:0.###})",
                StickToLookDeltaProcessor.DefaultUnitsPerSecond);
        }

        private static string CreateScaleVector2Processor(float x, float y)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "scaleVector2(x={0:0.###},y={1:0.###})",
                x,
                y);
        }
    }

    /// <summary>
    /// 入力バインディングのオーバーライド（再割り当て）を保存・復元するためのユーティリティ
    /// </summary>
    public static class InputSettingsBindingOverrides
    {
        public static string Serialize(InputActionAsset inputActions)
        {
            if (inputActions == null)
                throw new ArgumentNullException(nameof(inputActions));

            return inputActions.SaveBindingOverridesAsJson();
        }

        public static void Restore(InputActionAsset inputActions, string overridesJson)
        {
            if (inputActions == null)
                throw new ArgumentNullException(nameof(inputActions));

            inputActions.RemoveAllBindingOverrides();

            if (!string.IsNullOrWhiteSpace(overridesJson))
                inputActions.LoadBindingOverridesFromJson(overridesJson);
        }
    }
}
