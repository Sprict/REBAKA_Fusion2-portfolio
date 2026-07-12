# 開発ログ: 2026-06-22 - クライアントプロキシ描画同期バグ2件（グラブ隙間 / 押し先行・カクつき）

ホスト権威 + クライアント純補間プロキシ（Plan B、[[2026-06-18_peer_sync_pure_interpolation]]）の構成で、
クライアント画面だけに現れる「プレイヤーと物理オブジェクト（Obs_Cube）の位置不一致」を2件まとめて修正した。
2件は症状こそ似ているが**根本原因がまったく別レイヤー**（ホストの発行タイミング / クライアントの描画タイムフレーム）であり、
切り分けの実例として価値が高い。

- 修正コミット: `ee4abb0`（グラブ隙間）, `e1ba3a4`（押し先行・カクつき）
- ブランチ: `fix/client-proxy-visual-gap`（`develop` ベース）

---

## 1. 症状（何が起きていたか）

ホスト画面では2件とも正常（プレイヤーと Cube が密着し、両方ぬるぬる動く）。**クライアント画面でのみ**以下が出る。

### 症状A: グラブ中の隙間
- クライアントのプレイヤーが Obs_Cube を**掴んで運ぶ**と、手と Cube の間に隙間ができる。
- ホストでは手が Cube を掴んで運べているのに、クライアントでは離れて見える。

### 症状B: 押し中の先行・カクつき
- クライアントのプレイヤーが Cube を**押す**と、Cube がプレイヤーより**約1 tick 先行**した位置に見える（こぶし1個分以上）。
- さらにプレイヤーは滑らか（フレームレート）なのに、**Cube だけカクカク**（tick レート）に動く。

---

## 2. 調査プロセス（どうやって原因を特定したか）

### 症状A（グラブ隙間）の切り分け

最初の疑い: 「クライアントでラグドール物理を予測しているのでは？」
→ コードを追うと、プロキシの15パーツは kinematic + スナップショット補間で、**物理予測はしていない**ことを確認（仮説を棄却）。

次の着眼: 手と Cube の**位置を捕捉する時刻のズレ**。
- 手（ボーン）ポーズは `RagdollProxyPosePublisher.Publish()` が `[Networked]` 配列へ書く。
- 当時これは `FixedUpdateNetwork()`（= `Physics.Simulate()` の**前**, PRE-physics）で呼ばれていた。
- 一方 Cube の位置は `NetworkRigidbody` が `IAfterTick.AfterTick → CopyToBuffer`（= **後**, POST-physics）で捕捉する。
- → クライアントが同一 tick を補間すると **手 = PRE-physics 座標 / Cube = POST-physics 座標**。
  両者は `FixedJoint` で物理的に拘束されているのに、捕捉時刻が1物理ステップずれているため、
  そのステップの移動量ぶん隙間になる、と結論。

### 症状B（押し先行・カクつき）の切り分け — 当初仮説を実測で否定

最初の仮説: stock `NetworkRigidbody.Render()` の**早期リターン**が犯人。
具体的には [NetworkRigidbody.cs:435/534] の2つ:
```csharp
if (_doNotInterpolate || Object.RenderSource == RenderSource.Latest) return; // ①
if (IsStateBelowSleepingThresholds(_physicsBody, _physicsData)) return;      // ②
```
「①RenderSource=Latest か ②sleep閾値で補間がスキップされ、最新tick位置にスナップしている」と推測した。
プレイヤー側の自前補間 `RagdollSnapshotPoseInterpolator` は `RenderSource` を**見ずに毎フレーム必ず Lerp** するため
滑らか、という非対称性で説明できると考えた。

**ここで「Render() を override して無条件補間にする」修正に飛びつかなかった**のが分岐点。
理由は2つ:
1. `NetworkRigidbody` の補間は private なダーティフラグ（`_rootIsDirtyFromInterpolation` /
   `_targIsDirtyFromInterpolation`）で管理されており、サブクラスから `Render()` を完全再実装すると
   このフラグ管理が壊れる（アドオン本体は編集禁止）。＝クリーンな override は事実上不可能。
