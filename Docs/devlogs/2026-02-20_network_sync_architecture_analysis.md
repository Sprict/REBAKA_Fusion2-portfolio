# ネットワーク同期アーキテクチャ分析

**日付**: 2026-02-20
**カテゴリ**: アーキテクチャ設計
**ステータス**: 分析完了、実装は保留（安定動作優先のため後日検討）

---

## 問題提起

武器や動くアイテム（各々にNetworkRigidbody）を今後多数追加予定。
プレイヤーの帯域を節約するため、以下を検討した:

- 現行: 全15パーツにNetworkRigidbody（NRB）
- 提案: 「連鎖元（ソース）パーツ」のみ同期し、派生パーツはローカル物理に任せる

---

## 物理チェーン依存分析

`RagDollPhysics.cs` のジョイント階層と力の適用パターンから、各パーツを分類:

### ソース（状態が外部入力に直接依存）

| パーツ | Index | 理由 |
|--------|-------|------|
| APR_Root | 0 | ワールド接続、移動入力・回転入力が直接作用。唯一の真のソース |

MoveDirection, FacingDirection, CurrentState は既に `[Networked]` で同期済み。

### 派生（ジョイント制約 + ドライブで復元可能）

| パーツ | Index | 復元信頼度 | 根拠 |
|--------|-------|-----------|------|
| Body | 1 | 高 | Root→Body ジョイント + coreStiffnessドライブ |
| Head | 2 | 高 | Body→Head poseOnドライブ |
| 上腕(左右) | 3, 5 | 中 | パンチのtargetRotation変更あり。パンチ状態が未同期 |
| 下腕(左右) | 4, 6 | 中 | パンチインパルスが直接適用される部位 |
| 上脚(左右) | 7, 9 | 高 | 歩行サイクルはMoveDirection（同期済み）から算出 |
| 下脚(左右) | 8, 10 | 高 | 上脚に連動 |
| 足(左右) | 11, 12 | 高 | 下脚に連動 + FeetMountForce |

### 装飾（同期不要）

| パーツ | Index | 根拠 |
|--------|-------|------|
| 手(左右) | 13, 14 | LowerArmにLocked constraintで剛結合。自由度ゼロ |
| 飾りSphere等 | — | ゲームプレイに影響なし |

---

## 同期方式の比較

### 方式A: 全パーツNRB（現行）

全15パーツにNetworkRigidbody3Dをアタッチ。

### 方式B: 状態マシン + Root NRBのみ

Root NRBのみ残し、`[Networked]` 状態プロパティでクライアント側のローカル物理を駆動。

### 方式C: Adaptive Hybrid

プレイヤー状態に応じて同期の深さを変える折衷案。

### シナリオ別ビジュアル品質テスト

| シナリオ | 方式A | 方式B | 方式C |
|---------|-------|-------|-------|
| 通常歩行 | 完全一致 | 完全一致 | 完全一致 |
| 壁衝突 | 完全一致 | 許容範囲（四肢の折れ方が異なる） | 許容範囲 |
| **パンチ被弾** | 完全一致 | **致命的** — 衝撃方向が未伝達 | 衝撃ベクトル同期で解決 |
| **KO/脱力** | 完全一致 | **致命的** — 四肢が各クライアントで別姿勢 | 全パーツスナップショットで解決 |
| **オブジェクト掴み** | 完全一致 | **致命的** — 手とオブジェクトの位置不一致 | 手NRB or 手位置同期で解決 |
| 落下物圧壊 | 完全一致 | 許容範囲 | 許容範囲 |

**結論: 純粋な方式Bはコアメカニクス3つで破綻。方式Cが現実的な最適化パス。**

---

## 帯域推定（4プレイヤー + 将来の20アイテム）

| 構成 | 帯域/tick（最悪） | デルタ圧縮後 [※推測] |
|------|------------------|----------------------|
| 方式A: 15 NRB/player + 20 items | 3,520 B | 1,200-1,800 B |
| 方式B: 1 NRB/player + state + 20 items | 1,640 B | 1,000-1,400 B |
| 方式C: Adaptive | 状態依存 | 800-1,200 B |

4人対戦では方式Aでも問題なし。8人以上に拡張する場合に方式Cが有効。

---

## 重要な発見: ハイブリッドモードとNRBの関係

### 現行のハイブリッドプロキシモードの動作

1. `EnsureClientProxyBootstrap()` で全15 RBを **dynamic** に設定
2. `ClientRagdollKinematicGuard.BeforeTick()` はハイブリッドモードで **スキップ**
3. `UpdateClientVisualProxyPhysics()` がFusion tick毎（~60Hz）にローカル物理を実行
4. クライアント側のプレイヤーは **NRBのスナップショット頻度ではなく、ローカル物理の60Hzで動いている**

### NRBが実質無効化されている可能性

ハイブリッドモードで全RBがdynamicに設定されるため:
- [※推測] NRBはdynamic RBに対してtransformを書き込まない
- もし書き込んでいたら、ローカル物理との競合で既に振動が発生しているはず
- **15パーツのNRBは帯域を消費しているだけで、ハイブリッドモードでは実質的に寄与していない可能性がある**

### 「連鎖元だけ同期」の妥当性

