using System;
using System.Collections.Generic;
using Fusion;
using Fusion.Sockets;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;
using MyFolder.Scripts.Utils;
using MyFolder.Scripts.Network;
//using ExitGames.Client.Photon.StructWrapping;

//Fusionを開始するには、Fusion NetworkRunnerでStartGameメソッドを呼び出す必要があります。
/// <summary>
/// 入力に関する定義をまとめたクラス。このコンポーネントは<see cref="NetworkRunner"/>と同じゲームオブジェクト上にある必要がある。
/// </summary>
//[ScriptHelp(BackColor = EditorHeaderBackColor.Steel)]
public class AprInputBehaviour : MonoBehaviour, INetworkRunnerCallbacks
{
    #region Fields

    [SerializeField] private NetworkPrefabRef playerPrefab;
    [SerializeField] private Vector2 mouseSensitivity = new Vector2(0.1f, 0.1f);
    private readonly Dictionary<PlayerRef, NetworkObject> _spawnedCharacters = new Dictionary<PlayerRef, NetworkObject>();

    // 素早いタップを見逃さないように、マウスボタンはUpdate()でサンプル化され、インプット構造で記録されたらfalseにする
    private readonly bool[] _mouseButton = new bool[2];
    private Vector2 _mouse;
    private Vector2 _rightJoystick;

    private NetworkRunner _networkRunner;
    private RunnerEnableVisibility _runnerEnableVisibility;
    private InputActions _inputActions;
    private NetworkInputData _inputData;

    #endregion

    #region Unity Lifecycle Methods

    private void Awake()
    {
        _inputActions = new InputActions();
        _inputData = new NetworkInputData();
    }

    private void OnDestroy()
    {
        _inputActions?.Dispose();
    }

    public void OnEnable()
    {
        _inputActions.gameplay.Enable();
        if (_networkRunner != null)
        {
            _networkRunner.AddCallbacks(this);
        }
    }

    public void OnDisable()
    {
        _inputActions.gameplay.Disable();
        if (_networkRunner != null)
        {
            _networkRunner.RemoveCallbacks(this);
        }
    }

