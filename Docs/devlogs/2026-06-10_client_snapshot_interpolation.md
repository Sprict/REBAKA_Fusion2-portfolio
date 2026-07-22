# クライアント側ラグドール表示を全身ポーズ・スナップショット補間へ移行

> **公開時注記（2026-07-22）:** 全身15パーツをState Authorityの確定ポーズから補間表示する判断は現行方式です。一方、この資料は6月10日〜12日の失敗した仮説、撤回した実装、装飾Rigidbodyの追加調査まで時系列で残した長い開発記録です。現在の構成を先に知りたい場合は[`ARCHITECTURE_OVERVIEW.md`](../ARCHITECTURE_OVERVIEW.md)を参照してください。

日付: 2026-06-10
ブランチ: feature/concept-v3-spec
関連: ハードスナップ、四肢ドリフト、2026-04-11 ボトルネック分析、2026-03-27 Forecast A/B テスト

## 問題

クライアント画面のアクティブラグドールがホスト画面ほどスムーズに見えず、商用レベルに達していなかった。

コード調査で特定した4つの根本原因:

1. **Render() での補間が存在しない** — クライアント補正（`ClientProxyCorrection`）は
   `FixedUpdateNetwork`（tick ドメイン）でのみ実行され、`RagdollClientProxyRuntime.RunRender()` は
   Hybrid モードではほぼ空だった。描画フレームレート（144Hz 等）> tick レート（60Hz）の環境では
   tick 間の動きが階段状に見える。
2. **[Networked] プロパティの生読み** — クライアントは `NetRootPosition` 等を直接読んでいた。
   [Networked] プロパティの生読みは「最後に受信したスナップショットの値」を返すため、
   パケット到着ジッタ（ネットワークの揺らぎ）がそのまま追従ターゲットのガタつきになる。
   Fusion が提供するスナップショット補間 API（`TryGetSnapshotsBuffers`）は未使用だった。
3. **同期は Root/Head/両手の4パーツのみ** — 残り11パーツはクライアントローカル物理
   （ジョイント駆動）で動かしており、PhysX の非決定論性により発散。
   発散が1mを超えるとハードスナップ（瞬間テレポート）が発生していた。
4. **MoveTowards の速度クランプ** — `max(hostSpeed*1.5, 3m/s)` でターゲットを追うため、
   急加速やジッタ時に「遅れて追いつく」ラバーバンド感が出ていた。

## なぜこのアプローチか（検討した代替案）

ユーザー要件: **遅延は許容、スムーズな見た目が最優先**。

| 選択肢 | 判断 | 理由 |
|---|---|---|
| **全身ポーズのスナップショット補間（採用）** | ✅ | 遅延を許容するなら商用ゲームの定石。遅延した2つの確定スナップショット間を純粋に視覚補間するため、原理的にガタつかない。物理を使わないのでクライアント CPU 負荷も減る |
| Hybrid 方式のパラメータ調整 | ❌ | 根本原因（補間なし・生読み・部分同期）が残るため、どう調整しても「物理追従の揺れ」は消えない |
| Forecast Physics（クライアント予測） | ❌ | 2026-03-27 の A/B テストで棄却済み。ラグドールはカオス系（初期値鋭敏性）でクライアント予測が構造的に発散する |
| Photon Fusion を捨てて自前同期 | ❌ | Fusion 自体は問題ではない（問題はクライアント側の表示戦略）。リライトはコスト過大で、同じ表示問題を自前で解き直すことになるだけ |

### 設計のキーアイデア

- [※理論] スナップショット補間（snapshot interpolation）は Valve の Source エンジン等で
  確立された手法。受信済みの確定状態 2 つの間を描画時刻で補間するため、
  「未来を当てる」予測と違い、誤差・巻き戻し・スナップが原理的に発生しない。
  対価は補間バッファ分の表示遅延（数十ms）。
- 14パーツは **Root 相対**（`relPos = inv(rootRot) * (part.pos - rootPos)`）で送る。
  ワールド座標で送るより、Root の移動・回転と各部位のローカルな動きが分離され、
  Root 補間と合成したときに姿勢が崩れない。
- テレポート対応は `NetworkRigidbody` の TeleportKey と同じパターン:
  キーが from/to で異なる場合は補間せず from に留める。

## 仕組み

### データフロー

```
ホスト (StateAuthority)
  RagdollHostSimulationOrchestrator.RunFixedUpdate()
    └→ RagdollProxyPosePublisher.Publish()  [毎 tick]
        ├→ DetectTeleport(): 1tickで2m超のRoot移動 → NetPoseTeleportKey++
        ├→ 既存: NetRootPosition/Rotation 等（Root ワールド姿勢）
        └→ 新規: PublishRelativePartPoses()
            └→ NetPartPositions[0..13] / NetPartRotations[0..13]
               (bodyRigidbodies[1..14] の Root 相対ポーズ)
                    │ Fusion スナップショット配信
                    ▼
クライアント (非StateAuthority, ProxySyncMode.SnapshotInterpolation)
  FixedUpdateNetwork/BeforeTick: ForceClientKinematic() のみ（物理停止）
  Render() [毎描画フレーム]
    └→ RagdollSnapshotPoseInterpolator.RunRender()
        ├→ TryGetSnapshotsBuffers(out from, out to, out alpha)
        ├→ TeleportKey 不一致 → to=from, alpha=0（補間スキップ）
        ├→ Root: Lerp/Slerp → transform.SetPositionAndRotation
        └→ 14パーツ: 相対ポーズを Lerp/Slerp → Root と合成してワールドへ
```

