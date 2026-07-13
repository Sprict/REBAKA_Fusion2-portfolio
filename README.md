# REBAKA_Fusion2（仮）

<!-- TODO: Docs/SPEC.md 以外に正式タイトルの記載が見つからなかったため仮題のまま。正式名が決まったら差し替え -->

「フニャフニャのアクティブラグドール 2〜4 人で、罠と怪物だらけのランダム洞窟から、重い宝を抱えて命懸けで持ち帰る協力抽出ゲーム」

Unity + Photon Fusion 2 による協力オンラインゲームの個人開発プロジェクトです（開発期間: devlog記録ベースで 2025-05-28〜2026-07-12、継続中）。

ポートフォリオとして、**ゲームの完成度そのものより「なぜこう実装したか」を一貫して説明できること**を優先しています。詳細は [`Docs/TECHNICAL_DESIGN.md`](Docs/TECHNICAL_DESIGN.md) と [`Docs/SPEC.md`](Docs/SPEC.md) をご参照ください。

---

## 🎬 動画（Videos）

※ ゲームループ（勝敗・終了条件）は未実装のため、動画は「システム実演」です。ネットワーク同期・物理インタラクションの実装品質をご覧ください。

1. **[まず見る — プレイヤーアクション一覧](https://youtu.be/Dh4gYdEsR18)**: 移動・ジャンプ・しゃがみ・掴み・運搬などの操作を一通り実演
2. **[ネットワーク実機検証 — 応答遅延テスト](https://youtu.be/xFO1bmT5-0E)**: Windows（ホスト）と Mac（クライアント）の実機2台を1画面に収め、ゲームパッド入力→ホスト反映→クライアント反映の遅延を実撮影
3. **[切断耐性 — 同一PC2クライアントテスト（ノーカット）](https://youtu.be/_Gx9Be2RTYM)**: 参加→切断→再参加→ホスト終了時のクライアント安全復帰→ホスト不在時のJoin失敗処理までを編集なしで収録
4. **[自動生成・同期 — マップ生成の2クライアント同期](https://youtu.be/ftKzJMMD07A)**: シード値を変えた3例で、ホストが生成したマップがクライアントに同一再構築される様子
5. **開発過程 — 2020→2026 開発変遷**（編集作業中・後日追加）: 前身プロジェクトの物理発散から現在の安定同期までの記録映像

### 🕹 実行ファイル（プレイアブルビルド）

Windows / macOS のプレイアブルビルド（テストシーン）を **[Releases](https://github.com/Sprict/REBAKA_Fusion2-portfolio/releases/tag/demo-2026-07-13)** からダウンロードできます。2台（または同一PCで2重起動）で片方が「Host」、もう片方が「Join」を選ぶと、Photon Cloud 経由で同じセッションに接続します。

- 未署名ビルドのため起動時に警告が出ます（Windows: SmartScreen「詳細情報」→「実行」 / macOS: 右クリック→「開く」）。詳細はリリースノートを参照してください。

### 🧪 実機検証環境

| 役割 | マシン |
|---|---|
| ホスト | Windows 10 デスクトップ（Intel Core i7-4790K / 16GB RAM / GeForce RTX 4060 Ti） |
| クライアント | MacBook Pro 13-inch 2017（Intel Core i7 デュアルコア / Intel Iris Plus Graphics 650 / 16GB RAM / macOS Ventura 13.7.8） |

世代・OS・GPU 構成の異なる実機 2 台間でネットワーク同期を確認しています。

---

## ✨ 技術ハイライト

### アクティブラグドール物理（kinematic + PID 制御）
13 パーツの ConfigurableJoint ラグドールを PID 制御（P=300, D=100）で直立バランスさせる自作実装です。Fusion 2.1 の Forecast Physics（外挿）との A/B テスト（2026-03-28）でクライアント同期品質が実機で劣ると判断し、kinematic 純補間プロキシ方式を採用しました。
関連: [`Assets/Code/Scripts/Player/RagDollPhysics.cs`](Assets/Code/Scripts/Player/RagDollPhysics.cs) / [devlog: PID導入](Docs/devlogs/2026-01-25_pid_balance_control.md) / [devlog: Forecast A/Bテスト](Docs/devlogs/2026-03-24_forecast_physics_ab_test_setup.md)

### ネットワーク物理同期（ホスト権威 + スナップショット補間）
State Authority側のみで物理を実行し（`HasStateAuthority`ガード）、プロキシ側はローカル物理を止めてホストのスナップショットを補間表示するだけの「純補間プロキシ」に統一しています。プレイヤーだけでなく Obs_Cube 等のピアオブジェクトにも同じ原理を横展開しました。
関連: [`Assets/Code/Scripts/Network/GameNetworkRigidbody.cs`](Assets/Code/Scripts/Network/GameNetworkRigidbody.cs) / [devlog: クライアントスナップショット補間](Docs/devlogs/2026-06-10_client_snapshot_interpolation.md) / [devlog: ピア同期の純補間統一](Docs/devlogs/2026-06-18_peer_sync_pure_interpolation.md)

### 手続きマップ生成（手作りモジュール連結 × ホスト配布 × 自前ナビグラフ）
完全 PCG は不採用としました。手作り部屋テンプレートをホスト側で抽選・接続し、`[Networked]` なマニフェストとしてクライアントへ配布、NavMesh に頼らない自前のパス探索グラフを持たせる方式に決定しました（完全 PCG はネットワーク同期という技術的挑戦の軸と分離した別問題になり scope が爆発するため不採用）。
関連: [`Assets/Code/Scripts/Map/MapGenerator.cs`](Assets/Code/Scripts/Map/MapGenerator.cs) / [`Assets/Code/Scripts/Map/MapNetworkDistributor.cs`](Assets/Code/Scripts/Map/MapNetworkDistributor.cs) / [devlog: マップ生成方式決定](Docs/devlogs/2026-06-27_map_generation_decision.md)

### 自作開発ツール
Network Debug HUD（実機での同期状態のリアルタイム可視化）、Preflight checker（統合前のネットワーク配線チェック）、UnusedAssetFinder（未使用アセット検出 Editor 拡張）、SyncMetricsRecorder（同期負荷の計測）などを内製しています。
関連: [`Assets/Code/Scripts/Debugging/NetworkDebugHud.cs`](Assets/Code/Scripts/Debugging/NetworkDebugHud.cs) / [`Assets/UnusedAssetFinder/`](Assets/UnusedAssetFinder/) / [devlog: Network Debug HUD](Docs/devlogs/2026-07-02_network-debug-hud.md) / [devlog: Preflight checker](Docs/devlogs/2026-07-02_preflight-checker.md)

---

## 📖 このリポジトリの読み方

**これは抜粋リポジトリです。** サードパーティ SDK・アセット（Photon Fusion 2 SDK、Asset Store 由来アセット）は再配布ライセンスの制約により含めていません。そのため Unity プロジェクトとしてはビルドできません。ゲーム本体は上記の動画を参照してください。

### コード地図（`Assets/Code/`）

| ディレクトリ | 内容 |
|---|---|
| `Scripts/Camera/` | 追従・軌道カメラ制御 |
| `Scripts/Debugging/` | 実機同期状態を可視化する Network Debug HUD |
| `Scripts/Diagnostics/` | 同期負荷計測（SyncMetricsRecorder）・ラグドールのCSVプロファイラ・ネット診断 |
| `Scripts/Map/` | 手作りモジュール連結によるマップ生成・接続トポロジ・ホスト配布・自前ナビグラフ・宝物スポーン計画 |
| `Scripts/Network/` | 入力収集、プレイヤー/スポーン管理、Host/JoinロビーUI・ホスト切断復帰、ピア同期物理の純補間サブクラス |
| `Scripts/Player/` | アクティブラグドール本体（物理・入力・状態・クライアントプロキシ戦略・ポーズ同期など20+ファイルに責務分離） |
| `Scripts/Settings/` | 設定メニュー（感度・リバインド・入力デバイス切替）と設定の永続化 |
| `Scripts/Treasure/` | 宝物オブジェクトの掴み・運搬レジストリ |
| `Scripts/Utils/` | ジョイント設定・デバッグ表示等の共通ユーティリティ |
| `Editor/` | ProbePlacement（ライトプローブ自動配置）や統合前チェック（Fusion設定重複・シーン配線）の Editor 拡張とテスト |
| `Tests/EditMode/` | NUnit EditMode テスト（ラグドール・マップ生成・宝物レジストリ等の純粋ロジック） |

`Assets/Editor/` と `Assets/UnusedAssetFinder/` は上記とは別の、プロジェクト全体向け Editor 拡張です（未使用アセット検出ツールなど）。

### おすすめ devlog（技術的に濃いもの3本）

1. [`Docs/devlogs/2026-06-18_peer_sync_pure_interpolation.md`](Docs/devlogs/2026-06-18_peer_sync_pure_interpolation.md) — ホスト権威 + kinematic 純補間の原理をプレイヤー以外にも一般化した設計判断
2. [`Docs/devlogs/2026-06-27_map_generation_decision.md`](Docs/devlogs/2026-06-27_map_generation_decision.md) — 完全PCGを不採用にし手作りモジュール×ホスト配布マニフェスト×自前ナビグラフを選んだ経緯
3. [`Docs/devlogs/2026-02-18_client_spawn_ko_gang_beasts_fix.md`](Docs/devlogs/2026-02-18_client_spawn_ko_gang_beasts_fix.md) — `HasStateAuthority` と `HasInputAuthority` の混同バグの根本原因調査（ポストモーテム系）

`Docs/devlogs/` 配下には上記以外にも開発の全記録（問題→原因→判断→仕組み）が時系列で残っています。

---

## 🛠 技術スタック

- **Unity**: 6000.3.7f1
- **Photon Fusion**: 2.1.0（Host Mode）
- **言語**: C#
- **テスト**: NUnit EditMode（`Assets/Code/Tests/EditMode/`）

---

## License / 利用について

本リポジトリは採用選考・技術レビュー目的の閲覧用です。コード・ドキュメントの著作権は作者に帰属します（All rights reserved）。Photon Fusion 2 は Photon Engine（Exit Games）の製品です。
