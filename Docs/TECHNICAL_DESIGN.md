# REBAKA_Fusion2 技術設計ドキュメント

> **作成開始:** 2026-01-30
> **Phase 2 期間:** 2/1〜2/14
> **目的:** 本プロジェクトの技術設計と設計判断の記録

---

## 📌 このドキュメントの目的

このドキュメントは、REBAKA_Fusion2の技術的な理解を深め、設計判断を第三者に説明できる状態にするために作成します。

**重要な方針:**
- AIが生成した部分と自分で理解した部分を明確に区別する
- 問題解決のプロセスを「学習ストーリー」として記録する
- 理解が不十分な部分も正直に記載し、学習の余地を示す

---

## 1. プロジェクト概要

### 1.1 プロジェクトの目的

本プロジェクトは**ポートフォリオ作品**として、以下3つの技術力を示すために制作した。

1. **問題解決能力**: 物理演算やネットワーク同期で発生するバグを発見し、原因を分析し、解決するプロセスを示す
2. **ネットワーク物理同期の技術的理解**: Photon Fusion 2でのラグドール物理同期の仕組みを、コードレベルで説明できる
3. **AIとの協働力**: AI（Claude Code）をコード生成ツールとして活用しつつ、生成されたコードを自分で読み解き、理解を深める姿勢を示す

方針として「ゲーム完成度 < 技術的理解の深さ」を明確に選択した。面白いゲームを作ることよりも、「なぜこう実装したのか」「どんな問題が起きてどう解決したか」を説明できる状態を優先している。

### 1.2 技術的な挑戦

このプロジェクトが技術的に挑戦的な理由は、「アクティブラグドール制御」と「ネットワーク物理同期」という2つの難しい問題を同時に解く必要がある点にある。

**アクティブラグドールの複雑さ:**
- 13個のボディパーツ（Root, Body, Head, 両腕上下, 両脚上下, 両足）をConfigurableJointで接続
- 各関節のJointDriveで「バネの強さ」と「ダンピング」を設定し、物理的に自然な姿勢を維持する
- PID制御で直立バランスを保ちつつ、歩行・ジャンプなどの動作を物理ベースで実現する
- 単純にバネを強くすると振動し、弱くすると倒れる。このバランス調整が難しい

**ネットワーク越しの物理同期の難しさ:**
- 物理演算は非決定的（同じ入力でも浮動小数点の丸め誤差等で結果が微妙に異なる）
- 13個のRigidbodyの位置・回転・速度をすべて同期する必要がある
- ネットワーク遅延（ラグ）があるため、リアルタイムに正確な状態を共有できない
- 当初はPhoton Fusion 2.1のForecast Physics（外挿ベース）で対処を試みたが、A/Bテスト（2026-03-28）で棄却。現在は**ホスト権威＋kinematic純補間プロキシ**（スナップショット補間）で対処している（§3.3 / §4.8）

**組み合わせの困難さ:**
- ラグドール制御はFixedUpdate（物理フレーム）で動作し、ネットワーク同期はFixedUpdateNetwork（Fusionのティック）で動作する。この2つのタイミングを正しく統合する必要がある
- ローカルで完璧に動くラグドールが、ネットワーク越しではガタガタ震えたり手足が吹っ飛んだりする問題が実際に発生した

### 1.3 スコープと制約
- **実装する要素**: アクティブラグドール制御、ネットワーク物理同期、最小限のゲームシーン
- **実装しない要素**: ダンジョン、美術、UI/UX、4人マルチ
- **理由**: 技術的理解の深さを優先し、短期間で技術的な完成度を示せる状態にするため

---

## 2. 技術選定理由

### 2.1 なぜPhoton Fusionを選んだのか

#### 選定背景

Unityのマルチプレイヤーフレームワークには主に以下の選択肢がある：

| フレームワーク | 特徴 | 物理同期 |
|---|---|---|
| **Mirror** | 無料・OSS、サーバー権威型 | 手動実装が必要 |
| **Netcode for GameObjects** | Unity公式、サーバー権威型 | NetworkRigidbodyあり（基本的） |
| **Photon Fusion 2** | ティックベース、State Sync | NetworkRigidbody + Forecast Physics |

Photon Fusionを選んだ最大の理由は、**物理演算の同期に対する公式サポートが最も充実している**こと。特にFusion 2.1で導入されたForecast Physicsは、外挿（Extrapolation）ベースの物理同期をフレームワークレベルで提供しており、ラグドールのような複雑な物理オブジェクトの同期に適している。

#### Photon Fusionの強み

1. **NetworkRigidbodyによる自動同期**: 各ボディパーツのRigidbodyの位置・回転・速度をフレームワークが自動的に同期する。13パーツのラグドールでも個別に同期コードを書く必要がない
2. **State Authorityによる状態管理**: 「誰がこのオブジェクトの物理演算を実行するか」を明確に管理できる。Authority側で物理演算を実行し、結果を他クライアントに配信する
3. **Forecast Physics（Fusion 2.1）**: 外挿ベースの物理同期。フル再シミュレーション（CPU負荷が高い）ではなく、外挿で補間するためCPU負荷が低い
4. **ティックベースの入力同期**: `FixedUpdateNetwork()`で入力と物理を同じティックで処理できる

#### トレードオフ

- **Pros（利点）**:
  - 物理同期のインフラが整っており、ラグドール同期に集中できる
  - Forecast Physicsにより再シミュレーション不要でCPU負荷が低い
  - `[Networked]`属性で状態同期が簡潔に書ける

- **Cons（欠点）**:
  - Fusion 2.1はPreview版であり、ドキュメントが不十分な部分がある
  - 学習コストが高い（ティックベースの概念、State Authority、NetworkBehaviourのライフサイクル等）
  - 無料枠に制限がある（同時接続数）

- **判断**: Fusion 2.0.xではクライアント側でラグドールがガタガタ震える・手足が吹っ飛ぶ問題が発生した。「壊れた古いシステムを修理する時間」より「Forecast Physicsで根本解決する時間」の方が確実性が高いと判断し、Preview版のリスクを承知の上で2.1に移行した。Gitでバックアップを取り、いつでも2.0.xに戻れる状態を維持した

---

## 3. アーキテクチャ

### 3.1 システム全体図

```
[クライアント1 (State Authority)]          [クライアント2 (Proxy)]
       │                                           │
  ┌────┴────────────────┐                  ┌────────┴───────────┐
  │ RagdollController   │                  │ RagdollController  │
  │  (NetworkBehaviour) │                  │  (NetworkBehaviour)│
  │                     │                  │                    │
  │ FixedUpdateNetwork()│   Photon Server  │ 状態を受信して     │
  │  ├→ 入力処理        │◄────────────────►│ 物理を外挿補間     │
  │  ├→ 状態更新        │   [Networked]    │                    │
  │  └→ 物理更新        │   プロパティ同期  │                    │
  │                     │                  │                    │
  │ RagdollPhysics      │  NetworkRigidbody│ RagdollPhysics     │
  │  ├→ PID直立制御     │  ×13パーツ自動同期│  (Proxy側は制限)   │
  │  ├→ 歩行サイクル    │                  │                    │
  │  └→ バランス計算    │                  │                    │
  └─────────────────────┘                  └────────────────────┘
```

**データフローの要約:**
1. Authority側で `FixedUpdateNetwork()` が毎ティック実行される
2. 入力（WASD、ジャンプ等）を `NetworkInputData` として受信
3. `RagdollPhysics.UpdatePhysics()` で物理演算を実行
4. 13個のRigidbodyの状態が他クライアントへ同期
5. `[Networked]` 属性の `CurrentState`, `MoveDirection`, `LookDirection` も同期

> **⚠️ 現状との差分（2026-06 時点）:** 上の図と要約は「Forecast Physics + 全パーツ NetworkRigidbody」を前提にした
> **初期アーキテクチャ**を表す。現在は (a) プロキシは Rigidbody を **kinematic にした純補間方式**（外挿補間しない、§3.3 採用方式 / §4.8）、
> (b) **プレイヤーの NetworkRigidbody は削除済み**で、ルート・頭・手の位置はカスタム同期（`[Networked]` + `PublishProxyPoseSnapshot()`）が担う（§4.7）。
> NetworkRigidbody（正確には `GameNetworkRigidbody`）が残るのは Obs_Cube / RingRails 等の**ピア同期物体のみ**。図は学習ストーリーの起点として残す。

### 3.2 ラグドール制御の仕組み

#### RagdollPhysics の役割（カスタム実装）

本プロジェクトでは、サードパーティの「APR Player」をベースに、物理制御を `RagdollPhysics` クラスとして独自に再実装した。

**ボディ構造（13パーツ）:**
```
     [Head] (2)
       │
     [Body] (1)
    ／  │  ＼
[UpperL  [Root] (0)  UpperR
 Arm](5)  │    [Arm](3)
   │    ／  ＼    │
[LowerL  [UpperL [UpperR  [LowerR
 Arm](6)  Leg](9) Leg](7)  Arm](4)
            │      │
          [LowerL [LowerR
           Leg](10) Leg](8)
            │      │
          [LeftFoot [RightFoot
           ](12)    ](11)
```

各パーツはRigidbodyとConfigurableJointを持ち、JointDriveの「バネ力」と「ダンピング」で接続されている。

**制御の仕組み:**

1. **JointDrive（バネ + ダンピング）**: 各関節に設定される力のプロファイル
   - `balanceOn`: バランス用（強い力: 5000 + ダンピング500）
   - `poseOn`: ポーズ維持用（中程度: 500 + ダンピング75）
   - `coreStiffness`: コア用（1500 + ダンピング150）
   - `driveOff`: 無効化用（微小な力: 25 + ダンピング5）

