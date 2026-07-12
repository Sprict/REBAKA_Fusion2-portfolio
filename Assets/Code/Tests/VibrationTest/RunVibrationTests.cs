using UnityEngine;
using MyFolder.Tests.Vibration;

/// <summary>
/// execute_script から呼び出してテストを開始するエントリポイント。
/// </summary>
public static class RunVibrationTests
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var harness = Object.FindFirstObjectByType<VibrationTestHarness>();
        if (harness == null)
            return "ERROR: VibrationTestHarness not found in scene.";

        if (harness.IsRunning)
            return "INFO: Tests already running.";

        harness.RunAllTestsExternal();
        return "OK: Tests started. Check Console for results (takes ~35s).";
    }
}
