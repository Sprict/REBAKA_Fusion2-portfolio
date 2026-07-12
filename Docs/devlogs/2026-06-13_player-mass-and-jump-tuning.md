# プレイヤーのRigidbody Mass再配分とジャンプ力調整 (2026-06-13)

## 問題

1. **Mass配分が非物理的だった**
   `newAPRPlayer.prefab` の本体15パーツのRigidbody Massが、人体の質量分布とかけ離れていた。
   - 胴体 `APR_Body` = 1.0kg なのに 手 `APR_Hand` = 0.5kg（手が胴の半分）
   - 足 `APR_Foot` = 1.0kg（胴体と同じ重さ）
   - 体幹より末端の方が重い箇所があり、バランス・ジャンプ・転倒挙動が不自然になりうる。

2. **ジャンプ力が新Massと未整合**
   「1mの段差は登れるが2mの段差は登れない」程度のジャンプ力に調整したい。
   ただしジャンプ高さはMass配分に依存するため（後述）、Massを変えたらジャンプ力も再設定が必要。

## 対象ファイル

- `Assets/Level/Prefabs/newAPRPlayer.prefab` … 実プレイヤープレハブ（CLAUDE.md の `APR_Root.prefab` 記述は旧情報）
- `Assets/Settings/MainPlayer_AprProfile.asset` … 現役の `RagdollProfile`。
  プレハブの `RagDollController.profile`（GUID `bfac459fc7c27654e9f7b38ec2c28040`）が指すアセット。
  `MainPlayer_PIDProfile.asset` は**未使用**なので触っていない。
- `RagdollProfile.cs` の `jumpForce = 54f` は ScriptableObject の**デフォルト値**であり、
  ランタイムで使われるのは上記 `.asset` にシリアライズされた値。コードのデフォルトは変更不要（変えても既存アセットに反映されない）。

## ジャンプの仕組み（最重要・物理モデル）

実際に効いているジャンプ処理は `RagDollPhysics.cs:995 ProcessJumpingPhysics()`:

```csharp
var v3 = rigidBody.transform.up * _context.JumpForce; // rigidBody = Root のみ
v3.x = rigidBody.linearVelocity.x;  // 水平成分は維持
v3.z = rigidBody.linearVelocity.z;
rigidBody.linearVelocity = v3;       // 速度を「直接設定」(AddForce ではない)
```

ポイント:

- **`jumpForce` は力ではなく「Rootに与える上向き速度 (m/s)」**。AddForce でないのでMassは初速計算式に直接は出てこない。
- ただし速度を与えるのは **Root 1パーツだけ**。残り14パーツはConfigurableJointでぶら下がっており、
  Rootが上昇する際にジョイント経由で引き上げられる。
- したがって全身の「実効初速」は、Rootの運動量が全身に分配される度合いで決まる。
  粗い運動量保存近似では:

  ```
  実効初速 v_eff ≈ jumpForce × (m_root / M_total)
  apex(最高到達高さ) h = v_eff² / (2g)   → h ∝ jumpForce²
  ```

  実際はジョイントがバネ結合で、かつ接地中は `Jumping` 状態が継続して毎tick Root速度が
  `jumpForce` に再設定されるため（状態遷移は `RagdollStateEvaluator.cs:17`、接地が外れると離脱）、
  上式は下限寄りの近似。正確な高さは解析的には出せず**実測が必要**。

- 状態遷移: `command.IsJumping && isPlayerGrounded` の間だけ `Jumping`。
  離地すると `isPlayerGrounded=false` で `Jumping` を抜け、以降は放物運動。

**帰結**: Rootを重くすると「Rootが速度を保ったまま軽い末端を引き上げやすくなる」ため、
同じ `jumpForce` でもジャンプが伸びる。Mass変更とジャンプ力はセットで考える必要がある。

## アプローチ：Mass再配分

### なぜ「合計質量を維持して比率だけ人体に寄せる」か

検討した選択肢:

| 案 | 内容 | 採否 |
|---|---|---|
| A | 合計≈15kgを維持し、比率だけ人体解剖学に寄せる | **採用** |
| B | 現実的な体重(40〜50kg)に絶対値ごと変更 | 不採用 |
| C | near-massless ヘルパー(0.01kg)も「正常値」に修正 | 不採用 |

- **B不採用の理由**: バランスPID（`balanceStrength=5000 / coreStrength=1500 / limbStrength=500`）は
  トルクを生む。角加速度 = トルク / 慣性モーメント ∝ 1/質量 なので、質量スケールを大きく変えると
  「そもそも直立できるか」が変わる。合計を保てばバランス制御への影響を最小化できる。
