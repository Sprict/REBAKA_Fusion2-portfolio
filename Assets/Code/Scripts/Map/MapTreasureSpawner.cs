// Assets/Code/Scripts/Map/MapTreasureSpawner.cs
using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 段階C（宝配線）: ホストが確定レイアウトから <see cref="MapSpawnPlanner"/> の配置プランを計算し、
    /// 宝 prefab をプラン上のワールドセルへ <c>Runner.Spawn</c> する。Fusion がクライアントへ自動配布する。
    ///
    /// 設計の要点（なぜこの形か）:
    ///   - 宝は <see cref="NetworkObject"/>。地形（非ネットワーク＋manifest ローカル再生成）とは違い、
    ///     ホスト権威 Spawn → state replication が正しい配線（既存 PlayerSpawner.SpawnWorldObjects と同型）。
    ///   - よって配置セルを <c>[Networked]</c> で配る必要はない。プランはホストでだけ計算すれば足りる
    ///     （クライアントは Spawn 結果を受け取るだけ）。プラン自体は決定論だが、ここでは権威ホスト 1 箇所のみ消費。
    ///   - 地形ビルド完了（<see cref="MapBuilder.Layout"/> 確定）を待ってから 1 度だけスポーンする。
    ///
    /// 同シーンに <see cref="MapBuilder"/>（地形）と <see cref="MapNetworkDistributor"/>（配布）がある前提。
    /// 敵は未実装のため対象外（プランの敵分は生成しない）。Value はスコア系が未存在のため適用せずログのみ。
    /// </summary>
    public sealed class MapTreasureSpawner : NetworkBehaviour
    {
        [Header("宝 prefab")]
        [Tooltip("スポーンする宝の NetworkObject prefab。本物の NetworkProjectConfig(Assets/Level/Photon) に登録必須。")]
        [SerializeField] private NetworkObject _treasurePrefab;

        [Header("計画パラメータ（線形スケール: value = base + slope*depth）")]
        [Tooltip("置こうとする宝の数（候補セルが尽きればこれ未満になる）。")]
        [SerializeField, Min(0)] private int _treasureBudget = 8;
        [SerializeField] private int _treasureBaseValue = 10;
        [SerializeField] private int _treasureValueSlope = 10;

        [Header("配置")]
        [Tooltip("セル床面からの持ち上げ高さ(m)。地形コライダーに食い込まないようにする。")]
        [SerializeField, Min(0f)] private float _spawnHeight = 0.5f;

        private MapBuilder _builder;
        private bool _spawned;

        public override void Spawned()
        {
            _builder = FindBuilder();
            if (_builder == null)
                Debug.LogError("[MapTreasureSpawner] MapBuilder がシーンに見つかりません。", this);
        }

        // ホストのみ: 地形レイアウトが確定したら 1 度だけ宝をスポーンする。
        // 物理を扱う Spawn は Render ではなく FixedUpdateNetwork で行う（Fusion 規約）。
        public override void FixedUpdateNetwork()
        {
            if (_spawned || !HasStateAuthority) return;
            if (_builder == null || _builder.Layout == null) return;

            SpawnTreasures(_builder.Layout);
            _spawned = true;
        }

        private void SpawnTreasures(MapLayout layout)
        {
            if (_treasurePrefab == null)
            {
                Debug.LogError("[MapTreasureSpawner] 宝 prefab が未割当。スポーンをスキップします。", this);
                return;
            }

            MapSpawnPlannerConfig config = MapSpawnPlannerConfig.Default;
            config.TreasureBudget = _treasureBudget;
            config.TreasureBaseValue = _treasureBaseValue;
            config.TreasureValueSlope = _treasureValueSlope;
            config.EnemyBudget = 0; // 敵は未実装。

            // ホスト権威 1 箇所のみ消費するので seed は地形 seed を流用（マップと対応づけて再現可能にする）。
            SpawnPlan plan = MapSpawnPlanner.Plan(layout, config, (ulong)_builder.Seed);

            int spawned = 0;
            foreach (SpawnPlacement p in plan.Placements)
            {
                if (p.Kind != SpawnKind.Treasure) continue;

                Vector3 pos = _builder.CellToWorld(p.Cell) + Vector3.up * _spawnHeight;
                Runner.Spawn(_treasurePrefab, pos, Quaternion.identity);
                spawned++;
            }

            Debug.Log($"[MapTreasureSpawner] 宝スポーン完了 count={spawned}/{plan.CountOf(SpawnKind.Treasure)} seed={_builder.Seed}", this);
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
