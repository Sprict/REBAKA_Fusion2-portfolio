using UnityEditor;
using UnityEngine;

namespace Rebaka.Editor.Preflight
{
    /// <summary>
    /// Preflight プロファイルを実行するウィンドウ。
    /// Production Integration は develop 統合可否、Map Prototype は Map 作業完了可否を確認する。
    /// </summary>
    public sealed class PreflightCheckWindow : EditorWindow
    {
        private readonly PreflightProfileRunner _runner = new();
        private PreflightRunResult _lastRun;
        private string _lastExpectedScenePath;
        private Vector2 _scroll;

        [MenuItem("Tools/REBAKA/Preflight Check")]
        public static void Open() => GetWindow<PreflightCheckWindow>("Preflight Check");

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Production Integration", EditorStyles.boldLabel);
            if (GUILayout.Button("Run Production Integration", GUILayout.Height(30f)))
            {
                RunProfile(PreflightProfile.ProductionIntegration);
            }
            EditorGUILayout.HelpBox("Test_Playground を対象に develop 統合可否を確認します。", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Map Prototype", EditorStyles.boldLabel);
            if (GUILayout.Button("Run Map Prototype", GUILayout.Height(30f)))
            {
                RunProfile(PreflightProfile.MapPrototype);
            }
            EditorGUILayout.HelpBox("MapNetworkSandbox を対象に Map 作業完了可否を確認します。", MessageType.Info);

            if (_lastRun == null)
            {
                EditorGUILayout.HelpBox("実行するプロファイルを選んでください。", MessageType.Info);
                return;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(_lastRun.Title, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(_lastExpectedScenePath, EditorStyles.wordWrappedMiniLabel);
            DrawSummary();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach ((string name, PreflightResult result) in _lastRun.Results)
            {
                DrawResult(name, result);
            }
            EditorGUILayout.EndScrollView();
        }

        private void RunProfile(PreflightProfile profile)
        {
            PreflightProfileDefinition definition = PreflightProfileCatalog.Create(profile);
            _lastExpectedScenePath = definition.ExpectedScenePath;
            _lastRun = _runner.Run(definition);
            Repaint();
        }

        private void DrawSummary()
        {
            if (_lastRun.FailCount > 0)
            {
                string consequence = _lastRun.BlocksDevelopIntegration
                    ? "develop 統合を阻止。赤を解決してから再実行。"
                    : "Map 作業完了を阻止。develop 統合判定には使用しない。";
                EditorGUILayout.HelpBox(
                    $"FAIL {_lastRun.FailCount} 件 / WARN {_lastRun.WarningCount} 件 — {consequence}",
                    MessageType.Error);
            }
            else if (_lastRun.WarningCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"WARN {_lastRun.WarningCount} 件 — 黄の項目を目視確認のうえ判断。", MessageType.Warning);
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

            Color previousColor = GUI.color;
            EditorGUILayout.BeginHorizontal();
            GUI.color = color;
            GUILayout.Label(label, EditorStyles.boldLabel, GUILayout.Width(48f));
            GUI.color = previousColor;
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