### 重要な実装ポイント

1. **PropertyReader / ArrayReader**（`RagDollController.EnsureSnapshotReaders`）
   - [Networked] プロパティの生読みではなく、`GetPropertyReader<T>(nameof(...))` /
     `GetArrayReader<T>(nameof(...))` で from/to バッファから読む。
     `PropertyReader<T>.Read(from, to)` は (fromValue, toValue) のタプルを返す。
   - リーダーは Spawned 後に一度だけ初期化してキャッシュ（名前引きのコストを毎フレーム払わない）。
2. **NetworkRigidbody の無効化**（`SnapshotInterpolationClientProxyModeStrategy`）
   - APR_Root には `Fusion.Addons.Physics.NetworkRigidbody` が付いており、その Render() も
     同じ transform にスナップショット補間結果を書く（二重書き込み）。
     本モードでは `enabled = false` にして当方の interpolator を唯一の書き込み源にする。
   - 副作用が出た場合のフォールバック: `[OrderAfter(typeof(NetworkRigidbody))]` で上書き勝ち。
3. **transform 直書きで安全な理由**
   - 全 Rigidbody は kinematic で、クライアントはこのラグドールの物理結果を一切使わない。
     kinematic RB の transform 移動は次の物理ステップで PhysX に同期されるだけで副作用なし。
     NetworkRigidbody 自身も interpolationTarget 非設定時は transform 直書き（NetworkRigidbody.cs:553 付近）。
4. **zero quaternion ガード**
   - 接続直後のスナップショットは未初期化（全ゼロ）の可能性があるため、
     `sqrMagnitude < 0.01` の quaternion は identity にフォールバック。
   - さらに `NetProxyPoseInitialized`（to バッファから読む）が false の間は描画しない。
5. **A/B 共存**
   - `RagdollProfile.proxySyncMode` enum（Hybrid / SnapshotInterpolation / Forecast）で切替。
   - 旧 `useForecastPhysics` は `ResolveProxySyncMode()` で後方互換（既存アセットを壊さない）。
   - SnapshotInterpolation 時のみ全身ポーズを発行し、Hybrid 時の帯域増加を避ける。

### 帯域影響

- 追加: 14 × (Vector3 12B + Quaternion 16B) + key 4B = **+396B/tick ≈ +24KB/s/人**（60Hz 最悪値）
- 現状ベースライン 9.8KB/s → 約 34KB/s/人。4人で ~136KB/s。プロトタイプでは許容。
- 超過時の対策（未実装・設計済み）: quaternion smallest-three 圧縮（14×16B → 14×4B）で
  回転を 1/4 に削減。位置の half 化は第二段。

## 変更ファイル

| ファイル | 変更 |
|---|---|
| `RagdollProfile.cs` | `ProxySyncMode` enum + `proxySyncMode` + `ResolveProxySyncMode()` + `poseTeleportDetectThreshold` |
| `RagDollController.cs` | NetworkArray×2 + `NetPoseTeleportKey` + リーダーキャッシュ + `IPoseSnapshotAccess` 実装 + `RequestPoseTeleport()` + モード分岐 |
| `RagdollControllerContracts.cs` | `RagdollPoseSync` 定数 + `IPoseSnapshotAccess` + publisher/runtime/rig コンテキスト拡張 |
| `RagdollProxyPosePublisher.cs` | 相対ポーズ発行ループ + テレポート自動検出 |
| `RagdollClientProxyRuntime.cs` | SnapshotInterpolation 分岐（FUN/BeforeTick=kinematic維持、Render=補間） |
| `RagdollSnapshotPoseInterpolator.cs` | **新規**: 補間本体 |
| `SnapshotInterpolationClientProxyModeStrategy.cs` | **新規**: クライアント初期化戦略 |
| `ClientRagdollKinematicGuard.cs` | モード対応（SnapshotInterpolation では enforcement） |
| `MyRespawn.cs` | `RequestPoseTeleport()` 呼び出し |
| `SyncMetricsRecorder.cs` | M8（描画スムーズさ CV）追加 + M6 帯域推定のモード対応 |

## 検証手順

1. Unity でコンパイル確認（NetworkArray<Quaternion> の weaver 対応はコンパイルで判明。
   NG なら NetworkArray<Vector4> + 手動変換にフォールバック）
