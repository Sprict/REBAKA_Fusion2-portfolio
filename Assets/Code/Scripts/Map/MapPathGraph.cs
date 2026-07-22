// Assets/Code/Scripts/Map/MapPathGraph.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>パスグラフのノード（配置後のワールド座標に展開済み）。</summary>
    public readonly struct MapPathGraphNode
    {
        /// <summary>ワールドのセル座標。</summary>
        public readonly Vector3Int WorldCell;

        /// <summary>このノードが属する配置モジュールのスロット（layout.Modules の index）。</summary>
        public readonly int ModuleSlot;

        /// <summary>モジュールローカルでのパスノード index（spec.PathNodes の index）。</summary>
        public readonly int LocalNodeIndex;

        public MapPathGraphNode(Vector3Int worldCell, int moduleSlot, int localNodeIndex)
        {
            WorldCell = worldCell;
            ModuleSlot = moduleSlot;
            LocalNodeIndex = localNodeIndex;
        }
    }

    /// <summary>パスグラフの無向辺。通行プロファイル付き。</summary>
    public readonly struct MapPathLink
    {
        public readonly int A;
        public readonly int B;
        public readonly TraversalProfile Profile;

        public MapPathLink(int a, int b, TraversalProfile profile)
        {
            A = a;
            B = b;
            Profile = profile;
        }
    }

    /// <summary>
    /// N1: モジュール埋め込みパスグラフ（devlog 2026-06-27 §6 / §10）。
    ///
    /// なぜこれで NavMesh を置き換えられるか:
    /// モジュール連結生成では、ジェネレータが配置のためモジュール隣接を既に解決している。
    /// 各モジュールに手置きしたノード（戸口セルに置く）を、噛み合ったソケット越しに跨ぎ辺で結べば、
    /// 「レイアウトが連結 = パスグラフが連結」が決定論的・ベイク不要・軽量に成立する。
    /// 敵 AI はホスト権威で動くため（§6）、クライアント側のナビ一致は不要 → 本グラフはホストだけが使う。
    ///
    /// 本クラスはレイアウト幾何から純粋に再構築する（生成器に記録責務を増やさない。MapConnectivity と同方針）。
    /// 局所ステアリング（N3）は実行時側の責務で、本グラフは大域構造（A* で辿る骨格）を担う
    /// （N3 単独は凹地形で局所最小に詰むため、必ず大域グラフと組む。§6）。
    /// </summary>
    public sealed class MapPathGraph
    {
        private readonly List<MapPathGraphNode> _nodes = new List<MapPathGraphNode>();
        private readonly List<MapPathLink> _edges = new List<MapPathLink>();
        private readonly List<List<int>> _adjacency = new List<List<int>>();
        private readonly Dictionary<(int, int), int> _edgeIndexByPair = new Dictionary<(int, int), int>();

        public IReadOnlyList<MapPathGraphNode> Nodes => _nodes;
        public IReadOnlyList<MapPathLink> Edges => _edges;
        public int NodeCount => _nodes.Count;
        public IReadOnlyList<int> Neighbors(int node) => _adjacency[node];

        /// <summary>
        /// レイアウトから埋め込みパスグラフを構築する。
        /// 1) 各モジュールのローカルノードをワールドへ展開、2) モジュール内エッジを張る、
        /// 3) 噛み合うソケット対を見つけ、両戸口セルのノードを跨ぎ辺で結ぶ。
        /// </summary>
        public static MapPathGraph Build(MapLayout layout)
        {
            var graph = new MapPathGraph();
            if (layout == null || layout.Count == 0)
                return graph;

            ModuleCatalog catalog = layout.Catalog;

            // (slot, localIndex) -> nodeId、(slot, worldCell) -> nodeId（ソケット戸口のノード引き当て用）。
            var nodeByModuleLocal = new Dictionary<(int, int), int>();
            var nodeByModuleCell = new Dictionary<(int, Vector3Int), int>();

            // 1. ノード展開。
            for (int slot = 0; slot < layout.Count; slot++)
            {
                PlacedModule pm = layout.Modules[slot];
                ModuleSpec spec = catalog[pm.ModuleIndex];
                IReadOnlyList<Vector3Int> pathNodes = spec.PathNodes;
                for (int li = 0; li < pathNodes.Count; li++)
                {
                    Vector3Int world = pm.OriginCell + GridRotation.RotateCell(pathNodes[li], pm.RotationSteps);
                    int nodeId = graph._nodes.Count;
                    graph._nodes.Add(new MapPathGraphNode(world, slot, li));
                    graph._adjacency.Add(new List<int>());
                    nodeByModuleLocal[(slot, li)] = nodeId;
                    // 同一モジュール内で同セルに複数ノードは想定しない（後勝ちで上書き）。
                    nodeByModuleCell[(slot, world)] = nodeId;
                }
            }

            // 2. モジュール内エッジ。
            for (int slot = 0; slot < layout.Count; slot++)
            {
                ModuleSpec spec = catalog[layout.Modules[slot].ModuleIndex];
                foreach (ModulePathEdge e in spec.InternalEdges)
                {
                    if (nodeByModuleLocal.TryGetValue((slot, e.NodeA), out int a) &&
                        nodeByModuleLocal.TryGetValue((slot, e.NodeB), out int b))
                    {
                        graph.AddEdge(a, b, e.Profile);
                    }
                }
            }

            // 3. モジュール間エッジ（噛み合うソケット対）。全ワールドソケットを Cell でインデックス。
            var socketsByCell = new Dictionary<Vector3Int, List<(int slot, WorldSocket ws)>>();
            for (int slot = 0; slot < layout.Count; slot++)
            {
                foreach (WorldSocket ws in layout.Modules[slot].WorldSockets(catalog))
                {
                    if (!socketsByCell.TryGetValue(ws.Cell, out List<(int, WorldSocket)> bucket))
                    {
                        bucket = new List<(int, WorldSocket)>();
                        socketsByCell[ws.Cell] = bucket;
                    }
                    bucket.Add((slot, ws));
                }
            }

            for (int slot = 0; slot < layout.Count; slot++)
            {
                foreach (WorldSocket s in layout.Modules[slot].WorldSockets(catalog))
                {
                    if (!socketsByCell.TryGetValue(s.NeighborCell, out List<(int slot, WorldSocket ws)> candidates))
                        continue;

                    foreach ((int otherSlot, WorldSocket t) in candidates)
                    {
                        // 各対は小さいスロット側で 1 回だけ処理（重複辺の防止）。
                        if (otherSlot <= slot)
                            continue;
                        if (t.Channel != s.Channel)
                            continue;
                        if (t.Facing != GridRotation.Opposite(s.Facing))
                            continue;

                        // 戸口セルにノードがある両モジュールだけ跨ぎ辺を張る。
                        if (nodeByModuleCell.TryGetValue((slot, s.Cell), out int portS) &&
                            nodeByModuleCell.TryGetValue((otherSlot, t.Cell), out int portT))
                        {
                            graph.AddEdge(portS, portT, new TraversalProfile(Mathf.Min(s.Clearance, t.Clearance)));
                        }
                    }
                }
            }

            return graph;
        }

        /// <summary>無向辺を追加。重複対は通行性の高い方（clearance 大）を残す。</summary>
        private void AddEdge(int a, int b, TraversalProfile profile)
        {
            if (a == b)
                return;

            (int, int) key = a < b ? (a, b) : (b, a);
            if (_edgeIndexByPair.TryGetValue(key, out int idx))
            {
                if (profile.Clearance > _edges[idx].Profile.Clearance)
                    _edges[idx] = new MapPathLink(key.Item1, key.Item2, profile);
                return;
            }

            _edgeIndexByPair[key] = _edges.Count;
            _edges.Add(new MapPathLink(key.Item1, key.Item2, profile));
            _adjacency[a].Add(b);
            _adjacency[b].Add(a);
        }

        /// <summary>2 ノード間の辺の clearance。辺が無ければ 0。</summary>
        public int EdgeClearance(int a, int b)
        {
            (int, int) key = a < b ? (a, b) : (b, a);
            return _edgeIndexByPair.TryGetValue(key, out int idx) ? _edges[idx].Profile.Clearance : 0;
        }

        /// <summary>グラフ全体が 1 つの連結成分か。空グラフは true。</summary>
        public bool IsConnected()
        {
            if (_nodes.Count == 0)
                return true;

            var visited = new bool[_nodes.Count];
            var stack = new Stack<int>();
            stack.Push(0);
            visited[0] = true;
            int count = 1;

            while (stack.Count > 0)
            {
                int c = stack.Pop();
                foreach (int nb in _adjacency[c])
                {
                    if (!visited[nb])
                    {
                        visited[nb] = true;
                        count++;
                        stack.Push(nb);
                    }
                }
            }
            return count == _nodes.Count;
        }


        /// <summary>
        /// slot 0（Start）に属する全パスノードを起点に BFS し、各モジュールの深さを返す。
        /// 深さは辺数ベースのグラフ距離で、モジュールが複数ノードを持つ場合は最小距離を採用する。
        /// ノードを持たない、または Start から到達できないモジュールは -1。
        /// </summary>
        public static int[] ComputeModuleDepths(MapLayout layout)
        {
            if (layout == null || layout.Count == 0)
                return System.Array.Empty<int>();

            MapPathGraph graph = Build(layout);
            int[] nodeDepths = graph.ComputeNodeDepthsFromModule(0);
            var moduleDepths = new int[layout.Count];
            for (int i = 0; i < moduleDepths.Length; i++)
                moduleDepths[i] = -1;

            for (int nodeId = 0; nodeId < graph._nodes.Count; nodeId++)
            {
                int depth = nodeDepths[nodeId];
                if (depth < 0)
                    continue;

                int slot = graph._nodes[nodeId].ModuleSlot;
                if (slot < 0 || slot >= moduleDepths.Length)
                    continue;
                if (moduleDepths[slot] < 0 || depth < moduleDepths[slot])
                    moduleDepths[slot] = depth;
            }

            return moduleDepths;
        }

        /// <summary>指定モジュールに属する全ノードを起点に、各ノードへの BFS 距離を返す。未到達は -1。</summary>
        public int[] ComputeNodeDepthsFromModule(int startSlot)
        {
            var depths = new int[_nodes.Count];
            for (int i = 0; i < depths.Length; i++)
                depths[i] = -1;

            var queue = new Queue<int>();
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].ModuleSlot != startSlot)
                    continue;
                depths[i] = 0;
                queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in _adjacency[current])
                {
                    if (depths[next] >= 0)
                        continue;
                    depths[next] = depths[current] + 1;
                    queue.Enqueue(next);
                }
            }

            return depths;
        }
        /// <summary>指定スロットに属するノード id を列挙する。</summary>
        public IEnumerable<int> NodesInModule(int slot)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].ModuleSlot == slot)
                    yield return i;
            }
        }

        /// <summary>指定スロットの最初のノード id。無ければ -1。</summary>
        public int FirstNodeInModule(int slot)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].ModuleSlot == slot)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// A* で startNode→goalNode の経路を探す。minClearance 未満の辺は通れない（敵サイズフィルタ）。
        /// コスト/ヒューリスティックはワールドセルのマンハッタン距離（admissible）。
        /// tie-break は node id 昇順で固定し、結果を決定論にする。
        /// </summary>
        public bool TryFindPath(int startNode, int goalNode, int minClearance, out List<int> path)
        {
            path = null;
            int n = _nodes.Count;
            if (startNode < 0 || goalNode < 0 || startNode >= n || goalNode >= n)
                return false;
            if (startNode == goalNode)
            {
                path = new List<int> { startNode };
                return true;
            }

            var gScore = new int[n];
            var fScore = new int[n];
            var cameFrom = new int[n];
            var closed = new bool[n];
            var inOpen = new bool[n];
            for (int i = 0; i < n; i++)
            {
                gScore[i] = int.MaxValue;
                fScore[i] = int.MaxValue;
                cameFrom[i] = -1;
            }

            gScore[startNode] = 0;
            fScore[startNode] = Heuristic(startNode, goalNode);
            var open = new List<int> { startNode };
            inOpen[startNode] = true;

            while (open.Count > 0)
            {
                // 小規模グラフ前提: f 最小を線形抽出（tie は node id 小で安定）。
                int bestIdx = 0;
                for (int i = 1; i < open.Count; i++)
                {
                    int c = open[i];
                    int best = open[bestIdx];
                    if (fScore[c] < fScore[best] || (fScore[c] == fScore[best] && c < best))
                        bestIdx = i;
                }

                int current = open[bestIdx];
                if (current == goalNode)
                {
                    path = Reconstruct(cameFrom, current);
                    return true;
                }

                open.RemoveAt(bestIdx);
                inOpen[current] = false;
                closed[current] = true;

                foreach (int nb in _adjacency[current])
                {
                    if (closed[nb])
                        continue;
                    if (EdgeClearance(current, nb) < minClearance)
                        continue;

                    int tentative = gScore[current] + StepCost(current, nb);
                    if (tentative < gScore[nb])
                    {
                        cameFrom[nb] = current;
                        gScore[nb] = tentative;
                        fScore[nb] = tentative + Heuristic(nb, goalNode);
                        if (!inOpen[nb])
                        {
                            open.Add(nb);
                            inOpen[nb] = true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// fromSlot 内のいずれかのノードから toSlot 内のいずれかのノードへ到達できるか。
        /// Start→Goal 到達保証（敵の徘徊・追跡が成立するか）の確認に使う。
        /// </summary>
        public bool TryFindPathBetweenModules(int fromSlot, int toSlot, int minClearance, out List<int> path)
        {
            path = null;
            foreach (int s in NodesInModule(fromSlot))
            {
                foreach (int g in NodesInModule(toSlot))
                {
                    if (TryFindPath(s, g, minClearance, out path))
                        return true;
                }
            }
            return false;
        }

        private List<int> Reconstruct(int[] cameFrom, int current)
        {
            var p = new List<int> { current };
            while (cameFrom[current] != -1)
            {
                current = cameFrom[current];
                p.Add(current);
            }
            p.Reverse();
            return p;
        }

        private int StepCost(int a, int b) => Manhattan(_nodes[a].WorldCell, _nodes[b].WorldCell);
        private int Heuristic(int a, int b) => Manhattan(_nodes[a].WorldCell, _nodes[b].WorldCell);

        private static int Manhattan(Vector3Int a, Vector3Int b)
        {
            return Mathf.Abs(a.x - b.x) + Mathf.Abs(a.y - b.y) + Mathf.Abs(a.z - b.z);
        }
    }
}
