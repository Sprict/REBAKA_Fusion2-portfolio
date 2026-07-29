using UnityEngine;
using MyFolder.Tests.Vibration;
using System.Text;

/// <summary>
/// テスト完了後に結果を収集して返す。
/// </summary>
public static class CollectVibrationResults
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var harness = Object.FindFirstObjectByType<VibrationTestHarness>();
        if (harness == null)
            return "ERROR: VibrationTestHarness not found.";

        if (harness.IsRunning)
            return $"RUNNING: Tests still in progress... ({harness.Results.Count} done so far)";

        var results = harness.Results;
        if (results.Count == 0)
            return "NO_RESULTS: No tests completed yet.";

        var sb = new StringBuilder();
        sb.AppendLine($"=== VIBRATION TEST RESULTS ({results.Count} tests) ===");
        sb.AppendLine();

        // ペア比較
        AppendPairComparison(sb, results, "要因1: JointDriveダンパー=0",
            "T1a_JointDrive_Damper0", "T1b_JointDrive_DamperGood");
        AppendPairComparison(sb, results, "要因2: UpdatePhysicsVisualOnly力競合",
            "T2a_JointDriveOnly", "T2b_JointDrive_Plus_AddForce");
        AppendPairComparison(sb, results, "要因3: Kp/Kd減衰不足",
            "T3a_Kp45_Kd8_Underdamped", "T3b_Kp20_Kd15_Critical");
        AppendPairComparison(sb, results, "要因4: バランス判定フリップ",
            "T4a_NoBalanceFlip", "T4b_BalanceFlip");
        AppendPairComparison(sb, results, "要因5: 予測先読みオーバーシュート",
            "T5a_NoPrediction", "T5b_Prediction_0_06s");

        sb.AppendLine();
        sb.AppendLine("=== 全テスト詳細 ===");
        foreach (var r in results)
        {
            string verdict = r.IsVibrating ? "[VIBRATING]" : "[STABLE]   ";
            sb.AppendLine($"{verdict} {r.TestName}");
            sb.AppendLine($"  oscFreq={r.OscillationFreqHz:F2}Hz  maxAngVel={r.MaxAngularVelocity:F3}rad/s  posRange={r.PositionRange:F4}m  samples={r.SampleCount}");
        }

        string output = sb.ToString();
        Debug.Log(output);
        return output;
    }

    private static void AppendPairComparison(StringBuilder sb, System.Collections.Generic.IReadOnlyList<TestResult> results,
        string factorName, string baselineKey, string problemKey)
    {
        TestResult baseline = null, problem = null;
        foreach (var r in results)
        {
            if (r.TestName == baselineKey) baseline = r;
            if (r.TestName == problemKey) problem = r;
        }

        sb.AppendLine($"── {factorName} ──");
        if (baseline == null || problem == null)
        {
            sb.AppendLine("  データ不足（テスト未完了）");
            sb.AppendLine();
            return;
        }

        bool baseVib = baseline.IsVibrating;
        bool probVib = problem.IsVibrating;
        float freqDelta = problem.OscillationFreqHz - baseline.OscillationFreqHz;
        float angDelta  = problem.MaxAngularVelocity  - baseline.MaxAngularVelocity;

        string verdict;
        string impact;
        if (!baseVib && probVib)
        {
            verdict = "🔴 CONFIRMED";
            impact  = "この要因単体で振動を引き起こす";
        }
        else if (!baseVib && !probVib && (freqDelta > 1.5f || angDelta > 3f))
        {
            verdict = "🟠 PARTIAL";
            impact  = $"振動増加傾向あり（freqΔ={freqDelta:+0.0;-0.0}Hz, angVelΔ={angDelta:+0.0;-0.0}）";
        }
        else if (baseVib && probVib)
        {
            verdict = "🟡 BOTH_VIB";
            impact  = "両方振動（他要因が支配的か閾値設定を確認）";
        }
        else
        {
            verdict = "⚪ NOT_CONFIRMED";
            impact  = "単体では振動差なし";
        }

        sb.AppendLine($"  判定: {verdict} — {impact}");
        sb.AppendLine($"  baseline [{baselineKey}]: osc={baseline.OscillationFreqHz:F2}Hz  angVel={baseline.MaxAngularVelocity:F3}  posRange={baseline.PositionRange:F4}m");
        sb.AppendLine($"  problem  [{problemKey}]: osc={problem.OscillationFreqHz:F2}Hz  angVel={problem.MaxAngularVelocity:F3}  posRange={problem.PositionRange:F4}m");
        sb.AppendLine();
    }
}