2. RagdollProfile アセットの `proxySyncMode` を `SnapshotInterpolation` に変更
3. ホスト+クライアント2インスタンスで: 歩行・ジャンプ・パンチ・転倒・リスポーンを目視
4. `SyncMetricsRecorder` 60秒: M1 PASS 必須、M8 の CV を Hybrid と比較（小さいほどスムーズ）
5. ログ確認: `proxy_pose_snap`（ハードスナップ）が出ないこと、
   `[RAGDOLL_CLIENT_MODE] mode=snapshot_interpolation` が出ること

## 追記（2026-06-11): 装飾 Sphere 分離バグの修正

初回 2 インスタンス検証で、クライアント画面の装飾用 Sphere（ぷるぷる揺れる球、
bodyRigidbodies 外の 12 RB）が本体から分離して見えるバグが発覚。

- 原因: 全 27 RB を kinematic 化していたため、ポーズ同期対象の 15 パーツ以外は
  誰にも transform を書かれず、Transform 階層の親（APR_Root）に固定されたまま
  補間で動く本体から取り残された
- 修正: kinematic 化を `PoseDrivenRigidbodies`（15 パーツ）に限定。Sphere は
  クライアントでも dynamic + 重力ありのまま残す。ジョイントで kinematic な本体に
  繋がっているため、補間で動く本体に追従して揺れる
  （ホスト相当の二次運動を帯域ゼロでローカル再現）
- 併せて SnapshotInterpolation の strategy からジョイントドライブ無効化を撤去
  （kinematic パーツ間のドライブは元々無効果。Sphere の揺れ用スプリングは必要）
- 教訓: 「全 RB kinematic 化」はポーズを同期している RB にだけ正当。
  同期対象外の装飾 RB はローカル物理に残すのが正しい

### 再修正（同日）: dynamic 化だけでは不十分だった

実機確認で分離が解消されなかった。装飾 Sphere は `APR_Root/Other/` 配下
= **補間が毎 Render フレーム書き換える APR_Root の Transform 階層の子**であり、
親 Transform のテレポートに引きずられて PhysX 上でも毎フレーム姿勢を上書きされ、
dynamic でもローカル物理が殺されていた（揺れない・ルートに張り付く）。

- 修正: 補間書き込みの**前に装飾 RB のワールド姿勢を退避、書き込み後に復元**
  （正味の Transform 変更ゼロ → PhysX への干渉ゼロ）。ジョイントは Rigidbody 参照で
  階層と無関係のため、kinematic な本体に追従して揺れる
- 設計判断: 装飾はゲームプレイに影響しないためホスト・クライアント間の状態一致は不要。
  各ピアのローカル物理で揺らす（帯域ゼロ、ユーザー合意済み）
- 教訓: 「transform 直書き補間」と「物理駆動の子オブジェクト」は同じ階層に共存できない。
  書き込み対象外の物理オブジェクトが階層内にいる場合は退避/復元か unparent が必要

### 根本原因の確定（2026-06-12): Spawned 時の kinematic 初期化が真犯人

3 回の対症修正（dynamic 化 enforcement、stash/restore、Render 経路自己修復）でも
クライアントの Sphere が揺れず、ユーザーの Inspector 観測で「クライアント側だけ
isKinematic がオン」と確定。コードを総点検した結果:

- **真犯人**: `RagdollRigInitializer.InitializeRigidbodies()`。非権限ピアの Spawned 時に
  `GetKinematicTargetRigidbodies()`（= 階層全体の 27 RB）を一括 kinematic + 重力オフ化していた
- 「毎 tick 上書き」ではなく **初期化 1 回**だった。SnapshotInterpolation モードで装飾を
  kinematic に戻すコードは他に存在しない（`ClientRagdollKinematicGuard` は SnapshotInterpolation
  では 15 パーツ限定だが、そもそもどのプレハブ/シーンにもアタッチされていない死にコードと判明）
- dynamic に戻すはずだった `EnforceSnapshotPhysicsState()` は FUN / IBeforeTick 経路にあり、
  [※推測] プロキシ（非シミュレート対象）ではこれらのコールバックが呼ばれず一度も実行されなかった

修正（根本）: `IRagdollRuntimeHost.InitializeRigidbodies()` で、SnapshotInterpolation の
クライアントなら kinematic 初期化の対象を `bodyRigidbodies`（15 パーツ）に限定。
装飾 12 RB はプレハブの dynamic + 重力あり設定のまま一度も殺さない。
Render 経路の `EnsureDecorationsDynamic` は自己修復の保険として残置し、
decorations=0 の場合もログを出すよう切り分け欠陥を修正。

- 教訓: 「戻す側を増やす」対症修正を重ねる前に、**最初に状態を壊す側**を grep で全列挙する
  （`isKinematic = true` の検索 → アタッチ有無の確認まで含めて初めて容疑者リストが完成する）
- 教訓: FUN / IBeforeTick はプロキシで呼ばれる保証がない。クライアント表示系の保証は
  Render 経路か Spawned 時に行う

### 真の根本原因（2026-06-12 確定): プレハブのデータがコードの前提と不一致

前項の「Spawned 初期化が真犯人」も外れだった。ユーザーの Inspector 観測
（クライアントだけ全 RB が isKinematic オン）と `[SNAPSHOT_DECO] decorations=0` ログから
実際にスポーンされている **newAPRPlayer.prefab** を解析して確定:

