using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnusedAssetFinder.Editor
{
    /// <summary>
    /// プロジェクト内の「どこからも参照されていないアセット」を検出するコアロジック。
    ///
    /// アルゴリズム（到達可能性解析）:
    ///   1. 使用の「起点（root）」を集める = ビルド対象シーン / Resources / ProjectSettings 参照 など、
    ///      “必ず使われる”入口。
    ///   2. 各 root から AssetDatabase.GetDependencies(path, recursive:true) で推移的依存を全部たどり、
    ///      到達できるアセット集合（reachable）を作る。
    ///   3. プロジェクト内の全アセットのうち、reachable に含まれず、除外条件にも当たらないものが「未使用」。
    ///
    /// 限界（誤検出の原因）:
    ///   - Resources.Load / Addressables / 文字列パス・リフレクションでの動的ロードは依存解析で追えない
    ///     → Resources は既定で root 扱い、それ以外は利用者が除外リストで保護する。
    ///   - スクリプト(.cs)はコードからの参照を追えない → 既定で除外。
    ///   削除前に必ず Unity の参照検索などで最終確認すること。
    /// </summary>
    public static class UnusedAssetScanner
    {
        private static readonly Regex GuidRegex = new Regex("[0-9a-f]{32}", RegexOptions.Compiled);

        /// <summary>進捗コールバック付きでスキャンを実行し、未使用アセット一覧を返す。</summary>
        public static List<UnusedAssetEntry> Scan(UnusedAssetFinderSettings settings, Action<float, string> onProgress = null)
        {
            onProgress?.Invoke(0f, "起点を収集中...");
            HashSet<string> roots = CollectRoots(settings);

            onProgress?.Invoke(0.2f, "依存関係を解析中...");
            HashSet<string> reachable = CollectReachable(roots, onProgress);

            onProgress?.Invoke(0.85f, "未使用アセットを抽出中...");
            string selfFolder = GetSelfFolder();

            var results = new List<UnusedAssetEntry>();
            string[] all = AssetDatabase.GetAllAssetPaths();
            foreach (string path in all)
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue; // Packages 等は対象外
                if (AssetDatabase.IsValidFolder(path)) continue;                      // フォルダ自体は除外
                if (reachable.Contains(path)) continue;                               // 使用中
                if (IsExcluded(path, settings, selfFolder)) continue;                 // 除外条件

                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                results.Add(new UnusedAssetEntry(path, type != null ? type.Name : null, GetFileSize(path)));
            }

            results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes)); // 既定はサイズ降順
            onProgress?.Invoke(1f, "完了");
            return results;
        }

        // ---- 起点(root)収集 ----

        private static HashSet<string> CollectRoots(UnusedAssetFinderSettings settings)
        {
            var roots = new HashSet<string>(StringComparer.Ordinal);

            // シーン: 全シーンを起点にするか、ビルド有効シーンのみか
            if (settings.TreatAllScenesAsRoots)
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Scene"))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (p.StartsWith("Assets/", StringComparison.Ordinal)) roots.Add(p);
                }
            }
            else
            {
                foreach (var s in EditorBuildSettings.scenes)
                {
                    if (s.enabled && !string.IsNullOrEmpty(s.path)) roots.Add(s.path);
                }
            }

            // Resources / 特殊フォルダ配下は実行時ロードされ得るので起点に含める
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;
                if (AssetDatabase.IsValidFolder(path)) continue;
                if (settings.TreatResourcesAsUsed && ContainsFolderSegment(path, "Resources")) roots.Add(path);
                if (ContainsFolderSegment(path, "StreamingAssets")) roots.Add(path);
                if (ContainsFolderSegment(path, "Editor Default Resources")) roots.Add(path);
            }

            // ProjectSettings (GraphicsSettings/QualitySettings/InputManager 等) が参照する GUID を起点に
            if (settings.ScanProjectSettings)
            {
                foreach (string p in CollectProjectSettingsReferencedAssets()) roots.Add(p);
            }

            return roots;
        }

        /// <summary>
        /// ProjectSettings フォルダ内の全ファイルから 32 桁 GUID を抽出し、対応アセットパスへ解決する。
        /// URP パイプライン・InputSystem 設定など「コードにもシーンにも出ないがビルドに必須」の参照を救済する。
        /// </summary>
        private static IEnumerable<string> CollectProjectSettingsReferencedAssets()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) yield break;

            string settingsDir = Path.Combine(projectRoot, "ProjectSettings");
            if (!Directory.Exists(settingsDir)) yield break;

            var seen = new HashSet<string>();
            foreach (string file in Directory.GetFiles(settingsDir, "*", SearchOption.TopDirectoryOnly))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }

                foreach (Match m in GuidRegex.Matches(text))
                {
                    if (!seen.Add(m.Value)) continue;
                    string p = AssetDatabase.GUIDToAssetPath(m.Value);
                    if (!string.IsNullOrEmpty(p) && p.StartsWith("Assets/", StringComparison.Ordinal))
                    {
                        yield return p;
                    }
                }
            }
        }

        // ---- 到達可能集合 ----

        private static HashSet<string> CollectReachable(HashSet<string> roots, Action<float, string> onProgress)
        {
            var reachable = new HashSet<string>(roots, StringComparer.Ordinal);
            // GetDependencies(recursive:true) は推移的依存を一括で返すので BFS は不要。
            int i = 0, n = Math.Max(1, roots.Count);
            foreach (string root in roots)
            {
                foreach (string dep in AssetDatabase.GetDependencies(root, true))
                {
                    if (dep.StartsWith("Assets/", StringComparison.Ordinal)) reachable.Add(dep);
                }
                if ((++i & 31) == 0) onProgress?.Invoke(0.2f + 0.6f * i / n, $"依存解析 {i}/{n}");
            }
            return reachable;
        }

        // ---- 除外判定 ----

        private static bool IsExcluded(string path, UnusedAssetFinderSettings settings, string selfFolder)
        {
            // ツール自身は絶対に消させない
            if (!string.IsNullOrEmpty(selfFolder) && path.StartsWith(selfFolder, StringComparison.Ordinal)) return true;

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (settings.IgnoreScripts && ext == ".cs") return true;
            if (settings.IgnoredExtensions != null && settings.IgnoredExtensions.Contains(ext)) return true;

            if (settings.IgnoredPathSubstrings != null)
            {
                foreach (string sub in settings.IgnoredPathSubstrings)
                {
                    if (!string.IsNullOrEmpty(sub) && path.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
            }
            return false;
        }

        // ---- ユーティリティ ----

        /// <summary>"Foo/Resources/Bar.png" のように、フォルダ階層の一区切りとして name を含むか。</summary>
        private static bool ContainsFolderSegment(string path, string segment)
        {
            return path.IndexOf("/" + segment + "/", StringComparison.OrdinalIgnoreCase) >= 0
                   || path.StartsWith(segment + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static long GetFileSize(string assetPath)
        {
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                string full = Path.Combine(projectRoot, assetPath);
                var fi = new FileInfo(full);
                return fi.Exists ? fi.Length : 0L;
            }
            catch { return 0L; }
        }

        /// <summary>このツール（UnusedAssetScanner.cs）が置かれている Assets 相対フォルダを返す。自己保護用。</summary>
        private static string GetSelfFolder()
        {
            string[] guids = AssetDatabase.FindAssets("UnusedAssetScanner t:MonoScript");
            foreach (string g in guids)
            {
                string p = AssetDatabase.GUIDToAssetPath(g);
                if (p.EndsWith("/UnusedAssetScanner.cs", StringComparison.Ordinal))
                {
                    // 例: Assets/UnusedAssetFinder/Editor/UnusedAssetScanner.cs -> Assets/UnusedAssetFinder/
                    int editorIdx = p.LastIndexOf("/Editor/", StringComparison.Ordinal);
                    return editorIdx >= 0 ? p.Substring(0, editorIdx + 1) : Path.GetDirectoryName(p)?.Replace('\\', '/') + "/";
                }
            }
            return null;
        }

        public static string HumanSize(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return $"{v:0.##} {units[u]}";
        }
    }
}
