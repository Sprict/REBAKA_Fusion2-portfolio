# 2026-07-01 掴み腕の肘折れ修正：角度limit(壁) + maxForce(降伏) + 可動域ビジュアライザ

## 問題

プレイヤーが重い物を掴んだ状態で前進すると、肘が見た目として破綻する曲がり方をする。

- 期待する挙動: 物の重さに引っ張られて**腕全体が後方へ流れる**（肩から動く）。
- 実際の挙動: 肩は前を向いたまま、**肘だけが逆方向へ折れる**（関節の可動域を超えて見える）。

原因は2つの設定が重なっていたこと。

1. `ConfigurableJoint.angularXMotion` が `Free`（無制限）だった肘関節があり、関節の構造上ありえない角度まで回転できてしまっていた。
2. `JointDrive.maximumForce` が既定値 `3.4028233e+38`（実質無限大）のままで、目標姿勢(`targetRotation`)へ戻そうとする力に上限が無かった。これにより「重さに負けて自然に後ろへ流れる」のではなく、無限の力で押し合って暴れる動きになっていた。

## なぜこのアプローチか

設計をCodexとすり合わせ、以下の役割分担（v1）で合意した。

- **角度limit（prefab側、ハードな壁）**: 関節が物理的にありえない角度まで曲がることを防ぐ。「正常なポーズを作る」ためではなく「壊れを防ぐ」ためのもの。
- **`maximumForce`（profile側、ソフトな降伏）**: 重い物に引っ張られたとき、関節が**有限の力までしか目標姿勢を維持できない**ようにする。これが「重さに負けて腕が後方へ流れる」の正体。

**不採用案**: 自身のコリジョンに当たらない範囲を自動探索して可動域を決めるキャリブレーションツール。
理由は、出力された数値が「正しいかどうか」を見た目で検証する手段が結局必要になり、ツールの結果を盲目的に信用できないため。代わりに「現在の数値が腕にどう重なるか」を直感的に確認できる**可視化ツール**（後述）を選んだ。

**v2/v3へ先送りしたもの**（過剰設計の回避）:
- 物の重さに応じて連続的に `maxForce` を変える仕組み（現時点ではその必要性が出ていない）。
- `frontness`（体の向きに対する力の入り方）による筋力変調。
- 歩行中の動的な降伏（今回は静止/歩行どちらでも同じ `maxForce` を使う単純な実装）。

## 仕組み

### 1. `RagdollProfile.cs` — チューニングパラメータと原点リセット

```csharp
public const float DefaultReachUpperArmJointMaxForce = 1000f;
public const float DefaultReachLowerArmJointMaxForce = 1000f;
// spring/damperも同様にDefault定数化

public float reachUpperArmJointMaxForce = DefaultReachUpperArmJointMaxForce;
public float reachLowerArmJointMaxForce = DefaultReachLowerArmJointMaxForce;

[ContextMenu("Reset Reach Arm Tuning")]
public void ResetReachArmTuning()
{
    reachUpperArmJointSpring = DefaultReachUpperArmJointSpring;
    reachUpperArmJointDamper = DefaultReachUpperArmJointDamper;
    reachUpperArmJointMaxForce = DefaultReachUpperArmJointMaxForce;
    reachLowerArmJointSpring = DefaultReachLowerArmJointSpring;
    reachLowerArmJointDamper = DefaultReachLowerArmJointDamper;
    reachLowerArmJointMaxForce = DefaultReachLowerArmJointMaxForce;
#if UNITY_EDITOR
    UnityEditor.EditorUtility.SetDirty(this);
#endif
}
```

Play中にInspectorで `spring`/`damper`/`maxForce` を試行錯誤すると、何が初期値だったか分からなくなる問題があった。チューニング原点を `const` として明示し、Inspector右上の歯車メニューから戻せるようにした。これは全パラメータの汎用Resetとは別の、Reach腕ドライブだけを対象にした専用Resetにしてある（他の移動・バランス系パラメータを巻き込まないため）。

### 2. `RagDollPhysics.cs` — `ApplyReachPose` でのドライブ生成

```csharp
// Reach中は現在の profile 値から毎回 drive を作る。Play中の tuning と有限 maximumForce を即反映するため。
JointDrive upperReachDrive = JointConfigurator.CreateJointDrive(
    _context.ReachUpperArmJointSpring,
    _context.ReachUpperArmJointDamper,
    _context.ReachUpperArmJointMaxForce);
JointDrive lowerReachDrive = JointConfigurator.CreateJointDrive(
    _context.ReachLowerArmJointSpring,
    _context.ReachLowerArmJointDamper,
    _context.ReachLowerArmJointMaxForce);
```

`JointDrive` をキャッシュせず毎フレーム profile から生成しているのは、Play中に `maxForce` を変えた結果を即座に反映してホットチューニングできるようにするため（キャッシュすると再生のたびにEditor再起動が必要になる）。

### 3. `newAPRPlayer.prefab` — 肘・上腕の角度limit

肘（`APR_LowerRightArm` / `APR_LowerLeftArm`）の `angularXMotion` を `Free`(2) から `Limited`(1) に変更し、上腕（`APR_UpperRightArm` / `APR_UpperLeftArm`）にも `Limited` のX/Y/Z制限を追加した。最終的な実機検証値（左右対称）:

