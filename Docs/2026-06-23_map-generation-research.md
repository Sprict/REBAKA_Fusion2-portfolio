# マップ自動生成 技術調査（2026-06-23）

> **目的**: 「洞窟 vs 石造りダンジョン」の議論が「作れるか不明」のまま設計判断に進んでいたため、
> Unity + Photon Fusion 2 前提で自動生成方式の実装現実性を調査した。
> **重要**: このゲーム固有のハード制約（特にアクティブラグドール）で各方式を採点している。
> 一般論としての優劣ではない点に注意。

---

## 0. 最重要の発見：「洞窟 vs 石造り」は技術の選択ではない

マップ生成は**2つの層**に分かれる:

- **レイアウト層** = 部屋の繋がり方をどう決めるか（BSP / グラフ / WFC / Delaunay+MST / セルラーオートマトン）
- **ジオメトリ層** = 決まった空間を何で埋めるか（タイル/プレハブ / マーチングスクエア / マーチングキューブス）

「洞窟か石造りか」は主に**アートのスキン**であって、生成アルゴリズムとは独立に決められる。

### 証拠: Lethal Company

Lethal Company の「洞窟」は有機的な地形生成ではなく、`DunGen` という**タイルベースのモジュール生成**で作られている。
部屋・通路のプレハブを繋ぎ合わせる方式で、岩肌アートを貼って洞窟に見せているだけ。
天井高が常に十分なのは、人間が設計したタイルを繋いでいるから。

→ **「自然な洞窟（生成が難しい）」と「石造り（生成が楽）」の二択は誤り。** タイルベースで生成して見た目だけ洞窟にできる。

