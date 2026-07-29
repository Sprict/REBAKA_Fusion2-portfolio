// Assets/Code/Scripts/Map/MapConnectivity.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// F1: 占有セルの隣接 FloodFill による連結検証（devlog 2026-06-27 §6「検証 = F1 連結」）。
    ///
    /// 「全モジュールが 1 つの連結成分に属するか」「Start から Goal へ到達できるか」を確認する。
    /// 連結は占有セルの 4 近傍（XZ 平面、同一 Y）で定義する。Stairs 等で Y をまたぐ接続は
    /// 段階 B 以降でソケットベースの隣接に拡張する余地を残す（現状は平面前提）。
    /// </summary>
    public static class MapConnectivity
    {
        private static readonly Vector3Int[] PlanarNeighbors =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1),
        };

        /// <summary>
        /// 占有セル全体が 1 つの連結成分かを判定する。空なら true（空マップは自明に連結扱い）。
        /// </summary>
        public static bool IsFullyConnected(MapLayout layout)
        {
            Dictionary<Vector3Int, int> occupancy = layout.BuildOccupancy();
            if (occupancy.Count == 0) return true;

            Vector3Int start = default;
            bool hasStart = false;
            foreach (Vector3Int cell in occupancy.Keys)
            {
                start = cell;
                hasStart = true;
                break;
            }
            if (!hasStart) return true;

            int reached = FloodFillCount(occupancy, start);
            return reached == occupancy.Count;
        }

        /// <summary>
        /// fromModule の任意セルから toModule の任意セルへ占有セル経由で到達できるか。
        /// Start→Goal 到達保証に使う。
        /// </summary>
        public static bool AreModulesConnected(MapLayout layout, int fromModuleIndex, int toModuleIndex)
        {
            if (fromModuleIndex == toModuleIndex) return true;
            if (fromModuleIndex < 0 || toModuleIndex < 0) return false;

            Dictionary<Vector3Int, int> occupancy = layout.BuildOccupancy();
            if (occupancy.Count == 0) return false;

            Vector3Int start = default;
            bool hasStart = false;
            foreach (Vector3Int cell in layout.Modules[fromModuleIndex].WorldFootprint(layout.Catalog))
            {
                if (occupancy.ContainsKey(cell))
                {
                    start = cell;
                    hasStart = true;
                    break;
                }
            }
            if (!hasStart) return false;

            var visited = new HashSet<Vector3Int>();
            var stack = new Stack<Vector3Int>();
            stack.Push(start);
            visited.Add(start);

            while (stack.Count > 0)
            {
                Vector3Int cell = stack.Pop();
                if (occupancy.TryGetValue(cell, out int moduleIndex) && moduleIndex == toModuleIndex)
                    return true;

                foreach (Vector3Int offset in PlanarNeighbors)
                {
                    Vector3Int next = cell + offset;
                    if (occupancy.ContainsKey(next) && visited.Add(next))
                        stack.Push(next);
                }
            }
            return false;
        }

        private static int FloodFillCount(Dictionary<Vector3Int, int> occupancy, Vector3Int start)
        {
            var visited = new HashSet<Vector3Int>();
            var stack = new Stack<Vector3Int>();
            stack.Push(start);
            visited.Add(start);

            while (stack.Count > 0)
            {
                Vector3Int cell = stack.Pop();
                foreach (Vector3Int offset in PlanarNeighbors)
                {
                    Vector3Int next = cell + offset;
                    if (occupancy.ContainsKey(next) && visited.Add(next))
                        stack.Push(next);
                }
            }
            return visited.Count;
        }
    }
}
