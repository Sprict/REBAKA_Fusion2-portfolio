// Assets/Code/Scripts/Map/MapBuilder.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 確定レイアウトを各クライアントでローカル Instantiate する Unity 層（段階 B）。
    ///
    /// 役割: シード＋カタログから <see cref="MapLayout"/> を生成し、各モジュールの prefab を
    /// ワールド姿勢で Instantiate する。地形そのものは非ネットワーク（devlog 2026-06-27 §1/§10）。
    /// 段階 C ではこの Build を「受信した manifest を TryRebuild → 同じ Instantiate 経路」に差し替える。
    ///
    /// 配置規約（prefab 側オーサリングの前提）:
    ///   - prefab のローカル原点 = モジュールセル (0,0,0)、回転 0 の向き。
    ///   - 1 セル = <see cref="_cellSize"/> m。
    ///   - 配置 = position: originCell * cellSize、rotation: Y 軸 90°×rotationSteps。
    ///     これは <see cref="GridRotation.RotateCell"/>（時計回り (x,z)→(z,-x)）と一致する。
    ///
    /// prefab 未割当（または Catalog 未設定）のモジュールはプレースホルダ（役割色の床タイル）を生成するので、
    /// アセットを 1 つも作らなくても生成パイプライン全体を目視確認できる。
    /// </summary>
    public sealed class MapBuilder : MonoBehaviour
    {
        [Header("カタログ")]
        [Tooltip("使うモジュールカタログ。未設定なら組み込みサンドボックスカタログ（コード定義）を使う。")]
        [SerializeField] private ModuleCatalogAsset _catalogAsset;

        [Header("生成")]
        [SerializeField] private int _seed = 12345;
        [SerializeField, Min(0)] private int _mainPathLength = 5;
        [SerializeField, Min(0)] private int _branchCount = 2;
        [SerializeField, Min(0)] private int _branchLength = 2;
        [SerializeField, Min(1)] private int _maxPlacementTries = 16;
        [SerializeField, Min(0)] private int _maxRerolls = 16;
        [Tooltip("ツリー構築後に閉じるループ（閉路）の最大数。0 で一本道ツリー。網目状にするには増やす。")]
        [SerializeField, Min(0)] private int _loopConnections = 4;

        [Tooltip("生成後に最深の開口へ置く裏口数。0..2。")]
        [SerializeField, Range(0, 2)] private int _backDoorCount = 2;

        [Header("配置")]
        [Tooltip("1 セルあたりのワールドサイズ（m）。prefab のオーサリングスケールと一致させる。")]
        [SerializeField, Min(0.1f)] private float _cellSize = 2f;

        [Tooltip("Start 時に自動で Build する（Play モード確認用）。")]
        [SerializeField] private bool _buildOnStart = true;

        [Header("プレースホルダ")]
        [Tooltip("prefab 未割当のモジュールに床タイルを生成して目視できるようにする。")]
        [SerializeField] private bool _placeholderWhenPrefabMissing = true;

        private const string GeneratedRootName = "_Generated";

        // --- 生成結果（AI・段階 C 配布が参照する） ---
        public ModuleCatalog Catalog { get; private set; }
        public MapLayout Layout { get; private set; }
        public MapPathGraph Graph { get; private set; }
        public MapGenerationResult LastResult { get; private set; }

        private void Start()
        {
            if (_buildOnStart)
                Build();
        }

        /// <summary>このビルダーが使う生成シード（段階C: ホストが生成に使う）。</summary>
        public int Seed => _seed;

        /// <summary>このビルダーの生成パラメータ（段階C: ホストが生成に使う）。</summary>
        public MapGeneratorConfig CurrentConfig => new MapGeneratorConfig
        {
            MainPathLength = _mainPathLength,
            BranchCount = _branchCount,
            BranchLength = _branchLength,
            MaxPlacementTries = _maxPlacementTries,
            MaxRerolls = _maxRerolls,
            LoopConnections = _loopConnections,
            BackDoorCount = _backDoorCount,
        };

        /// <summary>カタログを解決してキャッシュする（生成はしない）。段階C のホスト生成・クライアント復元で共有する。</summary>
        public ModuleCatalog GetOrResolveCatalog()
        {
            if (Catalog == null)
                Catalog = ResolveCatalog();
            return Catalog;
        }

        /// <summary>ローカル生成: シードからレイアウトを生成して Instantiate する（サンドボックス・段階B 確認用）。</summary>
        [ContextMenu("Build")]
        public void Build()
        {
            ModuleCatalog catalog = GetOrResolveCatalog();
            if (catalog == null)
            {
                Debug.LogError("[MapBuilder] カタログを解決できません（Catalog アセットに未割当モジュールがある可能性）。", this);
                return;
            }

            var generator = new MapGenerator(catalog);
            LastResult = generator.Generate((ulong)_seed, CurrentConfig);
            Realize(LastResult.Layout);
        }

        /// <summary>
        /// 配布 manifest からレイアウトを復元して Instantiate する（段階C: 全ピア共通の入口）。
        /// checksum がカタログと不一致なら何もせず false（参加拒否に使える）。
        /// </summary>
        public bool BuildFromManifest(MapManifest manifest)
        {
            ModuleCatalog catalog = GetOrResolveCatalog();
            if (catalog == null)
            {
                Debug.LogError("[MapBuilder] カタログを解決できません。", this);
                return false;
            }
            if (manifest == null || !manifest.TryRebuild(catalog, out MapLayout layout))
            {
                Debug.LogError("[MapBuilder] manifest の復元に失敗（checksum 不一致・配列不整合の可能性）。", this);
                return false;
            }
            Realize(layout);
            return true;
        }

        // レイアウトを受け取り、グラフ構築＋ Instantiate する共通経路。既存生成物は先に破棄する。
        private void Realize(MapLayout layout)
        {
            Clear();
            Layout = layout;
            Graph = MapPathGraph.Build(layout);

            Transform root = GetOrCreateGeneratedRoot();
            for (int slot = 0; slot < layout.Count; slot++)
                InstantiateModule(layout.Modules[slot], root);
        }

        /// <summary>生成物（_Generated 配下）を破棄する。Edit/Play 両対応。</summary>
        [ContextMenu("Clear")]
        public void Clear()
        {
            Transform root = transform.Find(GeneratedRootName);
            if (root != null)
                DestroySafe(root.gameObject);
        }

        /// <summary>現在の生成状態の要約（ログ・デバッグ用）。</summary>
        [ContextMenu("Log Summary")]
        private void LogSummary()
        {
            if (Layout == null) { Debug.Log("[MapBuilder] (未生成)", this); return; }
            bool connected = Graph != null && Graph.IsConnected();
            Debug.Log($"[MapBuilder] seed={_seed} modules={Layout.Count} nodes={Graph?.NodeCount} " +
                      $"graphConnected={connected} fallback={LastResult.UsedFallback} rerolls={LastResult.RerollsUsed}", this);
        }

        // --- 内部 ---------------------------------------------------------------

        private ModuleCatalog ResolveCatalog()
        {
            if (_catalogAsset != null)
            {
                if (_catalogAsset.TryBuildCatalog(out ModuleCatalog fromAsset))
                    return fromAsset;
                return null; // 穴あきカタログ
            }
            return SandboxCatalog.Build();
        }

        private void InstantiateModule(PlacedModule pm, Transform root)
        {
            GameObject prefab = _catalogAsset != null ? _catalogAsset.PrefabAt(pm.ModuleIndex) : null;

            if (prefab != null)
            {
                Vector3 pos = CellToWorld(pm.OriginCell);
                Quaternion rot = Quaternion.Euler(0f, GridRotation.ToDegrees(pm.RotationSteps), 0f);
                GameObject instance = Instantiate(prefab, pos, rot, root);
                instance.name = $"{Catalog[pm.ModuleIndex].Id}_{pm.OriginCell.x}_{pm.OriginCell.z}";
                return;
            }

            if (_placeholderWhenPrefabMissing)
                InstantiatePlaceholder(pm, root);
        }

        // prefab 未割当時: ワールド footprint セルごとに役割色の床タイルを置く（Gizmo 確認と同じ見え方）。
        private void InstantiatePlaceholder(PlacedModule pm, Transform root)
        {
            ModuleRole role = Catalog[pm.ModuleIndex].Role;
            Color color = RoleColor(role);
            var tileScale = new Vector3(_cellSize * 0.95f, _cellSize * 0.1f, _cellSize * 0.95f);

            var group = new GameObject($"{Catalog[pm.ModuleIndex].Id}_{pm.OriginCell.x}_{pm.OriginCell.z}");
            group.transform.SetParent(root, false);

            var mpb = new MaterialPropertyBlock();
            // URP(_BaseColor) と Built-in(_Color) の両方をセット（MPB は存在しないプロパティを無視する）。
            mpb.SetColor("_BaseColor", color);
            mpb.SetColor("_Color", color);

            foreach (Vector3Int cell in pm.WorldFootprint(Catalog))
            {
                GameObject tile = GameObject.CreatePrimitive(PrimitiveType.Cube);
                tile.name = $"cell_{cell.x}_{cell.z}";
                tile.transform.SetParent(group.transform, false);
                tile.transform.position = CellToWorld(cell);
                tile.transform.localScale = tileScale;
                tile.GetComponent<Renderer>().SetPropertyBlock(mpb);
            }
        }

        /// <summary>
        /// グリッドセル → ワールド座標。段階C のスポーナーが <see cref="MapSpawnPlanner"/> の
        /// 配置セルを Instantiate/Spawn 位置へ変換するのに使う（地形と同じ配置規約で一致させる）。
        /// </summary>
        public Vector3 CellToWorld(Vector3Int cell)
        {
            return transform.position + new Vector3(cell.x, cell.y, cell.z) * _cellSize;
        }

        private Transform GetOrCreateGeneratedRoot()
        {
            Transform root = transform.Find(GeneratedRootName);
            if (root != null)
                return root;
            var go = new GameObject(GeneratedRootName);
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        private void DestroySafe(GameObject go)
        {
            if (Application.isPlaying)
                Destroy(go);
            else
                DestroyImmediate(go);
        }

        private static Color RoleColor(ModuleRole role)
        {
            switch (role)
            {
                case ModuleRole.Start: return new Color(0.2f, 0.8f, 0.3f);
                case ModuleRole.Goal: return new Color(0.85f, 0.25f, 0.25f);
                case ModuleRole.DeadEnd: return new Color(0.85f, 0.8f, 0.2f);
                case ModuleRole.Exit: return new Color(0.2f, 0.45f, 0.95f);
                default: return new Color(0.6f, 0.6f, 0.65f);
            }
        }
    }
}
