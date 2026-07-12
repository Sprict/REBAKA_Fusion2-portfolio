# Reach中の腕上下入力が ReachPose アセット使用時に無視されるバグ修正（2026-07-03）

ブランチ: `feature/two-handed-body-yaw-camera`

## 問題

崖よじ登り（HFF風マントル）の改善で Profile の `reachArmInputLimit` /
`reachUpperArmMaxPitch` を上げても腕が全く動かなかった。ユーザーの観察どおり、
Reach 中のマウスY（腕上下）は胴体ベンドにしか効かず、腕は固定 targetRotation だった。

## 原因

`RagDollPhysics.ResolveReachDelta` は Profile に `ReachPose`（ActionPoseAsset）が
割り当てられていると、アセットの静的デルタをそのまま返していた。マウスYから計算した
`upperArmPitch` は「アセット未割当時のフォールバック」にしか使われておらず、
データ駆動ポーズの導入時に腕上下入力の経路が実質的に切断されていた。

前段の修正（RagdollInput の範囲保険クランプを Profile 実効値化、`3cd01f7`）は
このさらに上流であり単体では効果が出なかった（修正自体は本件の前提として有効）。

## 修正（案A: 基準ポーズ＋スイング合成）

- アセットのデルタ＝基準姿勢（base 角の役割）とし、マウスY/右スティックY 由来の
  上下スイング角を乗算合成: `Quaternion.Euler(eulerDelta) * Quaternion.Euler(swing*side,0,0)`
- スイング角は base を含まない振り幅のみ: `-armReach × reachUpperArmPitchPerUnit` を
  `[min-base, max-base]` でクランプ（パラメトリック経路と同じ実効可動域）。
- 下腕（肘）はアセット固定のまま（スイングは上腕のみ、第一歩として安全側）。

不採用案B: ReachPose を外してパラメトリック経路へ戻す — ポーズオーサリング成果と
リグ軸差異対応を捨てるため不採用（切り分けテストとしてのみ有用）。

## 追記: 固定軸スイングは2回失敗 → 「アセットデルタ自身の回転軸」方式へ（同日）

固定軸でのスイング合成は実プレイで2回とも破綻した:

1. `assetDelta × swing`（アセット後の座標系でX回転）→ 腕の**開閉**に化ける
2. `swing × assetDelta`（rest座標系でX回転）→ 押すと**腕が下がり**、引くと**両腕が胸の前でクロス**

原因: このリグの上腕ジョイントのローカルX軸は「腕の上下」に対応していない。
リグの joint 軸の向きは実機でしか分からず、固定軸の推測はどの座標系でも当たらない。

解決: **アセットデルタ自身の回転軸を使う**。ReachPose のデルタは
「rest（腕を垂らす）→ リーチポーズ（腕を前方へ上げる）」への回転なので、
その回転軸（`ToAngleAxis` で抽出）こそがこのリグで「腕を上げ下げする軸」そのもの。
`AngleAxis(swingDegrees, assetAxis) × assetDelta` で、正=ポーズ延長方向へさらに上げる／
負=restへ戻す方向へ下げる、がリグの軸の向きに依存せず保証される。同軸なので合成順も可換。
左右のミラーもアセットの各側デルタが軸ごと持っているため side 反転が不要になった。

副次変更: スイング角は `armReach × reachUpperArmPitchPerUnit`（armReach は
±reachArmInputLimit でクランプ済み）とし、Min/MaxPitch の base シフトクランプを廃止
（あれはパラメトリック絶対角用の概念で、軸相対スイングには意味を持たない）。

デルタがほぼ無回転（axis 不定）の場合はスイングを適用しないガード付き。

- `reachArmInputLimit`: 1.2 → 2.5 目安（腕の累積上限）
- `reachUpperArmMaxPitch`: 70 → 110〜130 目安（角度クランプ）
- 腕力不足なら Reach Arm Drive の spring/maxForce（掴み FixedJoint の breakForce=2000N
  ハードコードが先に折れる点に注意）

## 自力再実装チェックリスト

- [ ] データ駆動ポーズ（アセット）とパラメトリックポーズの2経路と、フォールバック構造を説明できる
- [ ] 「基準ポーズ×スイング乗算合成」がリグ軸差異への耐性を保つ理由を説明できる
- [ ] クランプ範囲を base 分シフトして2経路の実効可動域をそろえた理由を説明できる
