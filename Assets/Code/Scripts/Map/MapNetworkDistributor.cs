// Assets/Code/Scripts/Map/MapNetworkDistributor.cs
using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 段階C: ホストが確定したレイアウトを最小 manifest として `[Networked]` 状態で配り、
    /// 各ピアが**非ネットワークでローカルに地形を Instantiate** する（devlog 2026-06-27 §6 / E2）。
    ///
    /// 設計（決定 devlog E2）:
    ///   - クライアントに生成アルゴリズムを再実行させない（プラットフォーム差での非決定性を避ける）。
    ///     ホストが生成した**配置リスト**を networked で配る。地形メッシュは各ピアがローカル生成。
    ///   - 一発 RPC ではなく `[Networked]` 状態にするのは、後から参加したクライアントにも
    ///     state sync で確実に届くため（late-join 対応）。
    ///   - checksum はカタログ Id まで含むため、ホスト/クライアントのカタログ不一致を検出できる。
    ///
    /// 同一 GameObject か同シーンに <see cref="MapBuilder"/> がある前提（地形生成を委譲する）。
    /// </summary>
    public sealed class MapNetworkDistributor : NetworkBehaviour
    {
        /// <summary>配置 1 件分の networked 表現（PlacedModule の固定長シリアライズ素体）。</summary>
        public struct NetworkPlacement : INetworkStruct
        {
            public int Index;
            public int X;
            public int Y;
            public int Z;
            public int Rot;
        }

        /// <summary>networked 配列の固定容量。1 マップの最大モジュール数。</summary>
        public const int MaxModules = 128;

        [Networked] public int Count { get; set; }
        [Networked] public int SeedLow { get; set; }
        [Networked] public int SeedHigh { get; set; }
        [Networked] public int ChecksumBits { get; set; }
        [Networked] public NetworkBool LayoutReady { get; set; }

        [Networked, Capacity(MaxModules)]
        public NetworkArray<NetworkPlacement> Placements => default;

        private MapBuilder _builder;
        private bool _built;
        private uint _appliedChecksum;

        public override void Spawned()
        {
            _builder = FindBuilder();
            if (_builder == null)
            {
                Debug.LogError("[MapNetworkDistributor] MapBuilder がシーンに見つかりません。", this);
                return;
            }

            // ホスト（StateAuthority）のみ生成して manifest を networked 状態へ書く。
            if (HasStateAuthority)
                GenerateAndPublish();
        }

        // 全ピア共通: networked 状態が届いたら（ホストは公開直後、クライアントは state sync 後）
        // ローカルに地形をビルドする。Render は毎フレーム走るが checksum でガードして 1 回だけ実行。
        public override void Render()
        {
            if (_builder == null || _built) return;
            if (Count <= 0 || !LayoutReady) return;

            MapManifest manifest = ReadManifest();
            if (_builder.BuildFromManifest(manifest))
            {
                _built = true;
                _appliedChecksum = manifest.Checksum;
                Debug.Log($"[MapNetworkDistributor] 地形ビルド完了 modules={Count} checksum={_appliedChecksum} authority={HasStateAuthority}", this);
            }
        }

        // --- ホスト側 -----------------------------------------------------------

        private void GenerateAndPublish()
        {
            ModuleCatalog catalog = _builder.GetOrResolveCatalog();
            if (catalog == null)
            {
                Debug.LogError("[MapNetworkDistributor] カタログを解決できません。", this);
                return;
            }

            var generator = new MapGenerator(catalog);
            MapGenerationResult result = generator.Generate((ulong)_builder.Seed, _builder.CurrentConfig);
            MapManifest manifest = MapManifest.FromLayout(result.Layout);

            int n = manifest.ModuleCount;
            if (n > MaxModules)
            {
                Debug.LogError($"[MapNetworkDistributor] モジュール数 {n} が容量 {MaxModules} を超過。容量を増やすか生成規模を下げてください。", this);
                n = MaxModules;
            }

            Count = n;
            SeedLow = unchecked((int)(manifest.Seed & 0xFFFFFFFFUL));
            SeedHigh = unchecked((int)(manifest.Seed >> 32));
            ChecksumBits = unchecked((int)manifest.Checksum);

            for (int i = 0; i < n; i++)
            {
                Placements.Set(i, new NetworkPlacement
                {
                    Index = manifest.ModuleIndices[i],
                    X = manifest.Origins[i].x,
                    Y = manifest.Origins[i].y,
                    Z = manifest.Origins[i].z,
                    Rot = manifest.Rotations[i],
                });
            }

            LayoutReady = true;
            Debug.Log($"[MapNetworkDistributor] manifest 公開 modules={n} seed={manifest.Seed} checksum={manifest.Checksum}", this);
        }

        // --- 受信側: networked 状態 → MapManifest 復元 ---------------------------

        private MapManifest ReadManifest()
        {
            int n = Count;
            var manifest = new MapManifest
            {
                Version = MapManifest.CurrentVersion,
                Seed = ((ulong)(uint)SeedHigh << 32) | (uint)SeedLow,
                ModuleIndices = new int[n],
                Origins = new Vector3Int[n],
                Rotations = new int[n],
                Checksum = unchecked((uint)ChecksumBits),
            };

            for (int i = 0; i < n; i++)
            {
                NetworkPlacement p = Placements.Get(i);
                manifest.ModuleIndices[i] = p.Index;
                manifest.Origins[i] = new Vector3Int(p.X, p.Y, p.Z);
                manifest.Rotations[i] = p.Rot;
            }
            return manifest;
        }

        private static MapBuilder FindBuilder()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<MapBuilder>();
#else
            return UnityEngine.Object.FindObjectOfType<MapBuilder>();
#endif
        }
    }
}
