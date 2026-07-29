# ピア同期物理オブジェクトを純補間 kinematic プロキシへ統一（Plan B）

> **公開時注記（2026-07-22）:** GameNetworkRigidbodyによるプロキシ側kinematic化は、スポーンドリフトとローカル物理の独走を防ぐ方式として現行実装に残っています。ただし、当初の目的だった「手とCubeの表示上の隙間」は解消しませんでした。結論は本資料の実機検証結果と[`ARCHITECTURE_FAILURE_MODES.md`](../ARCHITECTURE_FAILURE_MODES.md)を参照してください。Treasure専用方式に関する記述は7月20日の削除前の状態です。

- 日付: 2026-06-18
- 種別: feat（ネットワーク同期 / 物理 / 設計判断）
- 対象: `Assets/Code/Scripts/Network/GameNetworkRigidbody.cs`（新規）, `Obs_Cube.prefab`, `RingRails Variant.prefab`, `Assets/Code/Scripts/Player/RagdollHandContact.cs`
- 関連: [`ARCHITECTURE_OVERVIEW.md`](../ARCHITECTURE_OVERVIEW.md) / [`2026-03-27_syncmetrics_baseline_measurement.md`](2026-03-27_syncmetrics_baseline_measurement.md)

---

## 1. 問題

ホスト権威（ホストの物理結果のみが唯一の正解）の構成で、掴める物理オブジェクト（Obs_Cube）を
プレイヤーが押すと、**クライアント側でだけ**プレイヤーと Cube の間に不自然な「隙間」が見えた。
Cube がプレイヤーより数 tick 先行して動き、「触れていないのに押している」ように見える。ホスト側は正しい。

### 1.1 なぜ隙間が出るのか（非対称性が原因）

このプロジェクトのプレイヤーは既に **SnapshotInterpolation 方式** のプロキシ表示を採用している:

- クライアント（プロキシ）では、プレイヤーの全 15 ボディパーツを `isKinematic=true` にする
  （`RagDollController.cs` の SnapshotInterpolation モード分岐）。
- ローカル物理シミュレーション・PID・外挿は一切行わず、**ホストのスナップショットを補間して描画するだけ**
  （`RagdollSnapshotPoseInterpolator`）。
- さらに `Object.ForceRemoteRenderTimeframe = true` により、確定済みスナップショット間を補間する
  **remote（遅延）タイムフレーム**で描画される。

一方で Cube は **非 kinematic（dynamic）** のままだった。Fusion 物理アドオンの `RunnerSimulatePhysics` は
全ピア（プロキシ含む）で `Physics.Simulate()` を毎 tick 実行する。つまりクライアント上の Cube は
**ローカル物理で動いていた**。

結果として:

| | プレイヤー（プロキシ） | Cube（旧: 非kinematic） |
|---|---|---|
| 動かし方 | ホストスナップショットの補間（遅延） | ローカル物理シミュレーション（present 寄り） |
| タイムフレーム | remote / delayed | ローカル（先行） |

プレイヤーの kinematic コライダは `Physics.Simulate` 中に「最新 tick ポーズ（present 寄り）」へ置かれる
（`RagdollSnapshotPoseInterpolator.WriteBodyTickPosesForPhysics` / `OnBeforeSimulate`）が、
**画面表示は補間（遅延）**。そこに present 位置の手コライダで押された dynamic な Cube が同居すると、
Cube は present で先行し、表示は遅延しているプレイヤーとの間に隙間が生じる。

### 1.2 もう一つの症状（スポーンドリフト）の機序

前段の修正（プレハブ Transform を (0,0,0) にリセット）でスポーン位置ずれは解消していたが、
その根本機序も同じ「非 kinematic プロキシのローカル物理」にある:

- 基底 `NetworkRigidbody.BeforeAllTicks` は `CopyToEngine()` を
  `_rootIsDirtyFromInterpolation || resimulation` が真のときだけ呼ぶ（`NetworkRigidbody.cs:172-174`）。
- 基底 `Render()` は速度が sleep threshold 以下だと **early-return** し、補間も dirty フラグ立ても行わない
  （`NetworkRigidbody.cs:534-536`）。
