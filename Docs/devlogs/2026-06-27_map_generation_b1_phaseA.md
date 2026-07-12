# B1 マップ生成 段階A: 純粋生成コアの実装

> **作成日:** 2026-06-27
> **ステータス:** 段階A 実装完了・ロジック検証済み（standalone）。Unity 上の .meta 生成・EditMode 実行は未実施（後述の環境事情）。
> **ブランチ:** `Sprict/map-generation`
> **正本設計:** `Docs/devlogs/2026-06-27_map_generation_decision.md`（§6 決定 / §9 ガード / §11 MVP順序）

---

## 1. 何をやったか

決定記録（同日）の §11 ステップ2 = **B1「手作りモジュールのシード連結」**の中核を実装した。
今回のスコープは **Unity / Fusion に依存しない純粋生成ロジック（段階A）** に限定し、
Unity 層（prefab・Instantiate）とネットワーク層（[Networked] 配布・LayoutReady ゲート）は段階B/C に分けた。

新規ファイル（`Assets/Code/Scripts/Map/`）:

| ファイル | 役割 |
|---|---|
| `MapPrimitives.cs` | `MapDirection`（N/E/S/W を時計回り順）・`GridRotation`（整数90°回転）・`MapSocket`/`WorldSocket` |
| `ModuleSpec.cs` | モジュールの論理定義（footprint＋ソケット＋ロール）・`ModuleCatalog` |
| `DeterministicRng.cs` | 生成専用 PRNG（xorshift64*）。`UnityEngine.Random` のグローバル状態に依存しない |
| `MapLayout.cs` | `PlacedModule`（整数原点＋回転ステップのみ）・占有セル表 |
| `MapConnectivity.cs` | F1 FloodFill 連結検証 |
| `MapManifest.cs` | E2 配布 payload の素体。FNV-1a checksum（カタログ Id 込み） |
| `MapGenerator.cs` | 一本道＋短い分岐・ソケット噛み合わせ・衝突回避・F4 リロール＋fallback |

テスト（`Assets/Code/Tests/EditMode/Map/`）: `GridRotationTests.cs` / `MapGeneratorTests.cs`。

## 2. なぜこのアプローチか

### 2.1 純粋ロジックとして分離（既存パターン踏襲）
`TreasureGrabRegistry`（質量分配を NetworkBehaviour から切り出した純粋クラス）と同じ思想で、
生成ロジックを MonoBehaviour/Fusion から完全に分離した。利点:
- **EditMode テストおよび Unity 非依存の standalone テストの両方で検証できる**（実際に後者で検証した）。
- ネットワーク・物理の不確定要素を持ち込まないので、生成の正しさを単体で確定できる。

### 2.2 整数離散配置で決定論を担保（§9 ガードの実体）
配置を「整数セル原点 ＋ 90°回転ステップ（0..3）」だけで表現し、**浮動小数を配置判定に一切使わない**。
ソケットの噛み合わせも整数演算のみ。これにより、同一カタログ・同一シードなら全プラットフォームで
ビット同一のレイアウトになる。E2（ホスト配布）採用下でも、checksum 照合や将来の E1 切替に効く土台。

代替案として `UnityEngine.Random` や `System.Random` を使う手もあったが:
- `UnityEngine.Random` はプロセス全体で共有される静的状態を持ち、フレーム中の他コードの抽選で揺れる。
- `System.Random` は実装がランタイム依存でプラットフォーム間のビット同一性を保証しない。
- xorshift64* は仕様が固定された公開アルゴリズムで、同一 seed → 同一系列を保証できる。

## 3. 仕組み（原理）

### 3.1 座標系と回転
- Unity 左手 Y-up、平面は XZ、グリッドは `Vector3Int`（MVP は y=0 平面）。
- 回転は Y 軸まわり 90°ステップ。1 ステップ = 上から見て時計回り（Unity の正回転）= `(x,z) → (z,-x)`、Y 不変。
- 方位 enum を **時計回り順**（N=+Z, E=+X, S=-Z, W=-X）に並べることで、
  「方位の回転 = `(dir + steps) mod 4`」「逆向き = `(dir + 2) mod 4`」と一発で書ける。

### 3.2 ソケット噛み合わせ（接続の核心）
モジュール A の口 `f`（外向き facing）に新モジュール B を付けるとき、B の entry ソケット `s` を:
- **向きが逆**: `worldFacing(s) == Opposite(f.Facing)`
- **隣接セル一致**: `worldCell(s) == f.Cell + vec(f.Facing)`（= f の 1 マス外側）

になるよう置く。entry を 1 つ選ぶと回転は一意に決まる:
```
rot    = Normalize((int)Opposite(f.Facing) - (int)s.Facing)
origin = (f.Cell + vec(f.Facing)) - RotateCell(s.LocalCell, rot)
```
この「方位の回転」と「セル位置の回転」が一致すること（`ToVector(Rotate(d,k)) == Rotate(ToVector(d),k)`）が
全体の前提であり、`GridRotationTests` で不変条件として固定した。これが崩れると回転モジュールが
隣接セルでつながらず連結が壊れる。

