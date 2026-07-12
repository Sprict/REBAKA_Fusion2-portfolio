# REBAKA_Fusion2 アーキテクチャの主要 Failure Modes

> 最終更新: 2026-03-09
> 目的: 「このアプリはどこで壊れやすいか」を、実装証拠ベースでいつでも参照できる形に残す

---

## 1. このメモの前提

この整理は、以下のリポジトリ証拠をもとにまとめた。

- `Assets/Code/Scripts/Network/SessionManager.cs`
- `Assets/Code/Scripts/Network/PlayerSpawner.cs`
- `Assets/Code/Scripts/Network/InputCollector.cs`
- `Assets/Code/Scripts/Player/RagDollController.cs`
- `Assets/Code/Scripts/Player/RagdollHandContact.cs`
- `Assets/Resources/NetworkProjectConfigBackup.json`
- `Assets/Level/Scenes/Main_Backup.unity`
- `Assets/Level/Prefabs/newAPRPlayer.prefab`
- `Assets/Level/Prefabs/APR_Root.prefab`

---

## 2. 確認できた構成

### 確認済み

- Unity + Photon Fusion ベースのセッション型マルチプレイ
- `SessionManager` が `NetworkRunner` の起動、シーン切替、Fusion config 読み込みを担当
- `PlayerSpawner` が参加/退出に応じたスポーンと Despawn を担当
- `InputCollector` がローカル入力を `NetworkInputData` に変換して送信
- `RagdollController` が authority 側物理、proxy 側補正、診断ログを束ねる
- `RagdollHandContact` が grab/release を RPC で State Authority に委譲
- `NetworkProjectConfigBackup.json` では `PhysicsForecast: false`
- `NetworkProjectConfigBackup.json` では `EnableEncryption: false`
- `NetworkProjectConfigBackup.json` では `HostMigration.EnableAutoUpdate: false`
- アプリケーションコード上、DB・キュー・永続バックエンドは見当たらない

### 推測

- 実運用時も Photon Cloud 依存で、dedicated server はまだ未導入の可能性が高い
- セッション状態はほぼインメモリで、永続化より「試合中状態の維持」が重要

---

## 3. 主要 Failure Modes

### 3.1 ホスト障害や Photon 障害で試合全体が消える

**Failure mode**
ホスト離脱や Photon 側の接続障害で、試合全体が継続不能になる。

**Trigger**

- ホストのクラッシュ、終了、回線断
- Photon 接続失敗や一時障害
- ルーム参加/作成時の接続不良

**Symptoms**

- 全員切断される
- ルームが消える
- 進行中のゲーム状態が失われる

**Detection**

- `OnDisconnectedFromServer`
- `OnConnectFailed`
- `OnShutdown`
- Photon ダッシュボードや接続失敗率の監視

**Mitigation**

- Host Migration を実装する
- もしくは dedicated server 化する
- 再接続・再参加フローを用意する
- 最低限のセッション復元情報を持てるようにする

---

### 3.2 authority 側と proxy 側の物理同期が競合して見た目が破綻する

**Failure mode**
ラグドールの同期方式が混在し、authority の正解状態と proxy の再現がズレる。

**Trigger**

- 高遅延や再シミュレーション
- `DetachRootFromParent()` による階層変更
- 独自の `[Networked]` スナップショット補正
- prefab 側の `NetworkRigidbody` 残存

**Symptoms**

- ガタつき、ラバーバンド
- 手足・頭・手先のズレ
- 床すり抜け
- grab 状態の見え方不整合

**Detection**

- `RagdollNetDiagnostics`
- `RagdollCsvProfiler`
- proxy 側の non-kinematic カウント
- root error や補正量の増加

**Mitigation**

- 同じ部位に複数の同期責務を持たせない
- authority 側だけで物理を確定する方針を徹底する
- `DetachRootFromParent()` と競合する `NetworkRigidbody` 構成を見直す
- 本当に同期が必要な部位だけに絞る

**根拠メモ**

- `RagdollController` は root/head/hands の独自 `[Networked]` スナップショットを持つ
- `RagdollRigSetup` には root の `NetworkRigidbody` 検出処理がある
- `newAPRPlayer.prefab` に `NetworkRigidbody` が複数残っている
- `APR_Root.prefab` の `NetworkRigidbody` は `SyncParent: 1` で、親切り離しと相性が悪い

---

### 3.3 Runner と Spawn のライフサイクル競合で幽霊オブジェクトや再接続不整合が出る

**Failure mode**
セッション開始、Runner 再生成、シーン切替、Despawn 後片付けが衝突し、状態が壊れる。

**Trigger**

- Host/Join の連打
- シーン自動切替中の起動
- `DestroyImmediate` による Runner 差し替え
- 切断直後の再参加
- detached root の手動破棄漏れ

**Symptoms**

- 二重スポーン
- 入力不能
- 孤立した Rigidbody や GameObject
- 再接続後に不正な出現位置やゴーストが残る

**Detection**

- start/shutdown の重複ログ
- stale cleanup 警告
- `_spawnedCharacters` と `ActivePlayers` のズレ
- シーン上の player/runner 実体数の目視確認

**Mitigation**

- Runner の状態遷移を明示的なステートマシンに寄せる
- spawn/despawn を冪等化する
- reconnect 専用パスを分ける
- 高速再接続とシーン切替を自動テストする

---

### 3.4 多人数 ragdoll 負荷でホストの CPU と帯域が飽和し、遅延が急増する

**Failure mode**
ragdoll 物理と同期負荷がホストに集中し、先に体感品質が崩れる。

**Trigger**

- 複数プレイヤーが同時に衝突・掴み・補正を行う
- 低スペックホスト
- 不安定な回線
- 同期対象が多いまま人数が増える

**Symptoms**

- tick 低下
- 入力遅延
- `TryGetInput` 欠落
- 遠隔プレイヤーのカクつき
- 先に「遊べない」状態になり、その後切断に至る

**Detection**

- Fusion Statistics
- Runner の tick rate / delta time
- ホスト FPS
- 帯域使用量
- missing input ログ

**Mitigation**

- 同期対象パーツを減らす
- 重要度ごとに更新頻度を落とす
- AOI/優先度制御を実際に効かせる
- 4人同時の最悪ケースで先に負荷試験する
- 必要なら dedicated host を検討する

---

### 3.5 セッション保護が弱く、意図しない参加や盗聴耐性不足がある

**Failure mode**
公開・共有ビルド時に、セッションが予期しない参加者や観測に弱い。

**Trigger**

- `defaultSessionName = "TestRoom"` のような推測しやすい room 名
- custom auth 未実装
- connect/auth callback が実質空
- `EnableEncryption: false`

**Symptoms**

- テストルームへの想定外参加
- griefing
- 公開環境でのセッション露出

**Detection**

- 想定外の join ログ
- ルーム利用数の異常
- ビルド version ごとの接続傾向監視

**Mitigation**

- 通信暗号化を有効にする
- ランダム room code を使う
- custom auth / admission check を追加する
- Photon App 設定を公開用と開発用で分離する

---

## 4. 補足

- このプロジェクトでは、永続 DB が見当たらないため、一般的な Web アプリの「データ破損」よりも「ライブセッション消失」と「同期破綻」が支配的なリスクになる
- 今後 dedicated server、matchmaking backend、save data を追加した場合は、このメモを更新し、failure mode を再整理する