- → 静止中のプロキシは dirty にならず CopyToEngine も呼ばれない。その間 dynamic な Cube は
  ローカル重力で独走し、最初の接触（`InterestEnter → CopyToEngine(true)`）まで補正されない。

---

## 2. 解決方針と、不採用にした代替案

### 採用: Plan B 「ピア同期物理を全て純補間 kinematic プロキシへ統一」

Cube に限らず**ピア間で同期する全ての物理オブジェクト**を、プレイヤーと同じ原理で同期する:

> プロキシ（非ホスト）では Rigidbody を kinematic にし、ローカル物理を止め、
> ホストのスナップショット補間だけで動かす。

こうすると Cube もプレイヤーも同じ Fusion 補間バッファ・同じ remote タイムフレームで描画され、
相対位置がホストと一致 → 隙間が消える。スポーンドリフトも、kinematic body は `Physics.Simulate` で
重力を受けないため原理的に発生しない。

### 不採用にした代替案

- **(却下) 掴める物体だけクライアントで dynamic 維持**: 隙間問題が再発するため本末転倒。
- **(却下) Forecast Physics（外挿）方式**: 2026-03-28 のA/Bテストでは、当時のkinematic方式より誤差とovershootが大きかった
  （[`2026-03-27_syncmetrics_baseline_measurement.md`](2026-03-27_syncmetrics_baseline_measurement.md)）。
- **(不採用) orphan の `NetworkRigidbodySpawnTeleport`（Spawned で Teleport して初期位置固定）**:
  これは「非 kinematic プロキシの初期ドリフトを補正する対症療法」。kinematic 化すればドリフト自体が
  起きないため不要になり、削除した。

### グラブの権限設計: authority 非移譲・ホスト joint

掴み制御はクライアントへ authority を移さず、**ホスト側で `FixedJoint` を生成**して行う
（元から `RPC_GrabObject` が StateAuthority 上で joint を作る設計だった）。ホスト権威方針と整合。

---

## 3. 実装の仕組み

### 3.1 `GameNetworkRigidbody : NetworkRigidbody`（新規）

プロキシを kinematic に保つだけの最小サブクラス。ポイントは2つ:

```csharp
public override void Spawned()
{
    base.Spawned();
    _rb = GetComponent<Rigidbody>();
    if (_rb != null && !HasStateAuthority)   // プロキシのみ
    {
        _rb.isKinematic = true;              // ローカル物理を止める
        CopyToEngine(true);                  // スポーン位置をネットワーク状態へ即同期（後述）
    }
}

protected override void CopyToEngine(bool forceAwake = false)
{
    base.CopyToEngine(forceAwake);
    // 基底 CopyToEngine は毎回ホストの kinematic フラグ(=非kinematic)を
    // プロキシへ再適用する(NetworkRigidbody.cs:349-351)。だから戻す。
    if (_rb != null && !HasStateAuthority)
        _rb.isKinematic = true;
}
```

- **なぜ `CopyToEngine` の override が必要か**: 基底 `CopyToEngine` は同期されたフラグ（ホストの Cube は
  dynamic なので `IsKinematic=false`）をプロキシの Rigidbody に毎回書き戻す。Spawned で一度
  kinematic にしても、次の CopyToEngine で非 kinematic に戻されてしまう。だから override で再適用する。
- **なぜ `_physicsBody` を使わず `GetComponent<Rigidbody>()` か**: 基底の `_physicsBody` は private で
  サブクラスから触れない。Unity の `Rigidbody` を直接キャッシュして操作する。
