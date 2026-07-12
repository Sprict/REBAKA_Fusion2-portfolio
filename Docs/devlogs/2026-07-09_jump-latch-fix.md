# 2026-07-09 ジャンプができたりできなくなったりするバグの修正（バグ6）

## 対象ファイル

- `Assets/Code/Scripts/Player/RagDollPhysics.cs`

## 1. 問題

姉との実機テスト（Windows PC ホスト / MacBook Pro 2018 クライアント）で見つかった7件のバグのうち、
バグ6「ジャンプができたりできなかったりする」を調査・修正した。

症状: プレイ中、最初は正常にジャンプできていたのに、あるジャンプを境に以降ジャンプボタンを押しても
一切ジャンプしなくなる。ホスト単独プレイでも再現したため、ネットワーク同期は主因ではないと判断した。

## 2. なぜこのアプローチか（仮説→反証の過程）

いきなり修正するのではなく、まず「何が起きているか」を実機ログで確定させてから直す方針を取った。
理由: ラグドール物理は`RagDollPhysics.cs`/`RagDollController.cs`/`RagdollProfile.cs`が絡み合う上、
足接地・ジャンプ状態遷移・ラッチ管理という3つの独立した仕組みが関与しており、勘での修正は
別の症状を誘発するリスクが高いと判断したため。実際、この判断は後の試行1・試行2で的中した
（下記参照）。

### 仮説1（棄却）: ジャンプ初速の方向がズレている

`rigidBody.transform.up`がワールド上方向からズレて、ジャンプ初速の上方向成分が不足しているのでは
ないか、という仮説。`ProcessJumpingPhysics()`に`transform.up`、`transform.up.y`、`beforeVel`、
`afterVel`の診断ログを追加して実機検証した。

結果: `transform.up.y`は常に0.98〜1.00、`afterVel.y`もJumpForce(27.00)に近い正常値だった。
**この仮説は棄却**。

### 決定的な手がかり: コンソールログが出なくなる

ユーザーから「ジャンプが失敗するようになってからはコンソールログが何も出なくなった」という報告。
これは`ProcessJumpingPhysics()`冒頭の早期リターン

```csharp
if (_jumpVelocityApplied) return;
```

で毎tick弾かれている、つまり「1回のジャンプにつき初速付与を1回に制限するラッチ」
（`_jumpVelocityApplied`フラグ）が`true`のまま固着している可能性を示唆した。

### state遷移自体は正常と確認

BalanceDebugのState表示で「ジャンプができない時でもジャンプボタンを押している間はIdle→Jumpingに
状態遷移する」という追加報告があった。`RagdollStateEvaluator.Resolve()`の
`command.IsJumping && isPlayerGrounded`は真になっている、つまりstate遷移自体は正常に起きている
ことが判明。これにより原因はstate遷移ではなくラッチ側に絞り込まれた。

### ラッチ固着の実証

診断ログをラッチチェックの直前（早期リターンより手前）に移動し、`_jumpVelocityApplied` /
`_isLeftFootGrounded` / `_isRightFootGrounded` / `_isAnyFootGrounded`を毎tick出力した。
実機ログで、最後に成功したジャンプ直後のログとして以下が確認された:

```text
[JUMP_DIAG_ENTRY] jumpVelocityApplied=False leftFootGrounded=True rightFootGrounded=False anyFootGrounded=True
```

このジャンプの直後からジャンプ不能になった。つまり**左足の`IsGrounded`が`True`のまま固着**し、
右足は正常に`False`になったが、OR演算（`anyFootGrounded = leftFootGrounded || rightFootGrounded`）で
`anyFootGrounded`が永久に`True`のまま残り、旧来の再武装条件（「足が接地を失ったらラッチを戻す」）が
二度と成立しなくなった。

## 3. 仕組み（根本原因の構造）

### 3.1 旧設計

修正前は次の再武装ロジックだった（`LastFootGrounded`は`_isAnyFootGrounded`のプロパティ）:

