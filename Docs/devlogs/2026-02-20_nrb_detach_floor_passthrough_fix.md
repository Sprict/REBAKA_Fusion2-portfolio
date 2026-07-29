# 開発ログ: 2026-02-20 - 床すり抜け時のNetworkRigidbody構成見直し

> **公開時注記（2026-07-22）:** これは2026年2月時点の旧プレハブと旧クラス構成を対象にした記録です。NetworkRigidbodyを削除した後に床すり抜けが解消したことと、Hierarchy上の警告は観測済みですが、機能不全の内部機序には推測が含まれます。公開抜粋には対象プレハブを含めていません。現在の同期構成は[`ARCHITECTURE_OVERVIEW.md`](../ARCHITECTURE_OVERVIEW.md)を優先してください。

## 1. 症状（何が起きていたか）

- プレイヤーがスポーンした直後から床をすり抜けて落下し続ける
- スポーン位置自体は正しい（Y=6 のスポーンポイントからスポーンされていることをログで確認済み）
- 物理シミュレーション自体は動いている（重力で落下するため）
- 床との衝突だけが機能しない状態

```
[PlayerSpawner] Player 1 spawned at (-4.84, 6.00, 2.67). Tracked=1
[RagdollController] SetupCollisionIgnores: 351 collider pairs set to ignore collision
```

## 2. 調査プロセス（どうやって原因を特定したか）

### 最初の仮説

「スポーン位置が悪いのでは？」という仮説から調査開始。

### 調査手順

1. **スポーンロジックの確認**: `PlayerSpawner.cs` → `SpawnPointManager.AssignSpawnPoint()` の経路を確認。スポーンポイントが正しく割り当てられていることを確認（Y=6）

2. **GameLauncher との二重スポーンを疑う**: `GameLauncher.cs` と `PlayerSpawner.cs` の両方が `OnPlayerJoined` でスポーンするコードを持っていた。しかしユーザーが「GameLauncherはシーンに存在しない」と確認し、除外

3. **コライダー設定を確認**: Floor_Flat.prefab の BoxCollider / MeshCollider は `isTrigger: false`、`m_Enabled: 1` で正常

4. **LayerCollisionMatrix を確認**: `DynamicsManager.asset` の `m_LayerCollisionMatrix` が全て `f` → 全レイヤー間で衝突が有効。Layer 0 (Default) と Layer 3 (Ground) も衝突する設定

5. **SetupCollisionIgnores を疑う（ユーザー提起）**: 351ペアの ignore 設定が原因では？ → 否。`GetComponentsInChildren<Collider>()` はプレイヤー自身の階層内のコライダーのみを収集するため、床（別GameObject）のコライダーには一切影響しない

6. **プレハブの構造確認**: `newAPRPlayer.prefab` の APR_Root に **`Fusion.Addons.Physics.NetworkRigidbody`** がアタッチされていることを発見（`m_EditorClassIdentifier` で特定）

7. **ヒエラルキーのスクリーンショットで確認**: ユーザーが Unity の Hierarchy と Inspector を確認し、「NetworkBehaviour requires a NetworkObject component to function」の警告が出ていることを確認。全ボディパーツが階層から外れており、NetworkObject がない状態で NRB が動作しようとしていた

### 原因の絞り込み

`DetachRootFromParent()` によって APR_Root が親（newAPRPlayer）の NetworkObject 階層から切り離された後、APR_Root にアタッチされた NetworkRigidbody が親 NetworkObject を見つけられず機能不全に陥っていた。NRB が Rigidbody の物理処理（コリジョン応答を含む）を妨害していた。

## 3. 原因（なぜ起きていたか）

### コードレベルの原因

**問題の流れ:**

```csharp
// RagdollController.Spawned() の処理順序
// (1) APR_Root は newAPRPlayer の子として存在し、NetworkObject 配下にある
InitializeRigidbodies();  // Rigidbody を WakeUp、重力有効化

// (2) APR_Root を親から切り離す
DetachRootFromParent();   // SetParent(null, true) → 親なし状態に
// ↑ この時点で APR_Root は newAPRPlayer の NetworkObject 階層外になる

// APR_Root にある NetworkRigidbody は NetworkBehaviour を継承しており、
// 親の NetworkObject が見つからない → 機能不全
```

**問題のコンポーネント構成（修正前）:**

```
newAPRPlayer (NetworkObject あり)
  └─ Armature
       └─ APR_Root (NetworkRigidbody あり ← 問題)
            ├─ APR_Body
            ├─ APR_Head
            ├─ APR_LeftFoot
            └─ ...（各ボディパーツ）
```

