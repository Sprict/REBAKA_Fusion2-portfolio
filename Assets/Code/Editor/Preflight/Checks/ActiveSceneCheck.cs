using System;
using UnityEngine.SceneManagement;

namespace Rebaka.Editor.Preflight
{
    public sealed class ActiveSceneCheck : IPreflightCheck
    {
        public string Name => "Active Scene";
        public string ExpectedScenePath { get; }

        public ActiveSceneCheck(string expectedScenePath)
        {
            ExpectedScenePath = expectedScenePath ?? throw new ArgumentNullException(nameof(expectedScenePath));
        }

        public PreflightResult Run()
        {
            return Evaluate(SceneManager.GetActiveScene().path, ExpectedScenePath);
        }

        public static PreflightResult Evaluate(string actualScenePath, string expectedScenePath)
        {
            string actual = NormalizeScenePath(actualScenePath);
            string expected = NormalizeScenePath(expectedScenePath);
            if (!MatchesExpectedScenePath(actualScenePath, expectedScenePath))
            {
                return PreflightResult.Fail(
                    $"対象シーンが開かれていません。期待: {expected} / 現在: {actual}",
                    $"{expectedScenePath} をロードし、Hierarchy の scene header から Set Active Scene にして再実行してください。ほかのsceneを閉じる必要はありません。");
            }

            return PreflightResult.Pass($"対象シーンを確認: {expected}");
        }

        public static bool MatchesExpectedScenePath(string actualPath, string expectedPath)
        {
            string actual = NormalizeScenePath(actualPath);
            string expected = NormalizeScenePath(expectedPath);
            return !string.IsNullOrEmpty(actual) && string.Equals(actual, expected, StringComparison.Ordinal);
        }

        public static string NormalizeScenePath(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }
    }
}
