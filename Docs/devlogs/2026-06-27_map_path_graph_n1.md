# N1 埋め込みパスグラフの実装（モジュール連結からタダで得る敵ナビ骨格）

> **作成日:** 2026-06-27
> **ステータス:** 実装完了（EditMode テスト 19/19 緑）。§11 MVP 順序の「埋め込みグラフ」に該当。
> **関連:** `docs/devlogs/2026-06-27_map_generation_decision.md`（方式決定。本実装の §6/§10 が根拠）
> **対象コミット:** B1 純粋生成コア（段階A, e32787e）の続き。

---

## 1. 何を作ったか

モジュール連結で生成したマップ上を敵が徘徊・追跡するための **大域ナビ骨格（パスグラフ）** を、
NavMesh を使わず純ロジックで構築する `MapPathGraph` を追加した。

- `MapPathGraph.Build(MapLayout)` — レイアウト幾何からグラフを**再構築する純関数**。
- ノード = 各モジュールに手置きしたパスノード（ワールドへ展開）。
- 辺 = ①モジュール内エッジ（`ModuleSpec.InternalEdges`）＋②噛み合うソケット越しの跨ぎ辺。
- `TryFindPath` / `TryFindPathBetweenModules` — clearance フィルタ付き A*。
- 通行プロファイル `TraversalProfile`（戸口の通行幅）で**敵サイズ別にパスを弾ける**。

## 2. なぜこの設計か（原理）

### 2.1 なぜ NavMesh ではないか
決定 devlog §6 の通り、**敵 AI はホスト権威**で動く。経路探索に使うのはホストの構造だけで、
クライアントは敵 Transform を補間描画するのみ → クライアント側のナビ一致は不要。
よってナビ方式は「決定論／同期制約」から解放され、「AI 品質 × 制作コスト」だけで選べる。
NavMesh のランタイムベイク・NavMeshLink・事前ベイク連結という難所群（F6/F7/F8）を丸ごと回避できる。

### 2.2 なぜ「タダで」グラフが手に入るのか
モジュール連結生成では、ジェネレータが配置のため**モジュール隣接を既に解決済み**。
各モジュールの戸口セルにノードを手置きしておけば、噛み合ったソケット越しに
そのノード同士を辺で結ぶだけで連結グラフになる。**「レイアウトが連結 = パスグラフが連結」**。
決定論的・ベイク不要・設計者が通り道を制御でき・軽量。

### 2.3 なぜ生成器ではなく「幾何から再構築」する純関数か
既存 `MapConnectivity`（F1 FloodFill）と同じ疎結合方針。生成器に「接続を記録する」責務を増やさない。
グラフは `MapLayout`（配置済みモジュール列）と `ModuleCatalog` だけから決定論的に再構築できるので、
生成・連結検証・ナビが互いに独立してテストできる。

ソケット接続の判定は純幾何:
**socket S の隣セル（NeighborCell）に、向きが逆（`Opposite`）で同 channel の socket T があれば噛み合い**。
両者の戸口セルにあるノードを跨ぎ辺で結ぶ。各対は「小さいスロット側で 1 回だけ」処理して重複辺を防ぐ。

### 2.4 通行プロファイル（clearance）の意味
辺に「通行に必要な最小空き幅（セル）」を持たせ、A* は `minClearance` 未満の辺を通れない。
跨ぎ辺の clearance は両ソケットの**狭い方**（`Mathf.Min`）。これで
「大きい敵は狭い戸口を通れない → 別ルートを探すか詰む」を表現できる（決定 devlog §6 の「敵サイズ別通行プロファイル」）。
将来は段差高さ・水/溶岩などの属性を `TraversalProfile` に足す拡張余地を残した。

### 2.5 A* の決定論
コスト／ヒューリスティックはワールドセルのマンハッタン距離（admissible）。
open から f 最小を取り出す際の tie-break を **node id 昇順に固定**し、同じ入力なら同じ経路を返す。
（局所ステアリング N3 は実行時側の責務。N3 単独は凹地形で局所最小に詰むため、必ず本大域グラフと組む。§6）

## 3. 触ったファイル

- `MapPrimitives.cs` — `TraversalProfile` / `ModulePathEdge` 追加。`MapSocket`・`WorldSocket` に `Clearance`（既定 1、後方互換の任意引数）。
- `ModuleSpec.cs` — `PathNodes` / `InternalEdges` 追加（任意引数、既定空 → 既存呼び出しは無改変で通る）。
- `MapLayout.cs` — `WorldSockets` が socket の clearance を伝播。
- `MapPathGraph.cs`（新規）— グラフ本体。
- `Tests/EditMode/Map/MapPathGraphTests.cs`（新規）— 検証 5 本。

## 4. テストで保証したこと

1. 隣接 2 モジュール → ノード 2・辺 1・連結。
2. **生成レイアウト（seed 1..50）でグラフが常に連結、かつ Start→Goal の A* 経路が必ず存在**（N1 の核心不変条件）。
3. 同一レイアウト → 同一グラフ（ノード／辺／clearance がビット一致＝決定論）。
4. 多セルモジュールの内部辺が経路として辿れる。
5. 狭い戸口（clearance 1）が大型敵（minClearance 2）を弾き、小型敵（1）は通れる。

> テストは任意レイアウトを `MapManifest` 往復（`ComputeChecksum`→`TryRebuild`）で組む。
> `MapLayout.Add` が internal でテスト側から呼べないため、公開 API 経由で組み立てる方針。

## 5. 自力再実装チェックリスト

1. なぜ NavMesh を使わずに済むか（敵がホスト権威 → クライアントのナビ一致不要 → 決定論制約から解放）を説明できる。
2. 「モジュール隣接は生成時に解決済み → 戸口ノードを結ぶだけでグラフが連結」の原理を説明できる。
3. ソケット噛み合いの純幾何条件（NeighborCell 一致 ＋ Opposite 向き ＋ 同 channel）を書ける。
4. グラフを生成器ではなく幾何から再構築する利点（疎結合・独立テスト）を説明できる。
5. clearance による敵サイズフィルタと、跨ぎ辺が両戸口の狭い方を取る理由を説明できる。
6. A* の決定論を保つ tie-break（node id 昇順）の必要性を説明できる。
7. 大域グラフ（N1）と局所ステアリング（N3）を組む理由（N3 単独は局所最小で詰む）を説明できる。

## 6. 残課題・次の段階

- **実 Collider 検証（決定 devlog §6/§9）**: 現状の clearance はオーサリング値。Unity 実行時に実 Collider で通行可否を裏取りする検証層は段階 B（Unity 層）で追加。
- **段階 B（Unity 層）**: `ModuleDefinition`(ScriptableObject)＋`MapBuilder`(MonoBehaviour) でレイアウトをローカル Instantiate。パスノード／ソケットはオーサリングで配置。
- **段階 C（ネットワーク）**: `[Networked]` manifest 配布 ＋ LayoutReady ゲート（PlayerSpawner への波及あり。着手前にユーザー確認）。
- **AffordanceLink（動的縦移動）** と Y 跨ぎ接続（現状の連結は平面前提。Stairs はソケットベース隣接へ拡張）。
