using UnityEditor;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor
{
    public class ProbePlacementWindow : EditorWindow
    {
        private ProbePlacementSettings settings = new ProbePlacementSettings();
        private PreviewResult? lastPreview;
        private Vector2 scroll;
        private bool showPreview = true;
        private SerializedObject serialized;

        [MenuItem("Tools/Lighting/Indoor Probe Placer")]
        public static void Open()
        {
            var win = GetWindow<ProbePlacementWindow>("Indoor Probe Placer");
            win.minSize = new Vector2(360, 480);
            win.Show();
        }

        private void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGui;
        }

        private void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGui;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Indoor Probe Placer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "屋内マップ向けに Light Probe Group と Reflection Probe を自動配置します。Switch 60fps 想定の既定値。",
                MessageType.Info);

            scroll = EditorGUILayout.BeginScrollView(scroll);

            settings.root = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Root GameObject", "マップのルート。配下の Renderer/Collider から屋内を判定します。"),
                settings.root, typeof(GameObject), true);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Volume Detection", EditorStyles.boldLabel);
            settings.floorMask = LayerMaskField("Floor Layer Mask", settings.floorMask);
            settings.wallMask = LayerMaskField("Wall Layer Mask", settings.wallMask);
            settings.cellSize = EditorGUILayout.Slider(
                new GUIContent("Grid Cell Size (m)", "XZ グリッド間隔。小さいほど密・重い。"),
                settings.cellSize, 1.0f, 5.0f);
            settings.maxFloorHeightJump = EditorGUILayout.Slider(
                new GUIContent("Max Floor Jump (m)", "階段/段差のセグメント許容段差。"),
                settings.maxFloorHeightJump, 0.0f, 3.0f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Indoor / Outdoor Filter", EditorStyles.boldLabel);
            settings.requireCeiling = EditorGUILayout.Toggle(
                new GUIContent("Require Ceiling",
                    "ON にすると、床の真上に天井/壁/床 Collider がある場所だけを屋内とみなします。" +
                    "屋外でも同じ floor レイヤーを使うシーンで屋外を除外するために使用。"),
                settings.requireCeiling);
            using (new EditorGUI.DisabledScope(!settings.requireCeiling))
            {
                settings.ceilingMask = LayerMaskField("Ceiling Layer Mask", settings.ceilingMask);
                settings.minCeilingHeight = EditorGUILayout.Slider(
                    new GUIContent("Min Ceiling Height (m)",
                        "この高さ未満の空間は屋内扱いしない (家具の下/隙間除外)。"),
                    settings.minCeilingHeight, 0.0f, 3.0f);
                settings.maxCeilingHeight = EditorGUILayout.Slider(
                    new GUIContent("Max Ceiling Height (m)",
                        "この高さ以内に天井が見つからなければ屋外扱い。"),
                    settings.maxCeilingHeight, 1.0f, 30.0f);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Light Probe Grid", EditorStyles.boldLabel);
            DrawVerticalLayers();
            settings.maxGapMultiplier = EditorGUILayout.Slider(
                new GUIContent("Max Gap Multiplier", "冗長除去後の最大許容ギャップ倍率。"),
                settings.maxGapMultiplier, 1.2f, 2.5f);
            settings.wallProximityRadius = EditorGUILayout.Slider(
                new GUIContent("Wall Proximity (m)", "壁からこの距離以内の候補は密度を維持。"),
                settings.wallProximityRadius, 0.1f, 2.0f);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reflection Probes", EditorStyles.boldLabel);
            settings.enableReflectionProbes = EditorGUILayout.Toggle(
                "Enable Reflection Probes", settings.enableReflectionProbes);
            using (new EditorGUI.DisabledScope(!settings.enableReflectionProbes))
            {
                settings.reflectionProbeResolution = ResolutionPopup(
                    "Resolution", settings.reflectionProbeResolution);
                settings.reflectionHdr = EditorGUILayout.Toggle("HDR", settings.reflectionHdr);
                settings.smoothnessThreshold = EditorGUILayout.Slider(
                    new GUIContent("Smoothness Threshold", "この値以上の Renderer 近傍に追加 RP を置く。"),
                    settings.smoothnessThreshold, 0.3f, 1.0f);
                settings.reflectionBlendDistance = EditorGUILayout.Slider(
                    new GUIContent("Blend Distance (m)", "Switch では 0 推奨。"),
                    settings.reflectionBlendDistance, 0f, 1f);
            }

            EditorGUILayout.Space();
            showPreview = EditorGUILayout.ToggleLeft("Show Scene Preview Gizmos", showPreview);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Preview", GUILayout.Height(26)))
                {
                    RunPreview();
                }
                using (new EditorGUI.DisabledScope(settings.root == null))
                {
                    if (GUILayout.Button("Apply", GUILayout.Height(26)))
                    {
                        ProbePlacementRunner.Apply(settings);
                        RunPreview();
                    }
                    if (GUILayout.Button("Clear", GUILayout.Height(26)))
                    {
                        ProbePlacementRunner.Clear(settings);
                        lastPreview = null;
                        SceneView.RepaintAll();
                    }
                }
            }

            if (lastPreview.HasValue)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Stats", EditorStyles.boldLabel);
                var pr = lastPreview.Value;
                EditorGUILayout.LabelField($"Light Probes: {pr.lightProbeCount}");
                EditorGUILayout.LabelField($"Rooms: {pr.roomCount}");
                EditorGUILayout.LabelField($"Reflection Probes: {pr.reflectionProbeCount}");
                EditorGUILayout.LabelField($"Bounds Size: {pr.worldBounds.size:F1}");

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Diagnostics", EditorStyles.miniBoldLabel);
                EditorGUILayout.LabelField($"Renderers: {pr.debugRendererCount}");
                EditorGUILayout.LabelField($"Floor Hits: {pr.debugFloorHits}");
                EditorGUILayout.LabelField($"Rejected by Ceiling Filter: {pr.debugCeilingRejected}");
                EditorGUILayout.LabelField($"Accepted Cells: {pr.debugCellCount}");

                // ゼロ結果の原因を UI 上でもヒント表示
                if (pr.debugCellCount == 0)
                {
                    string hint;
                    if (pr.debugRendererCount == 0)
                        hint = "Root 配下に Renderer がありません。Root を指定してください。";
                    else if (pr.debugFloorHits == 0)
                        hint = "床 Collider が当たりません。Floor Layer Mask と床の Collider を確認。";
                    else if (settings.requireCeiling && pr.debugCeilingRejected == pr.debugFloorHits)
                        hint = "床は検出されましたが、全て『頭上 Collider なし』で屋外判定されました。\n" +
                               "対処: Require Ceiling を OFF にする / Max Ceiling Height を上げる / Ceiling Layer Mask を広げる";
                    else
                        hint = "候補は見つかりましたが最終的に 0 になりました。Vertical Layers と壁近傍判定を確認。";
                    EditorGUILayout.HelpBox(hint, MessageType.Warning);
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void RunPreview()
        {
            if (settings.root == null)
            {
                Debug.LogWarning("[ProbePlacement] Root GameObject is required.");
                return;
            }
            lastPreview = ProbePlacementRunner.Preview(settings);
            SceneView.RepaintAll();
            Repaint();
        }

        private void DrawVerticalLayers()
        {
            EditorGUILayout.LabelField(new GUIContent(
                "Vertical Layers (m)", "床を基準とした高さ方向のプローブレイヤー。"));
            using (new EditorGUI.IndentLevelScope())
            {
                for (int i = 0; i < settings.verticalLayers.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        settings.verticalLayers[i] = EditorGUILayout.FloatField(
                            $"Layer {i}", settings.verticalLayers[i]);
                        if (GUILayout.Button("-", GUILayout.Width(22)))
                        {
                            settings.verticalLayers.RemoveAt(i);
                            GUIUtility.ExitGUI();
                        }
                    }
                }
                if (GUILayout.Button("+ Add Layer"))
                {
                    float last = settings.verticalLayers.Count > 0
                        ? settings.verticalLayers[settings.verticalLayers.Count - 1] + 1.0f
                        : 0.2f;
                    settings.verticalLayers.Add(last);
                }
            }
        }

        private static LayerMask LayerMaskField(string label, LayerMask mask)
        {
            var options = new string[32];
            for (int i = 0; i < 32; i++) options[i] = LayerMask.LayerToName(i);
            int picked = EditorGUILayout.MaskField(label, mask.value, options);
            return (LayerMask)picked;
        }

        private static int ResolutionPopup(string label, int current)
        {
            int[] values = { 32, 64, 128, 256 };
            string[] labels = { "32", "64", "128", "256" };
            int idx = 1;
            for (int i = 0; i < values.Length; i++) if (values[i] == current) { idx = i; break; }
            idx = EditorGUILayout.Popup(label, idx, labels);
            return values[idx];
        }

        private void OnSceneGui(SceneView view)
        {
            if (!showPreview || !lastPreview.HasValue) return;
            var pr = lastPreview.Value;

            Handles.color = new Color(1f, 0.85f, 0.25f, 0.9f);
            if (pr.lightProbePositions != null)
            {
                foreach (var p in pr.lightProbePositions)
                    Handles.SphereHandleCap(0, p, Quaternion.identity, 0.12f, EventType.Repaint);
            }

            if (pr.reflectionProbes != null)
            {
                foreach (var spec in pr.reflectionProbes)
                {
                    Handles.color = spec.importance >= 2
                        ? new Color(0.2f, 0.8f, 1f, 0.9f)
                        : new Color(0.4f, 1f, 0.4f, 0.9f);
                    Handles.DrawWireCube(spec.position + spec.center, spec.size);
                    Handles.SphereHandleCap(0, spec.position, Quaternion.identity, 0.25f, EventType.Repaint);
                }
            }
        }
    }
}
