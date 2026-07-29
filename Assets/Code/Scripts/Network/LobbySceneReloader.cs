using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// Shutdown コールバック中は SessionManager 本体が次フレームまで生き残らないことがあるため、
    /// DontDestroyOnLoad 上でロビーシーン再読み込みを1フレーム遅延実行する。
    /// </summary>
    internal sealed class LobbySceneReloader : MonoBehaviour
    {
        private static LobbySceneReloader s_instance;

        public static void Schedule(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath))
                return;

            EnsureInstance().StartCoroutine(ReloadNextFrame(scenePath));
        }

        private static LobbySceneReloader EnsureInstance()
        {
            if (s_instance != null)
                return s_instance;

            var go = new GameObject(nameof(LobbySceneReloader));
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<LobbySceneReloader>();
            return s_instance;
        }

        private static IEnumerator ReloadNextFrame(string scenePath)
        {
            yield return null;

            Debug.Log($"[LobbySceneReloader] Loading lobby scene: {scenePath}");
            SceneManager.LoadScene(scenePath, LoadSceneMode.Single);

            if (s_instance != null)
            {
                Destroy(s_instance.gameObject);
                s_instance = null;
            }
        }
    }
}
