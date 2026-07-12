using UnityEngine;
using UnityEditor;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System;

namespace MyFolder.Editor
{
    /// <summary>
    /// ParrelSync クローンを直接起動するユーティリティ。
    ///
    /// 【使用理由】
    /// ParrelSync の ClonesManager.OpenProject() は起動前に
    /// ValidateCopiedFoldersIntegrity.ValidateFolder() を呼ぶ。
    /// このメソッドは Packages フォルダ全ファイルの MD5 を File.ReadAllBytes で計算するが、
    /// Coplay の console_logs_*.log がメインエディタにロックされているため
    /// IOException: Sharing violation が発生してクローンを起動できない。
    ///
    /// このツールは ValidateFolder の代わりにロック耐性のある同期を行い、
    /// その後 Unity を直接起動する。
    ///
    /// Menu: Tools > ParrelSync > Launch Clone 0  (Ctrl+Alt+0)
    ///       Tools > ParrelSync > Launch Clone 1  (Ctrl+Alt+1)
    /// </summary>
    public static class LaunchParrelSyncClone
    {
        // Coplay の揮発ディレクトリは Packages 差分比較から除外する。
        // ログやキャッシュは複数 Editor インスタンスで差分が出やすく、
        // 比較に含めると毎回 Packages 全体の再同期が走りやすい。
        private static readonly string[] IgnoredRelativePathPrefixes =
        {
            "coplay/editor/coplaylogs/",
            "coplay/editor/formattingcache/",
        };

        // それ以外のログは内容のみ無視してロック競合を避ける。
        private static readonly string[] SkipContentPatterns =
        {
            ".log",
        };

        [MenuItem("Tools/ParrelSync/Launch Clone 0 %&0")]
        public static void LaunchClone0() => LaunchClone(0);

        [MenuItem("Tools/ParrelSync/Launch Clone 1 %&1")]
        public static void LaunchClone1() => LaunchClone(1);

        private static void LaunchClone(int index)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string parentDir   = Path.GetDirectoryName(projectRoot)!;
            string projectName = Path.GetFileName(projectRoot);
            string clonePath   = Path.Combine(parentDir, $"{projectName}_clone_{index}");

            if (!Directory.Exists(clonePath))
            {
                EditorUtility.DisplayDialog("Clone Not Found",
                    $"Clone directory not found:\n{clonePath}\n\nCreate it first via ParrelSync > Clones Manager.",
                    "OK");
                return;
            }

            // Packages フォルダをロック耐性のある方法で同期
            SyncPackagesSafe(projectRoot, clonePath);

            // Unity を直接起動（ValidateCopiedFoldersIntegrity をバイパス）
            string unityPath = EditorApplication.applicationPath;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName         = unityPath,
                Arguments        = $"-projectPath \"{clonePath}\"",
                UseShellExecute  = false,
                CreateNoWindow   = false,
            };

            Debug.Log($"[LaunchClone] Launching clone {index}:\n  {unityPath}\n  {clonePath}");
            System.Diagnostics.Process.Start(psi);
        }

        /// <summary>
        /// Packages フォルダをロック耐性のある方法で同期する。
        /// ロックされたファイルは MD5 計算をスキップし、コンテンツ変更なしとみなす。
        /// </summary>
        private static void SyncPackagesSafe(string originalRoot, string cloneRoot)
        {
            string originalPkg = Path.Combine(originalRoot, "Packages");
            string clonePkg    = Path.Combine(cloneRoot,    "Packages");

            if (!Directory.Exists(originalPkg)) return;

            string originalHash = ComputeFolderHashSafe(originalPkg);
            string cloneHash    = Directory.Exists(clonePkg)
                                  ? ComputeFolderHashSafe(clonePkg)
                                  : string.Empty;

            if (originalHash == cloneHash)
            {
                Debug.Log("[LaunchClone] Packages folder is up to date.");
                return;
            }

            Debug.Log("[LaunchClone] Packages folder changed. Syncing clone...");
            FileUtil.ReplaceDirectory(originalPkg, clonePkg);
            Debug.Log("[LaunchClone] Packages folder synced.");
        }

        private static string ComputeFolderHashSafe(string path)
        {
            if (!Directory.Exists(path)) return string.Empty;

            var files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                                 .OrderBy(f => f)
                                 .Select(file => new
                                 {
                                     File = file,
                                     RelativePath = file.Substring(path.Length + 1)
                                         .ToLower()
                                         .Replace('\\', '/')
                                 })
                                 .Where(entry => !ShouldIgnoreFile(entry.RelativePath))
                                 .ToArray();

            if (files.Length == 0) return string.Empty;

            using var md5  = MD5.Create();
            int last = files.Length - 1;

            for (int i = 0; i < files.Length; i++)
            {
                string file         = files[i].File;
                string relativePath = files[i].RelativePath;

                byte[] pathBytes    = Encoding.UTF8.GetBytes(relativePath);
                byte[] contentBytes = ShouldSkipContent(relativePath)
                                      ? Array.Empty<byte>()
                                      : ReadSafe(file);

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

        private static bool ShouldIgnoreFile(string relativePath)
            => IgnoredRelativePathPrefixes.Any(p => relativePath.Contains(p));

        private static bool ShouldSkipContent(string relativePath)
            => SkipContentPatterns.Any(p => relativePath.Contains(p));

        private static byte[] ReadSafe(string path)
        {
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
                var buf = new byte[fs.Length];
                _ = fs.Read(buf, 0, buf.Length);
                return buf;
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }
}