2. **PID制御による直立バランス** (`ApplyUprightForce()`):
   - 現在の体の傾き（`rootRb.transform.up` vs `Vector3.up`）を測定
   - PIDコントローラーで補正トルクを計算（P=300, I=0, D=100）
   - Y軸（ヨー）のトルクは除外（向きの制御は移動時に行う）
   - 5度未満の傾きはデッドゾーンとして無視（微振動防止）
   - バランス優先度（Idle: 0.8, Walking: 0.6）でトルクをスケール

3. **歩行サイクル** (`UpdateWalkCycle()`):
   - sin波で左右の足を交互に振る（位相差180度）
   - 太もも: `sin(t) * 15度`、膝: 後ろ足のみ曲げる
   - PID制御で水平移動速度を目標値に近づける

4. **バランス判定** (`CalculateDetailedBalanceState()`):
   - 全Rigidbodyの質量加重平均から重心（COM）を計算
   - 両足の中間点を支持基底面の中心とする
   - 重心が支持基底面から0.15m以上逸脱したらバランス崩壊と判定

**理解度:**
- ✅ **理解できている部分**: JointDriveのバネ+ダンピングモデル、PID制御の各項（P=即座の反応、I=累積誤差、D=変化率でブレーキ）、バランス判定の仕組み（重心 vs 支持基底面）
- ⚠️ **まだ不十分な部分**: PIDパラメータの最適チューニング方法（現在は試行錯誤）、ConfigurableJointの内部でUnityがどのようにtargetRotationを実現しているか

#### Rigidbodyと物理演算

**Rigidbodyの役割:**
- Unityの物理エンジン（PhysX）が管理するオブジェクト
- 質量、速度、角速度、重力の影響を受ける
- `AddForce()` / `AddTorque()` で力を加えて動かす

**FixedUpdateでの物理演算:**
- Unityの物理演算は `FixedUpdate()` で固定タイムステップ（デフォルト0.02秒=50Hz）で実行される
- `Update()` はフレームレート依存で不安定なため、物理演算には使わない
- Photon Fusionでは `FixedUpdateNetwork()` がこの役割を担い、ネットワークティックと物理フレームを同期する

**ConfigurableJointによる関節制御:**
- `targetRotation`: 関節の目標回転（Joint空間での相対回転）
- `angularXDrive` / `angularYZDrive`: 目標回転に向かう力の強さ（バネ + ダンピング）
- Joint空間の計算: `targetRotation = Inverse(spawnRotation) * desiredWorldRotation`

**理解度:**
- ✅ **理解できている部分**: FixedUpdateで物理を処理する理由、Rigidbodyの基本操作、ConfigurableJointのtargetRotation計算
- ⚠️ **まだ不十分な部分**: PhysXの内部ソルバーの動作、Joint制約の解決順序

### 3.3 ネットワーク物理同期の仕組み

#### NetworkRigidbodyの役割

> **⚠️ 現状との差分:** 以下は初期アーキテクチャ（全13パーツに NetworkRigidbody）の説明。**プレイヤーの NetworkRigidbody は §4.7 で削除済み**で、
> 現在ルート・頭・手の位置同期はカスタム同期（`[Networked]` + スナップショット）が担う。NetworkRigidbody が現役で使われるのは
> Obs_Cube / RingRails 等の**ピア同期物体**で、そこも純補間プロキシ用のサブクラス `GameNetworkRigidbody`（§3.3 採用方式 / §4.8）に置き換わっている。
> State Authority の概念自体は現在も有効なので学習用に残す。

**State Authorityの概念:**
- 各ネットワークオブジェクトには「誰がこのオブジェクトの正しい状態を持っているか」を示すState Authorityがある
- Authority側: 物理演算を実行し、結果をサーバー経由で他クライアントに配信
- Proxy側: 受信した状態をもとに表示を更新（自分では物理演算を実行しない）

**実装での使われ方（`RagdollController.cs`より）:**
```csharp
// Authority側のみ物理演算を実行
public override void FixedUpdateNetwork()
{
    if (!GetInput(out NetworkInputData data)) return;
    // 入力処理 → 状態更新 → 物理更新
    _ragdollPhysics.UpdatePhysics(CurrentState, MoveDirection, LookDirection);
}

// ジャンプもAuthority側のみ
if (_controller.Object.HasStateAuthority)
{
    rigidBody.linearVelocity = v3;
}
```

**同期されるデータ:**
- `[Networked] CurrentState`: プレイヤーの状態（Idle/Walking/Jumping等）
- `[Networked] MoveDirection`: 移動方向ベクトル
- `[Networked] LookDirection`: 視線方向
- `[Networked] IsLeftFootGrounded / IsRightFootGrounded`: 足の接地状態
- NetworkRigidbody経由: 13パーツの位置・回転・速度（自動同期）

**理解度:**
- ✅ **理解できている部分**: State Authorityの役割、Authority側で物理実行→Proxyに配信の流れ、`[Networked]`属性による状態同期
- ⚠️ **まだ不十分な部分**: NetworkRigidbodyの内部での補間アルゴリズム、帯域幅の最適化方法

#### 予測補間（Prediction & Interpolation）

**なぜ予測補間が必要か:**
- ネットワーク通信には遅延（ラグ）がある（例: 50ms）
- サーバーからの状態更新を待っていると、その間プレイヤーが止まって見える
- 予測: 「次にこう動くだろう」と推測して先に動かす
- 補間: サーバーから正しい状態が届いたら、滑らかに修正する

**Forecast Physics（Fusion 2.1）:**

| 項目 | Fusion 2.0.x | Fusion 2.1 |
|---|---|---|
| 同期方式 | Physics Addon（別パッケージ）| NetworkTransformに統合 |
| 予測方法 | フル再シミュレーション | 外挿（Extrapolation）ベース |
| CPU負荷 | 高い | 低い |

- **外挿ベース**: 最新の状態から「この方向・速度で動き続けるだろう」と推測する
- **フル再シミュレーション不要**: 過去の状態に戻って物理を再計算する必要がないため、CPU負荷が大幅に低い
- これが、13パーツのラグドールをネットワーク同期する上で重要（再シミュレーションだと13個分のコストがかかる）

**理解度:**
- ✅ **理解できている部分**: 予測補間がなぜ必要か、外挿と再シミュレーションの違い、Forecast Physicsの利点
- ⚠️ **まだ不十分な部分**: 外挿の具体的な補間アルゴリズム（線形か非線形か）、状態のスナップバック処理の詳細

**実際に採用した方式（2026-06-18 更新・上表の Forecast は不採用）:**

A/B テスト（2026-03-28）の結果、Forecast Physics（外挿）は棄却し、**プロキシでは Rigidbody を
kinematic にしてローカル物理を完全に止め、ホストのスナップショットを補間して描画するだけ**の
「純補間プロキシ」方式を採用した（プレイヤー = SnapshotInterpolation）。外挿のスナップバックを
そもそも発生させない、ホスト権威と素直に整合する、という利点が実機で勝った。

2026-06-18、この原理を**プレイヤー以外のピア同期物理オブジェクト（Obs_Cube / RingRails 等）にも一般化**した。
`GameNetworkRigidbody : NetworkRigidbody`（`[Networked]`/`[Rpc]` を足さず weaver の語数を不変に保つ
サブクラス）で、プロキシの `isKinematic=true` を Spawned と `CopyToEngine` override の両方で維持する。
これにより「非 kinematic プロキシがローカル物理で先行 → 補間遅延のプレイヤーとの間にできる隙間」が消える。
詳細・原理・自力再実装手順は `Docs/devlogs/2026-06-18_peer_sync_pure_interpolation.md` を参照。

> 残課題: `Treasure_Heavy` は別経路の独自同期で、まだこの統一の対象外。

### 3.4 ラグドール制御とネットワーク同期の結びつき

#### 統合の課題

1. **多数のRigidbody同期**: 一般的なネットワークゲームではキャラクターは1つのRigidbody。本プロジェクトでは13個を同時に同期する必要がある
2. **物理演算の非決定性**: 浮動小数点の丸め誤差により、同じ入力でもクライアント間で結果が微妙に異なる。ラグドールのような不安定な物理系ではこの差が増幅される
3. **NetworkBehaviourのライフサイクル**: `Spawned()` が呼ばれる前にネットワーク関連のプロパティにアクセスするとクラッシュする。初期化順序の管理が重要

#### 実装の詳細

**同期されるパーツ:**
- 全13パーツにNetworkRigidbodyがアタッチされ、自動同期される
- ルート（APR_Root）が物理世界での基準点。`DetachRootFromParent()` で親オブジェクトから切り離し、独立した物理運動を可能にしている

**入力の同期方法:**
```
[キーボード入力] → OnInput() → NetworkInputData（構造体）
    → FixedUpdateNetwork()で受信 → RagdollInput.ProcessInput()
    → RagdollCommand（内部データ） → 状態更新 + 物理更新
```
入力は `NetworkInputData` としてサーバー経由で同期される。入力を同期するのであり、物理の結果を直接同期するのではない（入力→物理をAuthority側で実行→結果をNetworkRigidbodyで配信）。

**衝突判定の処理:**
- 同一ラグドール内のパーツ間衝突は `Physics.IgnoreCollision()` で無効化（自分の体の部品同士がぶつかって振動するのを防止）
- 他プレイヤーとの衝突はそのまま有効（フレンドリーファイヤのため）
- 足の接地判定は `RagdollFootContact` コンポーネントで検出し、`[Networked]` で同期

**理解度:**
- ✅ **理解できている部分**: 入力同期→物理実行→結果配信の流れ、NetworkBehaviourのライフサイクル（Spawned()の重要性）、衝突無効化の仕組み
- ⚠️ **まだ不十分な部分**: 13パーツ同期時の帯域幅消費量、大人数（4人以上）でのスケーラビリティ

---

## 4. 課題と解決（問題解決ストーリー）★メイン

> **このセクションが最も重要です。** 「問題解決能力」を示すために、発見した問題とその解決プロセスを詳しく記録します。

### 4.1 Fusion 2.0.9 → 2.1 移行とForecast Physics導入