- **スポーン時の初期位置同期（追記 2026-06-19）**: 当初 kinematic 化でドリフトが消えるから不要と判断したが、
  実機 2 クライアントで「Join 側だけ Cube が prefab 位置(0,0,0)にスポーンし、ホストの (3,5,0) に同期されない。
  ホスト側で誰かが Cube に触れて初めて Join 側もテレポートで正しい位置に飛ぶ」症状が出た。
  原因: 静止オブジェクトは基底 `Render()` が sleep 閾値以下で early-return する（`NetworkRigidbody.cs:534-536`）ため
  dirty フラグが立たず `CopyToEngine` も呼ばれず、プロキシは prefab 位置に取り残される。触れると
  `InterestEnter → CopyToEngine(true)` が走って初めて同期される。kinematic 化は「重力ドリフト」は消すが、この
  「初期位置を一度もエンジンへ書き込まない」問題は別物で残っていた。
  対策: プロキシの `Spawned()` で `CopyToEngine(true)`（forceAwake、InterestEnter と同じ手）を明示的に呼び、
  スポーン時点でネットワーク状態（ホストの位置・回転）へ即同期する。
  ※ プロキシ視点では Cube もプレイヤーも kinematic なので「ローカルでは衝突しない（すり抜ける）」が、
    衝突はホストが dynamic 同士で処理し、その結果が補間で Join 側へ反映されるため実害はない（これが Plan B の前提）。
- **基底 `Render()` の補間はそのまま活かす**: プロキシの視覚追従はこれが担う。
  GameNetworkRigidbody は補間ロジックには一切手を入れない。
- **weaver 安全性**: `[Networked]` プロパティと `[Rpc]` を**一切追加しない**ため、基底の
  `[NetworkBehaviourWeaved(WORDS)]` レイアウト（`NetworkTRSPData.WORDS + NetworkPhysicsData.WORDS`）を
  そのまま継承し、語数は不変。全ピアが同一クラスを実行するためレイアウトは一致する。
  Unityのコンパイルで0 errors / 0 warningsを確認済み（= weaverが受理）。
- **ホストでは無効**: `HasStateAuthority` のときは何もせず、基底の dynamic 物理挙動のまま
  （ホストの物理結果が唯一の正解）。

### 3.2 プレハブ差し替え

`Obs_Cube.prefab` と `RingRails Variant.prefab` の `NetworkRigidbody` コンポーネントの
`m_Script` guid を `GameNetworkRigidbody` の guid に差し替えた。サブクラスは基底の SerializeField
（`SyncScale`/`SyncParent`/`_interpolationTarget` 等）を継承するため、シリアライズ値はそのまま引き継がれる。

> `Treasure_Heavy.prefab` は `NetworkRigidbody`（guid `5baa37e0…`）を参照しておらず、別スクリプトで
> 独自同期している。今回のスコープ外。**Treasure 系で同じ隙間問題が出るかは別途要検証**（§6）。

### 3.3 グラブ検出のホスト側移行（Option A）

Cube を kinematic 化すると、クライアント上では「kinematic な手 × kinematic な Cube」になり、
`OnCollisionEnter` が発火しなくなる（Unity 物理: kinematic 同士は衝突応答せずイベントも出ない）。
従来のグラブ検出はこのクライアント側 dynamic 衝突に依存していたため機能しなくなる。

そこで検出・判定・実行を全てホスト（StateAuthority）に移した。ホスト上では全プレイヤーの ragdoll と
Cube が dynamic で実際に衝突するため `OnCollisionEnter` が発火する。

- `FixedUpdateNetwork`: `!HasStateAuthority` で early-return。`GetInput` で入力権限クライアントの入力を
  読み、掴み/パンチボタンを `_grabButtonHeld`/`_punchButtonHeld` にキャッシュ。
  **GetInput が成功した tick のみ**解放判定を行う（入力欠落 tick での誤解放を防ぐ）。
- `OnCollisionEnter`: `!HasStateAuthority` で early-return。物理コールバックからは `GetInput` を直接
  呼べないため、キャッシュ済みの掴み入力を見て `DoGrab` を直接呼ぶ。
- `RPC_GrabObject` → `DoGrab`（非 RPC）に変換。`RPC_ReleaseGrab` は削除し `ReleaseGrab()` を直接呼ぶ。
  検出と実行が同じホスト上にあるので RPC が不要になった。
- 副次的効果: 旧コードはクライアントの `FixedUpdateNetwork` で `_attachJoint==null` を見ていたが、
  `_attachJoint` はホスト側でのみ生成されるため、クライアントでは常に null で判定が噛み合わない懸念があった。
  ホストに寄せたことで joint のライフサイクルと判定主体が一致する。

---

## 4. 検証

