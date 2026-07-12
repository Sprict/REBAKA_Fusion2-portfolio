# 宝スポーン配線（MapTreasureSpawner・段階C）

> **作成日:** 2026-06-30
> **ステータス:** コード配線・コンパイル完了（0/0）。**ホスト/クライアント実機テストは未実施（Unity GUI 必須）**。
> **関連:** `2026-06-30_map_spawn_planner.md`（純コア (C)）、`2026-06-27_map_generation_decision.md`（方式・E2）、`TECHNICAL_DESIGN.md` §6.4

---

## 1. 何を作ったか / なぜ

純コア `MapSpawnPlanner`（深さ→宝/敵の配置プラン）の結果を、初めて実際の `Treasure` スポーンへ接続する配線スライス。
ホストが確定レイアウトからプランを計算し、宝 prefab をプラン上のワールドセルへ `Runner.Spawn` する
`MapTreasureSpawner`（`NetworkBehaviour`）を追加した。

## 2. 設計判断（なぜこの配線か）

### 宝は「ホスト Spawn → 自動レプリケーション」。地形の manifest 方式は使わない
地形（`MapBuilder`/`MapNetworkDistributor`）は **非ネットワーク＋`[Networked]` manifest をクライアントがローカル再生成**
する方式だった。理由は「地形メッシュを全部 networked にすると重い・プラットフォーム差で非決定になる」から。

宝は事情が違う。**宝は `NetworkObject`**（`Treasure : NetworkBehaviour`、質量分配などを `[Networked]` で持つ）。
よって正しい配線は既存 `PlayerSpawner.SpawnWorldObjects` と同型の **ホスト権威 `Runner.Spawn` → Fusion が
state replication でクライアントへ配布**。配置セルを自前で `[Networked]` 配列にして配る必要はない。

| 案 | 内容 | 判定 |
|---|---|---|
| **ホスト Spawn（採用）** | ホストが Plan を消費し `Runner.Spawn`。Fusion が自動配布 | ✓ NetworkObject の標準。既存 worldPrefabs と同型。最小。 |
| 地形と同じ manifest 方式 | 配置セルを `[Networked]` で配り各ピアがローカル Instantiate | ✗ 宝は NetworkObject。ローカル Instantiate だと同期されない・二重生成になる。 |

### プランはホスト 1 箇所でだけ消費する
純コアは決定論（同一 seed → 同一プラン）だが、**ここでは権威ホストだけがプランを消費**するので、
クライアント側の再計算は不要。地形 seed（`MapBuilder.Seed`）を流用して「マップと宝の対応」を再現可能にした。

### ゲート: `MapBuilder.Layout` 確定を待つ
ホストは `MapNetworkDistributor.Spawned` で manifest を公開し、`Render` で地形をビルドして `MapBuilder.Layout` を確定する。
スポーナーは `FixedUpdateNetwork` で `HasStateAuthority && Layout != null` を満たした最初の 1 回だけ実行する
（`_spawned` ガード）。物理オブジェクト Spawn を `Render` ではなく `FixedUpdateNetwork` で行うのは Fusion 規約に従うため。

## 3. スコープを宝のみに絞った理由
- **敵エンティティは未存在**（プレハブも設計もない）→ プランの敵分は生成しない（`EnemyBudget = 0`）。
- **`Value`（宝価値）はスコア系が未存在** → 適用先が無い `[Networked]` 状態を `Treasure` に足すのは死蔵。今はログのみ。
  スコア/抽出ループ実装時に消費する。

## 4. 影響範囲 / 変更点
- 新規: `Assets/Code/Scripts/Map/MapTreasureSpawner.cs`（`NetworkBehaviour`、薄いグルー）。
- 変更: `MapBuilder.CellToWorld` を `private` → `public`（配置規約を地形と一致させるため共有・1 行）。
- 純コア・生成・配布・既存 Treasure には触れていない。

## 5. 検証
- `uloop compile` 0 error / 0 warning。
- 新規 EditMode テストは追加せず。スポーナーは「Plan→CellToWorld→Spawn」のグルーで純ロジックを持たず、
  検証には `NetworkRunner` が要る（EditMode で Runner をモックするのは過剰）。純コアの正しさは
  `MapSpawnPlannerTests`（41/41 緑）で既に固定済み。
- **未実施（要 Unity GUI・あなたの手番）**: ホスト/クライアント実機テスト（30 秒以上）。

## 6. 残作業（Unity 側・手番）
1. **宝 prefab を用意**し、本物の `NetworkProjectConfig`（`Assets/Level/Photon`）の prefab テーブルに登録。
   （過去の stray NetworkProjectConfig 事故＝重複 config で weave 対象が外れる、に注意。本物は `Assets/Level/Photon`。）
2. 検証シーン（`MapNetworkSandbox` 等）に `MapTreasureSpawner` を**シーン配置 NetworkObject** として置き、
   `_treasurePrefab` に上記 prefab を割当。`MapBuilder` / `MapNetworkDistributor` と同シーンであること。
3. Host 起動 → 地形ビルド後に宝が出るか、Client でも同じ位置に複製されるかを目視（ログ `[MapTreasureSpawner] 宝スポーン完了`）。
4. OK なら develop へ統合可（feature ブランチの間は WIP 可）。

## 7. 自力再実装チェックリスト
- [ ] なぜ宝は「ホスト Spawn＋自動レプリケーション」で、地形は「manifest ローカル再生成」なのか説明できる
      （NetworkObject か否か／重さ／決定論の都合）
- [ ] なぜプランはホスト 1 箇所でだけ消費すれば足りるか（権威 Spawn が配布を担う）を言える
- [ ] 物理 Spawn を `FixedUpdateNetwork` で行い `Render` で行わない理由を説明できる
- [ ] `Layout != null` ＋ `_spawned` ガードで「1 回だけ・地形確定後」を保証する仕組みを言える
- [ ] `Value` を今 `Treasure` に持たせない判断（未使用 networked 状態を避ける）を説明できる
