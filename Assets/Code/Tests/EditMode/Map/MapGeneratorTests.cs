// Assets/Code/Tests/EditMode/Map/MapGeneratorTests.cs
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MyFolder.Scripts.Map;

namespace MyFolder.Scripts.Tests.Map
{
    /// <summary>
    /// B1 生成コアの振る舞い検証（Unity Editor 非依存・純粋ロジック）。
    /// 検証対象: 決定論（同一 seed → 同一レイアウト）、F1 連結、占有衝突なし、manifest 往復。
    /// </summary>
    public class MapGeneratorTests
    {
        private static readonly Vector3Int[] PlanarNeighbors =
        {
            new Vector3Int(1, 0, 0),
            new Vector3Int(-1, 0, 0),
            new Vector3Int(0, 0, 1),
            new Vector3Int(0, 0, -1),
        };

        private struct LoopMetricSummary
        {
            public int SeedCount;
            public int ZeroLoopSeeds;
            public int TotalAddedCycles;
            public int TotalConnectorModules;
            public int ConnectorCorridors;

            public float ZeroLoopRate => SeedCount == 0 ? 0f : ZeroLoopSeeds / (float)SeedCount;
            public float AverageCorridorLength => ConnectorCorridors == 0 ? 0f : TotalConnectorModules / (float)ConnectorCorridors;
        }

        // 1 セルモジュールだけで構成した最小カタログ。ソケットは外向き facing で定義。
        private static ModuleCatalog BuildCatalog()
        {
            var one = new[] { new Vector3Int(0, 0, 0) };

            var start = new ModuleSpec("start", ModuleRole.Start, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North) });

