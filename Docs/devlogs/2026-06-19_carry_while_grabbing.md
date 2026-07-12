# つかみ(Reaching)中もプレイヤーが移動できるようにする（運搬対応）

- 日付: 2026-06-19
- 種別: fix / feat（プレイヤー操作・物理）
- 対象: `Assets/Code/Scripts/Player/RagDollPhysics.cs`（状態別物理ディスパッチ）
- 関連: [[project-peer-sync-pure-interpolation]]（Plan B 実機検証中に発覚）

---

## 1. 問題

物をつかむ（グラブ）と State が `Reaching` になり、その間プレイヤーが**一切移動できない**。
これでは掴んだ物を運べない（運搬不可）。

## 2. 根本原因（既存設計。私のグラブ変更ではない）

- 状態評価 `RagdollStateEvaluator.Resolve` は優先順位チェーン:
  Jumping → Ragdoll → Punching → **Grabbing→Reaching** → Walking → Idle。
  つまり掴み入力があると Walking より先に `Reaching` が選ばれ、移動入力があっても Walking にならない。
- 物理ディスパッチ `RagDollPhysics` の `switch(state)` では、実際に体を動かす
  `ApplyMovementForce(MoveDirection)`（linearVelocity を目標速度へ Lerp）が **Walking 状態でしか呼ばれない**。
  `Reaching` は `ProcessReachingPhysics`（腕ジョイントの targetRotation を設定する“腕を伸ばすポーズ”だけ）を呼ぶ。
- 結果、掴んでいる間は移動力が一切適用されず静止する。

この挙動は以前からあったが、グラブ検出をホスト側へ移して**グラブが安定して持続する**ようになって初めて
顕在化した（以前はクライアント側検出が不安定で掴みがすぐ解けていたため気づかれにくかった）。

## 3. 修正

`RagDollPhysics` の `Reaching` ケースで、Walking と同じ移動・歩行処理を併用する:

```csharp
case PlayerState.Reaching:
    ApplyMovementForce(command.MoveDirection);          // 追加: 移動力（運搬移動）
    if (!_isRagdoll)
        ProcessWalking(command.MoveDirection, deltaTime); // 追加: 歩行（足の運び）
    ProcessReachingPhysics(command.LookDirection);        // 既存: 腕を伸ばすポーズ
    break;
```

- 立ち止まって掴むだけのときは `MoveDirection ≈ 0` のため `ApplyMovementForce` は目標速度ほぼ 0 で実害なし、
  `ProcessWalking` も `sqrMagnitude > 0.01` ガードで何もしない。移動入力があるときだけ歩く。
- ルートの向き（FacingDirection 追従）は switch の前段 `UpdateRootRotation` で状態に依らず処理済み。
- ホスト権威: この物理処理はホスト（StateAuthority）側で実行され、結果がクライアントへ同期される。
  クライアントのプロキシは kinematic 補間表示のままで変更不要。

### 不採用の代替案
- (却下) 状態評価で「掴み＋移動なら Walking を返す」: Reaching の腕ポーズが出なくなり、掴み姿勢が崩れる。
- (却下) 移動を状態から切り離して常時適用: 影響範囲が大きく、Jumping/Punching 等への副作用が読みづらい。
  まずは Reaching への最小追加に留める。

## 4. 検証

- main / clone（ParrelSync）両エディタで再コンパイル＋ weave 維持を確認（NetworkInputData weaved / CreateFromLoadedAssemblies OK）。
- 実機確認（ユーザー）: 掴んだまま WASD で移動でき、運搬できること。重い Treasure を掴んだ際の
  バランス（腕を伸ばしつつ歩く）に破綻がないかは要確認。必要なら運搬時の移動速度に係数を掛ける調整余地あり。

## 5. 残課題

- 運搬時の移動速度ペナルティ（重量感の演出）は未実装。必要になれば `ApplyMovementForce` に係数を渡す。
- 「押した時のプレイヤーと Cube の隙間」は別問題として未解決（[[project-peer-sync-pure-interpolation]] 参照）。