1. **`bodyRigidbodies` 配列に 27 個全部が登録されていた**（0..14 = 正規 15 ボディ、
   15..26 = 装飾 Sphere 12 個）。コード側のポーズ同期・kinematic 制御・装飾検出は
   すべて「bodyRigidbodies = ポーズ同期対象の 15 個」前提なので:
   - `DecorationRigidbodies` = 全 RB − bodyRigidbodies = 27 − 27 = **0**（装飾検出が全滅）
   - クライアント初期化が「bodyRigidbodies に限定」しても**装飾まで kinematic 化**
   - ポーズ発行/補間はインデックス 0..14 しか書かないため、15..26 は
     **kinematic のままその場に固定 → 本体から分離**
2. **装飾 Sphere (4) ×6 個に Fusion.Addons.Physics.NetworkRigidbody が付いていた**。
   NRB はプロキシ側で RB を kinematic 化して transform を上書きするため、
   「装飾は各ピアのローカル物理」方針と真っ向から矛盾していた

修正（PrefabUtility 経由。Fusion の NetworkObject ベイクを壊さないため YAML 直編集は避けた）:
- newAPRPlayer.prefab の bodyRigidbodies を先頭 15 個に縮小
- Sphere (4) ×6 の NetworkRigidbody を削除（ホスト→クライアント同期は不要、帯域も節約）
- Spawned に bodyRigidbodies.Length != 15 の LogError 検証を追加（再発防止）

- 教訓: コードを 4 回修正しても直らないバグは、**コードではなくデータ（プレハブ/アセット）が
  コードの前提を破っている**ことを疑う。SerializeField 配列の中身は必ず実機プレハブで確認する
- 教訓: 調査対象のプレハブを間違えない。リポジトリ内の同名/類似プレハブ
  （APR_Root.prefab は bodyRigidbodies=15 で正しかった）ではなく、
  **実際にスポーンされるプレハブ**（newAPRPlayer.prefab）を Hierarchy のスクショ等から特定する

### 派生障害（2026-06-12): NRB 削除で物理シミュレーション全体が停止

プレハブ修正（装飾 Sphere の NetworkRigidbody ×6 削除）の後、ホスト単独でも
「全プレイヤーがスポーン時から T ポーズのまま、エラーなしで一切動かない」障害が発生。

調査での決定的な計測: `root.linearVelocity = (0, 0, 10)` なのに position が 1mm も
動かない = 力も状態遷移も正常だが **PhysX のシミュレーションステップが走っていない**。

- 根本原因: 本プロジェクトは `Physics.simulationMode = Script`（SessionManager が設定、
  物理を Fusion の tick に同期させるため）。Script モードでは誰かが `Physics.Simulate()` を
  呼ぶ必要があるが、その役の `RunnerSimulatePhysics` はシーンにもコードにも明示されておらず、
  **`NetworkRigidbody.SetupPhysicsBody()`（Spawned 時）が Runner に自動 AddComponent する
  副作用**でのみ登録されていた
- newAPRPlayer.prefab の NRB は装飾 Sphere (4) の 6 個が全て（Root にも無い）。
  これを削除した瞬間、物理エンジンの起動トリガーが消えた
- 症状が「T ポーズ凍結」なのは: Fusion の tick・入力・ゲームロジックは正常進行する一方、
  ジョイントドライブも重力も移動力も全て PhysX ステップが無いと効かないため

修正: `SessionManager.StartGame` 直後に `EnsurePhysicsSimulation()` で
RunnerSimulatePhysics を明示登録（NRB の自動追加と同じ手順）。
asmdef に Fusion.Addons.Physics 参照を追加。

- 教訓: 「機能 A（物理シミュレーション）が動いている」ことが「無関係に見える部品 B
  （装飾の NRB）の副作用」に暗黙依存していることがある。B を消す前に
  「B の Spawned/初期化が何をしているか」をアドオンのソースまで読んで確認する
- 教訓: 「力は加わっているのに動かない」ときは velocity と position を別々に計測する。
  velocity が出て position が動かないなら物理ステップ自体を疑う（isKinematic ではない）

## 自力再実装チェックリスト

- [ ] [Networked] プロパティの「生読み」と「スナップショット補間読み」の違いを説明できる
      （生読み = 最新受信値でジッタがそのまま見える / 補間読み = from/to バッファ + alpha で時間軸が滑らか）
- [ ] `TryGetSnapshotsBuffers` が返す from/to/alpha の意味を説明できる
      （描画時刻を挟む 2 つの確定スナップショットと、その間の補間係数）
- [ ] なぜ 14 パーツを Root 相対で送るのか説明できる（Root 補間との合成で姿勢が崩れない・値域が小さい）
- [ ] TeleportKey パターンを説明できる（key 不一致 = 瞬間移動を跨ぐ区間 → 補間せず留める）
- [ ] なぜクライアント全 kinematic + transform 直書きで安全か説明できる
- [ ] スナップショット補間 vs 予測（Forecast）のトレードオフを説明できる
      （補間 = 遅延を払って滑らかさと正確さを買う / 予測 = 遅延を隠すが誤差・巻き戻しが出る。
      カオス系のラグドールでは予測が破綻する）