ハイブリッドモードの文脈では:
- ローカル物理が60Hzで全パーツを駆動 → NRBの位置データは使われていない
- Root NRB + `[Networked]` 状態プロパティだけで60Hzスムーズな動きが実現できる
- [※未確認] Fusion 2のNRB3Dがdynamic RBに対してどう振る舞うか要確認

---

## 同期頻度・補間・混在問題の検証

### 懸念1: NRBの~20Hz同期でカクカクする

**ハイブリッドモードでは問題なし。** ローカル物理が60Hzで動いているため、NRBの受信頻度は直接影響しない。非ハイブリッドモード（全RBがkinematic）の場合のみ、NRB補間の品質に依存する。

### 懸念2: 補間によるモデル変形・振動

**ハイブリッドモードでは問題なし。** ローカルのConfigurableJointが接続を維持するため、パーツ間の伸び・分離は発生しない。非ハイブリッドモードでは、各パーツが独立に線形補間されるためジョイント制約が無視される可能性あり。

### 懸念3: NRB同期パーツとローカル物理パーツの混在

**要検証だが、現状問題は発生していない。** ハイブリッドモードで全RBがdynamicのため、NRBが実質無効化されている可能性が高い。

| 懸念 | ハイブリッドモード | 非ハイブリッドモード |
|------|-------------------|---------------------|
| 10Hz同期でカクカク | 問題なし | NRB補間の品質に依存 |
| 補間で変形・振動 | 問題なし | 可能性あり |
| NRB + ローカル混在 | 要検証 | 該当なし |

---

## Adaptive Hybrid方式の設計（方式C）

### 状態別同期戦略

| 状態 | 同期方式 | 帯域/player/tick |
|------|---------|-----------------|
| Walking/Idle | Root NRB + [Networked]状態のみ | ~102 B |
| Punching | + 衝撃ベクトル同期（被弾側） | ~118 B |
| KO/Ragdoll | + 全パーツスナップショット | ~522 B |
| Grabbing | + 手NRB or 手位置同期 | ~190 B |

### 必要な追加 [Networked] プロパティ

```csharp
// パンチ状態（腕のローカル物理を正確にする）
[Networked] private NetworkBool IsPunchingRight { get; set; }
[Networked] private NetworkBool IsPunchingLeft { get; set; }

// 衝撃同期（パンチ被弾をクライアントで再現）
[Networked] private Vector3 LastImpactVector { get; set; }
[Networked] private float LastImpactForce { get; set; }
[Networked] private int ImpactSequenceId { get; set; }

// KO時の全パーツスナップショット
[Networked, Capacity(15)] private NetworkArray<Vector3> LimbPositions { get; }
[Networked, Capacity(15)] private NetworkArray<Quaternion> LimbRotations { get; }
[Networked] private NetworkBool SyncFullPose { get; set; }
```

### 変更箇所

1. `RagdollController.cs`: ホスト側 `FixedUpdateNetwork()` で状態に応じたスナップショット書き込み
2. `RagdollController.cs`: クライアント側 `UpdateClientVisualProxyPhysics()` で衝撃ベクトル適用 + KO時Lerp
3. `RagdollPhysics.cs`: `BuildProxyCommandFromNetworkState()` で左右パンチ個別反映
4. `RagdollImpactContact.cs`: 衝撃発生時にControllerへImpactVector通知
5. プレハブ: 14パーツからNRB除去（Root NRBのみ残す）

---

## 推奨タイムライン

| 時期 | アクション |
|------|----------|
| 今（安定性優先） | 現行の全15パーツNRBを維持。安定動作優先 |
| 後日 | Fusion Statisticsで実際の帯域を計測 |
| 帯域問題が顕在化したら | Adaptive Hybrid方式に段階的移行 |
| 移行前に試すこと | InterestManagement（AOI）で遠いプレイヤーの更新頻度を下げる |

### 低リスク段階的最適化（今すぐやる場合）

1. 手（13, 14）のNRB除去 — Locked constraintでドリフトゼロ
2. 足（11, 12）のNRB除去 — 下脚に追従するだけ
3. ここで計測 → 4パーツ削減（15→11 NRB/player、27%削減）

---

## 分析のまとめ

1. **物理チェーンの依存分析**でソース vs 派生パーツを分類し、最適化パスを把握
2. **シナリオ分析**で状態マシン方式がパンチ・KO・掴みで破綻することを特定
3. **Adaptive Hybrid方式**を設計し、帯域70%削減の見込みを算出
4. [※理論] PhysXの非決定性により、KO状態（弱いドライブ）ではクライアント間で四肢の姿勢が再現不可能
5. 安定性を優先し、**段階的移行パスを計画**
6. ハイブリッドプロキシモードでNRBが実質無効化されている可能性を発見 → 帯域最適化の余地

---

## 自力再実装チェックリスト

- [ ] 15パーツのジョイント階層を理解し、ソース/派生を分類できる
- [ ] PhysicsForecast OFF でNRBがプロキシRBをどう制御するか説明できる
- [ ] ハイブリッドプロキシモードの動作フロー（bootstrap→ローカル物理→補正）を説明できる
- [ ] 状態マシン方式がKO/パンチ/掴みで破綻する理由を説明できる
- [ ] Adaptive Hybrid方式の状態別同期戦略を設計できる
- [ ] 帯域推定の計算（NRB1個あたり~44B、デルタ圧縮の効果）ができる
- [ ] [※理論] PhysXの非決定性がマルチプレイヤー物理同期に与える影響を説明できる
