# 開発ログ: 2026-02-18 - クライアントスポーン直後KO問題（Gang Beasts方式で解決）

## 1. 症状（何が起きていたか）

Host + Client の2インスタンスでテストしたところ、クライアント側の自キャラが
スポーン直後（1〜2秒以内）に必ずKO状態（`PlayerState.Ragdoll`）になる。

- **Client自キャラ**: スポーン直後にKO → 動けなくなる
- **Host（自分が操作するキャラ）**: 正常にスポーン
- **Client側のProxy（他プレイヤー表示）**: 正常

再現条件: `NetworkRigidbody` を全ボディパーツに追加した後（2026-02-18の前作業）に発生。
それ以前は正常だった。

---

## 2. 調査プロセス（どうやって原因を特定したか）

[※推測] 以下は計画ドキュメントと変更差分から推測した調査フロー。

### 最初の仮説

「スポーン時の何かが `KnockoutForce` を超えている」

- `RagdollImpactContact.OnCollisionEnter` が原因候補
- `knockoutForce = 15f` を超える衝突が発生しているはず

### 調査手順

1. **Fusionのデバッグログを確認** → `RunClientSideResimulationLoop` の中で `Wall_Chiseled` との衝突が複数回検知されていた
2. **再シミュレーション中の衝突を確認** → `IsResimulation` フラグが `true` のまま `OnCollisionEnter` が複数Tick分発火していた
3. **`RagdollImpactContact.cs` にガードを追加** → `IsResimulation` 中はスキップするよう修正（部分修正）
4. **しかし症状が残る** → `IsResimulation` ガードだけでは不十分
5. **根本原因の複合性を特定**:
   - クライアントの `FixedUpdateNetwork()` が `HasStateAuthority` チェックなしで走っている
   - `ActivateRagdoll()` / `DeactivateRagdoll()` の中で全Rigidbodyに `isKinematic = false` を設定している
   - これが NetworkRigidbody の自動Kinematic設定を上書きしていた

### 原因の絞り込み

3つの問題が複合していた:

| # | 問題 | 影響 |
|---|------|------|
| 1 | クライアントでも物理が実行される | 再シミュレーション中に衝突判定が複数発火 |
| 2 | 再シミュレーション中の `OnCollisionEnter` 複数発火 | KnockoutForce 閾値を突破してKO |
| 3 | `isKinematic=false` の強制上書き | NetworkRigidbody の自動Kinematic管理と競合 |

---

## 3. 原因（なぜ起きていたか）

### コードレベルの原因

```csharp
// ❌ 変更前: HasStateAuthorityチェックなし
public override void FixedUpdateNetwork()
{
    if (!GetInput(out NetworkInputData data))
        return;

    // ↓ クライアント（InputAuthority）でも実行されてしまっていた
    _ragdollInput.ProcessInput(data);
    RagdollCommand cmd = _ragdollInput.CurrentCommand;

    UpdatePlayerState(cmd, _ragdollPhysics);  // ActivateRagdoll/Deactivate呼び出し含む
    _ragdollPhysics.UpdatePhysics(...);        // 全物理演算が走る
}
```

### なぜこのコードが問題だったか

Photon Fusion 2 の `Forecast OFF` モードでは:

- **StateAuthority（ホスト）**: Rigidbody が物理的にアクティブ → 物理を実行すべき
- **Proxy（他プレイヤーの表示）**: Rigidbody が NetworkRigidbody によって自動的に `isKinematic = true` に設定される → 物理を実行してはいけない
- **InputAuthority（クライアントの自キャラ）**: Proxy と同様に物理は実行してはいけない

`FixedUpdateNetwork()` は `HasInputAuthority` が `true` のインスタンスでも呼ばれる。
`InputAuthority ≠ StateAuthority`（ホスト/サーバーモードではホストが全オブジェクトの StateAuthority を持つ）
のため、クライアントの自キャラ（InputAuthority のみ）でも物理が動いていた。

### 背景にある原理: Photon Fusion の Authority モデル

[※理論] Photon Fusion 2 の Authority 概念:

```
[ホストモード]
  ホストプロセス: StateAuthority = ALL objects（全オブジェクトの物理を担当）
  クライアントプロセス: InputAuthority = 自分のキャラのみ（入力だけ提供）

[ServerMode（専用サーバー）]
  サーバー: StateAuthority = ALL objects
  クライアント: InputAuthority = 自分のキャラのみ
```

つまり:
- `HasStateAuthority` = 「このオブジェクトの物理状態を決定する権限がある」
- `HasInputAuthority` = 「このオブジェクトへの入力を提供する権限がある」

