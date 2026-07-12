# Indoor Probe Placer: 屋内マップ向け Light/Reflection Probe 自動配置 Editor 拡張

**Date:** 2026-04-17
**Branch:** feature/indoor-probe-placer
**Scope:** 新規 Editor 拡張 (`Assets/Code/Editor/ProbePlacement/`)
**計画:** `C:\Users\raren\.claude\plans\fuzzy-bubbling-sky.md`

## 問題

屋内マップを新規作成するたびに Light Probe Group / Reflection Probe を手動配置するのが非効率。さらに **Nintendo Switch (Tegra X1 / Maxwell, 4GB RAM) 60fps 維持** が動作目標であり、数と解像度を意識した配置が必須。手作業では「とりあえず密に並べる」ことになりがちで、Switch ビルド時のメモリ/ベイク時間を圧迫する。

## なぜこのアプローチか

### 採用: 「選択 Root の Renderer AABB + 床 Collider 判定 → グリッド → 遮蔽/冗長除去」

- **シーン側にタグ/命名規約を要求しない** ので導入コストが最小。既存マップにも即適用できる。
- Bounds から XZ グリッド、各セルで下方向 Raycast → 床ヒット点が「屋内床セル」。`floorMask` と `wallMask` だけでフィルタ。
- 壁 Collider が 4-近傍間 Raycast を遮れば部屋 (連結成分) が分離される。ドア開口は床が繋がっている限り同室。Union-Find で O(N α(N))。

### 不採用の代替案

1. **タグ/レイヤーで Room / Floor / Wall を明示** → シーン側への命名規約強制が開発フリクションになる。却下。
2. **BoxCollider を部屋境界としてユーザが事前配置** → 小物 map ごとに追加作業が発生。却下。
3. **GI 勾配ベース配置 (Magic Light Probes 風)** → 一度ベイクしてから解析する 2 パス方式。品質は高いが Editor 実装コストが倍。今回はシンプルな 6-近傍冗長除去でスタートし、品質不足なら Phase 2 で移行する余地を残した。

### Switch 向け設計判断

| 項目 | 既定値 | 理由 |
|---|---|---|
| Reflection Probe `mode` | Baked 固定 | Realtime は Switch で致命的 |
| RP 解像度 | 64 | 64² HDR BC6H ≒ 48KB/個。部屋 10 でも <0.5MB |
| RP `hdr` | true | ハイライト階調優先。解像度で容量を削る |
| RP `blendDistance` | 0 | 同時サンプル RP を 1 個に抑え CPU/GPU 両削減 |
| RP `importance` | 部屋=1 / Specular=2 | 追加 RP を優先、中央 RP はフォールバック |
| Light Probe 冗長除去 | 内部均質点を checkerboard で 50% 間引き | シンプル・予測可能・最大ギャップ cellSize×√2 |
| Light Probe Group | 1 個に集約 | 複数 Group はベイク競合の元 |

### 冗長除去のヒューリスティック

- **壁際/角/開口近傍は維持** (`wallProximityRadius` 以内に `wallMask` のコライダー → `nearWall=true`)
- **6-近傍 (同一レイヤー 4 方向 + 上下レイヤー) 全員生存 かつ `nearWall=false`** → 内部均質点と判定
- **checkerboard**: `(gridIndex.x + gridIndex.y + layerIdx) & 1 == 1` の点を除去。最大ギャップ `cellSize × √2 ≒ 1.41 × cellSize` で、UI の `maxGapMultiplier = 1.8` 以内に確実におさまる。

この方式は (a) 単一パスで実装できる (b) 結果が決定的で再現可能 (c) 壁際の密度は維持されるため屋内 GI 品質の劣化が目立ちにくい、の 3 点が利点。

## 仕組み

