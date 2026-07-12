# 段階B: マップ生成の Unity 層（ModuleDefinition + MapBuilder でローカル Instantiate）

> **作成日:** 2026-06-28
> **ステータス:** 実装完了（EditMode テスト 24/24 緑。MapBuilder 実 Instantiate を Editor 実機で検証済み）。
> **関連:** `docs/devlogs/2026-06-27_map_generation_decision.md`（方式決定 §11 MVP 順序）、
> `docs/devlogs/2026-06-27_map_path_graph_n1.md`（N1 埋め込みパスグラフ）
> **対象コミット:** B1 純粋生成コア（段階A, e32787e）＋ N1 パスグラフ（9dc91f7）の続き。

---

## 1. 何を作ったか

純ロジックの生成コア（段階A/N1）と Unity を橋渡しする **Unity 層** を追加した。

- `ModuleDefinition`（ScriptableObject）— Inspector でモジュールをオーサリングする表現。
  footprint / socket / パスノード / 内部辺 ＋ 見た目 prefab を持ち、`ToSpec()` で生成コア用の
  純粋 `ModuleSpec` へ写す。
- `ModuleCatalogAsset`（ScriptableObject）— `ModuleDefinition` の順序付き集合。`TryBuildCatalog()` で
  `ModuleCatalog` を組み、index→prefab を解決する。
- `MapBuilder`（MonoBehaviour）— シード＋カタログから `MapLayout` を生成し、各モジュールの prefab を
  ワールド姿勢でローカル Instantiate する。`MapPathGraph` も同時に構築して公開する。
- `SandboxCatalog`（static）— コード定義カタログを Visualizer と MapBuilder で**共有**（重複排除）。

## 2. なぜこの設計か（原理）

### 2.1 なぜオーサリング層と生成コアを分けるか
生成コア（`ModuleSpec` / `MapGenerator` / `MapPathGraph`）は **Unity 非依存の純粋データ＋純関数**で、
EditMode で高速・決定論的にテストできる。Unity の ScriptableObject/MonoBehaviour 依存をここに混ぜると
テストが重くなり決定論検証も難しくなる。そこで Unity 側オーサリング（`ModuleDefinition`）を別レイヤにし、
`ToSpec()` で純粋データへ**写してから**コアに渡す。コアはオーサリング手段を一切知らない。

### 2.2 配置規約（prefab ↔ セルの対応）
離散配置（整数セル原点＋90°回転ステップ）をワールドへ写す規約を一点に固定した:

- prefab のローカル原点 = モジュールセル `(0,0,0)`、回転 0 の向き、1 セル = `cellSize` m。
- 配置 = **position: `originCell * cellSize`、rotation: Y 軸 `90°×rotationSteps`**。

この回転方向は生成コアの `GridRotation.RotateCell`（時計回り `(x,z)→(z,-x)`）と一致する。
検算: Unity の `Quaternion.Euler(0,90,0) * forward(+Z) = right(+X)`。コア側 1 ステップ回転でも
`North(+Z) → East(+X)`、セル `(0,0,1) → (1,0,0)`。**見た目の回転とセル回転がビット一致**するので、
host/client が同じ manifest から同じ姿勢を再現できる（段階 C の前提）。

### 2.3 prefab 未割当でもパイプライン全体が動く（プレースホルダ）
prefab を 1 つも作らなくても生成→配置→ナビの**全経路**を目視確認できるよう、prefab 未割当のモジュールには
役割色（Start=緑/Goal=赤/Body=灰/DeadEnd=黄）の床タイルをワールド footprint セルごとに生成する。
これは Gizmo 確認（`MapGenerationVisualizer`）と同じ見え方で、アート制作と生成ロジック開発を分離できる。
着色は `MaterialPropertyBlock` に `_BaseColor`（URP）と `_Color`（Built-in）の両方をセット
（MPB は存在しないプロパティを無視するため、レンダーパイプラインを問わずマテリアルを汚さず着色できる）。

### 2.4 カタログの穴あきを拒否する理由
`ModuleCatalogAsset` の並び順がそのまま `ModuleCatalog` の index になり、manifest の moduleIndex が
この順序に依存する（E2 配布の前提）。null 要素を詰めて飛ばすと index がずれ、配布側と受信側で
別モジュールを指してしまう。よって未割当があれば `TryBuildCatalog` は **false を返して拒否**し、
黙ってズレたカタログを作らない。

