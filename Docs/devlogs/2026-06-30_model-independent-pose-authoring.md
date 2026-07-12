# 2026-06-30 アクションポーズのモデル非依存化とオーサリングツール

## 問題

プレイヤーのアクション（Reach/Punch/Walk）のポーズが C# にハードコードされていた。

```csharp
// 旧 ApplyReachPose（デバッグ状態の例）
_bodyJoints[upperArmIndex].targetRotation =
    _originalRotations[upperArmIndex] * Quaternion.Euler(120f * side, 0f, 0f);
```

ポーズ値がコードに埋まっているため、調整のたびに再コンパイルが必要で、モデルを差し替えると
リグ固有の値が破綻する。やりたいこと:

1. アクションのポーズをモデル非依存にする。
2. Unity エディタでアクションごとにポーズを簡単に調整できるツール。
3. 重力下（実際のゲームプレイ物理）での見え方を見ながら調整できると最良。

## なぜこのアプローチか（不採用案も）

### 「モデル非依存」の定義 — 再オーサリング型を選択

- **採用: コード非依存（再オーサリング型）** … モデルを差し替えてもコードは不変。ポーズは
  ツールでモデルごとに録り直す。「非依存」を“コードを触らない”の意味に落とす。
- 不採用: 完全自動（リターゲット型） … 同じポーズデータで任意モデルが自動で正しく動く。
  IK/リターゲットソルバが必要で大規模、PID ラグドールと衝突しやすい。小規模ゲームの
  説明可能性に対して労力過大。

### ポーズ表現 — rest 相対デルタ（絶対 Quaternion 不採用）

APR サンプル（`Assets/Downloads/APR Player/APR/Scripts/APRController.cs`）は
`new Quaternion(0.58f, -0.88f, 0.8f, 1)` のような**絶対 targetRotation 直打ち**。これは APR 固有
リグの joint ローカル軸に合わせた手調整値で、別モデルでは破綻する。
→ 「安定姿勢(rest)からどれだけ曲げるか」という**相対デルタ**なら、rest が違うモデルでも
“同じ意図の曲げ”を再現しやすい。実行時は `joint.targetRotation = rest * Quaternion.Euler(delta)`。

### Reach は「静的決めポーズ＋コードの薄いオーバーレイ」に分解

APR の Reach を精読した結果:
- Body は `PlayerReach()` 冒頭で reach 判定の外、毎フレーム `targetRotation =
  new Quaternion(MouseYAxisBody,0,0,1)`（APRController.cs:668）。マウス Y で常時前傾。
- 腕の targetRotation 自体にも `MouseYAxisArms` 項（APRController.cs:776）。振れ幅 ±1.2 と大きく、
  腕も直接上下する。
→ つまり APR の Reach は「固定ポーズ＋Body 曲げ＋腕ピッチ変調」の3層合成。
ただし**我々は設計選択として**「腕＝静的決めポーズ（データ）／上下の照準＝Body ピッチ 1DOF
（コード）」に分解。責務分離（データ＝見た目／コード＝入力変調）が明確で、一文で説明可。

### PlayerBoneMap を棄却（重要な軌道修正）

当初は「論理骨ID→実 Joint」を持つ `PlayerBoneMap` MonoBehaviour を新設する計画だった。
しかし権威シムの `bodyJoints[]` は `RagdollController` の `[SerializeField]` 配列で、
Inspector 手動割当（スロット index == 論理ID）。**マッピングは既存配列が既に明示的に担っていた**。
`GetComponentsInChildren` 順依存だったのは `RagdollRigSetup` のプロキシ用ヘルパだけ。
→ `PlayerBoneMap` は二重のソース・オブ・トゥルースになり冗長なので削除。
`LogicalJoint` enum（スロット意味の明示的ドキュメント）＋既存 `bodyJoints[]`（マッピング）＋
`ActionPoseAsset`（ポーズ値）で「コード非依存」を達成。モデル差し替え時に触るのは
**Inspector の配列＋ポーズアセットだけ**（コード変更ゼロ）。

### 重力下プレビューの実現 — ツールがアセット編集／走行中コントローラが毎tick適用