- **C不採用の理由**: `Other/Sphere(n)` 群（12個・各0.01kg・SphereCollider）は装飾/補助コライダーの
  意図的な near-massless ヘルパー。バグではないので触らない。これを1kg等にすると合計が激変しバランス崩壊。

### 新旧Mass（本体15パーツ・合計15.0kg維持）

| パーツ | 旧 | 新 | 体重比の目安 |
|---|---|---|---|
| APR_Root（骨盤） | 2.0 | **3.0** | 体幹下部 |
| APR_Body（胸腹） | 1.0 | **3.8** | 体幹上部・最重量 |
| APR_Head | 1.0 | **1.2** | ~8% |
| APR_UpperArm ×2 | 1.0 | **0.5** | ~2.8% |
| APR_LowerArm ×2 | 1.0 | **0.3** | ~1.6% |
| APR_Hand ×2 | 0.5 | **0.15** | ~0.6% |
| APR_UpperLeg ×2 | 1.0 | **1.5** | ~10% |
| APR_LowerLeg ×2 | 1.0 | **0.8** | ~4.7% |
| APR_Foot ×2 | 1.0 | **0.25** | ~1.4% |

体幹 (Root+Body) = 6.8kg ≈ 45%、人体の体幹比(~50%)に近づけた。
`Other/Sphere(n)` 12個 × 0.01kg は不変。総計 15.12kg。

適用は YAML直編集ではなく `PrefabUtility.LoadPrefabContents → SaveAsPrefabAsset`（fileID破損を避けるため）。

## アプローチ：ジャンプ力

### 設定値: jumpForce 15.5 → 12.0（`MainPlayer_AprProfile.asset`）

- 旧Mass(Root=2.0)→新Mass(Root=3.0)でRoot質量比が **1.5倍**。前述の `v_eff ∝ jumpForce × m_root/M_total`
  より、同じ高さを保つには `jumpForce` を約 **2/3倍** にする必要 → 15.5 × 0.667 ≈ **10.3**。
- そこへ「1m段差を確実に越える」マージンを少し乗せて **12.0** を初期値とした。
- 狙う apex（最高到達高さ）は **約1.2〜1.5m**（1m段差は越え、2m段差は越えない中間帯）。

### ⚠️ この値は実機未検証の推定値

検証経路の制約: 検証用段差（`Platform_H1`=上面1.0m / `Platform_H2`=上面2.0m、`Obs_Cube1`=1.0m / `Obs_Cube2`=2.0m）は
**Test_Playground** にあるが、`SessionManager` は①OnGUIの「Host」ボタン手動起動 ②`RequiredNetworkScenePath`で
**Main_Backup.unity へ強制シーン切替**する設計（`SessionManager.cs:351`）。このためTest_Playground単体では
プレイヤーがspawnせず、自動での段差検証ができなかった。最終確認はユーザーが手動で行う方針。

### 実機での微調整指針（apex ∝ jumpForce²）

1. Playして1mと2mの段差で確認。
2. **1mも越えられない** → jumpForce を上げる。高さを1.5倍にしたいなら ×√1.5 ≈ ×1.22（例 12→14.7）。
3. **2mも越えてしまう** → jumpForce を下げる。
4. 二分探索で apex≈1.3m に追い込む（高さは jumpForce² に比例＝単調なので収束は速い）。
5. ラグドールは空中で姿勢が崩れ「登れたか」の判定がばらつくため、複数回試行で平均を見る。

## 自力再実装チェックリスト

- [ ] 実プレイヤープレハブは `newAPRPlayer.prefab`（`APR_Root.prefab` ではない）
- [ ] 編集すべきProfileは `RagDollController.profile` が指すアセット（GUIDで特定）。今回は `MainPlayer_AprProfile.asset`
- [ ] `jumpForce` は「力」でなく「Rootに与える上向き速度」。`linearVelocity` 直接設定であることを `ProcessJumpingPhysics()` で確認
- [ ] ジャンプ高さはMass配分に依存（Rootを重くすると伸びる）。Massとjumpはセットで調整
- [ ] Massは合計を維持して比率だけ人体に寄せる（バランスPIDが質量前提のため）
- [ ] `Other/Sphere(n)` の 0.01kg は意図的ヘルパー。触らない
- [ ] Mass変更後はまず**直立30秒**を確認してからジャンプ調整（バランス崩壊チェック）
- [ ] apex ∝ jumpForce² の関係で二分探索

## バグ修正: ジャンプ高さがスペース押下時間で変わる問題（ラッチ化・2026-06-13追記）

### 症状

スペースキーを長く押すほどジャンプが高くなり、チョン押しだと低い。`jumpForce` 一定でも
高さが不定 → 「1m登れて2m登れない」を確定的に調整できない。

### 根本原因（毎tick初速再設定）