```
IndoorVolumeDetector.Detect(settings)
  ├─ Renderer.bounds Encapsulate → worldAABB
  ├─ XZ grid (cellSize) ごとに下方向 Raycast(floorMask) → floorPoint
  ├─ 4-近傍間で wall-blocking Raycast(wallMask) → Union-Find
  └─ IndoorCell[] { roomId, floorPoint, gridIndex }

LightProbeGridBuilder.Build(detection, settings)
  ├─ 各セル × verticalLayers で候補生成
  ├─ CheckSphere(wallMask) で壁内・天井上を除外
  ├─ 壁近傍フラグ付与 (wallProximityRadius)
  └─ checkerboard 間引き (内部均質点のみ)

ReflectionProbePlacer.Build(detection, settings)
  ├─ 部屋ごとに centroid を計算、天井までの中間 Y に Box RP を配置 (importance=1)
  └─ 高 Smoothness/Metallic Renderer 近傍に追加 RP (importance=2, 部屋 RP から cellSize×2 以上離れる場合のみ)

ProbePlacementRunner
  ├─ Preview(s)  → PreviewResult (シーン変更なし、Gizmo 用統計)
  ├─ Apply(s)    → LightProbeGroup 1 個 + ReflectionProbe 群生成、Undo 1 ステップ
  └─ Clear(s)    → 名前 "[AutoProbe]" プレフィックスの GameObject のみ削除

ProbePlacementWindow  (menu: Tools > Lighting > Indoor Probe Placer)
  └─ IMGUI EditorWindow + SceneView Gizmo Preview
```

## 自力再実装チェックリスト

- [ ] `ProbePlacementSettings` に Root GO / LayerMask (floor, wall) / cellSize / verticalLayers / maxFloorHeightJump / wallProximityRadius / RP 設定 / `AutoProbePrefix` 定数を持たせる
- [ ] `IndoorVolumeDetector.Detect` は先頭で `Physics.SyncTransforms()` を呼ぶ (EditMode テストで必須だった)
- [ ] 下方向 Raycast は `max.y + 2` から `(max.y - min.y) + 4` の距離、`floorMask`, `QueryTriggerInteraction.Ignore`
- [ ] 壁遮蔽判定は隣接 floorPoint + `up * 0.3` 間の Raycast (地面スレスレでは床自身がヒットするため少し浮かす)
- [ ] Union-Find は path compression + union by rank の標準実装
- [ ] 冗長除去: 壁近傍は必ず維持、内部 6-近傍全員生存点のみ checkerboard 1 パス
- [ ] Reflection Probe は `boxProjection = true`, 部屋 AABB size + cellSize マージン, `importance` は部屋=1/Specular=2
- [ ] Apply は `Undo.SetCurrentGroupName` → 生成ごとに `RegisterCreatedObjectUndo` → `CollapseUndoOperations` で 1 ステップ Undo
- [ ] Clear は `FindObjectsByType<GameObject>(FindObjectsSortMode.None)` + `name.StartsWith("[AutoProbe]")` + `Undo.DestroyObjectImmediate`
- [ ] `LightProbeGroup.probePositions` は Group GameObject のローカル座標系。ワールド位置から `transform.position` を引く
- [ ] EditMode テストは `GameObject.CreatePrimitive(Cube)` + `transform.localScale` で床/壁を作る。`Physics.SyncTransforms()` を Detect 内で呼ぶことで常にキャッシュが同期

## 追加修正: 屋内/屋外混在シーンへの対応 (2026-04-17 PM)

### 問題

初版実装を Preview したところ、`Root` 配下に屋内と屋外が混在しているシーンで屋外 (広場、中庭、オープンエリアなど) の床上にも Light Probe が配置されてしまった。屋外でも屋内と同じ Floor Layer を使っているため、`floorMask` だけでは絞り切れない。

### 採用アプローチ: 「頭上 Collider 判定」

`floorPoint` の真上に向けて `Physics.RaycastAll` し、`[minCeilingHeight, maxCeilingHeight]` の範囲内に Collider があるかを判定する。Collider が存在すれば屋内、存在しなければ屋外として除外。

- **レイヤー**: `ceilingMask | wallMask | floorMask` の合成マスクを使う。天井専用レイヤーがなくても、壁・床レイヤーで天井役 Collider が拾える。
- **自己ヒット除外**: Raycast 原点を floorPoint + up*0.05 に設定した上で、`hits[i].collider == floorCollider` のヒットをスキップ。厚い床の側面/上面が再度ヒットしても取り除ける。
- **既定値**: `minCeilingHeight = 0.5m` (家具/瓦礫の下を除外)、`maxCeilingHeight = 8m` (通常の屋内は 2〜4m、吹き抜けで 8m まで許容)。
- **UI**: `Require Ceiling` トグルで機能 ON/OFF。OFF にすれば従来どおり `floorMask` のみで判定 (純粋な屋内マップ向け)。