            var goal = new ModuleSpec("goal", ModuleRole.Goal, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North) });

            var straight = new ModuleSpec("straight", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South),
                }, weight: 3);

            var corner = new ModuleSpec("corner", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.East),
                }, weight: 2);

            var tee = new ModuleSpec("tee", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.East),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.West),
                }, weight: 1);

            var deadEnd = new ModuleSpec("deadend", ModuleRole.DeadEnd, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South) });

            return new ModuleCatalog(new[] { start, goal, straight, corner, tee, deadEnd });
        }

        private static MapGeneratorConfig SmallConfig()
        {
            return new MapGeneratorConfig
            {
                MainPathLength = 5,
                BranchCount = 2,
                BranchLength = 2,
                MaxPlacementTries = 16,
                MaxRerolls = 16,
            };
        }

        private static int CountUniqueCells(MapLayout layout)
        {
            var occupancy = layout.BuildOccupancy();
            return occupancy.Count;
        }

        private static int CountFootprintCells(MapLayout layout)
        {
            int total = 0;
            foreach (PlacedModule m in layout.Modules)
            {
                foreach (var _ in m.WorldFootprint(layout.Catalog))
                    total++;
            }
            return total;
        }

        private static int CountGraphCycles(MapPathGraph graph)
        {
            if (graph.NodeCount == 0)
                return 0;
            Assert.That(graph.IsConnected(), Is.True, "cycleCount は連結グラフ前提で集計する");
            return graph.Edges.Count - graph.NodeCount + 1;
        }

        private static int CountModulesWithRole(MapLayout layout, ModuleRole role)
        {
            int count = 0;
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout.Catalog[layout.Modules[i].ModuleIndex].Role == role)
                    count++;
            }
            return count;
        }

        private static int CountConnectorCorridors(MapLayout layout)
        {
            var connectorCells = new HashSet<Vector3Int>();
            for (int i = 0; i < layout.Count; i++)
            {
                PlacedModule module = layout.Modules[i];
                if (layout.Catalog[module.ModuleIndex].Role != ModuleRole.Connector)
                    continue;

                foreach (Vector3Int cell in module.WorldFootprint(layout.Catalog))
                    connectorCells.Add(cell);
            }

            int components = 0;
            var sorted = new List<Vector3Int>(connectorCells);
            sorted.Sort((a, b) => LessCell(a, b) ? -1 : (a == b ? 0 : 1));
            var visited = new HashSet<Vector3Int>();

            foreach (Vector3Int start in sorted)
            {
                if (!visited.Add(start))
                    continue;

                components++;
                var stack = new Stack<Vector3Int>();
                stack.Push(start);
                while (stack.Count > 0)
                {
                    Vector3Int current = stack.Pop();
                    foreach (Vector3Int offset in PlanarNeighbors)
                    {
                        Vector3Int next = current + offset;
                        if (connectorCells.Contains(next) && visited.Add(next))
                            stack.Push(next);
                    }
                }
            }

            return components;
        }

        private static LoopMetricSummary CollectLoopMetrics(int firstSeed, int lastSeed)
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);

            var treeConfig = MapGeneratorConfig.Default;
            treeConfig.LoopConnections = 0;

            var loopConfig = MapGeneratorConfig.Default;
            loopConfig.LoopConnections = 4;

            var summary = new LoopMetricSummary();
            for (ulong seed = (ulong)firstSeed; seed <= (ulong)lastSeed; seed++)
            {
                MapLayout treeLayout = gen.Generate(seed, treeConfig).Layout;
                MapLayout loopLayout = gen.Generate(seed, loopConfig).Layout;

                Assert.That(MapConnectivity.IsFullyConnected(loopLayout), Is.True, $"seed={seed} layout connected");
                MapPathGraph treeGraph = MapPathGraph.Build(treeLayout);
                MapPathGraph loopGraph = MapPathGraph.Build(loopLayout);
                Assert.That(loopGraph.IsConnected(), Is.True, $"seed={seed} graph connected");

                int addedCycles = CountGraphCycles(loopGraph) - CountGraphCycles(treeGraph);
                summary.SeedCount++;
                summary.TotalAddedCycles += addedCycles;
                summary.TotalConnectorModules += CountModulesWithRole(loopLayout, ModuleRole.Connector);
                summary.ConnectorCorridors += CountConnectorCorridors(loopLayout);
                if (addedCycles <= 0)
                    summary.ZeroLoopSeeds++;
            }

            return summary;
        }

        private static bool LessCell(Vector3Int a, Vector3Int b)
        {
            if (a.x != b.x) return a.x < b.x;
            if (a.z != b.z) return a.z < b.z;
            return a.y < b.y;
        }

        [Test]
        public void Generate_ProducesStartAndGoal()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);
            MapGenerationResult result = gen.Generate(seed: 12345, SmallConfig());

            Assert.That(result.Layout.Count, Is.GreaterThanOrEqualTo(2), "最低 Start と Goal は置かれる");

            bool hasStart = false, hasGoal = false;
            foreach (PlacedModule m in result.Layout.Modules)
            {
                ModuleRole role = catalog[m.ModuleIndex].Role;
                if (role == ModuleRole.Start) hasStart = true;
                if (role == ModuleRole.Goal) hasGoal = true;
            }
            Assert.That(hasStart, Is.True, "Start が存在する");
            Assert.That(hasGoal, Is.True, "Goal が存在する");
        }

        [Test]
        public void Generate_IsDeterministicForSameSeed()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);

            MapGenerationResult a = gen.Generate(seed: 0xABCDEF, SmallConfig());
            MapGenerationResult b = gen.Generate(seed: 0xABCDEF, SmallConfig());

            Assert.That(b.Layout.Count, Is.EqualTo(a.Layout.Count));
            for (int i = 0; i < a.Layout.Count; i++)
            {
                PlacedModule pa = a.Layout.Modules[i];
                PlacedModule pb = b.Layout.Modules[i];
                Assert.That(pb.ModuleIndex, Is.EqualTo(pa.ModuleIndex), $"#{i} module");
                Assert.That(pb.OriginCell, Is.EqualTo(pa.OriginCell), $"#{i} origin");
                Assert.That(pb.RotationSteps, Is.EqualTo(pa.RotationSteps), $"#{i} rotation");
            }
        }

        [Test]
        public void Generate_DifferentSeedsCanDiffer()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);

            MapGenerationResult a = gen.Generate(seed: 1, SmallConfig());
            MapGenerationResult b = gen.Generate(seed: 99999, SmallConfig());

            bool identical = a.Layout.Count == b.Layout.Count;
            if (identical)
            {
                for (int i = 0; i < a.Layout.Count; i++)
                {
                    if (a.Layout.Modules[i].OriginCell != b.Layout.Modules[i].OriginCell ||
                        a.Layout.Modules[i].ModuleIndex != b.Layout.Modules[i].ModuleIndex)
                    {
                        identical = false;
                        break;
                    }
                }
            }
            Assert.That(identical, Is.False, "異なる seed は（ほぼ）異なるレイアウトを生む");
        }

        [Test]
        public void Generate_ManySeeds_AlwaysConnectedAndNoOverlap()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);
            var config = SmallConfig();

            for (ulong seed = 1; seed <= 50; seed++)
            {
                MapGenerationResult result = gen.Generate(seed, config);

                Assert.That(MapConnectivity.IsFullyConnected(result.Layout), Is.True,
                    $"seed={seed} で連結が壊れている（fallback={result.UsedFallback}）");

                // 占有セルの総数と footprint セルの総数が一致 = 重なりゼロ。
                Assert.That(CountUniqueCells(result.Layout), Is.EqualTo(CountFootprintCells(result.Layout)),
                    $"seed={seed} でモジュールが重なっている");
            }
        }

        [Test]
        public void Generate_StartReachesGoal()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);

            for (ulong seed = 1; seed <= 20; seed++)
            {
                MapLayout layout = gen.Generate(seed, SmallConfig()).Layout;

                int startSlot = -1, goalSlot = -1;
                for (int i = 0; i < layout.Count; i++)
                {
                    ModuleRole role = catalog[layout.Modules[i].ModuleIndex].Role;
                    if (role == ModuleRole.Start) startSlot = i;
                    if (role == ModuleRole.Goal) goalSlot = i;
                }

                Assert.That(startSlot, Is.GreaterThanOrEqualTo(0));
                Assert.That(goalSlot, Is.GreaterThanOrEqualTo(0));
                Assert.That(MapConnectivity.AreModulesConnected(layout, startSlot, goalSlot), Is.True,
                    $"seed={seed} で Start から Goal へ到達できない");
            }
        }

        [Test]
        public void Manifest_RoundTrips()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);
            MapLayout original = gen.Generate(seed: 777, SmallConfig()).Layout;

            MapManifest manifest = MapManifest.FromLayout(original);
            Assert.That(manifest.Version, Is.EqualTo(MapManifest.CurrentVersion));
            Assert.That(manifest.ModuleCount, Is.EqualTo(original.Count));

            bool ok = manifest.TryRebuild(catalog, out MapLayout rebuilt);
            Assert.That(ok, Is.True, "正しい manifest は再構築できる");
            Assert.That(rebuilt.Count, Is.EqualTo(original.Count));

            for (int i = 0; i < original.Count; i++)
            {
                Assert.That(rebuilt.Modules[i].ModuleIndex, Is.EqualTo(original.Modules[i].ModuleIndex));
                Assert.That(rebuilt.Modules[i].OriginCell, Is.EqualTo(original.Modules[i].OriginCell));
                Assert.That(rebuilt.Modules[i].RotationSteps, Is.EqualTo(original.Modules[i].RotationSteps));
            }
        }

        [Test]
        public void Manifest_RejectsCorruptedChecksum()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);
            MapLayout original = gen.Generate(seed: 555, SmallConfig()).Layout;

            MapManifest manifest = MapManifest.FromLayout(original);
            manifest.Checksum ^= 0x1; // 1 ビット破壊

            bool ok = manifest.TryRebuild(catalog, out MapLayout rebuilt);
            Assert.That(ok, Is.False, "checksum 不一致は拒否される（参加拒否に使える）");
            Assert.That(rebuilt, Is.Null);
        }

        [Test]
        public void Manifest_RejectsTamperedPlacement()
        {
            var catalog = BuildCatalog();
            var gen = new MapGenerator(catalog);
            MapLayout original = gen.Generate(seed: 321, SmallConfig()).Layout;

            MapManifest manifest = MapManifest.FromLayout(original);
            // checksum は据え置きで配置だけ改ざん → 再計算 checksum と食い違って弾かれる。
            manifest.Origins[0] += new Vector3Int(100, 0, 0);

            bool ok = manifest.TryRebuild(catalog, out _);
            Assert.That(ok, Is.False, "配置改ざんは checksum 照合で検出される");
        }



        [Test]
        public void BackDoors_SandboxCatalog_PlacesOneOrTwoExits_AndKeepsGraphConnected()
        {
            // 仕様: 裏口は「1〜2 個」（メイン出入口=Start とは別）。網目状を最大化するため最深の開口は
            // ループが優先的に使い、裏口は予約 1 で最低 1 を保証・空きがあれば 2 まで置く。
            // よって「ちょうど 2」ではなく「1〜2」を検証する（密 seed では 1 個になり得る）。
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            var config = MapGeneratorConfig.Default;
            config.BackDoorCount = 2;

            for (ulong seed = 1; seed <= 20; seed++)
            {
                MapGenerationResult result = gen.Generate(seed, config);
                MapLayout layout = result.Layout;
                MapPathGraph graph = MapPathGraph.Build(layout);
                int[] depths = MapPathGraph.ComputeModuleDepths(layout);

                int exitCount = CountModulesWithRole(layout, ModuleRole.Exit);
                Assert.That(exitCount, Is.InRange(1, 2), "seed=" + seed + " 裏口は 1〜2 個");
                Assert.That(MapConnectivity.IsFullyConnected(layout), Is.True, "seed=" + seed + " layout connected");
                Assert.That(graph.IsConnected(), Is.True, "seed=" + seed + " graph connected");

                for (int slot = 0; slot < layout.Count; slot++)
                {
                    if (catalog[layout.Modules[slot].ModuleIndex].Role == ModuleRole.Exit)
                        Assert.That(depths[slot], Is.GreaterThan(0), "seed=" + seed + " Exit depth");
                }
            }
        }

        [Test]
        public void BackDoors_SameSeed_IsBitIdenticalIncludingExit()
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            var config = MapGeneratorConfig.Default;
            config.BackDoorCount = 2;

            MapGenerationResult a = gen.Generate(seed: 0xBADC0DE, config);
            MapGenerationResult b = gen.Generate(seed: 0xBADC0DE, config);

            Assert.That(CountModulesWithRole(a.Layout, ModuleRole.Exit), Is.GreaterThanOrEqualTo(1));
            Assert.That(b.Layout.Count, Is.EqualTo(a.Layout.Count));
            for (int i = 0; i < a.Layout.Count; i++)
            {
                PlacedModule pa = a.Layout.Modules[i];
                PlacedModule pb = b.Layout.Modules[i];
                Assert.That(pb.ModuleIndex, Is.EqualTo(pa.ModuleIndex), "#" + i + " module");
                Assert.That(pb.OriginCell, Is.EqualTo(pa.OriginCell), "#" + i + " origin");
                Assert.That(pb.RotationSteps, Is.EqualTo(pa.RotationSteps), "#" + i + " rotation");
            }
            Assert.That(MapManifest.FromLayout(b.Layout).Checksum, Is.EqualTo(MapManifest.FromLayout(a.Layout).Checksum));
        }

        [Test]
        public void Manifest_RoundTrips_WithExitAndStableChecksum()
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            var config = MapGeneratorConfig.Default;
            config.BackDoorCount = 2;
            MapLayout original = gen.Generate(seed: 0xE117, config).Layout;

            MapManifest manifest = MapManifest.FromLayout(original);
            uint checksum = manifest.Checksum;

            Assert.That(CountModulesWithRole(original, ModuleRole.Exit), Is.GreaterThanOrEqualTo(1));
            Assert.That(manifest.TryRebuild(catalog, out MapLayout rebuilt), Is.True, "Exit あり manifest は再構築できる");
            Assert.That(MapManifest.FromLayout(rebuilt).Checksum, Is.EqualTo(checksum));
            Assert.That(CountModulesWithRole(rebuilt, ModuleRole.Exit),
                Is.EqualTo(CountModulesWithRole(original, ModuleRole.Exit)));

            for (int i = 0; i < original.Count; i++)
            {
                Assert.That(rebuilt.Modules[i].ModuleIndex, Is.EqualTo(original.Modules[i].ModuleIndex), "#" + i + " module");
                Assert.That(rebuilt.Modules[i].OriginCell, Is.EqualTo(original.Modules[i].OriginCell), "#" + i + " origin");
                Assert.That(rebuilt.Modules[i].RotationSteps, Is.EqualTo(original.Modules[i].RotationSteps), "#" + i + " rotation");
            }
        }
        [Test]
        public void LoopConnections_ManySandboxSeeds_ReducesZeroLoopRate_AndReportsCorridorMetrics()
        {
            LoopMetricSummary metrics = CollectLoopMetrics(firstSeed: 1, lastSeed: 50);
            TestContext.WriteLine(
                $"seeds={metrics.SeedCount} zeroLoopSeeds={metrics.ZeroLoopSeeds} " +
                $"zeroLoopRate={metrics.ZeroLoopRate:P1} addedCycles={metrics.TotalAddedCycles} " +
                $"connectorModules={metrics.TotalConnectorModules} avgCorridorLength={metrics.AverageCorridorLength:F2}");

            Assert.That(metrics.ZeroLoopRate, Is.LessThanOrEqualTo(0.12f),
                "LoopConnections=4 の 0 ループ seed 率が高すぎる");
            Assert.That(metrics.TotalAddedCycles, Is.GreaterThan(0), "ループ追加で閉路が増える");
            Assert.That(metrics.AverageCorridorLength, Is.GreaterThan(0f), "平均廊下長を集計できる");
        }

        [Test]
        public void LoopConnections_UsesSingleCellConnectorVariants_InsteadOfOnlyJunctions()
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            var config = MapGeneratorConfig.Default;
            config.LoopConnections = 6;

            int connectorModules = 0;
            int junctionModules = 0;
            bool usedStraight = false;
            bool usedCorner = false;

            for (ulong seed = 1; seed <= 50; seed++)
            {
                MapLayout layout = gen.Generate(seed, config).Layout;
                for (int i = 0; i < layout.Count; i++)
                {
                    ModuleSpec spec = catalog[layout.Modules[i].ModuleIndex];
                    if (spec.Role != ModuleRole.Connector)
                        continue;

                    connectorModules++;
                    if (spec.Id == "junction") junctionModules++;
                    if (spec.Id == "loop_straight") usedStraight = true;
                    if (spec.Id == "loop_corner") usedCorner = true;
                }
            }

            Assert.That(connectorModules, Is.GreaterThan(0), "ループ廊下 connector が配置される");
            Assert.That(junctionModules, Is.LessThan(connectorModules), "全廊下セルが 4 方位 junction になるのは避ける");
            Assert.That(usedStraight, Is.True, "直線 1 セル connector が使われる");
            Assert.That(usedCorner, Is.True, "角 1 セル connector が使われる");
        }

        [Test]
        public void LoopConnections_SandboxCatalog_IsBitIdenticalForSameSeed()
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            var config = MapGeneratorConfig.Default;
            config.LoopConnections = 6;

            MapGenerationResult a = gen.Generate(seed: 0xBEEF, config);
            MapGenerationResult b = gen.Generate(seed: 0xBEEF, config);

            Assert.That(b.Layout.Count, Is.EqualTo(a.Layout.Count));
            for (int i = 0; i < a.Layout.Count; i++)
            {
                PlacedModule pa = a.Layout.Modules[i];
                PlacedModule pb = b.Layout.Modules[i];
                Assert.That(pb.ModuleIndex, Is.EqualTo(pa.ModuleIndex), $"#{i} module");
                Assert.That(pb.OriginCell, Is.EqualTo(pa.OriginCell), $"#{i} origin");
                Assert.That(pb.RotationSteps, Is.EqualTo(pa.RotationSteps), $"#{i} rotation");
            }

            Assert.That(MapManifest.FromLayout(b.Layout).Checksum, Is.EqualTo(MapManifest.FromLayout(a.Layout).Checksum));
        }
    }
}
