# ラグドール物理ネットワーク同期 アーキテクチャ設計書

**日付**: 2026-02-16
**ステータス**: 設計提案（レビュー待ち）
**対象問題**: ピア側でプレイヤーが高速振動する（物理同期の不備）

---

## 1. 問題の要約

ピア（クライアント）側で全プレイヤーが高速に震え、残像が見えるほどのジッターが発生している。
根本原因は、ネットワーク物理同期の構成が不完全であること。

### 現在の構成の問題点（優先度順）

| # | 問題 | 影響 | 深刻度 |
|---|------|------|--------|
| 1 | `RunnerSimulatePhysics`がシーンに存在しない | Fusionが物理シミュレーションのタイミングを制御できない。Unity標準のFixedUpdateとFusionのティックが非同期で実行される | **Critical** |
| 2 | `PhysicsForecast: false`（無効） | クライアント側で物理の外挿・補間が行われない | **Critical** |
| 3 | 15個のRigidbodyのうちAPR_Rootの1個だけに`NetworkRigidbody`がある | 14個のボディパーツの位置/回転がネットワーク同期されない。ネットワーク補正とローカル物理の衝突で振動発生 | **Critical** |
| 4 | 全Rigidbodyの`Interpolation`が`None` | 物理ステップ間のビジュアル補間がなく、ガクガクした見た目になる | **High** |
| 5 | `RagDollPhysics.cs`で`Time.deltaTime`を使用 | `FixedUpdateNetwork()`から呼ばれる物理計算が、レンダリングフレーム時間を使っている。Fusionのティックレートと不一致 | **High** |
| 6 | `GetInput()`失敗時にProxy側で物理更新が行われない | `FixedUpdateNetwork()`で`GetInput()`が`false`を返すとreturnしてしまい、非入力権限者の物理が更新されない | **Medium** |

---

## 2. 既存タイトルの調査結果

### 2.1 Gang Beasts

- **ネットワーク**: Unity NGO + Steamworks P2P → 後にサーバーリレーに移行
- **物理同期方式**: サーバー権威型。ホストが物理シミュレーションを実行し、クライアントはビジュアル補間
- **ラグドール構成**: Rigidbody + ConfigurableJointベースのアクティブラグドール（APR方式と同系統）
- **同期対象**: 主要ボディパーツ（Root, Body, Head等）の位置・回転のみを同期。末端（指等）はローカル物理に委任
- **帯域削減**: 位置は半精度浮動小数点（half）で量子化、回転は最小3成分圧縮（Smallest Three）、更新頻度は20Hz程度に制限
- **既知問題**: ラグ時に掴み操作が同期しない、物理の非決定性により状態が徐々にずれる
- **教訓**: 完全な物理同期は目指さず「見た目が破綻しなければOK」の割り切りが重要

### 2.2 Party Animals

- **ネットワーク**: カスタムのサーバー権威型ネットコード
- **物理同期方式**: サーバーで物理実行 → クライアントはスナップショット補間
- **ラグドール構成**: 簡略化されたラグドール（ボディパーツ数を削減した専用ネットワーク用構造）
- **工夫**: ビジュアル用メッシュをネットワーク同期されたボーンターゲットにスキニングで追従させる。物理ボーンとビジュアルボーンを分離
- **帯域削減**: デルタ圧縮、優先度ベースの更新（遠いオブジェクトは低頻度）
- **アニメーション**: Procedural Animation（物理駆動）とState Machine Animationのブレンド

### 2.3 Human: Fall Flat

- **ネットワーク**: P2P（ホスト権威型）
- **物理同期方式**: ホストが全物理を実行、クライアントはビジュアル補間
- **教訓**: 物理は非決定的なので、同じ入力を送っても結果が一致しない。必ず権威側の結果を真とする

### 2.4 共通パターン

