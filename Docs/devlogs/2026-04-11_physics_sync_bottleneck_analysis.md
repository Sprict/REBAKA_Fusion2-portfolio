# 物理同期ボトルネック分析

**日付**: 2026-04-11  
**目的**: アクティブラグドールの物理同期における性能・品質のボトルネックを把握する  
**スコープ**: 分析のみ（実装変更は別タスク）

---

## 背景

現行の物理同期方式（kinematic + PID補正）は A/B テストでForecast Physicsを上回ることが実証済み（2026-03-27 devlog）。  
クライアント側は 6/6 PASSED、ホスト側は M2（Y振動）のみ FAIL。  
この分析は「どこが性能・品質のボトルネックになっているか」を把握するためのもの。

---

## 現行アーキテクチャの処理フロー

```
FixedUpdateNetwork() ごとの処理フロー

【Host (StateAuthority)】
  RagdollHostSimulationOrchestrator.RunFixedUpdate()
    ↓
  RagDollPhysics.UpdatePhysics()
    ├─ IsGrounded()              ← Physics.Raycast × 1
    ├─ CalculateBalanceState()   ← COM計算
    ├─ ApplyBlendedJointDrives() ← ConfigurableJoint 書き込み × 20
    ├─ UpdateRootRotation()      ← targetRotation 設定
    └─ ProcessWalking() / ApplyMovementForce() など
    ↓
  RagdollProxyPosePublisher.Publish()
    ↓ （毎 tick 必ず）
  [Networked] 10プロパティ書き込み
  (NetRootPos/Rot/Vel/AngVel, NetHead×2, NetLHand×2, NetRHand×2)

【Client (non-StateAuthority) — Hybridモード】
  BeforeTick(): kinematic guard スキップ
    ↓
  RagdollClientProxyRuntime.RunFixedUpdate()
    ├─ ClientProxyCorrection.EnsureBootstrap()
    ├─ UpdateVisualProxyPhysics()
    │    └─ RagDollPhysics.UpdatePhysicsVisualOnly()
    │         └─ ProcessWalking() のみ（脚 targetRotation）
    └─ ClientProxyCorrection.ApplyCorrection()
         ├─ root: MoveTowards + Slerp（毎 tick）
         ├─ head/hands: Lerp（proxyCorrectHeadAndHands=true 時）
         ├─ HardSnapProxyPose()  ← 閾値超過持続時に瞬間テレポート
         └─ ApplySecondaryMotion() ← 全 non-kinematic RB に AddForce
```

---

## 特定されたボトルネック（優先順位順）

### BN-1: ホスト側 Y 軸振動（最重要・計測済み）

- **場所**: `RagDollPhysics.cs` — ConfigurableJoint balanceOn ドライブ
- **症状**: M2 FAIL、Y振幅 0.269m（静止時）。ホストの APR_Root が上下に揺れる
- **原因**: `balanceStrength=5000, balanceDamperRatio=0.1` → `balanceDamper=500`  
  [※理論] 臨界減衰には damper ≈ 2√(spring × mass) が必要。トルソー質量を考慮すると 500 は不足（underdamped）
- **データ**: `RagdollProfile.cs:24-27`、`RagDollPhysics.cs:258-264`
- **対処候補**: `balanceDamperRatio` を 0.1 → 0.15〜0.2 に上げて計測（1行変更）

---

### BN-2: 毎 tick の ConfigurableJoint 書き込み（CPU ホットパス）

- **場所**: `RagDollPhysics.cs:548-579` — `ApplyBlendedJointDrives()`
- **症状**: 毎 `FixedUpdateNetwork` で 10 ジョイント × 2軸 = 20 回の `joint.angularXDrive` / `angularYZDrive` 書き込み
- **原因**:
  1. `_currentPoseStiffnessMultiplier` が Lerp で毎 tick 変化 → 値が変わらなくても書き込む
  2. Unity ConfigurableJoint のプロパティ setter は PhysX へのブリッジ → プロパティ一致確認なし
- **計測なし**: CPU プロファイラ未取得。ジョイント数が多いため影響は無視できない
- **対処候補**: 前 tick の値とのイプシロン比較でスキップ

---

### BN-3: `IsGrounded()` の毎 tick Physics.Raycast

- **場所**: `RagDollPhysics.cs:1141-1148`
- **症状**: ホスト物理ループ内で毎 `FixedUpdateNetwork` に `Physics.Raycast` × 1 発火
- **コード**:
  ```csharp
  public bool IsGrounded()
  {
      Ray ray = new Ray(_bodyParts[IndexRoot].transform.position, Vector3.down);
      bool raycastHit = Physics.Raycast(ray, _context.BalanceHeight, _groundLayerMask);  // ← 毎tick
      LastRaycastHit = raycastHit;
      return raycastHit || _isAnyFootGrounded;
  }
  ```
- **注記**: `_isAnyFootGrounded` は `RagdollFootContact` コールバックで更新されており、接地時はレイキャスト不要  
  LayerMask はキャッシュ済み（文字列ルックアップは回避できている）
- **対処候補**: `_isAnyFootGrounded` が true の場合は Raycast をスキップ

---

### BN-4: ハードスナップによる位置ジャンプ（クライアント品質）

