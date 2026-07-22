using UnityEngine;
using MyFolder.Tests.Vibration;
using System.Reflection;

/// <summary>
/// ハーネスの内部状態をリフレクションで確認する診断スクリプト。
/// </summary>
public static class CheckHarnessState
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var harness = Object.FindFirstObjectByType<VibrationTestHarness>();
        if (harness == null)
            return "ERROR: VibrationTestHarness not found in scene.";

        var type = harness.GetType();

        var phaseField = type.GetField("_phase", BindingFlags.NonPublic | BindingFlags.Instance);
        var timerField = type.GetField("_phaseTimer", BindingFlags.NonPublic | BindingFlags.Instance);
        var indexField = type.GetField("_currentTestIndex", BindingFlags.NonPublic | BindingFlags.Instance);
        var casesField = type.GetField("_testCases", BindingFlags.NonPublic | BindingFlags.Instance);
        var ctxField   = type.GetField("_currentCtx", BindingFlags.NonPublic | BindingFlags.Instance);

        string phase = phaseField?.GetValue(harness)?.ToString() ?? "N/A";
        float timer  = timerField?.GetValue(harness) is float t ? t : -1f;
        string index = indexField?.GetValue(harness)?.ToString() ?? "N/A";
        int casesCount = (casesField?.GetValue(harness) as System.Collections.IList)?.Count ?? -1;
        var ctx = ctxField?.GetValue(harness) as TestRigContext;

        string rootInfo = "null";
        if (ctx?.Root != null)
        {
            var rb = ctx.Root;
            rootInfo = $"pos={rb.position} vel={rb.linearVelocity} kinematic={rb.isKinematic}";
        }

        // FixedUpdateが動いているか確認するため、手動でFixedUpdateを1回呼ぶ
        // (リフレクション経由)
        var fixedUpdateMethod = type.GetMethod("FixedUpdate",
            BindingFlags.NonPublic | BindingFlags.Instance);
        fixedUpdateMethod?.Invoke(harness, null);

        float timerAfter = timerField?.GetValue(harness) is float t2 ? t2 : -1f;

        string result =
            $"Phase={phase}\n" +
            $"PhaseTimer before={timer:F4} after={timerAfter:F4} (delta={timerAfter - timer:F4})\n" +
            $"TestIndex={index}/{casesCount}\n" +
            $"IsRunning={harness.IsRunning}\n" +
            $"ResultsCount={harness.Results.Count}\n" +
            $"CurrentRig={rootInfo}\n" +
            $"Time.time={Time.time:F3}\n" +
            $"Time.fixedTime={Time.fixedTime:F3}\n" +
            $"Time.fixedDeltaTime={Time.fixedDeltaTime:F4}\n" +
            $"Application.isPlaying={Application.isPlaying}";

        Debug.Log("[CheckHarnessState] " + result);
        return result;
    }
}
