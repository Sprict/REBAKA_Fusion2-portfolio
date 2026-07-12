# 両手持ち時のボディヨー操作とカメラ背後自動追従（2026-07-03）

ブランチ: `feature/two-handed-body-yaw-camera`

## 問題 / 要求

- 通常時: マウスX = カメラの水平旋回（OrbitCamera）。体のヨーは移動方向へ自動で向く。
- 仕様変更: **両手で同一のオブジェクトを掴んでいる間だけ**（片手ずつ別のものを持つ場合は除外）、
  マウスX をプレイヤーボディのヨー回転（Limit なし）に切り替える。
- その間カメラは操作できなくなるため、プレイヤーの背後へイーズイン/アウトで自動的に回り込む。
- ただしラグドールの体の傾き・回転にカメラが機敏に反応すると画面酔いするので、
  ロケットリーグのカメラ設定 Stiffness のような「カメラの時間差」を Inspector で調整可能にする。

## なぜこのアプローチか

体のヨーは既に `NetworkInputData.facingDirection` → ホストの `UpdateRootRotation()`
（ルートジョイントの targetRotation を turnSpeed で Slerp）で駆動されている。
そのため新しい回転経路を作らず、**入力側（InputCollector）で facingDirection の
生成規則を切り替えるだけ**で実現できる。resim 安全性も既存方式（描画レートで
絶対値を累積し OnInput でスナップショット送信）をそのまま踏襲する。

不採用案:
- **ホスト側でマウスXデルタを受けて回す**: マウスデルタは非決定的で resim と相性が悪い
  （既存の bodyBend/bodyRoll と同じ理由で絶対値累積方式が正）。
- **カメラのヨーをそのまま体に同期（カメラ基準操作へ反転）**: カメラと体が剛結合になり、
  ラグドールの揺れ→カメラ揺れの経路ができて酔い対策と矛盾する。
- **NetworkInputData に yaw フィールド追加**: facingDirection（Vector3）で表現できるため
  入力構造体の拡張は不要。帯域も増やさない。

## 仕組み

### 両手持ち判定（全ピア共通で読める）
- `RagdollHandContact.GrabbedNetworkId`（新設 public、実体は [Networked] GrabbedObjectId）。
- 各手は `Spawned()` で `RagdollController.RegisterHandContact()` に自己登録
  （APR_Root は NO ルートから detach されるため階層検索に頼らない登録制）。
- `RagdollController.IsTwoHandedHold` = 左右とも掴んでいて GrabbedNetworkId が一致
  （default 除外）。`ILocalPlayerViewSource` にも公開。
- 注: 同一プレイヤーの別パーツを両手で掴んだ場合も NO は同一なので true（仕様通り「同じもの」扱い）。

### 入力（InputCollector）
- `LocalPlayerCameraBinder.LocalView`（新設 static）経由でローカルプレイヤーの
  `IsTwoHandedHold` / `FacingForward` を参照。
- 両手持ち中: `_bodyYaw` にマウスX×`bodyYawSensitivity` を **clamp なし**で累積し、
  `OnInput` で `facingDirection = Euler(0,_bodyYaw,0)*forward` を送る（移動方向より優先）。
- 突入フレームで現在の体の向き（FacingForward）から `_bodyYaw` を初期化 → スナップしない。
- Alt 押下中はロール操作なのでヨーに累積しない（既存の bodyRoll と排他）。

### カメラ（OrbitCamera）
- `SetTarget` で受けていた `ILocalPlayerViewSource` を保持するよう変更（従来は未使用）。
- 両手持ち中: マウスX旋回と followMovementDirection を停止し、
  `Mathf.SmoothDampAngle(_orbitYaw → 体のヨー, smoothTime=twoHandedFollowLag)` で
  背後へイーズイン/アウト追従。
- **`twoHandedFollowLag`（秒）が「カメラの時間差」**: Rocket League の Stiffness の逆相当。
  大きいほど体の回転に遅れてゆっくり追従（酔いにくい）、0 に近いほど機敏。既定 0.35s。
