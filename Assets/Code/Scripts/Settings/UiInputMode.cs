using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Scripts.Settings
{
    /// <summary>
    /// UI の Select 表示（ゲームパッド／キーボード用カーソル）を出すべきかを追跡する。
    ///
    /// マウス操作中に Select 状態が残ると、ホバー色と Select 色を同色にしている本作では
    /// 「ホバーしていないのに光っているボタン」に見えて混乱する。そこで
    /// Navigate 系入力（スティック / D-Pad / WASD / 矢印キー / 決定ボタン）が始まったら表示、
    /// ポインタ入力（マウス移動・クリック・ホイール）が始まったら非表示に切り替える。
    /// SettingsMenuController と LobbyMenuUi の両方から毎フレーム Poll() を呼ぶ
    /// （同一フレームの二重 Poll はフレーム番号でガード）。
    /// </summary>
    public static class UiInputMode
    {
        // 生のスティック値を読むため Input System のデッドゾーン処理を通らない。自前の閾値を持つ。
        private const float StickThresholdSqr = 0.0625f; // 0.25^2
        private const float MouseMoveThresholdSqr = 4f;  // 2px^2

        private static int _lastPollFrame = -1;

        /// <summary>true のとき EventSystem の Select 状態を UI に見せてよい</summary>
        public static bool NavigationSelectionVisible { get; private set; }

        public static void Poll()
        {
            if (Time.frameCount == _lastPollFrame)
                return;
            _lastPollFrame = Time.frameCount;

            // 同一フレームで両方来たら Navigate 優先（選択操作の意図を尊重する）
            if (DetectNavigationInput())
                NavigationSelectionVisible = true;
            else if (DetectPointerInput())
                NavigationSelectionVisible = false;
        }

        private static bool DetectNavigationInput()
        {
            Gamepad gamepad = Gamepad.current;
            if (gamepad != null)
            {
                if (gamepad.leftStick.ReadValue().sqrMagnitude > StickThresholdSqr
                    || gamepad.rightStick.ReadValue().sqrMagnitude > StickThresholdSqr
                    || gamepad.dpad.ReadValue().sqrMagnitude > 0.25f
                    || gamepad.buttonSouth.wasPressedThisFrame
                    || gamepad.buttonEast.wasPressedThisFrame)
                    return true;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                // UI/Navigate のキーボードバインディング（WASD＋矢印）に合わせる
                if (keyboard.wKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame
                    || keyboard.sKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame
                    || keyboard.upArrowKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame
                    || keyboard.leftArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame)
                    return true;
            }

            return false;
        }

        private static bool DetectPointerInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
                return false;

            if (mouse.delta.ReadValue().sqrMagnitude > MouseMoveThresholdSqr)
                return true;
            if (mouse.leftButton.wasPressedThisFrame
                || mouse.rightButton.wasPressedThisFrame
                || mouse.middleButton.wasPressedThisFrame)
                return true;
            if (Mathf.Abs(mouse.scroll.ReadValue().y) > 0.01f)
                return true;

            return false;
        }
    }
}