- [ ] NetworkRigidbody との二重書き込み問題と解決策を説明できる

## 追記（2026-06-12）: 装飾 RB のクライアント側微振動と描画補間

報告: クライアント画面でのみ、プレイヤー移動時に Other/ 以下（装飾 Sphere）が
微振動してブレて見え、揺れもホスト画面より固く見える。

### 原因: 描画レートと物理レートの不一致

- 本体 15 パーツ: Render()（毎描画フレーム）でスナップショット補間 → 滑らか
- 装飾 12 RB: ローカル物理 = `Physics.Simulate()`（tick レート ≈ 60Hz）でしか動かない
- さらに本プロジェクトは simulationMode=Script の手動 Simulate のため、
  Rigidbody.interpolation=Interpolate を設定していても **Unity の物理補間は効かない**
  （物理補間は FixedUpdate 自動シミュレーション前提。[※理論]）
- 旧実装の Stash/Restore は「本体への transform 書き込みに引きずられない」ことだけを
  保証する正味ゼロ復元で、装飾の描画は物理ステップそのまま = 階段状更新だった
- 結果: 毎フレーム滑らかに動く本体に対し、装飾だけが tick 刻みで動き、相対的な
  微振動（ジャダー）に見える。ホスト画面では全身が同じ物理ステップで同位相に動くため
  目立たない

### 修正: 物理ポーズ 2 世代バッファによる描画専用補間

`RagdollSnapshotPoseInterpolator` に追加:

1. `OnAfterSimulate`（Physics.Simulate 直後）: 装飾のワールド姿勢を prev/curr の
   2 世代リングバッファに記録（参照スワップで alloc ゼロ）
2. `RunRender`: 本体書き込み後、prev→curr を「最後のステップからの経過時間 / ステップ間隔」
   の alpha で Lerp/Slerp した描画姿勢を transform に書く（Stash/Restore を置換）
3. `OnBeforeSimulate`（Simulate 直前）: 装飾 transform を curr（真の物理姿勢）へ復元。
   描画姿勢が SyncTransforms 経由で次ステップの初期値にコミットされるのを防ぐ

フックは `RunnerSimulatePhysics.OnBeforeSimulate/OnAfterSimulate` イベント
（アドオン標準装備）。FixedUpdateNetwork の実行順に依存しないため確実。
`RagdollController` が interpolator 生成時に購読し Despawned で解除。
イベント未配線時は従来の Stash/Restore へフォールバックし分離は起きない。

表示は最大 1 tick（約 16ms）遅れるが、揺れもの装飾では慣性遅れに見え許容。

### 残課題: 揺れの「固さ」の物理的要因

クライアントの本体 15 パーツは kinematic で、Render の transform 書き込み =
速度ゼロのテレポート移動。ジョイント越しに装飾へ伝わる励起が
ホスト（連続的な dynamic 運動、正しい速度場）と質的に異なる。
改善候補: 物理ステップ前に本体を `Rigidbody.MovePosition/MoveRotation` で
tick ポーズへ動かし速度を物理に伝える（transform 書き込みとの競合検証が必要）。
今回の描画補間で知覚上の固さがどこまで残るか、実機確認後に判断する。

- 教訓: 「正味ゼロの復元」は物理を守るが描画は守らない。描画レート ≠ 物理レートの
  環境では、物理オブジェクトにも描画専用の補間レイヤーが必要
- 教訓: 手動 Physics.Simulate 構成では Rigidbody.interpolation は当てにしない

## 追記（2026-06-12 その2）: 描画補間だけではジャダーが消えず、本体を MovePosition 化

実機テスト結果: 描画補間（前項）導入後もジャダーは同レベルで残存。
→ 主成分は「描画レートの階段」ではなく**装飾の物理軌道そのもののガタつき**だった。

### 真のメカニズム

クライアントの本体 15 パーツは kinematic で、Render() の transform 直書きで動く。
物理エンジンから見るとこれは**速度ゼロのテレポート**の繰り返し:

1. 物理ステップ時、本体は「最後の Render が書いた位置」に瞬間移動している
2. テレポートなので本体の速度場はゼロ。ジョイントソルバーは位置エラーだけを見て
   装飾を毎 tick「ガッ」と引き戻す
3. テレポート距離はフレーム/tick の位相差で不均一 → 引き戻しの強さも不均一
4. 結果、装飾の物理姿勢系列自体がガタつく。これを滑らかに描画補間しても
   「ガタついた軌道を滑らかになぞる」だけでジャダーは消えない

### 修正: OnBeforeSimulate で本体を MovePosition で動かす

`MoveBodyTowardRenderPoses()`（RagdollSnapshotPoseInterpolator）:

