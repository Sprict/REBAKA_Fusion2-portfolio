# 2026-07-11: ホスト切断・Leave Session 後のタイトル（Host/Join）復帰

> **公開時注記（2026-07-22）:** 本資料のロビー復帰方式は現行コードに残っています。Host Migrationは未実装で、ホスト終了後に同じ試合を継続する機能ではありません。

## 問題

Client Host モードでセッション中にホストが Leave Session すると、クライアント側で NetworkObject が一斉に消えるが、**Host/Join のタイトル画面に戻れない**ことがあった。ホスト側も、修正の途中段階では同様に戻れないケースがあった。

完了条件は次のとおり。

- ホスト/クライアントとも Leave Session またはホスト切断後、`Test_Playground` の Host/Join UI に戻れる
- 予期しない切断時は理由を表示し、再 Host/Join できる
- 本番シーンは未作成。**当面は `Test_Playground` のみ**をロビー兼プレイシーンとして使う

## 症状（テストログ）

### 修正前（第2段階バグが残っていた時点）

**ホスト**（Leave Session）:

```
[SessionManager] Leaving session by user request.
[SessionManager] Shutdown: Ok
[SessionManager] Returning to lobby scene: Assets/Level/Scenes/Test_Playground.unity
[PlayerSpawner] Shutdown (Ok). All player objects cleaned up.
```

→ NetworkObject は一瞬消えるが、**タイトルに戻れた**。

**クライアント**（ホスト切断に追従）:

```
[SessionManager] Shutdown: DisconnectedByPluginLogic
[SessionManager] Returning to lobby scene: Assets/Level/Scenes/Test_Playground.unity
[PlayerSpawner] Shutdown (DisconnectedByPluginLogic). All player objects cleaned up.
```

→ NetworkObject は消えるが、**タイトルに戻れない**。`OnDisconnectedFromServer` のログは出ないこともあった（`OnShutdown` 側のメッセージ補完でカバー）。

### 修正後（`LobbySceneReloader` 導入後）

ホスト/クライアントとも:

```
[SessionManager] Returning to lobby scene: ...
[LobbySceneReloader] Loading lobby scene: ...
```

→ **両方タイトルに復帰**。再 Host/Join も可能。

## 調査

### 観測: Shutdown の入口が Host/Client で異なる

Console の stack trace より（要約1行）:

> **Host** = `OnGUI → LeaveSession → Shutdown` / **Client** = `NetworkRunner.Update → CloudServices.OnDisconnected → Shutdown`

これは devlog 上の推測ではなく、当時の Unity Console に出ていた call stack の要約である。

### 原因は2段階だった

| 段階 | 症状 | 原因 | 対策 |
|------|------|------|------|
| 1 | Host/Join UI 自体が消える | Fusion `NetworkSceneManagerDefault` が Shutdown 時にネットワーク登録済みシーン（`Test_Playground`）を unload し、`SessionManager` ごと破棄 | `LobbyNetworkSceneManager` で unload を no-op |
| 2 | クライアントだけ `Returning to lobby` ログは出るが画面が戻らない | `ReturnToLobby()` 内の `LoadScene` を `SessionManager` 上の coroutine（`yield return null`）に任せていた。Client は `NetworkRunner.Update` 内の同期 Shutdown 経路のため、`OnShutdown` **後も**同一 `Shutdown()` 内で Fusion の teardown が続き、coroutine 再開前に `SessionManager` 側の予約がキャンセルされた | `LobbySceneReloader`（DontDestroyOnLoad）に 1 フレーム遅延 reload を委譲 |

第2段階のポイント:

- `ReturnToLobby()` の**同期部分**（ログ出力）は Host/Client とも成功していた
- 失敗していたのは **1 フレーム後の `SceneManager.LoadScene`**
- ホストはフレーム後半の `OnGUI` から Shutdown するため、たまたま `SessionManager` 上の coroutine が生き延びやすかった
- クライアントは Update 序盤で Shutdown し、`OnShutdown` 後も同じ `Shutdown()` 呼び出しの中で片付けが続く

```
同一フレーム内の時間軸 →

Client:
  Update: [Shutdown → OnShutdown → coroutine予約 → teardown 続行 …]
  フレーム末尾: SessionManager 側 coroutine は既にキャンセル → LoadScene 来ない

Host (Leave Session):
  Update: [通常動作]
  OnGUI:  [Shutdown → OnShutdown → coroutine予約]
  フレーム末尾: SessionManager まだ生存 → LoadScene 成功
```

## 何をやったか

### 1. `SessionManager` — ロビー復帰フロー