ホストモードでは **ホスト側の自キャラだけが両方 `true`**。
クライアントの自キャラは `HasInputAuthority = true` だが `HasStateAuthority = false`。

---

### Fusionの再シミュレーションとは

[※理論] Forecast OFF 設定の場合、Fusion は物理の「外挿」を使って補間する。
しかしクライアント側でも内部的に `RunClientSideResimulationLoop` が走ることがある。

再シミュレーション中は:
1. 過去のティックから現在まで物理を「巻き戻して再計算」する
2. `OnCollisionEnter` が同一衝突に対して複数回発火する可能性がある
3. `Runner.IsResimulation == true` で検出できる

`RagdollImpactContact.cs` の `IsResimulation` ガードは「再シミュレーション中の誤発火」を防ぐが、
「物理自体がクライアントで実行されること」は防げなかった。

---

## 4. 解決策（何をどう変えたか）

### 修正内容

```csharp
// ✅ 変更後: HasStateAuthorityガードを追加
public override void FixedUpdateNetwork()
{
    if (!GetInput(out NetworkInputData data))
        return;

    // Gang Beasts方式: ホストのみ物理実行、クライアントは補間表示のみ
    // GetInput()を先に呼ぶ理由: Fusionの入力パイプライン維持のため
    // （クライアント→サーバーへの入力送信は継続する必要がある）
    if (!Object.HasStateAuthority) return;

    _ragdollInput.ProcessInput(data);
    RagdollCommand cmd = _ragdollInput.CurrentCommand;

    MoveDirection = cmd.MoveDirection;
    FacingDirection = cmd.FacingDirection;
    LookDirection = cmd.LookDirection;

    UpdatePlayerState(cmd, _ragdollPhysics);
    _ragdollPhysics.UpdatePhysics(
        CurrentState, MoveDirection, FacingDirection, LookDirection,
        cmd.IsPunchingRight, cmd.IsPunchingLeft, Runner.DeltaTime
    );
}
```

**修正ファイル**: `Assets/Code/Scripts/Player/RagDollController.cs:149`

### コードの各行が「なぜこう書く必要があるか」

**`GetInput()` を `HasStateAuthority` ガードの前に置く理由:**

```csharp
// ❌ 間違い: GetInputをガードの後に置く
if (!Object.HasStateAuthority) return;
if (!GetInput(out NetworkInputData data)) return;  // ← 呼ばれない!
```

Fusion の入力システムでは `GetInput()` を呼び出すことで、クライアントが収集した入力データを
「受信済み」としてマークする。これを呼ばないと、ホスト側での入力処理に問題が起きる可能性がある。
[※推測] また、Fusion の内部バッファリング機構が `GetInput()` の呼び出しを期待している可能性がある。

```csharp
// ✅ 正解: GetInputを先に呼んでから返す
if (!GetInput(out NetworkInputData data)) return;  // 入力を「受信済み」とマーク
if (!Object.HasStateAuthority) return;              // 物理処理はホストのみ
```

### この修正で解消される問題

| 問題 | 解消の仕組み |
|------|------------|
| 再シミュレーション物理 | `UpdatePhysics()` がクライアントで呼ばれない |
| `isKinematic = false` の上書き | `ActivateRagdoll/Deactivate` がクライアントで呼ばれない |
| NetworkRigidbody との競合 | Kinematic 状態が NetworkRigidbody の管理に委ねられる |

### よくある間違い（アンチパターン）

```csharp
// ❌ HasInputAuthorityでガードしてしまう誤り
if (!Object.HasInputAuthority) return;
```

なぜダメか: ホスト側では「ホストが操作するキャラ」のみが `HasInputAuthority = true` になる。
他のプレイヤーのキャラ（Proxy）に対しては `HasInputAuthority = false` なので、
他プレイヤーの物理をホストが実行できなくなる。
ホストは全オブジェクトの物理を担当する必要があるため、`HasStateAuthority` が正解。

---

## 5. 検討した代替案

| 代替案 | 評価 | 不採用の理由 |
|--------|------|-------------|
| A: `IsResimulation` ガードのみ強化 | △ | `OnCollisionEnter` の誤発火は防げるが、`isKinematic` 競合は解消されない |
| B: `knockoutForce` の閾値を大幅に上げる | × | 根本的な解決ではない。正常なKOも検知できなくなる |
| C: `OnCollisionEnter` をクライアント側で完全無効化 | △ | `RagdollImpactContact` をクライアントで無効化する方法だが、StateAuthority チェックが分散して管理が複雑になる |
| D: `HasStateAuthority` ガード（Gang Beasts方式）★ | ○ | 物理実行の権限チェックを一箇所に集約できる。クライアントの Rigidbody は Kinematic のまま → NetworkRigidbody との競合が根本解消 |

