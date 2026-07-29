// Assets/Code/Tests/EditMode/Map/MapSpawnPlannerTests.cs
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MyFolder.Scripts.Map;

namespace MyFolder.Scripts.Tests.Map
{
    /// <summary>
    /// (C) 深さ→宝/敵スポーン重み付け 純コアの検証（Unity Editor 非依存）。
    /// 検証対象: 決定論（同一 seed → 同一プラン）、線形 value、Start 除外 / MinEnemyDepth、
    /// 予算遵守、セルがモジュール占有内、深さ偏り（深いほど多く置かれる）。
    /// </summary>
    public class MapSpawnPlannerTests
    {
        private static MapLayout BuildLayout(int seed)
        {
            var catalog = SandboxCatalog.Build();
            var config = new MapGeneratorConfig
            {
                MainPathLength = 4,
                BranchCount = 2,
                BranchLength = 1,
                MaxPlacementTries = 16,
                MaxRerolls = 16,
                LoopConnections = 4,
                BackDoorCount = 2,
            };
            return new MapGenerator(catalog).Generate((ulong)seed, config).Layout;
        }

        private static int StartSlot(MapLayout layout)
        {
            for (int i = 0; i < layout.Count; i++)
                if (layout.Catalog[layout.Modules[i].ModuleIndex].Role == ModuleRole.Start)
                    return i;
            return -1;
        }

        [Test]
        public void Plan_SameSeed_IsBitIdentical()
        {
            MapLayout layout = BuildLayout(12345);
            var cfg = MapSpawnPlannerConfig.Default;

            SpawnPlan a = MapSpawnPlanner.Plan(layout, cfg, 777);
            SpawnPlan b = MapSpawnPlanner.Plan(layout, cfg, 777);

            Assert.AreEqual(a.Count, b.Count, "同一 seed でプラン件数が一致しない");
            for (int i = 0; i < a.Count; i++)
            {
                SpawnPlacement pa = a.Placements[i];
                SpawnPlacement pb = b.Placements[i];
                Assert.AreEqual(pa.ModuleSlot, pb.ModuleSlot);
                Assert.AreEqual(pa.Cell, pb.Cell);
                Assert.AreEqual(pa.Kind, pb.Kind);
                Assert.AreEqual(pa.Value, pb.Value);
                Assert.AreEqual(pa.Depth, pb.Depth);
            }
        }