- `RequiredNetworkScenePath` / `Main_Backup` 強制切替を廃止し、`lobbyScenePath`（`Test_Playground.unity`）に統一
- `LeaveSession()` 追加（Host/Client 共通、`await _runner.Shutdown()`）
- `OnGUI`: セッション中は Leave Session、非セッション時は Host/Join + 切断メッセージ
- `s_lobbyMessage`（static）で切断通知を保持。`StartSession` 開始時にクリア
- `_isLeavingIntentionally`: 自発離脱時は切断メッセージを出さない
- `OnDisconnectedFromServer` / `OnShutdown`: 予期しない切断時にメッセージ設定
- `OnShutdown` → `ReturnToLobby()`: 物理・カーソル復元 + ロビーシーン再読み込み
- `LobbyNetworkSceneManager` を `StartGameArgs.SceneManager` に使用
- `RegisterRunnerCallbacks()` / `CleanupSessionComponents()` でセッション開始時の Runner 周辺を整理

### 2. `LobbyNetworkSceneManager`（新規）

Shutdown 時のシーン unload を抑止。`SessionManager` が Shutdown コールバック中に消えないようにする。

```csharp
protected override IEnumerator UnloadSceneCoroutine(SceneRef sceneRef)
{
    Debug.Log($"[LobbyNetworkSceneManager] Skipping unload of {sceneRef} to preserve lobby shell.");
    yield break;
}
```

### 3. `LobbySceneReloader`（新規）

Shutdown フレームから切り離して `LoadScene` を実行。

```csharp
// DontDestroyOnLoad 上で1フレーム後に LoadScene
public static void Schedule(string scenePath)
{
    EnsureInstance().StartCoroutine(ReloadNextFrame(scenePath));
}
```

`ReturnToLobby()` は `LobbySceneReloader.Schedule(path)` を呼ぶだけに変更。

## 検証

手動（ParrelSync 2 クライアント）:

1. Host + Client でセッション開始
2. Host が Leave Session
3. Client: 切断メッセージ + Host/Join 表示、`[LobbySceneReloader] Loading lobby scene:` ログ
4. Host: 意図的離脱なのでメッセージなし、同様にタイトル復帰
5. 両方から再 Host/Join 成功

## 仕組みの説明

### なぜシーン再読み込みか

Fusion 2 の `NetworkRunner` は Shutdown 後に再利用できない。当初は停止済みRunner参照を保持して次回`StartSession`時に破棄する案もあったが、今回は **`Test_Playground` を Single で再読み込み**し、Editor配置の`Runner` GameObject（SessionManager / PlayerSpawner含む）を丸ごと復元する方式を採用した。

理由:

- 本番タイトルシーンは未作成。当面は `Test_Playground` がロビー兼プレイground
- Shutdown 後の NetworkObject / シーン状態を「その場掃除」で初期化できる
- `LobbyNetworkSceneManager` と組み合わせれば unload→reload の流れが説明しやすい

### NetworkObject が一瞬消える件

Shutdown 時の Fusion クリーンアップ（despawn）と、直後のシーン reload が連続するため、一瞬オブジェクトが消えて見える。reload 後にシーン配置オブジェクトは復元される。

## 面接で聞かれたら

**Q: ホスト切断後、クライアントをどう安全にロビーへ戻したか？**

A: 3 層で対処した。(1) Fusion の Shutdown 時シーン unload を `LobbyNetworkSceneManager` で抑止。(2) `OnShutdown` で `ReturnToLobby()` を呼び Host/Join 状態へ。(3) `LoadScene` は Shutdown コールバック中の `SessionManager` に任せず、`DontDestroyOnLoad` の `LobbySceneReloader` が 1 フレーム後に実行する。Client は `NetworkRunner.Update` 内の同期 Shutdown 経路のため、旧実装では coroutine がキャンセルされていた。

**Q: なぜ Host だけ通って Client だけ落ちたように見えたか？**

A: バグは Client 固有ではなく、Shutdown の**入口とフレーム内タイミング**の差。Host の Leave Session は `OnGUI` 起点、Client のホスト切断追従は `NetworkRunner.Update` 起点。ログ上は両方 `Returning to lobby scene` まで出ていたが、Client だけ 1 フレーム後の `LoadScene` が走らなかった。

## 学んだこと

- **`ReturnToLobby()` が呼ばれた ≠ タイトルに戻った**。同期ログと非同期処理（coroutine / 次フレーム `LoadScene`）を分けて確認する
- **Shutdown コールバック内で「次フレームやる」処理を、コールバック対象の MonoBehaviour に置かない**。Fusion teardown と同フレームで持ち主が消えることがある
- devlog には **stack trace の要約1行**（OnGUI vs Update）を残すと、後から「本当に Update 中だった？」という疑問を自分で解ける

## 残タスク（Out of Scope）

- 本番タイトルシーン作成後、`lobbyScenePath` の差し替え
- UI Toolkit への Host/Join / 切断メッセージ移行
- Host Migration（別設計）
- EditMode テスト（設計書に記載あったが今回は手動検証のみ）
- `Main_Backup` レガシーシーン関連コードの完全削除（参照は既に除去済み）

## 参考

- `Assets/Code/Scripts/Network/SessionManager.cs`
- `Assets/Code/Scripts/Network/LobbyNetworkSceneManager.cs`
- `Assets/Code/Scripts/Network/LobbySceneReloader.cs`
