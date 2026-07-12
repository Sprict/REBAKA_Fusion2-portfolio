# ネットワーク責務分離リファクタリング

**日付:** 2026-02-09
**種別:** refactor
**関連ファイル:** SessionManager.cs, PlayerSpawner.cs, InputCollector.cs, SpawnPointManager.cs, MyRespawn.cs

## 問題

`GameLauncher.cs` と `AprInputBehaviour.cs` の2つのクラスが、以下の責務を重複して持っていた:

- セッション管理（StartGame, Shutdown）
- プレイヤーのスポーン/デスポーン（OnPlayerJoined/Left）
- 入力収集（OnInput）
- UI（Host/Joinボタン）

加えて、`MyRespawn.cs` がリスポーン位置をハードコード（`Vector3(10, 100, 10)`）しており、スポーンポイント管理と連携していなかった。

## なぜこのアプローチか

### Single Responsibility Principle（SRP）に基づく4分割

| クラス | 責務 |
|---|---|
| **SessionManager** | セッションの開始/終了/接続状態管理 + 暫定UI |
| **PlayerSpawner** | プレイヤーの Spawn/Despawn + SpawnPointManager 連携 |
| **InputCollector** | ローカル入力の収集と NetworkInputData への変換 |
| **SpawnPointManager** | スポーン位置の一元管理（スロットベース） |

### 不採用にした代替案

1. **2分割（SessionManager + PlayerInputHandler）**: スポーン管理と入力収集が1クラスに残り、依然として責務過多
2. **既存ファイルの修正のみ**: GameLauncher に集約すると AprInputBehaviour の InputSystem 連携が失われる。逆も同様
3. **Zenject等のDIフレームワーク導入**: プロジェクト規模に対してオーバーエンジニアリング

### SpawnPointManager を独立させた理由

- 初期スポーンとリスポーンで「位置を決める」同じ判断が必要
- 将来の切断復帰機能に備えて「スロット予約」の概念を導入
  - `_slotToPlayerId[]`: どのスロットにどのプレイヤーが割り当てられているか
  - `_slotOccupied[]`: そのスロットが現在使用中か
  - 切断時: `_slotOccupied = false` だが `_slotToPlayerId` は保持 → 復帰時に同じスロットを再割り当て

## 仕組みの説明

### Fusion の INetworkRunnerCallbacks 分割

Fusion では `runner.AddCallbacks(this)` で複数のクラスをコールバックリスナーとして登録できる。
SessionManager, PlayerSpawner, InputCollector の3つが同じ NetworkRunner GameObject に配置され、それぞれ自分の責務に関連するコールバックのみを実装する。

### スポーンフロー

```
OnPlayerJoined (PlayerSpawner)
  → SpawnPointManager.AssignSpawnPoint(player)
    → 復帰チェック（_slotToPlayerId で既存スロット検索）
    → 新規割り当て（空きスロット検索）
    → フォールバック（デフォルト位置）
  → runner.Spawn(prefab, position, ...)
```

### リスポーンフロー

```
OnTriggerEnter (MyRespawn)
  → SpawnPointManager.GetRespawnPosition(player)
  → rootRigidbody.position = respawnPosition
```

## Unity Editor での設定手順

1. NetworkRunner GameObject に SessionManager, PlayerSpawner, InputCollector を追加
2. シーンに空の GameObject を作成し SpawnPointManager を追加
3. SpawnPointManager の spawnPoints にシーン上の Transform を4つ設定
4. PlayerSpawner の playerPrefab に APR_Root プレハブを設定
5. 旧コンポーネント（GameLauncher, AprInputBehaviour）を削除

## 想定される質問

**Q: なぜネットワークコールバックを複数クラスに分けたのですか？**

A: Fusion の INetworkRunnerCallbacks は巨大なインターフェースで、全コールバックを1クラスに実装すると「セッション管理」「スポーン」「入力」の異なる関心事が混在します。Fusion は AddCallbacks() で複数リスナーを登録できるので、責務ごとにクラスを分離しました。これにより各クラスが独立してテスト・修正できます。

**Q: SpawnPointManager のスロット予約はどう動きますか？**

A: プレイヤー退出時に `_slotOccupied` は false にしますが `_slotToPlayerId` は保持します。復帰時に同じ PlayerId で参加すると、AssignSpawnPoint() の最初のループで以前のスロットが見つかり、同じ位置にスポーンされます。完全退出の場合は `preserveSlot: false` で呼ぶことでスロットを完全解放できます。
