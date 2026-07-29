# ループ生成の質向上（スコア選択＋密度バイアス＋通路実体化）— 三者協働

> **作成日:** 2026-06-28
> **公開時注記（2026-07-22）:** これはMapNetworkSandbox実装時の測定記録です。EditMode件数は当時の値であり、現在の全テスト件数を表しません。
> **ステータス:** 実装(Codex)＋独立検証(Claude)完了。EditMode 62/62 緑。zeroLoopRate 約25%→10%。
> **関連:** [`2026-06-27_map_generation_decision.md`](2026-06-27_map_generation_decision.md)
> **協働:** 方式判断=ユーザー＋Codex＋Claudeで合意 / 実装=Codex（GPT-5.x） / 検証・統合=Claude。

---

## 1. 何を作ったか / なぜ

前段の「廊下掘り」でループは出るようになったが、Codex の設計レビューで弱点が指摘された:
ループが**後処理の偶然**に依存し（被覆率~75%＝0ループseedが残る）、ペア選択が「座標順で最初に成功した組」で
ループの**質**（意味ある近道か）を評価していない、通路が全部十字路で見た目が単調。

三者で「現方式を土台に強化（案A）、グラフ先行(案B)への全面移行は階層実装時にまとめる」で合意し、
実装を Codex に委任、Claude が独立検証した。

## 2. 実装の中身（原理）

- **ペア選択のスコアリング化**: `CloseLoops` が候補ペアを全列挙し、**既存パスグラフ上の所属ノード間距離が
  大きいペアを優先**（＝遠回りを短絡する"意味のある"ループ）。同点は通路長昇順→出口セル座標で安定 tie-break。
- **密度バイアス**: `ChooseNextFrontier` を占有セル近傍スコアで選ぶよう変更。ツリーが広がりすぎて口が
  廊下長上限(8)圏外に出るのを抑え、ループ候補を圏内に保つ。
- **分岐 DeadEnd の延期**: ループ生成時は分岐末端の DeadEnd を CloseLoops 後まで延期し、未使用なら塞ぐ
  （`SealDeadEnds`）。蓋がループ機会を先に潰すのを防ぐ。zeroLoopRate 低下の主因。
- **通路モジュールの実体化（marching）**: 各廊下セルの「実際に接続する方位 mask」を計算し、1セルの
  `loop_straight`/`loop_corner`/`loop_tee`/`junction`（`ModuleRole.Connector`、`SandboxCatalog` に追加）から
  mask 最小一致のピースを回転付きで選ぶ。全部十字路 → 通路らしい見た目に。

## 3. 決定論・ネット同期の担保（Claude 検証）

- 毎反復でグラフ再構築 → 開いた口を安定ソート → 全ペアを決定論スコアで選択。Dictionary/HashSet の
  列挙順に結果を依存させていない（必ずソート/安定 tie-break）。
- 追加した通路ピースも通常カタログモジュールで、`PlacedModule(index,cell,rot)` で配置。
  `MapManifest.FromLayout/TryRebuild` 往復・checksum 不変。host/client 同一レイアウト（既存
  `MapBuilderManifestTests` のビット一致テストが緑）。
- 容量: 1マップのモジュール数 ≈ 基本(7-10)＋通路(平均~9) ≈ 17-20、`MapNetworkDistributor` の固定容量 128 内。

## 4. 計測（before/after, seed 1..50）

- zeroLoopRate: 約25%(前段 devlog の 3/12) → **10.0%(5/50)**
- addedCycles=78、avgCorridorLength=6.14、disconnected=0、同一seed checksum一致、junctionModules=0(straight/corner採用)。

## 5. 検証（Claude 独立実施）

- Unityコンパイル 0 error / 0 warning。
- Unity Test Runner（EditMode全件） **62/62成功**（既存59＋Codex新規3: ループ被覆・決定論・通路ピース）。
- コードレビュー: 決定論・manifest 安全・制約遵守（Map 配下のみ・SerializeField/属性温存）を確認。

## 6. 残課題・チューニング

- `DenseFrontierBiasPercent = 100`（密度バイアス常時）。決定論・テストはOKだが、マップが詰まり気味/単調に
  なり得る。プレイ感を見て下げる余地（変化と被覆のトレードオフ）。
- 残り 10% の 0ループseed: maxCorridor を状況依存で伸ばす等で更に低減可（通路長との相談）。
- 案B（LayoutIntent グラフ先行）への全面移行は階層実装と同時に行う方針（縦移動で連結再設計が不可避なため）。

## 7. 担当

- ユーザー: 方式選択と採用判断
- Codex: 実装
- Claude: 独立検証（テスト、決定論、manifest往復）と統合
