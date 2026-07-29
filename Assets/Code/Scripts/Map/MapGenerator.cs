// Assets/Code/Scripts/Map/MapGenerator.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>生成パラメータ。一本道＋短い分岐（devlog 2026-06-27 §11 ステップ2）。</summary>
    public struct MapGeneratorConfig
    {
        /// <summary>Start と Goal の間に挟む Body モジュール数。</summary>
        public int MainPathLength;

        /// <summary>主経路から生やす分岐の本数。</summary>
        public int BranchCount;

        /// <summary>1 分岐あたりの Body モジュール数（末端は DeadEnd で塞ぐ）。</summary>
        public int BranchLength;

        /// <summary>1 フロンティアに対し候補モジュールを試す最大回数。</summary>
        public int MaxPlacementTries;

        /// <summary>連結／配置に失敗したとき seed をずらして再試行する最大回数。</summary>
        public int MaxRerolls;

        /// <summary>
        /// ツリー構築後に閉じるループ（閉路）の最大数。0 でツリーのまま（従来挙動）。
        /// カタログに <see cref="ModuleRole.Connector"/> が無ければ無効。
        /// </summary>
        public int LoopConnections;

        /// <summary>生成後に最深の開口へ置く裏口数。実行時に 0..2 へクランプする。</summary>
        public int BackDoorCount;

        public static MapGeneratorConfig Default => new MapGeneratorConfig
        {
            MainPathLength = 5,
            BranchCount = 2,
            BranchLength = 2,
            MaxPlacementTries = 12,
            MaxRerolls = 8,
            LoopConnections = 4,
            BackDoorCount = 2,
        };
    }

    /// <summary>生成結果。失敗時の診断（reroll 回数・fallback 使用）を含む。</summary>
    public struct MapGenerationResult
    {
        public MapLayout Layout;
        public bool Succeeded;     // 目標構成（主経路＋Goal）を満たしたか
        public bool UsedFallback;  // 保証済み最小テンプレへ落ちたか（§9 F4）
        public int RerollsUsed;
    }

    /// <summary>
    /// B1: 手作りモジュールをシード連結する決定論ジェネレータ。
    ///
    /// 配置は整数セル原点＋90°回転ステップのみで表現し（浮動小数を配置判定に使わない、§9）、
    /// ソケットを「向かい合わせ＋隣接セル一致」で噛み合わせる（MapPrimitives 参照）。
    /// 衝突は占有セル集合で弾き、F1 連結を満たすまで F4 リロール。最後は保証済みテンプレへ fallback。
    /// </summary>
    public sealed class MapGenerator
    {
        private const ulong RerollStride = 0x9E3779B97F4A7C15UL;
        private const int DenseFrontierBiasPercent = 100;

        private readonly struct OpenExit
        {
            public readonly Vector3Int Cell;
            public readonly Vector3Int SourceCell;
            public readonly MapDirection Facing;
            public readonly int Channel;
            public readonly int Clearance;
            public readonly int ModuleSlot;
            public readonly int NodeId;

            public OpenExit(Vector3Int cell, Vector3Int sourceCell, MapDirection facing, int channel, int clearance, int moduleSlot, int nodeId)
            {
                Cell = cell;
                SourceCell = sourceCell;
                Facing = facing;
                Channel = channel;
                Clearance = clearance;
                ModuleSlot = moduleSlot;
                NodeId = nodeId;
            }
        }


        private readonly struct BackDoorCandidate
        {
            public readonly OpenExit Exit;
            public readonly int Depth;

            public BackDoorCandidate(OpenExit exit, int depth)
            {
                Exit = exit;
                Depth = depth;
            }
        }
        private readonly struct LoopCandidate
        {
            public readonly OpenExit A;
            public readonly OpenExit B;
            public readonly List<Vector3Int> Path;
            public readonly List<PlacedModule> Connectors;
            public readonly int GraphDistance;

            public LoopCandidate(OpenExit a, OpenExit b, List<Vector3Int> path, List<PlacedModule> connectors, int graphDistance)
            {
                A = a;
                B = b;
                Path = path;
                Connectors = connectors;
                GraphDistance = graphDistance;
            }
        }

        private readonly struct FrontierChoice
        {
            public readonly WorldSocket Socket;
            public readonly int OriginalIndex;
            public readonly int Density;

            public FrontierChoice(WorldSocket socket, int originalIndex, int density)
            {
                Socket = socket;
                OriginalIndex = originalIndex;
                Density = density;
            }
        }

        private readonly ModuleCatalog _catalog;

        public MapGenerator(ModuleCatalog catalog)
        {
            _catalog = catalog;
        }

        public MapGenerationResult Generate(ulong seed, MapGeneratorConfig config)
        {
            for (int reroll = 0; reroll <= config.MaxRerolls; reroll++)
            {
                ulong rerollSeed = unchecked(seed + (ulong)reroll * RerollStride);
                var rng = new DeterministicRng(rerollSeed);

                if (TryBuildOnce(seed, rng, config, out MapLayout layout) &&
                    MapConnectivity.IsFullyConnected(layout))
                {
                    return new MapGenerationResult
                    {
                        Layout = layout,
                        Succeeded = true,
                        UsedFallback = false,
                        RerollsUsed = reroll,
                    };
                }
            }

            // F4: 全 reroll が失敗 → 保証済み最小テンプレへ落とす（無限ループ・開始遅延の防止、§9）。
            MapLayout fallback = BuildFallback(seed);
            return new MapGenerationResult
            {
                Layout = fallback,
                Succeeded = false,
                UsedFallback = true,
                RerollsUsed = config.MaxRerolls,
            };
        }

        // --- 1 回分の生成試行 ---------------------------------------------------

        private bool TryBuildOnce(ulong seed, DeterministicRng rng, MapGeneratorConfig config, out MapLayout layout)
        {
            layout = null;

            int startIndex = _catalog.FirstIndexWithRole(ModuleRole.Start);
            int goalIndex = _catalog.FirstIndexWithRole(ModuleRole.Goal);
            if (startIndex < 0 || goalIndex < 0)
                return false;

            var working = new MapLayout(seed, _catalog);
            var occupied = new HashSet<Vector3Int>();

            // Start を原点・回転 0 で置く。
            var startModule = new PlacedModule(startIndex, Vector3Int.zero, 0);
            if (!TryOccupy(startModule, occupied))
                return false;
            working.Add(startModule);

            // 開いたソケット（未接続）。主経路フロンティアと分岐の種に使う。
            var open = CollectWorldSockets(startModule);
            if (open.Count == 0)
                return false;

            // 主経路フロンティアを 1 つ選ぶ。
            WorldSocket frontier = TakeAt(open, rng.NextInt(open.Count));

            // 主経路: Body を MainPathLength 個つなぐ。
            for (int i = 0; i < config.MainPathLength; i++)
            {
                if (!TryGrow(working, occupied, frontier, ModuleRole.Body, rng, config.MaxPlacementTries,
                        out frontier, out List<WorldSocket> spawned))
                    return false;

                // 主経路で使わなかった口は分岐の種としてプールへ。
                open.AddRange(spawned);
            }

            // Goal を主経路末端に付ける。
            if (!TryGrowSpecific(working, occupied, frontier, goalIndex, rng, config.MaxPlacementTries,
                    out _, out _))
                return false;

            // 短い分岐（任意・失敗しても致命ではないので best-effort）。
            var deferredDeadEnds = new List<WorldSocket>();
            for (int b = 0; b < config.BranchCount && open.Count > 0; b++)
            {
                WorldSocket branchFrontier = TakeBiasedFrontier(open, occupied, rng);
                GrowBranch(working, occupied, branchFrontier, config.BranchLength, rng, config.MaxPlacementTries,
                    open, deferredDeadEnds);
            }

            int backDoorCount = ClampBackDoorCount(config.BackDoorCount);

            // ループ閉じ。裏口の置き場を最低 1 つだけ温存する（予約 1）。最深の開口は裏口とループの
            // 両方が欲しがるため、予約を増やすほど網目が痩せる。最低 1 で「裏口≥1」を保証しつつループを最大化する。
            int reservedOpenExits = (_catalog.FirstIndexWithRole(ModuleRole.Exit) >= 0 && backDoorCount > 0) ? 1 : 0;
            if (config.LoopConnections > 0)
                CloseLoops(working, occupied, config.LoopConnections, reservedOpenExits);

            // 裏口: ループ後に残った開口のうち最深へ Exit を最大 backDoorCount 個置く（密 seed では 1 個になり得る）。
            PlaceBackDoors(working, occupied, rng, config.MaxPlacementTries, backDoorCount);

            // 残った開口を DeadEnd で塞ぐ。
            SealRemainingOpenSockets(working, occupied, rng, config.MaxPlacementTries);

            layout = working;
            return true;
        }

        /// <summary>
        /// 既に連結済みのツリーに「閉路」を足して網目状にする（廊下掘り方式）。
        ///
        /// 原理: 離れた 2 つの「開いた口（外側セルが空のソケット）」の間を、空きセルを辿って
        /// <see cref="ModuleRole.Connector"/> の 1 セル通路で繋ぐ。両端はツリーで既に連結済みなので、
        /// 廊下で繋ぐと別経路ができる＝閉路になる。各セルは実際に使う方位だけを持つ
        /// straight/corner/tee/junction へ割り当て、見た目とパスグラフを一致させる。
        ///
        /// 疎なツリーでは「都合よく口が向かい合う空きセル」は生じないため、単に十字路を 1 個置く方式では
        /// ループが作れない（実測で確認）。空きセル上の BFS で経路を掘るのが確実。
        ///
        /// 決定論: 開いた口と候補ペアは安定順で総当りし、BFS は方位固定順で最短路を返す。
        /// 候補は既存パスグラフ上の距離が長いほど優先し、同点は座標順で固定する。
        /// </summary>
        private void CloseLoops(MapLayout layout, HashSet<Vector3Int> occupied, int maxLoops, int reservedOpenExits)
        {
            const int maxCorridor = 8; // 廊下の最大長（これ以上離れた口同士は繋がない）。

            for (int closed = 0; closed < maxLoops; closed++)
            {
                MapPathGraph graph = MapPathGraph.Build(layout);
                List<OpenExit> exits = CollectOpenExits(layout, occupied, graph);
                if (exits.Count < 2 || exits.Count < reservedOpenExits + 2)
                    return;

                if (!TryChooseLoopCandidate(exits, graph, occupied, maxCorridor, out LoopCandidate candidate))
                    return; // maxCorridor 以内で繋げる有効な口の組が無い。

                foreach (PlacedModule connector in candidate.Connectors)
                {
                    Occupy(connector, occupied);
                    layout.Add(connector);
                }
            }
        }

        /// <summary>開いた口（外側セルが空のソケット）を、座標と所属ノードで安定ソートして返す。</summary>
        private List<OpenExit> CollectOpenExits(MapLayout layout, HashSet<Vector3Int> occupied, MapPathGraph graph)
        {
            var list = new List<OpenExit>();
            for (int slot = 0; slot < layout.Count; slot++)
            {
                PlacedModule pm = layout.Modules[slot];
                foreach (WorldSocket ws in pm.WorldSockets(_catalog))
                {
                    Vector3Int cell = ws.NeighborCell;
                    if (occupied.Contains(cell))
                        continue;

                    int nodeId = FindNodeForSocket(graph, slot, ws.Cell);
                    if (nodeId < 0)
                        continue;

                    list.Add(new OpenExit(cell, ws.Cell, ws.Facing, ws.Channel, ws.Clearance, slot, nodeId));
                }
            }

            list.Sort(CompareOpenExit);
            return list;
        }

        private bool TryChooseLoopCandidate(List<OpenExit> exits, MapPathGraph graph, HashSet<Vector3Int> occupied,
            int maxCorridor, out LoopCandidate best)
        {
            best = default;
            bool hasBest = false;

            for (int i = 0; i < exits.Count; i++)
            {
                for (int j = i + 1; j < exits.Count; j++)
                {
                    OpenExit a = exits[i];
                    OpenExit b = exits[j];
                    if (a.Channel != b.Channel)
                        continue;
                    if (!TryGraphDistance(graph, a.NodeId, b.NodeId, out int graphDistance))
                        continue;

                    List<Vector3Int> path = FindEmptyPath(a.Cell, b.Cell, occupied, maxCorridor);
                    if (path == null)
                        continue;
                    if (!TryBuildConnectorPath(path, a, b, occupied, out List<PlacedModule> connectors))
                        continue;

                    var candidate = new LoopCandidate(a, b, path, connectors, graphDistance);
                    if (!hasBest || IsBetterLoopCandidate(candidate, best))
                    {
                        best = candidate;
                        hasBest = true;
                    }
                }
            }

            return hasBest;
        }

        private bool TryBuildConnectorPath(List<Vector3Int> path, OpenExit a, OpenExit b, HashSet<Vector3Int> occupied,
            out List<PlacedModule> connectors)
        {
            connectors = null;
            if (path == null || path.Count == 0)
                return false;

            var masks = new int[path.Count];
            for (int i = 0; i < path.Count - 1; i++)
            {
                if (!TryDirectionBetween(path[i], path[i + 1], out MapDirection dir))
                    return false;
                masks[i] |= DirectionMask(dir);
                masks[i + 1] |= DirectionMask(GridRotation.Opposite(dir));
            }

            if (path[0] != a.Cell || path[path.Count - 1] != b.Cell)
                return false;

            masks[0] |= DirectionMask(GridRotation.Opposite(a.Facing));
            masks[path.Count - 1] |= DirectionMask(GridRotation.Opposite(b.Facing));

            var built = new List<PlacedModule>(path.Count);
            for (int i = 0; i < path.Count; i++)
            {
                int mask = masks[i];
                if (CountBits(mask) < 2)
                    return false;
                if (!TryFindConnectorVariant(mask, a.Channel, out int moduleIndex, out int rotation))
                    return false;

                var placed = new PlacedModule(moduleIndex, path[i], rotation);
                if (!FitsWithoutCollision(placed, occupied))
                    return false;
                built.Add(placed);
            }

            connectors = built;
            return true;
        }

        private bool TryFindConnectorVariant(int requiredMask, int channel, out int moduleIndex, out int rotation)
        {
            moduleIndex = -1;
            rotation = 0;

            foreach (int idx in _catalog.IndicesWithRole(ModuleRole.Connector))
            {
                ModuleSpec spec = _catalog[idx];
                if (!IsSingleCellConnector(spec))
                    continue;

                for (int rot = 0; rot < 4; rot++)
                {
                    int mask = ConnectorSocketMask(spec, rot, channel);
                    if (mask == requiredMask)
                    {
                        moduleIndex = idx;
                        rotation = rot;
                        return true;
                    }
                }
            }

            int bestBits = int.MaxValue;
            foreach (int idx in _catalog.IndicesWithRole(ModuleRole.Connector))
            {
                ModuleSpec spec = _catalog[idx];
                if (!IsSingleCellConnector(spec))
                    continue;

                for (int rot = 0; rot < 4; rot++)
                {
                    int mask = ConnectorSocketMask(spec, rot, channel);
                    if (mask < 0 || (mask & requiredMask) != requiredMask)
                        continue;

                    int bits = CountBits(mask);
                    if (bits < bestBits || (bits == bestBits && (idx < moduleIndex || moduleIndex < 0)) ||
                        (bits == bestBits && idx == moduleIndex && rot < rotation))
                    {
                        bestBits = bits;
                        moduleIndex = idx;
                        rotation = rot;
                    }
                }
            }

            return moduleIndex >= 0;
        }

        private static bool IsBetterLoopCandidate(LoopCandidate candidate, LoopCandidate best)
        {
            if (candidate.GraphDistance != best.GraphDistance)
                return candidate.GraphDistance > best.GraphDistance;
            if (candidate.Path.Count != best.Path.Count)
                return candidate.Path.Count < best.Path.Count;

            int a = CompareOpenExit(candidate.A, best.A);
            if (a != 0)
                return a < 0;
            return CompareOpenExit(candidate.B, best.B) < 0;
        }

        private static int FindNodeForSocket(MapPathGraph graph, int moduleSlot, Vector3Int socketCell)
        {
            int fallback = -1;
            for (int i = 0; i < graph.NodeCount; i++)
            {
                MapPathGraphNode node = graph.Nodes[i];
                if (node.ModuleSlot != moduleSlot)
                    continue;
                if (fallback < 0)
                    fallback = i;
                if (node.WorldCell == socketCell)
                    return i;
            }
            return fallback;
        }

        private static bool TryGraphDistance(MapPathGraph graph, int start, int goal, out int distance)
        {
            distance = -1;
            if (start < 0 || goal < 0 || start >= graph.NodeCount || goal >= graph.NodeCount)
                return false;
            if (start == goal)
            {
                distance = 0;
                return true;
            }

            var dist = new int[graph.NodeCount];
            for (int i = 0; i < dist.Length; i++)
                dist[i] = -1;

            var queue = new Queue<int>();
            dist[start] = 0;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                foreach (int next in graph.Neighbors(current))
                {
                    if (dist[next] >= 0)
                        continue;
                    dist[next] = dist[current] + 1;
                    if (next == goal)
                    {
                        distance = dist[next];
                        return true;
                    }
                    queue.Enqueue(next);
                }
            }

            return false;
        }

        private static int CompareOpenExit(OpenExit a, OpenExit b)
        {
            int c = CompareCell(a.Cell, b.Cell);
            if (c != 0) return c;
            c = CompareCell(a.SourceCell, b.SourceCell);
            if (c != 0) return c;
            if (a.Facing != b.Facing) return (int)a.Facing < (int)b.Facing ? -1 : 1;
            if (a.Channel != b.Channel) return a.Channel.CompareTo(b.Channel);
            if (a.ModuleSlot != b.ModuleSlot) return a.ModuleSlot.CompareTo(b.ModuleSlot);
            return a.NodeId.CompareTo(b.NodeId);
        }

        private static bool IsSingleCellConnector(ModuleSpec spec)
        {
            return spec.FootprintCells.Count == 1 && spec.FootprintCells[0] == Vector3Int.zero;
        }

        private static int ConnectorSocketMask(ModuleSpec spec, int rotation, int channel)
        {
            int mask = 0;
            foreach (MapSocket socket in spec.Sockets)
            {
                if (socket.Channel != channel)
                    return -1;
                if (GridRotation.RotateCell(socket.LocalCell, rotation) != Vector3Int.zero)
                    return -1;
                mask |= DirectionMask(GridRotation.RotateDirection(socket.Facing, rotation));
            }
            return mask;
        }

        private static bool TryDirectionBetween(Vector3Int from, Vector3Int to, out MapDirection direction)
        {
            Vector3Int delta = to - from;
            if (delta == GridRotation.ToVector(MapDirection.North)) { direction = MapDirection.North; return true; }
            if (delta == GridRotation.ToVector(MapDirection.East)) { direction = MapDirection.East; return true; }
            if (delta == GridRotation.ToVector(MapDirection.South)) { direction = MapDirection.South; return true; }
            if (delta == GridRotation.ToVector(MapDirection.West)) { direction = MapDirection.West; return true; }
            direction = default;
            return false;
        }

        private static int DirectionMask(MapDirection direction)
        {
            return 1 << (int)direction;
        }

        private static int CountBits(int mask)
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if ((mask & (1 << i)) != 0)
                    count++;
            }
            return count;
        }

        /// <summary>
        /// start から goal まで、空きセルのみを辿る 4 近傍 BFS 最短路（XZ 平面・maxLen 以内）。
        /// 見つからなければ null。方位固定順で決定論。
        /// </summary>
        private static List<Vector3Int> FindEmptyPath(Vector3Int start, Vector3Int goal, HashSet<Vector3Int> occupied, int maxLen)
        {
            if (start == goal)
                return new List<Vector3Int> { start };

            Vector3Int[] dirs =
            {
                new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0),
                new Vector3Int(0, 0, -1), new Vector3Int(-1, 0, 0),
            };

            var came = new Dictionary<Vector3Int, Vector3Int>();
            var dist = new Dictionary<Vector3Int, int> { { start, 0 } };
            var queue = new Queue<Vector3Int>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                Vector3Int cur = queue.Dequeue();
                int cd = dist[cur];
                if (cd >= maxLen)
                    continue;

                foreach (Vector3Int d in dirs)
                {
                    Vector3Int nxt = cur + d;
                    if (nxt == start || came.ContainsKey(nxt))
                        continue;
                    if (occupied.Contains(nxt))
                        continue; // 経路セルは空きのみ（goal も開いた口=空き）。
                    came[nxt] = cur;
                    if (nxt == goal)
                        return Reconstruct(came, start, goal);
                    dist[nxt] = cd + 1;
                    queue.Enqueue(nxt);
                }
            }
            return null;
        }

        private static List<Vector3Int> Reconstruct(Dictionary<Vector3Int, Vector3Int> came, Vector3Int start, Vector3Int goal)
        {
            var path = new List<Vector3Int>();
            Vector3Int cur = goal;
            while (cur != start)
            {
                path.Add(cur);
                cur = came[cur];
            }
            path.Add(start);
            path.Reverse();
            return path;
        }

        /// <summary>セル座標の安定な全順序（x → z → y）。決定論的な tie-break に使う。</summary>
        private static bool LessCell(Vector3Int a, Vector3Int b)
        {
            return CompareCell(a, b) < 0;
        }

        private static int CompareCell(Vector3Int a, Vector3Int b)
        {
            if (a.x != b.x) return a.x < b.x ? -1 : 1;
            if (a.z != b.z) return a.z < b.z ? -1 : 1;
            if (a.y != b.y) return a.y < b.y ? -1 : 1;
            return 0;
        }


        /// <summary>
        /// ループ生成の前に、ツリー上で最深の開口へ Exit（裏口）を置く。
        /// 深さをツリー段階で測ることで「奥＝本道の遠さ」に忠実になり、かつ予約で
        /// ループを削らずに済む（ループは残りの開口で回し切れる）。
        /// </summary>
        private void PlaceBackDoors(MapLayout layout, HashSet<Vector3Int> occupied,
            DeterministicRng rng, int maxTries, int backDoorCount)
        {
            int exitIndex = _catalog.FirstIndexWithRole(ModuleRole.Exit);
            if (exitIndex < 0 || backDoorCount <= 0)
                return;

            MapPathGraph graph = MapPathGraph.Build(layout);
            int[] nodeDepths = graph.ComputeNodeDepthsFromModule(0);
            List<BackDoorCandidate> candidates = CollectBackDoorCandidates(layout, occupied, graph, nodeDepths);

            int placed = 0;
            foreach (BackDoorCandidate candidate in candidates)
            {
                if (placed >= backDoorCount)
                    break;
                if (occupied.Contains(candidate.Exit.Cell))
                    continue;

                if (TryGrowSpecific(layout, occupied, ToWorldSocket(candidate.Exit), exitIndex, rng, maxTries,
                        out _, out _))
                {
                    placed++;
                }
            }
        }

        /// <summary>残った開口（未使用の口）を DeadEnd で塞ぐ。ループ・裏口配置の後に呼ぶ。</summary>
        private void SealRemainingOpenSockets(MapLayout layout, HashSet<Vector3Int> occupied,
            DeterministicRng rng, int maxTries)
        {
            List<WorldSocket> remaining = CollectOpenSockets(layout, occupied);
            if (remaining.Count > 0)
                SealDeadEnds(layout, occupied, remaining, rng, maxTries);
        }

        private List<BackDoorCandidate> CollectBackDoorCandidates(MapLayout layout, HashSet<Vector3Int> occupied,
            MapPathGraph graph, int[] nodeDepths)
        {
            List<OpenExit> exits = CollectOpenExits(layout, occupied, graph);
            var candidates = new List<BackDoorCandidate>(exits.Count);
            foreach (OpenExit exit in exits)
            {
                if (exit.NodeId < 0 || exit.NodeId >= nodeDepths.Length)
                    continue;

                int depth = nodeDepths[exit.NodeId];
                if (depth < 0)
                    continue;

                candidates.Add(new BackDoorCandidate(exit, depth));
            }

            candidates.Sort(CompareBackDoorCandidate);
            return candidates;
        }

        private List<WorldSocket> CollectOpenSockets(MapLayout layout, HashSet<Vector3Int> occupied)
        {
            var list = new List<WorldSocket>();
            for (int slot = 0; slot < layout.Count; slot++)
            {
                PlacedModule pm = layout.Modules[slot];
                foreach (WorldSocket ws in pm.WorldSockets(_catalog))
                {
                    if (!occupied.Contains(ws.NeighborCell))
                        list.Add(ws);
                }
            }

            list.Sort(CompareWorldSocket);
            return list;
        }

        private static WorldSocket ToWorldSocket(OpenExit exit)
        {
            return new WorldSocket(exit.SourceCell, exit.Facing, exit.Channel, exit.Clearance);
        }

        private static int CompareBackDoorCandidate(BackDoorCandidate a, BackDoorCandidate b)
        {
            if (a.Depth != b.Depth)
                return a.Depth > b.Depth ? -1 : 1;

            int c = CompareCell(a.Exit.Cell, b.Exit.Cell);
            if (c != 0) return c;
            return CompareOpenExit(a.Exit, b.Exit);
        }

        private static int ClampBackDoorCount(int count)
        {
            if (count < 0) return 0;
            if (count > 2) return 2;
            return count;
        }

        /// <summary>分岐を best-effort で伸ばし、末端の封鎖はループ／裏口選択後まで延期する。</summary>
        private void GrowBranch(MapLayout layout, HashSet<Vector3Int> occupied, WorldSocket frontier,
            int length, DeterministicRng rng, int maxTries, List<WorldSocket> openPool, List<WorldSocket> deferredDeadEnds)
        {
            WorldSocket current = frontier;
            for (int i = 0; i < length; i++)
            {
                if (!TryGrow(layout, occupied, current, ModuleRole.Body, rng, maxTries,
                        out current, out List<WorldSocket> spawned))
                {
                    return;
                }
                openPool.AddRange(spawned);
            }

            if (deferredDeadEnds != null)
            {
                openPool.Add(current);
                deferredDeadEnds.Add(current);
                return;
            }

            SealDeadEnd(layout, occupied, current, rng, maxTries);
        }

        private void SealDeadEnds(MapLayout layout, HashSet<Vector3Int> occupied, List<WorldSocket> frontiers,
            DeterministicRng rng, int maxTries)
        {
            frontiers.Sort(CompareWorldSocket);
            foreach (WorldSocket frontier in frontiers)
            {
                if (occupied.Contains(frontier.NeighborCell))
                    continue;
                SealDeadEnd(layout, occupied, frontier, rng, maxTries);
            }
        }

        private void SealDeadEnd(MapLayout layout, HashSet<Vector3Int> occupied, WorldSocket frontier,
            DeterministicRng rng, int maxTries)
        {
            int deadEndIndex = _catalog.FirstIndexWithRole(ModuleRole.DeadEnd);
            if (deadEndIndex >= 0)
                TryGrowSpecific(layout, occupied, frontier, deadEndIndex, rng, maxTries, out _, out _);
        }

        // --- 配置プリミティブ ---------------------------------------------------

        /// <summary>
        /// frontier に指定ロールのモジュールを重み付き抽選で生やす。
        /// nextFrontier には新モジュールの「次に伸ばす口」を、spawned にはそれ以外の開いた口を返す。
        /// </summary>
        private bool TryGrow(MapLayout layout, HashSet<Vector3Int> occupied, WorldSocket frontier,
            ModuleRole role, DeterministicRng rng, int maxTries,
            out WorldSocket nextFrontier, out List<WorldSocket> spawned)
        {
            nextFrontier = default;
            spawned = null;

            for (int attempt = 0; attempt < maxTries; attempt++)
            {
                int candidate = PickWeighted(role, rng);
                if (candidate < 0)
                    return false;

                if (TryGrowSpecific(layout, occupied, frontier, candidate, rng, maxTries,
                        out nextFrontier, out spawned))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>特定モジュールを frontier に噛み合わせて配置する。</summary>
        private bool TryGrowSpecific(MapLayout layout, HashSet<Vector3Int> occupied, WorldSocket frontier,
            int moduleIndex, DeterministicRng rng, int maxTries,
            out WorldSocket nextFrontier, out List<WorldSocket> spawned)
        {
            nextFrontier = default;
            spawned = null;

            ModuleSpec spec = _catalog[moduleIndex];
            MapDirection desiredFacing = GridRotation.Opposite(frontier.Facing);
            Vector3Int desiredCell = frontier.NeighborCell;

            // entry ソケットを rng 順で試す。各 entry に対し回転は一意に決まる。
            int socketCount = spec.SocketCount;
            int offset = socketCount > 0 ? rng.NextInt(socketCount) : 0;
            for (int k = 0; k < socketCount; k++)
            {
                int entryIdx = (offset + k) % socketCount;
                MapSocket entry = spec.Sockets[entryIdx];

                if (entry.Channel != frontier.Channel)
                    continue;

                int rot = GridRotation.Normalize((int)desiredFacing - (int)entry.Facing);
                Vector3Int origin = desiredCell - GridRotation.RotateCell(entry.LocalCell, rot);

                var placed = new PlacedModule(moduleIndex, origin, rot);
                if (!FitsWithoutCollision(placed, occupied))
                    continue;

                // 配置確定。
                Occupy(placed, occupied);
                layout.Add(placed);

                // 新モジュールの開いた口（entry 以外）を集める。
                var worldSockets = CollectWorldSockets(placed);
                // entry に対応するワールドソケット（desiredCell・desiredFacing）を除外。
                spawned = new List<WorldSocket>(worldSockets.Count);
                bool entryRemoved = false;
                for (int i = 0; i < worldSockets.Count; i++)
                {
                    WorldSocket ws = worldSockets[i];
                    if (!entryRemoved && ws.Cell == desiredCell && ws.Facing == desiredFacing)
                    {
                        entryRemoved = true;
                        continue;
                    }
                    spawned.Add(ws);
                }

                // 次フロンティア = 開いた口のうち、外側セルが未占有のものを優先。
                nextFrontier = ChooseNextFrontier(spawned, occupied, rng);
                return true;
            }
            return false;
        }

        /// <summary>外向きセルが未占有の口を優先しつつ、既存占有セルに近い口を高確率で選ぶ。</summary>
        private static WorldSocket ChooseNextFrontier(List<WorldSocket> open, HashSet<Vector3Int> occupied, DeterministicRng rng)
        {
            int index = ChooseFrontierIndex(open, occupied, rng, growableOnly: true);
            if (index >= 0)
                return open[index];

            index = ChooseFrontierIndex(open, occupied, rng, growableOnly: false);
            return index >= 0 ? open[index] : default;
        }

        private static WorldSocket TakeBiasedFrontier(List<WorldSocket> open, HashSet<Vector3Int> occupied, DeterministicRng rng)
        {
            int index = ChooseFrontierIndex(open, occupied, rng, growableOnly: true);
            if (index < 0)
                index = ChooseFrontierIndex(open, occupied, rng, growableOnly: false);
            if (index < 0)
                return default;

            WorldSocket ws = open[index];
            open.RemoveAt(index);
            return ws;
        }

        private static int ChooseFrontierIndex(List<WorldSocket> open, HashSet<Vector3Int> occupied, DeterministicRng rng, bool growableOnly)
        {
            if (open.Count == 0)
                return -1;

            var candidates = new List<FrontierChoice>();
            for (int i = 0; i < open.Count; i++)
            {
                WorldSocket ws = open[i];
                if (growableOnly && occupied.Contains(ws.NeighborCell))
                    continue;
                candidates.Add(new FrontierChoice(ws, i, OccupiedProximityScore(ws.NeighborCell, occupied)));
            }

            if (candidates.Count == 0)
                return -1;

            candidates.Sort(CompareFrontierChoice);
            if (candidates.Count == 1 || rng.NextInt(100) < DenseFrontierBiasPercent)
                return candidates[0].OriginalIndex;

            return candidates[rng.NextInt(candidates.Count)].OriginalIndex;
        }

        private static int OccupiedProximityScore(Vector3Int cell, HashSet<Vector3Int> occupied)
        {
            int score = 0;
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dz = -2; dz <= 2; dz++)
                {
                    int dist = Mathf.Abs(dx) + Mathf.Abs(dz);
                    if (dist == 0 || dist > 2)
                        continue;

                    Vector3Int probe = cell + new Vector3Int(dx, 0, dz);
                    if (!occupied.Contains(probe))
                        continue;

                    score += dist == 1 ? 4 : 1;
                }
            }
            return score;
        }

        private static int CompareFrontierChoice(FrontierChoice a, FrontierChoice b)
        {
            if (a.Density != b.Density)
                return a.Density > b.Density ? -1 : 1;

            int c = CompareCell(a.Socket.NeighborCell, b.Socket.NeighborCell);
            if (c != 0) return c;
            c = CompareCell(a.Socket.Cell, b.Socket.Cell);
            if (c != 0) return c;
            if (a.Socket.Facing != b.Socket.Facing) return (int)a.Socket.Facing < (int)b.Socket.Facing ? -1 : 1;
            if (a.Socket.Channel != b.Socket.Channel) return a.Socket.Channel.CompareTo(b.Socket.Channel);
            return a.OriginalIndex.CompareTo(b.OriginalIndex);
        }

        private static int CompareWorldSocket(WorldSocket a, WorldSocket b)
        {
            int c = CompareCell(a.NeighborCell, b.NeighborCell);
            if (c != 0) return c;
            c = CompareCell(a.Cell, b.Cell);
            if (c != 0) return c;
            if (a.Facing != b.Facing) return (int)a.Facing < (int)b.Facing ? -1 : 1;
            return a.Channel.CompareTo(b.Channel);
        }

        private int PickWeighted(ModuleRole role, DeterministicRng rng)
        {
            int total = 0;
            foreach (int idx in _catalog.IndicesWithRole(role))
                total += _catalog[idx].Weight;
            if (total <= 0)
                return -1;

            int roll = rng.NextInt(total);
            foreach (int idx in _catalog.IndicesWithRole(role))
            {
                roll -= _catalog[idx].Weight;
                if (roll < 0)
                    return idx;
            }
            return -1;
        }

        // --- 占有セルユーティリティ --------------------------------------------

        private bool FitsWithoutCollision(PlacedModule module, HashSet<Vector3Int> occupied)
        {
            foreach (Vector3Int cell in module.WorldFootprint(_catalog))
            {
                if (occupied.Contains(cell))
                    return false;
            }
            return true;
        }

        private bool TryOccupy(PlacedModule module, HashSet<Vector3Int> occupied)
        {
            if (!FitsWithoutCollision(module, occupied))
                return false;
            Occupy(module, occupied);
            return true;
        }

        private void Occupy(PlacedModule module, HashSet<Vector3Int> occupied)
        {
            foreach (Vector3Int cell in module.WorldFootprint(_catalog))
                occupied.Add(cell);
        }

        private List<WorldSocket> CollectWorldSockets(PlacedModule module)
        {
            var list = new List<WorldSocket>();
            foreach (WorldSocket ws in module.WorldSockets(_catalog))
                list.Add(ws);
            return list;
        }

        private static WorldSocket TakeAt(List<WorldSocket> list, int index)
        {
            WorldSocket ws = list[index];
            list.RemoveAt(index);
            return ws;
        }

        // --- F4 fallback --------------------------------------------------------

        /// <summary>
        /// 保証済み最小テンプレ。Start に Goal を直結（不可なら Start 単独）。
        /// 必ず連結なレイアウトを返し、生成失敗でゲーム開始が止まらないことを保証する。
        /// </summary>
        private MapLayout BuildFallback(ulong seed)
        {
            var layout = new MapLayout(seed, _catalog);
            var occupied = new HashSet<Vector3Int>();

            int startIndex = _catalog.FirstIndexWithRole(ModuleRole.Start);
            if (startIndex < 0)
                return layout; // カタログが不正：空レイアウト（呼び出し側で検証ダッシュボードが検出）

            var start = new PlacedModule(startIndex, Vector3Int.zero, 0);
            TryOccupy(start, occupied);
            layout.Add(start);

            int goalIndex = _catalog.FirstIndexWithRole(ModuleRole.Goal);
            if (goalIndex < 0)
                return layout;

            var open = CollectWorldSockets(start);
            var rng = new DeterministicRng(seed);
            foreach (WorldSocket frontier in open)
            {
                if (TryGrowSpecific(layout, occupied, frontier, goalIndex, rng, 1, out _, out _))
                    break;
            }
            return layout;
        }
    }
}