### 3.3 生成フロー
1. Start を原点・回転0 で配置。占有セルを記録。
2. Start の口を 1 つ主経路フロンティアに選ぶ。
3. 主経路: Body を重み付き抽選し、フロンティアに噛み合わせて `MainPathLength` 個連結。
   占有セルと衝突したら別 entry/別候補を試す（`MaxPlacementTries`）。使わなかった口は分岐の種にプール。
4. 末端に Goal を付ける。
5. プールから短い分岐を `BranchCount` 本生やし、末端を DeadEnd で塞ぐ（best-effort）。
6. **F1 FloodFill** で占有セルが 1 連結成分かを検証。
7. 失敗時は **F4**: seed をずらして最大 `MaxRerolls` 回リロール。全滅したら保証済み最小テンプレ
   （Start→Goal 直結、不可なら Start 単独）へ fallback し、ゲーム開始が止まらないことを保証。

### 3.4 マニフェスト（E2 の素体）
`MapManifest` は version＋seed＋(moduleIndex, origin, rotation) 列＋checksum を持つ。
checksum は FNV-1a で、**カタログのモジュール Id 文字列まで混ぜる**ため、index は同じでも中身が違う
カタログ（host/client のモジュール定義ズレ）を検出して参加拒否できる。段階C でこれを固定長
`[Networked]` 構造体へ写す。

## 4. 検証（と環境事情の正直な記録）

**worktree（`Sprict/map-generation`）で Unity を開けなかった。** 原因は **ParrelSync + git worktree の
UPM 非互換**で、`manifest.json` の Git URL パッケージ解決時に ParrelSync がオリジナルパスを `undefined`
として扱い、Unity Package Manager 全体が「No packages loaded」で初期化失敗する[※推測]。
マップ生成コードとは無関係の環境障害。

代替として、Map コードが Unity に依存しているのは `Vector3Int`（`+ - == GetHashCode/Equals`）だけである点を
利用し、それをスタブして **standalone C#（.NET 10）で EditMode テストと同等の検証**を実行した。結果:

```
[GridRotation] 回転×方位の整合・不変条件          OK
[MapGenerator]
  生成（Start/Goal 配置）                          OK
  決定論（同一シード→ビット同一レイアウト）        OK
  異シードで差異が出る                              OK
  50シード全て完全連結・モジュール重なりゼロ        OK   ← fallback 使用 0/50
  20シード Start→Goal 到達保証                      OK
  manifest 往復                                     OK
  checksum 破壊を検出                               OK
  配置改ざんを checksum 照合で検出                  OK
==== 19 passed, 0 failed ====
```

`fallback 0/50` = 通常生成だけで全シード成功し、F4 保険に頼っていない（ジェネレータが健全）。
`Vector3Int` の `+ - == GetHashCode` は標準的な値セマンティクスなので、この検証は EditMode と等価。

**残: Unity 上での .meta 生成・コンパイル確認・EditMode Test Runner 実行はメインチェックアウトで行う**
（worktree をやめてメインで本ブランチを checkout する方針に決定。worktree とメインは同じ `.git` を
共有するため、本コミットはメインからそのまま引き継げる）。

## 5. 自力再実装チェックリスト

1. なぜ配置を「整数セル原点＋90°回転ステップ」だけで表すのか（浮動小数を排して決定論＝ビット同一生成）を説明できる。
2. 方位 enum を時計回り順に並べる利点（回転＝+steps mod4、逆向き＝+2 mod4）を説明できる。
3. ソケット噛み合わせの式（rot と origin の導出）と、その前提となる「方位回転＝ベクトル回転」不変条件を説明できる。
4. 生成専用 PRNG を自前 xorshift にした理由（UnityEngine.Random のグローバル状態・System.Random の非決定性）を説明できる。
5. F1 FloodFill が「全モジュール 1 連結成分」をどう判定するか、占有セル隣接の意味を説明できる。
6. F4 リロール＋fallback がなぜ必要か（特定 seed の制約同時充足失敗で開始が止まるのを防ぐ）を説明できる。
7. manifest checksum にカタログ Id を混ぜる理由（index 一致でも中身違いのカタログを弾く）を説明できる。
8. 純粋ロジック分離により、Unity が開けない環境でもロジックを完全検証できた経緯を説明できる。

## 6. 次にやること（段階B / C）

- **段階B（Unity 層・メインで実施）**: `ModuleDefinition`(ScriptableObject) でソケットをオーサリング → `ModuleSpec` 変換、
  `MapBuilder`(MonoBehaviour) で `MapLayout` をローカル Instantiate（整数座標＋90°、非ネットワーク静的）。
  まずプレースホルダ primitive で組み上がる絵を確認。Editor プレビュー。
- **段階C（ネットワーク層）**: マニフェストを `[Networked]` 状態として配布（E2）、LayoutReady ゲート、
  prefab checksum 照合、起動時検証ダッシュボード（§9 ガード本体）。
- `Docs/TECHNICAL_DESIGN.md` の関連 TODO 反映（要確認）。