- 追従目標は物理ボディの実回転ではなく [Networked] FacingDirection（＝入力エコー）。
  ラグドールの揺れが直接カメラに乗らないため、これ自体も酔い対策になっている。
- 解除時は `_orbitYaw` がその場の値から通常マウス操作に戻る（連続的、スナップなし）。

## 酔い対策の改修（同日追記）

初版（常時 SmoothDampAngle 追従）は実プレイで強い画面酔いを起こした。原因は
**カメラが常に微小回転し続けて世界が流れる（vection）**こと。「回転を遅くする」ではなく
「**カメラが静止している時間を最大化する**」方針へ再設計した:

- **デッドゾーン**（`twoHandedDeadZoneDegrees`=25°）: ヨー差がこの範囲内ならカメラを1°も回さない。
- **縁を目標に追従**: デッドゾーン外では中心ではなく「縁」を SmoothDamp の目標にする。
  操作をやめた瞬間に誤差が縁の内側へ入り、カメラが即座に完全静止する（中心追いはドリフトの尾を引く）。
- **角速度上限**（`twoHandedMaxYawSpeed`=110°/s）＋**ソフトゾーン**（70°、超過は縁へクランプ）:
  Rocket League の stiffness / swivel speed 分離に相当。
- **静穏時リセンター**（`twoHandedRecenterDelay`=1.5s → `twoHandedRecenterSmoothTime`=1.2s）:
  マウスXが止まってから遅れて真後ろへ寄せる。射撃系アイテムでの正面合わせを想定して採用。
- 不採用: 構図用ピボット分離（`_framingPivot`）。酔いの主因は回転であり、位置追従は既に
  SmoothDamp 済みで寄与が小さい。差分の大きさに見合わないため第2弾送り。

## 追加修正（同日・プレイ検証フィードバック2件）

1. **歩行ボビングによるカメラ縦揺れ（手振れ）**: 注視ピボットの SmoothDamp が上下動を
   そのまま通していた。水平（`pivotSmoothTime`=0.18s）と垂直（`pivotVerticalSmoothTime`=0.8s）で
   ローパス時定数を分離。垂直の強いローパスが歩行ボビングを実質完全に吸収しつつ、
   段差・ジャンプの高さ変化にはゆっくり追従する。値を上げれば「手振れ完全オフ」相当。
2. **頭上の掴み物へカメラがめり込む**: スプリングアームの SphereCast がプレイヤー自身しか
   除外しておらず、頭上に掲げた荷物を遮蔽物と誤認してカメラを引き寄せていた
   （物の内部から Cast が始まるとヒットせずメッシュ内に留まる）。
   `ILocalPlayerViewSource.GetHeldObjectRoot(isLeftHand)`（GrabbedNetworkId→Runner.FindObject）を
   新設し、左右の手の掴み対象を衝突判定から除外。
   制約: 他プレイヤーを掴んだ場合、相手の detach 済みボディパーツは NO ルートの子ではないため
   除外が効かない（レアケースとして許容、必要なら RagdollBodyOwnerRegistry で拡張）。

## 既知の制約

- 両手持ち状態・FacingDirection は [Networked] のため、クライアントでは検出/追従に
  RTT ぶんの遅延がある（カメラは元々遅延追従なので体感影響は小さい想定）。
- Play モードでの実機検証は未実施（Unity Editor での両手持ち→ヨー回転→カメラ追従の確認が必要）。

## 自力再実装チェックリスト

- [ ] 体のヨーが「入力の facingDirection → ジョイント targetRotation の Slerp」で駆動されている経路を説明できる
- [ ] マウスデルタを OnInput で直接送ってはいけない理由（resim 非決定性）を説明できる
- [ ] 両手持ち判定に [Networked] プロパティを使うことで全ピアで一貫する理由を説明できる
- [ ] SmoothDampAngle の smoothTime が「カメラの時間差」としてイーズイン/アウトを生む仕組みを説明できる
- [ ] detach 済みボディパーツで GetComponentInParent が使えない問題と登録制での回避を説明できる
