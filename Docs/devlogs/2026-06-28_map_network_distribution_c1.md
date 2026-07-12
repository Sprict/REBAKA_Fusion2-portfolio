# 段階C-1: マップの [Networked] manifest 配布（地形同期の隔離検証）

> **作成日:** 2026-06-28
> **ステータス:** 実装＋EditMode 検証完了（26/26 緑）。**2 ピア実機検証 済み（2026-06-28, ParrelSync）: host/client 両方で地形が自動生成・一致、host に「地形ビルド完了」ログ確認**。
> **関連:** `docs/devlogs/2026-06-27_map_generation_decision.md`（E2 決定）、`2026-06-28_map_builder_unity_layer.md`（段階B）
> **対象コミット:** 段階B（551b556 系）の続き。

---

## 1. 何を作ったか

ホストが確定したマップを **`[Networked]` 状態として配り、各ピアが地形をローカル生成する** 経路（E2）を実装した。
本番のネットワーク経路（SessionManager/PlayerSpawner）を巻き込まず、**隔離した検証シーン**で配布機構だけを先に実証する構成。

- `MapNetworkDistributor`（NetworkBehaviour）— manifest を `[Networked]` で保持・配布。
- `MapNetworkSandboxLauncher`（MonoBehaviour）— 検証専用の最小ランナー起動（Host/Join）。
- `MapBuilder.BuildFromManifest()` — 受信 manifest から地形を復元する全ピア共通入口（段階B の Instantiate を再利用）。
- 検証シーン `MapNetworkSandbox.unity`（Build Settings 登録済み）。

## 2. なぜこの設計か（原理）

### 2.1 なぜ「seed を配って各自再生成」ではなく「配置リストを配る」か（E2）
決定 devlog E2 の通り。生成器は決定論だが、**クライアントに生成アルゴリズムを再実行させると「生成器のバージョン/プラットフォーム差で結果がズレる」リスク**を恒久的に背負う。
代わりにホストが生成した**確定配置リスト（moduleIndex/origin/rotation）**を配れば、受信側は決定論再現の責任を持たず、ただ並べるだけでよい。地形メッシュは各ピアがローカル Instantiate（非ネットワーク）。

### 2.2 なぜ一発 RPC ではなく `[Networked]` 状態か
`[Networked]` 状態は**後から参加したクライアントにも state sync で確実に届く**（late-join 対応）。一発 RPC は送信時に居たピアにしか届かない。地形は「セッション中ずっと真であるべき状態」なので状態同期が正しい。

### 2.3 固定長 networked 配列（容量 128）
Fusion の `[Networked]` 配列は固定容量が要る。`NetworkArray<NetworkPlacement>`（`NetworkPlacement : INetworkStruct {Index,X,Y,Z,Rot}`）＋ `Count` で可変長を表現。
seed は `ulong` を 2 つの int（Low/High）に、checksum は `uint` を int ビットに詰めて配る（int 系のみ使い weave の型サポートを確実にするため）。

### 2.4 checksum でカタログ不一致を弾く
`MapManifest.TryRebuild` は受信側カタログで checksum を再計算し、一致しなければ復元を拒否する。
checksum はモジュール Id 文字列まで含むので、**ホスト/クライアントのカタログ定義ズレ**（＝同じ index が別モジュールを指す）を検出して参加拒否に使える（§9「prefab checksum 不一致なら参加拒否」の論理版）。

### 2.5 ビルド駆動を Render の checksum ガードで冪等化
全ピア共通で、`Render()` 内で `Count>0 && LayoutReady` かつ未ビルドなら 1 回だけ `BuildFromManifest`。
ホストは `Spawned()` で生成・公開した直後、クライアントは state sync 到達後に、同じ経路でビルドする（host/client 対称）。

### 2.6 なぜ本番 SessionManager を使わず最小ランチャーか
本番 `SessionManager` は検証シーンを `Main_Backup.unity` へ**強制切替**するため、隔離検証に使えない。
また過去に「シーン配置 NetworkObject が StartGameArgs.Scene 未指定で RegisterSceneObjects 不発」の障害があったため、
最小ランチャーでも**検証シーンを networked として渡す**（Build Settings 登録＋`Scene` 指定）。

