// Assets/Code/Tests/EditMode/Map/MapPathGraphTests.cs
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using MyFolder.Scripts.Map;

namespace MyFolder.Scripts.Tests.Map
{
    /// <summary>
    /// N1 埋め込みパスグラフの検証（devlog 2026-06-27 §6 / §10）。
    /// 核心不変条件: 「レイアウトが連結 = パスグラフが連結」「Start モジュールから Goal モジュールへ A* 到達」。
    /// 加えて、戸口の通行幅（clearance）で敵サイズ別にパスをフィルタできること。
    /// </summary>
    public class MapPathGraphTests
    {
        // ナビ対応カタログ。各 1 セルモジュールは中心 (0,0,0) にパスノードを 1 個持つ。
        // 戸口セル = ノードセル = (0,0,0) なので、噛み合ったソケット越しに単一ノード同士が結ばれる。
        private static ModuleCatalog BuildNavCatalog()
        {
            var one = new[] { new Vector3Int(0, 0, 0) };
            var center = new[] { new Vector3Int(0, 0, 0) };

            var start = new ModuleSpec("start", ModuleRole.Start, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North) },
                weight: 1, pathNodes: center);

            var goal = new ModuleSpec("goal", ModuleRole.Goal, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North) },
                weight: 1, pathNodes: center);

            var straight = new ModuleSpec("straight", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South),
                }, weight: 3, pathNodes: center);

            var corner = new ModuleSpec("corner", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.East),
                }, weight: 2, pathNodes: center);

            var tee = new ModuleSpec("tee", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.East),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.West),
                }, weight: 1, pathNodes: center);

            var deadEnd = new ModuleSpec("deadend", ModuleRole.DeadEnd, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South) },
                weight: 1, pathNodes: center);

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

        // 任意レイアウトをテストから組むためのヘルパ。manifest 往復で内部 Add を経由する
        // （MapLayout.Add は internal なのでテスト側からは直接呼べない）。
        private static MapLayout BuildLayout(ModuleCatalog catalog, int[] indices, Vector3Int[] origins, int[] rotations)
        {
            var manifest = new MapManifest
            {
                Version = MapManifest.CurrentVersion,
                Seed = 0,
                ModuleIndices = indices,
                Origins = origins,
                Rotations = rotations,
            };
            manifest.Checksum = manifest.ComputeChecksum(catalog);
            Assert.That(manifest.TryRebuild(catalog, out MapLayout layout), Is.True, "テスト用レイアウトを再構築できる");
            return layout;
        }

        private static int SlotWithRole(MapLayout layout, ModuleRole role)
        {
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout.Catalog[layout.Modules[i].ModuleIndex].Role == role)
                    return i;
            }
            return -1;
        }


        [Test]
        public void ComputeModuleDepths_StartIsZero_AndExitIsDeeperThanStart()
        {
            var one = new[] { new Vector3Int(0, 0, 0) };
            var center = new[] { new Vector3Int(0, 0, 0) };
            var catalog = new ModuleCatalog(new[]
            {
                new ModuleSpec("start", ModuleRole.Start, one,
                    new[] { new MapSocket(Vector3Int.zero, MapDirection.North) },
                    weight: 1, pathNodes: center),
                new ModuleSpec("tee", ModuleRole.Body, one,
                    new[]
                    {
                        new MapSocket(Vector3Int.zero, MapDirection.South),
                        new MapSocket(Vector3Int.zero, MapDirection.North),
                        new MapSocket(Vector3Int.zero, MapDirection.East),
                    }, weight: 1, pathNodes: center),
                new ModuleSpec("goal", ModuleRole.Goal, one,
                    new[] { new MapSocket(Vector3Int.zero, MapDirection.South) },
                    weight: 1, pathNodes: center),
                new ModuleSpec("exit", ModuleRole.Exit, one,
                    new[] { new MapSocket(Vector3Int.zero, MapDirection.South) },
                    weight: 1, pathNodes: center),
            });

            var layout = BuildLayout(catalog,
                new[] { 0, 1, 2, 3 },
                new[]
                {
                    Vector3Int.zero,
                    new Vector3Int(0, 0, 1),
                    new Vector3Int(0, 0, 2),
                    new Vector3Int(1, 0, 1),
                },
                new[] { 0, 0, 0, 1 });

            int[] depthsA = MapPathGraph.ComputeModuleDepths(layout);
            int[] depthsB = MapPathGraph.ComputeModuleDepths(layout);

            Assert.That(depthsA, Is.EqualTo(new[] { 0, 1, 2, 2 }));
            Assert.That(depthsB, Is.EqualTo(depthsA), "同一レイアウトの深さ計算は決定論的");
            Assert.That(depthsA[SlotWithRole(layout, ModuleRole.Goal)], Is.GreaterThan(0));
            Assert.That(depthsA[SlotWithRole(layout, ModuleRole.Exit)], Is.GreaterThan(0));
        }
        [Test]
        public void Build_AdjacentModules_AreLinkedAndConnected()
        {
            var catalog = BuildNavCatalog(); // 0=start(N), 1=goal(N)
            // start を原点、goal をその北隣 (0,0,1)。goal を 180° 回して socket を南向き（start を向く）に。
            var layout = BuildLayout(catalog,
                new[] { 0, 1 },
                new[] { Vector3Int.zero, new Vector3Int(0, 0, 1) },
                new[] { 0, 2 });

            MapPathGraph graph = MapPathGraph.Build(layout);

            Assert.That(graph.NodeCount, Is.EqualTo(2), "1 セル×2 モジュール = ノード 2");
            Assert.That(graph.Edges.Count, Is.EqualTo(1), "噛み合う戸口 1 対 = 跨ぎ辺 1");
            Assert.That(graph.IsConnected(), Is.True);
        }

        [Test]
        public void Build_GeneratedLayout_GraphConnected_AndStartReachesGoal()
        {
            var catalog = BuildNavCatalog();
            var gen = new MapGenerator(catalog);

            for (ulong seed = 1; seed <= 50; seed++)
            {
                MapLayout layout = gen.Generate(seed, SmallConfig()).Layout;
                MapPathGraph graph = MapPathGraph.Build(layout);

                // 全 1 セルモジュールにノードが 1 個ずつ → ノード数 = モジュール数。
                Assert.That(graph.NodeCount, Is.EqualTo(layout.Count), $"seed={seed} ノード数");

                // レイアウト連結 = グラフ連結（N1 の核心不変条件）。
                Assert.That(graph.IsConnected(), Is.True, $"seed={seed} でパスグラフが非連結");

                int startSlot = SlotWithRole(layout, ModuleRole.Start);
                int goalSlot = SlotWithRole(layout, ModuleRole.Goal);
                Assert.That(startSlot, Is.GreaterThanOrEqualTo(0));
                Assert.That(goalSlot, Is.GreaterThanOrEqualTo(0));

                Assert.That(graph.TryFindPathBetweenModules(startSlot, goalSlot, minClearance: 1, out List<int> path), Is.True,
                    $"seed={seed} で Start→Goal の A* 経路なし");
                Assert.That(path, Is.Not.Null);
                Assert.That(path.Count, Is.GreaterThanOrEqualTo(2));
            }
        }

        [Test]
        public void Build_IsDeterministic_ForSameLayout()
        {
            var catalog = BuildNavCatalog();
            var gen = new MapGenerator(catalog);
            MapLayout layout = gen.Generate(seed: 4242, SmallConfig()).Layout;

            MapPathGraph a = MapPathGraph.Build(layout);
            MapPathGraph b = MapPathGraph.Build(layout);

            Assert.That(b.NodeCount, Is.EqualTo(a.NodeCount));
            Assert.That(b.Edges.Count, Is.EqualTo(a.Edges.Count));
            for (int i = 0; i < a.Edges.Count; i++)
            {
                Assert.That(b.Edges[i].A, Is.EqualTo(a.Edges[i].A), $"edge #{i} A");
                Assert.That(b.Edges[i].B, Is.EqualTo(a.Edges[i].B), $"edge #{i} B");
                Assert.That(b.Edges[i].Profile.Clearance, Is.EqualTo(a.Edges[i].Profile.Clearance), $"edge #{i} clearance");
            }
        }

        [Test]
        public void MultiCellModule_InternalEdge_IsTraversable()
        {
            // 2 セルの廊下モジュール: footprint {(0,0,0),(0,0,1)}、ノード 2 個、内部辺 0-1。
            var hallFootprint = new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1) };
            var hallNodes = new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1) };
            var hallEdges = new[] { new ModulePathEdge(0, 1, new TraversalProfile(2)) };
            var hall = new ModuleSpec("hall", ModuleRole.Start, hallFootprint,
                new[] { new MapSocket(new Vector3Int(0, 0, 1), MapDirection.North) },
                weight: 1, pathNodes: hallNodes, internalEdges: hallEdges);
            var catalog = new ModuleCatalog(new[] { hall });

            var layout = BuildLayout(catalog,
                new[] { 0 }, new[] { Vector3Int.zero }, new[] { 0 });

            MapPathGraph graph = MapPathGraph.Build(layout);

            Assert.That(graph.NodeCount, Is.EqualTo(2));
            Assert.That(graph.Edges.Count, Is.EqualTo(1), "内部辺 1 本");
            Assert.That(graph.IsConnected(), Is.True);
            Assert.That(graph.TryFindPath(0, 1, minClearance: 1, out List<int> path), Is.True);
            Assert.That(path, Is.EqualTo(new List<int> { 0, 1 }));
        }

        [Test]
        public void Clearance_NarrowDoorway_BlocksLargeEnemy()
        {
            // 一本道 start—straight—goal。start↔straight の戸口だけ clearance=1、残りは clearance=2。
            // 大きい敵（minClearance=2）は start の戸口で詰み、Start→Goal 経路が無い。
            var one = new[] { new Vector3Int(0, 0, 0) };
            var center = new[] { new Vector3Int(0, 0, 0) };

            var start = new ModuleSpec("start", ModuleRole.Start, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North, channel: 0, clearance: 1) },
                weight: 1, pathNodes: center);
            var straight = new ModuleSpec("straight", ModuleRole.Body, one,
                new[]
                {
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South, channel: 0, clearance: 2),
                    new MapSocket(new Vector3Int(0, 0, 0), MapDirection.North, channel: 0, clearance: 2),
                }, weight: 1, pathNodes: center);
            var goal = new ModuleSpec("goal", ModuleRole.Goal, one,
                new[] { new MapSocket(new Vector3Int(0, 0, 0), MapDirection.South, channel: 0, clearance: 2) },
                weight: 1, pathNodes: center);
            var catalog = new ModuleCatalog(new[] { start, straight, goal });

            // start(0,0,0) — straight(0,0,1) — goal(0,0,2)、すべて回転 0。
            var layout = BuildLayout(catalog,
                new[] { 0, 1, 2 },
                new[] { Vector3Int.zero, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, 2) },
                new[] { 0, 0, 0 });

            MapPathGraph graph = MapPathGraph.Build(layout);

            Assert.That(graph.NodeCount, Is.EqualTo(3));
            Assert.That(graph.Edges.Count, Is.EqualTo(2));
            Assert.That(graph.IsConnected(), Is.True);

            int startSlot = SlotWithRole(layout, ModuleRole.Start);
            int goalSlot = SlotWithRole(layout, ModuleRole.Goal);

            Assert.That(graph.TryFindPathBetweenModules(startSlot, goalSlot, minClearance: 1, out _), Is.True,
                "小型敵（clearance 1）は通れる");
            Assert.That(graph.TryFindPathBetweenModules(startSlot, goalSlot, minClearance: 2, out _), Is.False,
                "大型敵（clearance 2）は狭い戸口で詰む");
        }
    }
}
