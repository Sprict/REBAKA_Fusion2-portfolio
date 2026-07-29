using UnityEngine;
using Fusion;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Network;

namespace MyFolder.Scripts.Respawn
{
    /// <summary>
    /// プレイヤーのリスポーン処理を行うネットワーク対応コンポーネント。
    /// SpawnPointManager と連携してリスポーン位置を取得する。
    /// </summary>
    public class MyRespawn : NetworkBehaviour
    {
        [Header("Respawn Settings")]
        [SerializeField] private bool enableRespawn = true;

        [Header("Fallback (SpawnPointManager が無い場合)")]
        [SerializeField] private Vector3 fallbackRespawnPosition = new Vector3(0, 2, 0);

        private SpawnPointManager _spawnPointManager;

        public override void Spawned()
        {
            _spawnPointManager = FindFirstObjectByType<SpawnPointManager>();
        }

        private void OnTriggerEnter(Collider col)
        {
            if (!col.gameObject.CompareTag("Player") || !enableRespawn) return;

            var networkObject = col.GetComponentInParent<NetworkObject>();
            if (networkObject == null || !networkObject.HasStateAuthority) return;

            Debug.Log("MyRespawn: プレイヤーがリスポーンゾーンに入りました。");

            var ragdollController = networkObject.GetComponent<RagdollController>();
            if (ragdollController != null)
            {
                RespawnPlayer(ragdollController, networkObject);
            }
            else
            {
                Debug.LogWarning("MyRespawn: RagdollControllerが見つかりません。");
            }
        }

        private void RespawnPlayer(RagdollController controller, NetworkObject networkObject)
        {
            if (controller == null) return;

            // SpawnPointManager からリスポーン位置を取得
            Vector3 respawnPosition = GetRespawnPosition(networkObject);

            var bodyRigidbodies = controller.BodyRigidbodies;
            if (bodyRigidbodies == null || bodyRigidbodies.Length == 0) return;

            var rootRigidbody = bodyRigidbodies[0];
            if (rootRigidbody == null) return;

            Debug.Log($"MyRespawn: プレイヤーを {respawnPosition} にリスポーンします。");

            rootRigidbody.position = respawnPosition;
            rootRigidbody.linearVelocity = Vector3.zero;
            rootRigidbody.angularVelocity = Vector3.zero;

            // クライアントの SnapshotInterpolation 補間がテレポートを跨いで
            // スミアしないよう明示通知する（Publisher の自動検出のバックアップ）
            controller.RequestPoseTeleport();
        }

        private Vector3 GetRespawnPosition(NetworkObject networkObject)
        {
            if (_spawnPointManager != null)
            {
                PlayerRef player = networkObject.InputAuthority;
                return _spawnPointManager.GetRespawnPosition(player);
            }

            return fallbackRespawnPosition;
        }
    }
}