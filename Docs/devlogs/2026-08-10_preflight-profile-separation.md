# PreflightのProduction / Mapプロファイル分離

## 公開スナップショット

- 対象コード: 開発リポジトリの安定境界 `c9bcbe8`
- 実装: `579b75d`〜`6902a6d`
- 公開側への反映: 2026-08-21、公開リポジトリの専用ブランチ上で実施

## 問題

従来の一括Preflightでは、本番統合用の問題とMap試作用の問題を同じFailとして扱っていました。Mapの未完成がdevelop統合を誤って止める一方、Mapの完成条件をWarningへ下げると配線欠落を見逃すため、目的ごとに結果を分けました。

## 採用した設計

同じEditor Windowに2つのprofileを置きます。

- `Production Integration`: `Test_Playground`を完全パスで対象にし、Failはdevelop統合を止める
- `Map Prototype`: `MapNetworkSandbox`を完全パスで対象にし、FailはMap作業の完成を止めるが、Production統合は止めない
- 前提のactive sceneが一致しない場合は、後続checkを実行しない
- object検索では`gameObject.scene.path`を使い、additive sceneやPrefab Stageのobjectを混ぜない

この構造にした理由は、判定の目的とFailの影響範囲を画面上で追えるようにし、scene名だけの判定による同名sceneの誤判定を避けるためです。一括checkのままMap FailをWarningへ下げる案と、Windowを2つに分ける案は、誤判定または判定基盤の重複を増やすため採用していません。

## 手動確認

2026-08-21、ユーザー本人がUnity Editorで次を確認し、すべて問題ありませんでした。

| 確認項目 | 結果 |
|---|---|
| `Test_Playground`でProduction Integrationを実行 | 問題なし |
| `MapNetworkSandbox`でMap Prototypeを実行 | 問題なし |
| 誤ったactive sceneで前提Failになり後続が止まる | 問題なし |
| additive loadした別sceneが結果へ混入しない | 問題なし |
| Prefab Stageのobjectが対象sceneへ混入しない | 問題なし |
| 実行前後のscene / Prefab / Build Settings / dirty状態 | 問題なし |

実装担当は公開記録だけでは特定できないため、実装の独立所有をこの記録から主張しません。手動操作と結果確認はユーザー本人、手順の説明と公開資料の整理はCodex（GPT-5）が担当しました。

## 検証の限界

この確認はPreflightのEditor UIとscene境界の確認です。2-client通信、30秒以上のPlay Mode、ゲーム全体のプレイアビリティ、またはPreflightコードをAIなしで独立実装できることは検証していません。既存の能力評価は[`MY_ROLE.md`](../MY_ROLE.md)に記載したまま変更していません。
