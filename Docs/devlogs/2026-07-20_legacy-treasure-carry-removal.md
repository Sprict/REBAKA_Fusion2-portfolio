# 2026-07-20 旧Treasure専用運搬システムの一括削除

> **公開時注記（2026-07-22）:** 本資料の削除結果は公開コードへ反映済みです。`Treasure_Heavy`というゲーム上のオブジェクト名は残っていますが、専用の質量分配・運搬ハーネス・設定classは現行コードに存在しません。

## 問題

`Treasure_Heavy` は2026-07-09に `Treasure` / `TreasureDebugLabel` を外し、`CanBeGrabbed`タグ・mass=80の`Rigidbody`・`GameNetworkRigidbody`で動く一般オブジェクトへ変更済みだった。一方、旧Treasure専用の質量分配、掴み通知、専用ジョイント、運搬ハーネス、設定値、テストはコードベースに残っていた。

現行prefabから到達しないコードでも、読む人には「Treasure_Heavyが専用方式で動く」ように見える。面接用のコード読解や今後の変更で誤った前提を作らないため、旧システムを一部分だけでなく一つの縦断的な機能単位として削除した。

## 調査プロセスと根拠

1. ユーザーが「旧Treasure専用運搬システムは現在未使用」「Treasure_Heavyは一般的な重いRigidbodyとして維持」と削除境界を指定した。
2. GPT-5 Codexが関連devlogと現行コードを読み、`rg`でruntime・asset・test・docsの依存を監査した。
3. `Treasure_Heavy.prefab`をGUIDレベルで確認し、旧`Treasure` / `TreasureDebugLabel` script参照が無いこと、`CanBeGrabbed`、mass=80、dynamic Rigidbody、`GameNetworkRigidbody`が残っていることを確認した。
4. `MapTreasureSpawner`は`NetworkObject` prefabをspawnするだけで旧`Treasure`型に依存しないため、削除対象から外した。

直接の判断根拠は`2026-07-09_grab-joint-stability-and-carry-drive-cleanup.md`の実機記録である。そこでは一般の重いRigidbody + FixedJoint + 複数人の力の合成で狙った協力運搬が成立し、旧専用システムの使用例が0件になったことが記録されている。

## 削除した一式

| 層 | 削除内容 |
|---|---|
| 質量分配 | `Treasure`、`TreasureGrabRegistry`、`TreasureProfile`、`TreasureDebugLabel`、`TreasureProfile_Heavy.asset` |
| 掴み通知 | `RagdollHandContact`のTreasure型判定、grab/release通知、専用breakForce override |
| 掴み拘束 | Treasure専用`ConfigurableJoint` + `grabDriveSpring/Damper/MaxForce` |
| 運搬ハーネス | `IRagdollTreasureCarryContext`、Controllerのref count、root-Treasure harness生成/破棄 |
| 移動制限 | `RagDollPhysics`のTreasure保持中だけの`carryMoveMaxForce` force clamp |
| 設定 | 現行/backup RagdollProfile assetのgrab/carry値、残骸の`carryLegDriveMaxForce` |
| テスト | `TreasureGrabRegistryTests`、`TreasureProfileTests` |

## 維持した現行方式

- `RagdollHandContact`は対象種別で分岐せず、プレイヤーと`CanBeGrabbed`一般オブジェクトを同じ`FixedJoint`で掴む。
- 破断閾値は既存の`RagdollProfile.genericGrabBreakForce`を使う。
- `Treasure_Heavy.prefab`とVariant、`MapTreasureSpawner`は維持する。
- Treasureの重さは動的な人数割りではなく、prefabの固定`Rigidbody.mass=80`として扱う。

現行`Treasure_Heavy`には旧`Treasure` componentが無かったため、削除前も実行時には一般`FixedJoint`分岐を通っていた。今回の変更は、その現行挙動にコード構造を一致させる整理である。

## 不採用案

- **旧classだけ削除し、Player側の通知・harness・設定を残す**: 到達不能コードとInspector上の死んだ調整値が残り、混乱防止という目的を満たさない。
- **将来用として`TreasureGrabRegistry`だけ残す**: 現行仕様・使用例・再導入計画がなく、履歴はdevlogとgitに残るためYAGNIと判断した。
- **`MapTreasureSpawner`やTreasure prefab名まで削除する**: それらは「宝を配置するゲーム上の役割」であり、旧専用運搬実装への依存ではない。

## 検証

- `Assets/`に対する旧symbol/GUIDの`rg`: 残存0件。
- Unity Editorのコンパイル: 0 warnings / 0 errors。
- `dotnet build MyProject.Scripts.csproj --no-restore --nologo -m:1`: 0 warnings / 0 errors。
- `dotnet build MyProject.EditModeTests.csproj --no-restore --nologo -m:1`: 初回は0 warnings / 0 errors。Unity Test Runner終了後の再実行では、削除された`Temp/Bin`配下の生成DLLを参照してCS0006になった。ソースのコンパイル可否は、直後のUnity Test Runner実行結果を正本とする。
- 影響fixture（`RagdollProfileTuningTests` + `MapWiringCheckTests`）: 18/18成功。
- 全EditMode: 114件中113件成功。既存の`BalanceJointVibrationTests.MainPlayerProfile_BalanceDamperRatio_IsAtLeastImprovedValue`だけ失敗（期待`>=0.15`、現行asset`0.025`）。今回の差分はこの値を変更していない。
- `Treasure_Heavy.prefab`: `CanBeGrabbed`、mass=80、`m_IsKinematic=0`、旧script GUID無しをYAMLで確認。
- Play Mode / 2-clientの手動再検証は未実施。旧経路は現行prefabから到達していなかったため、今回の削除で新しいネットワーク同期方式や権限分担は導入していない。

## 自力再実装チェックリスト

- [ ] prefabに`Treasure` componentが無いと、なぜ削除前から一般`FixedJoint`分岐を通っていたか説明できるか
- [ ] 質量を人数で割らなくても、複数のプレイヤーが同じ重いRigidbodyを引く力の合成で協力運搬が成立する理由を説明できるか
- [ ] `MapTreasureSpawner`を残して旧`Treasure` classを削除できる依存方向を説明できるか
- [ ] class、通知、harness、設定、testのどれか一部だけを残すと、どんな誤読や死んだ調整面が生まれるか説明できるか
