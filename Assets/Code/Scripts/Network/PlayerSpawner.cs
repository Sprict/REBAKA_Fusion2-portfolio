using Fusion;
using Fusion.Sockets;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// プレイヤーの生成（Spawn）と破棄（Despawn）を管理するクラス。
    /// SpawnPointManager と連携してスポーン位置を決定する。
    /// NetworkRunner と同じ GameObject に配置する。
    /// </summary>
    public class PlayerSpawner : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("Player Settings")]
        [SerializeField] private NetworkPrefabRef playerPrefab;

        [Header("World Objects")]
        [SerializeField] private NetworkPrefabRef[] worldPrefabs;
        [SerializeField] private Vector3[] worldSpawnPositions;

        private SpawnPointManager _spawnPointManager;
        private NetworkRunner _runner;
        private readonly Dictionary<PlayerRef, NetworkObject> _spawnedCharacters = new Dictionary<PlayerRef, NetworkObject>();
        private bool _worldSpawned = false;

        // 定期クリーンアップ用タイマー
        private float _cleanupTimer;
        private const float CleanupInterval = 3f;

        /// <summary>
        /// スポーン済みプレイヤーの読み取り専用アクセス
        /// </summary>
        public IReadOnlyDictionary<PlayerRef, NetworkObject> SpawnedCharacters => _spawnedCharacters;

        private void Awake()
        {
            _spawnPointManager = FindFirstObjectByType<SpawnPointManager>();
            if (_spawnPointManager == null)
            {
                Debug.LogError("[PlayerSpawner] SpawnPointManager not found in scene!");
            }
        }

        private void Update()
        {
            // クリーンアップの権威はサーバーのみが持つ
            if (_runner == null || !_runner.IsServer) return;

            _cleanupTimer += Time.deltaTime;
            if (_cleanupTimer >= CleanupInterval)
            {
                _cleanupTimer = 0f;
                CleanupStaleObjects(_runner);
            }
        }

        #region INetworkRunnerCallbacks (スポーン関連のみ)

        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
        {
            _runner = runner;
            if (!runner.IsServer) return;

            if (!_worldSpawned)
            {
                SpawnWorldObjects(runner);
                _worldSpawned = true;
            }

            Debug.Log($"[PlayerSpawner] >>> OnPlayerJoined: Player {player.PlayerId}. Tracked={_spawnedCharacters.Count}");

            if (playerPrefab == default)
            {
                Debug.LogError("[PlayerSpawner] playerPrefab is not assigned!");
                return;
            }

            // 既に同じPlayerRefのオブジェクトが残っている場合は先にDespawn
            if (_spawnedCharacters.TryGetValue(player, out NetworkObject existingObject))
            {
                Debug.LogWarning($"[PlayerSpawner] Player {player.PlayerId} already tracked. Despawning old object.");
                SafeDespawn(runner, existingObject);
                _spawnedCharacters.Remove(player);
            }

            Vector3 spawnPosition = _spawnPointManager != null
                ? _spawnPointManager.AssignSpawnPoint(player)
                : new Vector3(0, 2, 0);

            NetworkObject networkPlayerObject = runner.Spawn(playerPrefab, spawnPosition, Quaternion.identity, player);
            _spawnedCharacters[player] = networkPlayerObject;

            Debug.Log($"[PlayerSpawner] Player {player.PlayerId} spawned at {spawnPosition}. Tracked={_spawnedCharacters.Count}");
        }

        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
        {
            _runner = runner;
            Debug.Log($"[PlayerSpawner] >>> OnPlayerLeft: Player {player.PlayerId}. Tracked={_spawnedCharacters.Count}");

            if (_spawnedCharacters.TryGetValue(player, out NetworkObject networkObject))
            {
                SafeDespawn(runner, networkObject);
                _spawnedCharacters.Remove(player);
                Debug.Log($"[PlayerSpawner] Player {player.PlayerId} cleaned up. Tracked={_spawnedCharacters.Count}");
            }
            else
            {
                Debug.LogWarning($"[PlayerSpawner] Player {player.PlayerId} left but was not in _spawnedCharacters.");
            }

            if (_spawnPointManager != null)
            {
                _spawnPointManager.ReleaseSpawnPoint(player, preserveSlot: true);
            }
        }

        #endregion

        #region World Objects

        private void SpawnWorldObjects(NetworkRunner runner)
        {
            if (worldPrefabs == null) return;

            for (int i = 0; i < worldPrefabs.Length; i++)
            {
                if (worldPrefabs[i] == default) continue;

                Vector3 pos = (worldSpawnPositions != null && i < worldSpawnPositions.Length)
                    ? worldSpawnPositions[i]
                    : Vector3.zero;

                runner.Spawn(worldPrefabs[i], pos, Quaternion.identity);
                Debug.Log($"[PlayerSpawner] World object [{i}] spawned at {pos}.");
            }
        }

        #endregion

        #region Player Cleanup

        /// <summary>
        /// Despawn 失敗時や Despawn 後も GameObject が残るケースに備え、明示的な Destroy で破棄を保証する
        /// </summary>
        private void SafeDespawn(NetworkRunner runner, NetworkObject obj)
        {
            if (obj == null) return;

            var go = obj.gameObject;

            try
            {
                runner.Despawn(obj);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayerSpawner] Despawn failed: {e.Message}");
            }

            // Despawn後もGameObjectが残っている場合は明示的に破棄
            if (go != null)
            {
                Destroy(go);
            }
        }

        /// <summary>
        /// 定期実行: _spawnedCharactersの中で、既にnullまたは無効になっている
        /// NetworkObjectのエントリを掃除し、対応するGameObjectも破棄する。
        /// また、ActivePlayersに存在しないプレイヤーのオブジェクトもDespawnする。
        /// </summary>
        private void CleanupStaleObjects(NetworkRunner runner)
        {
            if (_spawnedCharacters.Count == 0) return;

            var activePlayers = new HashSet<PlayerRef>();
            foreach (var p in runner.ActivePlayers)
            {
                activePlayers.Add(p);
            }

            var toRemove = new List<PlayerRef>();

            foreach (var kvp in _spawnedCharacters)
            {
                bool shouldRemove = false;
                string reason = "";

                // ケース1: NetworkObjectが既にnull（Unity側で破棄済み）
                if (kvp.Value == null)
                {
                    shouldRemove = true;
                    reason = "NetworkObject is null";
                }
                // ケース2: NetworkObjectは存在するがActivePlayersにいない
                else if (!activePlayers.Contains(kvp.Key))
                {
                    shouldRemove = true;
                    reason = "not in ActivePlayers";
                    SafeDespawn(runner, kvp.Value);
                }

                if (shouldRemove)
                {
                    Debug.LogWarning($"[PlayerSpawner] Periodic cleanup: removing Player {kvp.Key.PlayerId} ({reason}). Tracked={_spawnedCharacters.Count}");
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var key in toRemove)
            {
                _spawnedCharacters.Remove(key);
            }
        }

        #endregion

        #region INetworkRunnerCallbacks (未使用 - 他クラスの責務)

        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason)
        {
            foreach (var kvp in _spawnedCharacters)
            {
                if (kvp.Value != null)
                {
                    Destroy(kvp.Value.gameObject);
                }
            }
            _spawnedCharacters.Clear();
            _runner = null;
            _worldSpawned = false;
            Debug.Log($"[PlayerSpawner] Shutdown ({shutdownReason}). All player objects cleaned up.");
        }

        public void OnInput(NetworkRunner runner, NetworkInput input) { }
        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }
        public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }
        public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }
        public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }
        public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }
        public void OnSceneLoadDone(NetworkRunner runner) { }
        public void OnSceneLoadStart(NetworkRunner runner) { }
        public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }
        public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }

        #endregion
    }
}
