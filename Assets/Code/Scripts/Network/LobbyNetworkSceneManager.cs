using System.Collections;
using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// Shutdown 時に Fusion がネットワーク登録済みシーンを Unload しないようにする SceneManager。
    ///
    /// NetworkSceneManagerDefault は Shutdown で Test_Playground をアンロードするため、
    /// シーン上の SessionManager ごと消えて Host/Join が出なくなる。
    /// アンロードを抑止し、ReturnToLobby のシーン再読み込みで NetworkObject を復元する。
    /// </summary>
    public sealed class LobbyNetworkSceneManager : NetworkSceneManagerDefault
    {
        protected override IEnumerator UnloadSceneCoroutine(SceneRef sceneRef)
        {
            Debug.Log($"[LobbyNetworkSceneManager] Skipping unload of {sceneRef} to preserve lobby shell.");
            yield break;
        }
    }
}