1. 現在の transform 姿勢（= 最後の Render が書いた描画姿勢）を target として取得
2. transform を**前回ステップの物理姿勢へ戻す**（戻さないと現在姿勢 = target で移動量ゼロ）
3. `rb.MovePosition(target)` / `rb.MoveRotation(target)` を発行
4. PhysX はステップ中に速度 (target - prev) / dt を持つ移動として解決 [※理論]
   → ジョイント経由で装飾にホスト同様の速度励起が伝わる
5. kinematic の MovePosition はステップ終了時に必ず target に到達するため、
   ステップ後の物理姿勢として target を記録すれば OnAfterSimulate での読み直しは不要

ターゲットを「networked 生読みの tick ポーズ」ではなく「描画姿勢」にした理由:
生読みは補間バッファより数 tick 先の時間軸にあるため、装飾が「本体の未来位置」
基準で揺れてしまい、描画上の本体と装飾に移動速度×遅延分の定常オフセットが出る。
描画姿勢を物理の基準にすれば描画と物理の時間軸が一致する。

テレポート対策: 1 tick で 2m 超の移動は MovePosition せずスキップ
（リスポーン時に巨大速度が発生して装飾が吹き飛ぶのを防ぐ）。

- 教訓: kinematic ボディを transform 直書きで動かすと速度ゼロのテレポートになり、
  ジョイントで繋がった dynamic ボディへの励起が壊れる。速度を伝えたいなら
  MovePosition/MoveRotation（kinematic target 移動）を使う
- 教訓: 「描画がガタつく」とき、描画レイヤーの問題か物理軌道の問題かを分けて考える。
  描画補間を入れても直らなければ、補間の端点（物理姿勢系列）自体を疑う

## 追記（2026-06-12 その3）: MovePosition 撤回と真因 = resim による Physics.Simulate 多重実行

実機テスト結果: MovePosition 方式は**大幅悪化**（装飾が竜巻のように発散）。即撤回。

### MovePosition が破綻した理由（推定）

「transform を前回姿勢へ戻す + MovePosition(target)」の併用は、
SyncTransforms と kinematic target の適用順序が Unity 内部実装依存で、
意図（prev→target の速度付き移動）どおりに解決されない。
さらにクライアントでは resimulation で OnBeforeSimulate が 1 フレームに
複数回呼ばれ、prev/target が交互に入れ替わって正負の速度が毎 tick 反転
→ ジョイント越しに装飾へ交番励起が入り発散した。
[※推測] 正確な内部順序は未確認だが、実機の発散で方式自体を棄却。

- 教訓: kinematic RB に対する transform 書き込みと MovePosition の併用は
  同一ステップ内で混ぜない。どちらか一方に統一する

### 真因の特定: RunnerSimulatePhysics に resim ガードがない

「ログは出ている（コードは動いている）のにジャダーが同レベル」という報告で、
描画レイヤーより下、物理時間そのものを疑い、アドオンのソースを再読:

- `RunnerSimulatePhysics.FixedUpdateNetwork()` は `Runner.IsResimulation` を見ずに
  毎回 `Physics.Simulate()` を呼ぶ
- Fusion のクライアントは入力予測のため 1 フレームに複数 tick を再実行（resim）する
- 本プロジェクトはホスト権威 + クライアント本体は kinematic ポーズ同期のため、
  クライアントの resim 物理は**同期に何も寄与しない**。一方、ローカル物理で
  揺らしている装飾 RB だけが resim 回数分、不規則に時間加速する
- ホスト: resim なし → 60Hz の均一ステップ → 綺麗に揺れる
- クライアント: (1 + resim 回数) 倍速で進み、回数はネットワーク状況で毎フレーム変動
  → ジャダー（不規則な時間加速）+ 固い揺れ（バネが実時間より速く減衰しきる）

これは最初の報告「微振動・ホストより固い」と完全に整合する。
また「描画補間（その1）が効かなかった」ことも説明できる:
OnAfterSimulate が 1 フレームに複数回発火して補間端点の時間軸が壊れていた。

### 修正: NoResimulationSimulatePhysics（resim ガード付き継承クラス）

アドオンのファイルは編集せず、継承で挙動を差し替える:

```csharp
public sealed class NoResimulationSimulatePhysics : RunnerSimulatePhysics
{
    public override void FixedUpdateNetwork()
    {
        if (Runner.IsResimulation) return ; base.FixedUpdateNetwork() ; }
}
```

- SessionManager.EnsurePhysicsSimulation の AddComponent をこのクラスに変更
- NetworkRigidbody.SetupPhysicsBody の自動追加は**基底型の TryGetComponent** で
  既存チェックするため、本クラスが先に登録されていれば二重追加されない（コード確認済み）
- ホストは IsResimulation が常に false のため動作不変（実機で退行なし確認済み）
- 購読ログに simulator の型名を追加（クライアントで NoResimulationSimulatePhysics で
  あることを確認できる）

- 教訓: 「クライアントでだけ物理の見た目がおかしい」ときは resimulation による
  多重 Simulate を疑う。サーバー権威構成ではクライアントの resim 物理は
  寄与ゼロでコストと副作用だけが残る