`DetachRootFromParent()` 後:

```
newAPRPlayer (NetworkObject あり)    APR_Root (NetworkRigidbody あり、孤立)
  └─ Armature                           ← NetworkObject から切り離された！
```

### なぜこのコンポーネント構成が問題だったか

**NetworkRigidbody（NRB）の前提条件:**

[※理論] Photon Fusion 2 の公式ドキュメントには以下の記述がある：

> "The `NetworkRigidbody` must be on the root of the Network Object."
>
> "you cannot rearrange the children of a single Network Object."

NRB は `NetworkBehaviour` を継承しており、動作には親階層に `NetworkObject` が存在することが必須。`DetachRootFromParent()` で `SetParent(null, true)` を呼ぶと APR_Root は NetworkObject の管轄外になり、NRB が機能しなくなる。

**コライダーとRigidbodyへの影響（推測）:**

[※推測] NRB が機能不全になった場合、その内部で Rigidbody の状態（isKinematic、position 等）を不正な値で上書きしていた可能性がある。結果として物理コリジョン応答が無効化され、床をすり抜ける現象が発生した。

### 背景にある原理

**DetachRootFromParent() を使う理由:**

親オブジェクト（newAPRPlayer の Transform）が毎ティック Fusion によって位置を上書きされると、APR_Root の物理シミュレーション結果が干渉されてしまう。これを防ぐために APR_Root を親から切り離し、独立した Rigidbody として動作させる設計になっている。

**NRB と DetachRootFromParent() の根本的な非互換性:**

| | NRB の要件 | DetachRootFromParent() の動作 |
|---|---|---|
| NetworkObject との関係 | 同じ階層内に必要 | 階層から外れる |
| 親オブジェクト | 変更不可（同一NetworkObject内） | null に設定 |

この2つは設計上共存できない。

**アクティブラグドールで NRB を使うべきか:**

[※推測] このプロジェクトのような「複数 Rigidbody を動的に階層から切り離すラグドール」では、NRB よりも `[Networked]` プロパティによるカスタム同期の方が適切。すでに `PublishProxyPoseSnapshot()` でルート・頭・手の位置を `[Networked]` プロパティ経由で同期しているため、NRB は冗長だった。

## 4. 解決策（何をどう変えたか）

### 修正内容

Unity Inspector 上で `newAPRPlayer.prefab` の **APR_Root（および全ボディパーツ）から NetworkRigidbody コンポーネントを削除**した。

**コード変更は不要**。`RagdollController.cs` の全 `_rootNetworkRigidbody` 参照箇所はすでに null チェック済みだったため：

```csharp
// RagdollController.cs:229 - NRB が null でも動作継続
if (_rootNetworkRigidbody == null && !useLegacyCustomRootCorrection)
{
    Debug.LogWarning("[RagdollController] APR_Root に NetworkRigidbody が見つかりません。...");
    // ← 警告を出すだけ。処理は継続する
}
```

```csharp
// RagdollController.cs:844 - 見つからなければ null を返すだけ
_rootNetworkRigidbody = FindRootNetworkRigidbodyComponent();
// → NRB が存在しない場合は null になるが、以降の null チェックで安全に処理
```

**修正後のコンポーネント構成:**

```
newAPRPlayer (NetworkObject あり)
  └─ Armature
       └─ APR_Root (Rigidbody、BoxCollider、ConfigurableJoint のみ)
            ├─ APR_Body
            ├─ APR_Head
            └─ ...（各ボディパーツ）
```

### コードの各行が「なぜこう書く必要があるか」

カスタム同期（`PublishProxyPoseSnapshot()`）はすでに以下を同期している：

```csharp
[Networked] private Vector3 NetRootPosition { get; set; }
[Networked] private Quaternion NetRootRotation { get; set; }
[Networked] private Vector3 NetRootLinearVelocity { get; set; }
[Networked] private Vector3 NetRootAngularVelocity { get; set; }
[Networked] private Vector3 NetHeadPosition { get; set; }
[Networked] private Quaternion NetHeadRotation { get; set; }
// ...（手の位置も同様）
```

NRB が行う「Rigidbody の位置をネットワーク状態に書き出す / 読み込む」処理を、カスタム同期が代替している。NRB は不要だった。

### よくある間違い（アンチパターン）

```
❌ NetworkRigidbody を「Rigidbody があるオブジェクトには全部つける」という考え方
```