```csharp
// ジャンプ初速の再武装: 足が実際に地面接触を失った（LastFootGrounded == false）ときだけフラグを戻す。
if (!LastFootGrounded)
    _jumpVelocityApplied = false;
```

このコードのコメントには「過去に`IsGrounded()`（BalanceHeight圏内のraycastも含む合成値）を使うと
"ジャンプが1回しかできなくなる"regressionを起こした」という教訓が既に記されていた。今回のバグは、
その回避策として使っていた「足接触のみを見る`LastFootGrounded`（コヨーテタイム込み）」自体が、
別の経路で固着しうることが原因だった。

### 3.2 なぜ左足が固着しうるか

`RagdollFootContact.cs`（左右の足それぞれ独立した`NetworkBehaviour`）は、
`OnCollisionEnter`/`OnCollisionStay`で地面レイヤーとの接触を検出し、`_groundedTimer`
（コヨーテタイム0.1秒）が切れた時だけ`OnFootGroundedChanged(isLeftFoot, false)`イベントを
発火する**エッジトリガー**方式（`Assets/Code/Scripts/Player/RagdollFootContact.cs`
`FixedUpdateNetwork()`）。

```csharp
// 接触フラグをリセット（次のフレームでOnCollisionStayが呼ばれなければ非接地と判断）
_hasLastGroundContact = false;
```

もし左足のコライダーが地面判定レイヤーの何かと接触し続ける状況（自己接触・地形へのめり込みなど、
具体的な引き金は未特定 [※未確認]）になると、`OnCollisionStay`が呼ばれ続けて`_groundedTimer`が
切れず、`false`エッジが永久に発火しない。

`RagDollController.cs`の`OnFootGroundedChanged`（イベント駆動でのみ呼ばれる。
`RagdollControllerContracts.cs`の`IRagdollGroundingSink`インタフェース経由）から
`RagDollPhysics.SetFootGroundedInfo()`が更新する`_isAnyFootGrounded`（`LastFootGrounded`）も、
このイベントが来ない限り古い値のまま。つまり「足の接地状態」はジャンプの再武装に使うには
信頼できない信号だった。

### 3.3 デッドロックの構造（一般化）

```text
エッジトリガー方式のイベント (OnFootGroundedChanged(false))
  ↓ 発火しないと
_isAnyFootGrounded が古い true のまま固着
  ↓ を信号源にすると
再武装ロジック (!LastFootGrounded で戻す) が永久に不成立
  ↓
_jumpVelocityApplied が true のまま → 永久にジャンプ不能
```

片方の足の接触判定がどこかで詰まっただけで、OR演算の合成値が永久に汚染される。
イベント駆動の接地判定にラッチ解除を依存させる設計は、こういう固着に弱い、という一般化できる
教訓。

## 4. 修正の試行錯誤（3段階）

いずれも「1回のジャンプ入力につき初速は1回」という制約を、**何を信号源に再武装するか**という
設計判断のバリエーションだった。

### 試行1（不採用）: 最低滞空時間 + 着地ポーリング方式

離陸エッジ（false化イベント）を待つのではなく、「最低滞空時間（0.15秒、コヨーテタイム0.1秒より
長い値）経過後、毎tick`LastFootGrounded`をポーリングして`true`になったら再武装する」という
レベルトリガー方式に変更した。片足が固着していても、もう片方の足が正しく着地イベントを発火すれば
OR演算で救える、つまり構造的にデッドロックしなくなる、という設計意図だった。

しかし実機検証で新たな不具合が判明した: 「歩き始めてから0.5秒後くらいにジャンプボタンを
長押しすると大ジャンプ（2段ジャンプ）になる」。

原因: 走行中の踏み出し足はジャンプ入力の瞬間も実際にまだ地面へ接触しているため、0.15秒の
最低滞空時間を過ぎても`LastFootGrounded`が「離陸直後の残留true」ではなく素で`true`のままに
なり、ボタン長押し中に誤って再武装され、上昇中に2回目の初速が加算されてしまう。

**教訓**: 足の接地状態は「歩行中は常に何らかの形でtrueになりうる」ため、時間経過だけでは
「ジャンプ後の残留」と「歩行中の正常接地」を区別できない。

