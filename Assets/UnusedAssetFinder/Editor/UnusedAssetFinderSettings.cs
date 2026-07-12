using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UnusedAssetFinder.Editor
{
    /// <summary>
    /// スキャンの挙動を決める設定。EditorPrefs に JSON でプロジェクト単位に永続化する。
    /// （ProjectSettings に置かず EditorPrefs にするのは、ツール本体を別プロジェクトへ
    ///   コピーしても設定キーが衝突せず、リポジトリを汚さないため。）
    /// </summary>
    [System.Serializable]
    public sealed class UnusedAssetFinderSettings
    {
        // ---- ルート（使用判定の起点）に関する設定 ----

        /// <summary>Resources フォルダ配下を常に「使用中」とみなす。実行時 Resources.Load を依存解析で追えないため既定 ON。</summary>
        public bool TreatResourcesAsUsed = true;

        /// <summary>ProjectSettings/*.asset を走査して参照 GUID を起点に加える。URP/InputSystem 等の誤検出を防ぐため既定 ON。</summary>
        public bool ScanProjectSettings = true;

        /// <summary>Build Settings 未登録のシーンも起点として扱う（＝シーン自体は未使用判定しない）。
        /// false にすると未登録シーンを未使用候補として洗い出せるが、開発用シーンも候補化されるので注意。既定 ON（安全側）。</summary>
        public bool TreatAllScenesAsRoots = true;

        // ---- 除外（未使用候補から外す）に関する設定 ----

        /// <summary>スクリプト類を候補から除外。.cs はコードからの参照を依存解析で追えず誤検出するため既定 ON。</summary>
        public bool IgnoreScripts = true;

        /// <summary>パスに以下の部分文字列を含むアセットを候補から除外する。</summary>
        public List<string> IgnoredPathSubstrings = new List<string>
        {
            "/Editor/",
            "/Editor Default Resources/",
            "/Gizmos/",
        };

        /// <summary>以下の拡張子（小文字・ドット込み）を候補から除外する。</summary>
        public List<string> IgnoredExtensions = new List<string>
        {
            ".cs", ".asmdef", ".asmref", ".dll", ".rsp",
            ".cginc", ".hlsl", ".uxml", ".uss",
        };

        // ---- 永続化 ----

        private static string Key => "UnusedAssetFinder.Settings." + PlayerSettings.productGUID;

        public static UnusedAssetFinderSettings Load()
        {
            string json = EditorPrefs.GetString(Key, string.Empty);
            if (string.IsNullOrEmpty(json))
            {
                return new UnusedAssetFinderSettings();
            }

            try
            {
                var s = JsonUtility.FromJson<UnusedAssetFinderSettings>(json);
                return s ?? new UnusedAssetFinderSettings();
            }
            catch
            {
                return new UnusedAssetFinderSettings();
            }
        }

        public void Save()
        {
            EditorPrefs.SetString(Key, JsonUtility.ToJson(this));
        }
    }
}
