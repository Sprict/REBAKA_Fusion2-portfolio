# 公開版の検証記録

## 対象

- 公開コードの同期元: 開発リポジトリ `c9bcbe8`
- Preflight実装: `579b75d`〜`6902a6d`
- 手動確認日: 2026-08-21
- 手動確認者: ユーザー本人
- 手順説明・記録整理: Codex（GPT-5）

## PreflightのUnity Editor確認

| 確認項目 | 結果 |
|---|---|
| `Test_Playground`で`Run Production Integration` | 問題なし |
| `MapNetworkSandbox`で`Run Map Prototype` | 問題なし |
| 誤ったactive sceneの前提Failと後続停止 | 問題なし |
| additive sceneの結果混入防止 | 問題なし |
| Prefab Stageの結果混入防止 | 問題なし |
| scene / Prefab / Build Settings / dirty状態の前後一致 | 問題なし |

設計と確認内容の詳細は[`Preflightプロファイル分離devlog`](devlogs/2026-08-10_preflight-profile-separation.md)を参照してください。

## この記録が示さない範囲

- 2-client通信の正常性
- 30秒以上のPlay Mode安定性
- 3〜4人接続、不安定回線、長時間プレイの耐性
- ゲーム全体のプレイアビリティ
- Preflightコードの独立実装能力

この手動確認は、公開コードの存在やEditorツールの動作確認を、本人の独立実装能力と同一視しない前提で記録しています。能力評価は[`MY_ROLE.md`](MY_ROLE.md)を参照してください。
