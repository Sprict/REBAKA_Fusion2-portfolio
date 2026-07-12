# タスク: クライアント側ラグドールの動きを柔らかくする

## 問題

ホスト側のプレイヤーはフル物理シミュレーションが動いているため、ラグドール特有の「揺れ」「慣性」「二次運動」がある。
クライアント側はkinematic+PID補正で位置を直接追従させているため、動きが「固い」「ロボット的」に見える。

## 制約

- **Forecast Physicsは使わない**: A/Bテスト済みで、テレポート・ラバーバンディングが多発して不合格（devlog参照）
- **同期精度を犠牲にしない**: 現行のSyncMetricsRecorder M1-M7基準を維持（クライアント6/6 PASSED）
- **既存アーキテクチャを壊さない**: Hybrid/Legacy/Forecast の3戦略パターンを維持

## 現在のアーキテクチャ

### クライアント側の物理パス（Hybridモード）

```
RagdollClientProxyRuntime.RunFixedUpdate()
  ├── EnsureCorrection() → ClientProxyCorrection初期化
  ├── UpdateVisualProxyPhysics() → 脚アニメのみ（UpdatePhysicsVisualOnly）
  └── ApplyCorrection(dt) → ルート位置補正
```

### クライアントのRigidbody状態
- **APR_Root (bodyRigidbodies[0])**: `isKinematic=true`, `useGravity=false`
  - `MovePosition` / `MoveRotation` でネットワーク位置に追従
- **その他パーツ（Body, Head, Arms, Legs）**: `isKinematic=false`, `useGravity=false`
  - ジョイントドライブは維持されている（HybridStrategy）
  - しかし物理力が適用されない（AddForce等なし）
  - 結果: ジョイントのバネ力のみで動く → 固い

### ホスト側で実行されてクライアントで実行されないもの

以下は`HasAuthoritativePhysics()`ガード（`RagDollPhysics.cs:1025`）で制限:
- `ApplyMovementForce`（ルートのlinearVelocityを直接設定、`RagDollPhysics.cs:614`）
- `AddFeetDownForce`（足に下向きImpulse、`RagDollPhysics.cs:1033`）
- `ProcessJumpingPhysics`（ジャンプ時のlinearVelocity設定、`RagDollPhysics.cs:890`）

以下は`UpdatePhysicsVisualOnly`で意図的にスキップ:
- `UpdateRootRotation`（ルートジョイントのtargetRotation変更、spring=5000で巨大トルク発生→ラバーバンディングの原因）
- `UpdateStateBlending` / `ApplyBlendedJointDrives`（状態遷移時のジョイントブレンド）
- `ApplyMovementForce`（Walking時のルート移動力）

**なぜスキップするのか（`RagDollPhysics.cs:870-877`のコメント参照）:**
ルートジョイントのspring=5000でtargetRotationを変更すると巨大トルクが発生。
クライアントは重力OFF/接地なしなのでルートが大きく動く → プロキシ補正が引き戻す → ラバーバンディング。
脚ジョイント(spring=250)は1/20なので反作用が小さく安全。

### ネットワーク同期されているデータ（IProxyPoseSource）
- Root: position, rotation, linearVelocity, angularVelocity
- Head: position, rotation
- LeftHand/RightHand: position, rotation
- PlayerState, MoveDirection, FacingDirection, LookDirection

## 関連ファイル

| ファイル | 役割 |
|---|---|
| `Assets/Code/Scripts/Player/ClientProxyCorrection.cs` | ルート/四肢のPID補正 |
| `Assets/Code/Scripts/Player/RagdollClientProxyRuntime.cs` | クライアント物理パスのオーケストレーション |
| `Assets/Code/Scripts/Player/RagDollPhysics.cs` | 物理演算（ホスト用フル + クライアント用VisualOnly）|
| `Assets/Code/Scripts/Player/RagDollController.cs:28` | `useHybridProxySimulation=true`（デフォルト） |
| `Assets/Code/Scripts/Player/HybridClientProxyModeStrategy.cs` | ジョイントドライブ維持戦略 |
| `Assets/Code/Scripts/Player/RagdollProfile.cs` | 物理パラメータ（ScriptableObject） |
| `Assets/Code/Scripts/Player/RagdollControllerContracts.cs` | インターフェース定義 |

## 検討すべきアプローチ

### A. ネットワーク速度の注入（推奨度: 高）
Root以外のパーツに対して、ホスト側の速度データを反映する。
現在はRoot/Head/Handsの位置しか同期していないが、Rootの`linearVelocity`は同期されている。
- Rootの速度変化に応じて、他パーツに慣性力（`AddForce`）を加える
- パーツは非kinematicなので物理力に反応できる
- 強度パラメータをRagdollProfileに追加してInspectorで調整可能に

### B. ジョイントドライブの柔軟化（推奨度: 中）
クライアント側のジョイントドライブのspring/damperを弱くして、より「揺れる」ようにする。
- 現在HybridStrategyはドライブをそのまま維持している
- クライアント用に弱いドライブ値を適用すれば自然な揺れが出る
- ただし弱すぎるとラグドールが崩壊する

### C. 二次運動ノイズの追加（推奨度: 低）
Perlinノイズ等で人工的な揺れを追加する。
- 実装は簡単だが、物理ベースではないので不自然になりやすい
- 最終手段