        [Test]
        public void Plan_DifferentSeed_DiffersSomewhere()
        {
            MapLayout layout = BuildLayout(12345);
            var cfg = MapSpawnPlannerConfig.Default;

            SpawnPlan a = MapSpawnPlanner.Plan(layout, cfg, 1);
            SpawnPlan b = MapSpawnPlanner.Plan(layout, cfg, 2);

            bool identical = a.Count == b.Count;
            if (identical)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (a.Placements[i].Cell != b.Placements[i].Cell ||
                        a.Placements[i].ModuleSlot != b.Placements[i].ModuleSlot)
                    {
                        identical = false;
                        break;
                    }
                }
            }
            Assert.IsFalse(identical, "seed を変えてもプランが完全一致した（rng が効いていない疑い）");
        }

        [Test]
        public void TreasureValue_IsLinearInDepth()
        {
            MapLayout layout = BuildLayout(7);
            var cfg = MapSpawnPlannerConfig.Default;
            int[] depths = MapPathGraph.ComputeModuleDepths(layout);

            SpawnPlan plan = MapSpawnPlanner.Plan(layout, cfg, 42);
            Assert.Greater(plan.CountOf(SpawnKind.Treasure), 0, "宝が 1 つも置かれていない");

            foreach (SpawnPlacement p in plan.Placements)
            {
                Assert.AreEqual(depths[p.ModuleSlot], p.Depth, "Depth がモジュール深さと不一致");
                int expected = p.Kind == SpawnKind.Treasure
                    ? cfg.TreasureBaseValue + cfg.TreasureValueSlope * p.Depth
                    : cfg.EnemyBaseThreat + cfg.EnemyThreatSlope * p.Depth;
                Assert.AreEqual(expected, p.Value, "value が線形式 base + slope*depth と不一致");
            }
        }

        [Test]
        public void NoSpawn_InStartModule_AndEnemiesRespectMinDepth()
        {
            var cfg = MapSpawnPlannerConfig.Default;
            for (int seed = 1; seed <= 20; seed++)
            {
                MapLayout layout = BuildLayout(seed);
                int startSlot = StartSlot(layout);
                SpawnPlan plan = MapSpawnPlanner.Plan(layout, cfg, (ulong)(seed * 31 + 5));

                foreach (SpawnPlacement p in plan.Placements)
                {
                    Assert.AreNotEqual(startSlot, p.ModuleSlot, $"seed={seed}: Start にスポーンした");
                    Assert.GreaterOrEqual(p.Depth, 1, $"seed={seed}: 深さ<1 にスポーンした");
                    if (p.Kind == SpawnKind.Enemy)
                        Assert.GreaterOrEqual(p.Depth, cfg.MinEnemyDepth,
                            $"seed={seed}: 敵が MinEnemyDepth 未満に出た");
                }
            }
        }

        [Test]
        public void Budget_IsNeverExceeded()
        {
            var cfg = MapSpawnPlannerConfig.Default;
            for (int seed = 1; seed <= 20; seed++)
            {
                MapLayout layout = BuildLayout(seed);
                SpawnPlan plan = MapSpawnPlanner.Plan(layout, cfg, (ulong)(seed * 17 + 3));
                Assert.LessOrEqual(plan.CountOf(SpawnKind.Treasure), cfg.TreasureBudget);
                Assert.LessOrEqual(plan.CountOf(SpawnKind.Enemy), cfg.EnemyBudget);
            }
        }

        [Test]
        public void AllCells_AreWithinModuleFootprints_AndUnique()
        {
            var cfg = MapSpawnPlannerConfig.Default;
            for (int seed = 1; seed <= 20; seed++)
            {
                MapLayout layout = BuildLayout(seed);
                Dictionary<Vector3Int, int> occupancy = layout.BuildOccupancy();
                SpawnPlan plan = MapSpawnPlanner.Plan(layout, cfg, (ulong)(seed * 13 + 9));

                var seen = new HashSet<Vector3Int>();
                foreach (SpawnPlacement p in plan.Placements)
                {
                    Assert.IsTrue(occupancy.ContainsKey(p.Cell),
                        $"seed={seed}: スポーンセルがどのモジュール占有内でもない {p.Cell}");
                    Assert.IsTrue(seen.Add(p.Cell),
                        $"seed={seed}: 同一セルに 2 個スポーンした {p.Cell}");
                }
            }
        }

        // 深さ重み（weight = max(1, depth)）の効果: 宝の平均深さ > 適格モジュールの平均深さ。
        // weight∝depth の下で配置深さの期待値は E[d^2]/E[d] = mean + var/mean > mean となるため、
        // 深さにばらつきがあれば必ず成り立つ（多数 seed で安定）。
        [Test]
        public void Treasure_BiasedTowardDeeperModules()
        {
            var cfg = MapSpawnPlannerConfig.Default;

            long placementDepthSum = 0;
            long placementCount = 0;
            long eligibleDepthSum = 0;
            long eligibleCount = 0;

            for (int seed = 1; seed <= 30; seed++)
            {
                MapLayout layout = BuildLayout(seed);
                int[] depths = MapPathGraph.ComputeModuleDepths(layout);
                int startSlot = StartSlot(layout);

                // 適格モジュール（planner と同条件: depth>=1 かつ非 Start。footprint があるため必ず候補セルあり）。
                for (int slot = 0; slot < layout.Count; slot++)
                {
                    if (slot == startSlot) continue;
                    if (depths[slot] < 1) continue;
                    eligibleDepthSum += depths[slot];
                    eligibleCount++;
                }

                SpawnPlan plan = MapSpawnPlanner.Plan(layout, cfg, (ulong)(seed * 101 + 7));
                foreach (SpawnPlacement p in plan.Placements)
                {
                    if (p.Kind != SpawnKind.Treasure) continue;
                    placementDepthSum += p.Depth;
                    placementCount++;
                }
            }

            Assert.Greater(placementCount, 0, "宝が 1 つも置かれていない");
            double avgPlacementDepth = placementDepthSum / (double)placementCount;
            double avgEligibleDepth = eligibleDepthSum / (double)eligibleCount;
            Assert.Greater(avgPlacementDepth, avgEligibleDepth,
                $"宝の平均深さ({avgPlacementDepth:F2})が適格平均({avgEligibleDepth:F2})を上回らない＝深さ重みが効いていない");
        }
    }
}