理由：
- NRB は NetworkObject のルートに1つだけ配置するのが前提
- 動的に階層変更するオブジェクトには使えない
- カスタム同期（`[Networked]` プロパティ）が既にある場合は冗長

## 5. 検討した代替案

| 代替案 | 評価 | 不採用の理由 |
|--------|------|-------------|
| A: APR_Root に NetworkObject を追加して NRB を使い続ける | △ | NestedNetworkObject の管理が複雑になる。DetachRootFromParent() 後の Nested NO の扱いが不明確 |
| B: DetachRootFromParent() をやめる | × | 親 Transform の毎ティック上書きが APR_Root の物理に干渉する問題が再発する |
| C: **NRB を全削除してカスタム同期のみにする ★** | ○ | カスタム同期がすでに実装済みで NRB は冗長。コード変更不要でシンプル |

## 6. 教訓（今後同様の問題に遭遇したときのヒント）

### このバグのパターン

**「コンポーネントの前提条件と動的な階層変更の非互換」**

Unity の多くのコンポーネント（特にネットワーク系）は「初期化時の階層構造が維持される」という前提で動作する。動的に `SetParent()` で階層を変更すると、これらのコンポーネントが前提を破られて機能不全になる。

### 同じパターンのバグに遭遇したときの対処手順

1. **「このコンポーネントの前提条件は何か？」を確認する**
   - NetworkBehaviour 系は必ず「どの NetworkObject 配下にあるか」を確認
   - 公式ドキュメントに "must be on the root of..." のような制約が書かれていないか確認

2. **動的な階層変更（SetParent/DetachRootFromParent）との組み合わせを疑う**
   - Inspector で "This NetworkBehaviour requires a NetworkObject component to function" 警告が出ていないか確認
   - Hierarchy でオブジェクトが意図した位置にあるか（階層外に飛び出していないか）確認

3. **代替手段（カスタム同期）が既にあるか確認する**
   - `[Networked]` プロパティや RPC で同等の同期が実装済みなら、該当コンポーネントは冗長で削除可能

### 予防策

- `DetachRootFromParent()` のような階層変更を行うオブジェクトには、NetworkBehaviour を継承するコンポーネントを極力つけない
- アクティブラグドールでは「ルートのみ NRB」か「全部カスタム同期」のどちらかに統一する。混在させない
- スポーン直後に床すり抜けや重力無効が発生したら、まず NRB の状態（isKinematic の予期しない変更）を疑う

### 関連する技術概念

[※理論] **Photon Fusion 2 の NetworkRigidbody (NRB) の制約:**

- NRB は `NetworkBehaviour` を継承 → 親階層に `NetworkObject` が必要（必須）
- NRB は同じ GameObject の `Rigidbody` のみを対象（子や親の RB は非対象）
- 1 GameObject に 1 NRB のみ（`[DisallowMultipleComponent]` 属性）
- 公式: "you cannot rearrange the children of a single Network Object"

**カスタム同期による代替:**

NRB が行う「位置/回転/速度をネットワーク状態へ書き出し・読み込み」は、`[Networked]` プロパティ + `FixedUpdateNetwork()` での手動コピーで再現できる。このプロジェクトでは `PublishProxyPoseSnapshot()` / `EnsureClientProxyBootstrap()` がその役割を担っている。

参考: [Photon Fusion 2 Physics Addon ドキュメント](https://doc.photonengine.com/fusion/current/addons/physics-addon-2.0)

## 7. 自力で再実装するためのチェックリスト

同じ問題（NRB + 動的階層変更の非互換）を自分で解決する場合:

- [ ] Inspector の "This NetworkBehaviour requires a NetworkObject" 警告を確認
- [ ] Hierarchy で全ボディパーツが NetworkObject 配下にあるか確認（Play Mode 中）
- [ ] `DetachRootFromParent()` の後、該当オブジェクトに NetworkBehaviour 系コンポーネントがないか確認
- [ ] カスタム同期（`[Networked]` プロパティ）が既に実装されているなら NRB は削除対象と判断する
- [ ] NRB 削除後、コード中の `_rootNetworkRigidbody` 参照箇所が全て null チェック済みか確認
- [ ] 削除後に Play Mode でスポーン → 床着地が正常に動作するか確認（30秒以上）

---

**修正日**: 2026-02-20
**修正ファイル**:
- `Assets/Level/Prefabs/newAPRPlayer.prefab`（Inspector で NetworkRigidbody を削除）
**コードへの変更**: なし（`RagdollController.cs` は null チェック済みのため変更不要）
