using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Rebaka.Editor.Preflight
{
    /// <summary>
    /// チェック#3: 必須シーンが Build Settings に登録済み・有効か。
    /// 過去事故: StartGameArgs に Scene 未指定 + Build Settings 未登録で RegisterSceneObjects が
    /// 発火せず、シーン配置 NetworkObject が同期しない（2026-06-20）。
    /// </summary>
    public sealed class SceneRegistrationCheck : IPreflightCheck
    {
        public IReadOnlyList<string> RequiredScenePaths { get; }

        public SceneRegistrationCheck(params string[] requiredScenePaths)
        {
            if (requiredScenePaths == null) throw new ArgumentNullException(nameof(requiredScenePaths));
            RequiredScenePaths = Array.AsReadOnly(requiredScenePaths.ToArray());
        }

        public string Name => "必須シーンの Build Settings 登録";

        public PreflightResult Run()
        {
            List<(string, bool)> entries = EditorBuildSettings.scenes
                .Select(s => (s.path, s.enabled))
                .ToList();
            return Evaluate(entries, RequiredScenePaths);
        }

        public static PreflightResult Evaluate(
            IReadOnlyList<(string scenePath, bool enabled)> buildScenes,
            IReadOnlyList<string> requiredScenePaths)
        {
            var missing = new List<string>();
            var disabled = new List<string>();

            foreach (string required in requiredScenePaths)
            {
                bool found = false;
                bool enabledFound = false;
                string normalizedRequiredPath = ActiveSceneCheck.NormalizeScenePath(required);
                foreach ((string scenePath, bool enabled) in buildScenes)
                {
                    string normalizedScenePath = ActiveSceneCheck.NormalizeScenePath(scenePath);
                    if (!string.Equals(normalizedScenePath, normalizedRequiredPath, StringComparison.Ordinal)) continue;
                    found = true;
                    if (enabled)
                    {
                        enabledFound = true;
                        break;
                    }
                }

                if (!found) missing.Add(required);
                else if (!enabledFound) disabled.Add(required);
            }

            if (missing.Count == 0 && disabled.Count == 0)
            {
                return PreflightResult.Pass(
                    "必須シーンはすべて登録済み・有効: " + string.Join(", ", requiredScenePaths));
            }

            var parts = new List<string>();
            if (missing.Count > 0) parts.Add("未登録: " + string.Join(", ", missing));
            if (disabled.Count > 0) parts.Add("無効化: " + string.Join(", ", disabled));

            return PreflightResult.Fail(
                string.Join(" / ", parts),
                "File > Build Settings でシーンを追加・有効化する（過去事故: 未登録シーンで " +
                "RegisterSceneObjects 不発 → シーン配置 NetworkObject が同期しない）。");
        }
    }
}