#### 問題の発見
Fusion 2.0.xでネットワーク物理同期を実装した段階で、以下の深刻な問題が発生した：
- **クライアント側でラグドールがガタガタ震える**: Proxy（非Authority）側で表示されるラグドールが常に振動
- **手足が吹っ飛ぶ**: ネットワーク遅延の影響で、関節が異常な位置に移動する
- **重力が正しく動作しない**: Authority側では正常だが、Proxy側で重力の挙動が不自然

これらの問題は、Fusion 2.0.xの物理同期方式（Physics Addon + フル再シミュレーション）がラグドールのような多パーツ物理に適していなかったことが原因。

#### 原因の分析
- Fusion 2.0.xのPhysics Addonは、ネットワーク補正時に「過去の状態に戻って物理を再シミュレーション」する方式
- 13パーツのラグドールの場合、再シミュレーションのCPU負荷が非常に高い
- さらに、再シミュレーションの精度が不十分で、関節の拘束条件が正しく再現されない場合がある
- AIに「Fusion 2.0の物理同期でラグドールが震える」と相談し、Fusion 2.1のForecast Physicsの存在を知った

#### 解決策
**Fusion 2.0.9 → 2.1にアップグレードし、Forecast Physicsを導入**

- Forecast Physicsは外挿（Extrapolation）ベースで、再シミュレーション不要
- Physics AddonからNetworkTransform統合の物理同期に移行
- API変更対応: `OnReliableDataReceived`のシグネチャが `ArraySegment<byte>` → `ReadOnlySpan<byte>` に変更

**判断プロセス:**
- 「壊れた古いシステムを修理する時間」vs「新しいシステムを導入する時間」を比較
- Preview版のリスクはあるが、根本解決できる方が確実性が高いと判断
- Gitでバックアップを取り、いつでも2.0.xに戻れる状態を確保した上で移行

**AIが支援した部分:** Forecast Physicsの存在の情報提供、API変更点の特定、移行コードの生成
**自分で判断した部分:** Preview版への移行リスクの受け入れ判断、バックアップ戦略

#### 学んだこと
- **「パッチ」より「根本解決」**: 壊れたアーキテクチャの上にパッチを重ねるより、新しいアーキテクチャに移行する方が結果的に早い場合がある
- **リスク管理**: Preview版を使う判断には、「いつでも戻れる」バックアップ戦略が不可欠
- **フレームワークのバージョン選択の重要性**: 物理同期のような根本的な機能は、フレームワークのバージョンに大きく依存する

---

### 4.2 ラグドール制御の安定化

この問題は2段階で解決した：(A) PID制御の導入、(B) 振動と向きの修正。

#### 問題の発見

**問題A: 単純なバネ制御では不安定（2026-01-25発見）**
- ラグドールが外力を受けた後、直立に戻る際にオーバーシュート（行き過ぎ）して振動する
- `AddTorque` の力加減が難しく、強すぎると震え、弱すぎると倒れる

**問題B: プルプル震える＋向きが戻る（2026-02-04発見）**
- スポーン後、何も操作していなくてもプレイヤーが微細に震え続ける
- WASDキーで向きを変えても、入力を辞めるとスポーン時の向き（Z+方向）に戻ってしまう

#### 原因の分析

**問題Aの原因:**
- 単純な `AddTorque` は比例制御（P制御）のみで、ブレーキ成分がない
- 目標角度に近づいても速度を落とさないため、通り過ぎて振動する

**問題Bの原因:**
1. **向きが戻る問題**: `UpdateRootTargetRotation()` で `Quaternion.identity` を使っていた
   ```csharp
   // 誤り: 常にワールドZ+方向を向こうとする
   Quaternion initialWorldRot = Quaternion.identity;
   _bodyJoints[IndexRoot].targetRotation = Quaternion.Inverse(newTarget) * initialWorldRot;
   ```
   ConfigurableJointの `targetRotation` はJoint空間での回転を表すため、スポーン時のワールド回転を基準にする必要がある

2. **震え問題**: PID制御のデッドゾーンが2度と小さすぎ、微小な傾きに対してもトルクが発生。さらに、JointDriveのバネ力とPID制御のトルクが競合して振動を増幅

#### 解決策

**問題Aの解決: PIDコントローラーの導入（2026-01-25）**
- PID制御を自作し、直立バランスに適用
  - P（比例）: 傾きに比例した補正力（kp=300）。ただしPだけではオフセットが残る
  - I（積分）: オフセットを時間をかけて消す（ki=0、本プロジェクトでは未使用）
  - D（微分）: PV（傾き）の変化率を見て、偏差が大きくなる前に素早く修正（kd=100）
- D項が外乱（他プレイヤーの衝突等）への素早い反応を提供し、結果的にオーバーシュートも抑制
- パラメータをScriptableObject（`RagdollProfile`）に外出し、Inspector上で調整可能に

**問題Bの解決: スポーン回転の保存とデッドゾーン拡大（2026-02-04）**

1. スポーン時のワールド回転を保存:
   ```csharp
   _spawnWorldRotation = bodyRigidbodies[IndexRoot].rotation;
   ```

2. 正しいQuaternion計算に修正:
   ```csharp
   // 正しい: targetRotation = Inverse(spawnRotation) * targetWorldRotation
   _bodyJoints[IndexRoot].targetRotation = Quaternion.Inverse(_spawnWorldRotation) * targetWorldRot;
   ```

3. デッドゾーンを2度→5度に拡大し、PIDリセットを追加:
   ```csharp
   const float deadZoneDegrees = 5f;
   if (tiltAngle < deadZoneDegrees) {
       _uprightPid.Reset(); // 積分項もクリア
       return;
   }
   ```

**AIが支援した部分:** PIDコントローラーのコード生成、Quaternion計算の修正コード
**自分で判断した部分:** デッドゾーンの値（5度）の決定（歩行中の自然な揺れを許容するバランス）、ScriptableObjectへのパラメータ分離の設計判断

#### 学んだこと
- **PID制御の実用性**: P制御だけでは振動する系を、D項（微分）で安定化できる。ゲーム物理では非常に有用
- **ConfigurableJointのtargetRotation計算**: Joint空間とワールド空間の変換に `Inverse(spawnRotation) * targetWorldRotation` を使う。これを間違えると、意図しない方向に力が加わる
- **デッドゾーンの重要性**: 物理シミュレーションでは、微小な変動を無視する仕組みがないと永遠に収束しない。制御工学でいう「不感帯」の概念
- **データとロジックの分離**: パラメータをScriptableObjectに分離することで、コードを変更せずにInspector上で調整可能にできる。プログラマ以外（将来の自分を含む）が触りやすい設計

---

### 4.3 NetworkBehaviourのライフサイクル問題

#### 問題の発見
プレイヤーがスポーンした直後にNullReferenceExceptionが発生し、ラグドールが動作しない問題が複数回発生した。

具体的な症状：
- スポーン直後に `MissingReferenceException` や `NullReferenceException`
- ネットワーク関連のプロパティ（`Object.HasStateAuthority` 等）にアクセスするとクラッシュ
- ローカル（オフライン）テストでは問題なく動作するが、ネットワーク経由だと発生

#### 原因の分析
Photon FusionのNetworkBehaviourには固有のライフサイクルがある：

```
Awake() → Start() → ... → Spawned() → FixedUpdateNetwork()
                                ↑
                          ネットワーク初期化完了
```

**問題の根本原因:** `Awake()` や `Start()` の段階ではネットワーク初期化が完了しておらず、`[Networked]` プロパティやState Authorityへのアクセスが無効。`Spawned()` が呼ばれるまで待つ必要がある。

実際のコードで対処した例（`RagdollController.cs`）：
```csharp
public override void Spawned()
{
    // Spawned()内で全ての初期化を行う
    _ragdollInput = new RagdollInput();
    _ragdollState = new RagdollState(this);
    _ragdollPhysics = new RagdollPhysics(this, bodyParts, bodyRigidbodies, bodyJoints);
    // ...
}
```

#### 解決策
1. **初期化処理をすべて `Spawned()` 内に移動**: `Awake()` や `Start()` は使わず、`Spawned()` をエントリーポイントとする
2. **バリデーション追加**: `Spawned()` 冒頭で `ValidateComponents()` を実行し、必要なコンポーネントが揃っているか確認
3. **例外ハンドリング**: `Spawned()` をtry-catchで囲み、初期化失敗時にコンポーネントを無効化して安全に停止
4. **ルートの物理的独立**: `DetachRootFromParent()` でAPR_Rootを親オブジェクトから切り離し、Fusionの同期とUnityの親子関係が干渉しないようにした

**AIが支援した部分:** エラーメッセージからの原因特定、Spawned()への移行コード
**自分で判断した部分:** バリデーションの項目選定、DetachRootFromParentの必要性の判断

#### 学んだこと
- **フレームワーク固有のライフサイクルを理解する重要性**: Unityの標準ライフサイクル（Awake→Start→Update）とは異なるタイミングで初期化が必要。ドキュメントを読んで理解するべき基礎知識
- **防御的プログラミング**: ネットワーク環境では「いつ・どの順序で初期化されるか」が保証しにくいため、バリデーションと例外処理が重要
- **このバグパターンは頻出**: Photon Fusionを使う開発者が最も多く遭遇するバグの一つ。`Spawned()` を待つ、というルールを徹底するだけで予防できる

---

### 4.4 同一ラグドール内のパーツ衝突による振動

#### 問題の発見
ラグドールが動作中に、自身のボディパーツ同士（例: 腕と胴体、脚と脚）が衝突し、予期しない力が発生して振動や不自然な動きが起きた。

#### 原因の分析
Unityのデフォルトでは、同じGameObject階層内でも個別のCollider同士は衝突判定が有効。13パーツのラグドールでは、パーツ間の距離が近いため頻繁に衝突が発生する。