| アプローチ | 採用例 | メリット | デメリット |
|-----------|--------|---------|-----------|
| **サーバー権威 + クライアント補間** | Party Animals, Human:Fall Flat | 一貫性が高い、チート耐性 | 入力遅延、サーバー負荷 |
| **ホスト権威 + ビジュアル補間** | Gang Beasts | 実装が比較的シンプル | ホスト有利、非決定性 |
| **全クライアント物理 + Reconciliation** | FPS系（Fusionの得意分野） | 低遅延、レスポンシブ | ラグドールには非現実的 |

**業界のコンセンサス**:
> 物理ラグドールのネットワーク同期において、完全な予測+ロールバックは現実的でない（物理の非決定性＋状態量の多さ）。権威側で物理を実行し、非権威側はビジュアル補間するのが主流。

---

## 3. Photon Fusion Physics Addon の調査結果

### 3.1 Forecast Physics の仕組み

- **Forecast有効時**: 各クライアントがローカルで完全な物理シミュレーションを実行し、外挿（Extrapolation）で他プレイヤーのローカル時刻まで物理を進める。インタラクティブな物理（掴み、衝突等）が可能
- **Forecast無効時**: State Authority側のみが物理を実行。Proxy側のRigidbodyは**自動的にKinematicに設定**され、位置/回転のみが補間で同期される

### 3.2 NetworkRigidbody の動作

- **State Authority**: 物理シミュレーションを実行し、結果をネットワークに書き込む
- **Proxy（Forecast無効時）**: Rigidbodyが自動でKinematicになり、位置/回転がネットワークから補間で受け取る
- **Proxy（Forecast有効時）**: Rigidbodyがアクティブなまま、外挿結果を使ってローカル物理を実行

### 3.3 RunnerSimulatePhysics

- Fusionが物理シミュレーションのタイミングを制御するために**必須**のコンポーネント
- Unity側の`Physics.simulationMode`を`Script`に変更し、Fusionのティックに合わせて`Physics.Simulate()`を呼ぶ
- **これがないと**: Unity標準のFixedUpdateとFusionのティックが独立に走り、物理が二重実行されたりタイミングがずれたりする

### 3.4 ラグドール構成での考慮事項

| 構成 | 帯域コスト | 精度 | 複雑度 |
|------|-----------|------|--------|
| 全15パーツに`NetworkRigidbody` | 高（15 × 位置+回転+速度 ≈ 1.5KB/tick/player） | 最高 | 低（Fusionが自動同期） |
| Root + 主要5パーツのみ | 中（6 × ~600B/tick/player） | 高 | 中（末端はローカル物理） |
| Rootのみ（現状） | 低 | **極低（破綻）** | 低 |
| State Authority + Kinematic Proxy | 低〜中（カスタム圧縮可） | 高 | 高（カスタム実装必要） |

---

## 4. 推奨アーキテクチャ

### 4.1 推奨案: **Forecast OFF + 全ボディパーツ NetworkRigidbody（段階的）**

既存タイトルの調査結果とFusionの機能を総合し、以下を推奨します。

```
┌─────────────────────────────────────────────────┐
│                 推奨アーキテクチャ                  │
├─────────────────────────────────────────────────┤
│                                                   │
│  Host (State Authority)                           │
│  ├── 全Rigidbodyでアクティブ物理実行               │
│  ├── ConfigurableJoint による姿勢制御              │
│  ├── AddForce による移動・バランス制御              │
│  └── NetworkRigidbody が位置/回転を自動同期        │
│                                                   │
│  Client (Proxy)                                   │
│  ├── 全Rigidbody が Kinematic に自動設定           │
│  ├── NetworkRigidbody が位置/回転を補間で受信      │
│  ├── ローカル物理演算なし（振動の原因を排除）       │
│  └── ビジュアルは滑らかに補間表示                  │
│                                                   │
│  自分のプレイヤー (Input Authority)                │
│  ├── 全Rigidbodyでアクティブ物理実行               │
│  ├── 入力 → FixedUpdateNetwork → 物理更新          │
│  └── State Authorityと同じ物理ロジック実行          │
│                                                   │
└─────────────────────────────────────────────────┘
```

