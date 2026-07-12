using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Scripts.Utils
{
    /// <summary>
    /// ParrelSync（同一マシン上の2プロセス検証）専用の入力デバイス制限ユーティリティ。
    /// 制限しないと、メイン/クローン双方が接続中の全キーボード・全マウス・全Gamepadを
    /// 無差別に拾ってしまい、2人同時操作の検証ができない
    /// （綱引き系バグはボタン押下の継続が必須で、フォーカス切り替えでは再現できない）。
    /// メイン=Keyboard+Mouse固定、クローン=Gamepad固定にする。Editor限定・本番ビルドには影響しない。
    ///
    /// 入力を読む InputActionAsset のインスタンスは InputCollector と OrbitCamera が
    /// それぞれ独立に生成するため、両方に同じ制限を適用する必要がある。
    /// </summary>
    public static class ParrelSyncInputUtil
    {
        /// <summary>
        /// ParrelSync.ClonesManager.IsClone() は Editor 専用アセンブリのため、ランタイム
        /// アセンブリ（MyProject.Scripts）から直接参照するとビルド対象プラットフォームで
        /// 解決できなくなる。ClonesManager.IsClone() 自身の実装（プロジェクトルート直下の
        /// 空ファイル ".clone" の有無を見るだけ）をここで再現し、アセンブリ参照を避ける。
        /// </summary>
        public static bool IsCloneProject()
        {
#if UNITY_EDITOR
            string projectRoot = Application.dataPath.Replace("/Assets", "");
            string cloneMarkerPath = System.IO.Path.Combine(projectRoot, ".clone");
            return System.IO.File.Exists(cloneMarkerPath);
#else
            return false;
#endif
        }

        /// <summary>
        /// 【Editor限定】
        /// UnityEditorではキーボード＆マウス入力のみ受け付ける。
        /// UnityEditor(Clone)ではゲームパッド入力のみ受け付ける。
        /// </summary>
        /// <param name="inputActions"></param>
        /// <returns></returns>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        public static void RestrictDevices(InputActionAsset inputActions)
        {
#if UNITY_EDITOR
            if (inputActions == null)
                return;

            if (IsCloneProject())
            {
                if (Gamepad.current != null)
                    inputActions.devices = new[] { (InputDevice)Gamepad.current };
            }
            else
            {
                var devices = new List<InputDevice>();
                if (Keyboard.current != null)
                    devices.Add(Keyboard.current);
                if (Mouse.current != null)
                    devices.Add(Mouse.current);
                if (devices.Count > 0)
                    inputActions.devices = devices.ToArray();
            }
#endif
        }
    }
}