出典: [Steam Discussion: Algorithm used to generate the facility?](https://steamcommunity.com/app/1966720/discussions/0/4140564923177621836/)

---

## 1. 採点軸（このゲームのハード制約）

| 軸 | 中身 |
|---|---|
| ラグドール適性 | 床が平らで歩けるか（傾斜だらけだと物理破綻） |
| Fusion同期 | シード決定論で再現できるか |
| 配置制御 | トラップ・宝・ゴットレイ高天井を狙って置けるか |
| 迷子トポロジー | グリッドっぽくなく、ループがあり、道を覚える必要が出るか |
| 実装現実性 | 個人開発＋限られた期間で射程か |
| 洞窟の見た目 | 有機的な岩肌が出せるか |

---

## 2. Fusion 2 でのネット同期：シード方式（全方式共通の前提）

[※理論] プロシージャル生成のネット同期は、**ジオメトリを送らない**のが定石:

```
ホストがシード値（int 1個）を決める
  → [Networked]プロパティで全クライアントに同期
  → 各クライアントが同じシードで同じ生成器を回す
  → 全員同じマップがローカルに出来上がる（地形データ送信ゼロ）
```

Photon公式デモも「MasterClientが制御する決定論的ワールド生成器」という同じパターン。

出典: [Photon Procedural Demo](https://doc.photonengine.com/pun/current/demos-and-tutorials/package-demos/proceduraldemo)

### NavMesh

- タイルベース: 各タイルプレハブに**事前ベイク**して繋ぐのが最安。
- 生成メッシュ系: ランタイムベイク（`NavMeshSurface` + `com.unity.ai.navigation`、`BuildNavMesh()`）。チャンクごとに `NavMeshSurface` を持たせて再ビルド。事前ベイクより重い。

出典: [NavMeshComponents](https://github.com/Unity-Technologies/NavMeshComponents)

---

## 3. 各方式の評価

### レイアウト層

#### ◎ Delaunay三角分割 + 最小全域木（TinyKeep方式）— 本命1

部屋をランダムにばら撒く→物理シムで分離→中心点をDelaunay三角分割→最小全域木で全部屋到達可能を保証→辺の約15%を戻してループを作る。

| 軸 | 評価 |
|---|---|
| ラグドール適性 | ◎ 部屋は自作プレハブ＝床は平ら |
| Fusion同期 | ◎ シードだけで完全再現 |
| 配置制御 | ◎ 部屋プレハブにトラップ・宝・高天井を仕込める |
| 迷子トポロジー | ◎ グリッド感ゼロ＋ループあり |
| 実装現実性 | ○ 解説・OSS実装豊富 |
| 洞窟の見た目 | ○ 部屋メッシュ次第 |

タイルベースより「迷子の怖さ」が強い。作者の「方向感覚を失う洞窟が面白い」という直感に技術的に最も素直。

出典: [TinyKeep開発者解説](https://www.gamedev.net/forums/topic/642621-my-procedural-dungeon-generation-algorithm-explained-for-my-game-tinykeep/) / [2D実装例](https://github.com/AtlantiaKing/Procedural-2D-Dungeon-Generator)

#### ○ グラフ文法 / グラフ書き換え（Edgar・Dungeon Architect）

部屋の繋がりを設計者がグラフで指定して生成。レベルデザインの意図を込められる。

- **[※未確認] Edgar-Unity は 2D 専用**（検索結果が「2D dungeons and platformers」と明記）。3Dには直接使えない。
- **Dungeon Architect は 3D 対応だが商用（有償）**。

出典: [Edgar-Unity](https://github.com/OndrejNepozitek/Edgar-Unity)（OSS） / [Dungeon Architect](https://dungeonarchitect.dev/unity)（商用）

#### △ BSP（再帰矩形分割）

楽だが格子的で、迷子トポロジーと洞窟見た目が弱い。Delaunay+MSTの下位互換に近い。

#### △ Wave Function Collapse（WFC）

タイルの隣接ルールから制約伝播で生成（Caves of Qud採用）。強力でトレンドだが:
- [※推測] 連結性・プレイ可能性の保証が難しい（行き止まり・到達不能が出やすい）
- 正確な配置制御がしにくい
- 個人開発＋期間制約には複雑すぎる（オーバーキル）

→ 今回は非推奨。

出典: [Caves of Qud WFC (GDC)](https://gdcvault.com/play/1026263/Math-for-Game-Developers-Tile)

### ジオメトリ層

#### ◎ セルラーオートマトン + マーチング**スクエア**（平床×有機壁）— 本命2

重要な区別: **マーチング"キューブス"（3D・床凸凹）と、マーチング"スクエア"（2D輪郭を平床の上に壁として押し出す）は別物**。

セルラーオートマトンで2Dの有機洞窟輪郭を作り、平らな床の上に壁を押し出す。
[※推測] 床は完全に平ら（ラグドール安全）なのに、壁の形は有機的な洞窟になる。

| 軸 | 評価 |
|---|---|
| ラグドール適性 | ○ [※推測] 床は平面、壁だけ有機的 |
| Fusion同期 | ○ シード決定論可 |
| 配置制御 | △ 有機形状なので高天井を狙いにくい |
| 迷子トポロジー | ◎ ぐにゃぐにゃで方向感覚を失う |
| 実装現実性 | ○ OSS複数、Sebastian Lague解説あり |
| 洞窟の見た目 | ◎ 本物の有機洞窟 |

「本物の洞窟の見た目が譲れない」場合の唯一の現実解。マーチングキューブスの代わりにこれを使う。

出典: [AK-Saigyouji/Procedural-Cave-Generator](https://github.com/AK-Saigyouji/Procedural-Cave-Generator)（OSS）

#### ✗ マーチングキューブス（フル3Dボクセル）

床が凸凹→ラグドール破綻、コライダーベイク重い（処理落ちの主因）、同期難。**このゲームには不適。**

出典: [Fast-Unity-Marching-Cubes](https://github.com/Fobri/Fast-Unity-Marching-Cubes)（描画距離500ユニット超で非常に遅いと明記）

---

## 4. 総合判定マトリクス

| 方式 | ラグドール | 同期 | 配置制御 | 迷子 | 実装 | 洞窟見た目 | 総合 |
|---|---|---|---|---|---|---|---|
| タイルベース(LC方式) | ◎ | ◎ | ◎ | ○ | ○ | ○ | 堅実 |
| **Delaunay+MST** | ◎ | ◎ | ◎ | ◎ | ○ | ○ | **本命1** |
| グラフ文法(Edgar) | ◎ | ◎ | ◎ | ○ | △ | ○ | 2D制約 |
| BSP | ◎ | ◎ | ○ | △ | ◎ | △ | 凡庸 |
| WFC | ○ | △ | △ | ◎ | △ | ○ | 沼 |
| セルラー+ﾏｰﾁﾝｸﾞｽｸｴｱ | ○ | ○ | △ | ◎ | ○ | ◎ | **本命2** |
| マーチングキューブス | ✗ | △ | ✗ | ◎ | ✗ | ◎ | 不適 |

---

## 5. 推奨

1. **配置制御と迷子トポロジーを両取りしたいなら → Delaunay+MST（TinyKeep方式）**。部屋は自作プレハブ。タイルベースより迷子の怖さが強い。
2. **本物の洞窟の見た目が譲れないなら → セルラーオートマトン+マーチングスクエア**。天使ゾーンの配置制御が弱くなるトレードオフを飲む。
3. **組み合わせも可**: Delaunay+MSTで部屋の繋がり（迷子＋配置制御）、各部屋の壁をセルラーオートマトンで有機生成。両取りだが [※推測] 実装は重くなる。

### 次の一手（技術スパイク）

設計を続ける前に、最小生成器でこれを検証すれば地図問題は片付く:

> シードからレイアウトを作る最小生成器を作り、Fusion 2で2クライアント接続して
> **「両者が同一マップを見る」「NavMeshが繋がる」「ラグドールがその上を歩ける」**を確認する。

通れば見た目は後から乗せられる。詰まれば早期に分かる。

---

## ツール一覧（OSS優先）

| ツール | 種別 | ライセンス | 用途 |
|---|---|---|---|
| DunGen | タイルベース | 商用 | LCが採用 |
| dungen-unity | セルラー+部屋通路 | OSS | [fdefelici](https://github.com/fdefelici/dungen-unity) |
| Edgar-Unity | グラフ文法 | OSS（2D） | [OndrejNepozitek](https://github.com/OndrejNepozitek/Edgar-Unity) |
| Dungeon Architect | グラフ文法 | 商用 | 3D対応 |
| Procedural-Cave-Generator | セルラー+ﾏｰﾁﾝｸﾞｽｸｴｱ | OSS | [AK-Saigyouji](https://github.com/AK-Saigyouji/Procedural-Cave-Generator) |
| Procedural-2D-Dungeon-Generator | Delaunay+MST | OSS | [AtlantiaKing](https://github.com/AtlantiaKing/Procedural-2D-Dungeon-Generator) |

---

## 関連ファイル

- 設計判断まとめ: `Docs/2026-06-22_design-decisions.md`
- 発散セッション: `Docs/2026-06-21_concept-exploration-handoff.md`