**Gang Beasts方式の由来**: 物理ラグドールゲーム「Gang Beasts」が採用していると言われる
ホスト権威型物理アーキテクチャ。ラグドール特有のカオス的な物理では、クライアント予測の
誤差が大きく、再シミュレーションのコストが高い。ホスト一元管理の方が安定する。

### Gang Beasts方式のトレードオフ

**メリット:**
- 再シミュレーション関連の問題を根本排除
- クライアント側の物理計算が不要 → CPU 節約
- 実装がシンプル（1行追加で解決）

**デメリット:**
- RTT/2 の入力遅延が発生する（クライアントの操作がホストを経由して反映される）
- FPS やレーシングゲームでは許容できないが、ラグドール操作では体感上問題ない
  （ラグドール自体が「不正確な操作」前提のゲームデザインのため）

---

## 6. 教訓（今後同様の問題に遭遇したときのヒント）

### このバグのパターン

**「ネットワーク Authority の誤認識」パターン**

`HasInputAuthority` と `HasStateAuthority` の違いを意識していないと発生する。
「自分が操作するキャラなのだから、クライアントでも物理を実行してよい」という誤解が根本。

### 同じパターンのバグに遭遇したときの対処手順

1. **まず Authority を確認する**: ホストとクライアントで `HasInputAuthority` / `HasStateAuthority` をログ出力し、どの Authority でコードが走っているか確認
2. **`FixedUpdateNetwork()` の実行者を確認**: デバッグログで「何インスタンスで呼ばれているか」を特定
3. **Fusion のシミュレーションモードを確認**: `Runner.IsResimulation` と `Runner.Stage` をログ出力
4. **物理コードに Authority ガードがあるか確認**: `UpdatePhysics()` 系は必ず `HasStateAuthority` でガードされているべき

### 予防策

- `FixedUpdateNetwork()` に物理処理を書く際は **常に** `HasStateAuthority` チェックを先頭に入れる
- コードレビューチェックリスト項目に追加: 「物理処理は StateAuthority でガードされているか？」
- `RagdollImpactContact` のような衝突コールバックも StateAuthority でガードすることを検討

### 関連する理論/概念

[※理論] **Photon Fusion 2 の実行フロー（Forecast OFF の場合）**

```
毎ティック:
  StateAuthority（ホスト）: FixedUpdateNetwork() → 物理実行 → NetworkRigidbody が状態を送信
  Proxy（クライアント）:     FixedUpdateNetwork() → 物理スキップ → 受信した状態を補間表示
  InputAuthority（クライアント自キャラ）:
    Forecast ON の場合: ローカル物理予測 + 再シミュレーション（Gang Beasts方式では OFF）
    Forecast OFF の場合: StateAuthority と同じフロー（Gang Beasts方式と相性が良い）
```

参考: [Photon Fusion 2 State Authority](https://doc.photonengine.com/fusion/v2/manual/state-authority)

---

## 7. 自力で再実装するためのチェックリスト

- [ ] `HasStateAuthority` と `HasInputAuthority` の違いを説明できる
  - StateAuthority: オブジェクトの物理状態を決定する権限（ホストが全オブジェクトを持つ）
  - InputAuthority: 入力を提供する権限（各クライアントが自分のキャラのみ持つ）
- [ ] `FixedUpdateNetwork()` の先頭で `HasStateAuthority` チェックを入れる
- [ ] `GetInput()` は `HasStateAuthority` チェックの **前** に呼ぶ（入力パイプライン維持）
- [ ] `OnCollisionEnter` など衝突コールバックには `IsResimulation` ガードも入れる（多重防御）
- [ ] Forecast OFF の場合、Proxy の Rigidbody は NetworkRigidbody が自動 Kinematic 化する
  → `isKinematic` を手動で変更するコードは StateAuthority のみで実行させること
- [ ] NetworkRigidbody と手動 `isKinematic` 設定の競合に注意
  （ActivateRagdoll/DeactivateRagdoll 等でisKinematicを触る場合は StateAuthority ガードが必要）

---

**修正日**: 2026-02-18
**修正ファイル**:
- `Assets/Code/Scripts/Player/RagDollController.cs`（`FixedUpdateNetwork()` に `HasStateAuthority` ガード追加）
- `Assets/Code/Scripts/Player/RagdollImpactContact.cs`（`IsResimulation` ガード、別コミット済み）

**修正コミット**: `bec3bb2`
