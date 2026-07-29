using UnityEditor;
using UnityEngine;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

namespace MyFolder.Editor
{
    /// <summary>
    /// ParrelSync の ValidateCopiedFoldersIntegrity がロックされたファイル
    /// （Coplay の console_logs_*.log 等）を読もうとして IOException を起こす問題を修正。
    ///
    /// InitializeOnLoad の実行順序を利用して、ParrelSync より先に Packages フォルダの
    /// 同期を完了させる。ロックされたファイルは MD5 計算をスキップして常に一致扱いにする。
    /// </summary>
    [InitializeOnLoad]
    public static class ParrelSyncLockFix
    {
        private const string SessionKey = "ParrelSyncLockFix_Done";

        // 揮発ファイルは Packages の整合性比較対象から外す。
        // Coplay のログ/キャッシュは内容もファイル数も頻繁に変わるため、
        // 比較対象に含めると毎回差分扱いになりやすい。
        private static readonly string[] IgnoredRelativePathPrefixes = new[]
        {
            "coplay/editor/coplaylogs/",
            "coplay/editor/formattingcache/",
        };

        // それ以外のログは内容のみスキップし、パス差分は検知できるようにする。
        private static readonly string[] SkipContentPatterns = new[]
        {
            ".log",
        };

        static ParrelSyncLockFix()
        {
            if (SessionState.GetBool(SessionKey, false)) return;
            SessionState.SetBool(SessionKey, true);

            // クローンプロジェクトでのみ実行
            if (!IsClone()) return;

            try
            {
                SyncPackagesFolderSafely();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ParrelSyncLockFix] Sync failed: {e.Message}");
            }
        }

        private static void SyncPackagesFolderSafely()
        {
            string clonePath    = GetCurrentProjectPath();
            string originalPath = GetOriginalProjectPath();

            if (string.IsNullOrEmpty(clonePath) || string.IsNullOrEmpty(originalPath)) return;

            string clonePkg    = Path.Combine(clonePath,    "Packages");
            string originalPkg = Path.Combine(originalPath, "Packages");

            if (!Directory.Exists(originalPkg)) return;

            string cloneHash    = CreateMd5ForFolderSafe(clonePkg);
            string originalHash = CreateMd5ForFolderSafe(originalPkg);

            if (cloneHash != originalHash)
            {
                Debug.Log("[ParrelSyncLockFix] Packages folder changed. Updating clone...");
                FileUtil.ReplaceDirectory(originalPkg, clonePkg);
                Debug.Log("[ParrelSyncLockFix] Clone Packages folder updated.");
            }
            else
            {
                Debug.Log("[ParrelSyncLockFix] Packages folder is up to date.");
            }
        }

        /// <summary>
        /// ロックされたファイルをスキップしながら MD5 を計算する。
        /// </summary>
        private static string CreateMd5ForFolderSafe(string path)
        {
            if (!Directory.Exists(path)) return string.Empty;

            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                                 .OrderBy(p => p)
                                 .Select(file => new
                                 {
                                     File = file,
                                     RelativePath = file.Substring(path.Length + 1).ToLower().Replace('\\', '/')
                                 })
                                 .Where(entry => !ShouldIgnoreFile(entry.RelativePath))
                                 .ToList();

            if (files.Count == 0) return string.Empty;

            using var md5 = MD5.Create();
            int last = files.Count - 1;

            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i].File;
                string relativePath = files[i].RelativePath;

                // ロック競合が起きやすいファイルはパスのみハッシュしてコンテンツをスキップ
                bool skip = ShouldSkipContent(relativePath);

                byte[] pathBytes = Encoding.UTF8.GetBytes(relativePath);
                byte[] contentBytes = skip ? Array.Empty<byte>() : ReadAllBytesSafe(file);

                if (i == last)
                {
                    md5.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);
                    md5.TransformFinalBlock(contentBytes, 0, contentBytes.Length);
                }
                else
                {
                    md5.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);
                    md5.TransformBlock(contentBytes, 0, contentBytes.Length, contentBytes, 0);
                }
            }

            return BitConverter.ToString(md5.Hash).Replace("-", "").ToLower();
        }

        /// <summary>
        /// ファイルをロック競合に耐性のある方法で読む。失敗時は空バイト配列を返す。
        /// </summary>
        private static byte[] ReadAllBytesSafe(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                var buf = new byte[fs.Length];
                fs.Read(buf, 0, buf.Length);
                return buf;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }

        private static bool ShouldIgnoreFile(string relativePath)
            => IgnoredRelativePathPrefixes.Any(p => relativePath.Contains(p));

        private static bool ShouldSkipContent(string relativePath)
            => SkipContentPatterns.Any(p => relativePath.Contains(p));

        // ─── ParrelSync 内部メソッドの複製（パッケージ非公開のため） ───

        private static bool IsClone()
        {
            string path = GetCurrentProjectPath();
            if (string.IsNullOrEmpty(path)) return false;
            string name = Path.GetFileName(path);
            return name.Contains("_clone_");
        }

        private static string GetCurrentProjectPath()
        {
            try
            {
                return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            }
            catch { return null; }
        }

        private static string GetOriginalProjectPath()
        {
            try
            {
                string clonePath = GetCurrentProjectPath();
                if (string.IsNullOrEmpty(clonePath)) return null;

                // クローンパスから元プロジェクトパスを推定
                // 例: .../REBAKA_Fusion2_clone_0 → .../REBAKA_Fusion2
                string dirName = Path.GetFileName(clonePath);
                int idx = dirName.LastIndexOf("_clone_", StringComparison.Ordinal);
                if (idx < 0) return null;

                string originalName = dirName.Substring(0, idx);
                return Path.Combine(Path.GetDirectoryName(clonePath)!, originalName);
            }
            catch { return null; }
        }
    }
}
