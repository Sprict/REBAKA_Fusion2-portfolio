using System.Collections.Generic;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor
{
    public static class ReflectionProbePlacer
    {
        public struct ProbeSpec
        {
            public string name;
            public Vector3 position;
            public Vector3 size;
            public Vector3 center;
            public int importance;
            public int roomId;
        }

        public static ProbeSpec[] Build(DetectionResult detection, ProbePlacementSettings settings)
        {
            if (!settings.enableReflectionProbes || detection.cells == null || detection.cells.Length == 0)
                return System.Array.Empty<ProbeSpec>();

            int wallMask = settings.wallMask.value;

            // Group cells by room
            var roomCells = new Dictionary<int, List<IndoorCell>>();
            foreach (var c in detection.cells)
            {
                if (!roomCells.TryGetValue(c.roomId, out var list))
                {
                    list = new List<IndoorCell>();
                    roomCells[c.roomId] = list;
                }
                list.Add(c);
            }

            var result = new List<ProbeSpec>();

            foreach (var kv in roomCells)
            {
                var cells = kv.Value;
                Vector3 sum = Vector3.zero;
                Bounds aabb = new Bounds(cells[0].floorPoint, Vector3.zero);
                foreach (var cc in cells)
                {
                    sum += cc.floorPoint;
                    aabb.Encapsulate(cc.floorPoint);
                }
                Vector3 centroid = sum / cells.Count;

                float ceilY = detection.worldMaxY;
                if (Physics.Raycast(centroid + Vector3.up * 0.1f, Vector3.up, out RaycastHit hitUp,
                        (detection.worldMaxY - centroid.y) + 2f, wallMask, QueryTriggerInteraction.Ignore))
                {
                    ceilY = hitUp.point.y;
                }

                float midY = (centroid.y + ceilY) * 0.5f;
                Vector3 pos = new Vector3(centroid.x, midY, centroid.z);

                Vector3 size = new Vector3(
                    Mathf.Max(settings.cellSize * 2f, aabb.size.x + settings.cellSize),
                    Mathf.Max(settings.cellSize, ceilY - detection.worldMinY),
                    Mathf.Max(settings.cellSize * 2f, aabb.size.z + settings.cellSize)
                );

                Vector3 boxCenterWorld = new Vector3(centroid.x, (detection.worldMinY + ceilY) * 0.5f, centroid.z);
                Vector3 center = boxCenterWorld - pos;

                result.Add(new ProbeSpec
                {
                    name = $"{ProbePlacementSettings.AutoProbePrefix} ReflectionProbe_Room{kv.Key}",
                    position = pos,
                    size = size,
                    center = center,
                    importance = 1,
                    roomId = kv.Key,
                });
            }

            // Specular renderer additions
            var renderers = settings.root.GetComponentsInChildren<Renderer>();
            foreach (var rend in renderers)
            {
                if (rend.sharedMaterial == null) continue;
                var mat = rend.sharedMaterial;
                bool qualifies = false;
                if (mat.HasProperty("_Smoothness") && mat.GetFloat("_Smoothness") >= settings.smoothnessThreshold)
                    qualifies = true;
                if (mat.HasProperty("_Metallic") && mat.GetFloat("_Metallic") >= 0.5f)
                    qualifies = true;
                if (!qualifies) continue;

                Vector3 rcenter = rend.bounds.center;
                float minDist = float.MaxValue;
                foreach (var rp in result)
                    minDist = Mathf.Min(minDist, Vector3.Distance(rcenter, rp.position));
                if (minDist < settings.cellSize * 2f) continue;

                Vector3 specSize = Vector3.one * Mathf.Max(settings.cellSize * 3f, rend.bounds.size.magnitude);
                result.Add(new ProbeSpec
                {
                    name = $"{ProbePlacementSettings.AutoProbePrefix} ReflectionProbe_Spec_{rend.gameObject.name}",
                    position = rcenter,
                    size = specSize,
                    center = Vector3.zero,
                    importance = 2,
                    roomId = -1,
                });
            }

            return result.ToArray();
        }
    }
}
