using Fusion;
using Fusion.Sockets;
using System;
using System.Collections;
using System.Collections.Generic;
using MyFolder.Scripts.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// Fusionセッションのライフサイクルを管理するクラス。
    /// 責務: セッションの開始・終了・接続状態の管理・暫定UI
    /// NetworkRunner と同じ GameObject に配置する。
    /// </summary>
    public class SessionManager : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("Session Settings")]
        [SerializeField] private string defaultSessionName = "TestRoom";
        [SerializeField] private int maxPlayers = 4;
        [FormerlySerializedAs("RequiredNetworkScenePath")]
        [SerializeField] private string lobbyScenePath = "Assets/Level/Scenes/Test_Playground.unity";

        private NetworkRunner _runner;
        // StartGame中の二重呼び出し防止フラグ（OnGUIは1フレームに複数回呼ばれる）
        private bool _isStarting = false;

        // Shutdown 後に戻るロビーシーン。今は Test_Playground のみを使う。
        private static string s_lobbyScenePath;

        // ロビー(Host/Join)画面に一時表示するメッセージ（切断通知など）。
        // シーン再読み込みを跨ぐため static。次回 StartSession でクリアする。
        private static string s_lobbyMessage;
        private static bool s_isReturningToLobby;

        // ユーザーが自分でセッションを抜けたか。意図的な離脱では切断メッセージを出さない。
        private bool _isLeavingIntentionally;
        private bool _isApplicationQuitting;

        /// <summary>
        /// 外部からRunnerを参照するためのプロパティ（PlayerSpawner, InputCollector が利用）
        /// </summary>
        public NetworkRunner Runner => _runner;

        /// <summary>
        /// セッションが開始済みか、または開始処理中かどうか
        /// </summary>
        public bool IsSessionActive =>
            (_runner != null && _runner.IsRunning && !_runner.IsShutdown) || _isStarting;

        /// <summary>
        /// ロビー(Host/Join)画面に表示する一時メッセージ（LobbyMenuUi が表示する）
        /// </summary>
        public string LobbyMessage => s_lobbyMessage;

        private void Awake()
        {
            s_isReturningToLobby = false;
            RememberLobbyScene();

            // タイトル(Host/Join)UI。シーン編集を不要にするためコードから常設する。
            if (GetComponent<LobbyMenuUi>() == null)
                gameObject.AddComponent<LobbyMenuUi>();
        }

        private void OnApplicationQuit()
        {
            _isApplicationQuitting = true;
        }

        #region Session Lifecycle

        /// <summary>
        /// セッションを開始する
        /// </summary>
        public async void StartSession(GameMode mode, string roomName = null)
        {
            if (_isStarting)
                return;
            _isStarting = true;

            // 新しいセッション開始時に、前回の切断通知を消す。
            s_lobbyMessage = null;

            try
            {
                RememberLobbyScene();

                // 既存のRunnerを安全に破棄する
                // Fusion 2 の NetworkRunner は再利用不可 — shutdown後も StartGame() できない
                // ローカル変数にキャプチャし、await後も破棄対象の参照を保持する。
                var runnerToDestroy = _runner;
                if (runnerToDestroy != null)
                {
                    if (runnerToDestroy.IsRunning)
                    {
                        Debug.LogWarning("[SessionManager] Runner already running. Shutting down first.");
                        await runnerToDestroy.Shutdown();
                    }
                    // DestroyImmediate を使用: Destroy()は遅延削除のため、
                    // 同フレーム内のAddComponent()でコンポーネント競合が起きる
                    DestroyImmediate(runnerToDestroy);
                    _runner = null;
                }

                CleanupSessionComponents();

                // Runner取得または新規作成
                // 初回起動: Editor配置済みのRunnerをGetComponent()で取得（未使用なので再利用OK）
                // 再接続:   DestroyImmediate後はGetComponent()=null → AddComponent()で新規作成
                _runner = gameObject.GetComponent<NetworkRunner>() ?? gameObject.AddComponent<NetworkRunner>();
                _runner.ProvideInput = true;
                RegisterRunnerCallbacks(_runner);

                // 通常は NetworkProjectConfig.Global で自動ロードされる。
                // 以降のフォールバックは万一の保険（詳細は LoadNetworkProjectConfig 内）。
                NetworkProjectConfig config = LoadNetworkProjectConfig();
                if (config == null)
                {
                    Debug.LogError("[SessionManager] NetworkProjectConfig could not be loaded.");
                    return;
                }

                LogConfigSnapshot("startup", config, mode, roomName ?? defaultSessionName);
                LogSceneSnapshot("startup", mode);

                // Fusionに物理シミュレーションを委譲
                Physics.simulationMode = SimulationMode.Script;

                // ── ネットワーク化するシーンを明示的に渡す ──
                // これが無いと "Starting a runner by default will not load any scene as networked" となり、
                // シーン配置済みの NetworkObject が RegisterSceneObjects されず同期しない。
                // 現在のアクティブシーン（今は Test_Playground）をそのまま渡す。
                var sceneInfo = new NetworkSceneInfo();
                var activeScene = SceneManager.GetActiveScene();
                var sceneRef = SceneRef.FromIndex(activeScene.buildIndex); // シーンのindexからデフォルト値のSceneRefを返す
                if (sceneRef.IsValid)
                {
                    sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);
                }
                else
                {
                    Debug.LogError($"[SessionManager] '{activeScene.name}' は Build Settings 未登録なようです (buildIndex = -1)。" + "これだとシーンは位置オブジェクトは同期されません。EditorBuildSettings にシーンを追加してください。");
                }

                var startGameArgs = new StartGameArgs()
                {
                    GameMode = mode,
                    SessionName = roomName ?? defaultSessionName,
                    PlayerCount = maxPlayers,
                    SceneManager = gameObject.AddComponent<LobbyNetworkSceneManager>(),
                    Config = config,
                    Scene = sceneInfo
                };

                var result = await _runner.StartGame(startGameArgs);
                if (!result.Ok)
                {
                    // StartGame は接続失敗時に例外を投げず StartGameResult.Ok=false で返す。
                    // ShutdownReason を含めて原因を可視化する（Photon 接続タイムアウトや Region 未指定エラーをここで検出）。
                    Debug.LogError(
                        $"[SessionManager] StartGame failed: mode={mode} room={startGameArgs.SessionName} " +
                        $"shutdownReason={result.ShutdownReason} errorMessage={result.ErrorMessage}");
                    LogConfigSnapshot("start_failed", config, mode, startGameArgs.SessionName, _runner);
                    return;
                }

                // Physics.simulationMode = Script のため、tick に同期して Physics.Simulate() を
                // 呼ぶ RunnerSimulatePhysics を明示的に登録する。
                // 以前は装飾 Sphere の NetworkRigidbody.Spawned が偶然これを自動追加しており、
                // その NRB を削除した途端に物理シミュレーション全体が停止した（2026-06-12 の障害）
                EnsurePhysicsSimulation(_runner);

                Debug.Log($"[SessionManager] Session started: mode={mode}, room={startGameArgs.SessionName}");
                LogConfigSnapshot("started", config, mode, startGameArgs.SessionName, _runner);
                LogSceneSnapshot("started", mode);
            }
            catch (Exception e)
            {
                Debug.LogError($"[SessionManager] Failed to start session: {e.Message}");
            }
            finally
            {
                _isStarting = false;
            }
        }

        /// <summary>
        /// 物理シミュレーション駆動コンポーネントを Runner に登録する。
        /// NetworkRigidbody.SetupPhysicsBody() と同じ手順（TryGetComponent → AddComponent
        /// → AddGlobal → Update3DPhysicsScene = true）を Runner 起動直後に明示実行する。
        ///
        /// アドオン標準の RunnerSimulatePhysics ではなく それを継承した resim ガード付きの
        /// NoResimulationSimulatePhysics を登録する（クライアントの resimulation で
        /// Physics.Simulate が多重実行され装飾のローカル物理が加速する障害の対策）。
        /// 基底型で TryGetComponent するため、本クラスが先に居れば
        /// NetworkRigidbody 側の自動追加も発動しない。
        /// </summary>
        private static void EnsurePhysicsSimulation(NetworkRunner runner)
        {
            if (runner == null)
                return;

            if (!runner.TryGetComponent<Fusion.Addons.Physics.RunnerSimulatePhysics>(out var simulatePhysics))
            {
                simulatePhysics = runner.gameObject.AddComponent<NoResimulationSimulatePhysics>();
                runner.AddGlobal(simulatePhysics);
            }

            simulatePhysics.Update3DPhysicsScene = true;
        }

        /// <summary>
        /// 現在のセッションから抜けてタイトル(Host/Join)画面へ戻る。ホスト/クライアント共通。
        ///
        /// ホストが抜けると Photon がルームを閉じ、各クライアントは
        /// OnDisconnectedFromServer 経由で自動的にロビーへ戻る。
        ///
        /// 自発的な離脱なので切断メッセージは表示しない。
        /// OnShutdown でロビーシーンを再読み込みし、シーン配置の Runner を復元する。
        /// </summary>
        public async void LeaveSession()
        {
            if (_runner == null || !_runner.IsRunning)
                return;

            _isLeavingIntentionally = true;
            Debug.Log("[SessionManager] Leaving session by user request.");
            await _runner.Shutdown();
        }

        /// <summary>
        /// Shutdown 後にタイトル(Host/Join)へ戻す。
        /// NetworkSceneManagerDefault は Shutdown 時にネットワークシーンを unload しうるため、
        /// LobbyNetworkSceneManager で unload を抑止した上で、ここで明示的にロビーシーンを読み直す。
        /// </summary>
        private void ReturnToLobby()
        {
            if (_isApplicationQuitting || s_isReturningToLobby)
                return;

            s_isReturningToLobby = true;
            _isStarting = false;
            Physics.simulationMode = SimulationMode.FixedUpdate;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            var path = string.IsNullOrEmpty(s_lobbyScenePath)
                ? lobbyScenePath
                : s_lobbyScenePath;

            if (string.IsNullOrEmpty(path))
            {
                s_isReturningToLobby = false;
                Debug.LogWarning("[SessionManager] Lobby scene path is empty. Host/Join UI may not recover.");
                return;
            }

            Debug.Log($"[SessionManager] Returning to lobby scene: {path}");
            LobbySceneReloader.Schedule(path);
        }

        private void RememberLobbyScene()
        {
            var activeScene = SceneManager.GetActiveScene();
            var activeScenePath = string.IsNullOrEmpty(activeScene.path)
                ? activeScene.name
                : activeScene.path;

            if (!string.IsNullOrEmpty(activeScenePath))
            {
                s_lobbyScenePath = activeScenePath;
                lobbyScenePath = activeScenePath;
            }
        }

        private void CleanupSessionComponents()
        {
            var sceneManager = GetComponent<NetworkSceneManagerDefault>();
            if (sceneManager != null)
                DestroyImmediate(sceneManager);

            var simulatePhysics = GetComponent<Fusion.Addons.Physics.RunnerSimulatePhysics>();
            if (simulatePhysics != null)
                DestroyImmediate(simulatePhysics);
        }

        private void RegisterRunnerCallbacks(NetworkRunner runner)
        {
            if (runner == null)
                return;

            runner.AddCallbacks(this);

            var spawner = GetComponent<PlayerSpawner>();
            if (spawner != null)
                runner.AddCallbacks(spawner);

            var inputCollector = GetComponent<InputCollector>();
            if (inputCollector != null)
                runner.AddCallbacks(inputCollector);
        }

        #endregion

        // タイトル(Host/Join)UI は LobbyMenuUi へ移行済み（IMGUI はゲームパッド選択不可のため）

        #region INetworkRunnerCallbacks (セッション関連のみ)

        public void OnConnectedToServer(NetworkRunner runner)
        {
            Debug.Log("[SessionManager] Connected to server.");
        }

        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason)
        {
            Debug.Log($"[SessionManager] Disconnected from server: {reason}");

            // ホストが落ちた等の予期しない切断のみメッセージを出す。自発的な離脱では出さない。
            if (!_isLeavingIntentionally)
            {
                s_lobbyMessage = $"ホストとの接続が切れました（{reason}）。Host か Join で再開できます。";
            }
        }

        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }

        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason)
        {
            Debug.LogError($"[SessionManager] Connection failed: {reason}");
        }

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            Debug.Log($"[SessionManager] Shutdown: {shutdownReason}");

            // 予期しない Shutdown で、まだ切断メッセージが無ければ補完する
            // （OnDisconnectedFromServer を経ない終了経路の保険）。
            if (!_isLeavingIntentionally
                && string.IsNullOrEmpty(s_lobbyMessage)
                && shutdownReason != ShutdownReason.Ok)
            {
                s_lobbyMessage = $"セッションが終了しました（{shutdownReason}）。Host か Join で再開できます。";
            }

            _isLeavingIntentionally = false;
            ReturnToLobby();

            // ReturnToLobby が Test_Playground を再読み込みし、シーン配置の Runner を復元する。
        }

        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }

        // SessionManager は以下のコールバックを処理しない（他クラスの責務）
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }

        #endregion

        /// <summary>
        /// NetworkProjectConfig をロードする。複数のフォールバックを試みる。
        /// </summary>
        private static NetworkProjectConfig LoadNetworkProjectConfig()
        {
            // 1. 通常パス: NetworkProjectConfig.Global
            try
            {
                var global = NetworkProjectConfig.Global;
                if (global != null)
                {
                    Debug.Log("[SessionManager] Config loaded via NetworkProjectConfig.Global.");
                    return global;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SessionManager] NetworkProjectConfig.Global failed: {e.Message}");
            }

            // 2. 手動 Resources.Load
            try
            {
                var asset = Resources.Load<NetworkProjectConfigAsset>("NetworkProjectConfig");
                if (asset != null && asset.Config != null)
                {
                    Debug.Log("[SessionManager] Config loaded via Resources.Load<NetworkProjectConfigAsset>.");
                    return asset.Config;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SessionManager] Resources.Load failed: {e.Message}");
            }

            // 3. 全 Resources 検索
            try
            {
                var allConfigs = Resources.LoadAll<NetworkProjectConfigAsset>("");
                if (allConfigs != null && allConfigs.Length > 0 && allConfigs[0].Config != null)
                {
                    Debug.Log($"[SessionManager] Config loaded via Resources.LoadAll (count={allConfigs.Length}).");
                    return allConfigs[0].Config;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SessionManager] Resources.LoadAll failed: {e.Message}");
            }

            // 4. JSON TextAsset からの手動デシリアライズ（MPPM最終手段）
            // 'Tools > Fusion > Bake Config for MPPM' で生成された TextAsset を使用
            try
            {
                var textAsset = Resources.Load<TextAsset>("NetworkProjectConfigBackup");
                if (textAsset != null && !string.IsNullOrEmpty(textAsset.text))
                {
                    var config = JsonUtility.FromJson<NetworkProjectConfig>(textAsset.text);
                    if (config != null)
                    {
                        Debug.Log("[SessionManager] Config loaded from JSON TextAsset backup.");
                        return config;
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[SessionManager] JSON TextAsset fallback failed: {e.Message}");
            }

            // 5. デフォルト設定で新規作成（最終手段）
            try
            {
                Debug.LogWarning("[SessionManager] All config load methods failed. Creating default config.");
                var defaultConfig = new NetworkProjectConfig();
                return defaultConfig;
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[SessionManager] Default config creation failed: {e.Message}");
            }

            return null;
        }

        private static void LogConfigSnapshot(string phase, NetworkProjectConfig config, GameMode mode, string roomName,
            NetworkRunner runner = null)
        {
            if (config == null)
            {
                RagdollNetDiagnostics.Log("session_config",
                    $"phase={phase} mode={mode} room={roomName} config_missing=true");
                return;
            }

            var tickSelection = config.Simulation.TickRateSelection;
            var runnerTickRate = runner != null && runner.DeltaTime > 0f ? 1f / runner.DeltaTime : 0f;

            RagdollNetDiagnostics.Log("session_config",
                $"phase={phase} mode={mode} room={roomName} hub_mode={config.HubMode} peer_mode={config.PeerMode} " +
                $"physics_forecast={config.PhysicsForecast} tick_sel_client={tickSelection.Client} " +
                $"tick_sel_client_send_interval={tickSelection.ClientSendInterval} " +
                $"tick_sel_server_tick_interval={tickSelection.ServerTickInterval} " +
                $"tick_sel_server_send_interval={tickSelection.ServerSendInterval} " +
                $"runner_tick_rate={runnerTickRate:F2}");
        }

        private static void LogSceneSnapshot(string phase, GameMode mode)
        {
            var activeScene = SceneManager.GetActiveScene();
            var activeScenePath = string.IsNullOrEmpty(activeScene.path) ? activeScene.name : activeScene.path;

            RagdollNetDiagnostics.Log(
                "scene_context",
                $"phase={phase} mode={mode} active_scene={activeScenePath} loaded_scene_count={SceneManager.sceneCount}");

            if (activeScenePath.Contains("/_Recovery/") || activeScenePath.Contains("\\_Recovery\\"))
            {
                Debug.LogWarning(
                    $"[SessionManager] Active scene is a recovery scene: {activeScenePath}. " +
                    "Use Assets/Level/Scenes/Test_Playground.unity for network tests.");
            }
        }
    }
}

