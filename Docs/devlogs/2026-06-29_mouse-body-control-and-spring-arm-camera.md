# 2026-06-29 操作スキーム反転: マウス→Body直接操作 + 自動追従カメラ(スプリングアーム)

## 問題 / 背景

従来の操作は **マウス → OrbitCamera(マウスオービット) → 体がカメラ前方へ受動追従(facingDirection)** という連鎖だった。
APR サンプル（`Assets/Downloads/APR Player/APR/Scripts/APRController.cs`）と同じ操作感へ寄せたい:

- **マウスX = 体を左右に回す（ヨー）**
- **マウスY = 体が上下を向く（胴体ベンド + リーチ時の腕ピッチ）**
- **カメラはマウスで操作しない**。プレイヤーの向いている方向の背後へ **プログラムで自動追従**（三人称・固定角・体だけ上下）。
- カメラが **壁/地面に埋まらない**（UE5 のスプリングアーム相当）。
- Reach / Punch も APR と同様の挙動。

つまり **入力→カメラ→体** の連鎖を **入力→体→カメラ** へ反転させる。

加えて、直近で掴み(Reach)の腕角度バグ（右腕が前方20度しか出ない、左腕が後方へ出る）も
本対応の一部として修正済み（前方90度 = パンチrelease上腕Xの `8f` に合わせた）。

## なぜこのアプローチか（採用理由と不採用案）

### 1. 視点入力を「絶対値」で送る（最重要・決定論）
Fusion は resimulation で同じ tick の入力を複数回再生する。**マウスデルタ（相対値）をそのまま
NetworkInputData に載せると resim のたびに二重加算され非決定的になる**（`InputCollector.cs` の
従来コメントが既に警告していた）。

- **採用**: クライアント側(描画レート `Update`)で **絶対ヨー/ベンド/リーチ量に累積**し、
  `OnInput` ではその絶対値スナップショットを送る。resim で何度再生されても同じ絶対値→一致する。
- **不採用**: デルタを送ってホストで積算 → resim 二重加算で破綻。
- **不採用**: `OnInput` 内でデルタ累積 → `OnInput` は tick あたり 0〜複数回呼ばれ得るため不安定。

ヨーは既存の `facingDirection`（絶対の水平方向ベクトル）に載せ、`UpdateRootRotation` が
そのまま体を回す既存経路を再利用。上下は `bodyDir`(Vector2) に
`x=胴体ベンド(±0.9)`, `y=腕リーチ(±1.2)` を載せる。

### 2. APR の可動範囲をそのまま採用（傾き幅を一致）
APR `PlayerReach()` 実測: 胴体ベンド `MouseYAxisBody ∈ [-0.9, 0.9]`、腕リーチ `MouseYAxisArms ∈ [-1.2, 1.2]`。
この clamp とクォータニオン規約を採用して傾き範囲を一致させた。

- **リグ座標の注意**: APR の絶対ポーズ・クォータニオン定数は APR 旧リグ座標系の値で、本リグとは
  軸対応が異なる（punch が APR `(0.74,0.04,0)` に対し本リグは `Euler(8,…)` で前方=検証済み）。
  よって **clamp 範囲・累積モデル・感度は APR と一致**させつつ、前方リーチの絶対向きは
  **本リグで検証済みの規約（Euler X 前方）** で表現。胴体ベンドは同じクォータニオン規約
  `new Quaternion(bodyBend,0,0,1)` を `_originalRotations[IndexBody]` に合成して範囲を一致させた。

### 3. カメラはスプリングアーム（UE5風）で衝突回避
- **採用**: `pivot → desired(背後+高さ)` を **SphereCast** し、プレイヤー自身以外に当たれば
  衝突点の手前(`collisionSkin`分)までカメラを引き寄せる。プレイヤー自身は `target.root` 配下を
  `IsChildOf` で除外（レイヤー非依存で自己衝突を回避）。