2. ①と②でフィックスが変わる。どちらの分岐が発火しているか**確証がない**。

そこで挙動を変えない**読み取り専用の計装**を `GameNetworkRigidbody` に仕込み、2クライアント実機で push 中のログを取った:

```
[CUBE_RENDER] tf=Local src=Interpolated hasBuf=True alpha=0.44 segLen=0.000
              meshWorld==fromPos==toPos
```

この1行が当初仮説を**全否定**し、真因を確定させた:
- `src=Interpolated` → ①RenderSource=Latest **ではない**。②sleep でもない（補間自体は走っている）。
- `tf=Local` → Cube が **Local（予測・非遅延）タイムフレーム**で描画されている（プレイヤーは強制 Remote）。
- `segLen=0.000`（from==to）→ Local タイムフレームでは補間の2端点が**同一スナップ**。
  Lerp しても動かない＝実質補間が効かず、新ネットワーク値が来た tick でのみ位置が飛ぶ。

押されている Cube の Z 座標がログ上で -6.864 → -6.604 → -6.345 と**tick 刻みでジャンプ**しているのも確認でき、
「Local タイムフレーム + kinematic プロキシ → from==to → 補間不能 → tick レートでカクつき & 非遅延ゆえ先行」
で症状B両方が一本の原因に収束した。

---

## 3. 原因（なぜ起きていたか）

### 症状A: 書き込み時刻のミスマッチ（write-timing mismatch）

`FixedJoint` で拘束された2剛体でも、**ネットワークへ捕捉する時刻が違えば**補間後の見た目はズレる。
手は PRE-physics、Cube は POST-physics で捕捉していたのが直接原因。

### 症状B: プロキシが Local タイムフレームで描画されていた

[※理論] Fusion 2 の Host モードでは、**クライアントは既定で全 NetworkObject を予測（forward simulation）する**。
そのため明示しない限り `NetworkObject.RenderTimeframe` は `Local`（= 予測・現在tick・非遅延）になる。
Fusion 公式定義: `RenderTimeframe.Local` =「owned and predicted objects の既定」、
`RenderTimeframe.Remote` =「proxied objects の既定」。
ところが本プロジェクトのクライアント Cube は **kinematic で前進シミュレートしない**ため、
「予測」しても状態が動かず、Local タイムフレームの補間端点 from/to が同一スナップに潰れる（segLen=0）。

プレイヤーはこの罠を**既に回避済み**だった: `RagdollClientBootstrapper` が
`Object.ForceRemoteRenderTimeframe = true` を設定し、強制的に Remote（遅延補間・履歴の異なる2点で Lerp）に
していた（[RagDollController.cs:590]）。Cube プレハブは `ForceRemoteRenderTimeframe = 0` のまま放置されていた
＝プレイヤーには適用済みの対処が Cube に**横展開されていなかった**のが根本原因。

---

## 4. 解決策（何をどう変えたか）

### 症状A の修正（`ee4abb0`）

発行を PRE-physics から POST-physics へ移す。`RunnerSimulatePhysics.OnAfterSimulate`（`Physics.Simulate()` 直後）に
ホスト限定で購読し、そこで `Publish()` する。

`RagdollHostSimulationOrchestrator.RunFixedUpdate()` 内の `PublishProxyPoseSnapshot()` 呼び出しを全削除し、
`RagDollController` に経路を追加:

```csharp
// Spawned() の StateAuthority ブロック内
EnsureProxyPosePublisher().Publish();   // 初回tick前の粗同期は維持
SubscribeHostSimulationEvents();        // 以降の発行は post-physics へ

private void SubscribeHostSimulationEvents()
{
    UnsubscribeHostSimulationEvents();
    if (Runner == null ||
        !Runner.TryGetComponent(out Fusion.Addons.Physics.RunnerSimulatePhysics simulatePhysics))
    {
        Debug.LogWarning("[HOST_POSE_PUBLISH] RunnerSimulatePhysics not found; ...", this);
        return;
    }
    _hostSimulateHook = simulatePhysics;
    simulatePhysics.OnAfterSimulate += OnHostAfterPhysicsSimulate;
}

// Physics.Simulate() 完了後にボーン位置を発行する。
// FixedJoint で拘束された Cube と手が同一の POST-physics 座標になる。
private void OnHostAfterPhysicsSimulate(NetworkRunner runner)
{
    EnsureProxyPosePublisher().Publish();
}
```

