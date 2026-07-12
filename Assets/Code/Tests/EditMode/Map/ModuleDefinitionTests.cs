// Assets/Code/Tests/EditMode/Map/ModuleDefinitionTests.cs
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using MyFolder.Scripts.Map;

namespace MyFolder.Scripts.Tests.Map
{
    /// <summary>
    /// 段階 B（Unity 層）の変換の検証。
    /// ModuleDefinition(ScriptableObject) → ModuleSpec(純粋データ) のオーサリング往復と、
    /// ModuleCatalogAsset の「並び順 = カタログ index」「穴あき拒否」を保証する。
    /// </summary>
    public class ModuleDefinitionTests
    {
        // SerializeField private なオーサリング項目を SerializedObject 経由で組む
        // （public セッタを増やさずテストから組み立てるため）。
        private static ModuleDefinition MakeDefinition(
            string id, ModuleRole role, int weight,
            Vector3Int[] footprint,
            ModuleDefinition.SocketAuthoring[] sockets,
            Vector3Int[] pathNodes,
            ModuleDefinition.EdgeAuthoring[] edges)
        {
            var def = ScriptableObject.CreateInstance<ModuleDefinition>();
            var so = new UnityEditor.SerializedObject(def);
            so.FindProperty("_id").stringValue = id;
            so.FindProperty("_role").enumValueIndex = (int)role;
            so.FindProperty("_weight").intValue = weight;
            SetVector3IntList(so.FindProperty("_footprintCells"), footprint);
            SetVector3IntList(so.FindProperty("_pathNodes"), pathNodes);

            SerializedProperty sp = so.FindProperty("_sockets");
            sp.arraySize = sockets.Length;
            for (int i = 0; i < sockets.Length; i++)
            {
                SerializedProperty e = sp.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("localCell").vector3IntValue = sockets[i].localCell;
                e.FindPropertyRelative("facing").enumValueIndex = (int)sockets[i].facing;
                e.FindPropertyRelative("channel").intValue = sockets[i].channel;
                e.FindPropertyRelative("clearance").intValue = sockets[i].clearance;
            }

            SerializedProperty ep = so.FindProperty("_internalEdges");
            ep.arraySize = edges.Length;
            for (int i = 0; i < edges.Length; i++)
            {
                SerializedProperty e = ep.GetArrayElementAtIndex(i);
                e.FindPropertyRelative("nodeA").intValue = edges[i].nodeA;
                e.FindPropertyRelative("nodeB").intValue = edges[i].nodeB;
                e.FindPropertyRelative("clearance").intValue = edges[i].clearance;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            return def;
        }

        private static void SetVector3IntList(SerializedProperty prop, Vector3Int[] values)
        {
            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                prop.GetArrayElementAtIndex(i).vector3IntValue = values[i];
        }

        private static ModuleDefinition.SocketAuthoring Socket(Vector3Int cell, MapDirection facing, int channel, int clearance)
            => new ModuleDefinition.SocketAuthoring { localCell = cell, facing = facing, channel = channel, clearance = clearance };

        private static ModuleDefinition.EdgeAuthoring Edge(int a, int b, int clearance)
            => new ModuleDefinition.EdgeAuthoring { nodeA = a, nodeB = b, clearance = clearance };

        [Test]
        public void ToSpec_RoundTripsAuthoringData()
        {
            var def = MakeDefinition(
                "corridor", ModuleRole.Body, weight: 3,
                footprint: new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1) },
                sockets: new[]
                {
                    Socket(new Vector3Int(0, 0, 0), MapDirection.South, 0, 1),
                    Socket(new Vector3Int(0, 0, 1), MapDirection.North, 0, 2),
                },
                pathNodes: new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1) },
                edges: new[] { Edge(0, 1, 2) });

            ModuleSpec spec = def.ToSpec();

            Assert.That(spec.Id, Is.EqualTo("corridor"));
            Assert.That(spec.Role, Is.EqualTo(ModuleRole.Body));
            Assert.That(spec.Weight, Is.EqualTo(3));
            Assert.That(spec.FootprintCells, Is.EqualTo(new[] { new Vector3Int(0, 0, 0), new Vector3Int(0, 0, 1) }));

            Assert.That(spec.Sockets.Count, Is.EqualTo(2));
            Assert.That(spec.Sockets[0].Facing, Is.EqualTo(MapDirection.South));
            Assert.That(spec.Sockets[0].Clearance, Is.EqualTo(1));
            Assert.That(spec.Sockets[1].Facing, Is.EqualTo(MapDirection.North));
            Assert.That(spec.Sockets[1].Clearance, Is.EqualTo(2));

            Assert.That(spec.PathNodes.Count, Is.EqualTo(2));
            Assert.That(spec.InternalEdges.Count, Is.EqualTo(1));
            Assert.That(spec.InternalEdges[0].NodeA, Is.EqualTo(0));
            Assert.That(spec.InternalEdges[0].NodeB, Is.EqualTo(1));
            Assert.That(spec.InternalEdges[0].Profile.Clearance, Is.EqualTo(2));

            Object.DestroyImmediate(def);
        }

        [Test]
        public void ToSpec_ClampsClearanceAndUsesAssetNameWhenIdEmpty()
        {
            var def = MakeDefinition(
                "", ModuleRole.Start, weight: 1,
                footprint: new[] { new Vector3Int(0, 0, 0) },
                sockets: new[] { Socket(new Vector3Int(0, 0, 0), MapDirection.North, 0, 0) }, // clearance 0 → 1 へクランプ
                pathNodes: new[] { new Vector3Int(0, 0, 0) },
                edges: new ModuleDefinition.EdgeAuthoring[0]);
            def.name = "asset_named_start";

            ModuleSpec spec = def.ToSpec();

            Assert.That(spec.Id, Is.EqualTo("asset_named_start"), "ID 空ならアセット名を使う");
            Assert.That(spec.Sockets[0].Clearance, Is.EqualTo(1), "clearance 0 は 1 にクランプ");

            Object.DestroyImmediate(def);
        }

        [Test]
        public void CatalogAsset_PreservesOrder_AndBuildsUsableCatalog()
        {
            var start = MakeDefinition("start", ModuleRole.Start, 1,
                new[] { new Vector3Int(0, 0, 0) },
                new[] { Socket(new Vector3Int(0, 0, 0), MapDirection.North, 0, 1) },
                new[] { new Vector3Int(0, 0, 0) }, new ModuleDefinition.EdgeAuthoring[0]);
            var goal = MakeDefinition("goal", ModuleRole.Goal, 1,
                new[] { new Vector3Int(0, 0, 0) },
                new[] { Socket(new Vector3Int(0, 0, 0), MapDirection.North, 0, 1) },
                new[] { new Vector3Int(0, 0, 0) }, new ModuleDefinition.EdgeAuthoring[0]);

            var asset = ScriptableObject.CreateInstance<ModuleCatalogAsset>();
            var so = new UnityEditor.SerializedObject(asset);
            SerializedProperty list = so.FindProperty("_modules");
            list.arraySize = 2;
            list.GetArrayElementAtIndex(0).objectReferenceValue = start;
            list.GetArrayElementAtIndex(1).objectReferenceValue = goal;
            so.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(asset.TryBuildCatalog(out ModuleCatalog catalog), Is.True);
            Assert.That(catalog.Count, Is.EqualTo(2));
            Assert.That(catalog[0].Id, Is.EqualTo("start"), "index 0 = リスト先頭");
            Assert.That(catalog[1].Id, Is.EqualTo("goal"), "index 1 = リスト 2 番目");
            Assert.That(catalog.FirstIndexWithRole(ModuleRole.Goal), Is.EqualTo(1));

            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(start);
            Object.DestroyImmediate(goal);
        }

        [Test]
        public void CatalogAsset_RejectsHoles_ToPreventIndexShift()
        {
            var only = MakeDefinition("start", ModuleRole.Start, 1,
                new[] { new Vector3Int(0, 0, 0) },
                new[] { Socket(new Vector3Int(0, 0, 0), MapDirection.North, 0, 1) },
                new[] { new Vector3Int(0, 0, 0) }, new ModuleDefinition.EdgeAuthoring[0]);

            var asset = ScriptableObject.CreateInstance<ModuleCatalogAsset>();
            var so = new UnityEditor.SerializedObject(asset);
            SerializedProperty list = so.FindProperty("_modules");
            list.arraySize = 2;
            list.GetArrayElementAtIndex(0).objectReferenceValue = only;
            list.GetArrayElementAtIndex(1).objectReferenceValue = null; // 穴
            so.ApplyModifiedPropertiesWithoutUndo();

            Assert.That(asset.TryBuildCatalog(out ModuleCatalog catalog), Is.False, "穴あきは index ズレを生むので拒否");
            Assert.That(catalog, Is.Null);

            Object.DestroyImmediate(asset);
            Object.DestroyImmediate(only);
        }

        [Test]
        public void SandboxCatalog_GeneratesConnectedLayout_ThroughBuilderPipeline()
        {
            // MapBuilder が使うのと同じカタログ＋生成経路で、連結レイアウトと連結グラフが得られることを確認。
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);

            for (ulong seed = 1; seed <= 20; seed++)
            {
                MapGenerationResult result = gen.Generate(seed, MapGeneratorConfig.Default);
                Assert.That(result.Layout.Count, Is.GreaterThanOrEqualTo(2), $"seed={seed}");
                MapPathGraph graph = MapPathGraph.Build(result.Layout);
                Assert.That(graph.IsConnected(), Is.True, $"seed={seed} でグラフ非連結");
            }
        }

        [Test]
        public void LoopConnections_ProduceCycles_WhileStayingConnected()
        {
            // 連結グラフの閉路数 = edges - nodes + 1（連結成分 1 つの場合）。
            // LoopConnections=0 はツリー（閉路 0）、>0 で閉路が増えることを確認する。
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);

            var treeConfig = MapGeneratorConfig.Default;
            treeConfig.LoopConnections = 0;
            var loopConfig = MapGeneratorConfig.Default;
            loopConfig.LoopConnections = 6;

            int totalTreeCycles = 0, totalLoopCycles = 0, seedsWithMoreLoops = 0;
            for (ulong seed = 1; seed <= 30; seed++)
            {
                MapPathGraph tree = MapPathGraph.Build(gen.Generate(seed, treeConfig).Layout);
                MapPathGraph loop = MapPathGraph.Build(gen.Generate(seed, loopConfig).Layout);

                Assert.That(loop.IsConnected(), Is.True, $"seed={seed} ループ版が非連結");

                int treeCycles = tree.Edges.Count - tree.NodeCount + 1;
                int loopCycles = loop.Edges.Count - loop.NodeCount + 1;
                Assert.That(loopCycles, Is.GreaterThanOrEqualTo(treeCycles), $"seed={seed} ループ版の閉路がツリーを下回らない");

                totalTreeCycles += treeCycles;
                totalLoopCycles += loopCycles;
                if (loopCycles > treeCycles)
                    seedsWithMoreLoops++;
            }

            // exact connector は旧 junction 埋めより余計な接続を作らないため、seed 数と合計増分の両方で判定する。
            Assert.That(totalLoopCycles, Is.GreaterThan(totalTreeCycles + 35),
                $"LoopConnections>0 で閉路の総数が十分増えていない: tree={totalTreeCycles} loop={totalLoopCycles}");
            Assert.That(seedsWithMoreLoops, Is.GreaterThanOrEqualTo(24),
                $"閉路が増えた seed が少なすぎる: {seedsWithMoreLoops}/30");
        }
    }
}
