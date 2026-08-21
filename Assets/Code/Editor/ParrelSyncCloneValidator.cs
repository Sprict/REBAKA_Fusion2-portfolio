using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Rebaka.Editor
{
    /// <summary>
    /// ParrelSync クローンのリンク健全性を検査する。
    ///
    /// 【なぜ必要か】
    /// ParrelSync の ClonesManager.CreateCloneFromPath() は Library と Packages を「コピー」した後、
    /// Assets / ProjectSettings を「リンク」する。ところが LinkFolders() はこうなっている:
    ///
    ///   if ((Directory.Exists(destinationPath) == false) &amp;&amp; (Directory.Exists(sourcePath) == true))
    ///       CreateLinkWin(...);
    ///   else
    ///       Debug.LogWarning("Skipping Asset link, it already exists: " + destinationPath);
    ///
    /// リンク先が既に存在すると**リンクを作らず警告だけ出して続行**する。エラーにも例外にもならない。
    /// さらに CreateLinkWin() は `cmd.exe /C mklink /J` を投げっぱなしで、終了コードも標準エラーも
    /// 見ていないため、mklink 自体が失敗しても成功と区別できない。
    ///
    /// 結果として「作成は成功したように見えるが ProjectSettings がリンクされていないクローン」が
    /// 生まれる。そのクローンで Unity を開くと、本体の設定を読まず既定値の ProjectSettings.asset を
    /// 新規生成するため、入力システムやレンダーパイプラインの設定が本体と食い違う。
    ///
    /// 2026-08-07 に実際に発生した:
    ///   clone_1 の ProjectSettings が実体ディレクトリになっており、activeInputHandler が
    ///   本体の 1 (New Input System) に対し 0 (旧 Input Manager) のままだった。
    ///   毎起動時の入力システム警告と、画面全体のマゼンタ表示（レンダーパイプライン未解決）が
    ///   同時に起きていたが、症状からは原因が分からず切り分けに時間を要した。
    ///
    /// Menu: Tools > ParrelSync > Validate Clones
    /// </summary>
    public static class ParrelSyncCloneValidator
    {
        /// <summary>ParrelSync が本体へリンクするフォルダ（ClonesManager.CreateCloneFromPath 準拠）。</summary>
        private static readonly string[] LinkedFolders = { "Assets", "ProjectSettings" };

        /// <summary>ParrelSync がクローン識別に使う空ファイル。</summary>
        private const string CloneMarkerFileName = ".clone";

        internal enum FolderStatus
        {
            /// <summary>本体へのリンク（ジャンクション/シンボリックリンク）になっている。正常。</summary>
            Linked,

            /// <summary>実体ディレクトリ。ParrelSync がリンクをスキップした状態。設定が本体と食い違う。</summary>
            RealDirectory,

            /// <summary>存在しない（リンク切れを含む）。</summary>
            Missing,
        }

        /// <summary>
        /// 検査の判定部分。ファイルシステムから読み取った事実だけを受け取る純粋関数にして、
        /// EditMode テストから検証できるようにしている。
        /// </summary>
        /// <param name="exists">Directory.Exists の結果。リンク切れの場合も false になる。</param>
        /// <param name="isReparsePoint">FileAttributes に ReparsePoint が立っているか。
        /// mklink /J のジャンクションも New-Item -ItemType SymbolicLink のリンクも、どちらも立つ。</param>
        internal static FolderStatus Evaluate(bool exists, bool isReparsePoint)
        {
            if (!exists)
            {
                return FolderStatus.Missing;
            }

            return isReparsePoint ? FolderStatus.Linked : FolderStatus.RealDirectory;
        }

        [MenuItem("Tools/ParrelSync/Validate Clones")]
        public static void Validate()
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string parentDir = Path.GetDirectoryName(projectRoot);
            string projectName = Path.GetFileName(projectRoot);

            if (string.IsNullOrEmpty(parentDir))
            {
                Debug.LogError("[CloneValidator] プロジェクトの親ディレクトリを解決できなかった。");
                return;
            }

            string[] cloneDirs = Directory.GetDirectories(parentDir, projectName + "_clone_*");
            Array.Sort(cloneDirs, StringComparer.OrdinalIgnoreCase);

            if (cloneDirs.Length == 0)
            {
                EditorUtility.DisplayDialog(
                    "ParrelSync Clone Validator",
                    $"クローンが見つからなかった。\n検索場所: {parentDir}\nパターン: {projectName}_clone_*",
                    "OK");
                return;
            }

            var report = new StringBuilder();
            var brokenClones = new List<string>();

            foreach (string cloneDir in cloneDirs)
            {
                string cloneName = Path.GetFileName(cloneDir);
                report.Append("■ ").Append(cloneName).Append('\n');

                if (!File.Exists(Path.Combine(cloneDir, CloneMarkerFileName)))
                {
                    report.Append("   ! ").Append(CloneMarkerFileName)
                          .Append(" が無い。ParrelSync のクローンではない可能性がある\n");
                }

                bool cloneIsBroken = false;

                foreach (string folder in LinkedFolders)
                {
                    string path = Path.Combine(cloneDir, folder);
                    FolderStatus status = Evaluate(Directory.Exists(path), IsReparsePoint(path));

                    switch (status)
                    {
                        case FolderStatus.Linked:
                            report.Append("   OK   ").Append(folder).Append(" → 本体へのリンク\n");
                            break;

                        case FolderStatus.RealDirectory:
                            report.Append("   NG   ").Append(folder)
                                  .Append(" → 実体ディレクトリ（リンクされていない）\n");
                            cloneIsBroken = true;
                            break;

                        case FolderStatus.Missing:
                            report.Append("   NG   ").Append(folder)
                                  .Append(" → 存在しない（リンク切れの可能性）\n");
                            cloneIsBroken = true;
                            break;
                    }
                }

                if (cloneIsBroken)
                {
                    brokenClones.Add(cloneDir);
                }

                report.Append('\n');
            }

            if (brokenClones.Count == 0)
            {
                Debug.Log("[CloneValidator] 全クローン正常\n\n" + report);
                EditorUtility.DisplayDialog(
                    "ParrelSync Clone Validator",
                    $"検査した {cloneDirs.Length} 個のクローンはすべて正常。\n\n{report}",
                    "OK");
                return;
            }

            string repair = BuildRepairInstructions(projectRoot, brokenClones);
            Debug.LogError("[CloneValidator] リンクされていないフォルダを検出\n\n" + report + "\n" + repair);

            bool copyToClipboard = EditorUtility.DisplayDialog(
                "ParrelSync Clone Validator",
                $"{brokenClones.Count} 個のクローンでリンク不備を検出。\n\n{report}\n" +
                "修復コマンドをクリップボードにコピーする？\n" +
                "（実行前に対象クローンの Unity を必ず閉じること）",
                "コピーする", "閉じる");

            if (copyToClipboard)
            {
                EditorGUIUtility.systemCopyBuffer = repair;
                Debug.Log("[CloneValidator] 修復コマンドをクリップボードにコピーした。");
            }
        }

        /// <summary>
        /// 自動修復はしない。既存ディレクトリの削除を伴い、対象クローンの Unity が起動していると
        /// 失敗するうえ、未保存の変更を失わせうるため、手順の提示に留める。
        /// </summary>
        private static string BuildRepairInstructions(string projectRoot, List<string> brokenClones)
        {
            var sb = new StringBuilder();
            sb.Append("--- 修復手順（PowerShell / 対象クローンの Unity を閉じてから実行）---\n");
            sb.Append("# 開発者モードが無効な場合は管理者権限の PowerShell が必要\n\n");

            string stamp = DateTime.Now.ToString("yyyyMMddHHmm");

            foreach (string cloneDir in brokenClones)
            {
                foreach (string folder in LinkedFolders)
                {
                    string clonePath = Path.Combine(cloneDir, folder);
                    if (Evaluate(Directory.Exists(clonePath), IsReparsePoint(clonePath)) == FolderStatus.Linked)
                    {
                        continue;
                    }

                    string sourcePath = Path.Combine(projectRoot, folder);
                    sb.Append("# ").Append(Path.GetFileName(cloneDir)).Append(" / ").Append(folder).Append('\n');

                    if (Directory.Exists(clonePath))
                    {
                        // 消す前に必ず退避する。中身が本体と食い違っている可能性があり、
                        // 復旧の手掛かりになる（今回の clone_1 では旧 activeInputHandler が残っていた）。
                        sb.Append("Move-Item '").Append(clonePath).Append("' '")
                          .Append(clonePath).Append("._backup_").Append(stamp).Append("'\n");
                    }

                    sb.Append("New-Item -ItemType SymbolicLink -Path '").Append(clonePath)
                      .Append("' -Target '").Append(sourcePath).Append("'\n\n");
                }
            }

            sb.Append("# 確認\n");
            foreach (string cloneDir in brokenClones)
            {
                sb.Append("Get-Item '").Append(Path.Combine(cloneDir, "*"))
                  .Append("' | Select-Object Name, LinkType, Target\n");
            }

            return sb.ToString();
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                // 存在しない/アクセス不可。呼び出し側が Directory.Exists と合わせて判定する。
                return false;
            }
        }
    }
}
