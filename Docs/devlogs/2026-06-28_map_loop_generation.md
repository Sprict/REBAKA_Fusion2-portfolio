# 網目状ループ生成（ツリー → 廊下掘りで閉路を追加）

> **作成日:** 2026-06-28
> **ステータス:** 実装＋EditMode 検証完了（59/59 緑）。サンドボックス目視可。
> **関連:** `docs/devlogs/2026-06-27_map_generation_decision.md`、`2026-06-28_map_builder_unity_layer.md`
> **背景:** ユーザーから「全体的に一本道、ループや合流が少ない」というフィードバック（2026-06-28 ヒアリングで網目状を確定）。

---

## 1. 何を作ったか

従来の生成は「主経路＋行き止まり分岐」の**ツリー**で閉路ゼロ＝一本道だった。これに対し、
生成後に**離れた 2 つの開いた口を空きセル経由で廊下で繋ぎ、閉路（ループ）を能動的に追加**する
パスを足した。`MapGeneratorConfig.LoopConnections`（既定 4）で本数を制御する。

## 2. なぜこの設計か（原理）

### 2.1 ループ＝グラフの閉路。ツリーに「余分な辺」を足す
連結なツリーは閉路 0（辺数 = ノード数 − 1）。閉路を作るには、既に連結済みの 2 点を別経路で繋げばよい
（古典的な spanning-tree + extra-edges）。

### 2.2 なぜ「廊下掘り（空きセル BFS）」なのか
最初は「2 つ以上の口が向かい合う空きセルに十字路を 1 個置く」案を実装したが、**実測で 0/30**。
理由: 生成器は `ChooseNextFrontier` で未占有方向を優先して**外へ広がる**ため、口同士が同じ空きセルを
向く配置がほぼ生じない（空きターゲットセル 3〜6 個・最大 incoming = 1）。
そこで、離れた 2 つの開いた口の外側セル間を**空きセル上の BFS 最短路**で結び、その経路に
1 セル 4 方位の十字路（`ModuleRole.Connector`）を並べて廊下にする方式へ変更。両端は既に連結済みなので
廊下を通すと必ず閉路ができる。十字路は幾何的に隣接セルと自動連結する（`MapPathGraph` が跨ぎ辺を張る）。

### 2.3 決定論
- 開いた口の外側セルは重複排除＋座標順（x→z→y）。ペアは添字順で総当り。
- BFS は方位固定順（N,E,S,W）で最短路を返す。`maxCorridor = 8`（これ以上離れた口は繋がない＝長すぎる廊下を防ぐ）。
- これにより同一 seed・同一カタログでビット同一のループ配置になり、段階C の manifest 配布（host/client 一致）と両立する。

### 2.4 カタログ側の調整（フィードバック対応）
- スタート部屋を**多方向（北・東・西）**に（「進行方向が一方向」を解消）。
- 直線廊下の重みを 3→2、合流系（Tee）を 1→2 に（「通路が長い・多すぎ」を緩和）。
- ループ閉じ用の 1 セル十字路 `junction`（`Connector` 役割）を追加。通常成長の重み抽選には乗らず、
  `CloseLoops` だけが使う。

## 3. 触ったファイル

- `ModuleSpec.cs` — `ModuleRole.Connector` 追加。
- `MapGenerator.cs` — `MapGeneratorConfig.LoopConnections` 追加。`CloseLoops`（廊下掘り）＋
  `CollectOpenExits` / `FindEmptyPath`(BFS) / `Reconstruct` / `LessCell` を追加。
- `SandboxCatalog.cs` — `Junction`（Connector）追加、多方向スタート、重み再調整。
- `MapBuilder.cs` / `MapGenerationVisualizer.cs` — `_loopConnections`（既定 4）を公開し config に反映。
- `Tests/EditMode/Map/ModuleDefinitionTests.cs` — ループ生成テスト追加。

## 4. テストで保証したこと（EditMode 59/59 緑）

- `LoopConnections=0`（ツリー）はほぼ閉路 0。`LoopConnections=6` で閉路の**総数が大幅増**（seed 1..30 合計）。
- ループ版でも**常に連結**を維持。各 seed で `loopCycles >= treeCycles`。
- 実測（seed 1..12）: 多くの seed で 6 ループ／連結維持。3/12 は口が maxCorridor 圏外で 0 ループ（許容）。

## 5. 目視確認

- `Assets/Level/Scenes/MapGenerationSandbox.unity`（Gizmo）または `MapBuilderSandbox.unity`（床タイル）を開く。
  両コンポーネントの `Loop Connections = 4`。Seed を変えると網目状のループが見える。
- ネットワーク版 `MapNetworkSandbox` も `MapBuilder.LoopConnections=4` でループ込みのマップを配布する
  （配置は決定論なので host/client 一致は維持）。

## 6. 自力再実装チェックリスト

1. ループ＝閉路、ツリーに余分な辺を足す、を説明できる。
2. 「向かい合う空きセルに十字路 1 個」案が疎なツリーで失敗する理由（外へ広がるので口が揃わない）を説明できる。
3. 廊下掘り（開いた口の外側セル間を空きセル BFS で結ぶ）で確実に閉路を作る原理を説明できる。
4. 決定論を保つ要点（口の座標順・ペア添字順・BFS 方位固定順・maxCorridor 上限）を説明できる。
5. これが段階C の manifest 配布と両立する理由（離散・決定論なので host/client ビット一致）を説明できる。

## 7. 残課題・次の段階

- **ループ被覆率の向上**: 一部 seed は口が maxCorridor 圏外で 0 ループ。ツリーをやや密に生成する、
  または maxCorridor を状況に応じて伸ばすと被覆が上がる（通路長とのトレードオフ）。
- **十字路の見た目**: 現状プレースホルダ床。段階Bの実プレハブ化時に廊下/交差の見た目を与える。
- 目標像の残り（微起伏＝モジュール埋め込み / 階層＋崖）は別タスク。
