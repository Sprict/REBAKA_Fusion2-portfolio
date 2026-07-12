using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MyFolder.Editor.Preflight
{
    /// <summary>
    /// 統合前プリフライトチェックの実行ウィンドウ。
    /// develop へのマージ前・2-client 検証前に Run All Checks を実行し、
    /// 赤（Fail）が残っている間は統合しない運用（.claude/skills/integration-preflight 参照）。
    /// チェック中の例外は Fail 扱い（例外で緑に見えるのが最悪＝誤緑回避原則）。
    /// </summary>
    public sealed class PreflightCheckWindow : EditorWindow
    {
        private readonly List<(string name, PreflightResult result)> _results =
            new List<(string, PreflightResult)>();
        private Vector2 _scroll;

        [MenuItem("Tools/REBAKA/Preflight Check")]
        public static void Open() => GetWindow<PreflightCheckWindow>("Preflight Check");

        public static IPreflightCheck[] CreateAllChecks() => new IPreflightCheck[]
        {
            new ConfigUniquenessCheck(),
            new WeaveAssembliesCheck(),
            new SceneRegistrationCheck(),
            new ScenePlacedObjectsCheck(),
            new BackupFreshnessCheck(),
            new MapWiringCheck(),
        };

        private void OnGUI()
        {
            if (GUILayout.Button("Run All Checks", GUILayout.Height(30f)))
            {
                RunAll();
            }

            if (_results.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "統合・マージ前に Run All Checks を実行してください。", MessageType.Info);
                return;
            }

            DrawSummary();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach ((string name, PreflightResult result) in _results)
            {
                DrawResult(name, result);
            }
            EditorGUILayout.EndScrollView();
        }

        private void RunAll()
        {
            _results.Clear();
            foreach (IPreflightCheck check in CreateAllChecks())
            {
                PreflightResult result;
                try
                {
                    result = check.Run();
                }
                catch (System.Exception e)
                {
                    Debug.LogException(e);
                    result = PreflightResult.Fail(
                        "チェック実行中に例外: " + e.Message,
                        "例外はチェック自体のバグ。Console のスタックトレースを確認する。");
                }
                _results.Add((check.Name, result));
            }
        }

        private void DrawSummary()
        {
            int fails = 0;
            int warns = 0;
            foreach ((_, PreflightResult result) in _results)
            {
                if (result.Status == PreflightStatus.Fail) fails++;
                else if (result.Status == PreflightStatus.Warning) warns++;
            }

            if (fails > 0)
            {
                EditorGUILayout.HelpBox(
                    $"FAIL {fails} 件 / WARN {warns} 件 — 統合禁止。赤を解決してから再実行。",
                    MessageType.Error);
            }
            else if (warns > 0)
            {
                EditorGUILayout.HelpBox(
                    $"WARN {warns} 件 — 黄の項目を目視確認のうえ判断。", MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("全チェック合格。", MessageType.Info);
            }
        }

        private static void DrawResult(string name, PreflightResult result)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            (string label, Color color) = result.Status switch
            {
                PreflightStatus.Pass => ("PASS", new Color(0.3f, 0.8f, 0.3f)),
                PreflightStatus.Warning => ("WARN", new Color(0.9f, 0.8f, 0.2f)),
                _ => ("FAIL", new Color(0.9f, 0.3f, 0.3f)),
            };

            Color prev = GUI.color;
            EditorGUILayout.BeginHorizontal();
            GUI.color = color;
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.Width(48f));
            GUI.color = prev;
            GUILayout.Label(name, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.LabelField(result.Message, EditorStyles.wordWrappedLabel);
            if (!string.IsNullOrEmpty(result.FixHint))
            {
                EditorGUILayout.LabelField("→ " + result.FixHint, EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.EndVertical();
        }
    }
}
