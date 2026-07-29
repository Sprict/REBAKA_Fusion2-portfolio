// Assets/Code/Scripts/Map/SandboxCatalog.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// プレハブ・ScriptableObject オーサリングを待たずにマップ生成を動かすための、コード定義カタログ。
    ///
    /// 部屋（3x3）と幅1廊下で「らしい」見た目にし、各モジュールは中心＋各戸口セルにパスノードを置き、
    /// 中心と各戸口を内部辺で結ぶ（部屋を貫く通り道を作り、N1 グラフ連結を保証）。
    ///
    /// 目視確認は 2 系統がこれを共有する（同一カタログ＝同一生成で見え方が一致）:
    ///   - <see cref="MapGenerationVisualizer"/>: Gizmo で抽象描画（段階 A/N1 の確認）。
    ///   - <see cref="MapBuilder"/>: 実 Instantiate（段階 B の確認。prefab 未割当ならプレースホルダ生成）。
    /// </summary>
    public static class SandboxCatalog
    {
        /// <summary>目視確認用のモジュール集合を構築する。並び順は安定（manifest index がこれに依存）。</summary>
        public static ModuleCatalog Build()
        {
            var modules = new List<ModuleSpec>
            {
                // 多方向スタート（北・東・西）: 「進行方向が一方向」を解消。
                Room3x3("start_room", ModuleRole.Start, new[] { MapDirection.North, MapDirection.East, MapDirection.West }, weight: 1),
                Room3x3("goal_room", ModuleRole.Goal, new[] { MapDirection.South }, weight: 1),
                // 直線の重みを下げ（3→2）、合流系（Tee）を上げて長い一本道を抑制。
                Straight("corridor", ModuleRole.Body, weight: 2),
                Corner("corner", ModuleRole.Body, weight: 2),
                Tee("tee", ModuleRole.Body, weight: 2),
                Room3x3("hall", ModuleRole.Body, new[] { MapDirection.North, MapDirection.South, MapDirection.East }, weight: 1),
                // ループ閉じ用の 1 セル通路（通常成長では選ばれず、CloseLoops が空きセルに置く）。
                Junction("junction", ModuleRole.Connector),
                OneCellConnector("loop_straight", ModuleRole.Connector, new[] { MapDirection.North, MapDirection.South }),
                OneCellConnector("loop_corner", ModuleRole.Connector, new[] { MapDirection.North, MapDirection.East }),
                OneCellConnector("loop_tee", ModuleRole.Connector, new[] { MapDirection.North, MapDirection.East, MapDirection.West }),
                DeadEnd("deadend", ModuleRole.DeadEnd),
                DeadEnd("exit", ModuleRole.Exit),
            };
            return new ModuleCatalog(modules);
        }

        // 3x3 部屋。指定方位の辺中央に戸口を開ける（戸口幅 2）。
        private static ModuleSpec Room3x3(string id, ModuleRole role, MapDirection[] doors, int weight)
        {
            var footprint = new List<Vector3Int>();
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                    footprint.Add(new Vector3Int(x, 0, z));

            var sockets = new List<MapSocket>();
            var nodes = new List<Vector3Int> { new Vector3Int(0, 0, 0) }; // 0 = 中心
            var edges = new List<ModulePathEdge>();
            foreach (MapDirection d in doors)
            {
                Vector3Int doorCell = EdgeCenterCell(d); // 部屋の辺中央セル
                sockets.Add(new MapSocket(doorCell, d, channel: 0, clearance: 2));
                nodes.Add(doorCell);
                edges.Add(new ModulePathEdge(0, nodes.Count - 1, new TraversalProfile(2)));
            }
            return new ModuleSpec(id, role, footprint, sockets, weight, nodes, edges);
        }

        // 幅1・長さ3 の直線廊下（Z 方向）。両端に戸口。
        private static ModuleSpec Straight(string id, ModuleRole role, int weight)
        {
            var footprint = new[]
            {
                new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1),
            };
            var sockets = new[]
            {
                new MapSocket(new Vector3Int(0, 0, -1), MapDirection.South, 0, 1),
                new MapSocket(new Vector3Int(0, 0, 1), MapDirection.North, 0, 1),
            };
            var nodes = new[]
            {
                new Vector3Int(0, 0, 0), new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 1),
            };
            var edges = new[]
            {
                new ModulePathEdge(0, 1), new ModulePathEdge(0, 2),
            };
            return new ModuleSpec(id, role, footprint, sockets, weight, nodes, edges);
        }

        // L 字コーナー（South と East に戸口）。
        private static ModuleSpec Corner(string id, ModuleRole role, int weight)
        {
            var footprint = new[]
            {
                new Vector3Int(0, 0, -1), new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0),
            };
            var sockets = new[]
            {
                new MapSocket(new Vector3Int(0, 0, -1), MapDirection.South, 0, 1),
                new MapSocket(new Vector3Int(1, 0, 0), MapDirection.East, 0, 1),
            };
            var nodes = new[]
            {
                new Vector3Int(0, 0, 0), new Vector3Int(0, 0, -1), new Vector3Int(1, 0, 0),
            };
            var edges = new[]
            {
                new ModulePathEdge(0, 1), new ModulePathEdge(0, 2),
            };
            return new ModuleSpec(id, role, footprint, sockets, weight, nodes, edges);
        }

        // T 字（West/East のバー ＋ South の枝）。
        private static ModuleSpec Tee(string id, ModuleRole role, int weight)
        {
            var footprint = new[]
            {
                new Vector3Int(-1, 0, 0), new Vector3Int(0, 0, 0), new Vector3Int(1, 0, 0),
                new Vector3Int(0, 0, -1),
            };
            var sockets = new[]
            {
                new MapSocket(new Vector3Int(-1, 0, 0), MapDirection.West, 0, 1),
                new MapSocket(new Vector3Int(1, 0, 0), MapDirection.East, 0, 1),
                new MapSocket(new Vector3Int(0, 0, -1), MapDirection.South, 0, 1),
            };
            var nodes = new[]
            {
                new Vector3Int(0, 0, 0),
                new Vector3Int(-1, 0, 0), new Vector3Int(1, 0, 0), new Vector3Int(0, 0, -1),
            };
            var edges = new[]
            {
                new ModulePathEdge(0, 1), new ModulePathEdge(0, 2), new ModulePathEdge(0, 3),
            };
            return new ModuleSpec(id, role, footprint, sockets, weight, nodes, edges);
        }

        // 1 セルの十字路（4 方位の戸口・幅2）。ループ閉じパスが空きセルに置いて閉路を作る。
        // 1 セルなのでパスノードは中心 1 つ。各戸口は同セルから出るので、隣接モジュールと
        // 噛み合えば中心ノード同士が跨ぎ辺で結ばれる（内部辺は不要）。
        private static ModuleSpec Junction(string id, ModuleRole role)
        {
            return OneCellConnector(id, role,
                new[] { MapDirection.North, MapDirection.East, MapDirection.South, MapDirection.West }, clearance: 2);
        }

        // 1 セルのループ廊下ピース。CloseLoops が実際の接続方位に合う straight/corner/tee を選ぶ。
        private static ModuleSpec OneCellConnector(string id, ModuleRole role, MapDirection[] doors, int clearance = 1)
        {
            var footprint = new[] { new Vector3Int(0, 0, 0) };
            var sockets = new MapSocket[doors.Length];
            for (int i = 0; i < doors.Length; i++)
                sockets[i] = new MapSocket(new Vector3Int(0, 0, 0), doors[i], 0, clearance);

            var nodes = new[] { new Vector3Int(0, 0, 0) };
            return new ModuleSpec(id, role, footprint, sockets, weight: 1, nodes, null);
        }

        // 1 セルの行き止まり（South 戸口）。
        private static ModuleSpec DeadEnd(string id, ModuleRole role)
        {
            var footprint = new[] { new Vector3Int(0, 0, 0) };
            var sockets = new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South, 0, 1) };
            var nodes = new[] { new Vector3Int(0, 0, 0) };
            return new ModuleSpec(id, role, footprint, sockets, weight: 1, nodes, null);
        }

        // 3x3 部屋の指定方位の辺中央セル。
        private static Vector3Int EdgeCenterCell(MapDirection d)
        {
            switch (d)
            {
                case MapDirection.North: return new Vector3Int(0, 0, 1);
                case MapDirection.South: return new Vector3Int(0, 0, -1);
                case MapDirection.East: return new Vector3Int(1, 0, 0);
                default: return new Vector3Int(-1, 0, 0); // West
            }
        }
    }
}