namespace MyFolder.Scripts.Diagnostics
{
    using System.Collections.Generic;
    using UnityEngine;

    /// <summary>
    /// Ragdoll network diagnostics logger with lightweight throttling.
    /// Detailed logging is only active when RAGDOLL_NET_DIAG is defined.
    /// </summary>
    public static class RagdollNetDiagnostics
    {
#if RAGDOLL_NET_DIAG
        private static readonly Dictionary<string, float> LastLogTimes = new Dictionary<string, float>(64);
        private const float CleanupIntervalSeconds = 30f;
        private const float ExpireSeconds = 120f;
        private static float _nextCleanupAt;
#endif

        public static bool IsEnabled
        {
#if RAGDOLL_NET_DIAG
            get => true;
#else
            get => false;
#endif
        }

        public static void Log(string category, string message, Object context = null, float minIntervalSeconds = 0f,
            string throttleKey = null)
        {
#if RAGDOLL_NET_DIAG
            if (ShouldSkip(minIntervalSeconds, throttleKey, category))
                return;

            var formatted = $"[RAGDIAG] category={category} {message}";
            if (context != null)
            {
                Debug.Log(formatted, context);
            }
            else
            {
                Debug.Log(formatted);
            }
#endif
        }

        public static void LogAuthorityViolation(string message, Object context = null, float minIntervalSeconds = 0.2f,
            string throttleKey = null)
        {
            Log("authority_violation", message, context, minIntervalSeconds, throttleKey);
        }

