# 開発ログ: 2026-02-04 - アクティブラグドールプレイヤーの震えと向きの問題修正

## 発生した問題

アクティブラグドールプレイヤーで以下の2つの問題が発生していました：

1. **プルプル震える問題**
   - スポーン後、プレイヤーが何も操作していなくても震える
   - 移動やジャンプしても震えが治らない

2. **向きが戻る問題**
   - WASDキーでプレイヤーの向きを変更しても、入力を辞めるとすぐにスポーン時と同じ向き（Z+方向）に戻ろうとする

## 原因分析

### 原因1: 向きが戻る問題
`RagdollPhysics.cs:342` の `UpdateRootTargetRotation()` メソッドで、以下の誤った計算を使用していた：

```csharp
// 誤り: Quaternion.identity を使用していたため、常にワールドZ+方向を向こうとする
Quaternion initialWorldRot = Quaternion.identity; // スポーン時は直立と仮定
_bodyJoints[IndexRoot].targetRotation = Quaternion.Inverse(newTarget) * initialWorldRot;
```

これにより、プレイヤーが現在の向きを維持せず、常にワールド座標系のZ+方向（Quaternion.identity）に戻ろうとしていた。

### 原因2: 震え問題
`ApplyUprightForce()` メソッドで：
- PID制御のデッドゾーンが2度と小さすぎた
- ジョイントドライブとPID制御が競合し、微細な振動を発生していた
- デッドゾーン内でもPID積分項が蓄積され、微小なトルクが継続的に発生していた

## 実施した修正

### 修正1: スポーン時回転の保存と正しい計算

**ファイル**: `Assets/Code/Scripts/Player/RagdollPhysics.cs`

1. **新しいフィールドの追加** (行88-89):
```csharp
// スポーン時の回転を保存（向き維持用）
private Quaternion _spawnWorldRotation;
```

2. **コンストラクタでの保存** (行120-125):
```csharp
// スポーン時のワールド回転を保存（向き維持用）
if (bodyRigidbodies != null && bodyRigidbodies.Length > IndexRoot && bodyRigidbodies[IndexRoot] != null)
{
    _spawnWorldRotation = bodyRigidbodies[IndexRoot].rotation;
    Debug.Log(
        $"APR_Root: isKinematic={bodyRigidbodies[IndexRoot].isKinematic}, useGravity={bodyRigidbodies[IndexRoot].useGravity}, SpawnRotation={_spawnWorldRotation.eulerAngles}");
}
```

3. **UpdateRootTargetRotation() の修正** (行339-351):
```csharp
// 正しい計算: targetRotation = Inverse(spawnRotation) * targetWorldRotation
// これにより、スポーン時からの相対回転が正しく計算される
_bodyJoints[IndexRoot].targetRotation = Quaternion.Inverse(_spawnWorldRotation) * targetWorldRot;
```

### 修正2: デッドゾーンの拡大

**ファイル**: `Assets/Code/Scripts/Player/RagdollPhysics.cs`

`ApplyUprightForce()` メソッド (行800-801):
```csharp
// デッドゾーン: 5度未満の傾きは無視（身震い防止）
const float deadZoneDegrees = 5f;  // 変更前: 2f
```

### 修正3: スポーン時の初期化改善

**ファイル**: `Assets/Code/Scripts/Player/RagdollPhysics.cs`

`ApplyInitialJointDrives()` メソッドに以下を追加 (行207-215):
```csharp
// ルートのtargetRotationをスポーン時の向きに設定（直立姿勢）
// これによりスポーン直後から正しい向きで直立しようとする
if (_bodyRigidbodies[IndexRoot] != null)
{
    Vector3 spawnEuler = _spawnWorldRotation.eulerAngles;
    Quaternion uprightRotation = Quaternion.Euler(0f, spawnEuler.y, 0f);
    _bodyJoints[IndexRoot].targetRotation = Quaternion.Inverse(_spawnWorldRotation) * uprightRotation;
}
```

## 修正の効果

1. **向きの維持**: WASDキーで向きを変更しても、入力を辞めてもその向きを維持するようになった
2. **震えの軽減**: デッドゾーンを5度に拡大することで、微細な振動が大幅に減少
3. **スポーン時の安定性**: 初期化時に正しい向きを設定することで、スポーン直後から安定した姿勢を維持

## 技術的な補足

### Quaternion計算の解説

ConfigurableJointの`targetRotation`はJoint空間での回転を表す。正しく計算するには：

```
targetRotation = Inverse(spawnWorldRotation) * desiredWorldRotation
```

これにより、スポーン時の回転から見た相対的な目標回転が得られる。

### PID制御のデッドゾーン

デッドゾーンを設けることで：
- 小さな傾きに対してPID制御を無効化
- 積分項の蓄積を防ぐ
- ジョイントドライブとの競合を減らす

5度という値は、歩行中の自然な揺れを許容しつつ、不要な微修正を防ぐ適切なバランスである。

## 今後の改善案

1. **デッドゾーンの動的調整**: 状態（Idle/Walking）に応じてデッドゾーンを動的に変更
2. **ジョイントドライブの最適化**: PID制御とジョイントドライブの強度バランスをさらに調整
3. **スポーン位置のランダム化**: 複数プレイヤーが同じ位置にスポーンしないように

---

**修正日**: 2026-02-04  
**修正ファイル**: `Assets/Code/Scripts/Player/RagdollPhysics.cs`  
**修正者**: opencode (AI Assistant)