- 教訓: 対症療法（描画補間）が効かなかったら、より下のレイヤー（物理時間の進み方）
  を測る・読むへ戻る

## 追記（2026-06-12 その4）: Walking 時の残ジャダーを「ルート相対」描画で解消

実機テスト結果（resim ガード後）:
- 揺れの柔らかさ: ホストとほぼ同一（resim ガードの効果を確認）
- Idle 時のブレ: 許容範囲
- **Walking 時のみ**: Sphere が本体からわずかに離れるくらい荒ぶって目立つ

### 原因: 本体と装飾の「補間時間軸」の不一致

装飾の描画補間（その1）はワールド座標で記録・補間していた:

- 本体: ネットワークスナップショット補間（from/to バッファ + Fusion の alpha）
- 装飾: ローカル物理ステップ補間（prev/curr + 実時間経過の alpha）

2 つの alpha は**別の時間軸**で進む。静止中はどちらも動かないので差が出ないが、
移動中（6m/s なら 1 tick で 10cm）は、両者の位相差がそのまま
「本体と装飾の相対位置の振動」として毎フレーム現れる。
→ Idle は許容範囲・Walking のみ荒ぶる、という症状と完全に一致。

### 修正: 装飾を「物理ルート相対」で記録し、描画ルート変換で合成

- OnAfterSimulate: 装飾のワールド姿勢ではなく
  「その時点の本体ルート（APR_Root）相対の姿勢」を prev/curr に記録
- Render: 相対姿勢を補間し、**本体と同じ描画ルート変換**（rootPosition/rootRotation、
  ネットワーク補間結果）で合成して書き込む
- 本体と装飾が並進・回転を共有するため、時間軸の位相差は構造的に消える。
  残るのは相対空間内の揺れ（ジョイントによる二次運動）だけで、これは本来見せたいもの
- OnBeforeSimulate の物理復元はルート相対 → ワールド再構成
  （基準 = 前ステップ終了時のルート姿勢。本体だけが先へ進み、ジョイントが
  装飾を追従させる = ホストと同じ励起の与え方）
- リスポーン対策: ルートが 1 tick で 2m 超移動したら、復元基準を現在のルート姿勢に
  切り替えて装飾を本体に随伴テレポートさせる（ジョイント大エラーでの吹き飛び防止）

- 教訓: 異なる時間軸（ネットワーク補間 vs ローカル物理）で動くオブジェクト同士を
  ワールド座標で別々に補間すると、移動中に位相差が相対振動として見える。
  「親に追従して見えるべきもの」は親の基準系（相対座標）で補間してから
  親の描画変換で合成する

## 追記（2026-06-12 その5）: Walking 時ジャダーの最後の振動源 = 物理励起の不均一

実機テスト結果（ルート相対化後）: 揺れの柔らかさはホスト同等、Idle は許容範囲だが、
**Walking 時のジャダーが残存**（ユーザーの「揺れ成分」はジャダーを指していた）。

### 残っていた振動源

ルート相対化で「描画の合成」は本体と同期したが、**物理シミュレーション内で
装飾が本体に対して振動する**問題が残っていた:

- 物理ステップ時の本体 15 パーツは「最後に Render が書いた描画姿勢」に居る
- Render はフレームレートで走り tick と非同期 → ステップ間の本体移動量が不均一
  （fps < tick だと「2 tick 静止 → 2 tick 分ジャンプ」）
- ジョイントはこの不均一な励起で装飾を相対空間内で振動させる
- Idle では本体がほぼ動かず励起ゼロ → ジャダーなし（症状と一致）

### 修正: 物理ステップ直前に本体を「生読み tick ポーズ」へ配置

`WriteBodyTickPosesForPhysics()`（OnBeforeSimulate 内、装飾復元の前）:

- [Networked] プロパティの生読み（NetRootPosition + NetPartPositions 等）は
  スナップショットバッファ補間と違い「最新受信 tick の確定ポーズ」で、
  tick ごとに均一に進む系列
- これを物理世界の本体姿勢として配置することで、ジョイント励起が均一化される
- 物理本体（tick ポーズ）と描画本体（補間ポーズ）の位置差は、
  Render が毎フレーム描画姿勢を上書きし、装飾はルート相対合成のため見た目に出ない
  ＝ 前回のルート相対化はこの修正の前提条件だった
- IPoseSnapshotAccess に生読みアクセス API を追加（IsLatestPoseInitialized /
  LatestRootPosition / GetLatestPartRelativePosition 等）

### 派生バグ: ホストでも interpolator がイベント購読されていた

実装後のホスト退行確認で移動が 23.8m/4s → 2.9m/4s に激減。stash 比較で自分の
変更が原因と確定し、調査の結果:

- `Spawned` は `_clientProxyRuntime.Initialize()` を **HasStateAuthority に関係なく
  無条件で呼ぶ**（RagDollController.cs の Spawned 内）
- そこから CreateSnapshotPoseInterpolator → イベント購読が走り、
  ホストでも OnBeforeSimulate が毎 tick 実行されていた