特にConfigurableJointで接続されたパーツ間では、Jointが引き寄せる力とCollisionが弾き返す力が競合し、激しい振動の原因になる。

#### 解決策
`SetupCollisionIgnores()` で同一ラグドール内の全Colliderペア間の衝突を無効化：

```csharp
// プレイヤー階層内の全コライダーを取得（装飾含む）
var allColliders = GetComponentsInChildren<Collider>();

// 同一プレイヤー内の全コライダー間の衝突を無視
for (var i = 0; i < allColliders.Length; i++)
    for (int j = i + 1; j < allColliders.Length; j++)
        if (allColliders[i] != null && allColliders[j] != null)
            Physics.IgnoreCollision(allColliders[i], allColliders[j], true);
```

他プレイヤーとの衝突は有効のまま。

**改善（2026-02-06）:** 当初は `bodyParts[]` 配列のコライダーのみを対象にしていたが、頭の飾り（Sphere (4)）等の装飾オブジェクトが漏れて不要な `OnCollisionEnter` が大量発火する問題が発生。`GetComponentsInChildren<Collider>()` でプレイヤー階層内の全コライダーを取得する方式に変更し、装飾オブジェクトも含めてPhysicsレベルで衝突を無視するようにした。Colliderは残るため装飾の物理的な揺れは維持される。

#### 学んだこと
- ラグドール系では「自分自身との衝突」が大きな不安定要因になる
- `Physics.IgnoreCollision()` はペア単位で設定する必要があり、パーツ数が多いと組み合わせ数が増える
- この処理は `Spawned()` 内で一度だけ実行すれば十分（実行時のコストは初期化時のみ）
- **bodyPartsだけでなく装飾オブジェクトのコライダーも忘れずに対象にする**。`GetComponentsInChildren<Collider>()` を使えば今後パーツが増えても自動対応される

---

### 4.5 APR_Root原点引力バグ

#### 問題の発見
テストプレイ中にプレイヤーがワールド原点(0,0,0)に向かって引っ張られ、正常に移動できない問題が発生した。スポーン位置が原点から離れるほど引力が強くなる。

#### 原因の分析
`ActivateRagdoll()` で全ジョイントに一律 `_driveOff`（positionSpring=25）を設定していたが、APR_Rootは `connectedBody=null`（ワールド接続）であるため、位置ドライブがワールド原点へのバネ力として作用していた。さらに `configuredInWorldSpace=false`（デフォルト）により、アンカーの基準点がスポーン時のワールド座標に固定されていた。

#### 解決策
3箇所（コンストラクタ、ActivateRagdoll、DeactivateRagdoll）でAPR_Rootの位置ドライブを明示的にゼロ化し、`configuredInWorldSpace=true` を設定。ルートジョイントは回転ドライブ（angularXDrive/angularYZDrive）のみを使用する。

#### 学んだこと
- `connectedBody=null` のConfigurableJointに位置ドライブを設定すると、ワールド原点への引力が発生する
- APRラグドールのルートジョイントでは回転ドライブのみを使い、位置ドライブは常にゼロにすべき
- ジョイント操作を一括で行う場合、ルートジョイント（connectedBody=null）は必ず特別扱いする

**詳細**: `Docs/devlogs/2026-02-12_origin_pull_fix.md` 参照

---

### 4.6 クライアントスポーン直後KO問題（Gang Beasts方式で解決）

#### 問題の発見

全ボディパーツへの `NetworkRigidbody` 追加後、Host + Client 2インスタンスで動作確認したところ、
クライアント側の自キャラがスポーン直後（1〜2秒以内）に必ずKO状態になる問題が発生した。

- Host側の自キャラ: 正常
- Client側の自キャラ: スポーン直後にKO → 動けない
- Client側の Proxy（他プレイヤー表示）: 正常

#### 原因の分析

3つの問題が複合していた:

1. **FixedUpdateNetwork() がクライアントでも物理を実行する**: `HasStateAuthority` チェックがなく、`InputAuthority`（クライアントの自キャラ）でも `UpdatePhysics()` が走っていた
2. **再シミュレーション中の衝突複数発火**: `RunClientSideResimulationLoop` 内で同一衝突が複数Tick検知され、`knockoutForce` 閾値を突破してKO
3. **isKinematic の競合**: `ActivateRagdoll()` が全Rigidbodyに `isKinematic = false` を強制設定し、NetworkRigidbodyの自動Kinematic管理を上書き

**HasStateAuthority と HasInputAuthority の違い（頻出の理解ポイント）:**
- `HasInputAuthority`: 入力を提供する権限（クライアントは自分のキャラのみ `true`）
- `HasStateAuthority`: 物理状態を決定する権限（ホストモードではホストが全オブジェクトで `true`）

クライアントの自キャラは `HasInputAuthority = true` だが `HasStateAuthority = false`。
物理演算は StateAuthority でのみ実行すべきだった。

#### 解決策: Gang Beasts方式

```csharp
public override void FixedUpdateNetwork()
{
    if (!GetInput(out NetworkInputData data))
        return;

    // GetInput()を先に呼ぶ（Fusionの入力パイプライン維持のため）
    if (!Object.HasStateAuthority) return;  // ← これだけで全問題が解消

    _ragdollInput.ProcessInput(data);
    // ... 以下の物理処理はホストのみ実行
}
```

**なぜ `GetInput()` をガードの前に置くか**: Fusion の入力バッファに「この入力は受信済み」と通知するため。
後ろに置くと、クライアントからホストへの入力送信が正常に機能しなくなる可能性がある。

**トレードオフ**: RTT/2 の入力遅延が発生するが、ラグドール操作は元々不正確なため体感上問題ない。

#### 学んだこと

- **物理処理は必ず `HasStateAuthority` でガードする**: `FixedUpdateNetwork()` に物理を書く際の鉄則
- **`HasInputAuthority ≠ HasStateAuthority`**: 混同するとクライアントで物理が走り再シミュレーション問題が発生
- **`GetInput()` の呼び出しタイミング**: ガードの前に呼ぶことで入力パイプラインを維持する
- **`isKinematic` を手動設定するコードは StateAuthority でガード**: NetworkRigidbody の自動管理と競合しないよう注意

**詳細**: `Docs/devlogs/2026-02-18_client_spawn_ko_gang_beasts_fix.md` 参照

---

### 4.7 NRB + DetachRootFromParent の非互換による床すり抜けバグ

#### 問題の発見

プレイヤーがスポーン直後から床をすり抜けて落下し続ける問題が発生。スポーン位置（Y=6）は正しく、物理シミュレーションは動作している（重力で落下する）のに、床との衝突のみが機能しない状態だった。

#### 原因の分析

調査で除外したもの：スポーン位置のミス・LayerCollisionMatrix の設定・`SetupCollisionIgnores()` の影響（プレイヤー内部のみで床に無関係）・isTrigger 設定。

真の原因は **APR_Root にアタッチされた `NetworkRigidbody (NRB)` と `DetachRootFromParent()` の非互換性**：

1. `Spawned()` 内で `DetachRootFromParent()` が APR_Root を `SetParent(null, true)` で親から切り離す
2. APR_Root は newAPRPlayer の NetworkObject 階層の外に出る
3. APR_Root にある NRB（`NetworkBehaviour` を継承）が親 NetworkObject を見つけられず機能不全
4. NRB が Rigidbody の物理処理（コリジョン応答含む）を妨害 → 床すり抜け

**Fusion 公式の制約:**

> "The `NetworkRigidbody` must be on the root of the Network Object."
> "you cannot rearrange the children of a single Network Object."

NRB は「NetworkObject 階層が変わらない」前提の設計であり、`DetachRootFromParent()` と根本的に非互換。

#### 解決策

`newAPRPlayer.prefab` の全パーツから **NetworkRigidbody コンポーネントを削除**（Inspector 操作のみ、コード変更なし）。

理由：カスタム同期（`[Networked]` プロパティ + `PublishProxyPoseSnapshot()`）がすでにルート・頭・手の位置同期を担っており、NRB は冗長だった。`_rootNetworkRigidbody` の全参照箇所がすでに null チェック済みのため、コード変更は不要。

#### 学んだこと

- **NRB の使用条件**: NetworkObject のルートに1つ、同じ GO に Rigidbody が必要。階層変更と非互換
- **動的な階層変更（SetParent）と NetworkBehaviour 系は原則として組み合わせない**
- **カスタム同期が既にある場合、NRB は削除すべき**（二重管理は害になる）
- 「スポーン直後の物理不正動作」は NRB の isKinematic 上書きを疑う

**詳細**: `Docs/devlogs/2026-02-20_nrb_detach_floor_passthrough_fix.md` 参照

---

### 4.8 クライアントプロキシ描画同期バグ2件（グラブ隙間 / 押し先行・カクつき）

#### 問題の発見

ホスト権威 + 純補間プロキシ構成で、**クライアント画面でのみ**プレイヤーと Obs_Cube の位置が一致しない症状を2件発見：

- **症状A（グラブ隙間）**: Cube を掴んで運ぶと、手と Cube の間に隙間ができる。
- **症状B（押し先行・カクつき）**: Cube を押すと、Cube がプレイヤーより約1 tick 先行し、かつプレイヤーは滑らかなのに Cube だけ tick レートでカクつく。

ホストでは2件とも正常。症状は似ているが**根本原因が別レイヤー**だった。

#### 原因の分析

- **症状A = 書き込み時刻のミスマッチ**: 手ポーズは `FixedUpdateNetwork()`（PRE-physics）で発行、Cube は `NetworkRigidbody` が `AfterTick`（POST-physics）で捕捉。`FixedJoint` で拘束された2剛体の捕捉時刻が1物理ステップずれ、補間後にその移動量ぶん隙間になる。
- **症状B = プロキシが Local タイムフレームで描画**: Host モードのクライアントは全 NetworkObject を予測するため、Cube の `RenderTimeframe` が既定 `Local`（非遅延）。だが kinematic プロキシは前進シミュレートしないため Local の補間端点 from/to が同一スナップに潰れ（segLen=0）、補間不能 → tick レートでカクつき。かつ Local は遅延ゼロのため Remote（遅延補間）のプレイヤーより先行。