### 4.2 なぜForecast OFFか

| 観点 | Forecast ON | Forecast OFF |
|------|------------|-------------|
| Proxy側の物理 | 全クライアントで物理実行 | Kinematic補間（物理なし） |
| 振動リスク | 外挿エラー + 物理の非決定性で振動しやすい | 補間のみなので振動しない |
| 帯域 | 各クライアントの外挿誤差補正が必要 | 権威側の結果だけ送信 |
| インタラクション | クライアント間で物理的に相互作用可 | Proxyはkinematicなので衝突しない（別途対処が必要） |
| 実装複雑度 | 低（Fusionが自動処理） | 低（Fusionが自動処理）+ 衝突の追加対処 |
| 業界実績 | FPS等のシンプルな物理向き | ラグドール系ゲーム（Gang Beasts等）の主流 |

**Forecast OFFの最大の利点**: Proxy側のRigidbodyが自動でKinematicになるため、
ネットワーク補正とローカル物理が衝突する「引っ張り合い振動」が**原理的に発生しない**。

### 4.3 Forecast ON も検討すべきケース

将来的に以下が必要になった場合は再検討：
- クライアント間での物理的なインタラクション（掴み合い等）の即時レスポンス
- 環境オブジェクトとの物理的なインタラクションの予測

ただし、Gang BeastsもParty Animalsも権威型で十分な体験を実現しているため、
最初はForecast OFFで始めるのが安全。

---

## 5. 実装計画（段階的）

### Phase 1: 基盤修正（最優先 - 振動の直接原因を修正）

#### 1-1. RunnerSimulatePhysics の追加
- Main_Backupシーンの NetworkRunner と同じ GameObjectに `RunnerSimulatePhysics3D` を追加
- Unity側の Physics Settings で `Simulation Mode` を確認（Fusionが自動制御するが念のため）

#### 1-2. NetworkProjectConfig の修正
```json
{
    "PhysicsForecast": false  // 現状維持（OFFのまま）
}
```
※ Forecast OFFの状態で、NetworkRigidbodyがProxy側を自動Kinematic化する

#### 1-3. 全ボディパーツに NetworkRigidbody を追加
- `newAPRPlayer.prefab` / `APR_Root.prefab` の全15個のRigidbodyに `NetworkRigidbody` コンポーネントを追加
- 設定:
  - `SyncParent`: 0（APR_Rootは `SetParent(null)` で切り離されるため）
  - `SyncScale`: 0（スケール変更なし）
  - `Interpolation Target`: 未設定（デフォルト）

#### 1-4. Rigidbody の Interpolation 設定
- 全RigidbodyのInterpolationを `Interpolate` に変更
  - プレハブの `m_Interpolate: 0` → `m_Interpolate: 1`

### Phase 2: コードの修正

#### 2-1. Time.deltaTime → Runner.DeltaTime
`RagDollPhysics.cs` の全ての `Time.deltaTime` を Fusion の tick delta に置換。
`RagdollPhysics`は非NetworkBehaviourなので、コンストラクタ経由でRunnerへの参照を渡すか、
`RagdollController`からdeltaTimeを引数で渡す。

```csharp
// Before
_bodyRigidbodies[i].AddForce(-Vector3.up * feetForce * Time.deltaTime, ForceMode.Impulse);

// After
_bodyRigidbodies[i].AddForce(-Vector3.up * feetForce * deltaTime, ForceMode.Impulse);
// deltaTimeはRagdollControllerから Runner.DeltaTime を渡す
```

#### 2-2. GetInput() 失敗時の処理
現在 `GetInput()` が `false` の場合（Proxy側）にreturnしている。
Forecast OFF + NetworkRigidbody 構成では、Proxy側の物理はNetworkRigidbodyが
自動で制御するため、この return は適切（物理コードを実行する必要がない）。
ただし、ビジュアルエフェクト等の更新は `GetInput()` の外で行うべき。

