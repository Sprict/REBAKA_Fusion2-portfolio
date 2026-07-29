# Unused Asset Finder

プロジェクト内の **どこからも参照されていないアセット** を検出し、ゴミ箱送り（復元可能）や CSV 出力ができる Unity エディタ拡張。
`Asset Hunter PRO` のようなツールの自作・軽量版。**Editor 専用 / 外部依存ゼロ** なので、フォルダごと他プロジェクトへコピーするだけで動く。

## 使い方

1. メニュー **Tools / Unused Asset Finder** を開く
2. **Scan** を押す
3. 一覧から削除したいものにチェック → **選択をゴミ箱へ（復元可）**
   - 行クリックで Project ウィンドウにハイライト（Ping）
   - **CSV エクスポート** で一覧を保存可能

## 判定アルゴリズム（到達可能性解析）

1. **起点(root)** を集める … 必ず使われる入口
   - シーン（全シーン or Build Settings 有効分。Settings で切替）
   - `Resources` / `StreamingAssets` / `Editor Default Resources` 配下
   - `ProjectSettings/*.asset` が参照する GUID（URP・InputSystem 等を救済）
2. 各 root から `AssetDatabase.GetDependencies(path, recursive:true)` で推移的依存を全部たどる
3. 全アセットのうち、到達できず・除外条件にも当たらないものを **未使用** として列挙

## 設定（Settings）

| 項目 | 既定 | 説明 |
|---|---|---|
| Resources 配下を使用中扱い | ON | 実行時 `Resources.Load` を依存解析で追えないため保護 |
| ProjectSettings の参照を起点に含める | ON | URP/Input 等の誤検出を防ぐ |
| 全シーンを起点扱い | ON | OFF にすると Build 未登録シーンも未使用候補に出す |
| スクリプト(.cs)を除外 | ON | コード参照は追えないため誤検出回避 |
| 除外パス / 除外拡張子 | 一部既定 | 部分一致で候補から除外。設定は EditorPrefs に永続化 |

## 既知の限界（削除前に必ず確認）

依存解析で追えない参照は **誤検出（未使用と誤判定）** の可能性がある:

- `Resources.Load` / **Addressables** / 文字列パス・リフレクションでの動的ロード
- コードからのみ参照されるスクリプト

→ 重要アセットは除外リストで保護し、削除は **ゴミ箱送り（復元可）** を使い、事前にコミット推奨。

## 他プロジェクトへの移植 / パッケージ化

- そのまま `Assets/UnusedAssetFinder/` をコピーすれば動く
- UPM パッケージにする場合はフォルダごと `Packages/com.rebaka.unused-asset-finder/` へ移動（`package.json` 同梱済み）
- `.unitypackage` 化する場合はフォルダを選択して Export

## ロードマップ（案）

- 種類別フィルタ（テクスチャ/マテリアル/オーディオ…）とサイズ集計
- 未使用スクリプト検出（別アルゴリズム: 全 .cs 型のコード参照走査）
- 依存ツリーのプレビュー（なぜ使用中/未使用かの根拠表示）
- 除外プリセットの ScriptableObject 化（チーム共有・リポジトリ管理）