| 関節 | angularX (low/high) | angularY (±) | angularZ (±) |
|---|---|---|---|
| 右上腕 | -15° / 120° | 89° | 89° |
| 左上腕 | -120° / 15° | 89° | 89° |
| 右肘 | 0° / 120° | 60° | (Locked) |
| 左肘 | -120° / 0° | 60° | (Locked) |

肘は `angularZMotion` を `Locked` のままにしている。ヒンジ的に曲げ伸ばしする関節なので、Y方向のわずかな捻れだけを許容しX/Zは絞る構成。

数値は理論計算ではなく、後述のビジュアライザで腕の見た目に可動域を重ねながら実機調整した結果。

### 4. `JointLimitVisualizer.cs` — 可動域の可視化Editorツール

`Tools/REBAKA/Joint Limit Visualizer` でトグル。`ConfigurableJoint` を持つボーンを選択すると、Scene/Prefabビューに可動域を腕の実形状へ重ねて描画する。選択中は `EditorApplication.update` でライブ再描画する。

このツールが必要だった理由: `ConfigurableJoint` の angular limit は `m_Axis`(primary)/`m_SecondaryAxis` を基準にした角度で、これは**腕の見た目方向と一致しない**。Inspectorの数値（例: `lowAngularXLimit: -15, highAngularXLimit: 120`）だけを見ても、それが「肘がどこからどこまで曲がる」ことを意味するのか直感的に分からない。

[※理論] Unityのangular DOF規約: `angularX` は `axis`（primary軸）回りの**twist**（low/highの非対称limitが使える）。`angularY`/`angularZ` は `secondaryAxis` から直交化された軸回りの**swing**（±limitの対称のみ）。この対応はUnity公式マニュアルのConfigurableJoint項に基づくが、本プロジェクトでの実装はこの理解を実機の見た目とすり合わせながら反復で固めた。

#### 実装の反復で踏んだ罠（教育的に重要）

1. **X可動域が90°ズレる**: 最初の実装は twist の0°基準を「腕の方向(limbDir)」に取っていた。Unityの規約上、0°基準は腕の方向ではなく `secondaryAxis` 由来の軸。これがズレの原因。
2. **符号が反転する**: 0°基準を `secondaryAxis` 由来の `jy` に変えたところ、今度は回転の正負がゲーム内の実際の動きと逆になった。[※理論] Unityの `angularX` 正回転はPhysX由来の左手系で、`Quaternion.AngleAxis`/`Handles`系のUnity標準（右手系）と逆向き。描画軸を `-jx` に反転して解決。
3. **180°ズレる**: 軸を反転しても今度は180°分ズレていた。0°基準を `jy` ではなく `-jy` に取ることで実機の見た目と一致した（理論値だけでは符号・基準の組み合わせを断定できず、最終的に実機の挙動と突き合わせて確定させた）。
4. **swingコーンが8の字に潰れる**: `angularY`/`angularZ` の±limitを楕円錐として描く際、回転軸に直接 `jy`/`jz` を使っていた。このリグでは腕の方向(`limbDir`)が `jz` とほぼ平行になる姿勢があり、その軸でいくら回転させても腕先の位置が動かない（回転軸と回転対象がほぼ平行＝退化）。これが8の字状の潰れとして見えていた。`limbDir` に直交する軸 `u = Cross(limbDir, jx)` / `v = Cross(limbDir, u)` を新たに作り、それを楕円錐の基底に使うことで、どの姿勢でも両軸が必ず腕先を動かせるようにして解決。

この4段階はいずれも「数式は合っているはずなのに見た目と合わない」という形で現れ、**実機の見た目との突き合わせなしに理論だけで解こうとすると正しい符号・基準に辿り着けない**、というのが今回得た教訓。

## 自力再実装チェックリスト

- [ ] `ConfigurableJoint.angularXMotion` (twist) と `angularYMotion`/`angularZMotion` (swing) の違いを説明できる
- [ ] `m_Axis`(primary)・`m_SecondaryAxis`から、ワールド空間での正規直交フレーム `jx`/`jy`/`jz` をどう作るか（`jz = Cross(jx, secondary)`, `jy = Cross(jz, jx)`）を再現できる
- [ ] twistの0°基準が「腕の見た目方向」ではなく「secondary軸」であることを、実機検証なしに人に説明できる
- [ ] `JointDrive.maximumForce` を有限値にすることで「降伏」が起きる理由（spring/damperによる目標復元力に上限が掛かる）を説明できる
- [ ] なぜ「理論上正しいはずの符号」が実機と食い違うことがあるのか（PhysXの座標系規約とUnity APIの規約が場面によって逆になりうる）を説明できる

## 関連

- `Assets/Code/Scripts/Player/RagdollProfile.cs`
- `Assets/Code/Scripts/Player/RagDollPhysics.cs`（`ApplyReachPose`）
- `Assets/Level/Prefabs/newAPRPlayer.prefab`（肘・上腕のConfigurableJoint）
- `Assets/Code/Editor/JointLimitVisualizer.cs`
- 関連コミット: `d232b97`（ビジュアライザのCS0103修正）