        public static void LogKinematicLeak(string message, Object context = null, float minIntervalSeconds = 0.2f,
            string throttleKey = null)
        {
            Log("kinematic_leak", message, context, minIntervalSeconds, throttleKey);
        }

#if RAGDOLL_NET_DIAG
        private static bool ShouldSkip(float minIntervalSeconds, string throttleKey, string category)
        {
            if (minIntervalSeconds <= 0f)
                return false;

            var now = Time.realtimeSinceStartup;
            var key = string.IsNullOrEmpty(throttleKey)
                ? category
                : throttleKey;

            if (LastLogTimes.TryGetValue(key, out var last) && now - last < minIntervalSeconds)
                return true;

            LastLogTimes[key] = now;
            Cleanup(now);
            return false;
        }

        private static void Cleanup(float now)
        {
            if (now < _nextCleanupAt)
                return;

            _nextCleanupAt = now + CleanupIntervalSeconds;

            var staleKeys = ListPool.Get();
            foreach (var pair in LastLogTimes)
            {
                if (now - pair.Value > ExpireSeconds)
                {
                    staleKeys.Add(pair.Key);
                }
            }

            for (var i = 0; i < staleKeys.Count; i++)
            {
                LastLogTimes.Remove(staleKeys[i]);
            }

            ListPool.Release(staleKeys);
        }

        private static class ListPool
        {
            private static readonly Stack<List<string>> Pool = new Stack<List<string>>();

            public static List<string> Get()
            {
                if (Pool.Count > 0)
                {
                    var list = Pool.Pop();
                    list.Clear();
                    return list;
                }

                return new List<string>(16);
            }

            public static void Release(List<string> list)
            {
                if (list == null)
                    return;

                if (Pool.Count > 8)
                    return;

                list.Clear();
                Pool.Push(list);
            }
        }
#endif
    }
}
