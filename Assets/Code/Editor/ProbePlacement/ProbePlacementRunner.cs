using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace MyProject.Tools.ProbePlacement.Editor
{
    public struct PreviewResult
    {
        public Vector3[] lightProbePositions;
        public ReflectionProbePlacer.ProbeSpec[] reflectionProbes;
        public int roomCount;
        public Bounds worldBounds;
        public int debugRendererCount;
        public int debugFloorHits;
        public int debugCeilingRejected;
        public int debugCellCount;
        public int lightProbeCount => lightProbePositions?.Length ?? 0;
        public int reflectionProbeCount => reflectionProbes?.Length ?? 0;
    }

    public static class ProbePlacementRunner
    {
        public static PreviewResult Preview(ProbePlacementSettings s)
        {
            var det = IndoorVolumeDetector.Detect(s);
            var lp = LightProbeGridBuilder.Build(det, s);
            var rp = ReflectionProbePlacer.Build(det, s);

            int cellCount = det.cells?.Length ?? 0;
            Debug.Log(
                $"[ProbePlacement] Preview: renderers={det.debugRendererCount} " +
                $"floorHits={det.debugFloorHits} ceilingRejected={det.debugCeilingRejected} " +
                $"cells={cellCount} rooms={det.roomCount} " +
                $"lightProbes={lp.Length} reflectionProbes={rp.Length}");

            if (cellCount == 0)
            {
                if (det.debugRendererCount == 0)
                    Debug.LogWarning("[ProbePlacement] Root 配下に Renderer が見つかりません。Root 指定とアクティブ状態を確認してください。");
                else if (det.debugFloorHits == 0)
                    Debug.LogWarning("[ProbePlacement] 床 Collider が検出されませんでした。Floor Layer Mask と床オブジェクトの Collider を確認してください。");
                else if (s.requireCeiling && det.debugCeilingRejected == det.debugFloorHits)
                    Debug.LogWarning(
                        $"[ProbePlacement] 床 {det.debugFloorHits} 箇所すべてが『頭上 Collider なし』で屋外判定されました。" +
                        "Require Ceiling を OFF にするか、Max Ceiling Height (既定 8m) を上げる、" +
                        "または Ceiling Layer Mask を見直してください。");
            }

            return new PreviewResult
            {
                lightProbePositions = lp,
                reflectionProbes = rp,
                roomCount = det.roomCount,
                worldBounds = det.worldBounds,
                debugRendererCount = det.debugRendererCount,
                debugFloorHits = det.debugFloorHits,
                debugCeilingRejected = det.debugCeilingRejected,
                debugCellCount = cellCount,
            };
        }

        public static GameObject Apply(ProbePlacementSettings s)
        {
            if (s == null || s.root == null)
            {
                Debug.LogWarning("[ProbePlacement] Root GameObject is required.");
                return null;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Indoor Probe Placement");

            Clear(s);
            var preview = Preview(s);

            if (preview.lightProbeCount == 0 && preview.reflectionProbeCount == 0)
            {
                Debug.LogWarning("[ProbePlacement] No probes generated. Check root, floor mask, and cell size.");
                return null;
            }

            GameObject groupGo = null;
            if (preview.lightProbeCount > 0)
            {
                groupGo = new GameObject(ProbePlacementSettings.AutoProbeGroupName);
                groupGo.transform.position = preview.worldBounds.center;
                var group = groupGo.AddComponent<LightProbeGroup>();

                var localPositions = new Vector3[preview.lightProbePositions.Length];
                Vector3 origin = groupGo.transform.position;
                for (int i = 0; i < localPositions.Length; i++)
                    localPositions[i] = preview.lightProbePositions[i] - origin;
                group.probePositions = localPositions;
                Undo.RegisterCreatedObjectUndo(groupGo, "Apply Indoor Probe Placement");
            }

            foreach (var spec in preview.reflectionProbes)
            {
                var pgo = new GameObject(spec.name);
                pgo.transform.position = spec.position;
                var rp = pgo.AddComponent<ReflectionProbe>();
                rp.mode = ReflectionProbeMode.Baked;
                rp.resolution = s.reflectionProbeResolution;
                rp.hdr = s.reflectionHdr;
                rp.boxProjection = true;
                rp.size = spec.size;
                rp.center = spec.center;
                rp.importance = spec.importance;
                rp.blendDistance = s.reflectionBlendDistance;
                Undo.RegisterCreatedObjectUndo(pgo, "Apply Indoor Probe Placement");
            }

            Undo.CollapseUndoOperations(undoGroup);

            var activeScene = EditorSceneManager.GetActiveScene();
            EditorSceneManager.MarkSceneDirty(activeScene);

            Debug.Log($"[ProbePlacement] Applied: {preview.lightProbeCount} light probes, " +
                      $"{preview.reflectionProbeCount} reflection probes, {preview.roomCount} rooms.");
            return groupGo;
        }

        public static int Clear(ProbePlacementSettings s)
        {
            int removed = 0;
            var all = Object.FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var go in all)
            {
                if (go == null) continue;
                if (!go.name.StartsWith(ProbePlacementSettings.AutoProbePrefix)) continue;
                Undo.DestroyObjectImmediate(go);
                removed++;
            }
            if (removed > 0)
                Debug.Log($"[ProbePlacement] Cleared {removed} auto-placed probe object(s).");
            return removed;
        }
    }
}
