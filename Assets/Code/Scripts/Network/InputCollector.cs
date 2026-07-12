using Fusion;
using Fusion.Sockets;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Settings;
using MyFolder.Scripts.Utils;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// ローカルプレイヤーの入力を収集し、Fusionネットワーク入力として送信するクラス。
    /// 責務: 入力の収集とNetworkInputDataへの変換のみ。
    /// NetworkRunner と同じ GameObject に配置する。
    ///
    /// 操作スキーム（APR サンプル準拠 / 2026-06-29 反転）:
    ///   Player/Look X → カメラ水平旋回（OrbitCamera）
    ///   Alt+Player/Look X → 体のロール
    ///   Player/Look Y → 体の上下（胴体ベンド + リーチ時の腕ピッチ）
    ///
    /// Fusion resim 対策: Lookデルタは非決定的なので、描画レート(Update)で
    /// ベンド/リーチ/ロール量に累積し、OnInput では絶対値を送る。
    /// </summary>
    public class InputCollector : MonoBehaviour, INetworkRunnerCallbacks
    {
        [Header("Look Sensitivity")]
        [Tooltip("Alt+Player/Look Xの胴体ロール感度（度/Lookデルタ）。")]
        [SerializeField] private float lookSensitivityX = 0.15f;

        [Tooltip("Player/Look Yの上下感度（胴体ベンド/腕リーチの累積速度）。")]
        [SerializeField] private float lookSensitivityY = 0.004f;

        [Tooltip("Player/Look Yを反転する（オン=上入力で下を向く）。")]
        [SerializeField] private bool invertY = false;

        [Header("Two-Handed Body Yaw")]
        [Tooltip("両手で同一オブジェクトを掴んでいる間の、Player/Look Xによるボディヨー感度（度/Lookデルタ）。Limit なしで一周できる。")]
        [SerializeField] private float bodyYawSensitivity = 0.2f;

        [Tooltip("プレイ中にカーソルをロック＆非表示にする（マウス由来のPlayer/Lookを連続取得するために必要）。Escでトグル。")]
        [SerializeField] private bool lockCursor = true;

        [Header("Tuning Profile")]
        [Tooltip("Player/Look由来の胴体ベンド/腕リーチ/ロール上限を読む profile。")]
        [SerializeField] private RagdollProfile profile;

        private REBAKA_Fusion2 _inputActions;

        // APR の MouseYAxisBody/MouseYAxisArms に相当する「絶対」累積値。
        // resim 安全のため OnInput では生デルタではなくこの絶対値を送る。
        // 体のヨーは移動方向へ向き直る。Look X単体はカメラ旋回、Alt+Look Xは胴体ロール。
        /// <summary>胴体ベンド入力値。範囲は <see cref="RagdollProfile.bodyBendInputLimit"/>（未設定時 <see cref="RagdollProfile.DefaultBodyBendInputLimit"/>）で決まる。APR MouseYAxisBody に対応</summary>
        private float _bodyBend;
        /// <summary>腕の上下リーチ入力値。範囲は <see cref="RagdollProfile.reachArmInputLimit"/>（未設定時 <see cref="RagdollProfile.DefaultReachArmInputLimit"/>）で決まる。APR MouseYAxisArms に対応</summary>
        private float _armReach;
        /// <summary>胴体ロール角（単位は度）。Alt キー押下中の Player/Look X 入力から <see cref="CalculateBodyRoll"/> で算出</summary>
        private float _bodyRoll;
        /// <summary>
        /// 両手持ち（両手が同一オブジェクトを掴んでいる）時のボディヨー角(度)。
        /// </summary>
        /// <remarks>
        /// 突入時に現在の体の向きから初期化するため、モード切替でスナップしない。
        /// </remarks>
        private float _bodyYaw;
        private bool _twoHandedHoldActive;

        // Reach（LeftGrab/RightGrab）の立ち上がり検出用。突入フレームで _armReach を
        // 0 にリセットし、前回reach終了後の残留累積値が次のreachへ持ち越されるのを防ぐ。
        private bool _reachActive;

        // Camera.main は FindWithTag を毎回実行するため、最初の参照時にキャッシュして再利用する
        private UnityEngine.Camera _mainCamera;

        private void Awake()
        {
            _inputActions = new REBAKA_Fusion2();
            ParrelSyncInputUtil.RestrictDevices(_inputActions.asset);
            InputSettingsRuntime.Register(_inputActions.asset);
        }

        private void OnEnable()
        {
            _inputActions.Player.Enable();
        }

        private void OnDisable()
        {
            _inputActions.Player.Disable();
        }

        private void OnDestroy()
        {
            InputSettingsRuntime.Unregister(_inputActions?.asset);
            _inputActions?.Dispose();
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        /// <summary>
        /// 描画レートでLookデルタを絶対ベンド/リーチ/ロールへ累積する。
        /// OnInput はこの絶対値のスナップショットを送るため resim でも一致する。
        ///
        /// カーソルロックは「セッション稼働中のみ」適用する。起動前は SessionManager の
        /// OnGUI(Host/Join) をクリックできる必要があるため、メニュー中はロックしない。
        /// </summary>
        private void Update()
        {
            bool sessionRunning = IsSessionRunning();
            UpdateCursorState(sessionRunning);

            // メニュー中・カーソル解除中は視点を累積しない（誤操作防止）
            if (!sessionRunning || SettingsMenuState.IsGameplayInputBlocked)
                return;

            Vector2 look = _inputActions.Player.Look.ReadValue<Vector2>();

            bool rollModifierPressed = _inputActions.Player.AxisModifierButton.IsPressed();

            // 上下: APR と同じく body/arm を同一デルタで累積し、別々の clamp を適用
            // デフォルト(invertY=false): Look Yが正なら体が上を向く
            float dy = look.y * lookSensitivityY * (invertY ? -1f : 1f);
            float bodyBendLimit = Mathf.Max(0f, profile != null ? profile.bodyBendInputLimit : RagdollProfile.DefaultBodyBendInputLimit);
            float armReachLimit = Mathf.Max(0f, profile != null ? profile.reachArmInputLimit : RagdollProfile.DefaultReachArmInputLimit);
            _bodyBend = Mathf.Clamp(_bodyBend + dy, -bodyBendLimit, bodyBendLimit);

            // reach突入フレームで基準をゼロへリセットしてから今フレーム分のdyを積む。
            // こうしないと、前回reach終了時点の累積値（reach外での視点移動も含む）が
            // そのまま残り、次のreach開始時に腕が正面を向かない。
            bool reachActive = _inputActions.Player.LeftGrab.IsPressed() || _inputActions.Player.RightGrab.IsPressed();
            if (reachActive && !_reachActive)
                _armReach = 0f;
            _reachActive = reachActive;

            _armReach = Mathf.Clamp(_armReach + dy, -armReachLimit, armReachLimit);

            float rollLimit = Mathf.Max(0f, profile != null ? profile.bodyRollInputLimitDegrees : RagdollProfile.DefaultBodyRollInputLimitDegrees);
            _bodyRoll = CalculateBodyRoll(
                _bodyRoll,
                look.x,
                lookSensitivityX,
                rollLimit,
                rollModifierPressed);

            // 両手持ち中: Look X はカメラ旋回ではなくボディヨーへ（OrbitCamera 側も同条件で旋回を止める）。
            // Alt 押下中はロール操作なのでヨーには累積しない。
            UpdateTwoHandedBodyYaw(look.x, rollModifierPressed);
        }

        /// <summary>
        /// 両手持ち状態の検出とボディヨーの累積。
        /// 突入フレームで現在の体の向きから _bodyYaw を初期化し、スナップを防ぐ。
        /// </summary>
        private void UpdateTwoHandedBodyYaw(float lookDeltaX, bool rollModifierPressed)
        {
            ILocalPlayerViewSource view = ResolveLocalView();
            bool twoHanded = view != null && view.IsTwoHandedHold;

            if (twoHanded)
            {
                if (!_twoHandedHoldActive)
                {
                    Vector3 forward = view.FacingForward;
                    _bodyYaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
                }

                if (!rollModifierPressed)
                {
                    _bodyYaw += lookDeltaX * bodyYawSensitivity; // Limit なし（無制限に回せる）
                }
            }

            _twoHandedHoldActive = twoHanded;
        }

        /// <summary>
        /// 入力権限を持つローカルプレイヤーのビュー。未バインド・destroy 済みなら null。
        /// </summary>
        private static ILocalPlayerViewSource ResolveLocalView()
        {
            ILocalPlayerViewSource view = LocalPlayerCameraBinder.LocalView;
            if (LocalPlayerViewUtil.IsDestroyedOrMissing(view) || !view.HasInputAuthority)
                return null;
            return view;
        }

        internal static float CalculateBodyRoll(
            float currentRoll,
            float lookDeltaX,
            float sensitivity,
            float rollLimit,
            bool rollModifierPressed)
        {
            if (!rollModifierPressed)
                return 0f;

            float limit = Mathf.Max(0f, rollLimit);
            return Mathf.Clamp(currentRoll + lookDeltaX * sensitivity, -limit, limit);
        }

        private bool IsSessionRunning()
        {
            foreach (var runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                    return true;
            }
            return false;
        }

        private void UpdateCursorState(bool sessionRunning)
        {
            // セッション前または設定画面表示中はカーソルを表示する。
            if (!sessionRunning || !lockCursor || SettingsMenuState.IsGameplayInputBlocked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                return;
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        #region INetworkRunnerCallbacks (入力のみ)

        public void OnInput(NetworkRunner runner, NetworkInput input)
        {
            var data = new NetworkInputData();

            if (SettingsMenuState.IsGameplayInputBlocked)
            {
                input.Set(data);
                return;
            }

            // --- 移動はカメラ基準（カメラはほぼ固定なので実質ワールド基準） ---
            Vector2 moveInput = _inputActions.Player.Move.ReadValue<Vector2>();
            Vector3 camForward = Vector3.forward;
            Vector3 camRight = Vector3.right;
            if (_mainCamera == null)
                _mainCamera = UnityEngine.Camera.main;
            var cam = _mainCamera;
            if (cam != null)
            {
                camForward = cam.transform.forward;
                camForward.y = 0f;
                camForward.Normalize();
                camRight = cam.transform.right;
                camRight.y = 0f;
                camRight.Normalize();
            }
            Vector3 move = (camForward * moveInput.y) + (camRight * moveInput.x);
            data.direction = move;

            // --- facingDirection: 両手持ち中はLook X由来のボディヨー / 通常は進行方向 ---
            if (_twoHandedHoldActive)
            {
                // 両手持ち: Look Xで累積したヨー角の方向を向く（移動方向より優先）
                data.facingDirection = Quaternion.Euler(0f, _bodyYaw, 0f) * Vector3.forward;
            }
            else if (move.sqrMagnitude > 0.0001f)
            {
                // 移動中: 進行方向へスムーズに向き直る（UpdateRootRotation が turnSpeed で回頭）
                data.facingDirection = move.normalized;
            }
            else
            {
                // 静止中: zero を送ると UpdateRootRotation は現在の向きを維持する
                data.facingDirection = Vector3.zero;
            }

            // --- 体の姿勢入力（絶対値）---
            // bodyDir.x = 胴体ベンド, bodyDir.y = 腕リーチ上下, bodyRoll = Alt+Look X胴体ロール角。
            data.bodyDir = new Vector2(_bodyBend, _armReach);
            data.bodyRoll = _bodyRoll;

            // --- ボタン入力 ---
            if (_inputActions.Player.LeftGrab.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonMouse0);

            if (_inputActions.Player.RightGrab.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonMouse1);

            if (_inputActions.Player.Jump.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonJump);

            if (_inputActions.Player.Dash.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonDash);

            if (_inputActions.Player.Crouch.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonCrouch);

            if (_inputActions.Player.LeftPunch.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonLeftpunch);

            if (_inputActions.Player.RightPunch.IsPressed())
                data.Buttons.SetDown(ButtonUtils.ButtonRightpunch);

            input.Set(data);
        }

        #endregion

        #region INetworkRunnerCallbacks (未使用 - 他クラスの責務)

        public void OnInputMissing(NetworkRunner runner, PlayerRef player, NetworkInput input) { }
        public void OnPlayerJoined(NetworkRunner runner, PlayerRef player) { }
        public void OnPlayerLeft(NetworkRunner runner, PlayerRef player) { }
        public void OnConnectedToServer(NetworkRunner runner) { }
        public void OnDisconnectedFromServer(NetworkRunner runner, NetDisconnectReason reason) { }
        public void OnConnectRequest(NetworkRunner runner, NetworkRunnerCallbackArgs.ConnectRequest request, byte[] token) { }
        public void OnConnectFailed(NetworkRunner runner, NetAddress remoteAddress, NetConnectFailedReason reason) { }
        public void OnShutdown(NetworkRunner runner, ShutdownReason shutdownReason) { }
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