**なぜ `OnAfterSimulate` か**: Cube の `NetworkRigidbody` も同じ `Physics.Simulate()` の後（`AfterTick`）に
捕捉する。手も同じ post-physics 点で捕捉すれば、両者の捕捉時刻が一致し、クライアント補間で密着する。
`Despawned()` では購読解除（`RunnerSimulatePhysics` は Runner と同寿命なので自分が先に消える時に外す）。

[※推測] `NoResimulationSimulatePhysics` が resim 中の `Simulate()` を止めるため、この `OnAfterSimulate` は
forward tick のみ発火する（resim で多重発行されない）。

### 症状B の修正（`e1ba3a4`）

プレイヤーと同じく Cube プロキシも Remote タイムフレームに固定する。`GameNetworkRigidbody.Spawned()` に1行:

```csharp
// プロキシの描画タイムフレームを Remote に固定する（押し先行・カクつき対策）。
// 根拠: 2クライアント実測で tf=Local src=Interpolated segLen=0.000（from==to）を確認。
if (!HasStateAuthority && Object != null)
{
    Object.ForceRemoteRenderTimeframe = true;
}
```

**なぜこれで両症状が消えるか**: Remote はスナップショット**履歴**の異なる2点（過去の確定 from / to）を
端点に取るため、Cube が動いていれば segLen>0 になり毎フレーム滑らかに Lerp される（カクつき解消）。
かつ Remote は補間バッファぶん遅延するので、同じく Remote のプレイヤーと**同一の遅延**で描かれ、先行も解消する。
押し・掴みの接触解決はホストの dynamic 物理が唯一の正解で、クライアントの描画タイムフレームには影響されない
（＝この変更は純粋に見た目だけ）。

### よくある間違い（アンチパターン）

```csharp
// ❌ 症状Bで飛びつきがちな「Render() を override して無条件補間」
public override void Render()
{
    if (TryGetSnapshotsBuffers(out var from, out var to, out var alpha)) {
        var pos = Vector3.Lerp(/* from */, /* to */, alpha);
        transform.position = pos;   // ← private な _rootIsDirtyFromInterpolation を立てられない
    }
}
```
基底は `BeforeAllTicks` で `_rootIsDirtyFromInterpolation || resimulation` のとき `CopyToEngine()` して
transform を networked tick ポーズへ戻す。サブクラスからこの private フラグを立てられないため、
override で root を動かすと**戻し処理が走らず tick ポーズと描画ポーズが乖離**して別のドリフトを生む。
そもそも真因は Local タイムフレームなので、`ForceRemoteRenderTimeframe = true` の1行で足りる。

---

## 5. 検討した代替案

| 代替案 | 評価 | 不採用の理由 |
|--------|------|-------------|
| 症状B: `Render()` を override し無条件 Lerp | × | private ダーティフラグ管理が壊れ、別のドリフトを生む。アドオン編集も禁止 |
| 症状B: sleep 閾値だけ無効化 | × | 実測で `src=Interpolated`（sleep 不発火）と判明。的外れ |
| 症状B: プレハブの `ForceRemoteRenderTimeframe` を直接 1 に | △ | 効果は同じだが、プレイヤーと同じ「コードで明示する」パターンに統一した方が意図が追える |
| 症状B: `Object.ForceRemoteRenderTimeframe=true` をコードで設定 ★ | ○ | プレイヤーの既存対処と同型。1行・描画のみ・低リスク |
| 症状A: 補間側で手と Cube の時刻差を後から補正 | × | 対症療法。発行時刻を揃えるのが根治 |
| 症状A: 発行を `OnAfterSimulate`(post-physics) へ移動 ★ | ○ | Cube の捕捉時刻と原理的に一致する |

