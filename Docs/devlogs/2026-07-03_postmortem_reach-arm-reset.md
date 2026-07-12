# ポストモーテム: Reach終了後、次のReachで腕が正面を向かない（2026-07-03）

関連: [2026-07-03_reach-arm-swing-with-pose-asset.md](2026-07-03_reach-arm-swing-with-pose-asset.md)（`_armReach` を腕スイング角へ変換する仕組みの前提）

コミット: `4458b42` fix(player): Reach腕リーチの累積オフセットをreach開始時にリセット

## 症状

- Reach中にLook Y入力（マウス上下）で腕を上下に振れる。
- Reachを離した後、再度Reachすると腕がまっすぐ正面に出ず、前回の傾きを引きずったまま始まる。
- 再現条件: Reach中にマウスを大きく上下に動かしてから離し、そのまま（マウスをY方向に戻さず）再度Reachする。

## 根本原因

`Assets/Code/Scripts/Network/InputCollector.cs` の `_armReach` は、**Reach中かどうかに関係なく毎フレーム常時累積され続ける「絶対値」**だった。

```csharp
// 修正前
float dy = look.y * lookSensitivityY * (invertY ? -1f : 1f);
_armReach = Mathf.Clamp(_armReach + dy, -armReachLimit, armReachLimit);
```

このコメント（修正前から存在）にあるとおり、これは体のベンド用累積値 `_bodyBend` と同じ仕組みを腕リーチにも流用したもの:

> 上下: APR と同じく body/arm を同一デルタで累積し、別々の clamp を適用

`_bodyBend`（体の前傾）は「常に現在のカメラ視点に体を追従させる」挙動として正しい。しかし `_armReach` は Reach 中の腕ピッチだけに使われる値であり、**Reachしていない間も同じデルタを吸い続けていた**。したがって：

1. Reach中に上下を見る → `_armReach` が動く → 腕が上下に振れる（意図通り）。
2. Reachを離す → `_armReach` はそのまま残る（リセット処理が存在しない）。
3. 離している間に視点を動かす／動かさない、どちらでも `_armReach` は「前回離した時点の値」のまま持ち越される。
4. 次にReachすると、`RagDollPhysics.ProcessReachingPhysics` が現在の `_armReach` からピッチを計算するため、正面（`armReach = 0` 相当）ではなく残留値から腕が始まる。

`RagDollPhysics.cs` 側の計算式自体（`upperArmBasePitch - armReach * upperArmPitchPerUnit`）は「現在の `_armReach` から絶対姿勢を毎tick計算し直す」設計であり、それ自体は正しい。問題は**入力側（`_armReach` という値）がそもそも汚染されていた**ことにある。

## 調査プロセス

1. 最初の仮説（誤り）: 「Reach終了時に `ConfigurableJoint.targetRotation` をリセットしていないから、残留回転が次のReachに影響している」と推測し、`RagDollPhysics.cs` の Reach終了検出ブロックに `ResetArmTargetToOriginal()` 呼び出しを追加。
2. ユーザーがPlayモードで検証 → **効果なし**。
3. `superpowers:systematic-debugging` の手順に従い振り出しに戻り、`ApplyReachPose()` の実装を1行ずつ再確認。
   - `_bodyJoints[upperArmIndex].targetRotation = _originalRotations[upperArmIndex] * ResolveReachDelta(...)` は **毎tick `_originalRotations`（固定値）を起点にした絶対代入**であり、前フレームの `targetRotation` を参照していない。
   - つまり次のReach開始tickで即座に上書きされるため、「targetRotationの残留」はそもそも症状の原因になり得なかった（前回の修正が効かなかった理由が判明）。
