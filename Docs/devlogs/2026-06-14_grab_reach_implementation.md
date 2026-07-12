# 2026-06-14 つかみ(Reach)実装 + UpdatePhysics の command 集約リファクタ

> **状態: コンパイル通過済み（エラー0・新規警告なし）／Play検証・数値較正は未実施**。
> リファクタ＋実装＋呼び出し側更新は完了。残るは実機での見た目較正とレビュー（「残作業」参照）。
> ⚠️ `ProcessReachingPhysics` の角度定数（基準-75f等）は**未検証の暫定値**。符号が逆の可能性あり。

## 問題

マウス左右クリックでプレイヤーがオブジェクトを掴まない。

調査の結果、入力経路（マウス→入力収集→状態遷移）はすべて正しく繋がっていたが、
腕を前に伸ばす物理処理 `RagDollPhysics.ProcessReachingPhysics(Vector2)` が
**空実装（TODO コメントのみ）** だった。

掴みの成立条件は「手のコライダーが対象に物理衝突した瞬間（`RagdollHandContact.OnCollisionEnter`）に
対応するマウスボタンが押されている」こと（APR元実装 `HandContact.cs` と同方式）。
腕が伸びないため手が対象に接触せず、`OnCollisionEnter` の掴み判定が走らない、という連鎖だった。

補強材料: リーチ用ジョイント剛性 `_reachStiffness`（宣言・代入のみ）もどこからも使われておらず、
リーチ機能はまるごと作りかけだった。

## 仕様（ユーザー合意済み・Human Fall Flat 準拠）

- 左マウス押下中 = 左腕を前に出す / 右マウス押下中 = 右腕を前に出す
- マウス（視点）を動かすと腕が上下に動く（**上下のみ**腕ピッチ、**左右は体ごとカメラ yaw へ追従**）
- 腕を伸ばす「前方」の基準座標系はカメラ正面
- つかみ操作中だけカメラ追従を強制（F5 の移動方向追従トグルを無視）
- 腕の可動域は人体相当にクランプ

## なぜこの設計か（採用案 = 案B）

### 入力の渡し方: 絶対カメラ姿勢を入力に載せる（マウスデルタ積分は不可）
Fusion の resimulation では入力が決定論的にリプレイされるため、腕の向きは
`FixedUpdateNetwork` で積分する「状態」ではなく、毎tickサンプリングされる「入力値」でなければならない。
→ マウスデルタ累積は却下。**カメラの絶対姿勢（forward.y）を入力に載せる**。

### 構造体は広げない: デッドパイプ `bodyDir` を再利用
`bodyDir → LookDirection → ProcessReachingPhysics` は唯一の消費先が空メソッドのデッドパイプだった。
`LookDirection` は既に `[Networked]`（`RagDollController.cs:112`）なのでクライアントへ自動同期される。
→ `NetworkInputData` の構造体拡張・新規 `[Networked]` は不要。`bodyDir` の**意味だけ**
「マウスデルタ」→「カメラ上下成分(forward.y, -1..1)」に変更。

### UpdatePhysics を RagdollCommand 渡しにリファクタ（案B）
`UpdatePhysics(state, moveDir, facingDir, lookDir, punchR, punchL, dt)` という7引数は、
既に存在する集約型 `RagdollCommand`（移動・向き・視線・掴み左右・パンチ左右を全部持つ）を
わざわざバラして渡していた。新アクション追加のたびに「定義＋全呼び出し」を直す
Long Parameter List / Shotgun Surgery アンチパターン。

**呼び出し側3箇所（Host, ClientProxy×2）は既に `command` を構築済み**だったため、
`UpdatePhysics(state, command, dt)` に変えると触る箇所は最小変更案と同数（定義2＋呼び出し3）なのに
呼び出しは7→3引数に縮み、以後のアクション追加コストがゼロになる。
コンパイラがリネーム漏れを検出。テストはシグネチャ非依存、`PhysicsHandler` は具象型でIF変更不要。

#### 不採用にした案
- **案A（引数2つ追加: isGrabbingR/L）**: 最小だが Long Parameter List を悪化させ、次のアクションでまた同じ作業。
- **案C（PlayerState×アクションを State/Strategy で全面再設計）**: 理想的だが物理同期が安定した直後で退行リスク大、今回の目的に対し過剰。→ **将来TODO**（`TECHNICAL_DESIGN.md` に1行記録予定）。

### command は値渡し（`in` 不可）
`RagdollCommand` は mutable struct。`in`（readonly ref）はメンバアクセスごとに隠れた防御コピーを生む C# のフットガン。値渡しが正解（struct は小さい）。

## ここまでに変更したファイル

### 入力層（編集済み）
- `Network/NetworkInputData.cs`: `bodyDir` のコメントを「カメラ上下成分」に変更
- `Network/InputCollector.cs`:
  - カメラ生 forward.y を水平化前に保存 → `data.bodyDir = new Vector2(cameraPitchComponent, 0f)`
  - グラブ入力判定を前倒しし、つかみ中は `facingDirection` をカメラ前方へ強制（F5無視）
- `Player/RagDollInput.cs`: `LookDirection` 生成コメントを新仕様に更新（`Clamp(-1,1)` は範囲保険として維持）

