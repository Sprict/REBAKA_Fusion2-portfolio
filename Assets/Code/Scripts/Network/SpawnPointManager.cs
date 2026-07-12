using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// スポーン位置の管理を一元化するクラス。
    /// 初期スポーン・リスポーン・切断復帰時の位置決定を担当する。
    /// シーンに1つだけ配置する。
    /// </summary>
    public class SpawnPointManager : MonoBehaviour
    {
        [Header("Spawn Points")]
        [Tooltip("シーン上のスポーン位置。Inspectorで配置する。")]
        [SerializeField] private Transform[] spawnPoints;

        [Header("Settings")]
        [SerializeField] private int maxPlayers = 4;

        // 各スロットの使用状態を管理
        // true = 使用中, false = 空き
        private bool[] _slotOccupied;

        // どのスロットにどのPlayerRefが割り当てられているか
        // -1 = 未割り当て
        private int[] _slotToPlayerId;

        private void Awake()
        {
            _slotOccupied = new bool[maxPlayers];
            _slotToPlayerId = new int[maxPlayers];
            for (int i = 0; i < maxPlayers; i++)
            {
                _slotToPlayerId[i] = -1;
            }
        }

        /// <summary>
        /// プレイヤーのスポーン位置を決定し、スロットを割り当てる。
        /// 優先順位: 1. 切断復帰 → 2. 新規空きスロット → 3. フォールバック
        /// </summary>
        /// <param name="player">参加したプレイヤー</param>
        /// <returns>スポーン位置。全スロット満員時はデフォルト位置</returns>
        public Vector3 AssignSpawnPoint(PlayerRef player)
        {
            // 1. 切断復帰チェック: 以前のスロットがあれば再利用
            for (int slotIndex = 0; slotIndex < maxPlayers; slotIndex++)
            {
                if (_slotToPlayerId[slotIndex] == player.PlayerId)
                {
                    _slotOccupied[slotIndex] = true;
                    Debug.Log($"[SpawnPointManager] Player {player.PlayerId} reconnected → Slot {slotIndex}");
                    return GetSpawnPositionAt(slotIndex);
                }
            }

            // 2. 新規: 空きスロットを順番に探す
            for (int slotIndex = 0; slotIndex < maxPlayers; slotIndex++)
            {
                if (!_slotOccupied[slotIndex] && _slotToPlayerId[slotIndex] == -1)
                {
                    _slotOccupied[slotIndex] = true;
                    _slotToPlayerId[slotIndex] = player.PlayerId;
                    Debug.Log($"[SpawnPointManager] Player {player.PlayerId} assigned → Slot {slotIndex}");
                    return GetSpawnPositionAt(slotIndex);
                }
            }

            // 3. フォールバック: 全スロット使用中
            Debug.LogWarning($"[SpawnPointManager] No available slot for Player {player.PlayerId}. Using default.");
            return GetDefaultSpawnPosition();
        }

        /// <summary>
        /// プレイヤー退出時にスロットを解放する。
        /// ただし、切断復帰に備えてPlayerIdの記録は残す。
        /// </summary>
        /// <param name="player">退出したプレイヤー</param>
        /// <param name="preserveSlot">trueならスロット予約を維持（切断復帰用）</param>
        public void ReleaseSpawnPoint(PlayerRef player, bool preserveSlot = true)
        {
            for (int i = 0; i < maxPlayers; i++)
            {
                if (_slotToPlayerId[i] == player.PlayerId)
                {
                    if (!preserveSlot)
                    {
                        _slotToPlayerId[i] = -1;
                    }
                    _slotOccupied[i] = false;
                    Debug.Log($"[SpawnPointManager] Slot {i} released for Player {player.PlayerId} (preserved={preserveSlot})");
                    return;
                }
            }
        }

        /// <summary>
        /// リスポーン位置を取得する。
        /// 現在割り当てられているスロットの位置を返す。
        /// </summary>
        public Vector3 GetRespawnPosition(PlayerRef player)
        {
            for (int i = 0; i < maxPlayers; i++)
            {
                if (_slotToPlayerId[i] == player.PlayerId)
                {
                    if (spawnPoints != null && i < spawnPoints.Length && spawnPoints[i] != null)
                    {
                        return spawnPoints[i].position;
                    }
                }
            }

            // フォールバック
            Debug.LogWarning($"[SpawnPointManager] Player {player.PlayerId} has no assigned slot. Using default position.");
            return GetDefaultSpawnPosition();
        }

        /// <summary>
        /// スロットインデックスからスポーン位置を安全に取得する
        /// </summary>
        private Vector3 GetSpawnPositionAt(int slotIndex)
        {
            if (spawnPoints != null && slotIndex < spawnPoints.Length && spawnPoints[slotIndex] != null)
            {
                return spawnPoints[slotIndex].position;
            }

            Debug.LogWarning($"[SpawnPointManager] spawnPoints[{slotIndex}] is not configured. Using default.");
            return GetDefaultSpawnPosition();
        }

        /// <summary>
        /// スポーンポイントが設定されていない場合のデフォルト位置
        /// </summary>
        private Vector3 GetDefaultSpawnPosition()
        {
            return new Vector3(0, 2, 0);
        }
    }
}