- **不採用**: 単純 `Raycast`（点）→ カメラ近接コライダーの角ですり抜ける。球の方が安定。
- カメラ自身はピッチせず、`height/distance` 比で決まる固定の見下ろし角。体だけが上下を向く
  （ユーザー要望: 酔いにくさ優先）。

### 4. FPS式 X/Y 別感度
`lookSensitivityX`(ヨー) と `lookSensitivityY`(上下) を分離して SerializeField 公開。invertY も用意。

## 仕組み（データフロー）

```
[マウス] InputCollector.Update(描画レート)
   _yaw      += look.x * lookSensitivityX        (絶対ヨー, 360ラップ)
   _bodyBend  = Clamp(_bodyBend + dy, ±0.9)      (APR MouseYAxisBody)
   _armReach  = Clamp(_armReach + dy, ±1.2)      (APR MouseYAxisArms)
        │
   OnInput(tick): facingDirection = Euler(0,_yaw,0)*forward
                  direction       = ヨー基準の WASD（Camera非依存）
                  bodyDir         = (_bodyBend, _armReach)
        ▼ NetworkInputData（絶対値のみ → resim安全）
   RagdollInput.ProcessInput → RagdollCommand{ FacingDirection, LookDirection(x=bend,y=reach) }
        ▼ Host FixedUpdateNetwork
   RagDollPhysics.UpdatePhysics:
     UpdateRootRotation(FacingDirection)  → 体ヨー（既存）
     UpdateBodyLook(LookDirection.x)      → 胴体ベンド常時（新規, IndexBody）
     ProcessReachingPhysics(LookDirection): armReach(y)で腕ピッチ、_reachStiffness(2000)へ昇圧
        ▼ ポーズ同期（SnapshotInterpolation 等）
   各クライアントで体の向き/ベンドが再現

[カメラ] OrbitCamera.LateUpdate(ローカルのみ):
   facing = ILocalPlayerViewSource.FacingForward (= networked FacingDirection)
   desired = pivot - facing*distance + up*height
   desired = SphereCast で壁/地面手前に補正（自己除外）
   SmoothDamp で追従、LookAt(pivot)
```

## 変更ファイル
- `Assets/Code/Scripts/Network/InputCollector.cs`: マウス累積(絶対)・X/Y別感度・カーソル管理(セッション稼働中のみLock, Escトグル)。F5トグル/grab強制カメラ追従/Camera.main依存を削除。
- `Assets/Code/Scripts/Player/RagDollInput.cs`: `LookDirection` clamp を x=±0.9 / y=±1.2 に。
- `Assets/Code/Scripts/Player/RagDollPhysics.cs`: `UpdateBodyLook` 追加(胴体ベンド常時)、`ProcessReachingPhysics` を armReach 駆動へ、reach 用 `_reachStiffness` 昇圧/終了復元、reach 腕角を `_context` 公開値に。
- `Assets/Code/Scripts/Player/RagdollProfile.cs` / `RagdollControllerContracts.cs` / `RagDollController.cs`: reach 腕パラメータ(base/perUnit/lower)を Inspector 公開(物理トライアド)。
- `Assets/Code/Scripts/Camera/OrbitCamera.cs`: 三人称自動追従+スプリングアーム衝突へ全面置換(クラス名/GUIDは据え置きでシーン参照維持)。
- `Assets/Code/Scripts/Player/RagdollControllerContracts.cs`(ILocalPlayerViewSource) / `RagDollController.cs` / `LocalPlayerCameraBinder.cs`: カメラへ体の facing を供給(`FacingForward`)。

## 既知の注意 / 未検証
- **未検証**: Host/Client 30秒の手動プレイ確認（決定論ジッタ・カメラ埋まり・操作感）。コンパイルは 0 error/0 warning 通過済み。
- `MyCameraController`（複数人グループフレーミング）が同シーンに同居。ローカル追従は `OrbitCamera` が担うが、同一カメラ上で両方有効だと競合しうる → プレイ時に確認。
- 感度初期値(`lookSensitivityX=0.15`, `Y=0.004`)は推測値。New Input System の Look デルタ規模に依存するため Inspector で要調整。
- legacy 入力 `AprInputBehaviour` / `GameLauncher` は Test_Playground 未使用（参照0件で確認済み）。今回は触っていない。