### 試行2（部分採用）: ボタン解放基準への転換

ここで「足の接地状態」をジャンプ回数制御の信号に使うこと自体が誤りだと気づいた。
「1回の押下につき初速は1回」という制約は、本来ボタンの押下/解放と一対一であるべきで、
地面判定とは独立した話のはずである。そこで地面判定と無関係な、ボタン状態そのものを
再武装の信号にする方式へ変更した:

```csharp
if (!command.IsJumping)
    _jumpVelocityApplied = false;
```

これにより長押しでの2段ジャンプは解消した。しかし実機検証で三度目の不具合が判明した:
「ジャンプボタンを連打すれば簡単にいつでも大ジャンプができる」。

原因: 離陸直後のコヨーテタイム(0.1秒)＋ラグドール足の物理的な接地解除の遅れにより、
着地判定(`isPlayerGrounded`)がジャンプ直後も一瞬`true`を維持し続ける窓が存在する。
その間に連打（離す→押す）すると、ラッチはボタン解放で即座に解除済みのため、
まだ地上と判定されているstateのままProcessJumpingPhysics()が再発火してしまう。

### 試行3（最終採用）: 発火条件への物理的ガード追加

再武装ロジック（ボタン解放基準、試行2のまま維持）に加えて、`ProcessJumpingPhysics()`の
発火直前に「上昇中は再ジャンプできない」という物理的に自明な制約を、再武装ロジックとは
独立したガードとして追加した:

```csharp
// ジャンプ直後の上昇中はこの速度を超えていれば「まだ跳んでいる最中」とみなし、
// 再発火をブロックする（連打による空中2段ジャンプ対策）。
private const float AscendingVelocityGuard = 1.5f;

private void ProcessJumpingPhysics()
{
    if (_jumpVelocityApplied)
        return;

    var rigidBody = _bodyRigidbodies[IndexRoot];

    if (HasAuthoritativePhysics())
    {
        if (rigidBody.linearVelocity.y > AscendingVelocityGuard)
            return;

        var v3 = rigidBody.transform.up * _context.JumpForce;
        v3.x = rigidBody.linearVelocity.x;
        v3.z = rigidBody.linearVelocity.z;
        rigidBody.linearVelocity = v3;
        _jumpVelocityApplied = true;
    }
}
```

このガードは再武装ロジック（`if (!command.IsJumping) _jumpVelocityApplied = false;`）とは
コードパス上完全に独立している。そのため再武装の仕組みを今後どう変えても揺らがない。
垂直速度という「足の接地状態を経由しない」直接的な物理量を見ることで、コヨーテタイムや
接地判定の信頼性に依存しなくなった。

`AscendingVelocityGuard = 1.5f`はバランス維持のための上下方向の揺れ（通常時のノイズ）より
十分大きく、ジャンプ初速27.00 (`JumpForce`) よりは十分小さい値として選んだ
[※未確認: 数値27.00は`RagdollProfile`の実測値、閾値1.5fは実機検証で問題なく機能した値であり
理論的な最適値の導出はしていない]。

### 代替案（不採用）: コヨーテタイムの廃止

ユーザーから「コヨーテタイムを廃止してはどうか」という代替案の提案があったが、以下の理由で
不採用とし、速度ガード方式を維持する判断をした:

- コヨーテタイムはプラットフォーマーで広く使われるゲームデザイン上の慣習 [※推測] であり、
  エッジからの着地/ジャンプの操作感を良くする役割がある。廃止すると別の不満（エッジでの
  タイミングのシビアさ）を生みうる。一次資料での裏取りはしていない。
- 速度ガードは既にコヨーテタイムの有無に関わらず連打2段ジャンプを防げている（物理的な上昇速度
  という直接指標で判定するため、コヨーテタイムの長さに影響されない）。
- 今回の左足固着バグ（調査の発端）自体はコヨーテタイムとは別の原因（`OnCollisionStay`の
  継続発火）であり、コヨーテタイムを廃止しても解消しない。

