using System.Collections.Generic;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor
{
    public struct IndoorCell
    {
        public int roomId;
        public Vector3 floorPoint;
        public Vector2Int gridIndex;
    }

    public struct DetectionResult
    {
        public IndoorCell[] cells;
        public int roomCount;
        public Bounds worldBounds;
        public float cellSize;
        public float worldMinY;
        public float worldMaxY;
        /// <summary>Preview UI / Debug 表示用: 床ヒット総数</summary>
        public int debugFloorHits;
        /// <summary>Preview UI / Debug 表示用: 天井判定で除外された数</summary>
        public int debugCeilingRejected;
        /// <summary>Preview UI / Debug 表示用: Renderer の数 (0 の場合は AABB 取得失敗)</summary>
        public int debugRendererCount;
    }

    public static class IndoorVolumeDetector
    {
        private static readonly Vector2Int[] Neighbors4 =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0),
            new Vector2Int(0, 1), new Vector2Int(0, -1),
        };

        public static DetectionResult Detect(ProbePlacementSettings settings)
        {
            if (settings == null || settings.root == null)
                return Empty();

            Physics.SyncTransforms();

            var renderers = settings.root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                var e = Empty();
                e.debugRendererCount = 0;
                return e;
            }

            Bounds worldBounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                worldBounds.Encapsulate(renderers[i].bounds);

            float cs = Mathf.Max(0.5f, settings.cellSize);
            Vector3 min = worldBounds.min;
            Vector3 max = worldBounds.max;
            int gridX = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / cs));
            int gridZ = Mathf.Max(1, Mathf.CeilToInt((max.z - min.z) / cs));

            var cellMap = new Dictionary<Vector2Int, IndoorCell>(gridX * gridZ);
            float castY = max.y + 2f;
            float castDist = (max.y - min.y) + 4f;
            int floorMaskValue = settings.floorMask.value;
            int ceilingMaskValue = settings.ceilingMask.value;
            int wallMaskValue = settings.wallMask.value;
            int floorHits = 0;
            int ceilingRejected = 0;

            for (int ix = 0; ix < gridX; ix++)
            {
                for (int iz = 0; iz < gridZ; iz++)
                {
                    float wx = min.x + (ix + 0.5f) * cs;
                    float wz = min.z + (iz + 0.5f) * cs;
                    Vector3 origin = new Vector3(wx, castY, wz);

                    // RaycastAll で floor レイヤーの全ヒットを取得。
                    // ヒット 0 個: 床なし。1 個以上: 最下段を「床」として扱い、
                    // それより上のヒットを「天井候補」とみなす。
                    // これにより 1 枚岩の床レイヤー上に複数階の床/天井を置いている
                    // シーン (2F の床が 1F の天井として機能する構成) に対応できる。
                    var downHits = Physics.RaycastAll(origin, Vector3.down, castDist,
                        floorMaskValue, QueryTriggerInteraction.Ignore);
                    if (downHits.Length == 0) continue;

                    int lowestIdx = 0;
                    for (int i = 1; i < downHits.Length; i++)
                        if (downHits[i].point.y < downHits[lowestIdx].point.y)
                            lowestIdx = i;
                    var floorHit = downHits[lowestIdx];

                    if (floorHit.point.y < min.y - 0.2f || floorHit.point.y > max.y + 0.2f)
                        continue;

                    floorHits++;

                    bool hasCeiling = false;
                    if (settings.requireCeiling)
                    {
                        // 1) 同じ下向きヒット群内で floor 上に別の floor-layer 面があるか
                        float floorY = floorHit.point.y;
                        for (int i = 0; i < downHits.Length; i++)
                        {
                            if (i == lowestIdx) continue;
                            float dy = downHits[i].point.y - floorY;
                            if (dy >= settings.minCeilingHeight && dy <= settings.maxCeilingHeight)
                            {
                                hasCeiling = true;
                                break;
                            }
                        }

                        // 2) floor 以外のレイヤー (ceiling/wall) にも天井役があるかチェック
                        if (!hasCeiling)
                        {
                            hasCeiling = HasCeilingAbove(floorHit.point, floorHit.collider,
                                settings.minCeilingHeight, settings.maxCeilingHeight,
                                ceilingMaskValue, wallMaskValue, floorMaskValue);
                        }

                        if (!hasCeiling)
                        {
                            ceilingRejected++;
                            continue;
                        }
                    }

                    var gi = new Vector2Int(ix, iz);
                    cellMap[gi] = new IndoorCell
                    {
                        roomId = -1,
                        floorPoint = floorHit.point,
                        gridIndex = gi,
                    };
                }
            }

            if (cellMap.Count == 0)
            {
                var empty = Empty();
                empty.debugRendererCount = renderers.Length;
                empty.debugFloorHits = floorHits;
                empty.debugCeilingRejected = ceilingRejected;
                empty.worldBounds = worldBounds;
                return empty;
            }

            var idxList = new List<Vector2Int>(cellMap.Keys);
            var idxToId = new Dictionary<Vector2Int, int>(idxList.Count);
            for (int i = 0; i < idxList.Count; i++) idxToId[idxList[i]] = i;

            var uf = new UnionFind(idxList.Count);
            int wallMask = settings.wallMask.value;

            for (int i = 0; i < idxList.Count; i++)
            {
                var cellA = cellMap[idxList[i]];
                foreach (var dir in Neighbors4)
                {
                    var ni = idxList[i] + dir;
                    if (!idxToId.TryGetValue(ni, out int nid)) continue;
                    if (nid <= i) continue;

                    var cellB = cellMap[ni];
                    if (Mathf.Abs(cellA.floorPoint.y - cellB.floorPoint.y) > settings.maxFloorHeightJump)
                        continue;

                    Vector3 a = cellA.floorPoint + Vector3.up * 0.3f;
                    Vector3 b = cellB.floorPoint + Vector3.up * 0.3f;
                    Vector3 ab = b - a;
                    float dist = ab.magnitude;
                    bool blocked = dist > 0.001f &&
                                   Physics.Raycast(a, ab / dist, dist, wallMask, QueryTriggerInteraction.Ignore);

                    if (!blocked) uf.Union(i, nid);
                }
            }

            var rootToRoom = new Dictionary<int, int>();
            int nextRoom = 0;
            var outCells = new IndoorCell[idxList.Count];
            for (int i = 0; i < idxList.Count; i++)
            {
                int r = uf.Find(i);
                if (!rootToRoom.TryGetValue(r, out int roomId))
                {
                    roomId = nextRoom++;
                    rootToRoom[r] = roomId;
                }
                var cell = cellMap[idxList[i]];
                cell.roomId = roomId;
                outCells[i] = cell;
            }

            return new DetectionResult
            {
                cells = outCells,
                roomCount = nextRoom,
                worldBounds = worldBounds,
                cellSize = cs,
                worldMinY = min.y,
                worldMaxY = max.y,
                debugRendererCount = renderers.Length,
                debugFloorHits = floorHits,
                debugCeilingRejected = ceilingRejected,
            };
        }

        /// <summary>
        /// floorPoint の真上に maxHeight 以内で ceiling/wall/floor のいずれかの collider が
        /// あり、その距離が minHeight 以上なら屋内とみなす。floor 自身との自己ヒットは除外する。
        /// 屋外 (頭上が開空) の floor は false。
        /// </summary>
        private static bool HasCeilingAbove(
            Vector3 floorPoint,
            Collider floorCollider,
            float minHeight,
            float maxHeight,
            int ceilingMask,
            int wallMask,
            int floorMask)
        {
            int combined = ceilingMask | wallMask | floorMask;
            if (combined == 0) return true; // マスク未指定は判定不能なので許容

            Vector3 origin = floorPoint + Vector3.up * 0.05f;
            var hits = Physics.RaycastAll(origin, Vector3.up, Mathf.Max(0.1f, maxHeight),
                combined, QueryTriggerInteraction.Ignore);
            if (hits.Length == 0) return false;

            float closest = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider == floorCollider) continue;
                if (hits[i].distance < closest) closest = hits[i].distance;
            }
            if (closest == float.MaxValue) return false;
            return closest >= minHeight;
        }

        private static DetectionResult Empty()
        {
            return new DetectionResult
            {
                cells = System.Array.Empty<IndoorCell>(),
                roomCount = 0,
                worldBounds = new Bounds(Vector3.zero, Vector3.zero),
                cellSize = 0f,
            };
        }

        private class UnionFind
        {
            private readonly int[] parent;
            private readonly int[] rank;

            public UnionFind(int n)
            {
                parent = new int[n];
                rank = new int[n];
                for (int i = 0; i < n; i++) parent[i] = i;
            }

            public int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a);
                int rb = Find(b);
                if (ra == rb) return;
                if (rank[ra] < rank[rb]) (ra, rb) = (rb, ra);
                parent[rb] = ra;
                if (rank[ra] == rank[rb]) rank[ra]++;
            }
        }
    }
}
