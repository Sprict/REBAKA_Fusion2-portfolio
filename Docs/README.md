# Docs の読み方

> **最終更新:** 2026-07-22

このリポジトリには、現在の実装を説明する資料と、試行錯誤を残した当時の開発記録があります。すべてを順番に読む必要はありません。知りたい内容に応じて、次の入口を使ってください。

## まず見る

1. リポジトリ直下の [`README.md`](../README.md) — ゲーム概要、動画、現在できていること、技術ハイライト
2. [`ARCHITECTURE_OVERVIEW.md`](ARCHITECTURE_OVERVIEW.md) — 現在のネットワーク物理同期方式と主要クラス

## 本人の担当とAI利用を確認する

- [`MY_ROLE.md`](MY_ROLE.md) — 本人が行った判断・問題発見・実機検証と、AIに任せた実装の範囲
- [`AUTHOR_NOTE.md`](AUTHOR_NOTE.md) — 掴みジョイントの試行を本人が振り返り、説明できる点と未理解点を記した資料

`MY_ROLE.md` は透明性を示すための詳細資料です。ゲームや技術構成を先に把握したい場合は、READMEとアーキテクチャ概要を先に読むことをおすすめします。

## 現在の制約を確認する

- [`ARCHITECTURE_FAILURE_MODES.md`](ARCHITECTURE_FAILURE_MODES.md) — 解決済みの故障、現在も残る制約、未検証事項

## 設計判断と検証を深掘りする

- [`2026-07-11_host-disconnect-lobby-recovery.md`](devlogs/2026-07-11_host-disconnect-lobby-recovery.md) — ホスト切断時にクライアントだけロビーへ戻れなかった問題の切り分け
- [`2026-06-27_map_generation_decision.md`](devlogs/2026-06-27_map_generation_decision.md) — マップ生成方式を比較して選んだ時点の設計判断
- [`2026-06-28_map_loop_quality_codex.md`](devlogs/2026-06-28_map_loop_quality_codex.md) — マップ生成実装後のループ率改善と測定結果
- [`2026-07-20_legacy-treasure-carry-removal.md`](devlogs/2026-07-20_legacy-treasure-carry-removal.md) — 廃止済み機能をruntime・設定・asset・test単位で削除した記録

## 過去の開発記録

[`devlogs/`](devlogs/) には、当時の仮説、失敗した案、修正途中の状態も残しています。「現行方式」という表記は、そのdevlogを書いた時点を指す場合があります。現在の仕様を確認するときは、各ファイル冒頭の公開時注記と、`ARCHITECTURE_OVERVIEW.md`を優先してください。

本リポジトリは開発リポジトリから応募向けに抜粋した公開版です。
