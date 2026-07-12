# 開発ログ: 2026-06-21 - Fusion config を既定パスへ戻し stray 再発を根絶

## 1. 症状（何が起きていたか）

- Unity Editor を再起動して Play を1回実行すると、`Assets/Photon/` フォルダが**自動生成**される。
- その状態で再度 Play すると Fusion のエラーでプレイヤーが**一切スポーンしない**（ホストでも出ない）。
- Editor.log（Console ではない）に以下が出る:
  - `[Fusion] Type X has not been weaved. Has the assembly MyProject.Scripts been added to NetworkProjectConfig?`
  - `ArgumentOutOfRangeException: ...NetworkInputData has no attribute Fusion.NetworkInputWeavedAttribute`
- **再発性**: 過去にも stray を削除して直したが、Editor 再起動初回でまた湧く。削除は対症療法でしかなかった。

このバグは過去 commit 3970894 でも一度削除しているが再発しており、根本原因の特定が必要だった。背景メモ: `project-fusion-stray-networkprojectconfig`。

## 2. 調査プロセス（どうやって原因を特定したか）

### 最初の仮説
「stray config（`Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion`）が本物（`Assets/Level/Photon/...`）と衝突して weave を壊す」までは既知。
未解明だったのは **「なぜ既定パスに stray が湧くのか」「なぜ weaver が stray を読むのか」**。

### 調査手順
1. `find Assets -iname '*NetworkProjectConfig*'` で重複を確認。本物 `Assets/Level/Photon/...`（ラベル付き・`MyProject.Scripts` 入り）と stray `Assets/Photon/...`（ラベル無し・`MyProject.Scripts` 無し）の2つが両方 `Resources/NetworkProjectConfig` パスに存在。
2. 両 config の中身を比較 → stray の `AssembliesToWeave` に `MyProject.Scripts` が無いことを確認。
3. weaver がどこを config 正規パスとするかを Fusion ソースで追跡:
   - `Fusion.CodeGen.cs` の `ILWeaverSettings.DefaultConfigPath` が **`Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion` をハードコード**。
   - `ILWeaverBindings` のパス解決ロジック（`_configPath`）が **「デフォルトパスにファイルがあれば最優先で採用」**（`File.Exists(defaultPath)` で即 return）。
4. stray を「再生成」しているのは誰かを追跡:
   - `NetworkProjectConfigUtilities.GetGlobalConfigPath()` → `FusionGlobalScriptableObjectUtils.GetGlobalAssetPath<NetworkProjectConfigAsset>()`。
   - config は `FusionDefaultGlobal` **ラベル**で検索される（`FindDefaultAssetPath`、キャッシュ付き）。
   - ラベル検索が空振りすると `EnsureAssetExists` → `CreateDefaultAsset` が **`FusionGlobalScriptableObjectAttribute.DefaultPath`（= 既定パス）に config を新規生成**。
5. `.meta` を確認 → 本物だけ `labels: - FusionDefaultGlobal` が付き、stray には無い。
6. ソース全体で `"Assets/Photon/Fusion` のハードコードを grep → **3か所**（weaver / EditorAssetLibrary / runtime 既定）。

### 原因の絞り込み
「移設運用が Fusion のハードコード前提と恒常的に戦っている」ことが確定。stray は単なるゴミではなく、**フレームワークが既定パスに作り直そうとする正規の挙動の産物**だった。

## 3. 原因（なぜ起きていたか）

### 根本原因: Fusion が `Assets/Photon/Fusion` を3か所でハードコードしている

| # | 場所 | 役割 | 移設時の挙動 |
|---|------|------|-------------|
| ① | `ILWeaverSettings.DefaultConfigPath`（`Fusion.CodeGen.cs`） | weaver が読む config パス | 既定パスに stray があれば**最優先**で採用し、その `AssembliesToWeave` を使う |
| ② | `FusionGlobalScriptableObjectAttribute.DefaultPath`（runtime） | global asset の生成先 | ラベル検索が空振りすると**ここへ config を再生成** |
| ③ | `FusionEditorAssetLibrary._assetPath` | エディタ用アセット | 既定パス前提のため移設で参照ズレ |

### コードレベルの原因
```csharp
// Fusion.CodeGen.cs（weaver のパス解決、抜粋）
var defaultPath = ILWeaverSettings.DefaultConfigPath; // "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion"
if (!string.IsNullOrEmpty(defaultPath) && File.Exists(defaultPath)) {
    return (defaultPath, ConfigPathSource.User);   // ← 既定パスにファイルがあれば即採用
}
// （無い時だけ）editor が書く path cache → それも無ければ Assets 内 *.fusion を grep
```

