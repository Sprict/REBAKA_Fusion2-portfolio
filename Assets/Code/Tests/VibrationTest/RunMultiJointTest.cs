using UnityEngine;
using MyFolder.Tests.Vibration;
using System.Text;

public static class RunMultiJointTest
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var test = Object.FindFirstObjectByType<MultiJointVibrationTest>();
        if (test == null)
            return "ERROR: MultiJointVibrationTest not found in scene.";

        if (test.IsRunning)
            return $"RUNNING: Tests in progress... ({test.Results.Count} done)";

        if (test.Results.Count > 0)
        {
            // 結果を返す
            var sb = new StringBuilder();
            sb.AppendLine($"=== MULTI-JOINT TEST RESULTS ({test.Results.Count} tests) ===\n");

            foreach (var r in test.Results)
            {
                string v = r.IsVibrating ? "[VIBRATING]" : "[STABLE]   ";
                sb.AppendLine($"{v} {r.TestName}");
                sb.AppendLine($"  {r.Description}");
                sb.AppendLine($"  oscFreq={r.RootOscFreqHz:F2}Hz  maxChainAngVel={r.MaxChainAngVel:F3}rad/s  " +
                              $"posRange={r.RootPosRange:F4}m  chainAmp={r.ChainAmplification:F2}x  samples={r.SampleCount}");
                sb.AppendLine();
            }

            // 修正前後比較
            MultiJointResult m1 = null, m2 = null;
            foreach (var r in test.Results)
            {
                if (r.TestName.StartsWith("M1")) m1 = r;
                if (r.TestName.StartsWith("M2")) m2 = r;
            }
            if (m1 != null && m2 != null)
            {
                sb.AppendLine("── 修正効果 ──");
                float oscR = m1.RootOscFreqHz > 0.001f ? (1f - m2.RootOscFreqHz / m1.RootOscFreqHz) * 100f : 0f;
                float angR = m1.MaxChainAngVel > 0.001f ? (1f - m2.MaxChainAngVel / m1.MaxChainAngVel) * 100f : 0f;
                float posR = m1.RootPosRange > 0.0001f ? (1f - m2.RootPosRange / m1.RootPosRange) * 100f : 0f;
                sb.AppendLine($"  振動周波数: {m1.RootOscFreqHz:F1}Hz -> {m2.RootOscFreqHz:F1}Hz ({oscR:+0.0;-0.0}%)");
                sb.AppendLine($"  最大角速度: {m1.MaxChainAngVel:F2} -> {m2.MaxChainAngVel:F2} rad/s ({angR:+0.0;-0.0}%)");
                sb.AppendLine($"  位置振幅:   {m1.RootPosRange:F4} -> {m2.RootPosRange:F4} m ({posR:+0.0;-0.0}%)");

                if (m1.IsVibrating && !m2.IsVibrating)
                    sb.AppendLine("  🟢 修正により振動が解消されました！");
                else if (m1.IsVibrating && m2.IsVibrating)
                    sb.AppendLine("  🟡 振動は軽減されましたが完全には解消されていません");
                else if (!m1.IsVibrating)
                    sb.AppendLine("  ⚪ M1も安定（テスト条件の調整が必要）");
            }

            return sb.ToString();
        }

        // テスト開始
        test.RunTests();
        return "OK: Multi-joint tests started. (~16s)";
    }
}