    private void OnGUI()
    {
        // ゲーム開始時に画面左上にHostボタンとJoinボタンを表示する
        if (_networkRunner is null)
        {
            if (GUI.Button(new Rect(0, 0, 200, 40), "Host"))
            {
                StartGame(GameMode.Host);
            }
            if (GUI.Button(new Rect(0, 40, 200, 40), "Join"))
            {
                StartGame(GameMode.Client);
            }
        }
        else
        {
#if UNITY_DEBUG
            GUILayout.Label($"data.MouseX:{_mouse.x}");
            GUILayout.Label($"data.MouseY:{_mouse.y}");`JJ
#endif
        }
    }

    #endregion

    #region Input Handling

    /// <summary>
    /// InputSystemのジャンプアクションに対するコールバック関数
    /// </summary>
    public void OnJump(InputAction.CallbackContext context)
    {
        if (context.started)
        {
            _inputData.Buttons.SetDown(ButtonUtils.ButtonJump);
            Debug.Log($"OnJump started:{context.started.ToString()}");
        }
        else if (context.canceled)
        {
            _inputData.Buttons.SetUp(ButtonUtils.ButtonJump);
            Debug.Log($"OnJump canceled:{context.canceled.ToString()}");
        }
    }

    /// <summary>
    ///  Fusionが入力を収集するタイミングで呼ばれるコールバック
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="input">ポーリングされた入力が格納されている構造体</param> <summary>
    /// </summary>
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        // マウス入力を取得
        _mouseButton[0] = _inputActions.gameplay.LeftGrab.IsPressed(); // 左クリック
        _mouseButton[1] = _inputActions.gameplay.RightGrab.IsPressed(); // 右クリック

        // デバッグログ
#if UNITY_DEBUG
        if (_mouseButton[0] || _mouseButton[1])
        {
            Debug.Log($"Mouse Input - Left: {_mouseButton[0]}, Right: {_mouseButton[1]}");
        }
#endif

        // 入力データの初期化
        var data = new NetworkInputData();

        // 移動方向の設定
        // Raw入力（WASD）を取得
        Vector2 moveInput = _inputActions.gameplay.Move.ReadValue<Vector2>();

        // カメラが存在する場合、カメラ基準のワールド方向に変換する
        if (Camera.main != null)
        {
            Transform camTransform = Camera.main.transform;

            Vector3 forward = camTransform.forward;
            Vector3 right = camTransform.right;

            forward.y = 0;
            right.y = 0;

            forward.Normalize();
            right.Normalize();

            data.direction = (forward * moveInput.y) + (right * moveInput.x);
        }
        else
        {
            // カメラがない場合（稀なケース）は依然と同じワールド基準
            data.direction = new Vector3(moveInput.x, 0, moveInput.y);
        }

        // マウス視点移動
        Vector2 mouseInput = _inputActions.gameplay.Look.ReadValue<Vector2>();
        data.bodyDir = mouseInput;

        // マウスボタンの設定
        if (_mouseButton[0])
        {
            data.Buttons.SetDown(ButtonUtils.ButtonGrab_L);
#if UNITY_DEBUG
            Debug.Log("Left mouse button pressed");
#endif
        }
        if (_mouseButton[1])
        {
            data.Buttons.SetDown(ButtonUtils.ButtonGrab_R);
#if UNITY_DEBUG
            Debug.Log("Right mouse button pressed");
#endif
        }

        // その他のボタン設定
        if (_inputActions.gameplay.Jump.IsPressed())
        {
            data.Buttons.SetDown(ButtonUtils.ButtonJump);
        }
        if (_inputActions.gameplay.Dash.IsPressed())
        {
            data.Buttons.SetDown(ButtonUtils.ButtonDash);
        }
        if (_inputActions.gameplay.Crouch.IsPressed())
        {
            data.Buttons.SetDown(ButtonUtils.ButtonCrouch);
        }
        if (_inputActions.gameplay.LeftPunch.IsPressed())
        {
            data.Buttons.SetDown(ButtonUtils.ButtonPunch_L);
        }
        if (_inputActions.gameplay.RightPunch.IsPressed())
        {
            data.Buttons.SetDown(ButtonUtils.ButtonPunch_R);
        }

        // 入力データの設定
        input.Set(data);
    }

    #endregion

    #region Network Methods

    /// <summary>
    /// Runnerを開始するメソッド
    /// </summary>
    /// <param name="mode">Host, Client</param>
    async void StartGame(GameMode mode)
    {
        // TODO:
        // Create the Fusion runner and let it know that we will be providing user input
        _networkRunner = gameObject.GetComponent<NetworkRunner>();
        _networkRunner.ProvideInput = true;
        _runnerEnableVisibility = gameObject.GetComponent<RunnerEnableVisibility>();

        // StartGameが完了した時点ですでに「ローカルプレイヤーの参加イベント（OnPlayerJoined）」が終わってしまっていることがあるため、このCallbacksはStartGameの前に置く必要がある。
        // コールバックを登録（OnInputが呼ばれるようにする）
        _networkRunner.AddCallbacks(this);
        // Create the NetworkSceneInfo from the current scene
        var scene = SceneRef.FromIndex(SceneManager.GetActiveScene().buildIndex);
        var sceneInfo = new NetworkSceneInfo();
        if (scene.IsValid)
        {
            sceneInfo.AddSceneRef(scene, LoadSceneMode.Additive);
        }

        // Start or join (depends on gamemode) a session with a specific name
        await _networkRunner.StartGame(new StartGameArgs()
        {
            GameMode = mode,
            SessionName = "TestRoom",
            Scene = scene,
            SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>()
        });

        _inputActions.gameplay.Enable();
    }

    #endregion

    #region Network Callbacks

    /// <summary>
    /// ユーザー入力失敗のコールバック
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="player"></param>
    /// <param name="input"></param>
    public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }

    /// <summary>
    /// NetworkRunnerがサーバーまたはホストに正常に接続したときのコールバック
    /// </summary>
    public void OnConnectedToServer(NetworkRunner runner)
    {
#if UNITY_DEBUG
        Debug.Log("APR_InputBehaviour: NetworkRunner connected to server.");
#endif
        throw new NotImplementedException();
    }


    public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }

    /// <summary>
    /// NetworkRunnerがリモートクライアントから接続要求を受信したときのコールバック
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="request"></param>
    /// <param name="token"></param>
    public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }

    /// <summary>
    /// NetworkRunnerがサーバーまたはホストから切断されたときのコールバック
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="reason"></param>
    public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }

    /// <summary>
    /// 新しいプレイヤーが参加したときのNetworkRunnerからのコールバック
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="player"></param>
    public void OnPlayerJoined(NetworkRunner runner, PlayerRef player)
    {
        if (runner.IsServer)
        {
            Vector3 spawnPosition = new Vector3((player.RawEncoded % runner.Config.Simulation.PlayerCount) * 3, 1, 0);
            NetworkObject networkPlayerObject = runner.Spawn(playerPrefab, spawnPosition, Quaternion.identity, player);
            // keep track of the player avatars for easy access
            _spawnedCharacters.Add(player, networkPlayerObject);
        }
    }

    /// <summary>
    /// プレイヤーが切断されたときのNetworkRunnerからのコールバック
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="player"></param>
    public void OnPlayerLeft(NetworkRunner runner, PlayerRef player)
    {
        if (_spawnedCharacters.TryGetValue(player, out NetworkObject networkObject))
        {
            runner.Despawn(networkObject);
            _spawnedCharacters.Remove(player);
        }
    }

    // OnUserSimulationMessage は Fusion 2.1 で廃止されました

    /// <summary>
    /// NetworkRunnerがシャットダウンされたときの呼び出される
    /// </summary>
    /// <param name="runner"></param>
    /// <param name="shutdownReason"></param>
    public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }

    public void OnSessionListUpdated(NetworkRunner runner, List<SessionInfo> sessionList) { }

    public void OnReliableDataReceived(NetworkRunner runner, PlayerRef player, ReliableKey key, ReadOnlySpan<byte> data) { }

    public void OnSceneLoadDone(NetworkRunner runner) { }

    public void OnSceneLoadStart(NetworkRunner runner) { }

    public void OnCustomAuthenticationResponse(NetworkRunner runner, Dictionary<string, object> data) { }

    public void OnHostMigration(NetworkRunner runner, HostMigrationToken hostMigrationToken) { }

    public void OnObjectExitAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }

    public void OnObjectEnterAOI(NetworkRunner runner, NetworkObject obj, PlayerRef player) { }



    public void OnReliableDataProgress(NetworkRunner runner, PlayerRef player, ReliableKey key, float progress) { }

    #endregion
}