## 5. 実機検証結果

以下3パターンを同一セッションで確認し、すべて問題なし:

1. タップ連打（着地→単押し→着地→単押し…）: 正常にジャンプできる
2. 長押し: 大ジャンプは起きない
3. 連打スパム（着地直後や空中で素早く連打）: 空中2段ジャンプは起きない

## 6. 未解決の既知課題

「なぜ左足の`IsGrounded`が固着したのか」という根本原因（`OnCollisionStay`が継続発火し続けた
直接の引き金——自己接触なのか地形へのめり込みなのか等）は**未特定のまま** [※未確認]。

今回の速度ガードは「足の固着がジャンプ回数に影響しなくなる」ようにしただけで、固着自体は
解消していない。固着したままだと`isPlayerGrounded`（`RagDollController.IsPlayerGrounded()`）や
`movementControlMultiplier`（空中/地上の移動力係数、`RagDollPhysics.UpdatePhysics()`内
`isGrounded ? 1f : _context.AirControlMultiplier`）が誤った値のままになる可能性があり、
別の症状（例: 空中にいるはずなのに地上と同じ移動力になる等）として表面化しうる。

追加調査する場合は`RagdollFootContact.OnCollisionStay`に`collision.gameObject.name`と
`.layer`を一時的にログ出力すれば、固着の直接原因（何と接触し続けているか）を特定できる。
7/12のポートフォリオ締切を優先し、今回は「既知の潜在課題」として記録し先送りする判断とした。

## 7. 自力再実装チェックリスト

- [ ] 「1回の押下につき初速は1回」という制約を実装する前に、**何を再武装の信号源にするか**を
      明示的に決める。候補は最低3つある: (a) 足の接地状態、(b) ボタンの押下/解放、
      (c) 物理速度（上昇中かどうか）。今回の結論は「(b)を主とし、(c)を独立したガードとして
      併用する」で、(a)は不採用。
- [ ] (a) 足の接地状態を信号源にする場合、その接地判定が**エッジトリガー**（変化の瞬間だけ
      イベント発火）かどうかを確認する。エッジトリガーの場合、イベントの取りこぼし（片方の
      足だけ固着する等）でOR演算の合成値が永久固着するリスクがあることを理解しておく。
- [ ] (b) ボタン解放を信号源にする場合、それだけでは「離陸直後の接地判定残留窓」で連打による
      多段ジャンプが起きうることを想定し、(c) 物理速度による独立ガードを併用する。
- [ ] (c) 物理速度ガードを入れる場合、閾値は「バランス維持ノイズより十分大きく、ジャンプ初速
      より十分小さい」範囲で実機検証しながら決める。理論値からの逆算ではなく実測ベースで良い。
- [ ] 修正のたびに実機で最低3パターン（タップ連打・長押し・連打スパム）を確認する。1パターンの
      修正が別パターンを壊すことが実際に2回起きた（試行1→2段ジャンプ、試行2→連打2段ジャンプ）。
- [ ] バグの原因切り分けは、まず「何が起きなくなったか」（今回は「ログが出なくなった」）から
      早期リターンの存在を疑い、次に「どのフラグが固着しているか」を診断ログで実証してから
      修正する、という順序を踏むと不要な修正の往復を避けやすい。

## 8. 関連ファイル・箇所

- `Assets/Code/Scripts/Player/RagDollPhysics.cs`
  - `ProcessJumpingPhysics()`（発火条件・速度ガード）
  - `UpdatePhysics()`内のラッチ再武装ロジック（485〜504行目付近）
- `Assets/Code/Scripts/Player/RagdollFootContact.cs`
  - `FixedUpdateNetwork()`（コヨーテタイム管理）
  - `OnCollisionEnter`/`OnCollisionStay`（エッジトリガーの発火源）
- `Assets/Code/Scripts/Player/RagDollController.cs`
  - `OnFootGroundedChanged()`（`IRagdollGroundingSink`実装）
- `Assets/Code/Scripts/Player/RagdollControllerContracts.cs`
  - `IRagdollGroundingSink`インタフェース定義
