# 2026-07-12 しゃがみ中の移動速度半減（crouchSpeedMultiplier）

## 問題

しゃがみ入力（`ButtonCrouch` → `RagdollInputCommand.IsCrouching`）は
ネットワーク入力としては既に collect / replicate されていたが、
**消費側が存在せず、しゃがんでも挙動が何も変わらなかった**。
将来 R.E.P.O のように「足音を消す・隠密移動」の手段としてしゃがみを使う構想があり、
その第一歩として移動速度を半分にする。

## なぜこのアプローチか（不採用案も）

採用: **Dash と同じ「MoveSpeed への倍率」パターン**

- `RagDollController.MoveSpeed` には既に Dash の倍率
  （`profile.dashSpeedMultiplier`、IsDashing 時のみ適用）が実装済み。
  しゃがみも同じ場所に `crouchSpeedMultiplier`（既定 0.5）を掛けるだけで、
  物理トライアドの流れ（Profile → Controller → Physics の `_context.MoveSpeed`）に
  そのまま乗る。`RagDollPhysics` 側は無変更。
- **resim 安全**: IsCrouching は毎 tick `ProcessInput` 後の `CurrentCommand` から読むため、
  ホスト権威 sim とクライアント予測の両経路で「同一入力 → 同一倍率」になる
  （Dash の既存コメントと同じ理由）。倍率を `[Networked]` にする必要はない
  —— 入力自体が replicate されるので、状態を追加同期しなくても両端で一致する。

不採用案:

- **Dash と排他（しゃがみ中は Dash 無効）**: 仕様が増えるわりに現状の利得がない。
  同時押しは乗算（1.8 × 0.5 = 0.9 倍）とし、Tooltip に明記した。
  隠密仕様を本実装するときに改めて設計する。
- **入力側（InputCollector）で direction を減衰**: 入力の意味（方向）と
  ゲームルール（速度）が混ざる。速度規則は Controller/Profile の責務。

## 仕組み

- `RagdollProfile.crouchSpeedMultiplier`（`[Range(0,1)]`、既定 0.5）を追加。
- `RagDollController.MoveSpeed` を
  `moveSpeed × dash倍率(≥1) × crouch倍率(0〜1)` に変更。
  `IsCrouching` プロパティ（`_ragdollInput.CurrentCommand.IsCrouching`）を追加。
- 既存アセット（MainPlayer_AprProfile.asset）はフィールド未保存だが、
  Unity のデシリアライズはフィールド初期化子の値（0.5）を使うため再設定不要。

## 検証

- EditMode テスト `RagdollProfileTuningTests` に既定値 0.5 と
  実アセットの範囲チェック（0〜1）を追加。13/13 パス。
- 残り: Play モードでしゃがみ歩行の体感確認（半減でラグドールの歩行が破綻しないか。
  ステップ周期は入力の大きさでスケールするため速度半減自体は安全なはずだが要目視）。

## 自力再実装チェックリスト

- [ ] 「入力は replicate されるので、入力から決定的に導ける値は同期不要」を説明できる
- [ ] resim（クライアント予測の巻き戻し再実行）で倍率が一致する理由を説明できる
- [ ] 物理トライアド（Profile / Controller / Physics）のどこに何を足すべきか判断できる
- [ ] シリアライズ済みアセットに新フィールドを足したとき、既定値がどう決まるか説明できる