### 2.5 段階 C への接続点
`MapBuilder.Build()` は現状「ローカルでシードから生成 → Instantiate」。段階 C では入口だけを
「受信した `MapManifest` を `TryRebuild` → 同じ Instantiate 経路」に差し替える。Instantiate 部分
（配置規約・プレースホルダ）は段階 C でもそのまま再利用できるよう、生成と配置を分離してある。

## 3. 触ったファイル

- `ModuleDefinition.cs`（新規）— オーサリング SO ＋ `ToSpec()`。socket/edge は readonly struct を直接
  シリアライズできないため `SocketAuthoring` / `EdgeAuthoring` の serializable struct を別途定義。
- `ModuleCatalogAsset.cs`（新規）— 順序付きカタログ SO。`TryBuildCatalog` / `PrefabAt` / `RoleAt`。
- `MapBuilder.cs`（新規）— 生成＋ローカル Instantiate ＋プレースホルダ ＋グラフ公開。
- `SandboxCatalog.cs`（新規）— コード定義カタログを共有化。
- `MapGenerationVisualizer.cs`（改修）— 自前カタログ生成を削除し `SandboxCatalog.Build()` を使用（重複排除）。
- `Tests/EditMode/Map/ModuleDefinitionTests.cs`（新規）— 変換 5 本。
- `Assets/Level/Scenes/MapBuilderSandbox.unity`（新規）— 確認シーン（MapBuilder ＋カメラ＋光）。

## 4. テストで保証したこと（24/24 緑）

1. `ToSpec()` が footprint/socket/パスノード/内部辺/clearance/role/weight を正しく写す。
2. clearance 0 は 1 にクランプ、ID 空ならアセット名を使う。
3. `ModuleCatalogAsset` が**並び順 = index** を保持し、役割引きが正しい。
4. null 穴あきカタログを**拒否**する（index ズレ防止）。
5. サンドボックスカタログ＋生成経路（MapBuilder と同一）で seed 1..20 が常に連結グラフを生む。

加えて Editor 実機で `MapBuilder.Build()` を実行し、seed 12345 → 7 モジュール / 床タイル 35 枚を
Instantiate、パスグラフ 21 ノード連結・fallback なしを確認した。

## 5. 目視確認のしかた

1. `Assets/Level/Scenes/MapBuilderSandbox.unity` を開く。
2. **Play** すると `MapBuilder` が自動 Build し、床タイルで生成マップが現れる（buildOnStart）。
   または Edit モードで `MapBuilder` を右クリック → **Build**（確認後 **Clear** で破棄）。
3. Inspector の `Seed` を変えて再 Build すると別マップになる。`Main Path Length` 等で規模を調整。
4. 実アートを使う場合は `ModuleDefinition` を作って prefab を割り当て、`ModuleCatalogAsset` に並べ、
   それを `MapBuilder` の Catalog に割り当てる（未割当モジュールはプレースホルダにフォールバック）。

## 6. 自力再実装チェックリスト

1. なぜオーサリング層（SO）と生成コア（純粋データ）を分けるか（テスト容易性・決定論）を説明できる。
2. 配置規約（position = originCell×cellSize / rotation = Y 90°×steps）と、それがコアの離散回転と
   一致する検算を書ける。
3. プレースホルダで「アート無しでも全経路を確認できる」狙いと、MPB 両プロパティ着色の理由を説明できる。
4. カタログ並び順 = index = manifest 依存、ゆえに穴あき拒否、という連鎖を説明できる。
5. 段階 C で差し替わるのは Build の「入口（生成 vs manifest 再構築）」だけで、Instantiate は再利用、と言える。

## 7. 残課題・次の段階

- **実 Collider 検証（決定 devlog §6/§9）**: 現状 clearance はオーサリング値。実 prefab の Collider で
  通行幅を裏取りする検証層は、実アート prefab が揃ってから追加する（プレースホルダには未適用）。
- **段階 C（ネットワーク）**: `[Networked]` manifest 配布 ＋ LayoutReady ゲート。`MapBuilder.Build` の
  入口を manifest 再構築へ差し替える。PlayerSpawner への波及があるため**着手前にユーザー確認**。
- **D1 デコレーション** / **AffordanceLink（動的縦移動）** は MVP 後段。