```csharp
// Fusion.Unity.Editor.cs（global asset の ensure、抜粋）
var defaultAssetPath = FindDefaultAssetPath(type); // FusionDefaultGlobal ラベルで検索
if (!string.IsNullOrEmpty(defaultAssetPath)) return false; // 見つかれば何もしない
CreateDefaultAsset(type); // ← 見つからないと attribute.DefaultPath（既定パス）へ生成
```

### なぜこの構成が壊れるか
- config 一式を `Assets/Level/Photon` に移すと、weaver は①の `File.Exists(既定パス)` が false になり path-cache 経由で本物を読む。**ここまでは動く。**
- ところが②が、何らかのタイミングでラベル検索を空振りすると既定パスに**ラベル無し config を生成**する。
- 生成された瞬間、①の `File.Exists(既定パス)` が true になり、weaver はラベル無し stray（`MyProject.Scripts` を含まない）を最優先で読む。
- 結果 `MyProject.Scripts` が weave されず、`NetworkInputData`/各 `NetworkBehaviour` に weave 属性が付かない。
- ランタイムで `Fusion.NetworkTypesMeta.CreateFromLoadedAssemblies()` が属性を見つけられず `ArgumentOutOfRangeException` → `NetworkRunner.Initialize` 失敗 → 全同期・全スポーン死。

### 背景にある原理
- **weave（IL Post Processing）とは**: Fusion は `[Networked]` プロパティや `NetworkInputData` を、コンパイル後の DLL に対して IL を書き換えて同期コードを注入する。どのアセンブリを weave するかは `NetworkProjectConfig.AssembliesToWeave` で決まる。ここから外れたアセンブリは「ネットワーク型として未登録」になる。
- [※推測] stray が「再起動初回」に湧くのは、起動直後はラベル索引/`FindDefaultAssetPath` のキャッシュが揃う前に `EnsureAssetExists` が走り、ラベル検索が一瞬空振りして②が発火するため。一度生成されると `CreateDefaultAsset` は `File.Exists` で例外を投げ二重生成しないので、以後はそのまま居座って①を汚染し続ける。
- **キャッシュが症状を遅延させる**: weave 済み DLL は `Library/ScriptAssemblies` にキャッシュされる。stray があっても `MyProject.Scripts` を再コンパイルするまでは古い weave 済み DLL が使われ、顕在化しない。`Player/Network` 配下を編集してコンパイルした瞬間に weave なし DLL へ置き換わって壊れる——ので「触ってないのに急に壊れた」ように見える。

## 4. 解決策（何をどう変えたか）

### 修正内容
Fusion 一式（`Fusion` / `FusionAddons` / `FusionDemos` / `FusionMenu` / `PhotonLibs` / `PhotonRealtime`、計573ファイル）を**既定パス `Assets/Photon/` へ戻す**。

```bash
# stray 削除（未追跡）
rm -rf Assets/Photon Assets/Photon.meta
# 本体を既定位置へ（.meta 同伴で GUID・参照・git 履歴を保持）
git mv Assets/Level/Photon Assets/Photon
git mv Assets/Level/Photon.meta Assets/Photon.meta
# サブフォルダ自身の .meta（フォルダ GUID）も移動
git mv Assets/Level/Photon/<each>.meta Assets/Photon/<each>.meta
```

加えて、不要になった `.gitignore` の stray 抑制エントリを削除:
```diff
-# Fusion が再生成する stray NetworkProjectConfig（本物は Assets/Level/Photon 配下）
-Assets/Photon/
-Assets/Photon.meta
-.stray_photon_backup_*/
```

### なぜこれで根絶できるか
- 本物 config（`FusionDefaultGlobal` ラベル付き・`MyProject.Scripts` 入り）が**既定パスそのものに座る**。
- ①の weaver は既定パスを読む → それが本物。②が再生成しようとしても `File.Exists(既定パス)` が true なので生成しない。仮に生成経路が走っても**生成先＝本物自身**なので衝突対象が存在しない。
- ③の EditorAssetLibrary 等の既定パス前提も自然に満たされる。
- つまり「3つのハードコードと戦う」構図そのものが消える。

### 移動時の落とし穴（実作業で踏んだもの）
- **GUID 保持**: 必ず `.meta` 同伴で移動する。`git mv` はディレクトリ内ファイルは運ぶが、`Fusion/` の隣にある `Fusion.meta`（フォルダ自身の GUID）は別ファイルなので**個別に mv が必要**。漏らすと Unity がフォルダ GUID を振り直す。
- **DLL ロック**: VS Code の C# Dev Kit（`Microsoft.CodeAnalysis.LanguageServer`）が `Fusion/Assemblies/*.dll` と `PhotonLibs/*.dll` を掴んでおり、`git mv` が `Permission denied` で失敗。**Unity だけでなく言語サーバ（VS Code）も閉じる**必要があった。
- **gitignore 罠**: 旧回避策で `Assets/Photon/` を ignore していたため、戻し先が ignore されてしまう。先に ignore を外す。

