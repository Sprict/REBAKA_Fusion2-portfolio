using UnityEngine;
using UnityEditor;
using MyFolder.Tests.Vibration;

public static class AddMultiJointToScene
{
    [MenuItem("Tools/VibrationTest/Add MultiJoint Test to Scene")]
    public static void Execute()
    {
        // 既存のVibrationTestHarnessオブジェクトを探す
        var harness = Object.FindFirstObjectByType<VibrationTestHarness>();
        if (harness == null)
        {
            Debug.LogError("VibrationTestHarness not found. Open VibrationTest scene first.");
            return;
        }

        // 同じGameObjectにMultiJointVibrationTestを追加（なければ）
        var existing = harness.GetComponent<MultiJointVibrationTest>();
        if (existing != null)
        {
            Debug.Log("MultiJointVibrationTest already exists on VibrationTestHarness.");
            return;
        }

        var mjt = harness.gameObject.AddComponent<MultiJointVibrationTest>();
        mjt.testDurationSeconds = 5f;
        mjt.cooldownSeconds = 0.5f;
        mjt.chainSegments = 5;
        mjt.segmentMass = 1f;
        mjt.springStrength = 5000f;
        mjt.damperRatio = 0.1f;
        mjt.correctionKp = 45f;
        mjt.correctionKd = 8f;
        mjt.correctionKpFixed = 20f;
        mjt.correctionKdFixed = 15f;
        mjt.predictionLead = 0.06f;
        mjt.flipFrequencyHz = 25f;

        EditorUtility.SetDirty(harness.gameObject);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
            UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());
        UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();

        Debug.Log("MultiJointVibrationTest added to VibrationTestHarness and scene saved.");
    }
}
