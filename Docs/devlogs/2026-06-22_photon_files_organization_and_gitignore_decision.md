# Photon関連ファイルの整理方針と .gitignore 判断（個人開発前提）

- 日付: 2026-06-22
- 種別: 設計判断（VCS / プロジェクト構成）
- 結論: **Photon一式は現状の `Assets/Photon` に置いたまま git 追跡を継続する。`.gitignore` 対象にはしない。**

## 1. 背景・問題

「外部SDK（Photon）を Package Manager 管理 + `.gitignore` + チーム共有ファイルだけ commit、という構成に整理したい」という発想から検討を開始した。

しかし調査の結果、この発想は **チーム開発かつ UPM 配布SDK** を前提にしたもので、本プロジェクトの実態（個人開発 + Photon Fusion 2）には一部が成立しないことが分かった。

あわせて、git index に異常な churn が見つかった：フォルダ用 `.meta` が「削除ステージ済みだがディスク上には実在」という矛盾状態（`git rm --cached` 系の操作痕）。Photon 関連で3件混在していた。

## 2. 調査で確定した事実

### 2-1. Photon Fusion 2 は UPM 配布が存在しない（裏取り済み）

- 公式 Getting Started に明記: 「The SDK is provided as a **.unitypackage file** and can be imported with `Assets > Import Package > Custom Package`」
- Asset Store 版もあるが、導入後の実体は同じ（`Assets/` 配下へ展開）
- ドキュメント全体に UPM / レジストリ配布への言及なし
- → **「Package Manager 管理」という前提が Fusion 2 では成立しない。** manifest.json でのバージョン自動復元ができない。
- 出典:
  - https://doc.photonengine.com/fusion/current/tutorials/host-mode-basics/1-getting-started
  - https://assetstore.unity.com/packages/tools/network/photon-fusion-267958

### 2-2. ファイルは既に公式推奨の配置にある

- `Assets/Photon`（`.unitypackage` のデフォルト展開先）に単一ツリーで存在。過去にあった stray な重複（`Assets/Level/Photon` 等）は現状なし。
- → **「公式が推奨する場所への整理」は実質完了済み。** 再配置の必要はない。
- 規模: 約16MB / git 追跡 570ファイル。

### 2-3. 設定ファイルが SDK ツリー内部に同居している（最重要）

プロジェクト固有で共有必須の設定が、SDK 本体と同じツリーの中にある：

- `Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion`（weave対象アセンブリ等。**欠けると全スポーンが死ぬ**）
- `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset`（App ID 等の接続設定）

→ `Assets/Photon` を丸ごと `.gitignore` すると、この致命的な2ファイルも巻き添えで除外される。回避するには `!` による除外の例外指定が必要だが、SDK 更新でパスが変わると壊れる脆い構成になる。

## 3. なぜ「追跡継続（gitignore しない）」を選んだか

`.gitignore` 化のメリットは本プロジェクトでほぼ消える一方、デメリットは実害として既に経験済みのため。

| 観点 | gitignore化のメリット | 本プロジェクトでの実際 |
|---|---|---|
| 共有リポジトリを軽量に保つ | チーム開発で有効 | **個人開発のため無効**（チームがいない） |
| UPM で版を自動復元 | UPM配布SDKで有効 | **Fusion 2は UPM 非対応のため無効** |
| リポジトリ容量削減 | 16MB削減 | git にとって16MBは些末。割に合わない |

逆に、gitignore 化で発生する具体的デメリット：

1. **再セットアップが手動 `.unitypackage` インポートになる。** 別マシン/新環境で「全く同じ版」を各自で探して入れ直す必要があり、バージョンずれの温床。個人開発でも複数マシン間で再現性を失う。
2. **設定ファイル（2-3）の除外例外が脆い。** SDK 更新でパスが変わると `NetworkProjectConfig.fusion` を取りこぼし、本リポジトリで過去に起きた「has not been weaved」「stray config で全スポーン死」級の事故を再発させうる。
3. **リポジトリがバックアップ/同期手段を兼ねる個人開発では、SDKを追跡から外すと丸ごとバックアップ対象外**になり復旧摩擦が増える。

→ 16MB の節約のために、本リポジトリが繰り返し被害を受けてきた Photon 設定の脆弱性リスクを引き受けるのは割に合わない。**追跡継続が最も安全。**

### 検討した代替案（不採用）

- **A: `Assets/Photon` 丸ごと gitignore** → 2-3 の設定ファイルを巻き添えにする。不採用。
- **B: SDK本体は ignore、設定ファイルだけ `!` で除外** → 例外指定が SDK 更新で壊れる脆さ。個人開発で得る利益が薄い。不採用。
- **C: デモ/サンプル（FusionDemos / FusionMenu）だけ ignore** → 容量寄与は小さく、管理複雑化に見合わない。将来どうしても容量を削りたくなった時の候補として保留。
- **D（採用）: 全追跡継続。** 設定ファイルとSDKが同居していても、すべて追跡下にあれば取りこぼし事故が原理的に起きない。

## 4. 実施した整理

1. 誤って削除ステージ（`git rm --cached` 痕）されていた Photon フォルダ meta を追跡へ復帰:
   - `git restore --staged Assets/Photon.meta Assets/Photon/Fusion.meta`
   - 両者ともディスク実在 & HEAD一致のため、削除ステージは誤りだった。
2. `Assets/Photon/Fusion/Runtime/Matchmaking.meta` の削除ステージは**そのまま維持**。
   - 対応フォルダ `Runtime/Matchmaking` 自体が SDK 更新で消滅しており、これは正当な削除。
3. `.gitignore` は **変更しない**（Photon を追跡し続けるため、追記不要）。

※ 非Photonの staged 削除（`Assets/Audio/Sound.meta` / `Assets/Code/Shaders.meta` / `Docs/tasks.meta` / `Assets/Level/UI.meta`）は本タスクのスコープ外として未着手。同種の churn の可能性があるため、別途確認推奨。

## 5. 自力再実装チェックリスト

外部SDKの「gitignore すべきか」を判断する手順：

- [ ] そのSDKは **UPM配布があるか**？ 無ければ「Package Manager管理」前提は捨てる（Fusion 2 は無し）。
- [ ] **個人開発か / チーム開発か**？ 個人なら「共有リポを軽くする」メリットは消える。
- [ ] **プロジェクト固有の必須設定がSDKツリー内部に同居していないか**？ 同居しているなら丸ごと ignore は危険（Fusion は `Fusion/Resources/` に同居）。
- [ ] ignore した場合の **再取得が自動か手動か**？ 手動 `.unitypackage` なら版ずれリスクを評価。
- [ ] 容量削減幅と、上記リスクが **割に合うか**？ 数十MB程度なら追跡継続が無難。
- [ ] index に `D`(削除ステージ) と `??`(未追跡) が同一パスで併存していたら、ディスク実在 & HEAD一致を確認 → 一致なら `git restore --staged` で追跡復帰。フォルダ自体が消えているなら削除は正当。