### 物理層（編集済み）
- `Player/RagDollPhysics.cs`:
  - フィールド `_wantsReachRight / _wantsReachLeft` 追加
  - `UpdatePhysics` を `(PlayerState state, RagdollCommand command, float deltaTime)` に変更。
    本体の `moveDirection/facingDirection/lookDirection/wantsPunch*` を `command.X` に置換。
    冒頭で `_wantsReach* = command.IsGrabbing*` を保存。
  - `UpdatePhysicsVisualOnly` も同シグネチャに変更（同様に置換・保存）
  - `ProcessReachingPhysics(Vector2 lookDirection)` を実装（下記「仕組み」）
  - `ApplyReachPose(bool isRight, float upperArmPitch, float lowerArmPitch)` 新規ヘルパー

## 仕組み（ProcessReachingPhysics）

1. `lookDirection.x` = カメラ forward.y（上向き正/下向き負, -1..1）。
2. 上腕ピッチ = `Clamp(基準角 - cameraPitch * 振れ幅, 下限, 上限)`。
   - パンチ windup（`ApplyPunchWindup`）が `targetRotation = _originalRotations[i] * Euler(X,…)` の
     X 軸で腕を前/上に動かすことを実証済み。これを土台に X をカメラピッチで駆動。
   - [※推測] このrigでは Euler の X 成分が腕の前後/上下ピッチ軸。符号・係数・可動域は **Play で要較正**。
3. `_wantsReachRight / _wantsReachLeft` を見て、押下側の腕だけ `ApplyReachPose` で伸ばす。
4. 前腕はほぼ直線に伸展（`lowerArmPitch = -10f` 暫定）。
5. drive は `ApplyBlendedJointDrives` が毎tick設定する `_poseOn` を流用（パンチと同方式）。
   伸びが弱ければ `_reachStiffness` 適用を検討。
6. **解放した腕の復帰は既存 `UpdatePunchRecovery` が兼ねる**。
   同メソッドは「`!_punchingRight && delay<=0` の腕を `LerpArmToOriginal` で original へ戻す」汎用復帰として
   機能しており、reach をやめた腕（punch していない）も自動で戻る。→ reach 専用の復帰処理は不要。

### 状態遷移と左右の扱い
- `RagdollStateEvaluator.Resolve`: jump → ragdoll → **punch → grab(Reaching)** → walk → idle の優先順。
  punch が grab より優先（同時押し時は Punching）。
- クライアントの `UpdatePhysicsVisualOnly` は reach/punch ポーズを再現せず歩行のみ。
  腕ポーズはホストの物理結果が NetworkRigidbody3D のスナップショット同期で伝わる設計（パンチと同じ）。
- 既知の制限（仕様・バグではない）: `PlayerState` は左右情報を持たないため、クライアント側の
  ローカル予測では左右を区別しない。実際の掴みジョイントは `RagdollHandContact` が
  ホスト権威で手ごとに正確処理する。これは既存のパンチと同じトレードオフ。

## 残作業（再開時はここから）

1. ~~呼び出し側3箇所を `command` 渡しに更新~~ **完了**:
   - `RagdollHostSimulationOrchestrator.cs:69`（L64-66 の `_context.X` 書き込みと `ResolvePlayerState` は残した）
   - `RagdollClientProxyRuntime.cs:143`（Forecast経路）、`:296`（VisualOnly）
2. ~~`uloop-compile` でコンパイル確認~~ **完了（エラー0・新規警告なし）**
3. `Main_Backup` で Host 起動 → 左/右クリックで腕が前に伸びる → 対象に当てて掴む → 離して解放、を確認。30秒安定確認
   - **Play 較正**: 上腕ピッチの符号・基準角(-75f)・振れ幅(45f)・可動域(-120f〜-30f)・前腕(-10f)を実際の見た目で調整
   - 伸びが弱ければ `_reachStiffness` を腕 drive に適用
   - 腕が水平にカメラ yaw を追従しきれず遅れる場合（ルートの balance slerp 追従遅れ）は、yaw も腕に与えることを検討
4. 可能ならホスト/クライアント両方で確認（CLAUDE.md ネットワークルール）
5. `code-review` 完了後にコミット（それまで commit/push 禁止）
6. `TECHNICAL_DESIGN.md`: 関連TODO更新 ＋ 案C（State/Strategy per-action 再設計）を将来TODOとして1行記録

## 自力再実装チェックリスト

- [ ] 掴みは「接触の瞬間 + ボタン押下」で成立する接触ゲート方式だと理解しているか
- [ ] 腕の向きは入力（絶対カメラ姿勢）で渡す。マウスデルタ積分は resim で壊れる、と説明できるか
- [ ] 既存集約型があるなら境界でバラさず渡す（command 渡し）。なぜ7引数が悪いか言えるか
- [ ] mutable struct を `in` で渡すと防御コピーが出る理由を説明できるか
- [ ] ConfigurableJoint の `targetRotation = _originalRotations[i] * Euler(...)` の意味（rest からのローカル相対回転）を理解しているか
- [ ] reach の腕復帰がなぜ専用処理不要か（UpdatePunchRecovery が汎用復帰を兼ねる）を説明できるか
- [ ] クライアントが腕ポーズをどう得るか（VisualOnlyは再現せずスナップショット同期）を説明できるか
