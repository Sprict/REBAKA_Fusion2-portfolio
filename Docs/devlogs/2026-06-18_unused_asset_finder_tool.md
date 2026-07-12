# 未使用アセット検出エディタ拡張（Unused Asset Finder）

- 日付: 2026-06-18
- 種別: feat（エディタツール / tooling）
- 対象: `Assets/UnusedAssetFinder/`（Editor 専用・依存ゼロの自己完結パッケージ）

## 問題 / 背景

度重なる仕様変更で、参照されなくなったプレハブ・シーン・ScriptableObject・テクスチャ等が蓄積していた。
手作業（`.meta` の GUID を全シーン/プレハブ/.asset から grep し、推移的に到達可能性を追う）で洗い出したが、
今後も継続的に発生するため、`Asset Hunter PRO` のような検出ツールを自作して再利用可能にする方針にした。
最終的には他プロジェクトでも使えるようパッケージ化する。

## なぜこのアプローチか（採用 / 不採用）

### 採用: AssetDatabase 依存グラフの到達可能性解析

- **起点(root)** を定義し、`AssetDatabase.GetDependencies(path, recursive:true)` で推移的依存を辿り、
  到達できないアセットを「未使用」とする。
- `GetDependencies(recursive:true)` は推移閉包を一括で返すため、自前 BFS が不要でコードが単純。

### 不採用案

- **手書き GUID grep（今回の手動調査の自動化）**: `.meta` 解析と参照走査を自前実装する案。
  Unity 公式の依存解決（ネストプレハブ・バリアント・サブアセット等の細かい仕様）を再実装するのは
  バグの温床。`AssetDatabase` に任せる方が正確。
- **BuildReport ベース**: 実ビルドの成果物から使用アセットを取る案。最も正確だが毎回ビルドが必要で重い。
  日常的なクリーンアップ用途には不向きなので見送り。

## 仕組み（root の決め方が肝）

到達可能性解析の弱点は「依存グラフに現れない参照」を取りこぼすこと。これを root で補う:

1. **シーン** … 全シーン or Build Settings 有効分（設定で切替）
2. **Resources / StreamingAssets / Editor Default Resources 配下** … 実行時に名前/パスでロードされ得るため起点化
3. **ProjectSettings/\*.asset の参照 GUID** … URP パイプライン・InputSystem 設定など
   「コードにもシーンにも出ないがビルドに必須」のアセットを、ProjectSettings をテキスト走査して
   32 桁 GUID を抽出 → `AssetDatabase.GUIDToAssetPath` で解決して救済する。
   （手動調査時、URP/Input が grep で参照ゼロに見えた誤検出を、この仕組みで防ぐ）

除外:
- **スクリプト(.cs)** … MonoBehaviour としてプレハブ/シーンに載っていれば依存に出るが、
  コードからのみ参照される .cs は追えず誤検出するため既定で除外。
- 除外パス（部分一致）/ 除外拡張子はユーザー設定（EditorPrefs に永続化）。
- ツール自身のフォルダは常にハードコードで保護（自分を消さない）。

削除は `AssetDatabase.MoveAssetToTrash`（OS ゴミ箱送り＝復元可能）を採用。`DeleteAsset`（完全削除）は
取り返しがつかないため既定にしない。

## 既知の限界（重要）

依存グラフに出ない以下は **未使用と誤判定する可能性** がある。削除前に Unity の参照検索で要確認:

- `Resources.Load` / **Addressables** / 文字列・リフレクションでの動的ロード
- コード専用参照のスクリプト
- ライトマップ等、シーンの Lighting データ経由の間接参照（実測でも `Lightmap-*.exr` が候補化された。
  真にスタレなのか LightingData 経由で生きているのか個別確認が要る例）

## 構成

```
Assets/UnusedAssetFinder/
  Editor/
    UnusedAssetFinder.Editor.asmdef   # Editor 専用・references 空（依存ゼロ＝移植容易）
    UnusedAssetEntry.cs               # 結果1行のデータ
    UnusedAssetFinderSettings.cs      # 設定 + EditorPrefs 永続化
    UnusedAssetScanner.cs             # 到達可能性解析の本体
    UnusedAssetFinderWindow.cs        # Tools/Unused Asset Finder の UI
  package.json                        # UPM 化用メタ
  README.md
```

## 検証

- コンパイル: エラー0 / 警告0
- 動作: `uloop execute-dynamic-code` で `UnusedAssetScanner.Scan` を直接実行 → 203 件 / 28.54MB を検出。
  上位は APR Player サンプル・Photon 付属物で、手動調査の結論（APR/Examples 未使用・ベンダー余剰）と一致。

## 自力再実装チェックリスト

- [ ] `AssetDatabase.GetAllAssetPaths()` で全アセット、`IsValidFolder` でフォルダ除外
- [ ] root: 全/ビルドシーン、Resources・StreamingAssets・Editor Default Resources、ProjectSettings の GUID
- [ ] `GetDependencies(path, true)` を全 root に対して union → reachable
- [ ] all − reachable − 除外（.cs/拡張子/パス/自フォルダ）= 未使用
- [ ] 削除は `MoveAssetToTrash`、実行前に確認ダイアログ
- [ ] 設定は `EditorPrefs`（productGUID 付きキー）で永続化
- [ ] 限界（動的ロード/スクリプト/ライティング）を UI と README に明記
```