- **場所**: `ClientProxyCorrection.cs:445-484` — `HardSnapProxyPose()`
- **症状**: rootError > `proxyHardSnapRootThreshold(1.0m)` が `proxyHardSnapHoldSeconds(0.25s)` 継続すると即時テレポート
- **計測**: M1 maxDelta=0.683m（PASSだが閾値 1.0m まで余裕 0.317m しかない）  
  衝突・パンチ被弾時に閾値超過リスクがある（M7 は「サンプル不足」でまだ未計測）
- **構造的問題**: Root のみ補正で、体の他 13 パーツ（胴体・腕・脚・足）はネットワーク補正対象外  
  → Ragdoll 状態・パンチ被弾でホスト-クライアント間の四肢姿勢乖離が拡大
- **次のアクション**: 壁接触テストシナリオで M7 を計測

---

### BN-5: 全 non-kinematic RB への毎 tick AddForce（クライアント慣性）

- **場所**: `ClientProxyCorrection.cs:348-388` — `ApplySecondaryMotion()`
- **症状**: Hybrid モードでは Root 以外の ~13 RB が dynamic。毎 tick 全てに `rb.AddForce()` を呼ぶ
- **条件**: `proxyInertiaForceScale > 0`（デフォルト 0.35）かつ加速度あり
- **影響**: AddForce 自体は軽量だが、dynamic RB 13 本の Physics ソルバー負荷が加算される

---

### BN-6: パンチ/Ragdoll 状態での四肢ドリフト（未同期パーツ）

- **場所**: `[Networked]` プロパティ（`RagDollController.cs:119-128`）
- **現状**: Root・Head・LeftHand・RightHand の 4 transforms のみネットワーク同期  
  Body・UpperArm・LowerArm・UpperLeg・LowerLeg・Foot（計 11 パーツ）は非同期
- **問題**: Ragdoll 状態（ドライブ OFF）では PhysX の非決定論性により各クライアントで四肢姿勢が独立に発散  
  → パンチ被弾シーン（KO 演出）でホスト-クライアント間の見た目が大きく乖離する可能性
- **devlog 参照**: `2026-02-20_network_sync_architecture_analysis.md` — Adaptive Hybrid 方式の検討

---

### BN-7: NetProxyPosePublisher の無条件毎 tick 書き込み

- **場所**: `RagdollProxyPosePublisher.cs:30-71`
- **症状**: ホストが移動していなくても毎 tick 全 10 `[Networked]` プロパティに書き込む  
  Fusion はデルタ圧縮するが、書き込み自体は毎 tick 発生
- **帯域**: A/B テストでは 9.8 KB/s 一定（Forecast ON/OFF でも不変）  
  → 現状は問題なし。4 人以上 or 敵追加時に重要になる

---

## ボトルネック対比マップ

```
          難しい
            │
 BN-6      │    BN-1
(四肢ドリフト)  │  (Y振動)
            │
  ──────────┼────────── 重大度（右→大）
  軽微       │          重大
            │
 BN-7      │  BN-4  BN-2
 (無条件書込)  │ (ハードスナップ) (毎tick Joint書込)
            │   BN-3 BN-5
          簡単
```

---

## 計測で確認すべき未確認項目

| ID | 項目 | 手順 |
|----|------|------|
| M7 | 壁接触時のホスト-クライアントドリフト | 壁に向かって歩く 30 秒間 SyncMetrics を取得 |
| CPU | ConfigurableJoint 書き込みコスト | Unity Profiler の Physics.Simulate 内のコストを確認 |
| Ragdoll ドリフト | KO 状態の四肢乖離量 | ラグドール状態でスクリーンショット比較（ホスト/クライアント） |

---

## 対処優先候補（実装は別タスク）

| 優先度 | BN | 対処内容 | 難易度 | ファイル |
|--------|-----|---------|--------|---------|
| 1 | BN-1 | `balanceDamperRatio` 0.1→0.15〜0.2 で計測 | 低 | `RagdollProfile.cs:29` |
| 2 | BN-4 | M7 計測（壁接触テスト） | 低（計測のみ） | `SyncMetricsRecorder.cs` |
| 3 | BN-2 | 前 tick 値キャッシュでイプシロン比較スキップ | 中 | `RagDollPhysics.cs:548-579` |
| 4 | BN-3 | `_isAnyFootGrounded` true 時の Raycast スキップ | 低 | `RagDollPhysics.cs:1141-1148` |

---

## 参照ファイル

| ファイル | 役割 |
|---------|------|
| `Assets/Code/Scripts/Player/RagDollPhysics.cs` | 物理ループ本体、IsGrounded、ApplyBlendedJointDrives |
| `Assets/Code/Scripts/Player/ClientProxyCorrection.cs` | クライアント補正、HardSnap、ApplySecondaryMotion |
| `Assets/Code/Scripts/Player/RagdollProxyPosePublisher.cs` | ホスト→クライアントへの pose 同期 |
| `Assets/Code/Scripts/Player/RagdollProfile.cs` | パラメータ定義（balanceDamperRatio 等） |
| `Assets/Code/Scripts/Player/RagDollController.cs:109-128` | [Networked] プロパティ一覧 |
| `Assets/Code/Scripts/Diagnostics/SyncMetricsRecorder.cs` | M1〜M7 計測ツール |
| `Docs/devlogs/2026-03-27_syncmetrics_baseline_measurement.md` | ベースライン計測結果 |
| `Docs/devlogs/2026-02-20_network_sync_architecture_analysis.md` | 帯域・NRB分析 |