### D. 重力の部分復活（推奨度: 中）
クライアント側パーツのuseGravityをtrueにし、弱い重力（Physics.gravity * 0.3f等）相当の力を加える。
- 自然な垂れ下がり・揺れが出る
- ただしルートがkinematicで固定されているので、ぶら下がるだけになる可能性

## 評価基準

修正後に以下を確認:
1. **見た目**: ホスト側とクライアント側の動きの差が小さくなったか（主観評価）
2. **SyncMetrics M1-M7**: クライアント6/6 PASSEDを維持しているか
3. **ClientSideDiagnostic G1-G6**: G2-G6がPASSを維持しているか（G1振動は現状FAILで許容）
4. **安定性**: 30秒間のプレイで物理崩壊が発生しないか

## 重要な注意事項

- **Fusion Nested NetworkObjectの罠**: APR_Rootはランタイムでシーンルートに分離される。`GetComponentsInChildren`ではなく`BodyRigidbodies`プロパティを使うこと。
- **UpdatePhysicsVisualOnlyのコメントを読む**: ルート回転の直接変更を避けた理由が書いてある（spring=5000のトルク→ラバーバンディング）
- **テストはParrelSyncで2インスタンス**: ホスト+クライアントで30秒計測
- **ApplyMovementForceの仕組み**: ホスト側ではルートのlinearVelocityを`Vector3.Lerp(current, target, 0.8f)`で直接設定している。クライアントではルートがkinematicなのでlinearVelocity設定は無効。

## A/Bテスト結果の要約（Forecast Physics vs 現行方式）

Forecast Physicsでフル物理をクライアントに走らせた結果:

- テレポート5回発生（最大4.953m）、位置追従avg=0.113m（現行0.000m）、回転追従avg=5.4deg（現行0.3deg）
- 全パーツにNetworkRigidbodyを追加するとさらに悪化（ジョイント干渉で振動57回/30秒）
- **結論**: ラグドール物理はカオス系で予測不可能。フル物理の復活は不可。部分的・装飾的な力の追加が必要。

詳細: `Docs/devlogs/2026-03-27_syncmetrics_baseline_measurement.md`

---

## Codex CLI プロンプト

以下をCodex CLIにコピー&ペーストして実行する。

```text
Docs/codex_tasks/client_ragdoll_softness.md を読んでから作業を開始してください。

## タスク

クライアント側ラグドールの動きを柔らかくする設計と実装を行う。

## 背景

現在、クライアント側プレイヤーはkinematicルート+PID補正で位置追従しており、
ホスト側と比べて動きが「固い」。ラグドール特有の揺れ・慣性・二次運動がない。

ボディパーツ（Head, Body, Arms, Legs）はisKinematic=false, useGravity=falseで、
ジョイントドライブは維持されているが、物理力（AddForce等）が一切適用されていない。
そのためジョイントのバネ力だけで動き、固くロボット的に見える。

## 制約（厳守）

1. Forecast Physicsは使わない（A/Bテストで不合格確認済み）
2. SyncMetrics M1-M7のクライアント6/6 PASSEDを維持すること
3. 既存の3戦略パターン（Hybrid/Legacy/Forecast）を壊さない
4. ルートジョイント（spring=5000）のtargetRotationは変更しない（ラバーバンディングの原因）
5. 新パラメータはRagdollProfileに追加してInspector調整可能にする

## 推奨アプローチ（優先順）

### A. 慣性力の注入（最優先で検討）
- ルートの速度変化（加速度）を検出し、ボディパーツに逆向きの慣性力をAddForceで加える
- ルートのNetRootLinearVelocityは同期されているので利用可能
- 例: ルートが急に止まる → 上半身が前に揺れる（慣性）
- 強度パラメータ（proxyInertiaForceScale等）をRagdollProfileに追加
- 実装場所: ClientProxyCorrection.ApplyCorrection() 内、またはRagdollClientProxyRuntime.UpdateVisualProxyPhysics() 後

### B. ジョイントドライブの柔軟化（Aと組み合わせ可）
- HybridClientProxyModeStrategy.Apply() でクライアント用に弱いドライブ値を適用
- または RagdollProfile にクライアント用ドライブ倍率を追加
- 弱すぎるとラグドール崩壊するので0.5〜0.8倍程度から試す

### C. 微小重力の復活（Aと組み合わせ可）
- ボディパーツにPhysics.gravity * scaleFactor相当のAddForceを毎フレーム適用
- 自然な垂れ下がりが出るが、ルートがkinematicなので過度にならないよう調整

### D. Perlinノイズ（最終手段）
- 物理ベースで不十分な場合のみ検討

## 実装ステップ

1. まず関連ファイルを全て読む（特にclient_ragdoll_softness.mdの「関連ファイル」表）
2. RagdollProfile.cs にクライアント柔軟化パラメータを追加
3. アプローチAの慣性力注入を実装（新メソッドとして分離）
4. 必要に応じてアプローチB/Cを追加
5. 実装後、変更内容の説明をコメントに残す

## 評価（手動テスト）

- ParrelSync 2インスタンスで30秒プレイ
- ホストとクライアントの動きの差が縮まったか目視確認
- SyncMetricsRecorder で M1-M7 が全PASS維持か確認
```