### 不採用の代替案

1. **屋内判定用レイヤーを追加** → シーン側のコライダー全てに手作業でレイヤー設定が必要。プロジェクト全体のレイヤー予約も必要。導入コスト高。
2. **屋内 Volume を BoxCollider で事前定義** → マップごとに作業発生。初期計画で却下した「BoxCollider 境界定義」と同じ理由で却下。
3. **ambient occlusion / GI 勾配ベース判定** → 事前ベイクが必要で 2 パス構成になる。品質は高いが Editor 実装コストが倍。今回は単純な頭上 Collider 判定で十分と判断。

### テスト

新規 EditMode テスト 2 件追加:

- `OutdoorFloor_IsExcludedWhenRequireCeilingEnabled`: 屋内 (床+天井) と屋外 (床のみ) を同じシーンに配置し、屋内のみ 1 部屋として検出されることを確認。屋外座標 (x=30) のセルが含まれないことを assert。
- `OutdoorFloor_IsIncludedWhenRequireCeilingDisabled`: `requireCeiling = false` にすると天井なしでも検出されることを確認 (既存挙動保持)。

既存テスト (`SingleRoom`, `NoFloor`, `TwoRoomsSplitByWall`, `LightProbeGridBuilderTests`) には天井が無いので `requireCeiling = false` を明示的に設定して既定値変更の影響を吸収。

### 自力再実装の追加チェックリスト

- [ ] `ProbePlacementSettings` に `requireCeiling` (既定 true), `ceilingMask`, `minCeilingHeight`, `maxCeilingHeight` を追加 (Clone も忘れず)
- [ ] `HasCeilingAbove()`: 合成マスクで `Physics.RaycastAll` → `floorCollider` を除いた最短ヒット距離が `[minCeilingHeight, maxCeilingHeight]` に入るか判定
- [ ] 既存 EditMode テストは天井を持たないため `requireCeiling = false` を明示的に指定しないと既定値変更で落ちる

## 追加修正 2: 床レイヤー上の天井メッシュ対応 (2026-04-17 PM2)

### 問題

第一段の修正 (天井判定追加) を実機シーンで Preview したところ、`floorHits=306, ceilingRejected=306` で **全セル却下**。ユーザーヒアリングで「Floor レイヤー (本プロジェクトでは Grounded) を持つオブジェクトのうち 2 枚が天井として使われている。2 階のような構造」と判明。

### 原因

下向き Raycast が `Physics.Raycast` (first-hit only) で `floorMask` 検索していたため、**天井役のメッシュが『床』として拾われる**。Raycast は空から下ろすと最上面に当たるので:

- 天井の真下のセル: 天井を床と判定 → 天井の真上には何もない → 却下
- 天井の外のセル: 本物の床を拾う → このセルの真上には天井 (別位置) がない → 却下

結果、全セルで Ceiling Filter が false を返していた。

### 修正

下向きを `Physics.RaycastAll` に変更。全ヒットを取得し、**最下段を床、それより上のヒットを天井候補**として使う。

```csharp
var downHits = Physics.RaycastAll(origin, Vector3.down, castDist, floorMaskValue, QueryTriggerInteraction.Ignore);
if (downHits.Length == 0) continue;

int lowestIdx = 0;
for (int i = 1; i < downHits.Length; i++)
    if (downHits[i].point.y < downHits[lowestIdx].point.y) lowestIdx = i;
var floorHit = downHits[lowestIdx];

// Step 1: 同じ downHits 内で floor 上にほかの floor-layer 面があるか (床レイヤー上の天井)
// Step 2: ceiling/wall 専用レイヤーで HasCeilingAbove (通常の天井)
```

### なぜこの方法か

1. **既存 UI を変えずに解決**: 「Require Ceiling」トグル 1 つで両ケースに対応
2. **層構造への自然な対応**: 2 階建て以上にも拡張しやすい (ただし 2F 自体の床セル生成は未対応、将来 TODO)
3. **追加アロケーションは RaycastAll の結果配列のみ**: 1 セルあたり数ヒット、Editor のみ実行なので性能面で無視できる

### テスト

- `CeilingOnSameLayerAsFloor_IsDetectedAsIndoor` を追加: 床と天井を両方同じデフォルトレイヤー (floorMask=~0) に置き、`cell.floorPoint.y ≈ 0.05`（最下段の床）であることと、セル数 ≥ 9 を確認。

