# 2026-06-24 PID関連デッドコードの整理

## 問題

棄却済みの「Hybrid（kinematic + PID補正）」プロキシ同期方式の遺物として、どこからも参照されないPID関連コードが残存していた。コードを読む人（および将来の自分）が「このPIDゲインは生きているのか？」と誤解する原因になり、保守上のノイズになっていた。

具体的に未使用だったもの:

1. **`RagDollController.cs` のPIDアクセサ8個**
   - `UprightPidKp` / `UprightPidKi` / `UprightPidKd` / `UprightPidIntegralMax`
   - `MovementPidKp` / `MovementPidKi` / `MovementPidKd` / `MovementPidIntegralMax`
   - いずれも `profile.xxx` を返すだけの `private` プロパティで、定義以外の参照がゼロだった。
2. **`RagdollProfile.cs` のPIDフィールド8個**（`uprightPid*` / `movementPid*`）
   - 上記アクセサ経由でしか参照されておらず、アクセサ削除で完全に孤立する状態だった。
3. **`PIDController.cs` 全体**（`PidController` / `RotationPidController` クラス）
   - プロジェクト内のどこからも `new` されず、`Update()` / `CalculateTorque()` も呼ばれていない未使用ファイル。

## なぜ削除（このアプローチ）か

- 現行のプロキシ同期は **SnapshotInterpolation（全パーツkinematic + `Render()` での純視覚補間）** に確定済み（`project-peer-sync-pure-interpolation`）。Hybrid方式は `RagdollSyncMode` のenumに参考用として名前が残るだけで、PIDゲインを読み出す実コードは存在しない。
- 「将来使うかもしれない」フィールドを残す案も検討したが、**読み出すコードが一切ない以上、値だけ残しても意味がない**（再導入時はその時点の要件で設計し直す方が安全）。Gitの履歴から復元も可能。
- 削除対象は確実に死んでいるものに限定。`WalkingBalancePriority` / `WalkingPoseStiffnessMultiplier` / 各種 `DamperRatio` / `ProxyInertia*` など近隣アクセサは `IRagdollPhysicsContext` 経由などで現役のため保持した。

### 代替案として見送ったもの

- **`[Obsolete]` 属性を付けて残す**: 参照ゼロなので警告すら出ず、ノイズが残るだけ。不採用。
- **アクセサだけ消してProfileフィールドは温存**: フィールドが「孤立した未使用public」になり、かえって誤解を招く。ユーザー確認のうえ両方削除。

## 仕組み（削除の安全性の根拠）

C#の `private` プロパティ・クラス・publicフィールドは、参照箇所をテキスト検索で網羅できる（リフレクション利用がない限り）。本プロジェクトはPIDゲインをリフレクションで読む箇所が無いため、以下を確認すれば安全に削除できる:

1. `grep` で `uprightPid|movementPid|UprightPid|MovementPid|PidController|RotationPidController|CalculateTorque` の参照が `Assets/Code` 内で **0件** であること。
2. 強制再コンパイル（Domain Reload込み）で **エラー0・警告0**。

`RagdollProfile` はScriptableObjectのため、削除したフィールドの `.asset` 内シリアライズ値は失われるが、読み出すコードが無いので挙動に影響はない。

## 自力で再実装するためのチェックリスト

デッドコードを安全に消すときの手順:

- [ ] 削除候補のシンボル名を列挙する（クラス名・フィールド名・プロパティ名）。
- [ ] 各シンボルを `Assets/Code` 全体で `grep` し、**定義以外の参照が無い**ことを確認する。
- [ ] 「アクセサ→Profileフィールド」のような連鎖は、上流を消すと下流が孤立する点に注意。連鎖の末端まで参照を追う。
- [ ] `SerializeField` / `public` シリアライズフィールドの削除は `.asset` / Inspector値の喪失を伴うため、消す前にユーザー確認（プロジェクト規約: SerializeFieldは無断削除しない）。
- [ ] ファイルごと消す場合は `.meta` も同時に削除する。
- [ ] 強制再コンパイル（`uloop compile --force-recompile true --wait-for-domain-reload true`）でエラー0を確認。
- [ ] リフレクション（`GetField` / `SendMessage` 等）で名前参照していないかも一応確認する。

## 検証結果

- `Assets/Code` 内の関連シンボル参照: 0件（grep確認済み）。
- 強制再コンパイル: `Success: true` / `ErrorCount: 0` / `WarningCount: 0`。
