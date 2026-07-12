using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MyFolder.Editor.Preflight
{
    /// <summary>
    /// チェック#1: NetworkProjectConfig.fusion が正本パスに1つだけ存在するか。
    /// 過去事故: stray config による weave 全滅（2026-06-19、症状 "has not been weaved"、診断に丸1日）。
    /// </summary>
    public sealed class ConfigUniquenessCheck : IPreflightCheck
    {
        public const string CanonicalPath = "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion";

        public string Name => "NetworkProjectConfig 一意性";

        public PreflightResult Run()
        {
            List<string> found = Directory
                .GetFiles(Application.dataPath, "NetworkProjectConfig.fusion", SearchOption.AllDirectories)
                .Select(ToAssetPath)
                .ToList();
            return Evaluate(found);
        }

        public static PreflightResult Evaluate(IReadOnlyList<string> foundAssetPaths)
        {
            if (foundAssetPaths.Count == 0)
            {
                return PreflightResult.Fail(
                    "NetworkProjectConfig.fusion が見つかりません。",
                    $"Fusion のインストール状態を確認（正本: {CanonicalPath}）。");
            }

            List<string> strays = foundAssetPaths.Where(p => p != CanonicalPath).ToList();
            if (strays.Count > 0)
            {
                return PreflightResult.Fail(
                    "正本パス以外に config を検出: " + string.Join(", ", strays),
                    $"正本は {CanonicalPath} のみ。それ以外は削除する（過去事故: stray config で weave 全滅、" +
                    "症状は全スポーン死 + Editor.log に 'has not been weaved'）。");
            }

            return PreflightResult.Pass($"正本パスに1つだけ存在: {CanonicalPath}");
        }

        // フルパス → "Assets/..." の Unity アセットパス（区切りは '/'）へ正規化する。
        private static string ToAssetPath(string fullPath)
        {
            string normalized = fullPath.Replace('\\', '/');
            int index = normalized.IndexOf("/Assets/", System.StringComparison.Ordinal);
            return index >= 0 ? normalized.Substring(index + 1) : normalized;
        }
    }
}