#### 2-3. FixedUpdate → FixedUpdateNetwork への統一
`RagDollPhysics.cs`の物理計算が`FixedUpdateNetwork()`から呼ばれることを確認し、
Unity標準の`FixedUpdate`から呼ばれているコードがないか確認。

### Phase 3: 最適化（Phase 1,2 の動作確認後）

#### 3-1. 帯域幅の監視と最適化
- Fusion Statistics で帯域使用量を確認
- 必要に応じて:
  - 末端パーツ（手、足先）の`NetworkRigidbody`同期頻度を下げる
  - `InterestManagement`（AOI）を設定し、遠いプレイヤーの更新頻度を下げる

#### 3-2. Proxy側の衝突対応
- Forecast OFFではProxy側がKinematicなので、他プレイヤーとの物理衝突が発生しない
- 対処法:
  - 掴み等のインタラクションはRPC経由でState Authorityに委任
  - 衝突判定はState Authority側で一元管理
  - 必要ならProxy側にTrigger Colliderを配置して視覚的フィードバックのみローカル処理

---

## 6. 既存タイトルとの比較

| 項目 | Gang Beasts | Party Animals | REBAKA (推奨案) |
|------|------------|---------------|-----------------|
| ネットワーク | Unity NGO + Steam | カスタム | Photon Fusion 2 |
| 物理権威 | ホスト | 専用サーバー | ホスト (State Authority) |
| Proxy側の物理 | 補間のみ | スナップショット補間 | Kinematic + NetworkRigidbody補間 |
| 同期対象 | 主要ボディパーツ | 簡略化ラグドール | 全15パーツ（Fusionが自動管理） |
| 帯域削減 | half精度, Smallest3圧縮 | デルタ圧縮, 優先度更新 | Fusionの内蔵圧縮 + AOI |
| ラグドールボーン数 | ~10 | ~8 | 15 |

---

## 7. リスクと注意事項

### 高リスク
- **15パーツ全同期の帯域**: プレイヤー数が多い場合（8-10人）、帯域が問題になる可能性。Phase 3で監視・最適化
- **SetParent(null) との互換性**: APR_RootをDetachするロジックが、NetworkRigidbodyの`SyncParent`と干渉する可能性。テストで確認必要

### 中リスク
- **ConfigurableJointの挙動**: Proxy側でRigidbodyがKinematicになった場合、ConfigurableJointが正しく無視されるか確認が必要
- **入力遅延**: ホスト権威のため、クライアントの入力にRTT分の遅延が発生。Fusionの入力予測である程度緩和可能

### 低リスク
- **既存物理パラメータ**: JointDrive等の既存チューニングはState Authority側でそのまま動作するはず

---

## 8. 自力再実装チェックリスト

- [ ] Fusionの物理シミュレーション制御の仕組み（`RunnerSimulatePhysics`）を理解しているか
- [ ] `NetworkRigidbody`がForecast OFF時にProxy側をKinematic化する仕組みを説明できるか
- [ ] なぜ「ルートのみ同期 + ローカル物理」が振動を起こすか説明できるか
- [ ] Gang Beasts等が権威型を採用する理由（物理の非決定性）を説明できるか
- [ ] `Time.deltaTime`と`Runner.DeltaTime`の違いを説明できるか
- [ ] 帯域削減手法（量子化、デルタ圧縮、AOI）の概念を説明できるか

---

## 参考資料

- [Photon Fusion Physics Manual](https://doc.photonengine.com/ja-jp/fusion/current/manual/physics)
- [Photon Fusion Physics Addon Overview](https://doc.photonengine.com/ja-jp/fusion/current/addons/physics/overview)
- Gang Beasts ネットワーキング: Boneloaf開発チームの知見（P2P→サーバーリレー移行の教訓）
- Party Animals: Recreate Games のGDC共有（権威型 + ビジュアル補間アーキテクチャ）
- [GDC Vault - Physics for Game Programmers](https://www.gdcvault.com/)（物理ネットワーク同期全般）