**調査の要点**: 症状Bは当初「stock `NetworkRigidbody.Render()` の早期リターン（`RenderSource.Latest` か sleep 閾値）」と推測したが、`Render()` の override は private ダーティフラグ管理を壊すため安易に実装せず、**挙動を変えない計装**で実測。`tf=Local src=Interpolated segLen=0.000` のログ1行が当初仮説を否定し真因（タイムフレーム）を確定させた。

#### 解決策

- **症状A**: ポーズ発行を `RunnerSimulatePhysics.OnAfterSimulate`（POST-physics）へ移動。Cube と同じ時刻に手を捕捉し、補間で密着する。
- **症状B**: プレイヤーが既に使っている `Object.ForceRemoteRenderTimeframe = true` を Cube プロキシにも横展開（`GameNetworkRigidbody.Spawned()`、1行）。Remote はスナップショット履歴の異なる2点で Lerp するためカクつきが消え、プレイヤーと同一遅延で描かれ先行も解消。

#### 学んだこと

- **「ホスト正常 / クライアント異常」は同期・描画レイヤーに原因を絞れる**最初のシグナル。
- **早期リターンや override に飛びつく前に、挙動を変えない計装で実測する**（`RenderTimeframe` / `RenderSource` / 補間端点）。仮説はログ1行で決まる。
- **補間で見た目を合わせたい2要素は捕捉時刻（PRE/POST-physics）を揃える**。
- **ホスト権威プロキシは Remote タイムフレームに固定する**（プレイヤーに効いた対処を同種オブジェクトへ横展開する設定漏れに注意）。

**詳細**: `Docs/devlogs/2026-06-22_client_proxy_visual_gap_fix.md` 参照

---

### 4.9 掴み腕の肘折れ問題と角度limit + maxForceによる解決

#### 問題の発見

重い物を掴んだ状態で前進すると、肩は前を向いたまま**肘だけが逆方向に折れる**見た目になる。期待する挙動は「物の重さに引っ張られて腕全体が後方へ流れる」こと。

#### 原因の分析

- 肘関節の `ConfigurableJoint.angularXMotion` が `Free`（無制限）になっており、構造上ありえない角度まで回転できた。
- `JointDrive.maximumForce` が既定値の実質無限大のままで、目標姿勢へ戻す力に上限が無かった。これにより「重さに負けて流れる」のではなく無限の力で押し合って暴れる動きになっていた。

#### 解決策

役割分担を分けて設計（Codexとすり合わせ）:

- **角度limit（prefab側）**: 壊れ防止のハードな壁。肘・上腕の `angularXMotion` を `Limited` にし、左右対称な可動域を実機検証で確定（例: 肘は0°〜120°、上腕Xは-15°〜120°、Y/Zは±89°）。
- **`maximumForce`（profile側）**: 重さに負けて後方へ流れるためのソフトな降伏。`RagdollProfile` に `reachUpperArmJointMaxForce`/`reachLowerArmJointMaxForce`（既定1000f）を追加し、`ApplyReachPose` が毎フレームprofile値からJointDriveを生成（Play中のホットチューニングを反映するため）。
- 可動域がjointローカル軸基準で腕の見た目方向と一致しないため、`JointLimitVisualizer.cs`（`Tools/REBAKA/Joint Limit Visualizer`）で可動域をScene/Prefabビュー上で腕の実形状に重ねて表示するEditorツールを作成。実装中に「twistの0°基準は腕方向ではなくsecondary軸」「PhysXの回転規約がUnity標準APIと逆向き」など、理論だけでは正しい符号・基準に辿り着けない罠を複数踏み、実機の見た目との突き合わせで解決した。

#### 学んだこと

- 角度limitは「正常なポーズを作る」ためではなく「壊れを防ぐ」ための壁として設計し、降伏挙動はJointDriveの力の上限で別途作る、と役割を分けることで設計がシンプルになる。
- ConfigurableJointの角度系はAPIの数値だけでは直感的に検証できない。可視化ツールを作ることで、理論上の符号・基準のズレを実機の見た目で素早く特定できた。
- frontness変調や歩行中の動的降伏など重量に応じた連続的な調整は、その必要性が出るまでv2/v3へ先送りした（過剰設計の回避）。

**詳細**: `Docs/devlogs/2026-07-01_grab-arm-elbow-limit-and-yield.md` 参照

---

### 4.10 ジャンプができたりできなくなったりするバグ（ラッチ固着）

#### 問題の発見

実機テスト（Windows PC ホスト / MacBook Pro 2018 クライアント）で発見した7件のバグの1つ。
最初は正常にジャンプできるが、あるジャンプを境に以降ジャンプボタンを押しても一切反応しなくなる。
ホスト単独プレイでも再現し、ネットワーク同期は主因ではなかった。

#### 原因の分析

- ジャンプ初速は「1回の押下につき1回だけ与える」ラッチ（`_jumpVelocityApplied`）で制御していた。
- 旧設計はラッチの再武装（解除）を「足が接地を失ったこと（`LastFootGrounded == false`）」を
  信号に行っていた。
- `RagdollFootContact.cs`の接地判定は`OnCollisionStay`継続中はタイマーが切れない**エッジ
  トリガー**方式。左足が何らかの原因（自己接触/めり込み等、未特定）で接地判定に固着すると、
  `false`エッジが二度と発火しなくなる。
- 左右の足はOR演算（`anyFootGrounded = leftFootGrounded || rightFootGrounded`）で合成される
  ため、片足の固着だけで合成値が永久に`true`のまま残り、再武装条件が二度と成立しなくなった。

#### 解決策

3段階の試行錯誤の末、以下の組み合わせに到達した:

1. 再武装の信号源を「足の接地状態」から「ジャンプボタンの押下/解放」に変更（地面判定と独立化）。
2. それだけでは離陸直後の接地判定残留窓を突いた連打で2段ジャンプが起きるため、
   `ProcessJumpingPhysics()`の発火直前に「上昇中（`linearVelocity.y > 1.5f`）は再ジャンプ
   できない」という物理速度による独立ガードを追加。

途中、「最低滞空時間+着地ポーリング」方式を試したが、走行中の踏み出し足が素で接地判定`true`に
なるケースで長押しによる2段ジャンプを誘発し不採用とした。

#### 学んだこと

- 「1回の入力につき1回」という制約は、その入力と直接対応する信号（ボタンの押下/解放）を
  再武装の基準にするべきで、間接的な状態（足の接地）を基準にすると、その状態自体の信頼性に
  引きずられる。
- エッジトリガー方式のイベント駆動フラグにラッチ解除ロジックを依存させると、イベントの
  取りこぼし1回で永久デッドロックしうる。
- 1つの修正が別の症状（長押し2段ジャンプ→連打2段ジャンプ）を誘発することが2回続いた。
  修正のたびに複数パターン（タップ連打・長押し・連打スパム）を実機で確認する運用にした。
- 未解決: 左足の接地判定がなぜ固着したか（`OnCollisionStay`の継続発火の直接原因）は
  未特定 [※未確認]。速度ガードは症状（ジャンプ不能）を防いだだけで、固着自体は解消していない。

**詳細**: `Docs/devlogs/2026-07-09_jump-latch-fix.md` 参照

---

## 5. 学習プロセス（AIとの協働）

> **このセクションで「AIとの協働力」を示します。** AIをどう活用し、どう自分で理解を深めたかを記録します。

### 5.1 AI支援の範囲

#### AIが生成したコード
プロジェクトの実装コードの約9割はAI（Claude Code）が生成した。主要なファイル：

- `RagdollPhysics.cs`（1040行）: PID制御、バランス計算、歩行サイクル等の物理制御全般
- `RagdollController.cs`（620行）: ネットワーク統合、状態管理、初期化処理
- `RagdollProfile.cs`（76行）: ScriptableObjectによるパラメータ管理
- `RagdollInput.cs`, `RagdollState.cs`, `RagdollInteractions.cs`: サブシステム群
- `PidController.cs`, `RotationPidController.cs`: PIDコントローラー実装
- `RagdollFootContact.cs`, `RagdollImpactContact.cs`: 衝突検出
- Prefab設定: APR_Rootのコンポーネント構成、NetworkRigidbodyのアタッチ

#### 自分で行ったこと
- **問題の発見と症状の報告**: Playモードで動作確認し、「ラグドールが震えている」「向きが勝手に戻る」「スポーン直後にクラッシュする」等の具体的な問題をAIに伝えた
- **設計方針の決定**: プロジェクトのスコープ（ゲーム要素を削り技術デモに集中）、Fusion 2.1への移行判断、優先順位の決定
- **Playモードでの動作テスト**: AIが生成したコードをUnity上で実行し、挙動を目視確認。問題があれば再度AIに修正を依頼するサイクルを回した

正直に言うと、コードの実装はほぼ全てAIが行った。自分の役割は「何を作るか」「何が問題か」の判断と、動作確認のフィードバックループの運用だった

### 5.2 学習の進め方

#### Phase 1（AIマイグレーション期間: 〜2026-01月）
- **AIに任せたこと**: コードの新規実装、既存APR Playerからのカスタムコードへの移行、Photon Fusion 2の統合、バグ修正のコード生成
- **理解していなかったこと**: PID制御の数学的背景、ConfigurableJointのtargetRotation計算、Photon Fusionのティックベースの仕組み
- **なぜAIに任せたか**: 時間的制約（短期間で技術的な完成度を示す必要）と、ネットワーク物理同期というコードベースの複雑さ

