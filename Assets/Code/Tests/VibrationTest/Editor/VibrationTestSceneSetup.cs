using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace MyFolder.Tests.Vibration
{
    /// <summary>
    /// VibrationTest シーンをエディタから自動構築するユーティリティ。
    /// Menu: Tools > VibrationTest > Create Test Scene
    /// </summary>
    public static class VibrationTestSceneSetup
    {
        private const string ScenePath = "Assets/Level/Scenes/VibrationTest.unity";

        [MenuItem("Tools/VibrationTest/Create Test Scene")]
        public static void CreateTestScene()
        {
            // 新規シーン作成
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

            // ── カメラ設定 ──────────────────────────────────────────
            Camera mainCam = Camera.main;
            if (mainCam != null)
            {
                mainCam.transform.position = new Vector3(2.5f, 4f, -12f);
                mainCam.transform.rotation = Quaternion.Euler(15f, 0f, 0f);
                mainCam.backgroundColor = new Color(0.1f, 0.1f, 0.15f);
                mainCam.clearFlags = CameraClearFlags.SolidColor;
            }

            // ── 地面プレーン ─────────────────────────────────────────
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.position = Vector3.zero;
            ground.transform.localScale = new Vector3(5f, 1f, 3f);
            var groundMat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            if (groundMat.shader.name == "Hidden/InternalErrorShader")
                groundMat = new Material(Shader.Find("Standard"));
            groundMat.color = new Color(0.25f, 0.25f, 0.3f);
            ground.GetComponent<Renderer>().material = groundMat;

            // ── テストハーネス GameObject ────────────────────────────
            GameObject harnessGO = new GameObject("VibrationTestHarness");
            var harness = harnessGO.AddComponent<VibrationTestHarness>();

            // デフォルト設定
            harness.testDurationSeconds = 3f;
            harness.cooldownSeconds = 0.3f;
            harness.t1Spring = 5000f;
            harness.t1DamperZero = 0f;
            harness.t1DamperGood = 500f;
            harness.t2JointSpring = 5000f;
            harness.t2JointDamper = 500f;
            harness.t2CorrectionKp = 45f;
            harness.t2CorrectionKd = 8f;
            harness.t3KpUnderdamped = 45f;
            harness.t3KdUnderdamped = 8f;
            harness.t3KpCritical = 20f;
            harness.t3KdCritical = 15f;
            harness.t4FlipFrequencyHz = 20f;
            harness.t5LeadSecondsHigh = 0.06f;
            harness.t5LeadSecondsZero = 0f;
            harness.t5SimulatedVelocity = 5f;

            // ── ラベル用 GameObject（各テスト位置に配置）──────────────
            CreateLabel("T1: JointDrive Damper", new Vector3(-10f, 0.1f, 2f));
            CreateLabel("T2: Force Fight",       new Vector3(-5f,  0.1f, 2f));
            CreateLabel("T3: Kp/Kd Ratio",       new Vector3(0f,   0.1f, 2f));
            CreateLabel("T4: Balance Flip",       new Vector3(5f,   0.1f, 2f));
            CreateLabel("T5: Prediction Lead",    new Vector3(10f,  0.1f, 2f));

            // ── シーン保存 ───────────────────────────────────────────
            EditorSceneManager.SaveScene(scene, ScenePath);
            Debug.Log($"[VibrationTestSetup] Scene saved to: {ScenePath}");
            Debug.Log("[VibrationTestSetup] ▶ Play モードで Space キーを押すとテスト開始します。");

            // Build Settings に追加
            AddSceneToBuildSettings(ScenePath);
        }

        [MenuItem("Tools/VibrationTest/Open Test Scene")]
        public static void OpenTestScene()
        {
            if (System.IO.File.Exists(ScenePath))
            {
                EditorSceneManager.OpenScene(ScenePath);
            }
            else
            {
                Debug.LogWarning($"[VibrationTestSetup] Scene not found at {ScenePath}. Run 'Create Test Scene' first.");
                CreateTestScene();
            }
        }

        private static void CreateLabel(string text, Vector3 pos)
        {
            var go = new GameObject($"Label_{text}");
            go.transform.position = pos;
            // エディタ上での識別用（実行時は非表示）
        }

        private static void AddSceneToBuildSettings(string scenePath)
        {
            var scenes = EditorBuildSettings.scenes;
            foreach (var s in scenes)
            {
                if (s.path == scenePath) return; // 既に登録済み
            }

            var newScenes = new EditorBuildSettingsScene[scenes.Length + 1];
            System.Array.Copy(scenes, newScenes, scenes.Length);
            newScenes[scenes.Length] = new EditorBuildSettingsScene(scenePath, true);
            EditorBuildSettings.scenes = newScenes;
            Debug.Log($"[VibrationTestSetup] Added '{scenePath}' to Build Settings.");
        }
    }
}
