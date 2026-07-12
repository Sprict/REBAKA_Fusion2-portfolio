using UnityEngine;
using UnityEditor;
using Fusion;
using System.IO;

namespace MyFolder.Editor
{
    /// <summary>
    /// Fusion の NetworkProjectConfig を JSON TextAsset としてベイクする。
    /// MPPM 仮想プレイヤーでは .fusion カスタムインポーターが動作しないため、
    /// JSON テキストファイルとして Resources に配置することで確実にロードできるようにする。
    ///
    /// Menu: Tools > Fusion > Bake Config for MPPM
    ///
    /// 使い方:
    ///   1. ホスト側（メインエディタ）でこのメニューを実行
    ///   2. Assets/Resources/NetworkProjectConfigBackup.json が生成される
    ///   3. MPPM 仮想プレイヤーが SessionManager 経由でこの JSON を読み込む
    /// </summary>
    public static class BakeFusionConfig
    {
        private const string OutputDir = "Assets/Resources";
        public const string OutputPath = "Assets/Resources/NetworkProjectConfigBackup.json";
        public const string ConfigAssetPath = "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion";

        [MenuItem("Tools/Fusion/Bake Config for MPPM")]
        public static void Bake()
        {
            NetworkProjectConfig config = null;

            // 通常パスでロード
            try
            {
                config = NetworkProjectConfig.Global;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[BakeFusionConfig] NetworkProjectConfig.Global failed: {e.Message}");
            }

            // 手動ロード
            if (config == null)
            {
                var asset = Resources.Load<NetworkProjectConfigAsset>("NetworkProjectConfig");
                if (asset != null)
                {
                    config = asset.Config;
                }
            }

            if (config == null)
            {
                Debug.LogError("[BakeFusionConfig] Could not load NetworkProjectConfig from any source. " +
                               "Ensure Fusion is properly installed.");
                return;
            }

            // Resources フォルダを確保
            if (!AssetDatabase.IsValidFolder(OutputDir))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }

            // JSON としてシリアライズして保存
            string json = JsonUtility.ToJson(config, true);
            File.WriteAllText(OutputPath, json);
            AssetDatabase.Refresh();

            Debug.Log($"[BakeFusionConfig] Baked NetworkProjectConfig to {OutputPath} ({json.Length} chars).\n" +
                      "MPPM virtual players can now load this config as a fallback.");
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="configAssetPath"></param>
        /// <param name="backupPath"></param>
        /// <returns>configAssetPathの最終更新日時がbackupPathよりも新しいかどうか</returns>
        public static bool ShouldBakeForMppm(string configAssetPath, string backupPath)
        {
            if (!File.Exists(backupPath))
                return true;

            if (!File.Exists(configAssetPath))
                return false;

            return File.GetLastWriteTimeUtc(configAssetPath) > File.GetLastWriteTimeUtc(backupPath);
        }

        /// <summary>
        /// Play Mode に入る前に自動ベイクする（MPPM使用時の利便性向上）
        /// </summary>
        [InitializeOnLoadMethod] // 💡Unityエディタ起動・再コンパイル直後に自動実行させる属性
        private static void AutoBakeOnPlayMode()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                {
                    if (ShouldBakeForMppm(ConfigAssetPath, OutputPath))
                    {
                        Debug.Log("[BakeFusionConfig] Auto-baking config for MPPM because the backup is missing or stale...");
                        Bake();
                    }
                }
            };
        }
    }
}