#### Phase 2（理解を深める期間: 2/1〜）
- **コードリーディング**: 現時点ではAIが生成したコードを自分で体系的に読む段階に至っていない。問題が発生した際にエラー箇所を確認する程度
- **AIに質問したこと**: 問題の原因を質問し、AIの回答を聞いて対処を進めた
- **自分で調べたこと**: Photon Fusion公式ドキュメントやUnityリファレンスを少し確認した
- **現状の課題**: コードの「動作」は確認できるが、「なぜそう書かれているか」の理解が不足している。これがPhase 2の残りで最も埋めるべきギャップ

### 5.3 理解の深化

#### 概念レベルで把握していること（✅）
以下は「言葉の意味と大まかな仕組みは分かる」レベル。コードを見せられて詳細を説明するのはまだ難しい。

1. **State Authorityの概念**: 「誰が物理演算を実行するか」の権限管理。ホスト側が計算し、結果を配信する
2. **Spawned()ライフサイクル**: Photon Fusionではawake/Startではなく`Spawned()`で初期化する。これを間違えるとクラッシュする
3. **同一ラグドール内衝突無効化**: 自分のパーツ同士がぶつかると振動するので、Physics.IgnoreCollisionで防ぐ

#### まだ理解が不十分なこと（⚠️）
**正直に言うと、技術的な大部分がこちらに該当する。** 優先的に学習すべき項目。

1. **PID制御の仕組み**: P動作はオフセットが残る、I動作はそれを消す、D動作はPVの変化に素早く反応するという各項の役割は学習済み。ただしコードレベルでの説明はまだ練習中
2. **ConfigurableJointのtargetRotation計算**: `Inverse(spawnRotation) * targetWorldRotation` というコードがあることは知っているが、なぜInverseが必要なのかを自分の言葉で説明できない
3. **JointDriveのバネ+ダンピング**: 「バネの強さとダンピングで制御する」ことは知っているが、具体的にどう振動を抑制するのか説明できない
4. **FixedUpdateNetwork()の詳細**: ネットワークティックと物理フレームの関係が不明瞭
5. **Forecast Physicsの仕組み**: 「外挿ベース」という言葉は知っているが、具体的に何をしているか説明できない
6. **PIDパラメータの最適チューニング方法**
7. **NetworkRigidbodyの帯域幅最適化**
8. **PhysXソルバーの内部動作**

#### 今後学びたいこと（📚）
1. **ネットワーク帯域の最適化**: 大人数対応（4人以上）のためのデータ圧縮・間引き手法
2. **アニメーション連携**: 物理ベースのラグドールとモーションキャプチャアニメーションの融合
3. **PID制御の理論的背景**: 制御工学の基礎を体系的に学ぶ

---

## 6. 今後の展望

### 6.1 技術的な改善点

1. **PID制御の動的調整**: 現在はIdle/Walkingで固定のバランス優先度だが、状態に応じてPIDゲインやデッドゾーンを動的に変更することで、より自然な挙動が実現できる
2. **帯域幅の最適化**: 13パーツ全てをフルレートで同期するのではなく、重要度に応じて同期頻度を変える（ルートは高頻度、手先は低頻度等）
3. **アニメーション駆動の歩行**: 現在のsin波歩行を、モーションキャプチャデータやプロシージャルアニメーションに置き換える
4. **ラグドール→起き上がりの改善**: 現在の起き上がりは関節を元のポーズに戻すだけだが、自然な起き上がりモーションを物理ベースで実装する

### 6.2 学習の継続

⚠️の項目のうち特に重要な以下を優先的に学習する：
1. **PID制御の仕組みを自分の言葉で説明できるようにする**: 「なぜD項がブレーキになるか」を理解する
2. **ConfigurableJointのtargetRotation計算を理解する**: Quaternionの基礎とJoint空間の概念
3. **FixedUpdateNetwork()とState Authorityの流れを整理する**: 入力→処理→同期の全体像
4. **主要メソッド3つを自分で読む**: ApplyUprightForce(), UpdateRootTargetRotation(), FixedUpdateNetwork()

### 6.3 ポートフォリオとしての価値

このプロジェクトを通じて示せたこと：

1. **複雑な技術への挑戦**: ネットワーク物理同期 × アクティブラグドールという、業界でも難しいとされる課題に取り組んだ
2. **問題解決のプロセス**: 単に「動くコード」を書くのではなく、問題の根本原因を分析し、トレードオフを考慮した上で解決策を選択するプロセスを実践した（例: Fusion 2.1への移行判断）
3. **AIとの協働力**: AIをコード生成ツールとして活用しつつ、生成されたコードの仕組みを自分で理解し、説明できる状態にした。2026年のエンジニアに求められる「AIを使いこなす力」を示す
4. **自己学習能力**: 制御工学（PID）、ネットワーク同期、Quaternion数学など、大学で体系的に学んでいない分野を、実装を通じて学習した

### 6.4 マップ生成方式の決定（2026-06-27）

リプレイ性のためのマップ自動生成方式を、Claude × Codex の独立評価・反証ワークフローで決定した。
詳細・全候補・反証・自力再実装チェックリストは `docs/devlogs/2026-06-27_map_generation_decision.md` を正本とする。

**決定（要約）:**
- **生成:** 手作り部屋モジュールをシードで連結（B1/B5）＋ 内部ランダム化（D1）＋ 大構造グラフ変化。完全 procedural（WFC/ノイズ/セルラー）は不採用。
- **同期:** ホストがレイアウトを確定し、最小マニフェスト（version＋seed＋moduleList＋checksum）を `[Networked]` 状態として配布（E2）。地形は各クライアントが**非ネットワークでローカル Instantiate**（NetworkObject 化しない）。動的物（宝/敵/スポーン）のみ既存 `runner.Spawn` 経路。
- **ナビ:** NavMesh 非依存。モジュール埋め込みパスグラフ ＋ 局所ステアリング ＋ A*（敵AIはホスト権威なのでクライアント側ナビ一致は不要）。プレイヤーの動的垂直は AffordanceLink（スマートオブジェクト）で動的グラフ辺。
- **モンスター:** 「物理相互作用する AI 制御キャラ」（押せる/落とせるが意思決定は物理任せにしない）。フル ragdoll AI にはしない。

**決定理由の核:** UX の主役は共有物理の協力ドタバタであり地図は舞台。手作りモジュールは Lethal レベルの十分なランダム性を、公平性・物理安全・徘徊可能性を保証しながら最小コストで実現する。E2 を選んだのは「過去の RegisterSceneObjects 全スポーン死を踏まえ、同期の責任点（決定論再現の負荷）を増やさない」ため。

**実装時の必須ガード（devlog §9 参照）:** LayoutReady ゲート / 離散配置厳守 / 非ネット地形は完全静的 / prefab checksum / 継ぎ目 bridge collider＋CCD / F4 最大試行＋fallback / 起動時検証ダッシュボード。

> 注: §1.3 のスコープ（当初「ダンジョン実装しない」技術デモ）から、コンセプト探索フェーズを経てランダムマップ生成を正式採用する方向へ更新。実装は MVP 順序（devlog §11）に従う。

**実装進捗（MVP 順序）:**
- ✅ **段階A: B1 純粋生成コア**（決定論連結＋衝突＋F1 連結検証＋F4 fallback）。`Assets/Code/Scripts/Map/`。devlog: `2026-06-27_map_generation_decision.md`。
- ✅ **N1 埋め込みパスグラフ**（NavMesh 非依存・clearance 別 A*・幾何から再構築）。devlog: `2026-06-27_map_path_graph_n1.md`。
- ✅ **段階B: Unity 層**（`ModuleDefinition`/`ModuleCatalogAsset` SO ＋ `MapBuilder` でローカル Instantiate。prefab 未割当はプレースホルダ）。確認シーン `Assets/Level/Scenes/MapBuilderSandbox.unity`。devlog: `2026-06-28_map_builder_unity_layer.md`。
- ⏳ **段階C: ネットワーク**（`[Networked]` manifest 配布 ＋ LayoutReady ゲート。`MapBuilder.Build` の入口を manifest 再構築へ差し替え。PlayerSpawner 波及あり）。
- ⏳ D1 デコレーション / 実 Collider 検証 / AffordanceLink（縦移動）は後段。