- Unityコンパイル → **0 errors / 0 warnings**。
- Unity 上で両プレハブが `MyFolder.Scripts.Network.GameNetworkRigidbody` に解決、Missing Script なし
  （Unity Editor上で確認）。
- **ランタイム2クライアント検証（手動・必須）**:
  - (a) 押した時の隙間が消えるか
  - (b) グラブ → 運搬 → 解放が両ピアで一貫するか（ホスト自身・リモートクライアント両方）
  - (c) 30 秒静止でドリフトなし
  - (d) ホスト退行なし（Cube の挙動・Treasure 掴み）

---

## 5. 自力再実装チェックリスト

1. [ ] ピアの物理プロキシが「ローカル物理で独走」しているか確認（`RunnerSimulatePhysics` が全ピアで
   `Physics.Simulate` するため、非 kinematic プロキシは重力を受ける）。
2. [ ] プレイヤー側プロキシが既に kinematic + 純補間か確認し、同期方式を揃える方針を取る。
3. [ ] `NetworkRigidbody` を継承するサブクラスを作り、プロキシ(`!HasStateAuthority`)で
   `isKinematic=true` を **Spawned と CopyToEngine override の両方**で適用する
   （基底が毎 tick 非 kinematic へ戻すため override 必須）。
4. [ ] `[Networked]`/`[Rpc]` を追加しない（weaver の WORDS を不変に保つ）。compile で weaver を通す。
5. [ ] 対象プレハブの `m_Script` guid をサブクラスへ差し替え（SerializeField は継承で維持される）。
6. [ ] kinematic 化で衝突検出が壊れる箇所（grab 等）をホスト側 dynamic 衝突へ移す。
   入力はホストが `GetInput` で読み、物理コールバックからは直接 GetInput せずキャッシュ参照。
7. [ ] 2 クライアントで隙間・グラブ一貫性・静止ドリフト・ホスト退行を確認。

---

## 6. 実機検証結果（2026-06-19, 2クライアント Host+Join）

- ✅ スポーン初期位置: Join 側でも最初から (3,5,0) に出る（§3.1 の `CopyToEngine(true)` で解決）。
- ✅ グラブ→運搬→解放: 両ピアで一貫。
- ✅ 静止ドリフト・ホスト退行: 問題なし（Host 側でスポーン＋グラブ正常）。
- ❌ **押した時の隙間: 解消せず（縮小もせず）。** → 下記のとおり別レイヤーの問題と判明し、**保留（ユーザー判断 2026-06-19）**。
- 付随: つかみ中に移動できず運搬不可だった件は別 devlog `2026-06-19_carry_while_grabbing.md` で修正済み。

### 隙間が Plan B で直らなかった理由（重要な学び）
当初の仮説「Cube を純補間プロキシ化すれば同じタイムフレームに揃って隙間が消える」は**不完全だった**。
プレイヤーと Cube は**別々の補間パイプライン**を使う:
- プレイヤー: 独自 `RagdollSnapshotPoseInterpolator`（[Networked] ポーズの from/to/alpha）＋ `ForceRemoteRenderTimeframe=true`。
- Cube: 基底 `NetworkRigidbody.Render`（NetworkTRSPData の from/to/alpha）＋ `ForceRemoteRenderTimeframe=0`。

両者が同じ "remote" でも、別パイプラインゆえ実効遅延・スナップショット取得点が完全一致せず、相対位置にズレが残る。
`ForceRemoteRenderTimeframe=1` を Cube に付けてもプロキシは元々 remote なので効果は薄い見込み。
真に揃えるには 2 系のタイムライン整合（計測ベースの調整、または Cube もプレイヤーと同一補間系に寄せる）が要る。
→ grab/carry/spawn は機能するため、隙間は視覚 polish として保留。再開時は「クライアントでプレイヤーの手と Cube の
   描画位置を時系列ログ採取し実効遅延差を定量化」から始める。

## 7. その他の残課題・フォロー
- **Treasure系の同期方式（当時）**: この時点では専用同期を別途検討対象としていたが、2026-07-20に専用経路を削除し、現在は一般の重いRigidbodyとして扱っている。
- スポーン時に「投げられた（移動中の）状態」で出現する Cube のエッジは未検証（静止前提で動作）。