ジャンプ初速 `linearVelocity.y = jumpForce` は「押した瞬間1回」ではなく、Jumping 状態が
続く限り **毎 FixedUpdateNetwork tick で再設定**されていた。連鎖:

1. `RagDollInput.cs:43` `IsJumping = Buttons.IsSet(ButtonJump)` … 押しっぱなしの間ずっと true（エッジ検出ではない）
2. `RagdollStateEvaluator.cs:17` `IsJumping && isPlayerGrounded` の間ずっと `Jumping`
3. `RagDollPhysics.IsGrounded()` … 足接触 or Root下 1.05m(`BalanceHeight`) のレイキャストで true。離陸直後もしばらく接地扱い
4. `RagDollPhysics.ProcessJumpingPhysics()` … `Jumping` の間毎tick `velocity.y` を `jumpForce` に直接上書き

毎tick velocity.y が +jumpForce に固定される間は重力が減速できない。長押し＝接地ゾーン(1.05m)を
抜けるまでフル速度で固定され続け、フル速度で離脱 → 高い。チョン押し＝早く固定が外れ重力に
削られ → 低い。これが「押下時間で高さが変わる」正体。

### 修正: ラッチ（1入力につき初速は1tickだけ）

`RagDollPhysics` に `_jumpVelocityApplied` フラグを追加し、Jumping 開始の1tickだけ初速を与える。
Jumping から抜けたら（離地 or ボタン離し → `state != Jumping`）フラグを戻して再武装する。

- 変更ファイル: `RagDollPhysics.cs` のみ（フィールド追加 ＋ `ProcessJumpingPhysics` 冒頭で早期return ＋ switch直前でリセット）
- 挙動: 1回のジャンプ高さが押下時間によらず確定する。空中で押しっぱなしだと着地時に
  再ジャンプ（連続ホップ）になるのは許容仕様（各ホップの高さは確定）。

### ネットワーク的注意（リシム安全性）

`_jumpVelocityApplied` は `[Networked]` ではないプレーンなインスタンスフィールド。これが安全なのは
**現構成（`useForecastPhysics: 0` ＋ `GameMode.Host`）ではジャンプ物理がホスト権威でのみ実行され、
ホストは自分の権威stateをリシム（ロールバック再計算）しない**ため（`HasAuthoritativePhysics()` は
Forecast OFF時 `HasStateAuthority` を返す → ホストのみ true）。
⚠️ もし将来 Forecast Physics を再有効化すると全クライアントで予測・リシムが走り、この
プレーンフィールドはリシム間で不整合になりうる。その場合は「押下のエッジ」を `[Networked]` な
前tickボタン状態から導く（`NetworkButtons.GetPressed`）等のリシム安定な実装へ変える必要がある。

### ⚠️ 副作用: jumpForce の再調整が必要

毎tick固定（実質的に複数tick加速）を「1tickのみ」に変えたため、**同じ jumpForce でも
ジャンプは大幅に弱くなる**。

- `[※理論]` 初速を Root(質量3) に1tickのみ与えると、全身(15kg)への実効初速は
  おおよそ `jumpForce × m_root / M_total = jumpForce × 3/15 = jumpForce/5`。
  jumpForce=12 なら全身≈2.4 m/s → apex≈0.29m で **1m に届かない可能性が高い**。
- 実際はジョイントのバネ結合で値がぶれるため実測した。**実測結果: jumpForce = 27 で確定**。
  12→27 ＝ 約2.25倍。apex ∝ jumpForce² なので狙い高さは約5倍で、1tick適用により弱まった分を補う形。
  なお上の `jumpForce/5` 概算（≈22〜25見込み）はバネ減衰を無視した**楽観値**で、実機はそれより
  やや高い 27 を要した。1m はぎりぎり登れ・2m は登れない＝狙った閾値挙動を確認。

### ラッチ後にジャンプが弱い／フニャつく場合の代替案

- A: 初速を Root だけでなく全パーツに1回与える（クリーンな弾道。ただし物理モデル変更）
- B: 固定tick数（例: 5tick）だけ初速を与える「固定ジャンプ持続時間」方式
  （押下時間非依存のまま強さを確保できる）

## 確定事項 / 残課題

**確定（実機検証済み・2026-06-13）:**
- `jumpForce = 27` に確定（ラッチ化後の実測）。1m はぎりぎり登れ・2m は登れない＝狙った閾値挙動。
- Mass再配分後（特に APR_Body 1.0→3.8 の慣性増）でも**直立30秒は崩れず安定**。バランスPIDは現状維持でOK。

**残:**
- 検証環境の食い違い（段差=Test_Playground / ネット起動=Main_Backup）の解消は別課題として残る。
- ラッチ後のジャンプ感が硬い/物足りない場合の代替案A・B（上記）は将来の調整余地。