`[※理論] PID＋重力では targetRotation そのままの姿勢にはならず定常的なたわみが出る`ため、
正確な見え方は物理を実走させるしかない。Edit-mode の `Physics.Simulate` 手回しは PID 実挙動と
ズレやすく実装も重いので不採用。採用したのは:
- ツール（EditorWindow）は `ActionPoseAsset` を `SerializedObject` 経由で編集（Undo/Dirty 自動）。
- プレビュー ON で `RagdollController.SetReachPosePreview()` を呼び、走行中の `RagdollPhysics` が
  入力/状態を無視して指定側 Reach ポーズを毎tick上書き適用（`ResolveReachDelta` がプレビュー
  アセットを優先）。
→ スライダを動かすと次の物理tickで実機（重力下）ポーズが更新。実機経路を100%再利用するので
プレビュー＝本番。

## 仕組み（実装ファイル）

- `Assets/Code/Scripts/Player/Posing/LogicalJoint.cs` … 骨の論理ID enum（0..14、従来 index と一致）。
- `Assets/Code/Scripts/Player/Posing/ActionPoseAsset.cs` … アクション1個分の rest 相対デルタ
  （`LogicalJoint → Vector3 eulerDelta`）。`TryGetDelta` で参照。
- `RagdollControllerContracts.cs` … `IRagdollPhysicsContext` に `ActionPoseAsset ReachPose`。
- `RagDollController.cs` … `[SerializeField] reachPose`、コンテキスト実装、
  `public SetReachPosePreview()`（プレビュー橋渡し）。
- `RagDollPhysics.cs` … `ApplyReachPose` をアセット駆動化（`ResolveReachDelta`）、
  プレビュー用フィールド＋ `SetReachPosePreview` ＋ `UpdatePhysics` 末尾でのプレビュー上書き。
- `Assets/Code/Editor/PoseAuthoringWindow.cs` … `Tools/REBAKA/Pose Authoring (Reach)`。
  asmdef に `MyProject.Scripts` 参照を追加。

適用フロー（権威側）:
`UpdatePhysics` → 状態 switch（Reaching で `ProcessReachingPhysics` → `ApplyReachPose`）→
末尾でプレビュー有効なら `ApplyReachPose` を強制上書き → `ResolveReachDelta` が
（プレビュー時はツール指定／通常時は `_context.ReachPose`）アセットの delta を返す → 無ければ fallback。

左右はミラー計算せず、アセットに両側（UpperRightArm / UpperLeftArm 等）を明示登録する方針
（モデル別に joint ローカル軸が違っても各側を実機で録れば正しく決まる）。

## 自力再実装チェックリスト

- [ ] 骨の論理ID enum を、既存の固定 index 定数と数値一致で定義したか。
- [ ] ポーズは絶対 Quaternion ではなく rest 相対デルタで保持したか（`rest * Euler(delta)`）。
- [ ] 既存の `[SerializeField] bodyJoints[]` が論理ID→Joint マップを兼ねている点を確認し、
      別マッピング層を二重に作っていないか。
- [ ] ポーズ適用はアセット優先・未登録なら fallback で、アセット未割当でも壊れないか。
- [ ] プレビューは「ツールがアセット編集／走行中シムが毎tick適用」で実機経路を再利用したか
      （Edit-mode シミュレーションを自前で組んでいないか）。
- [ ] プレビュー解除・Play モード退出時にドライブと targetRotation を通常へ戻したか。
- [ ] Editor asmdef にランタイム asmdef（`MyProject.Scripts`）参照を追加したか。

## 検証状況

- 縦切り（Reach のデータ駆動化）: **Play モードで合格**（アセットの値どおりに腕が動き、
  `eulerDelta` 変更が即反映）。
- オーサリングツール本体: 実装完了・**Play モード検証待ち**。

## 残課題

- クライアントプロキシ `UpdatePhysicsVisualOnly` に Reaching ケースが無い既知バグ（本作業の対象外）。
  権威側で確立後、同期経路へ展開する。
- ツールは現状 Reach 専用。Punch/Walk への一般化は別アクションのアセット化と合わせて後続。
- 照準（マウス Y）追従は Body ピッチ（`UpdateBodyLook`）が担当。腕への追従を足すかは
  プレイ感を見て判断。
