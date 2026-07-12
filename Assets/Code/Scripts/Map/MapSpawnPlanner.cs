// Assets/Code/Scripts/Map/MapSpawnPlanner.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>スポーンする中身の種別。</summary>
    public enum SpawnKind
    {
        /// <summary>宝（運搬対象）。深いほど高価値。</summary>
        Treasure,
        /// <summary>敵。深いほど脅威度が高い。</summary>
        Enemy,
    }

    /// <summary>
    /// 配置プラン 1 件。どのモジュール（slot）のどのワールドセルに、何を、どれだけの価値/脅威度で置くか。
    /// 浮動小数を持たない離散データ。Instantiate / [Networked] 配布はこのプランを後段が消費して行う。
    /// </summary>
    public readonly struct SpawnPlacement
    {
        /// <summary>配置先モジュールの slot（layout.Modules の index）。</summary>
        public readonly int ModuleSlot;

        /// <summary>ワールドセル。モジュールの占有セル/パスノード上に乗る。</summary>
        public readonly Vector3Int Cell;

        public readonly SpawnKind Kind;

        /// <summary>線形スケール値（宝＝価値 / 敵＝脅威度）。base + slope*depth。</summary>
        public readonly int Value;

        /// <summary>このモジュールの深さ（Start からのグラフ距離）。デバッグ・後段の重み再利用用。</summary>
        public readonly int Depth;

        public SpawnPlacement(int moduleSlot, Vector3Int cell, SpawnKind kind, int value, int depth)
        {
            ModuleSlot = moduleSlot;
            Cell = cell;
            Kind = kind;
            Value = value;
            Depth = depth;
        }
    }

    /// <summary>配置プランの結果。決定論なので同一 (layout, config, seed) で同一内容になる。</summary>
    public sealed class SpawnPlan
    {
        private readonly List<SpawnPlacement> _placements;

        public SpawnPlan(List<SpawnPlacement> placements)
        {
            _placements = placements ?? new List<SpawnPlacement>();
        }

        public IReadOnlyList<SpawnPlacement> Placements => _placements;
        public int Count => _placements.Count;

        public int CountOf(SpawnKind kind)
        {
            int n = 0;
            for (int i = 0; i < _placements.Count; i++)
                if (_placements[i].Kind == kind) n++;
            return n;
        }
    }

    /// <summary>
    /// スポーン計画のパラメータ。線形スケール（value = base + slope*depth）。
    /// 配置確率も深さに比例（深いモジュールほど選ばれやすい）。整数のみ＝決定論。
    /// </summary>
    public struct MapSpawnPlannerConfig
    {
        /// <summary>置こうとする宝の数（候補セルが尽きればこれ未満になる）。</summary>
        public int TreasureBudget;
        /// <summary>置こうとする敵の数。</summary>
        public int EnemyBudget;

        /// <summary>宝価値 = TreasureBaseValue + TreasureValueSlope * depth。</summary>
        public int TreasureBaseValue;
        public int TreasureValueSlope;

        /// <summary>敵脅威度 = EnemyBaseThreat + EnemyThreatSlope * depth。</summary>
        public int EnemyBaseThreat;
        public int EnemyThreatSlope;

        /// <summary>この深さ未満のモジュールには敵を置かない（Start 近傍の安全地帯）。</summary>
        public int MinEnemyDepth;

        public static MapSpawnPlannerConfig Default => new MapSpawnPlannerConfig
        {
            TreasureBudget = 8,
            EnemyBudget = 4,
            TreasureBaseValue = 10,
            TreasureValueSlope = 10,
            EnemyBaseThreat = 1,
            EnemyThreatSlope = 1,
            MinEnemyDepth = 2,
        };
    }

    /// <summary>
    /// 深さ（Start からのグラフ距離）を「宝の価値・敵の脅威度」へ変換する純粋・決定論的な配置プラン計算。
    ///
    /// 設計（既存マップコアと同じ分離）:
    /// - MonoBehaviour / Fusion 非依存 → EditMode で完全検証可能。
    /// - <see cref="MapPathGraph.ComputeModuleDepths"/> を消費。配置確率は深さ比例、価値/脅威度は線形。
    /// - Instantiate / [Networked] 配布 / 敵プレハブは持たない（次スライスで MapBuilder/Distributor 相当が消費）。
    ///
    /// 決定論ガード: 整数演算のみ・<see cref="DeterministicRng"/>・列挙はモジュール index / セル順で安定。
    /// </summary>
    public static class MapSpawnPlanner
    {
        // 候補セルが埋まっている場合の空きセル探索の上限（無限ループ防止・決定論を保つ固定値）。
        private const int MaxCellProbesPerPlacement = 8;

        public static SpawnPlan Plan(MapLayout layout, MapSpawnPlannerConfig config, ulong seed)
        {
            var placements = new List<SpawnPlacement>();
            if (layout == null || layout.Count == 0)
                return new SpawnPlan(placements);

            var rng = new DeterministicRng(seed);
            int[] depths = MapPathGraph.ComputeModuleDepths(layout);

            // モジュールごとの候補ワールドセル（パスノード優先・無ければ footprint）。index 順で安定。
            var candidateCells = new List<Vector3Int>[layout.Count];
            for (int slot = 0; slot < layout.Count; slot++)
                candidateCells[slot] = CollectCandidateCells(layout, slot);

            // 適格モジュール（深さ・役割でフィルタ）。layout index 順で安定。
            var treasureEligible = new List<int>();
            var enemyEligible = new List<int>();
            for (int slot = 0; slot < layout.Count; slot++)
            {
                int depth = depths[slot];
                if (depth < 1) continue; // Start(=0) と未到達(-1) は宝・敵とも除外。
                if (candidateCells[slot].Count == 0) continue;

                ModuleRole role = layout.Catalog[layout.Modules[slot].ModuleIndex].Role;
                if (role == ModuleRole.Start) continue;

                treasureEligible.Add(slot);
                if (depth >= config.MinEnemyDepth)
                    enemyEligible.Add(slot);
            }

            // 1 セルに 2 個重ねない（種別跨ぎ含む）。membership 用途なので列挙順非依存。
            var usedCells = new HashSet<Vector3Int>();

            // 宝 → 敵 の固定順で rng を消費（決定論）。
            PlaceKind(SpawnKind.Treasure, config.TreasureBudget, treasureEligible,
                candidateCells, depths, config.TreasureBaseValue, config.TreasureValueSlope,
                rng, usedCells, placements);

            PlaceKind(SpawnKind.Enemy, config.EnemyBudget, enemyEligible,
                candidateCells, depths, config.EnemyBaseThreat, config.EnemyThreatSlope,
                rng, usedCells, placements);

            return new SpawnPlan(placements);
        }

        private static void PlaceKind(
            SpawnKind kind, int budget, List<int> eligible,
            List<Vector3Int>[] candidateCells, int[] depths, int baseValue, int slope,
            DeterministicRng rng, HashSet<Vector3Int> usedCells, List<SpawnPlacement> placements)
        {
            for (int placed = 0; placed < budget; placed++)
            {
                int slot = PickModuleWeightedByDepth(eligible, depths, rng);
                if (slot < 0)
                    return; // 適格モジュールが無い → これ以上置けない。

                if (!TryPickFreeCell(candidateCells[slot], rng, usedCells, out Vector3Int cell))
                    continue; // この回は置けなかった（結果数が budget 未満になり得る）。

                int depth = depths[slot];
                int value = baseValue + slope * depth;
                usedCells.Add(cell);
                placements.Add(new SpawnPlacement(slot, cell, kind, value, depth));
            }
        }

        /// <summary>深さ重み（max(1, depth)）でモジュールを 1 つ抽選。eligible 空なら -1。</summary>
        private static int PickModuleWeightedByDepth(List<int> eligible, int[] depths, DeterministicRng rng)
        {
            if (eligible.Count == 0)
                return -1;

            int total = 0;
            for (int i = 0; i < eligible.Count; i++)
                total += DepthWeight(depths[eligible[i]]);
            if (total <= 0)
                return eligible[0];

            int roll = rng.NextInt(total);
            for (int i = 0; i < eligible.Count; i++)
            {
                roll -= DepthWeight(depths[eligible[i]]);
                if (roll < 0)
                    return eligible[i];
            }
            return eligible[eligible.Count - 1];
        }

        private static int DepthWeight(int depth) => depth < 1 ? 1 : depth;

        /// <summary>候補セルから未使用セルを 1 つ選ぶ。全部埋まっていれば false。</summary>
        private static bool TryPickFreeCell(
            List<Vector3Int> cells, DeterministicRng rng, HashSet<Vector3Int> usedCells, out Vector3Int picked)
        {
            picked = default;
            if (cells.Count == 0)
                return false;

            int start = rng.NextInt(cells.Count);
            int probes = cells.Count < MaxCellProbesPerPlacement ? cells.Count : MaxCellProbesPerPlacement;
            for (int k = 0; k < probes; k++)
            {
                Vector3Int cell = cells[(start + k) % cells.Count];
                if (!usedCells.Contains(cell))
                {
                    picked = cell;
                    return true;
                }
            }
            return false;
        }

        /// <summary>モジュールの配置足場セル（ワールド）。パスノード優先、無ければ footprint。順序は spec 定義順で安定。</summary>
        private static List<Vector3Int> CollectCandidateCells(MapLayout layout, int slot)
        {
            var result = new List<Vector3Int>();
            PlacedModule pm = layout.Modules[slot];
            ModuleSpec spec = layout.Catalog[pm.ModuleIndex];

            if (spec.PathNodes.Count > 0)
            {
                for (int i = 0; i < spec.PathNodes.Count; i++)
                    result.Add(pm.OriginCell + GridRotation.RotateCell(spec.PathNodes[i], pm.RotationSteps));
                return result;
            }

            foreach (Vector3Int cell in pm.WorldFootprint(layout.Catalog))
                result.Add(cell);
            return result;
        }
    }
}