### 診断ログ (副産物)

ゼロ結果の原因を切り分けるため以下を Preview 時に出力するように:

- `Debug.Log` で `renderers / floorHits / ceilingRejected / cells / rooms / lightProbes / reflectionProbes`
- ゼロ結果時は原因別の Warning (Renderer なし / 床 Collider なし / 天井フィルタで全除外)
- Window の Stats 下に Diagnostics セクション + HelpBox ヒント

**学び**: Editor ツールは silent failure を禁じる。Preview で何も出ないときに「何がゼロなのか」が 1 画面で分かる作りが必須。

## 学び

- **Unity EditMode での Physics Query 初期化**: `Physics.Raycast` はシーンにコライダーがあっても、Transform を `localScale` で変形した直後は `Physics.SyncTransforms()` を明示的に呼ばないとキャッシュが古いままでミスする。テスト駆動開発中に 4/6 テストが空結果で失敗 → Detector の先頭で `Physics.SyncTransforms()` を呼ぶように修正して解決。Editor ツール一般で「シーン変更直後のクエリ」を書くときの定型手続きとして覚えておく。
- **屋内/屋外混在のフィルタ**: レイヤーに頼らず Collider の有無で判定する手法は「シーン側に規約を強制しない」という初期方針と整合する。天井専用レイヤーがなくても `wall | floor` で拾えるようにしておくと、既存プロジェクトにそのまま適用しやすい。
- **自己ヒット除外の罠**: `Physics.RaycastAll` で上向きに撃つと、厚い床 (厚 >0.05m) の場合に自分自身の上面を二重にヒットすることがある。`Collider` 参照の直接比較で除外するのが最も確実 (GameObject.GetInstanceID 比較より Collider 同一性のほうが厳密)。[※推測]
- **Switch 向け HDR Reflection Probe**: HDR を切ると反射ハイライトが潰れて「プラスチックっぽい」見た目になる。メモリを削るなら HDR を維持して解像度を下げるのが正解。64² HDR ≒ 32² LDR の容量だが見た目はほぼ HDR 優位。
- **LightProbeGroup の tetrahedralization**: probe 位置が密集すると縮退三角形が出て Bake 時にエラー/警告。checkerboard 間引きは単に Switch メモリ対策だけでなく、Unity の tetrahedralizer を安定させる副次効果がある。[※推測]

## 未確認 / 未着手

- **[※未確認] Switch 実機ビルドでの実 GPU 時間**: エディタ上の URP Forward 想定設定 (Shadow Cascade=1, AA Off, 1280×720) で 6ms 以内を目標にしているが、実機検証は別セッションで。
- **[※未確認] BC6H 圧縮の自動適用**: Cubemap Import 設定の Auto が Switch ビルド時に BC6H に落ちるか、RGBM 8bit になるかはプロジェクト設定依存。生成時は `hdr=true` を保証するのみで、Import 設定への介入はしない方針。
- **テストシーン (`Test_ProbePlacement.unity`) の作成は未着手**: EditMode ユニットテスト 7 件で動作保証済みなので、ユーザーが既存の屋内マップ (`Main.unity` 等) で即試せる状態。必要なら後日フィクスチャシーンを追加する。
- **GI 勾配ベースの高品質配置**: 今回のスコープ外。現行の checkerboard 間引きで品質不足と判明した場合のみ Phase 2 として検討。

## 関連ファイル

- `Assets/Code/Editor/ProbePlacement/ProbePlacementSettings.cs`
- `Assets/Code/Editor/ProbePlacement/IndoorVolumeDetector.cs`
- `Assets/Code/Editor/ProbePlacement/LightProbeGridBuilder.cs`
- `Assets/Code/Editor/ProbePlacement/ReflectionProbePlacer.cs`
- `Assets/Code/Editor/ProbePlacement/ProbePlacementRunner.cs`
- `Assets/Code/Editor/ProbePlacement/ProbePlacementWindow.cs`
- `Assets/Code/Editor/ProbePlacement/Tests/IndoorVolumeDetectorTests.cs`
- `Assets/Code/Editor/ProbePlacement/Tests/LightProbeGridBuilderTests.cs`
- asmdef: `MyProject.Tools.ProbePlacement.Editor` / `MyProject.Tools.ProbePlacement.Tests`