- これまでの装飾処理はホストでは正味ゼロ（Render の装飾書き込みが無いため
  復元が同値書き込み）で実害がなかったが、本体 tick 配置は dynamic な本体を
  毎 tick 「最後に発行したポーズ」へ巻き戻し、移動を殺した

修正: CreateSnapshotPoseInterpolator 内で `!Object.HasStateAuthority` の場合のみ
購読する明示ガードを追加。ガード後にホスト 23.8m/4s 復旧を実測確認。

- 教訓: イベント購読（暗黙の実行経路）は「誰が・どのロールで呼ぶか」を
  購読側で明示的にガードする。呼び出し元の生成条件に暗黙依存しない
- 教訓: 「自分の変更が原因か」を最速で確定するのは git stash による A/B 実測。
  コードレビューでの「影響しないはず」は Spawned の無条件 Initialize のような
  見落としに勝てない

## 追記（2026-06-12 その6）: 残存微振動への描画専用ローパスフィルタ

実機テスト結果（励起均一化後）: ジャダーはかなり減ったが「携帯電話のバイブレーション」
程度の微振動が残存。本体の滑らかな動きとの対比で目立つ。

### なぜ原理的な残差が残るか

クライアントの本体 15 パーツは kinematic で、物理ステップ直前に transform 配置
（= 速度ゼロのテレポート移動）される。ジョイントソルバーは親の「速度」を使えず
位置誤差だけで装飾を駆動するため、ホスト（dynamic な本体が連続的な速度を持つ）と
比べて励起がインパルス列になる。これは構成上避けられない残差で、
これ以上は物理側では取れないと判断した。

### 修正: 描画レイヤーで相対姿勢に指数移動平均（EMA）

`WriteDecorationRenderPoses()` 内、補間後のルート相対姿勢に適用:

```
k = 1 - exp(-Time.deltaTime / tau)   // フレームレート非依存
smoothedPos = Lerp(smoothedPos, targetPos, k)
smoothedRot = Slerp(smoothedRot, targetRot, k)   // tau = 0.08s
```

- 1 次ローパスのカットオフは fc = 1/(2π·tau) ≈ 2Hz。バイブレーション帯（>10Hz）は
  大きく減衰し、見せたい揺れ（1〜2Hz）は振幅をほぼ保って通る [※理論]
- 位相遅れとして装飾の動きに最大 0.1s 弱の遅れが乗るが、揺れもの装飾では
  慣性遅れに見えるため許容（遅延許容方針とも整合）
- **物理には一切介入しない**: スムーザーは描画書き込み直前の値だけを平滑化し、
  OnBeforeSimulate の物理復元は素の `_decoRelPositionsCurr` を使い続ける。
  MovePosition 撤回の教訓（物理への介入は壊れやすい）に沿った安全側の設計

### リセット条件（尾引き防止）

大ジャンプを平滑化すると装飾だけが遅れて滑空して見えるため、以下で即追従に戻す:

- 初回フレーム / 装飾配列長の変化（プレハブ構成変更）
- 描画ルートのテレポート（前フレームから 2m 超移動、RootTeleportDistanceSqr と共用）

### 検証

- コンパイル: エラー 0 / 警告 0
- ホスト退行確認（自動操作）: スポーン (-3.90, 0.89, -4.42) → W 4 秒で
  z 19.37（移動量 23.8m）。正常値と一致、退行なし
  （ホストはイベント購読ガードによりスムーザー経路自体が動かないため理論上も影響なし）
- クライアント見た目の最終確認はユーザーの実機 2 クライアントテストで行う

### 自力再実装チェックリスト

- [ ] EMA はフレームレート非依存の式（1 - exp(-dt/tau)）を使ったか
      （固定係数 Lerp は fps で効きが変わる）
- [ ] 回転は Slerp で平滑化したか（成分 Lerp は正規化が崩れる）
- [ ] スムーザーは描画専用か（物理復元に平滑値を混ぜると発振・減衰の副作用）
- [ ] テレポート・配列長変化でリセットしているか（尾引き・配列不整合）
- [ ] tau の選定根拠を説明できるか（通したい周波数と切りたい周波数の比）

### 追補（同日）: tau の Inspector 化とデフォルト 0.05s への変更

実機確認の結果「微振動は消えたが遅れが気になる」とのことで、時定数を調整可能にした:

- `RagdollProfile.decorationSmoothingTau`（[Range(0, 0.3)]、デフォルト 0.05s）として
  シリアライズ。既存アセットは YAML に値が無いためフィールド初期値 0.05s が適用される
- 供給経路: RagdollProfile → RagdollController（IPoseSnapshotAccess 実装）→
  RagdollSnapshotPoseInterpolator が**毎フレーム読む**ため、Play 中の Inspector
  スライダー操作がリアルタイムに見た目へ反映される（チューニングループが最短になる）
- tau ≤ 0 で平滑化なし（係数 1 = 素通し）。フィルタ無効化の A/B 比較にも使える
- 調整指針: 微振動が見える→上げる（0.08〜0.15）、遅れが目立つ→下げる（0.03〜0.05）
