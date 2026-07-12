# 深さ→宝/敵スポーン重み付け 純コア（MapSpawnPlanner）

> **作成日:** 2026-06-30
> **ステータス:** 純コア＋EditMode 実装・検証完了（Map 41/41 緑）。Instantiate / `[Networked]` 配布は次スライス。
> **関連:** `2026-06-29_map_backdoors_and_depth.md`（深さメトリクス）、`2026-06-29_map_generation_competitor_study.md`（方式決定 §5-2）、`TECHNICAL_DESIGN.md` §6.4

---

## 1. 何を作ったか / なぜ

競合調査の決定（§5-2「ダンジョンの奥ほど宝が多く・敵が厄介」）を生成コアに接続する第一スライス。
`MapPathGraph.ComputeModuleDepths`（Start からのグラフ距離）を消費し、**深さ→「宝の価値・敵の脅威度」を
線形スケールで決める純粋・決定論的な配置プラン計算** `MapSpawnPlanner` を追加した。

### スコープを最小スライスに絞った理由
着手前の調査で判明: **宝はピックアップとしては存在するが、マップ生成が宝/敵をモジュールへ配置する仕組みは
未実装**。敵エンティティに至っては存在しない。よって「全部（Instantiate ＋ `[Networked]` 配布 ＋ 敵プレハブ）」を
一気に作るのは過大。既存マップコアの分離原則——**生成(MapGenerator)・配布(MapManifest)・ナビ(MapPathGraph) が
すべて純粋＝EditMode 完全検証可能、Unity/Network 配線は MapBuilder/MapNetworkDistributor へ分離**——に倣い、
まず「どこに・何を・どれだけの価値で置くか」を決める**純コアだけ**を作って単体テストで固定した。
Instantiate / `[Networked]` 配布は次スライス（MapBuilder/Distributor 相当がこのプランを消費する）。

## 2. 実装の中身（原理）

`Assets/Code/Scripts/Map/MapSpawnPlanner.cs`（純コア・MonoBehaviour/Fusion 非依存）:

- **型**: `SpawnKind`(Treasure/Enemy) / `SpawnPlacement`(ModuleSlot, Cell, Kind, Value, Depth・readonly struct) /
  `SpawnPlan`(プラン列＋CountOf) / `MapSpawnPlannerConfig`(予算・線形係数・MinEnemyDepth)。
- **線形スケール**: `value = base + slope * depth`（宝＝価値、敵＝脅威度）。既定 base=10/slope=10（宝）, 1/1（敵）。
- **配置確率も深さ比例**: モジュール抽選の重み = `max(1, depth)`。既存 `MapGenerator.PickWeighted` と同じ
  「重み累積 → `rng.NextInt(total)` → 減算」イディオムを流用。深いモジュールほど宝/敵が乗りやすい。
- **配置足場**: 各モジュールの `PathNodes`（ナビノード）をワールドへ写したセル。無ければ footprint セル。
- **安全フィルタ**: 深さ<1（Start=0・未到達=-1）と Start 役割は宝・敵とも除外。敵は `MinEnemyDepth` 未満を除外
  （Start 近傍の安全地帯）。同一セルに 2 個重ねない（`HashSet` membership で判定）。
- **決定論ガード**: 整数演算のみ・`DeterministicRng`・列挙はモジュール index/セル定義順で安定（`Dictionary`/`HashSet`
  の列挙順に依存しない）。宝→敵の固定順で rng を消費。同一 (layout, config, seed) で**ビット同一プラン**。

## 3. なぜ「線形スケール」を選んだか（不採用案）

| 案 | 内容 | 判定 |
|---|---|---|
| **線形スケール（採用）** | value = base + slope*depth、確率も深さ比例 | ✓ 最小・説明容易・チューニングは base/slope 2ノブ。土台に最適。後で曲線化可。 |
| 閾値 tier（浅/中/深3段） | 深さを閾値で 3 段階に量子化 | △ LC/R.E.P.O. 体験に近いが閾値調整が要る。線形で土台を作ってから検討。 |
| 重み関数を手書き | DepthToValue を任意関数で | △ 表現力は高いが今は過剰。線形で十分。 |

線形は**「深さが結果を一意に決める」ことをテストで厳密検証できる**（value == base+slope*depth の等式）のが利点。

## 4. 検証（EditMode・7 テスト）

`Assets/Code/Tests/EditMode/Map/MapSpawnPlannerTests.cs`:
- `Plan_SameSeed_IsBitIdentical` … 同一 seed で全フィールド一致（決定論）。
- `Plan_DifferentSeed_DiffersSomewhere` … seed を変えると変化（rng が効いている）。
- `TreasureValue_IsLinearInDepth` … 全配置で value == base + slope*depth、Depth == モジュール深さ。
- `NoSpawn_InStartModule_AndEnemiesRespectMinDepth` … 20 seed、Start 不在・深さ≥1・敵≥MinEnemyDepth。
- `Budget_IsNeverExceeded` … 宝/敵とも予算以下。
- `AllCells_AreWithinModuleFootprints_AndUnique` … 全セルがモジュール占有内・重複なし。
- `Treasure_BiasedTowardDeeperModules` … 30 seed 集計で「宝の平均深さ > 適格モジュールの平均深さ」。
  weight∝depth の下で配置深さ期待値は E[d²]/E[d] = mean + var/mean > mean ＝ 深さばらつきがあれば必ず成立。

結果: `uloop compile` 0 error/warning、Map 名前空間 **41/41 緑**（新規 7 含む）。
（全 EditMode は 79/80。残 1 は `BalanceJointVibrationTests` の AprProfile 調整値テストで本変更と無関係。）

## 5. 残課題 / 次スライス

- **次スライス（配線）**: `SpawnPlan` を消費して宝/敵を Instantiate ＋ ホストが `[Networked]` で配布
  （MapBuilder/MapNetworkDistributor と同じ host 権威・LayoutReady ゲート）。敵プレハブ・宝プレハブの用意。
- **別軸チューニング（目視で判明・後回し）**: A=裏口が浅めにぶれる / B=網目密度が薄い（独立閉路1〜2）。
  マップ規模拡大マイルストーンで `reservedOpenExits`/`LoopConnections` と併せ再評価。
- 線形で物足りなければ閾値 tier / 曲線化へ拡張（base/slope を残したまま差し替え可能）。

## 6. 自力再実装チェックリスト

- [ ] なぜ配置計画を「純コア（EditMode 可）」と「Instantiate/配布（Unity/Network）」に分けるか説明できる
- [ ] 線形スケール value=base+slope*depth の利点（厳密テスト可・2ノブ）を言える
- [ ] 深さ重み max(1,depth) で配置が深い側に偏る理由（E[d²]/E[d] > E[d]）を説明できる
- [ ] 決定論を保つための条件（整数のみ・専用 rng・列挙順非依存・固定消費順）を列挙できる
- [ ] Start 除外・MinEnemyDepth・セル重複回避が「体験」と「破綻回避」のどちらの制約か区別できる
