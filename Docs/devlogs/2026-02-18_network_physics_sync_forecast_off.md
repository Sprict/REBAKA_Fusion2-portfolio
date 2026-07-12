# ネットワーク物理同期 Forecast OFF 実装

**日付**: 2026-02-18
**種別**: feat (ネットワーク同期)
**関連ファイル**: `RagDollPhysics.cs`, `RagDollController.cs`, `RagdollNetworkSetup.cs`

---

## 問題

Client側で全プレイヤーが高速振動する。

**根本原因**: 15個のRigidbodyのうち APR_Root の1個だけに `NetworkRigidbody` があった。
残り14個のボディパーツはネットワーク非同期のため、Proxy側でローカル物理とネットワーク補正が
「引っ張り合い」して振動が発生していた。

---

## 採用アーキテクチャ: Forecast OFF + 全ボディパーツに NetworkRigidbody

### なぜこのアプローチか

**Forecast ON（状態予測）の問題点**:
- TickRateと同じ頻度で物理予測を再実行する必要がある
- Proxy側でも物理シミュレーションが走り続けるため CPU 負荷が高い
- ラグドールの複雑な関節連鎖では予測誤差が累積しやすい

**Forecast OFF（現状維持）を選んだ理由**:
- `NetworkProjectConfig.fusion` が既に `PhysicsForecast = false`
- Proxy側の Rigidbody が自動 Kinematic 化される（FusionランタイムDLLの機能）
- State Authority（Host）が物理を計算 → 結果をネットワーク送信 → Proxy は補間表示
- ラグドール特有の複雑な物理は Host 側で一元管理するのが安全

**不採用にした代替案**:
- Kinematic を手動で切り替える: コードが複雑になる、Fusionとの二重管理が危険
- 全ボディパーツを NetworkTransform で同期: データ量が大きく非推奨（NetworkRigidbodyが適切）

---

## 実装内容

### 1. RagDollPhysics.cs: deltaTime 引数化

`UpdatePhysics()` に `float deltaTime` を追加し、内部の `Time.deltaTime`（6箇所）と
`Time.fixedDeltaTime`（11箇所）を全て `deltaTime` に置換した。

**なぜこの変更が必要か**:
- `FixedUpdateNetwork()` は Fusion が固定タイムステップで呼ぶ
- そのデルタタイムは `Runner.DeltaTime` で取得する
- `Time.fixedDeltaTime` は Unity のデフォルト 0.02 秒固定なので、
  Fusion が異なる TickRate で動作する場合に不整合が生じる

```csharp
// Before
public void UpdatePhysics(..., bool wantsPunchRight, bool wantsPunchLeft)

// After
public void UpdatePhysics(..., bool wantsPunchRight, bool wantsPunchLeft, float deltaTime)
```

### 2. RagDollController.cs: Runner.DeltaTime を渡す

```csharp
_ragdollPhysics.UpdatePhysics(
    CurrentState, MoveDirection, FacingDirection, LookDirection,
    cmd.IsPunchingRight, cmd.IsPunchingLeft,
    Runner.DeltaTime  // ← 追加
);
```

### 3. RagdollNetworkSetup.cs: Editor ユーティリティ

`Assets/Code/Editor/RagdollNetworkSetup.cs` を新規作成。

**実行手順**:
1. Unity メニュー「Tools/REBAKA/Setup Ragdoll NetworkRigidbody」を実行
2. `newAPRPlayer.prefab` の全 `APR_*` GameObjectに `NetworkRigidbody3D` が追加される
   - `SyncParent = false`（親管理は APR_Root に委ねる）
   - `SyncScale = false`
3. 全 Rigidbody（Sphere含む）の `interpolation = Interpolate` に設定
4. プレハブ保存後、Prefab を開き NetworkObject の「Rebuild Object Table」をクリック

---

## 仕組みの説明

```
Host (State Authority)
  ├── 全15 Rigidbody でアクティブ物理実行
  └── 結果をネットワーク送信

Proxy (Client, 他プレイヤーを表示)
  ├── NetworkRigidbody が自動 Kinematic 化
  └── ネットワーク補間で位置・回転を表示 → 振動しない

自キャラ (Input Authority)
  └── Host と同じ物理ロジックで実行（ローカル予測）
```

---

## 検証方法

1. Unity Editor で Host と Client（2インスタンス）を起動
2. Client 側でプレイヤーが振動しないことを確認
3. Client 側 Hierarchy で各 `APR_*` の `Rigidbody.isKinematic == true` を確認
4. Fusion Statistics で帯域使用量を確認
5. 移動・パンチ操作のレスポンスを確認

---

## 自力再実装チェックリスト

- [ ] NetworkRigidbody3D は NetworkBehaviour の一種。追加後は NetworkObject の「Rebuild Object Table」が必要
- [ ] Forecast OFF の場合、Proxy の Rigidbody は FusionランタイムDLL が自動 Kinematic 化する
- [ ] `SyncParent = false` は「このRigidbodyの親子関係はFusionが管理しない」という意味
- [ ] `Runner.DeltaTime` は Fusion の固定タイムステップ。`Time.fixedDeltaTime` とは別物
- [ ] Sphere 形状の Rigidbody（計12個）は ConfigurableJoint でボディパーツに追従するため個別同期不要
