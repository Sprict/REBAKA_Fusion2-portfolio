// Assets/Code/Tests/EditMode/Map/MapBuilderManifestTests.cs
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using MyFolder.Scripts.Map;

namespace MyFolder.Scripts.Tests.Map
{
    /// <summary>
    /// 段階C の核心データ契約の検証（Fusion トランスポートを除いた純粋部分）。
    /// MapNetworkDistributor は「ホストが MapManifest.FromLayout で作った配置を networked で配り、
    /// 各ピアが MapBuilder.BuildFromManifest で復元する」だけ。その復元がホストのレイアウトを
    /// ビット同一に再現すること（＝全ピアの地形が一致）と、checksum 不一致を弾くことを保証する。
    /// 実際の 2 ピア同期は ParrelSync の手動検証（自動化不可）。
    /// </summary>
    public class MapBuilderManifestTests
    {
        private static MapBuilder NewBuilder(out GameObject go)
        {
            go = new GameObject("mapbuilder_test");
            return go.AddComponent<MapBuilder>(); // _catalogAsset 未割当 → SandboxCatalog を使う
        }

        [Test]
        public void BuildFromManifest_ReproducesHostLayout_BitIdentical()
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            MapLayout hostLayout = gen.Generate(seed: 777, MapGeneratorConfig.Default).Layout;
            MapManifest manifest = MapManifest.FromLayout(hostLayout);

            MapBuilder builder = NewBuilder(out GameObject go);
            try
            {
                Assert.That(builder.BuildFromManifest(manifest), Is.True, "checksum 一致で復元成功");
                Assert.That(builder.Layout.Count, Is.EqualTo(hostLayout.Count), "モジュール数一致");

                for (int i = 0; i < hostLayout.Count; i++)
                {
                    PlacedModule h = hostLayout.Modules[i];
                    PlacedModule c = builder.Layout.Modules[i];
                    Assert.That(c.ModuleIndex, Is.EqualTo(h.ModuleIndex), $"#{i} index");
                    Assert.That(c.OriginCell, Is.EqualTo(h.OriginCell), $"#{i} origin");
                    Assert.That(c.RotationSteps, Is.EqualTo(h.RotationSteps), $"#{i} rotation");
                }

                Assert.That(builder.Graph.IsConnected(), Is.True, "復元グラフも連結");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BuildFromManifest_RejectsChecksumMismatch()
        {
            ModuleCatalog catalog = SandboxCatalog.Build();
            var gen = new MapGenerator(catalog);
            MapLayout hostLayout = gen.Generate(seed: 42, MapGeneratorConfig.Default).Layout;
            MapManifest manifest = MapManifest.FromLayout(hostLayout);

            // 配布途中の破損・カタログ不一致を模す（checksum を改竄）。
            manifest.Checksum = unchecked(manifest.Checksum + 1u);

            MapBuilder builder = NewBuilder(out GameObject go);
            try
            {
                // BuildFromManifest は復元失敗時に Debug.LogError を出す（参加拒否の可視化）。期待を宣言する。
                LogAssert.Expect(LogType.Error, new Regex("manifest の復元に失敗"));
                Assert.That(builder.BuildFromManifest(manifest), Is.False, "checksum 不一致は復元拒否");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