**目標像の精緻化（2026-06-28 ヒアリングで確定。実装は段階C後）:**
現状の MVP（一本道ツリー・平面・整然モジュール）から、最終的に次を目指す:
- ✅ **接続: 網目状**（ループ・合流が多数）。実装済み（2026-06-28, `LoopConnections`）。ツリー生成後に「開いた口どうしを空きセル BFS で廊下掘りして閉路を追加」。質向上(スコア選択/密度バイアス/分岐DeadEnd延期/通路ピース実体化, Codex実装+Claude検証)で zeroLoopRate 25%→10%。devlog: `2026-06-28_map_loop_generation.md`, `2026-06-28_map_loop_quality_codex.md`。
- **生成方式: ツリー先行＋ループ後付け（BFS廊下掘り）を維持（2026-06-29 確定）。** 競合調査（`2026-06-29_map_generation_competitor_study.md`）の結論: R.E.P.O. 式「グリッド隣接でループを自然発生（＝旧"案B"グラフ先行）」は全域が等距離の網になり**深さ勾配が溶ける**。下記の深さベース risk/reward を成立させるには「入口からのグラフ距離＝深さ」を生成過程で確定するツリー先行が機能的に正しい。**縦移動のための案B全面移行は不要かもしれない**（階段/吹き抜けを辺としてツリーに含め、ループは階内/階間で後付け）。階層実装着手時に再評価。
- ✅ **出入口と深さ勾配: メイン出入口1（Start）＋裏口1〜2（`ModuleRole.Exit`）。** 実装済み（2026-06-29）。深さ＝Startからのグラフ距離（`MapPathGraph.ComputeModuleDepths`）。裏口はループ後の残り開口の最深へ配置。予約1で「裏口≥1」を保証しつつ網目を最大化（裏口0率0/50・ループ0率12%維持）。devlog: `2026-06-29_map_backdoors_and_depth.md`。
- ✅ **(C) 深さ→宝/敵スポーン重み付け（純コア）。** 実装済み（2026-06-30）。`MapSpawnPlanner`（純コア・EditMode 7件緑）が `ComputeModuleDepths` を消費し、線形スケール（value=base+slope*depth、配置確率も深さ比例）で決定論的な `SpawnPlan`（どのモジュール/セルに宝tier・敵threatをいくつ）を計算。Instantiate / `[Networked]` 配布 / 敵プレハブは次スライス。目視で判明した別軸課題=裏口深さのぶれ(A)・網目密度の薄さ(B)はマップ規模拡大時に再評価。devlog: `2026-06-30_map_spawn_planner.md`。
- ✅ **(C-配線) 宝スポーン配線（ホスト Spawn）。** 実装・コンパイル確認済み（2026-06-30、ホスト/クライアント実機テストは未）。`MapTreasureSpawner`（NetworkBehaviour）がホスト権威で `MapSpawnPlanner` のプランを消費し、宝 `NetworkObject` を `Runner.Spawn`→Fusion が自動配布。地形（manifest ローカル再生成）と違い NetworkObject はホスト Spawn が正配線（既存 PlayerSpawner.SpawnWorldObjects と同型）。敵は未存在のため対象外、`Value` はスコア系未存在のため未適用。残: 宝prefab登録＋実機テスト。devlog: `2026-06-30_map_treasure_spawn_wiring.md`。
- **見た目: ハイブリッド**（手作り部屋＋有機的通路）。完全 procedural（WFC/セルラー）は §6.4 決定どおり原則不採用だが、通路の有機性で折衷する余地を探る。
- **立体: 基本は階層フロア＋部分的な崖**（Q2 案3）。Y レベル＋階段モジュール＋要所の崖/吹き抜け。
- **微起伏（運搬ゲーム強化）: アクティブラグドールが Rigidbody を運ぶ特徴を活かし、進行不可にならない程度の起伏を地面に持たせる。**
  - **実装方針（決定）: モジュール埋め込み（authored relief）を第一候補**。理由: 同期モデル（manifest＋ローカル決定論生成）に追加リスクゼロ、起伏は運搬スケール＝モジュール局所、傾斜の安全（詰み・ラグドール不安定の回避）を目視で保証しやすい。
  - 手続き的変化が欲しい場合は **「離散整数ハイトオフセット」**（seed 決定の小段差＋authored ステップ片）で決定論・checksum 可能性を保つ。
  - **ランタイムの浮動小数ハイトマップ変形は不採用**（§9 離散配置厳守と矛盾、float のプラットフォーム差で物理デシンクの火種）。
  - いずれも **段階C 完了後**。段階C 中は地面フラットで同期のみ検証する。

### 6.5 開発ツールの先行整備（2026-07-02）

Fable 5 の従量課金化（2026-07-08〜）を前に、統合事故を「起きた後に診断」から「起きる前に検出」へ移すツール群を整備した。

- **Preflight Check（統合前チェック）**: `Tools > REBAKA > Preflight Check`。過去事故（stray config / シーン未登録 / 二重スポーン等）に対応する6チェックを3値判定（Pass/Warn/Fail）で実行。誤緑（false pass）回避を設計原則とし、判定純関数は EditMode テスト20件で固定。運用手順は `.claude/skills/integration-preflight/`。devlog: `2026-07-02_preflight-checker.md`。
- **NetworkDebugHud（同期デバッグHUD）**: Play 中 F1 で表示される read-only オーバーレイ。Runner 情報（Host/Client・Tick・RTT）、NetworkObject 毎の入力/状態権限・kinematic 状態、グラブ状態を両ピアで可視化。2-client 検証手順は `.claude/skills/two-client-verification/`。devlog: `2026-07-02_network-debug-hud.md`。

### 6.6 ダメージシステムの設計決定（2026-07-06）

「ダメージも全部物理」コンセプトのダメージシステムを対話レビューで設計確定した（実装前）。
詳細・比較表・不採用案・自力再実装チェックリストは `Docs/devlogs/2026-07-06_damage-system-design.md` を正本とする。

**決定（要約）:**
- **式:** 各自が受けるダメージ = 衝突力積 J（`Collision.impulse`）× 接触ペア鋭さ S ÷ 自分の硬度。**S = max(双方の鋭さ)**（sum は剣戟を外す本作で発揮場面がなく不採用）。ダメージ < 1.0 は無傷、同一ペアにクールダウン。
- **プロパティ分離:** 鋭さ・硬度＝全コライダー（コライダー単位で可変。剣＝刃5/柄1）。耐久値＝壊れる物だけ（地形には付けない）。
- **プレイヤー:** 耐久値1つ＋部位別硬度（頭＝低）。防具＝将来の部位硬度上げ。
- **エネミー:** 高硬度・高耐久で「武器無双」をステータスで防止（物理の創意工夫のみ有効）。リスポーン＝コスト容量式（LC 参考、時間で容量増＝帰還圧力）。スポーン地点は物理実体（塞げる／バリケードは本システムで破壊される）＋物理的予兆でテレグラフ。背後湧き禁止。
- **ネットワーク:** ホストがダメージ計算、`[Networked]` 耐久値のみ同期（衝突イベントは非レプリケーション）。通信コストは transform 同期比で誤差レベルの見積もり（実測は未）。

---

## 7. 技術理解Q&A

> **想定される技術的な質問と、その回答を整理します。** これらの質問に説明できる状態を目指します。

### 7.1 技術理解（理論レベル）

#### Q1: なぜPhoton Fusionを選んだのですか？
**回答:**
マルチプレイヤーフレームワークの選択肢としてMirror、Netcode for GameObjects、Photon Fusionを比較しました。Photon Fusionを選んだ最大の理由は、物理演算の同期に対する公式サポートが最も充実していたことです。特にFusion 2.1のForecast Physicsは外挿ベースの物理同期を提供しており、13パーツのラグドールを同期するのに適していると判断しました。Preview版のリスクはありましたが、Gitでバックアップを取った上で、根本解決できる方を選びました。

#### Q2: NetworkRigidbodyはどういう仕組みで動いていますか？
**回答案:**
NetworkRigidbodyは、Rigidbodyの位置・回転・速度をネットワーク越しに自動同期するコンポーネントです。State Authority（物理演算の実行権を持つ側）で物理演算を実行し、その結果をサーバー経由で他のクライアントに配信します。当初はラグドール13パーツ全てにNetworkRigidbodyをアタッチしていましたが、ルートを親から切り離す `DetachRootFromParent()` と NetworkRigidbody（NetworkObject 階層が不変である前提）が非互換で床すり抜けが起きたため、**プレイヤーからは NetworkRigidbody を削除し、ルート・頭・手だけをカスタム同期（`[Networked]` プロパティ + スナップショット）する方式**に変えました。NetworkRigidbody は現在 Obs_Cube のようなピア同期物体にのみ使い、そこもプロキシを kinematic にする純補間サブクラス `GameNetworkRigidbody` に置き換えています。「フレームワークの自動同期が常に最適とは限らず、階層を動的に変える設計とは相性が悪い」ことを学びました（§4.7 参照）。

#### Q3: State Authorityとは何ですか？
**回答案:**
State Authorityは「このネットワークオブジェクトの正しい状態を誰が持っているか」を示す概念です。通常はホストやサーバーがAuthorityを持ちます。Authority側では物理演算やゲームロジックを実行し、その結果が「正解」として他クライアントに配信されます。重要なのは、`Object.HasStateAuthority` を確認してからネットワーク状態を変更することで、複数クライアントが同時に状態を書き換える競合を防ぐことです。

### 7.2 問題解決（実践レベル）

#### Q4: 物理演算の同期で一番苦労した点は？
**回答案:**
一番苦労したのは、ローカルでは完璧に動くラグドールが、ネットワーク越しだとガタガタ震えたり手足が吹っ飛んだりした問題です。Fusion 2.0.xのPhysics Addonでは、再シミュレーション方式の物理同期を使っていましたが、13パーツのラグドールでは再シミュレーションの精度が足りず、関節の拘束が正しく再現されませんでした。当初はFusion 2.1のForecast Physics（外挿ベース）に移行しましたが、A/Bテスト（2026-03-28）で外挿のスナップバックが残ることが分かり、**最終的には「プロキシ側の Rigidbody を kinematic にしてローカル物理を完全に止め、ホストのスナップショットを補間して描画するだけ」の純補間方式**に落ち着きました。外挿そのものを発生させず、ホスト権威と素直に整合するこの方式が実機で勝ちました（§3.3 採用方式 / §4.8 参照）。

#### Q5: どのような問題が発生し、どう解決しましたか？
**回答案:**
代表的な問題を3つ挙げます：
1. **ラグドールの振動**: PID制御のデッドゾーンが小さすぎて微小な傾きにも反応し続けていた → デッドゾーンを5度に拡大し、PID積分項をリセットする処理を追加
2. **向きが勝手に戻る**: ConfigurableJointのtargetRotation計算でQuaternion.identityを使っていたため、常にワールドZ+方向に戻ろうとした → スポーン時の回転を保存し、`Inverse(spawnRotation) * targetWorldRotation` で正しく計算
3. **スポーン直後のクラッシュ**: NetworkBehaviourの`Spawned()`前にネットワークプロパティにアクセスしていた → 全初期化処理をSpawned()内に移動

