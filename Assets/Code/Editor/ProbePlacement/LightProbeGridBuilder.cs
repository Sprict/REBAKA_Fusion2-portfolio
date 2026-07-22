using System.Collections.Generic;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor
{
    public static class LightProbeGridBuilder
    {
        private static readonly Vector2Int[] Neighbors4 =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0),
            new Vector2Int(0, 1), new Vector2Int(0, -1),
        };

        private struct Candidate
        {
            public Vector3 position;
            public Vector2Int gridIndex;
            public int layerIdx;
            public bool alive;
            public bool nearWall;
        }

        public static Vector3[] Build(DetectionResult detection, ProbePlacementSettings settings)
        {
            if (detection.cells == null || detection.cells.Length == 0 ||
                settings.verticalLayers == null || settings.verticalLayers.Count == 0)
                return System.Array.Empty<Vector3>();

            int wallMask = settings.wallMask.value;
            var layers = settings.verticalLayers;
            var candidates = new List<Candidate>(detection.cells.Length * layers.Count);

            // Step 1: Generate candidates
            foreach (var cell in detection.cells)
            {
                for (int li = 0; li < layers.Count; li++)
                {
                    float y = layers[li];
                    Vector3 p = cell.floorPoint + Vector3.up * y;

                    if (Physics.CheckSphere(p, 0.05f, wallMask, QueryTriggerInteraction.Ignore))
                        continue;

                    if (Physics.Raycast(cell.floorPoint + Vector3.up * 0.02f, Vector3.up,
                            out RaycastHit ceil, y - 0.1f, wallMask, QueryTriggerInteraction.Ignore))
                    {
                        if (ceil.distance < y - 0.1f)
                            continue;
                    }

                    bool nearWall = Physics.CheckSphere(p, settings.wallProximityRadius, wallMask,
                        QueryTriggerInteraction.Ignore);

                    candidates.Add(new Candidate
                    {
                        position = p,
                        gridIndex = cell.gridIndex,
                        layerIdx = li,
                        alive = true,
                        nearWall = nearWall,
                    });
                }
            }

            if (candidates.Count == 0) return System.Array.Empty<Vector3>();

            // Step 2: Index by (gridIndex, layerIdx)
            var candIndex = new Dictionary<(Vector2Int, int), int>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                candIndex[(c.gridIndex, c.layerIdx)] = i;
            }

            // Step 3: Checkerboard thinning for fully-enclosed interior candidates
            // Keep: edges, corners, near-wall, bottom/top layer always
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!c.alive) continue;
                if (c.nearWall) continue;

                int nbCount = 0;
                foreach (var d in Neighbors4)
                {
                    if (candIndex.TryGetValue((c.gridIndex + d, c.layerIdx), out int ni) && candidates[ni].alive)
                        nbCount++;
                }
                if (nbCount < 4) continue;

                // Checkerboard parity: removes ~50% of fully-interior same-layer points.
                int parity = (c.gridIndex.x + c.gridIndex.y + c.layerIdx) & 1;
                if (parity == 1)
                {
                    c.alive = false;
                    candidates[i] = c;
                }
            }

            // Step 4: Collect positions
            var result = new List<Vector3>(candidates.Count);
            foreach (var c in candidates)
                if (c.alive) result.Add(c.position);

            return result.ToArray();
        }
    }
}