---

## 6. 教訓（今後同様の問題に遭遇したときのヒント）

### このバグのパターン
- 症状A =「**捕捉/書き込みタイミングのミスマッチ**」。同期する2要素を**同じ時刻に捕捉**しないとズレる。
- 症状B =「**レンダータイムフレームの不一致**」。同じ画面で比較する2オブジェクトは**同じタイムフレーム**で描かないと位相がズレる。
- 横断する教訓 =「プレイヤーに効いた対処が、同種の別オブジェクト（Cube 等）に**横展開されていない**」設定漏れ。

### 同じパターンに遭遇したときの対処手順
1. **ホストとクライアントで症状が違うか**を最初に確認。ホスト正常 = 同期/描画レイヤーの問題に絞れる。
2. 早期リターンや override に飛びつく前に、**挙動を変えない計装**で `RenderTimeframe` / `RenderSource` /
   補間端点（from/to/alpha/segLen）を実測する。仮説検証はログ1行で決まることが多い。
3. 「滑らかな基準（プレイヤー）」と「壊れている対象（Cube）」がある場合、**両者の描画経路の差分**を洗う
   （タイムフレーム、補間元、kinematic か、ForceRemote か）。
4. 修正は「基準側で既に効いている対処」を対象へ横展開できないかを最優先で探す（最小・実績あり）。

### 予防策
- ピア同期する物理オブジェクトを追加したら、プロキシで **`ForceRemoteRenderTimeframe=true` 相当**になっているかを
  チェックリスト化する（プレイヤーと同じ遅延補間タイムフレームに揃える）。
- ホスト権威で「補間で見た目を合わせたい2要素」は、**捕捉時刻（PRE/POST-physics）を必ず揃える**。

### 関連する理論/概念
[※理論] **RenderTimeframe（Local/Remote）と RenderSource（Interpolated/Latest/From/To）**:
Fusion は「いつの時点（timeframe）を、どう使って（source）」描くかを NetworkObject 単位で持つ。
Host モードのクライアントは全オブジェクトを予測するため既定 Local になりやすく、
ホスト権威で予測しない（kinematic）オブジェクトでは Local の補間端点が潰れて補間不能になる。
ホスト権威プロキシは原則 Remote に固定して履歴補間させる、と理解しておくと自力で再発防止できる。
公式: `Object.ForceRemoteRenderTimeframe`, `RenderTimeframe`, `RenderSource`（Fusion 2 ランタイム XML ドキュメント）。

---

## 7. 自力で再実装するためのチェックリスト

- [ ] ホスト画面とクライアント画面で症状が異なることを確認した（同期/描画レイヤーに絞れる）
- [ ] 早期リターンや Render override に飛びつく前に、読み取り専用計装で実測した
- [ ] `RenderTimeframe` がプレイヤー（基準）と一致しているか（両者 Remote か）を確認した
- [ ] 補間端点 from/to が同一スナップ（segLen=0）に潰れていないか確認した
- [ ] ホスト権威プロキシに `ForceRemoteRenderTimeframe=true` 相当を適用した
- [ ] 補間で密着させたい2要素の捕捉時刻（PRE/POST-physics）を揃えた
- [ ] `OnAfterSimulate` 購読は `Despawned()` で解除した（Runner と寿命が違う）
- [ ] resim で多重発行されない（forward tick のみ発火）ことを確認した

---

**修正日**: 2026-06-22
**修正ファイル**:
- Assets/Code/Scripts/Player/RagdollHostSimulationOrchestrator.cs（症状A: PRE-physics 発行を削除）
- Assets/Code/Scripts/Player/RagDollController.cs（症状A: post-physics 発行経路を追加）
- Assets/Code/Scripts/Network/GameNetworkRigidbody.cs（症状B: プロキシを Remote タイムフレームに固定）
**修正コミット**: ee4abb0（グラブ隙間）, e1ba3a4（押し先行・カクつき）
**関連**: [[2026-06-18_peer_sync_pure_interpolation]]（Plan B 純補間プロキシ）, 2026-06-19_carry_while_grabbing