## 3. 触ったファイル

- `MapNetworkDistributor.cs`（新規）— `[Networked]` manifest 配布 ＋ Render ビルド。
- `MapNetworkSandboxLauncher.cs`（新規）— 検証専用最小ランナー。
- `MapBuilder.cs`（改修）— `BuildFromManifest` / `Realize`（生成と Instantiate を分離）/ `GetOrResolveCatalog` / `Seed` / `CurrentConfig` を追加。`Build()` は維持。
- `MapNetworkSandbox.unity`（新規）＋ Build Settings 登録（`EditorBuildSettings.asset`）。
- `Tests/EditMode/Map/MapBuilderManifestTests.cs`（新規）— データ契約 2 本。

## 4. テストで保証したこと（EditMode 26/26 緑）

1. `BuildFromManifest` がホストのレイアウトを**ビット同一に再現**（index/origin/rotation 一致＋グラフ連結）＝全ピアの地形一致の論理保証。
2. checksum 不一致の manifest は**復元拒否**。

> Fusion トランスポート・シーン NObj 登録・2 ピアの実同期は EditMode では検証不能。下記の手動検証が必須。

## 5. 2 ピア実機検証（ParrelSync）— 済み（2026-06-28）

> **結果: 成功。** host/client 両方でマップが自動生成され、地形が一致。host Console に
> `[MapNetworkDistributor] 地形ビルド完了` ＋ `[Fusion] adding player [Player2]` を確認。
> クライアントがビルドできた事実が checksum 一致の証明（不一致なら BuildFromManifest が false）。
> なお初回は地形が出なかったが、原因はコードではなく **Unity Editor のフリーズ**（要 Editor 再起動）だった。
> Render が毎フレーム再試行する late-join 安全設計のため、同期が一度でも処理されれば必ずビルドされる。

### 検証手順（再現用）

1. `Assets/Level/Scenes/MapNetworkSandbox.unity` を開く。
2. メイン Editor で Play → 画面の **Host** を押す。床タイルでマップが出ることを確認。
3. ParrelSync クローンで同シーンを Play → **Join (Client)**。
4. **確認:** 両ピアの地形が完全一致（同じ配置・同じ色）。Console に両ピアの
   `[MapNetworkDistributor] 地形ビルド完了 modules=… checksum=…` が出て **checksum が一致**。
5. late-join 確認: ホストを先に立ててからクライアントを後入りさせ、後入りでも地形が出る。

> この検証が通るまで develop/main へは上げない（CRITICAL: 統合ブランチは常にプレイ可能）。

## 6. 自力再実装チェックリスト

1. なぜ seed 再生成でなく配置リスト配布か（生成器の非決定性リスクを受信側に負わせない）を説明できる。
2. なぜ一発 RPC でなく `[Networked]` 状態か（late-join）を説明できる。
3. 固定長 networked 配列＋Count で可変長を表す理由、seed/checksum を int に詰める理由を説明できる。
4. checksum がカタログ Id を含むことでカタログ不一致を弾ける仕組みを説明できる。
5. Render の checksum ガードで host/client 対称に 1 回だけビルドする冪等化を説明できる。
6. シーン配置 NObj 同期に Build Settings 登録＋StartGameArgs.Scene 指定が要る理由（RegisterSceneObjects）を説明できる。

## 7. 残課題・次の段階（C-2 以降）

- **C-2: 本番 PlayerSpawner へ LayoutReady ゲート**。配布機構が 2 ピアで実証できたら、`PlayerSpawner.OnPlayerJoined`
  のプレイヤー Spawn を `LayoutReady` まで保留（地形が無い状態での落下防止）。本番経路に触るので集中作業として分離。
- **スポーン位置を生成 Start 部屋へ**（現状は既存の固定/SpawnPointManager）。
- **容量 128 の妥当性**: 生成規模が増えたら容量見直し（超過は警告＋切り詰め）。
- 段階C 完了後に D1 デコレーション / 微起伏（モジュール埋め込み）/ 網目ループ / 階層へ。
