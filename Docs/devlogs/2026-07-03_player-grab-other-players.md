# 2026-07-03 他プレイヤーを掴めないバグの修正

## 問題

プレイヤーが他のプレイヤーを掴めない。Cube や Treasure は掴める。

## 根本原因（2段構え）

`RagdollHandContact.OnCollisionEnter` に、他プレイヤー掴みを不可能にする欠陥が2つ重なっていた。

1. **レイヤー比較による「自己」除外が他プレイヤーも弾く**
   ```csharp
   if (collision.gameObject.layer == gameObject.layer) return; // 「自分自身は除外」のつもり
   ```
   newAPRPlayer.prefab の全49パーツは Player レイヤー(6)。プレイヤーは全員同じ prefab から
   スポーンするため、他プレイヤーのパーツも常に layer==6 で一致し、タグ判定を通過しても
   ここで必ず return していた。この判定は初期コミットから存在（レイヤー分布も一貫して全6）。

2. **NetworkObject をパーツ直上から取得していて null になる**
   ```csharp
   NetworkObject netObj = collision.gameObject.GetComponent<NetworkObject>();
   ```
   プレイヤーの NetworkObject はルート1個のみ（ボディパーツには無い）。仮に(1)を通過しても
   netObj=null → `DoGrab(default)` → 「invalid NetworkId」で棄却される。
   Cube/Treasure は NO がオブジェクト自身に付いているので影響なし＝Cubeだけ掴める非対称の説明。

## 修正内容（RagdollHandContact.cs）

- 自己除外: レイヤー比較 → `GetComponentInParent<NetworkObject>() == Object`（同一 NO 所属か）
- NO 解決: パーツ直上 → 親階層から取得
- `DoGrab`: 接触した四肢の Rigidbody（`_lastContactedRigidbody`、対象 NO の子であることを確認）を
  優先してジョイント接続。従来の `netObj.GetComponent<Rigidbody>()` はフォールバックとして維持
  （Cube/Treasure は従来と同一挙動）。

## 不採用案

- **プレイヤーごとに実行時レイヤーを割り振る**: レイヤー枠を消費し、衝突マトリクス・カメラ
  マスク等への波及が大きい。自己判定は所属階層で十分。
- **タグでの自他判別**: タグはパーツ種別（CanBeGrabbed）に使用中で、自他の区別には使えない。

## 付随

- 未コミットだった prefab 差分（APR_LeftHand のタグ CanBeGrabbed→Player、経緯不明・左右不整合）
  は revert し、両手とも CanBeGrabbed に統一。

## 追補（同日・第2ラウンド）: GetComponentInParent では届かなかった

初回修正後もまだ掴めなかった。ユーザー仮説は「prefab GUID が同一だから自他を誤判定」だったが、
`targetOwner == Object` は Unity のインスタンス比較（ネイティブオブジェクト同一性）なので、
同じ prefab 由来でもランタイムの別インスタンスが一致することはない。仮説は棄却。

実際の原因は **DetachRootFromParent()**（RagDollController の Spawned 内）。
APR_Root はスポーン直後にワールド直下へ切り離されるため、ボディパーツから
`GetComponentInParent<NetworkObject>()` を辿っても NetworkObject に届かず null になる。
null → 自他判定は通過するが `DoGrab(default)` → invalid NetworkId で棄却、の経路で失敗していた。

### 第2修正

- `RagdollBodyOwnerRegistry`（新規・static）: Rigidbody → 所有 NetworkObject の対応表。
  RagDollController.Spawned（detach 前）に `GetComponentsInChildren<Rigidbody>` を登録、
  Despawned で解除。
- `RagdollHandContact.OnCollisionEnter`: 所有者解決を「レジストリ第一 → 親階層フォールバック
  （Cube/Treasure 用）」に変更。自他判定は所有 NO の同一性比較のまま。
- `DoGrab`: `IsChildOf` 判定（detach 後は常に false）をやめ、`_lastContactedObjectId == objId`
  の一致確認で接触パーツの Rigidbody を採用。

### 教訓

- 「階層を辿れば親に届く」という前提は、ランタイムに reparent/detach するプロジェクトでは
  成立しない。このプロジェクトでは APR_Root の detach が既知の罠（FixHandHierarchy 削除の
  経緯コメントにも同根の教訓が残っていた）。

## 検証

- [ ] 2-client 検証（`.claude/skills/two-client-verification/`）: ホスト⇔クライアント間で
      相互に掴めること、自分の体は掴まないこと、Cube/Treasure の掴みが退行していないこと。

## 自力再実装チェックリスト

- [ ] 「自分を除外する」条件を書くとき、その条件が「自分と同種の他者」も除外しないか確認したか
- [ ] Fusion で `GetComponent<NetworkObject>` を書くとき、NO がその GameObject 自身に
      付いている保証があるか（無ければ `GetComponentInParent`）
- [ ] ジョイント接続先は「触れた剛体」か「ルート剛体」か、意図を明示したか