## 追記（同日・操作感チューニング）
- **マウスY反転を解消**: 既定でマウス上=体が上を向く（`invertY` で反転可）。
- **カメラ追従を弱化**: 向き追従を `facingFollowSpeed`(既定0.5) に分離。位置追従(`followSmoothTime`)は維持しつつ、体を回してもカメラはほぼ動かずゆっくり背後へドリフト（GBFリリンク風）。0で向き追従なし。
- **進行方向への向き直しを追加**: 移動はカメラ基準（カメラがほぼ固定なので実質ワールド基準）に変更。移動入力がある間は `facingDirection = 進行方向` とし、`UpdateRootRotation` の `turnSpeed` スラープでスムーズに回頭。静止中はマウスXでその場回転（エイム/武器振り）。静止復帰時のスナップ防止に、移動中は累積ヨー `_yaw` を現在の進行方向へ同期する。
  - トレードオフ: 移動中はマウスXの体ヨーより進行方向が優先される（その場では従来どおりマウスXで回せる）。常時マウスX優先にしたい場合は要追加調整。

## 追記2（同日・カメラをHuman Fall Flat風オービットへ）
ユーザー要望でカメラ操作を再設計:
- **酔い対策(最重要)**: ラグドールの揺れがカメラに伝播していた。注視ピボットを `SmoothDamp(pivotSmoothTime≈0.18)` で強くローパスし、揺れを吸収。回転も安定ピボットを `LookAt` する。
- **マウスX = カメラ水平旋回**: 体ヨーをマウスXから切り離し、カメラがプレイヤー周りを水平に旋回（HFF操作感）。旋回は `OrbitCamera` 内でローカル完結（`Mouse.current.delta.x`、カーソルロック中のみ）。ネットワーク非同期。
- **マウスY = 体の上下のまま**（リーチ/グラブ狙い）。カメラ垂直は固定角（`height/distance`）。
- 体ヨーは「移動中=進行方向／静止中=現在向き維持(zero送出)」。マウスXは体に使わない。
- カメラのヨーがマウス制御になったことで、移動のカメラ基準計算がそのまま HFF の移動基準になる（旋回→その向きへ歩く→体が進行方向を向く）。

> 設計の変遷: 「マウスで体を直接操作」→「やっぱりマウスXでカメラ旋回(HFF)」。最終的に **マウスX=カメラ旋回 / マウスY=体の上下 / 移動=カメラ基準で進行方向へ向き直る** に落ち着いた。`InputCollector.lookSensitivityX` は未使用だが `[SerializeField]` 削除禁止のため残置。

## 自力再実装チェックリスト
1. NetworkInputData は **絶対姿勢**（ヨー/ベンド/リーチ）を持たせる。デルタは送らない（resim 二重加算）。
2. クライアントの描画レート(`Update`)でマウスデルタを絶対値へ累積し、`OnInput` で snapshot 送出。
3. ヨーは `facingDirection` ベクトルに載せ、既存の root 回転経路へ。上下は別フィールドで body/arm を別 clamp。
4. 胴体ベンドはリグの baseline(`_originalRotations`)に APR と同じクォータニオン規約を合成し、範囲を ±0.9 に一致。
5. 腕の前方向きは **自リグで検証した規約**（このリグでは Euler X=8f が前方90度）で表現する。APR の生クォータニオンを丸写ししない。
6. カメラは SphereCast スプリングアーム。自己衝突は `target.root` の `IsChildOf` 除外で回避（レイヤー非依存）。
7. カーソルロックは **セッション稼働中のみ**（起動 OnGUI メニューをクリックできる必要がある）。Esc で解除トグル。
