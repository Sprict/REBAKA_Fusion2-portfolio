using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace UnusedAssetFinder.Editor
{
    /// <summary>
    /// 未使用アセット検出ウィンドウ。Tools / Unused Asset Finder から開く。
    /// スキャン → 一覧で選択 → ゴミ箱送り（復元可能）/ CSV エクスポート、までを 1 画面で行う。
    /// </summary>
    public sealed class UnusedAssetFinderWindow : EditorWindow
    {
        private enum SortMode { SizeDesc, SizeAsc, Path, Type }

        private UnusedAssetFinderSettings _settings;
        private List<UnusedAssetEntry> _results = new List<UnusedAssetEntry>();
        private Vector2 _scroll;
        private bool _showSettings;
        private string _filter = string.Empty;
        private SortMode _sort = SortMode.SizeDesc;
        private bool _hasScanned;

        [MenuItem("Tools/Unused Asset Finder")]
        public static void Open()
        {
            var w = GetWindow<UnusedAssetFinderWindow>("Unused Assets");
            w.minSize = new Vector2(520, 360);
            w.Show();
        }

        private void OnEnable()
        {
            _settings = UnusedAssetFinderSettings.Load();
        }

        private void OnGUI()
        {
            DrawToolbar();
            if (_showSettings) DrawSettings();
            DrawSummary();
            DrawResults();
            DrawFooter();
        }

        // ---- ツールバー ----

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Scan", EditorStyles.toolbarButton, GUILayout.Width(80)))
                {
                    RunScan();
                }

                _showSettings = GUILayout.Toggle(_showSettings, "Settings", EditorStyles.toolbarButton, GUILayout.Width(80));

                GUILayout.Space(8);
                GUILayout.Label("Filter", GUILayout.Width(38));
                _filter = GUILayout.TextField(_filter, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));

                GUILayout.FlexibleSpace();

                GUILayout.Label("Sort", GUILayout.Width(30));
                var newSort = (SortMode)EditorGUILayout.EnumPopup(_sort, EditorStyles.toolbarPopup, GUILayout.Width(100));
                if (newSort != _sort) { _sort = newSort; ApplySort(); }
            }
        }

        private void DrawSettings()
        {
            using (new EditorGUILayout.VerticalScope(EditorGUI.indentLevel >= 0 ? GUI.skin.box : GUI.skin.box))
            {
                EditorGUILayout.LabelField("スキャン設定", EditorStyles.boldLabel);
                EditorGUI.BeginChangeCheck();

                _settings.TreatResourcesAsUsed = EditorGUILayout.ToggleLeft(
                    "Resources 配下を使用中扱い（実行時ロードを保護）", _settings.TreatResourcesAsUsed);
                _settings.ScanProjectSettings = EditorGUILayout.ToggleLeft(
                    "ProjectSettings の参照を起点に含める（URP/Input 等を保護）", _settings.ScanProjectSettings);
                _settings.TreatAllScenesAsRoots = EditorGUILayout.ToggleLeft(
                    "全シーンを起点扱い（OFF で未登録シーンも未使用候補にする）", _settings.TreatAllScenesAsRoots);
                _settings.IgnoreScripts = EditorGUILayout.ToggleLeft(
                    "スクリプト(.cs)を候補から除外（推奨）", _settings.IgnoreScripts);

                EditorGUILayout.Space(4);
                DrawStringList("除外パス（部分一致）", _settings.IgnoredPathSubstrings);
                DrawStringList("除外拡張子（.png など）", _settings.IgnoredExtensions);

                if (EditorGUI.EndChangeCheck())
                {
                    _settings.Save();
                }
            }
        }

        private static void DrawStringList(string label, List<string> list)
        {
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            int removeAt = -1;
            for (int i = 0; i < list.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    list[i] = EditorGUILayout.TextField(list[i]);
                    if (GUILayout.Button("−", GUILayout.Width(24))) removeAt = i;
                }
            }
            if (removeAt >= 0) list.RemoveAt(removeAt);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("+ 追加", GUILayout.Width(70))) list.Add(string.Empty);
            }
        }

        // ---- サマリ ----

        private void DrawSummary()
        {
            if (!_hasScanned) return;
            int selCount = _results.Count(r => r.Selected);
            long selSize = _results.Where(r => r.Selected).Sum(r => r.SizeBytes);
            long totalSize = _results.Sum(r => r.SizeBytes);

            EditorGUILayout.HelpBox(
                $"未使用候補: {_results.Count} 件 / 合計 {UnusedAssetScanner.HumanSize(totalSize)}　" +
                $"｜　選択中: {selCount} 件 / {UnusedAssetScanner.HumanSize(selSize)}",
                MessageType.Info);
        }

        // ---- 結果一覧 ----

        private void DrawResults()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("全選択", GUILayout.Width(70))) SetAll(true);
                if (GUILayout.Button("全解除", GUILayout.Width(70))) SetAll(false);
                if (GUILayout.Button("選択反転", GUILayout.Width(80))) _results.ForEach(r => r.Selected = !r.Selected);
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            string f = _filter?.Trim();
            foreach (var e in _results)
            {
                if (!string.IsNullOrEmpty(f) && e.Path.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                DrawRow(e);
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawRow(UnusedAssetEntry e)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                e.Selected = EditorGUILayout.Toggle(e.Selected, GUILayout.Width(18));

                var icon = AssetDatabase.GetCachedIcon(e.Path);
                if (icon != null) GUILayout.Label(new GUIContent(icon), GUILayout.Width(18), GUILayout.Height(16));

                if (GUILayout.Button(e.Path, EditorStyles.label))
                {
                    var obj = AssetDatabase.LoadMainAssetAtPath(e.Path);
                    if (obj != null) { EditorGUIUtility.PingObject(obj); Selection.activeObject = obj; }
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label(e.TypeName, EditorStyles.miniLabel, GUILayout.Width(110));
                GUILayout.Label(UnusedAssetScanner.HumanSize(e.SizeBytes), EditorStyles.miniLabel, GUILayout.Width(70));
            }
        }

        // ---- フッター（操作） ----

        private void DrawFooter()
        {
            if (!_hasScanned) return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("CSV エクスポート", GUILayout.Height(24)))
                {
                    ExportCsv();
                }

                GUI.backgroundColor = new Color(1f, 0.55f, 0.55f);
                if (GUILayout.Button("選択をゴミ箱へ（復元可）", GUILayout.Height(24)))
                {
                    DeleteSelected();
                }
                GUI.backgroundColor = Color.white;
            }
        }

        // ---- 処理 ----

        private void RunScan()
        {
            try
            {
                var list = UnusedAssetScanner.Scan(_settings,
                    (p, msg) => EditorUtility.DisplayProgressBar("Unused Asset Finder", msg, p));
                _results = list;
                _hasScanned = true;
                ApplySort();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            Repaint();
        }

        private void ApplySort()
        {
            switch (_sort)
            {
                case SortMode.SizeDesc: _results.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes)); break;
                case SortMode.SizeAsc: _results.Sort((a, b) => a.SizeBytes.CompareTo(b.SizeBytes)); break;
                case SortMode.Path: _results.Sort((a, b) => string.Compare(a.Path, b.Path, System.StringComparison.Ordinal)); break;
                case SortMode.Type: _results.Sort((a, b) => string.Compare(a.TypeName, b.TypeName, System.StringComparison.Ordinal)); break;
            }
        }

        private void SetAll(bool value)
        {
            string f = _filter?.Trim();
            foreach (var e in _results)
            {
                if (!string.IsNullOrEmpty(f) && e.Path.IndexOf(f, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                e.Selected = value;
            }
        }

        private void DeleteSelected()
        {
            var targets = _results.Where(r => r.Selected).Select(r => r.Path).ToList();
            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("Unused Asset Finder", "削除対象が選択されていません。", "OK");
                return;
            }

            long size = _results.Where(r => r.Selected).Sum(r => r.SizeBytes);
            bool ok = EditorUtility.DisplayDialog(
                "ゴミ箱へ移動",
                $"{targets.Count} 件（{UnusedAssetScanner.HumanSize(size)}）をゴミ箱へ移動します。\n" +
                "OS のゴミ箱から復元可能ですが、念のため事前のコミット/バックアップを推奨します。\n\n実行しますか？",
                "ゴミ箱へ移動", "キャンセル");
            if (!ok) return;

            var failed = new List<string>();
            foreach (string p in targets)
            {
                if (!AssetDatabase.MoveAssetToTrash(p)) failed.Add(p);
            }
            AssetDatabase.Refresh();

            _results.RemoveAll(r => r.Selected && !failed.Contains(r.Path));

            if (failed.Count > 0)
            {
                Debug.LogWarning("[UnusedAssetFinder] 移動に失敗:\n" + string.Join("\n", failed));
                EditorUtility.DisplayDialog("Unused Asset Finder",
                    $"{targets.Count - failed.Count} 件を移動。{failed.Count} 件は失敗（Console 参照）。", "OK");
            }
            Repaint();
        }

        private void ExportCsv()
        {
            string path = EditorUtility.SaveFilePanel("CSV エクスポート", "", "unused_assets.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            var sb = new StringBuilder();
            sb.AppendLine("Path,Type,SizeBytes,HumanSize");
            foreach (var e in _results)
            {
                sb.AppendLine($"\"{e.Path}\",{e.TypeName},{e.SizeBytes},{UnusedAssetScanner.HumanSize(e.SizeBytes)}");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            Debug.Log($"[UnusedAssetFinder] CSV を書き出しました: {path}");
        }
    }
}