## 5. 検討した代替案

| 代替案 | 評価 | 不採用の理由 |
|--------|------|-------------|
| A: 既定位置へ戻す ★採用 | ○ | ハードコード3か所すべてと一致し、stray の概念自体が消える。最も堅牢でフレームワーク準拠。代償は Assets ルートの見た目だけ |
| B: 現位置のまま weaver パスを override（`ILWeaverSettings.OverrideNetworkProjectConfigPath` 部分メソッドをユーザーファイルで実装） | △ | weaver（①）は直せるが、②の再生成と③、ビルド時 `Resources.Load` の曖昧さは残る。フレームワークと戦い続ける構造で再び別の形で壊れうる |
| C: stray を毎回削除（対症療法） | × | これまでやってきた事＝再発する。根本解決でない |

## 6. 教訓

### このバグのパターン
「**フレームワークのハードコード前提に逆らう配置**」が原因のクラス。プラグインを"整理"のために既定外へ移すと、フレームウークが既定パスを前提に動く箇所すべてと恒常衝突する。

### 同じパターンのバグに遭遇したときの対処手順
1. 「ホストでもスポーンしない/同期が全く効かない」→ まず **Editor.log** を `has not been weaved` で grep（Console には出ないことがある）。
2. `find Assets -iname NetworkProjectConfig.fusion` で重複確認。stray があれば本物との差分（`AssembliesToWeave`・ラベル）を見る。
3. **「なぜ湧くのか」をフレームワークのソースで追う**。今回の鍵は「既定パスのハードコード」と「ラベルベースの探索＋既定パスへの再生成」。対症療法で消える前に、生成元を特定する。

### 予防策
- サードパーティプラグインは**原則として既定インストール位置から動かさない**。"Assets を綺麗にしたい"動機で動かすと、ハードコード前提と衝突して高コストなデバッグを生む。
- 整理したいなら、まずそのプラグインが配置パスをハードコードしていないか（`grep "Assets/<Plugin>"`）を確認してから判断する。

### 関連する理論/概念
- [※理論] **ScriptableImporter / Resources / ラベル探索**: `.fusion` は ScriptedImporter で `NetworkProjectConfigAsset` にインポートされる。同名相対パスの `Resources/NetworkProjectConfig` が2つあると `Resources.Load` が曖昧になる。Fusion はエディタではラベル（`AssetDatabase` ラベル）で正本を識別するが、ビルドではラベルが無いため `Resources` パス解決に依存する——この二系統の差も移設運用の隠れリスク。公式: Photon Fusion 2 Manual（NetworkProjectConfig）で裏取り推奨。

## 7. 自力で再実装するためのチェックリスト

- [ ] スポーンしない時、Console ではなく **Editor.log** を `has not been weaved` で確認したか
- [ ] `NetworkProjectConfig.fusion` が複数存在していないか（`find`）
- [ ] 各 config の `AssembliesToWeave` に自分のアセンブリ（`MyProject.Scripts`）が入っているか
- [ ] config が Fusion **既定パス `Assets/Photon/Fusion/Resources/`** に1つだけあるか
- [ ] プラグインを移設している場合、移設先がハードコード前提と衝突していないか（`grep "Assets/Photon/Fusion"`）
- [ ] 検証は「コンパイル0/0」で満足せず、リフレクションで weave 属性（`NetworkInputWeavedAttribute` / `NetworkBehaviourWeavedAttribute`）と `NetworkTypesMeta.CreateFromLoadedAssemblies()` 無例外まで確認したか
- [ ] Play で実際に Host 起動しプレイヤー（`newAPRPlayer(Clone)`）がスポーンするか

## 検証結果（本修正で実施）

| 項目 | 結果 |
|------|------|
| 再インポート後コンパイル | 0 error / 0 warning |
| `NetworkInputData` の `NetworkInputWeavedAttribute` | True |
| `MyProject.Scripts` の NetworkBehaviour weave 網羅 | 7/7 |
| `NetworkTypesMeta.CreateFromLoadedAssemblies()` | 例外なし |
| Play(Host) スポーン | `newAPRPlayer(Clone)` 出現・Error ログ空・実機プレイ正常 |

---

**修正日**: 2026-06-21
**修正ファイル**:
- Fusion 一式: `Assets/Level/Photon/*` → `Assets/Photon/*`（573ファイルの移動）
- `.gitignore`（stray 抑制エントリ削除）
**修正コミット**: facb72c（branch `fix/fusion-config-default-location`、develop から分岐）
