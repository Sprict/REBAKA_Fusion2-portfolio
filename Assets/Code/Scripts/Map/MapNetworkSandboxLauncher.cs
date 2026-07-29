// Assets/Code/Scripts/Map/MapNetworkSandboxLauncher.cs
using System;
using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 段階C 検証専用の最小ネットワークランチャー（本番 SessionManager とは独立）。
    ///
    /// 本番の SessionManager は検証シーンを Main_Backup へ強制切替するため、隔離した
    /// マップ配布検証には使えない。ここでは「ランナー起動 → シーンを networked として渡す
    /// → シーン配置の <see cref="MapNetworkDistributor"/> が登録され、ホストが manifest を配る」
    /// という最小経路だけを用意する。プレイヤー・物理・入力は扱わない（地形同期の検証に集中）。
    ///
    /// シーン配置 NetworkObject を同期させるには、この検証シーンが Build Settings に登録され、
    /// StartGameArgs.Scene にそのシーンを渡す必要がある（過去の RegisterSceneObjects 不発の教訓）。
    /// </summary>
    public sealed class MapNetworkSandboxLauncher : MonoBehaviour
    {
        [SerializeField] private string _sessionName = "MapSandbox";
        [SerializeField] private int _maxPlayers = 4;

        private NetworkRunner _runner;
        private bool _starting;

        private bool IsActive => (_runner != null && _runner.IsRunning) || _starting;

        private void OnGUI()
        {
            if (IsActive)
            {
                GUI.Label(new Rect(10, 10, 400, 24), $"[MapSandbox] running mode={(_runner != null ? _runner.GameMode.ToString() : "?")}");
                return;
            }

            if (GUI.Button(new Rect(10, 10, 200, 40), "Host"))
                StartSession(GameMode.Host);
            if (GUI.Button(new Rect(10, 55, 200, 40), "Join (Client)"))
                StartSession(GameMode.Client);
        }

        public async void StartSession(GameMode mode)
        {
            if (_starting) return;
            _starting = true;

            try
            {
                _runner = gameObject.GetComponent<NetworkRunner>() ?? gameObject.AddComponent<NetworkRunner>();
                _runner.ProvideInput = false; // 入力不要（地形同期のみ検証）

                // 検証シーンを networked として渡す（シーン配置 Distributor の RegisterSceneObjects 発火条件）。
                var sceneInfo = new NetworkSceneInfo();
                Scene active = SceneManager.GetActiveScene();
                SceneRef sceneRef = SceneRef.FromIndex(active.buildIndex);
                if (sceneRef.IsValid)
                {
                    sceneInfo.AddSceneRef(sceneRef, LoadSceneMode.Additive);
                }
                else
                {
                    Debug.LogError($"[MapNetworkSandboxLauncher] '{active.name}' が Build Settings 未登録（buildIndex=-1）。" +
                                   "シーン配置の Distributor が同期しません。Build Settings に追加してください。");
                }

                var args = new StartGameArgs
                {
                    GameMode = mode,
                    SessionName = _sessionName,
                    PlayerCount = _maxPlayers,
                    SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>(),
                    Scene = sceneInfo,
                };

                StartGameResult result = await _runner.StartGame(args);
                if (!result.Ok)
                {
                    Debug.LogError($"[MapNetworkSandboxLauncher] StartGame 失敗: mode={mode} reason={result.ShutdownReason} msg={result.ErrorMessage}");
                    return;
                }

                Debug.Log($"[MapNetworkSandboxLauncher] セッション開始 mode={mode} room={_sessionName}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[MapNetworkSandboxLauncher] 起動例外: {e.Message}");
            }
            finally
            {
                _starting = false;
            }
        }
    }
}