#### Q6: ネットワークラグの影響をどう対処しましたか？
**回答案:**
物理演算はホスト（State Authority）でのみ実行し、クライアントのプロキシは Rigidbody を kinematic にして自前では物理を回さず、ホストから届くスナップショットを**補間して描画するだけ**にしています。これにより、ネットワーク遅延があってもプロキシが勝手に外挿で先走ってスナップバックする、という問題が原理的に発生しません。ホストモードのプロキシは Remote タイムフレームに固定し（`ForceRemoteRenderTimeframe`）、スナップショット履歴の異なる2点を Lerp することで滑らかさと「プレイヤーと同一遅延」を両立しています（§4.8）。検討の経緯としては Forecast Physics（外挿）も試しましたが、外挿のスナップバックが残ったため純補間方式を選びました。入力はNetworkInputDataとして同期し、Authority側で確定的に処理することで入力の順序を保証しています。

### 7.3 AI協働（姿勢レベル）

#### Q7: AIを使ったとのことですが、どの部分を自分で理解していますか？
**回答案:**
自信を持って説明できる部分として、State Authorityの概念、PID制御の各項の役割（Pが即座の反応、Dがブレーキ）、ConfigurableJointのtargetRotation計算（Joint空間とワールド空間の変換）、NetworkBehaviourのSpawned()ライフサイクルがあります。逆にまだ不十分な部分は、PIDパラメータの最適チューニング方法やForecast Physicsの内部アルゴリズムです。理解が不十分な部分を正直に認識し、今後の学習課題として把握しています。

#### Q8: AIが書いたコードをどのように学習しましたか？
**回答案:**
主に3つのアプローチで学習しました。第一に、AIが生成したコードを1行ずつ読んで、各関数の役割を理解しました。特にRagdollPhysics.csのApplyUprightForce()やUpdateRootTargetRotation()は重点的に読みました。第二に、「なぜこの計算が必要か」をAIに質問して理解を深めました。例えば「Quaternion.Inverseをなぜ使うのか」を質問し、Joint空間とワールド空間の変換の必要性を理解しました。第三に、問題が発生した際に原因を自分で推測してからAIに確認するプロセスを繰り返し、受動的な理解ではなく能動的な学習を心がけました。

#### Q9: 自分で解決した部分と、AIに任せた部分の違いは？
**回答案:**
AIに任せたのは主にコードの実装部分（物理制御のロジック、ネットワーク統合のコード等）です。一方、自分で判断・解決した部分は設計レベルの意思決定です。例えば、Fusion 2.0.xから2.1への移行を決断したのは自分です。「壊れたシステムを修理するか、新しいシステムに移行するか」というトレードオフを比較し、リスク（Preview版の不安定さ）を受け入れる判断をしました。また、問題の発見と症状の特定も自分の役割です。「ラグドールが震えている」「向きが勝手に戻る」という具体的な問題をAIに正確に伝えることで、適切な解決策を得られました。

---

## 8. 参考資料

### 8.1 公式ドキュメント
- [Photon Fusion Documentation](https://doc.photonengine.com/fusion/current/getting-started/fusion-intro)
- [Unity Physics Documentation](https://docs.unity3d.com/Manual/PhysicsSection.html)

### 8.2 参考記事・チュートリアル
- APR Player（Active Ragdoll）: サードパーティアセットのサンプルコードを参考に物理制御を設計
- Photon Fusion公式ドキュメント: 基本概念の確認に使用
- Unityリファレンス: ConfigurableJoint、Rigidbody等の公式リファレンス

### 8.3 コードリーディングメモ

**最も重要な3つのメソッド（説明できるように）:**

1. `RagdollPhysics.ApplyUprightForce()` (行798-836): PID制御で直立トルクを計算。デッドゾーン5度、Y軸除外、バランス優先度でスケール
2. `RagdollPhysics.UpdateRootTargetRotation()` (行337-356): Joint空間でのQuaternion計算。`Inverse(spawnRotation) * targetWorldRotation`
3. `RagdollController.FixedUpdateNetwork()` (行144-163): 入力受信→状態更新→物理更新のメインループ

---

## 📝 更新履歴

| 日付 | 更新内容 |
|------|---------|
| 2026-01-30 | テンプレート作成 |
| 2026-02-05 | Section 1-4, 7のTODOを埋める（コード分析・devlog・SPECから抽出） |
| 2026-02-05 | Section 5-6をヒアリング結果をもとに正直な現状で記載 |
| 2026-02-12 | Section 4.5: APR_Root原点引力バグの修正を追加 |
| 2026-06-22 | Section 4.8: クライアントプロキシ描画同期バグ2件（グラブ隙間/押し先行・カクつき）を追加 |
| 2026-06-27 | Section 6.4: マップ生成方式の決定（手作りモジュール連結×ホスト配布×自前ナビグラフ）を追加 |
| 2026-06-28 | Section 6.4: 実装進捗を追記（段階A / N1 パスグラフ / 段階B Unity 層 完了） |
| 2026-06-28 | Section 6.4: 目標像の精緻化を追記（網目状ループ / ハイブリッド / 階層＋崖 / 運搬向け微起伏はモジュール埋め込み方針）。実装は段階C後 |
| 2026-06-28 | Section 6.4: 段階C-1（[Networked]配布）2ピア検証済み、網目状ループ生成を実装済みに更新 |
| 2026-07-09 | Section 4.10: ジャンプができたりできなくなったりするバグ（ラッチ固着）の修正を追加 |
| 2026-06-29 | Section 6.4: 競合調査(LC=DunGenツリー / R.E.P.O.=モジュールグリッド網目)を反映。生成方式=ツリー先行＋BFS後付け維持(深さ勾配の根拠)、出入口=メイン1＋裏口1〜2の深さベースrisk/reward、縦=モジュール焼き込みを記録。devlog: `2026-06-29_map_generation_competitor_study.md` |
| 2026-06-29 | Section 6.4: 裏口(ModuleRole.Exit)＋深さメトリクス実装済みに更新(Codex実装+Claude検証/予約1チューニング, EditMode 66/66)。devlog: `2026-06-29_map_backdoors_and_depth.md` |
| 2026-06-30 | Section 6.4: (C)純コア MapSpawnPlanner（深さ→宝/敵重み付け, EditMode 7件）と宝スポーン配線 MapTreasureSpawner（ホスト Spawn＋Fusion 自動配布）を実装済みに更新。devlog: `2026-06-30_map_spawn_planner.md` / `2026-06-30_map_treasure_spawn_wiring.md` |
| 2026-06-29 | Section 3.1/3.3 に現状差分ノート追加、Section 7 Q2/Q4/Q6 を現行アーキテクチャ（純補間kinematicプロキシ＋カスタム同期, プレイヤーNRB削除済）に更新。Forecast Physics / 全パーツNetworkRigidbody前提の旧記述が「現在の最終形」として残っていた矛盾を解消（§3.3採用方式 / §4.7 / §4.8 と整合） |
| 2026-06-29 | 操作スキーム反転: マウス→Body直接操作(X=ヨー/Y=胴体ベンド+腕リーチ, APR可動範囲±0.9/±1.2準拠)、カメラはマウス非依存の三人称自動追従+スプリングアーム衝突回避(UE5風)。視点入力は resim 決定論のため**絶対値**で送出(デルタ禁止)。devlog: `2026-06-29_mouse-body-control-and-spring-arm-camera.md` |
| 2026-07-01 | Section 4.9: 掴み腕の肘折れ問題を追加。角度limit(壊れ防止の壁・prefab側)とJointDrive maxForce(重さへの降伏・profile側)で役割分担し解決。可動域可視化Editorツール`JointLimitVisualizer`を新規作成。devlog: `2026-07-01_grab-arm-elbow-limit-and-yield.md` |
| 2026-07-06 | Section 6.6: ダメージシステムの設計決定（力積×max鋭さ÷硬度、プロパティ/耐久分離、コスト容量式リスポーン、ホスト計算＋耐久値のみ同期）を追加。devlog: `2026-07-06_damage-system-design.md` |
| 2026-07-09 | Section 4.9関連: 2クライアント実機報告(掴み外れ/装飾発散/埋没/物理破綻)を受けた掴みジョイント再設計。ConfigurableJoint(Free+Drive)→Limited+slack→最終的にFixedJoint統一(genericGrabBreakForce=1400fで破断)に帰着。APR_RootのCollisionDetectionMode Discrete→ContinuousDynamicも実施したが単独では発散を防げず、FixedJoint統一とbreakForce調整で解決。Treasure_Heavyは専用の質量分配システムを外し一般オブジェクト化(mass=80)。意図しない体幹弱化コード(`carryLegDriveMaxForce`)を撤去。ParrelSync+Gamepad分離で2同時操作の検証基盤を整備。実機検証済み。devlog: `2026-07-09_grab-joint-stability-and-carry-drive-cleanup.md` |
| 2026-07-11 | Editor編集可能なSettingsMenu.prefabをTest_Playgroundへ配置。UI/ToggleSettings (Escape / gamepad Start)で開閉し、2タブとScrollRect、感度、KBM/Gamepad別リバインド、ゲームプレイ入力デバイス有効切替、Leave Session、QUITを集約。Player mapだけをmaskしUI mapは常時操作可能、両デバイス無効は禁止。compile error 0、EditMode 9件、Escape開閉Play Modeを確認。devlog: 2026-07-11_settings-menu.md |
| 2026-07-11 | SettingsMenu UX: Escape と UI/Cancel の二重発火を分離（Esc は ToggleSettings 専有）。タブは Select でページ切替、フッター Reset/Close は Explicit 左右ナビ。感度 `*SliderValue` に InputField を実行時付与して数値直接入力。`??=` を Unity 参照解決から除去。 |
| 2026-07-12 | SettingsMenu: ControlsPage の SavedMessage で保存成功／失敗を表示。同期保存のちらつき回避のため最短表示時間（既定2秒）＋連続保存時はタイマー延長。 |
| 2026-07-12 | Section 1.2: 「Forecast Physicsで対処」の旧記述を現行方式（ホスト権威＋kinematic純補間プロキシ）へ更新。2026-06-29整合パスの残り1箇所を解消 |

---

> **Phase 2 の目標:**
> 2/14までに、このドキュメントの [TODO] をすべて埋め、技術的な質問に答えられる状態にする。
