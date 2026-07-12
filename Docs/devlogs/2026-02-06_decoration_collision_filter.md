# 装飾オブジェクトの衝突フィルタリング

**日付:** 2026-02-06
**カテゴリ:** fix / physics
**関連ファイル:** `RagdollController.cs`

---

## 問題

`RagdollImpactContact.OnCollisionEnter` が Sphere (4)（頭の飾り）との衝突を大量に検知していた。

**症状:**
- コンソールに「衝撃」ログが大量出力される
- 不要な衝突コールバック発火 → サウンド再生判定等の計算コスト
- Collider を外すと飾りが物理的に揺れなくなるため、単純に外すことはできない

## 原因分析

`SetupCollisionIgnores()` は `bodyParts[]` 配列に含まれるコライダーのみを対象にしていた。

```csharp
// 旧コード: bodyPartsのみ対象
var allColliders = new Collider[bodyParts.Length];
for (var i = 0; i < bodyParts.Length; i++)
    if (bodyParts[i] != null)
        allColliders[i] = bodyParts[i].GetComponent<Collider>();
```

Sphere (4) 等の装飾オブジェクトは `bodyParts` に含まれていないため、同一プレイヤー内であっても衝突が発生し `OnCollisionEnter` が発火していた。

## 解決策

`bodyParts` からコライダーを手動取得する方式をやめ、`GetComponentsInChildren<Collider>()` でプレイヤー階層内の全コライダーを自動取得する方式に変更した。

```csharp
// 新コード: 階層内全コライダーを対象
var allColliders = GetComponentsInChildren<Collider>();
```

### なぜこのアプローチか

| 代替案 | 評価 |
|--------|------|
| **A: OnCollisionEnterでタグ判定** | ×：衝突コールバック自体は発火し続ける（CPU負荷残る） |
| **B: 装飾にレイヤー設定** | △：Inspector作業が必要、装飾追加のたびに設定必要 |
| **C: GetComponentsInChildrenで全コライダー取得** ★採用 | ○：Physicsレベルで無視→コールバック発火なし、自動で全装飾カバー |

**Physics.IgnoreCollision** はPhysicsエンジンレベルで衝突を無視するため、コールバック自体が発火しない。これが最も効率的。

### 変更の影響

- Collider は残るので装飾の物理的な揺れは維持される
- `ignoredCount` が増える（bodyPartsの78ペア → 全コライダーのC(n,2)ペア）
- 初期化時の1回だけの処理なので実行時コストはゼロ
- 今後装飾が増えても自動的にカバーされる

## 想定される質問

**Q: 同一ラグドール内の衝突無効化はどう実装しましたか？**

A: `GetComponentsInChildren<Collider>()` でプレイヤー階層内の全コライダーを取得し、全ペアに `Physics.IgnoreCollision()` を設定しています。最初は `bodyParts` 配列のコライダーだけを対象にしていましたが、頭の飾りなど装飾オブジェクトのコライダーが漏れて不要な衝突が発生する問題があったため、階層内全コライダーに拡張しました。Physics.IgnoreCollision はPhysicsエンジンレベルで衝突を無視するため、OnCollisionEnterコールバック自体が発火せず最も効率的です。