4. 「効かなかった」という事実は、原因が `targetRotation` より**上流の入力そのもの**にあることを示唆。`ProcessReachingPhysics(Vector2 lookDirection)` の呼び出し元を遡り、`RagdollCommand.LookDirection` → `RagDollInput.cs` → `NetworkInputData.bodyDir` → `InputCollector.cs` の `data.bodyDir = new Vector2(_bodyBend, _armReach)` に到達。
5. `_armReach` の更新箇所（`InputCollector.Update()`）を確認し、Reach状態を一切参照せず毎フレーム無条件に加算されていることを確認 → これが唯一の書き込み箇所であり、リセットも存在しない → 根本原因を特定。
6. 同ファイル内の `UpdateTwoHandedBodyYaw`（両手持ち突入フレームで `_bodyYaw` を現在の向きから初期化し、スナップを防ぐ既存パターン）を参考に、同じ「立ち上がりエッジで基準をリセットする」設計を踏襲して修正。

## 修正の仕組み

`InputCollector.cs` に、LeftGrab/RightGrabの立ち上がり（押されていない→押された）を検出する `_reachActive` フラグを追加。

```csharp
bool reachActive = _inputActions.Player.LeftGrab.IsPressed() || _inputActions.Player.RightGrab.IsPressed();
if (reachActive && !_reachActive)
    _armReach = 0f;
_reachActive = reachActive;

_armReach = Mathf.Clamp(_armReach + dy, -armReachLimit, armReachLimit);
```

Reach開始フレームでのみ `_armReach` を0にリセットしてから、当該フレーム分のdyを積む。これにより：

- Reach中でない間に `_armReach` がどう変化していても、次のReach開始時には必ず0（＝正面）から始まる。
- Reach開始と同じフレームでマウスを動かしていた場合、そのフレーム分の入力はリセット後に加算されるため取りこぼさない。
- `_bodyBend`（体のベンド）は変更していないため、体の前傾は従来どおり常時カメラ追従のまま。

「Reach終了時」ではなく「Reach開始時」にリセットする設計にしたのは、終了時にリセットしても、次の開始までの間に発生する視点移動（体のベンドのために動かした分も含む）でまた `_armReach` が動いてしまい、根本解決にならないため。開始時にリセットすれば、間に何が起きていても毎回必ず正面から始まることが保証できる。

`RagDollPhysics.cs` の `targetRotation` リセット（前回の修正）は上記の理由で無害だが根本原因ではなかった。実害がないため revert はせず、Reach終了直後の見た目上のスナップとして残している。

## 不採用にした代替案

- **案: Reach終了時に `_armReach` をリセットする**（開始時ではなく） → 上述のとおり、終了〜次回開始の間の視点移動を無視できないため却下。
- **案: `_armReach` を「Reach中のみ」加算対象にする**（Reach外では加算しない）→ 開始時0リセットと組み合わせないと「Reach中に離して即座に入れ直す」高速な操作で前回値が残る余地があり、より確実な「開始時ゼロ化」を採用。
- **案: `RagDollPhysics.cs` 側だけの修正で押し通す** → `ApplyReachPose` が毎tick絶対計算する設計である以上、入力側の汚染を直さない限り再発するため却下（既に実測で無効と確認済み）。

## 自力再実装チェックリスト

- [ ] `_armReach` と `_bodyBend` が同じ `dy` から生成される「常時累積の絶対値」であり、Fusionのresim対策（生デルタでなく絶対値スナップショットを送る）のために採用されている設計だと説明できる。
- [ ] `ApplyReachPose` が「前フレームからの相対更新」ではなく「`_originalRotations` を起点にした毎tick絶対代入」であることを説明できる。なぜこれが「targetRotationのリセット漏れ」を無効な仮説にするか。
- [ ] 症状から見て「妥当そうな」修正（targetRotationリセット）が実際には効かなかった理由を、コードを見ずに口頭で説明できる。
- [ ] なぜ「Reach終了時リセット」ではなく「Reach開始時リセット」が正しい設計かを、具体的な反例（終了後に視点を動かすケース）で説明できる。
- [ ] 同一ファイル内の `UpdateTwoHandedBodyYaw` の「突入フレームで基準を初期化する」パターンと、今回の修正がどう同型かを指摘できる。
- [ ] `_bodyBend`側には同種の問題が起きない理由（体の前傾は常時カメラ追従が仕様として正しいため）を説明できる。
