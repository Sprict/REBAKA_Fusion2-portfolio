# 2026-04-24: Fusion StartGameResult.Ok チェック追加と「タイムアウト」の真因

## 問題（当初の誤解）

前々回のセッションで、`Test_Playground.unity` と `Main_Backup.unity` の両方で
`Fusion.NetworkRunner.StartGameModeSinglePlayer` が TimeoutException を出して Host
が起動できないように見えていた。devlog には「pre-existing preview SDK 由来の警告」と
だけ記録し、根本原因は未特定だった。

## 調査

### 仮説

1. Photon AppId 未設定 → 接続不可
2. NetworkSceneManagerDefault 未アタッチ → シーンロード失敗
3. Scene が EditorBuildSettings にない → シーン解決失敗
4. Fusion 2.1.0 Preview の Single モード未実装
5. FixedRegion 未指定 → Nameserver 接続タイムアウト

### 実地検証

`uloop execute-dynamic-code` でリフレクション経由で `StartSession(GameMode.Host, ...)`
を呼び、30 秒後に全 NetworkRunner を確認した:

```
count=2
| Runner:IsRunning=True,IsServer=True,Mode=Host
| Runner_001:IsRunning=False,IsServer=False,Mode=0
```

ログには `[RAGDIAG] category=ragdoll_sync role=Host phase=fixed tick_est=9041.8
rb_total=27 rb_non_kinematic=27 stateAuthority=True inputAuthority=True ...` が
大量に出ていた。**Host はずっと動いていた。**

### 真因

1. **前々回の TimeoutException は人工症状**だった。
   私が検証用に書いた `call_starthost.csx` で `GameMode.Single` をリフレクション
   で呼び出していたが、`NetworkProjectConfig.fusion` の設定は:
   - `HubMode: 2` (Server 接続必須)
   - `PeerMode: 0` (Peer / Single モード非対応)
   このため Single は Photon サーバー接続を試み、`PhotonAppSettings.FixedRegion` が
   空のまま Nameserver 選定→10 秒 `ConnectionTimeout` で失敗していた。
   SessionManager の OnGUI ボタンは `Host` と `Client` しか出さないので、**通常
   プレイでは Single は選ばれない**。

2. **前回の "Runner=NotRunning" も人工症状**だった。
   `FindFirstObjectByType<NetworkRunner>()` が Runner_001（未使用の leftover
   GameObject）を拾っていた。SessionManager 本体は `Runner` に乗っており、
   こちらは正常に `IsRunning=True, IsServer=True` で動いていた。

3. **真に修正価値があるのは「結果チェック欠如」だけ**。
   `SessionManager.StartSession` は `await _runner.StartGame(startGameArgs)` の
   戻り値 `StartGameResult` を確認せず、例外でなければ必ず
   `"[SessionManager] Session started"` ログを出していた。Fusion 2 では接続失敗時
   に例外を投げず `StartGameResult.Ok=false` を返すので、**失敗時も「成功」と
   ログが出る**偽陽性が残っていた。

## 何をやったか

### `fix(network)`: StartGameResult.Ok チェック追加

```csharp
// Before
await _runner.StartGame(startGameArgs);
Debug.Log($"[SessionManager] Session started: mode={mode}, room={startGameArgs.SessionName}");

// After
var result = await _runner.StartGame(startGameArgs);
if (!result.Ok)
{
    Debug.LogError(
        $"[SessionManager] StartGame failed: mode={mode} room={startGameArgs.SessionName} " +
        $"shutdownReason={result.ShutdownReason} errorMessage={result.ErrorMessage}");
    LogConfigSnapshot("start_failed", config, mode, startGameArgs.SessionName, _runner);
    return;
}
Debug.Log($"[SessionManager] Session started: mode={mode}, room={startGameArgs.SessionName}");
```

これで将来、Photon 接続が切れたり AppId が無効化されたりした場合に、
`shutdownReason` / `errorMessage` / `LogConfigSnapshot("start_failed", ...)`
が一括で吐かれて即座に切り分けられる。

### 検証

`Test_Playground.unity` / Play モード / リフレクションで `StartSession(Host)` →
30 秒後:

```
count=2
| Runner:IsRunning=True,IsServer=True,Mode=Host  ← OK
| Runner_001:IsRunning=False,IsServer=False      ← 未使用 leftover
```

RAGDIAG ログも正常出力、`rb_total=27`, `role=Host`, `stateAuthority=True`。
Error 0 件。

スクショ: `.uloop/outputs/Screenshots/Rendering_20260424_070954_944.png`

## 仕組みの説明

### Fusion 2 の StartGame は例外を投げない（設計思想）

Fusion 2 以降、`NetworkRunner.StartGame(StartGameArgs)` は `Task<StartGameResult>`
を返す。接続失敗・シーンロード失敗・Runtime 設定不備などは**例外ではなく
`StartGameResult.Ok=false` + `ShutdownReason` + `ErrorMessage` として返却される**。
これは「起動失敗は制御フローではなくデータとして扱う」Result パターン。

したがって `try { await StartGame } catch` だけでは接続失敗を拾えない。
`result.Ok` を必ずチェックしないと「静かな失敗」が発生する。

### なぜ例外ではなくなったか（[※推測] Fusion 2 リリースノート未確認）

- Await 中に遠隔例外が伝播するとスレッド境界の扱いが難しい
- UniTask / Task の両対応がしやすい
- Result 経由なら呼び出し側が握り潰しを「意図的に」できる（握り潰すかどうかが
  コードで明示される）

## 自力再実装チェックリスト

1. `SessionManager.StartSession` 内の `await _runner.StartGame(startGameArgs);` を
   `var result = await _runner.StartGame(startGameArgs);` に変更
2. 直後に `if (!result.Ok) { ... return; }` ガードを追加
3. ガード内で `Debug.LogError` に `shutdownReason` / `errorMessage` を含める
4. 可能なら `LogConfigSnapshot("start_failed", ...)` で config スナップショット保存
5. `uloop compile` で通ることを確認
6. Play モードに入って Host 起動 → 30 秒後に `FindObjectsByType<NetworkRunner>`
   で **全 Runner** の `IsRunning` を確認（`FindFirstObjectByType` は leftover
   を拾う可能性）
7. RAGDIAG ログ (`category=ragdoll_sync role=Host stateAuthority=True`) が
   出ていることを確認

## 学んだこと

### 人工症状に注意

**検証スクリプトが再現している「バグ」が、自分で作った人工症状でないかを
先に確認する。** 今回のように GameMode.Single を使ったスクリプトで再現した
タイムアウトは、通常プレイでは発生しない。

### `FindFirstObjectByType` の罠

`FindFirstObjectByType<T>()` はシーン順の最初のインスタンスを返す。**複数
同種コンポーネントがある場合は `FindObjectsByType<T>(FindObjectsSortMode.None)`
で全部列挙してから用途に合うものを選ぶ**のが安全。今回の Runner_001 leftover
のように、片方しか動かないケースで誤認する。

### Fusion 2 の Result パターン

Fusion 1 の `StartGame` は例外を投げる可能性があったが、Fusion 2 は
`StartGameResult.Ok` を返す。**Fusion 1 の知識で書かれたサンプル/チュートリアル
をそのまま流用すると偽陽性ログが出る**。

## 残タスク（Out of Scope）

- `Runner_001` leftover GameObject のクリーンアップ（別 issue）
- `PhotonAppSettings.FixedRegion` の明示指定（Nameserver 依存を減らす）
- `PhotonAppSettings.AppVersion` の明示指定（バージョンマッチング保証）
- `NetworkSceneManagerDefault` を毎回 AddComponent している冗長性
  （Shutdown 時に掃除されるので実害は低いが、コード簡素化の余地あり）

## 参考

- `Assets/Code/Scripts/Network/SessionManager.cs` (L116-128 修正後)
- `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset` (AppIdFusion 設定済み)
- Fusion 2.1.0 Preview 1627 (2026-01-09 build)
- 前回の誤診断: `Docs/devlogs/2026-04-19_test-playground-scene.md`
  "既知の問題" 欄（本 devlog が真因を上書き）
