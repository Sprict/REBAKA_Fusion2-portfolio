using System;
using Fusion;
using MyFolder.Scripts.Diagnostics;
using MyFolder.Scripts.Network;
using MyFolder.Scripts.Player.Posing;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// Fusion ライフサイクルの入口とサブシステム委譲を担うコンポーネント
    /// </summary>
    public class RagdollController : NetworkBehaviour, IBeforeTick,
        IRagdollRuntimeHost, IClientBootstrapContext, IClientProxyRigAccess, IClientProxyRuntimeContext,
        IHostSimulationContext, IProxyPosePublisherContext, IRagdollStateContext, IRagdollPhysicsContext,
        IRagdollAudioSink, IRagdollGroundingSink, IRagdollTreasureCarryContext, ILocalPlayerViewSource, IProxyPoseSource, IPoseSnapshotAccess
    {
        #region Serialized Fields

        // 設定
        [Header("Settings")][SerializeField] private RagdollProfile profile; // ラグドールの物理パラメータ群（ScriptableObject）

        /// <summary>手側コンポーネント（握りドライブ等）が参照する読み取り専用 Profile。</summary>
        public RagdollProfile Profile => profile;

        [SerializeField] private ActionPoseAsset reachPose; // Reach(到達)アクションの決めポーズ（rest相対デルタ・モデル別に録り直す。未割当なら従来挙動）

        [Header("Debug Visualization")]
        [SerializeField]
        private bool showBalanceGizmos = true; // バランス関連のGizmoを表示するか

        [SerializeField] private bool showDebugGUI = true; // デバッグ用のオンスクリーンGUIを表示するか
        [SerializeField] private Key debugGuiToggleKey = Key.F2; // Balance Debug GUI の表示切替キー
        [SerializeField] private float gizmoSphereRadius = 0.05f; // GizmoSphereの半径
        [SerializeField] private bool useHybridProxySimulation = true; // プロキシをハイブリッド補間で動かすか
        [SerializeField] private bool forceRemoteRenderForAllClientProxies = true; // 全クライアントプロキシにRemoteRenderを強制するか
        [SerializeField] private bool forceRemoteRenderForInputAuthorityOnClient = true; // InputAuthorityクライアントにもRemoteRenderを強制するか
        [SerializeField] private bool relaxClientJointsOnSpawn; // スポーン時にクライアント側のジョイントを緩めるか
        [SerializeField] private bool proxyCorrectHeadAndHands; // プロキシの頭・手を補正するか
        [SerializeField] private bool useLegacyCustomRootCorrection; // 旧来のルートボーン補正を使うか
        [SerializeField] private float proxyRootPositionKp = 25f; // プロキシRoot位置のPゲイン（バネ強度）
        [SerializeField] private float proxyRootVelocityKd = 12f; // プロキシRoot速度のDゲイン（ダンパ強度）
        [SerializeField] private float proxyRootRotationKp = 18f; // プロキシRoot回転のPゲイン
        [SerializeField] private float proxyRootAngularKd = 4f; // プロキシRoot角速度のDゲイン
        [SerializeField] private bool enableRootPosePrediction = true; // Rootポーズの先読み予測を有効にするか
        [SerializeField] private float rootPredictionLeadSeconds = 0.06f; // 通常時の予測先読み秒数
        [SerializeField] private float rootPredictionLeadSecondsDuringResim = 0.01f; // 再シミュレーション中の予測先読み秒数
        [SerializeField] private float maxRootPredictionDistance = 1.5f; // 予測が許容する最大ズレ距離（超えたらリセット）
        [SerializeField] private float proxyPartLerpStrength = 15f; // プロキシ各パーツのLerp補間強度
        [SerializeField] private float proxyHardSnapRootThreshold = 1.0f; // Rootがこの距離を超えたら瞬間スナップする閾値
        [SerializeField] private float proxyHardSnapPartThreshold = 0.6f; // 各パーツがこの距離を超えたら瞬間スナップする閾値
        [SerializeField] private float proxyHardSnapHoldSeconds = 0.25f; // スナップ後にLerpを再開するまでの待機秒数

        // 参照系
        [Header("Component References")]
        [SerializeField] private GameObject[] bodyParts; // 全身のBodyPartオブジェクト配列

        [SerializeField] private Rigidbody[] bodyRigidbodies; // 全身のRigidbody配列
        [SerializeField] private ConfigurableJoint[] bodyJoints; // 全身のConfigurableJoint配列

        [Header("Arm Connections")]
        [SerializeField]
        private Rigidbody lowerLeftArmRb; // 左前腕のRigidbody

        [SerializeField] private Rigidbody lowerRightArmRb; // 右前腕のRigidbody

        [Header("Hand Connections")]
        [SerializeField] private Rigidbody rightHand; // 右手のRigidbody

        [SerializeField] private Rigidbody leftHand; // 左手のRigidbody

        [SerializeField] private Transform centerOfMassPoint; // 重心位置の基準Transform

        [Header("Audio")][SerializeField] private AudioSource soundSource; // SE再生用AudioSource

        #endregion

        #region Private Fields

        // サブシステム
        private RagdollInput _ragdollInput; // 入力収集サブシステム
        private RagdollState _ragdollState; // 状態管理サブシステム
        private RagdollPhysics _ragdollPhysics; // 物理演算サブシステム
        private RagdollRuntime _runtime; // Fusionランタイム統合
        private RagdollClientBootstrapper _clientBootstrapper; // クライアント初期化処理
        private RagdollStateEvaluator _stateEvaluator; // 状態遷移評価ロジック
        private RagdollAudioPlayer _audioPlayer; // 効果音再生サブシステム
        private RagdollGroundingService _groundingService; // 接地判定サブシステム
        private RagdollRigValidator _rigValidator; // リグ構成の検証
        private RagdollRigInitializer _rigInitializer; // リグの初期化処理
        private RagdollClientJointModeController _clientJointModeController; // クライアント側ジョイントモード制御
        private RagdollRigSetup _rigSetup; // リグセットアップ処理
        private RagdollClientProxyRuntime _clientProxyRuntime; // クライアントプロキシのランタイム処理
        private RagdollHostSimulationOrchestrator _hostSimulation; // ホスト側シミュレーション統括
        private RagdollProxyPosePublisher _proxyPosePublisher; // プロキシポーズのネットワーク発信
        private RagdollDiagnosticsReporter _diagnosticsReporter; // デバッグ情報収集・報告
        private RagdollDebugView _debugView; // デバッグ表示
        private int _treasureGrabRefCount; // Treasureを掴んでいる手の数（ローカルStateAuthority用）
        private ConfigurableJoint _carryHarnessJoint; // Treasure運搬中にRootとTreasureをつなぐ距離制限ジョイント
        private Rigidbody _carryHarnessTreasureRigidbody; // 現在の運搬ハーネスが接続しているTreasure

        // 階層全体の物理コンポーネント（クライアント側の強制Kinematic対象）
        private Rigidbody[] _allRigidbodies; // 階層下の全Rigidbody（kinematic強制切り替え用）
        private ConfigurableJoint[] _allConfigurableJoints; // 階層下の全ConfigurableJoint
        private bool _proxyBootstrapApplied; // プロキシ初期化が完了したか

        private Rigidbody _rootRigidbody; // 胴体（ルート）のRigidbody
        private Rigidbody _headRigidbody; // 頭のRigidbody
        private Rigidbody _leftHandRigidbody; // 左手のRigidbody（実行時参照）
        private Rigidbody _rightHandRigidbody; // 右手のRigidbody（実行時参照）
        private Component _rootNetworkRigidbody; // ルートのNetworkRigidbodyコンポーネント
        private Renderer[] _proxyRenderers; // プロキシ表示用のRenderer配列

        #endregion

        #region Networked Properties

        // ネットワーク同期変数
        [Networked] public PlayerState CurrentState { get; set; } // 現在のプレイヤー状態（Standing/Fallenなど）。外部から参照される
        [Networked] private Vector3 MoveDirection { get; set; } // 入力移動方向（ワールド空間）
        [Networked] private Vector3 FacingDirection { get; set; } // キャラクターが向いている方向
        [Networked] private Vector2 LookDirection { get; set; } // 体のピッチ/リーチ入力
        [Networked] private float BodyRoll { get; set; } // Alt+MouseX由来の胴体ロール角(度)

        // 足の接地状態
        [Networked] private NetworkBool IsLeftFootGrounded { get; set; } // 左足が地面に触れているか
        [Networked] private NetworkBool IsRightFootGrounded { get; set; } // 右足が地面に触れているか
        [Networked] private NetworkBool NetProxyPoseInitialized { get; set; } // プロキシの初期ポーズが同期済みか

        [Networked] private Vector3 NetRootPosition { get; set; } // ルート胴体の位置（プロキシ補間用）
        [Networked] private Quaternion NetRootRotation { get; set; } // ルート胴体の回転（プロキシ補間用）
        [Networked] private Vector3 NetRootLinearVelocity { get; set; } // ルートの線速度（予測補間用）
        [Networked] private Vector3 NetRootAngularVelocity { get; set; } // ルートの角速度（予測補間用）
        [Networked] private Vector3 NetHeadPosition { get; set; } // 頭の位置（プロキシ補間用）
        [Networked] private Quaternion NetHeadRotation { get; set; } // 頭の回転（プロキシ補間用）
        [Networked] private Vector3 NetLeftHandPosition { get; set; } // 左手の位置（プロキシ補間用）
        [Networked] private Quaternion NetLeftHandRotation { get; set; } // 左手の回転（プロキシ補間用）
        [Networked] private Vector3 NetRightHandPosition { get; set; } // 右手の位置（プロキシ補間用）
        [Networked] private Quaternion NetRightHandRotation { get; set; } // 右手の回転（プロキシ補間用）

        // 全身ポーズ同期（SnapshotInterpolation モード用）
        // bodyRigidbodies[1..14] の Root 相対ポーズをスロット 0..13 に格納
        [Networked, Capacity(RagdollPoseSync.RelativePartCount)]
        private NetworkArray<Vector3> NetPartPositions => default;

        [Networked, Capacity(RagdollPoseSync.RelativePartCount)]
        private NetworkArray<Quaternion> NetPartRotations => default;

        // テレポート検出キー。リスポーン等の瞬間移動時にインクリメントし、
        // クライアントが補間を跨がないようにする（NetworkRigidbody の TeleportKey と同パターン）
        [Networked] private int NetPoseTeleportKey { get; set; } // リスポーン等の瞬間移動を検出するカウンター

        #endregion

        #region Snapshot Readers

        // Render() でスナップショットバッファから補間値を読むためのリーダー（クライアント側のみ使用）
        private bool _snapshotReadersInitialized; // スナップショットリーダーの初期化済みフラグ
        private PropertyReader<Vector3> _rootPositionReader; // NetRootPositionの補間値リーダー
        private PropertyReader<Quaternion> _rootRotationReader; // NetRootRotationの補間値リーダー
        private PropertyReader<int> _poseTeleportKeyReader; // NetPoseTeleportKeyの補間値リーダー
        private PropertyReader<NetworkBool> _poseInitializedReader; // NetProxyPoseInitializedの補間値リーダー
        private ArrayReader<Vector3> _partPositionsReader; // NetPartPositionsの補間値リーダー
        private ArrayReader<Quaternion> _partRotationsReader; // NetPartRotationsの補間値リーダー

        private void EnsureSnapshotReaders()
        {
            if (_snapshotReadersInitialized)
            {
                return;
            }

            _rootPositionReader = GetPropertyReader<Vector3>(nameof(NetRootPosition));
            _rootRotationReader = GetPropertyReader<Quaternion>(nameof(NetRootRotation));
            _poseTeleportKeyReader = GetPropertyReader<int>(nameof(NetPoseTeleportKey));
            _poseInitializedReader = GetPropertyReader<NetworkBool>(nameof(NetProxyPoseInitialized));
            _partPositionsReader = GetArrayReader<Vector3>(nameof(NetPartPositions));
            _partRotationsReader = GetArrayReader<Quaternion>(nameof(NetPartRotations));
            _snapshotReadersInitialized = true;
        }

        #endregion

        #region Unity Lifecycle Methods

        public override void Spawned()
        {
            try
            {
                // ラグドールリグの構成を検証。問題がある場合はエラーを出して以降の処理を中断する。
                if (!EnsureRigValidator().Validate(bodyParts, rightHand, leftHand, centerOfMassPoint, soundSource))
                {
                    Debug.LogError("Validation failed. Disabling component.");
                    this.enabled = false;
                    return;
                }

                // ポーズ同期はインデックス 0..14 の 15 パーツを前提とする。装飾 RB が混入すると
                // クライアントで装飾まで kinematic 化され本体から分離する（2026-06-12 の障害）
                int expectedBodyCount =
                    RagdollPoseSync.FirstRelativePartIndex + RagdollPoseSync.RelativePartCount;
                if (bodyRigidbodies == null || bodyRigidbodies.Length != expectedBodyCount)
                {
                    Debug.LogError(
                        $"bodyRigidbodies must contain exactly {expectedBodyCount} pose-synced rigidbodies " +
                        $"(got {bodyRigidbodies?.Length ?? 0}). " +
                        "Decorative rigidbodies (Other/Sphere etc.) must NOT be registered here. " +
                        "If this appears after pulling the 2026-06-12 prefab fix, the editor may still hold " +
                        "the old import: reimport newAPRPlayer.prefab (or restart the editor). " +
                        "Note: Console 'Error Pause' will freeze the whole simulation on this error.",
                        this);
                }

                CacheHierarchyPhysicsComponents();
                CacheProxyBodyReferences();
                CacheProxyRenderers();
                // Hybrid モード以外は ClientProxyCorrection の bootstrap を使わない
                _proxyBootstrapApplied = Object.HasStateAuthority || !UseHybridProxySimulation ||
                                         ResolvedProxySyncMode != ProxySyncMode.Hybrid;

                _runtime = new RagdollRuntime(this);
                _clientBootstrapper = new RagdollClientBootstrapper(this);
                _stateEvaluator = new RagdollStateEvaluator();
                _audioPlayer = new RagdollAudioPlayer(profile, soundSource);
                _groundingService = new RagdollGroundingService();
                _rigValidator = new RagdollRigValidator();
                _rigInitializer = new RagdollRigInitializer();
                _clientJointModeController = new RagdollClientJointModeController(this, GetInstanceID);
                _rigSetup = new RagdollRigSetup();
                _proxyPosePublisher = new RagdollProxyPosePublisher(this);
                _diagnosticsReporter = new RagdollDiagnosticsReporter();
                _debugView = new RagdollDebugView();
                _runtime.InitializeCore();
                _clientProxyRuntime = new RagdollClientProxyRuntime(this);
                _clientProxyRuntime.Initialize();
                _hostSimulation = new RagdollHostSimulationOrchestrator(this, this);

                if (!Object.HasStateAuthority)
                {
                    _clientBootstrapper.Initialize();
                }

                // 同一ラグドール内のコライダー間衝突を無効化（振動防止）
                EnsureRigInitializer().SetupCollisionIgnores(GetComponentsInChildren<Collider>());

                // パーツ→所有者の対応を登録（掴み判定の自他識別用）。
                // DetachRootFromParent() で階層が切れる前に登録する必要がある。
                RagdollBodyOwnerRegistry.Register(GetComponentsInChildren<Rigidbody>(true), Object);

                // 親オブジェクトの Transform が毎 Tick 上書きする干渉を防ぐため
                // APR_Root（bodyRigidbodies[0]）をワールド直下に移動させる
                // SnapshotInterpolation モードのクライアントも Root をワールド座標で直接書くため detach する
                if (Object.HasStateAuthority || !UseHybridProxySimulation ||
                    ResolvedProxySyncMode == ProxySyncMode.SnapshotInterpolation)
                {
                    DetachRootFromParent();
                }

                if (Object.HasStateAuthority)
                {
                    // 初回ティック前にも粗同期スナップショットを持たせる。
                    EnsureProxyPosePublisher().Publish();
                    // 以降のポーズ発行は Physics.Simulate() 完了後に行う（掴み隙間バグ対策）。
                    SubscribeHostSimulationEvents();
                }

                LocalPlayerCameraBinder.EnsureAndBind(gameObject, this);
            }
            catch (Exception e)
            {
                Debug.LogError($"Error during player spawn: {e.Message}\n{e.StackTrace}");
                enabled = false;
            }
        }

        void IBeforeTick.BeforeTick()
        {
            if (Object == null || Runner == null || Object.HasStateAuthority)
                return;

            if (_clientProxyRuntime == null)
            {
                _clientProxyRuntime = new RagdollClientProxyRuntime(this);
                _clientProxyRuntime.Initialize();
            }

            int forcedCount = _clientProxyRuntime.RunBeforeTick();
            if (forcedCount > 0)
            {
                RagdollNetDiagnostics.Log(
                    "pre_sim_kinematic",
                    $"role=Client phase=before_tick forced_count={forcedCount} runner_is_resim={Runner.IsResimulation}",
                    this,
                    0.2f,
                    $"pre_sim_kinematic_{GetInstanceID()}");
            }
        }

        public override void FixedUpdateNetwork()
        {
            if (!Object.HasStateAuthority)
            {
                if (_clientProxyRuntime == null)
                {
                    _clientProxyRuntime = new RagdollClientProxyRuntime(this);
                    _clientProxyRuntime.Initialize();
                }

                _clientProxyRuntime.RunFixedUpdate();
                return;
            }

            if (_hostSimulation == null)
            {
                _hostSimulation = new RagdollHostSimulationOrchestrator(this, this);
            }

            _hostSimulation.RunFixedUpdate();
        }

        private void Update()
        {
            if (Object == null || !Object.HasInputAuthority)
                return;

            Keyboard keyboard = Keyboard.current;
            bool togglePressedThisFrame = keyboard != null && keyboard[debugGuiToggleKey].wasPressedThisFrame;
            showDebugGUI = RagdollDebugView.ResolveGuiVisibility(showDebugGUI, togglePressedThisFrame);
        }

        public override void Render()
        {
            if (!Object.HasStateAuthority)
            {
                if (_clientProxyRuntime == null)
                {
                    _clientProxyRuntime = new RagdollClientProxyRuntime(this);
                    _clientProxyRuntime.Initialize();
                }

                _clientProxyRuntime.RunRender();
            }
            else
            {
                EmitSyncDiagnostics("render");
            }
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            // RunnerSimulatePhysics は Runner と同寿命のため、自分が先に消える際に
            // イベント購読を解除しないと破棄済みオブジェクトへの呼び出しが残る
            UnsubscribeDecorationSimulationEvents();
            UnsubscribeHostSimulationEvents();

            DestroyCarryHarness();
            _treasureGrabRefCount = 0;

            RagdollBodyOwnerRegistry.Unregister(Object);

            // DetachRootFromParent()で親から切り離されたAPR_Rootは
            // 親オブジェクトのDestroyに巻き込まれないため、明示的に破棄する
            if (bodyRigidbodies != null && bodyRigidbodies.Length > 0 && bodyRigidbodies[0] != null)
            {
                Destroy(bodyRigidbodies[0].gameObject);
            }
        }

        #endregion

        #region State Management

        /// <summary>
        /// 入力に基づいてプレイヤーの状態を更新
        /// </summary>
        private void UpdatePlayerState(RagdollCommand command, RagdollPhysics physics)
        {
            if (_stateEvaluator == null)
            {
                _stateEvaluator = new RagdollStateEvaluator();
            }

            CurrentState = _stateEvaluator.Resolve(command, physics, MoveDirection, IsPlayerGrounded());
        }

        #endregion

        #region Setup Methods

        /// <summary>
        /// プレイヤー階層内の物理コンポーネントをキャッシュする。
        /// </summary>
        private void CacheHierarchyPhysicsComponents()
        {
            EnsureRigSetup().CacheHierarchyPhysicsComponents(this, out _allRigidbodies, out _allConfigurableJoints);
        }

        /// <summary>
        /// プロキシボディの参照をキャッシュする。
        /// </summary>
        private void CacheProxyBodyReferences()
        {
            EnsureRigSetup().CacheProxyBodyReferences(
                bodyRigidbodies,
                out _rootRigidbody,
                out _headRigidbody,
                out _rightHandRigidbody,
                out _leftHandRigidbody,
                out _rootNetworkRigidbody);
        }

        /// <summary>
        /// プロキシ描画用の Renderer 群をキャッシュする。
        /// </summary>
        private void CacheProxyRenderers()
        {
            _proxyRenderers = EnsureRigSetup().CacheProxyRenderers(this);
        }

        /// <summary>
        /// プロキシ描画用の Renderer 群を有効化または無効化する。
        /// </summary>
        /// <param name="enabled">有効化する場合は true、無効化する場合は false。</param>
        private void SetProxyVisualsEnabled(bool enabled)
        {
            EnsureRigSetup().SetProxyVisualsEnabled(this, ref _proxyRenderers, enabled);
        }

        /// <summary>
        /// ルートボディを親から切り離す。
        /// </summary>
        private void DetachRootFromParent()
        {
            EnsureRigSetup().DetachRootFromParent(bodyRigidbodies);
        }

        // FixHandHierarchy() は削除済み
        // 理由: transform.Find() は直接の子しか検索せず、
        //       DetachRootFromParent() 後は階層が変わるためサイレント失敗していた。
        //       手の接続は SetupHandJoints() の connectedBody + Locked Motion で保証される。

        private RagdollRigSetup EnsureRigSetup()
        {
            if (_rigSetup == null)
            {
                _rigSetup = new RagdollRigSetup();
            }

            return _rigSetup;
        }

        /// <summary>
        /// リグ構成の検証を行う。
        /// </summary>
        private RagdollRigValidator EnsureRigValidator()
        {
            if (_rigValidator == null)
            {
                _rigValidator = new RagdollRigValidator();
            }

            return _rigValidator;
        }

        /// <summary>
        /// リグの初期化を行う。
        /// </summary>
        private RagdollRigInitializer EnsureRigInitializer()
        {
            if (_rigInitializer == null)
            {
                _rigInitializer = new RagdollRigInitializer();
            }

            return _rigInitializer;
        }

        /// <summary>
        /// クライアント側ジョイントモード制御を行う。
        /// </summary>
        private RagdollClientJointModeController EnsureClientJointModeController()
        {
            if (_clientJointModeController == null)
            {
                _clientJointModeController = new RagdollClientJointModeController(this, GetInstanceID);
            }

            return _clientJointModeController;
        }

        private RagdollProxyPosePublisher EnsureProxyPosePublisher()
        {
            if (_proxyPosePublisher == null)
            {
                _proxyPosePublisher = new RagdollProxyPosePublisher(this);
            }

            return _proxyPosePublisher;
        }

        private RagdollDiagnosticsReporter EnsureDiagnosticsReporter()
        {
            if (_diagnosticsReporter == null)
            {
                _diagnosticsReporter = new RagdollDiagnosticsReporter();
            }

            return _diagnosticsReporter;
        }

        #endregion

        #region Client Proxy Logic

        /// <summary>
        /// クライアント用: 全ConfigurableJointのドライブを無効化
        /// スプリングフォースが振動の源になるのを防ぐ（Gang Beasts方式）
        /// </summary>
        private void DisableClientJointDrives()
        {
            EnsureClientJointModeController().DisableJointDrives(GetDriveTargetJoints());
        }

        /// <summary>
        /// クライアント（非StateAuthority）ではジョイント拘束を解除し、受信姿勢との干渉を防ぐ。
        /// ConfigurableJoint は Component のため enabled を持たないので、拘束自体を Free 化する。
        /// </summary>
        private void DisableClientJoints()
        {
            EnsureClientJointModeController().DisableJoints(GetDriveTargetJoints());
        }

        /// <summary>
        /// クライアント側で制御対象にすべき剛体群を取得する。
        /// bodyRigidbodies だけでは未登録Rigidbody（Sphere等）が漏れるため、
        /// 階層全体のRigidbodyを優先する。
        /// </summary>
        private Rigidbody[] GetKinematicTargetRigidbodies()
        {
            if (_allRigidbodies is { Length: > 0 })
            {
                return _allRigidbodies;
            }

            return bodyRigidbodies;
        }

        /// <summary>
        /// クライアント側でドライブ無効化対象にすべきジョイント群を取得する。
        /// </summary>
        private ConfigurableJoint[] GetDriveTargetJoints()
        {
            if (_allConfigurableJoints is { Length: > 0 })
            {
                return _allConfigurableJoints;
            }

            return bodyJoints;
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// 同期診断情報を発行する。
        /// </summary>
        /// <param name="phase">診断フェーズ。</param>
        private void EmitSyncDiagnostics(string phase)
        {
            Rigidbody[] rigidbodies = GetKinematicTargetRigidbodies();
            EnsureDiagnosticsReporter().EmitSyncDiagnostics(
                phase,
                this,
                Runner,
                Object,
                rigidbodies,
                UseHybridProxySimulation,
                _proxyBootstrapApplied,
                enableRootPosePrediction,
                _rootNetworkRigidbody != null,
                useLegacyCustomRootCorrection);
        }

        #endregion

        #region Interaction Methods

        /// <summary>
        /// プレイヤーが接地しているかどうかを取得する。
        /// </summary>
        /// <returns>接地している場合は true、接地していない場合は false。</returns>
        public bool IsPlayerGrounded()
        {
            return IsLeftFootGrounded || IsRightFootGrounded;
        }

        #endregion

        #region Composition Contracts

        void IRagdollRuntimeHost.SetupHandJoints()
        {
            EnsureRigInitializer().SetupHandJoints(leftHand, rightHand, lowerLeftArmRb, lowerRightArmRb);
        }

        void IRagdollRuntimeHost.InitializeRigidbodies()
        {
            bool hasStateAuthority = Object != null && Object.HasStateAuthority;

            // SnapshotInterpolation のクライアントは装飾 RB（Other/ 配下の Sphere 等）を
            // 各ピアのローカル物理で揺らすため、kinematic 初期化の対象を
            // ポーズ同期対象の 15 パーツに限定する（装飾はプレハブの dynamic 設定のまま残す）
            Rigidbody[] kinematicTargets =
                !hasStateAuthority && ResolvedProxySyncMode == ProxySyncMode.SnapshotInterpolation
                    ? bodyRigidbodies
                    : GetKinematicTargetRigidbodies();

            EnsureRigInitializer().InitializeRigidbodies(
                hasStateAuthority,
                bodyRigidbodies,
                kinematicTargets);
        }

        /// <summary>
        /// サブシステムを設定する。
        /// </summary>
        /// <param name="input">入力サブシステム。</param>
        /// <param name="state">状態サブシステム。</param>
        /// <param name="physics">物理サブシステム。</param>
        void IRagdollRuntimeHost.SetSubsystems(RagdollInput input, RagdollState state, RagdollPhysics physics)
        {
            _ragdollInput = input;
            _ragdollState = state;
            _ragdollPhysics = physics;
        }

        /// <summary>
        /// ポーズオーサリングツール用: Reach ポーズのプレビューを走行中の物理サブシステムへ橋渡しする。
        /// Play モードでのみ有効（_ragdollPhysics 生成後）。
        /// </summary>
        public void SetReachPosePreview(bool active, ActionPoseAsset asset, bool isRight)
        {
            _ragdollPhysics?.SetReachPosePreview(active, asset, isRight);
        }

        IRagdollStateContext IRagdollRuntimeHost.StateContext => this;
        IRagdollPhysicsContext IRagdollRuntimeHost.PhysicsContext => this;

        bool IClientBootstrapContext.HasInputAuthority => Object != null && Object.HasInputAuthority;
        bool IClientBootstrapContext.HasStateAuthority => Object != null && Object.HasStateAuthority;
        bool IClientBootstrapContext.ForceRemoteForAllClientProxies => forceRemoteRenderForAllClientProxies;
        bool IClientBootstrapContext.ForceRemoteForInputAuthorityOnClient => forceRemoteRenderForInputAuthorityOnClient;
        int IClientBootstrapContext.InstanceId => GetInstanceID();

        void IClientBootstrapContext.SetForceRemoteRenderTimeframe(bool value)
        {
            if (Object != null)
            {
                Object.ForceRemoteRenderTimeframe = value;
            }
        }

        IClientProxyModeStrategy IClientBootstrapContext.CreateClientProxyModeStrategy()
        {
            switch (ResolvedProxySyncMode)
            {
                // Forecast Physicsモード: ジョイントドライブを維持（無効化しない）
                case ProxySyncMode.Forecast:
                    return new ForecastClientProxyModeStrategy(this);

                // 全身ポーズのスナップショット補間モード
                case ProxySyncMode.SnapshotInterpolation:
                    return new SnapshotInterpolationClientProxyModeStrategy(this, this);

                default:
                    return UseHybridProxySimulation
                        ? new HybridClientProxyModeStrategy(this, this)
                        : new LegacyClientProxyModeStrategy(this, this);
            }
        }

        void IClientBootstrapContext.LogClientBootstrap(string key, string message, float throttle, string dedupeKey)
        {
            RagdollNetDiagnostics.Log(key, message, this, throttle, dedupeKey);
        }

        void IClientBootstrapContext.LogClientDebug(string message)
        {
            Debug.Log(message, this);
        }

        void IClientBootstrapContext.LogClientWarning(string message)
        {
            Debug.LogWarning(message, this);
        }

        bool IClientProxyRigAccess.RelaxClientJointsOnSpawn => relaxClientJointsOnSpawn;
        bool IClientProxyRigAccess.HasRootNetworkRigidbody => _rootNetworkRigidbody != null;
        bool IClientProxyRigAccess.UseLegacyCustomRootCorrection => useLegacyCustomRootCorrection;

        void IClientProxyRigAccess.DisableClientJointDrives()
        {
            DisableClientJointDrives();
        }

        void IClientProxyRigAccess.DisableClientJoints()
        {
            DisableClientJoints();
        }

        void IClientProxyRigAccess.SetProxyVisualsEnabled(bool enabled)
        {
            SetProxyVisualsEnabled(enabled);
        }

        void IClientProxyRigAccess.DisableRootNetworkRigidbody()
        {
            // SnapshotInterpolation モードでは Root NetworkRigidbody の Render 補間が
            // 当方の transform 書き込みと競合（二重書き込み）するため無効化する
            if (_rootNetworkRigidbody is UnityEngine.Behaviour behaviour && behaviour.enabled)
            {
                behaviour.enabled = false;
                Debug.Log("[RAGDOLL_CLIENT_MODE] Root NetworkRigidbody disabled (snapshot interpolation mode)", this);
            }
        }

        ProxySyncMode IClientProxyRuntimeContext.SyncMode => ResolvedProxySyncMode;
        bool IClientProxyRuntimeContext.UseHybridProxySimulation => UseHybridProxySimulation;
        bool IClientProxyRuntimeContext.UseForecastPhysics => ResolvedProxySyncMode == ProxySyncMode.Forecast;
        bool IClientProxyRuntimeContext.HasInputAuthority => Object != null && Object.HasInputAuthority;
        bool IClientProxyRuntimeContext.ProxyBootstrapApplied
        {
            get => _proxyBootstrapApplied;
            set => _proxyBootstrapApplied = value;
        }

        float IClientProxyRuntimeContext.DeltaTime => Runner != null ? Runner.DeltaTime : 0f;
        PlayerState IClientProxyRuntimeContext.CurrentState => CurrentState;
        Vector3 IClientProxyRuntimeContext.MoveDirection => MoveDirection;
        Vector3 IClientProxyRuntimeContext.FacingDirection => FacingDirection;
        Vector2 IClientProxyRuntimeContext.LookDirection => LookDirection;
        float IClientProxyRuntimeContext.BodyRoll => BodyRoll;
        Transform IClientProxyRuntimeContext.ProxyFacingFallbackTransform => _rootRigidbody != null ? _rootRigidbody.transform : transform;
        RagdollInput IClientProxyRuntimeContext.InputHandler => _ragdollInput;
        RagdollPhysics IClientProxyRuntimeContext.PhysicsHandler => _ragdollPhysics;
        Rigidbody[] IClientProxyRuntimeContext.KinematicTargetRigidbodies => GetKinematicTargetRigidbodies();
        Rigidbody[] IClientProxyRuntimeContext.PoseDrivenRigidbodies => bodyRigidbodies;

        bool IClientProxyRuntimeContext.TryGetInput(out NetworkInputData data)
        {
            return GetInput(out data);
        }

        void IClientProxyRuntimeContext.EmitSyncDiagnostics(string phase)
        {
            EmitSyncDiagnostics(phase);
        }

        RagdollSnapshotPoseInterpolator IClientProxyRuntimeContext.CreateSnapshotPoseInterpolator()
        {
            var interpolator = new RagdollSnapshotPoseInterpolator(this);

            // Spawned はホストでも無条件に Initialize() を呼ぶため、interpolator の
            // OnBeforeSimulate 購読（物理前イベント）はここで権限チェック必須。
            // ホストで購読すると: 毎 tick 物理前に interpolator が「前フレームの
            // スナップショット」をポーズとして復元する → dynamic 本体の velocity による
            // 移動が毎フレーム巻き戻される → 見た目上まったく動かなくなる。
            // （2026-06-12 実測: 23.8m/4s → 2.9m/4s に急落）
            if (Object != null && !Object.HasStateAuthority)
            {
                SubscribeDecorationSimulationEvents(interpolator);
            }

            return interpolator;
        }

        private Fusion.Addons.Physics.RunnerSimulatePhysics _decorationSimulateHook;
        private RagdollSnapshotPoseInterpolator _decorationInterpolator;

        private Fusion.Addons.Physics.RunnerSimulatePhysics _hostSimulateHook;

        /// <summary>
        /// ゲーム体験に影響のない装飾RigidBodyの描画補間を物理ステップの前後イベントに接続する。
        /// 物理姿勢の記録（After）と復元（Before）は Physics.Simulate の実行タイミングに
        /// 正確に同期する必要があるため、FixedUpdateNetwork の実行順に依存しない
        /// RunnerSimulatePhysics のイベントを使う。未配線でも interpolator 側が
        /// 従来の退避→復元動作にフォールバックするため分離は起きない。
        /// </summary>
        private void SubscribeDecorationSimulationEvents(RagdollSnapshotPoseInterpolator interpolator)
        {
            UnsubscribeDecorationSimulationEvents();

            if (Runner == null ||
                !Runner.TryGetComponent(out Fusion.Addons.Physics.RunnerSimulatePhysics simulatePhysics))
            {
                Debug.LogWarning(
                    "[SNAPSHOT_DECO] RunnerSimulatePhysics not found on Runner; " +
                    "decoration render interpolation disabled (falls back to stash/restore)",
                    this);
                return;
            }

            _decorationInterpolator = interpolator;
            _decorationSimulateHook = simulatePhysics;
            simulatePhysics.OnBeforeSimulate += OnBeforePhysicsSimulate;
            simulatePhysics.OnAfterSimulate += OnAfterPhysicsSimulate;

            // クライアント実機テストで補間経路の生死を切り分けるためのログ。
            // simulator の型も出す: NoResimulationSimulatePhysics でなければ
            // resim 多重 Simulate ガードが効いていない（旧 RunnerSimulatePhysics が先に登録された）
            Debug.Log(
                "[SNAPSHOT_DECO] simulation events subscribed " +
                $"(decoration render interpolation active, simulator={simulatePhysics.GetType().Name})",
                this);
        }

        private void UnsubscribeDecorationSimulationEvents()
        {
            if (_decorationSimulateHook != null)
            {
                _decorationSimulateHook.OnBeforeSimulate -= OnBeforePhysicsSimulate;
                _decorationSimulateHook.OnAfterSimulate -= OnAfterPhysicsSimulate;
                _decorationSimulateHook = null;
            }

            _decorationInterpolator = null;
        }

        private void OnBeforePhysicsSimulate(NetworkRunner runner)
        {
            _decorationInterpolator?.OnBeforeSimulate();
        }

        private void OnAfterPhysicsSimulate(NetworkRunner runner)
        {
            _decorationInterpolator?.OnAfterSimulate();
        }

        private void SubscribeHostSimulationEvents()
        {
            UnsubscribeHostSimulationEvents();

            if (Runner == null ||
                !Runner.TryGetComponent(out Fusion.Addons.Physics.RunnerSimulatePhysics simulatePhysics))
            {
                Debug.LogWarning(
                    "[HOST_POSE_PUBLISH] RunnerSimulatePhysics not found; " +
                    "pose snapshot will not be published post-physics.",
                    this);
                return;
            }

            _hostSimulateHook = simulatePhysics;
            simulatePhysics.OnAfterSimulate += OnHostAfterPhysicsSimulate;
        }

        private void UnsubscribeHostSimulationEvents()
        {
            if (_hostSimulateHook != null)
            {
                _hostSimulateHook.OnAfterSimulate -= OnHostAfterPhysicsSimulate;
                _hostSimulateHook = null;
            }
        }

        // Physics.Simulate() 完了後にボーン位置を発行する。
        // FixedJoint で拘束された Cube と手が同一の POST-physics 座標になるため
        // クライアント補間時に両者の位置が一致し、掴み中の隙間バグが解消される。
        // NoResimulationSimulatePhysics が resim 中の Simulate を止めるため
        // このコールバックは forward tick のみ発火する。
        private void OnHostAfterPhysicsSimulate(NetworkRunner runner)
        {
            EnsureProxyPosePublisher().Publish();
        }

        ClientProxyCorrection IClientProxyRuntimeContext.CreateClientProxyCorrection()
        {
            RagdollRigSetup rigSetup = EnsureRigSetup();
            return new ClientProxyCorrection(
                this,
                new ProxyCorrectionSettings
                {
                    proxyCorrectHeadAndHands = proxyCorrectHeadAndHands,
                    proxyRootPositionKp = proxyRootPositionKp,
                    proxyRootVelocityKd = proxyRootVelocityKd,
                    proxyRootRotationKp = proxyRootRotationKp,
                    proxyRootAngularKd = proxyRootAngularKd,
                    enableRootPosePrediction = enableRootPosePrediction,
                    rootPredictionLeadSeconds = rootPredictionLeadSeconds,
                    rootPredictionLeadSecondsDuringResim = rootPredictionLeadSecondsDuringResim,
                    maxRootPredictionDistance = maxRootPredictionDistance,
                    proxyPartLerpStrength = proxyPartLerpStrength,
                    proxyHardSnapRootThreshold = proxyHardSnapRootThreshold,
                    proxyHardSnapPartThreshold = proxyHardSnapPartThreshold,
                    proxyHardSnapHoldSeconds = proxyHardSnapHoldSeconds,
                    proxyInertiaForceScale = ProxyInertiaForceScale,
                    proxyInertiaMaxAcceleration = ProxyInertiaMaxAcceleration,
                    proxyInertiaSmoothing = ProxyInertiaSmoothing,
                    proxySecondaryGravityScale = ProxySecondaryGravityScale
                },
                this,
                () => bodyRigidbodies,
                DetachRootFromParent,
                SetProxyVisualsEnabled,
                index => rigSetup.TryGetBodyRigidbody(bodyRigidbodies, index));
        }

        bool IHostSimulationContext.TryGetInput(out NetworkInputData data)
        {
            return GetInput(out data);
        }

        bool IHostSimulationContext.HasInputAuthority => Object != null && Object.HasInputAuthority;
        bool IHostSimulationContext.IsResimulation => Runner != null && Runner.IsResimulation;
        int IHostSimulationContext.InstanceId => GetInstanceID();
        float IHostSimulationContext.DeltaTime => Runner != null ? Runner.DeltaTime : 0f;
        RagdollInput IHostSimulationContext.InputHandler => _ragdollInput;
        RagdollPhysics IHostSimulationContext.PhysicsHandler => _ragdollPhysics;
        PlayerState IHostSimulationContext.CurrentState
        {
            get => CurrentState;
            set => CurrentState = value;
        }

        Vector3 IHostSimulationContext.MoveDirection
        {
            get => MoveDirection;
            set => MoveDirection = value;
        }

        Vector3 IHostSimulationContext.FacingDirection
        {
            get => FacingDirection;
            set => FacingDirection = value;
        }

        Vector2 IHostSimulationContext.LookDirection
        {
            get => LookDirection;
            set => LookDirection = value;
        }

        float IHostSimulationContext.BodyRoll
        {
            get => BodyRoll;
            set => BodyRoll = value;
        }

        void IHostSimulationContext.ResolvePlayerState(RagdollCommand command)
        {
            UpdatePlayerState(command, _ragdollPhysics);
        }

        void IHostSimulationContext.PublishProxyPoseSnapshot()
        {
            EnsureProxyPosePublisher().Publish();
        }

        void IHostSimulationContext.EmitSyncDiagnostics(string phase)
        {
            EmitSyncDiagnostics(phase);
        }

        void IProxyPosePublisherContext.EnsureProxyBodyReferences()
        {
            if (_rootRigidbody == null)
            {
                CacheProxyBodyReferences();
            }
        }

        Rigidbody IProxyPosePublisherContext.RootRigidbody => _rootRigidbody;
        Rigidbody IProxyPosePublisherContext.HeadRigidbody => _headRigidbody;
        Rigidbody IProxyPosePublisherContext.LeftHandRigidbody => _leftHandRigidbody;
        Rigidbody IProxyPosePublisherContext.RightHandRigidbody => _rightHandRigidbody;

        // SnapshotInterpolation モード時のみ全身ポーズを発行（Hybrid 時の帯域増を避ける）
        bool IProxyPosePublisherContext.PublishFullPose =>
            ResolvedProxySyncMode == ProxySyncMode.SnapshotInterpolation;

        float IProxyPosePublisherContext.PoseTeleportDetectThreshold =>
            profile != null ? profile.poseTeleportDetectThreshold : 2f;

        Rigidbody IProxyPosePublisherContext.GetBodyRigidbody(int index)
        {
            return EnsureRigSetup().TryGetBodyRigidbody(bodyRigidbodies, index);
        }

        void IProxyPosePublisherContext.ApplyPartPose(int slot, Vector3 relativePosition, Quaternion relativeRotation)
        {
            NetPartPositions.Set(slot, relativePosition);
            NetPartRotations.Set(slot, relativeRotation);
        }

        void IProxyPosePublisherContext.IncrementPoseTeleportKey()
        {
            NetPoseTeleportKey++;
        }

        /// <summary>
        /// リスポーン等の瞬間移動時に呼び、クライアントの補間がテレポートを跨いで
        /// スミア（滑り移動）しないようにする。StateAuthority でのみ有効。
        /// </summary>
        public void RequestPoseTeleport()
        {
            if (Object == null || !Object.HasStateAuthority)
            {
                return;
            }

            NetPoseTeleportKey++;
        }

        void IProxyPosePublisherContext.ApplyProxyPoseSnapshot(ProxyPoseSnapshotData snapshot)
        {
            NetRootPosition = snapshot.RootPosition;
            NetRootRotation = snapshot.RootRotation;
            NetRootLinearVelocity = snapshot.RootLinearVelocity;
            NetRootAngularVelocity = snapshot.RootAngularVelocity;
            NetHeadPosition = snapshot.HeadPosition;
            NetHeadRotation = snapshot.HeadRotation;
            NetLeftHandPosition = snapshot.LeftHandPosition;
            NetLeftHandRotation = snapshot.LeftHandRotation;
            NetRightHandPosition = snapshot.RightHandPosition;
            NetRightHandRotation = snapshot.RightHandRotation;
            NetProxyPoseInitialized = snapshot.IsInitialized;
        }

        void IProxyPosePublisherContext.RecordHostGroundTruthSample(Vector3 actualRootPosition, Vector3 actualRootVelocity)
        {
            EnsureDiagnosticsReporter().RecordCsvSample(
                Runner,
                CurrentState,
                MoveDirection,
                IsPlayerGrounded(),
                "Host",
                0f,
                0f,
                0,
                false,
                actualRootPosition,
                actualRootVelocity,
                actualRootPosition,
                actualRootVelocity);
        }

        PlayerState IRagdollStateContext.CurrentState
        {
            get => CurrentState;
            set => CurrentState = value;
        }

        Rigidbody IRagdollStateContext.RootRigidbody => _rootRigidbody;

        bool IRagdollPhysicsContext.IsAnyHandGrabbing =>
            (_leftHandContact != null && _leftHandContact.IsGrabbing) ||
            (_rightHandContact != null && _rightHandContact.IsGrabbing);

        float IRagdollPhysicsContext.BalanceHeight => BalanceHeight;
        float IRagdollPhysicsContext.BalanceStrength => BalanceStrength;
        float IRagdollPhysicsContext.CoreStrength => CoreStrength;
        float IRagdollPhysicsContext.LimbStrength => LimbStrength;
        float IRagdollPhysicsContext.MoveSpeed => MoveSpeed;
        float IRagdollPhysicsContext.TurnSpeed => TurnSpeed;
        float IRagdollPhysicsContext.JumpForce => JumpForce;
        float IRagdollPhysicsContext.AirControlMultiplier => AirControlMultiplier;
        float IRagdollPhysicsContext.StepDuration => StepDuration;
        float IRagdollPhysicsContext.StepHeight => StepHeight;
        float IRagdollPhysicsContext.FeetMountForce => FeetMountForce;
        float IRagdollPhysicsContext.BalanceMargin => BalanceMargin;
        float IRagdollPhysicsContext.IdleBalancePriority => IdleBalancePriority;
        float IRagdollPhysicsContext.WalkingBalancePriority => WalkingBalancePriority;
        float IRagdollPhysicsContext.IdlePoseStiffnessMultiplier => IdlePoseStiffnessMultiplier;
        float IRagdollPhysicsContext.WalkingPoseStiffnessMultiplier => WalkingPoseStiffnessMultiplier;
        float IRagdollPhysicsContext.StateBlendSpeed => StateBlendSpeed;
        float IRagdollPhysicsContext.BalanceDamperRatio => BalanceDamperRatio;
        float IRagdollPhysicsContext.PoseDamperRatio => PoseDamperRatio;
        float IRagdollPhysicsContext.CoreDamperRatio => CoreDamperRatio;
        float IRagdollPhysicsContext.ReachArmInputLimit => ReachArmInputLimit;
        float IRagdollPhysicsContext.ReachUpperArmBasePitch => ReachUpperArmBasePitch;
        float IRagdollPhysicsContext.ReachUpperArmPitchPerUnit => ReachUpperArmPitchPerUnit;
        float IRagdollPhysicsContext.ReachUpperArmMinPitch => ReachUpperArmMinPitch;
        float IRagdollPhysicsContext.ReachUpperArmMaxPitch => ReachUpperArmMaxPitch;
        float IRagdollPhysicsContext.ReachLowerArmPitch => ReachLowerArmPitch;
        float IRagdollPhysicsContext.ReachUpperArmJointSpring => ReachUpperArmJointSpring;
        float IRagdollPhysicsContext.ReachUpperArmJointDamper => ReachUpperArmJointDamper;
        float IRagdollPhysicsContext.ReachUpperArmJointMaxForce => ReachUpperArmJointMaxForce;
        float IRagdollPhysicsContext.ReachLowerArmJointSpring => ReachLowerArmJointSpring;
        float IRagdollPhysicsContext.ReachLowerArmJointDamper => ReachLowerArmJointDamper;
        float IRagdollPhysicsContext.ReachLowerArmJointMaxForce => ReachLowerArmJointMaxForce;
        float IRagdollPhysicsContext.RagdollDriveOffSpring => RagdollDriveOffSpring;
        float IRagdollPhysicsContext.RagdollDriveOffDamper => RagdollDriveOffDamper;
        float IRagdollPhysicsContext.MovementVelocityLerp => MovementVelocityLerp;
        float IRagdollPhysicsContext.PunchImpulse => PunchImpulse;
        float IRagdollPhysicsContext.PunchRecoveryDelaySeconds => PunchRecoveryDelaySeconds;
        float IRagdollPhysicsContext.PunchRecoveryLerpSpeed => PunchRecoveryLerpSpeed;
        ActionPoseAsset IRagdollPhysicsContext.ReachPose => reachPose;
        bool IRagdollPhysicsContext.HasStateAuthority => Object != null && Object.HasStateAuthority;
        bool IRagdollPhysicsContext.UseForecastPhysics => ResolvedProxySyncMode == ProxySyncMode.Forecast;

        Vector3 IProxyPoseSource.NetRootPosition => NetRootPosition;
        Quaternion IProxyPoseSource.NetRootRotation => NetRootRotation;
        Vector3 IProxyPoseSource.NetRootLinearVelocity => NetRootLinearVelocity;
        Vector3 IProxyPoseSource.NetRootAngularVelocity => NetRootAngularVelocity;
        Vector3 IProxyPoseSource.NetHeadPosition => NetHeadPosition;
        Quaternion IProxyPoseSource.NetHeadRotation => NetHeadRotation;
        Vector3 IProxyPoseSource.NetLeftHandPosition => NetLeftHandPosition;
        Quaternion IProxyPoseSource.NetLeftHandRotation => NetLeftHandRotation;
        Vector3 IProxyPoseSource.NetRightHandPosition => NetRightHandPosition;
        Quaternion IProxyPoseSource.NetRightHandRotation => NetRightHandRotation;
        bool IProxyPoseSource.IsNetProxyPoseInitialized => NetProxyPoseInitialized;
        bool IProxyPoseSource.HasInputAuthority => Object != null && Object.HasInputAuthority;
        bool IProxyPoseSource.IsResimulation => Runner != null && Runner.IsResimulation;
        int IProxyPoseSource.TickRaw => Runner != null ? Runner.Tick.Raw : 0;
        string IProxyPoseSource.CurrentStateName => CurrentState.ToString();
        Vector3 IProxyPoseSource.MoveDirectionValue => MoveDirection;
        bool IProxyPoseSource.IsPlayerGrounded => IsPlayerGrounded();

        bool IPoseSnapshotAccess.TryGetPoseSnapshots(
            out NetworkBehaviourBuffer from, out NetworkBehaviourBuffer to, out float alpha)
        {
            return TryGetSnapshotsBuffers(out from, out to, out alpha);
        }

        (Vector3 from, Vector3 to) IPoseSnapshotAccess.ReadRootPosition(
            NetworkBehaviourBuffer from, NetworkBehaviourBuffer to)
        {
            EnsureSnapshotReaders();
            return _rootPositionReader.Read(from, to);
        }

        (Quaternion from, Quaternion to) IPoseSnapshotAccess.ReadRootRotation(
            NetworkBehaviourBuffer from, NetworkBehaviourBuffer to)
        {
            EnsureSnapshotReaders();
            return _rootRotationReader.Read(from, to);
        }

        (int from, int to) IPoseSnapshotAccess.ReadPoseTeleportKey(
            NetworkBehaviourBuffer from, NetworkBehaviourBuffer to)
        {
            EnsureSnapshotReaders();
            return _poseTeleportKeyReader.Read(from, to);
        }

        bool IPoseSnapshotAccess.ReadPoseInitialized(NetworkBehaviourBuffer buffer)
        {
            EnsureSnapshotReaders();
            return _poseInitializedReader.Read(buffer);
        }

        NetworkArrayReadOnly<Vector3> IPoseSnapshotAccess.ReadPartPositions(NetworkBehaviourBuffer buffer)
        {
            EnsureSnapshotReaders();
            return _partPositionsReader.Read(buffer);
        }

        NetworkArrayReadOnly<Quaternion> IPoseSnapshotAccess.ReadPartRotations(NetworkBehaviourBuffer buffer)
        {
            EnsureSnapshotReaders();
            return _partRotationsReader.Read(buffer);
        }

        Rigidbody IPoseSnapshotAccess.GetBodyRigidbodyByIndex(int index)
        {
            return EnsureRigSetup().TryGetBodyRigidbody(bodyRigidbodies, index);
        }

        private Rigidbody[] _decorationRigidbodies;

        Rigidbody[] IPoseSnapshotAccess.DecorationRigidbodies
        {
            get
            {
                if (_decorationRigidbodies == null)
                {
                    var poseDriven = new System.Collections.Generic.HashSet<Rigidbody>(bodyRigidbodies);
                    var decorations = new System.Collections.Generic.List<Rigidbody>();
                    Rigidbody[] all = GetKinematicTargetRigidbodies();
                    if (all != null)
                    {
                        foreach (Rigidbody rb in all)
                        {
                            if (rb != null && !poseDriven.Contains(rb))
                            {
                                decorations.Add(rb);
                            }
                        }
                    }

                    _decorationRigidbodies = decorations.ToArray();
                }

                return _decorationRigidbodies;
            }
        }

        void IPoseSnapshotAccess.SetProxyVisualsEnabled(bool enabled)
        {
            SetProxyVisualsEnabled(enabled);
        }

        bool IPoseSnapshotAccess.IsLatestPoseInitialized => NetProxyPoseInitialized;
        Vector3 IPoseSnapshotAccess.LatestRootPosition => NetRootPosition;
        Quaternion IPoseSnapshotAccess.LatestRootRotation => NetRootRotation;

        Vector3 IPoseSnapshotAccess.GetLatestPartRelativePosition(int slot)
        {
            return NetPartPositions.Get(slot);
        }

        Quaternion IPoseSnapshotAccess.GetLatestPartRelativeRotation(int slot)
        {
            return NetPartRotations.Get(slot);
        }

        float IPoseSnapshotAccess.DecorationSmoothingTau =>
            profile != null ? profile.decorationSmoothingTau : 0.05f;

        void IRagdollAudioSink.PlayImpactSound()
        {
            if (_audioPlayer == null)
            {
                _audioPlayer = new RagdollAudioPlayer(profile, soundSource);
            }

            _audioPlayer.PlayImpactSound();
        }

        void IRagdollAudioSink.PlayHitSound()
        {
            if (_audioPlayer == null)
            {
                _audioPlayer = new RagdollAudioPlayer(profile, soundSource);
            }

            _audioPlayer.PlayHitSound();
        }

        bool IRagdollTreasureCarryContext.IsGrabbingTreasure => _treasureGrabRefCount > 0;
        float IRagdollTreasureCarryContext.CarryMoveMaxForce => CarryMoveMaxForce;
        float IRagdollTreasureCarryContext.CarryHarnessSlack => CarryHarnessSlack;
        float IRagdollTreasureCarryContext.CarryHarnessLimitSpring => CarryHarnessLimitSpring;
        float IRagdollTreasureCarryContext.CarryHarnessLimitDamper => CarryHarnessLimitDamper;

        void IRagdollTreasureCarryContext.NotifyTreasureGrabbed(Rigidbody treasureRigidbody)
        {
            if (_treasureGrabRefCount == 0)
            {
                CreateCarryHarness(treasureRigidbody);
            }
            else if (_carryHarnessTreasureRigidbody != null && treasureRigidbody != null && _carryHarnessTreasureRigidbody != treasureRigidbody)
            {
                // MVP simplification: 2つ目以降の別Treasure grabでは既存ハーネスを維持する。
                Debug.Log("[TreasureCarry] Harness already exists for a different Treasure; keeping the existing harness for MVP.", this);
            }

            _treasureGrabRefCount++;
        }

        void IRagdollTreasureCarryContext.NotifyTreasureReleased(Rigidbody treasureRigidbody)
        {
            if (_treasureGrabRefCount > 0)
            {
                _treasureGrabRefCount--;
            }

            if (_treasureGrabRefCount == 0)
            {
                DestroyCarryHarness();
            }
        }

        private void CreateCarryHarness(Rigidbody treasureRigidbody)
        {
            if (_rootRigidbody == null || treasureRigidbody == null)
            {
                Debug.LogWarning("[TreasureCarry] Cannot create carry harness because Root or Treasure Rigidbody is missing.", this);
                return;
            }

            if (_carryHarnessJoint != null)
            {
                if (_carryHarnessTreasureRigidbody != treasureRigidbody)
                {
                    // MVP simplification: 1プレイヤー1ハーネスのみ。別Treasure通知はログだけ出して無視する。
                    Debug.Log("[TreasureCarry] Harness already exists for a different Treasure; keeping the existing harness for MVP.", this);
                }

                return;
            }

            ConfigurableJoint joint = _rootRigidbody.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = treasureRigidbody;
            joint.xMotion = ConfigurableJointMotion.Limited;
            joint.yMotion = ConfigurableJointMotion.Limited;
            joint.zMotion = ConfigurableJointMotion.Limited;
            joint.angularXMotion = ConfigurableJointMotion.Free;
            joint.angularYMotion = ConfigurableJointMotion.Free;
            joint.angularZMotion = ConfigurableJointMotion.Free;

            SoftJointLimit linearLimit = joint.linearLimit;
            linearLimit.limit = CarryHarnessSlack;
            linearLimit.bounciness = 0f;
            joint.linearLimit = linearLimit;
            joint.linearLimitSpring = new SoftJointLimitSpring
            {
                spring = CarryHarnessLimitSpring,
                damper = CarryHarnessLimitDamper
            };

            _carryHarnessJoint = joint;
            _carryHarnessTreasureRigidbody = treasureRigidbody;
        }

        private void DestroyCarryHarness()
        {
            if (_carryHarnessJoint != null)
            {
                Destroy(_carryHarnessJoint);
                _carryHarnessJoint = null;
            }

            _carryHarnessTreasureRigidbody = null;
        }

        void IRagdollGroundingSink.OnFootGroundedChanged(bool isLeftFoot, bool isGrounded)
        {
            if (_groundingService == null)
            {
                _groundingService = new RagdollGroundingService();
            }

            RagdollGroundingUpdate grounding = _groundingService.Apply(
                isLeftFoot,
                isGrounded,
                IsLeftFootGrounded,
                IsRightFootGrounded,
                CurrentState,
                AutoGetUpWhenPossible);

            IsLeftFootGrounded = grounding.LeftFootGrounded;
            IsRightFootGrounded = grounding.RightFootGrounded;

            if (_ragdollPhysics != null)
            {
                _ragdollPhysics.SetFootGroundedInfo(isLeftFoot, isGrounded, grounding.AnyFootGrounded);
            }

            if (grounding.ShouldAttemptRecover && _ragdollState != null)
            {
                _ragdollState.TryRecoverFromRagdoll();
            }
        }

        bool ILocalPlayerViewSource.HasInputAuthority => Object != null && Object.HasInputAuthority;
        Transform ILocalPlayerViewSource.Transform => transform;

        // 左右の手の接触コンポーネント（Spawned 時に RagdollHandContact 自身が登録する。
        // APR_Root detach 後は階層検索で辿れないため登録制）
        private RagdollHandContact _leftHandContact;
        private RagdollHandContact _rightHandContact;

        /// <summary>RagdollHandContact.Spawned から呼ばれる登録口。</summary>
        public void RegisterHandContact(RagdollHandContact hand, bool isLeftHand)
        {
            if (isLeftHand)
                _leftHandContact = hand;
            else
                _rightHandContact = hand;
        }

        /// <summary>
        /// 指定した手が掴んでいるオブジェクトのルート（未掴みなら null）。
        /// GrabbedNetworkId は [Networked] なので入力権限クライアントでも解決できる。
        /// </summary>
        public Transform GetHeldObjectRoot(bool isLeftHand)
        {
            RagdollHandContact hand = isLeftHand ? _leftHandContact : _rightHandContact;
            if (hand == null || Runner == null)
                return null;

            Fusion.NetworkId id = hand.GrabbedNetworkId;
            if (id == default)
                return null;

            NetworkObject held = Runner.FindObject(id);
            return held != null ? held.transform : null;
        }

        /// <summary>
        /// 両手で同一の NetworkObject を掴んでいるか。
        /// 片手だけ・左右で別オブジェクトの場合は false。
        /// GrabbedNetworkId は [Networked] なので入力権限クライアントでも正しく読める。
        /// </summary>
        public bool IsTwoHandedHold
        {
            get
            {
                if (_leftHandContact == null || _rightHandContact == null)
                    return false;

                Fusion.NetworkId leftId = _leftHandContact.GrabbedNetworkId;
                return leftId != default && leftId == _rightHandContact.GrabbedNetworkId;
            }
        }


        private const float HorizontalForwardEpsilonSqr = 0.0001f;
        private static bool TryGetHorizontalForward(Vector3 facing, out Vector3 forward)
        {
            forward = facing;
            forward.y = 0f;

            if (forward.sqrMagnitude <= HorizontalForwardEpsilonSqr)
            {
                forward = Vector3.zero;
                return false;
            }

            forward.Normalize();
            return true;
        }

        Vector3 ILocalPlayerViewSource.FacingForward
        {
            get
            {
                if (TryGetHorizontalForward(FacingDirection, out Vector3 facingForward))
                    return facingForward;

                // APR_Root は Spawn 後に detach されるため、owner transform ではなく実ボディ root を向きの fallback に使う。
                Transform bodyTransform = _rootRigidbody != null ? _rootRigidbody.transform : transform;
                if (TryGetHorizontalForward(bodyTransform.forward, out Vector3 bodyForward))
                    return bodyForward;

                if (TryGetHorizontalForward(transform.forward, out Vector3 ownerForward))
                    return ownerForward;

                return Vector3.forward;
            }
        }

        #endregion

        #region Properties

        // アクセサ
        public GameObject[] BodyParts => bodyParts;
        public Rigidbody[] BodyRigidbodies => bodyRigidbodies;
        public ConfigurableJoint[] BodyJoints => bodyJoints;
        public bool UseHybridProxySimulation => useHybridProxySimulation;

        /// <summary>
        /// プロファイルから解決した実効プロキシ同期モード（useForecastPhysics 後方互換込み）。
        /// </summary>
        public ProxySyncMode ResolvedProxySyncMode =>
            profile != null ? profile.ResolveProxySyncMode() : ProxySyncMode.Hybrid;

        public Transform CenterOfMassPoint => centerOfMassPoint;
        private float BalanceHeight => profile.balanceHeight;
        private float BalanceStrength => profile.balanceStrength;
        private float CoreStrength => profile.coreStrength;
        private float LimbStrength => profile.limbStrength;
        // Dash/Crouch は moveSpeed に倍率を掛けて一時的に加減速する。
        // どちらも毎tick ProcessInput 後の CurrentCommand から読むため、
        // ホスト権威sim・クライアント予測の両経路で同一入力から同じ倍率になる（resim安全）。
        private float MoveSpeed =>
            profile.moveSpeed
            * (IsDashing ? Mathf.Max(1f, profile.dashSpeedMultiplier) : 1f)
            * (IsCrouching ? Mathf.Clamp01(profile.crouchSpeedMultiplier) : 1f);

        private bool IsDashing => _ragdollInput != null && _ragdollInput.CurrentCommand.IsDashing;
        private bool IsCrouching => _ragdollInput != null && _ragdollInput.CurrentCommand.IsCrouching;
        private float TurnSpeed => profile.turnSpeed;
        private float JumpForce => profile.jumpForce;
        private float AirControlMultiplier => profile.airControlMultiplier;
        private float StepDuration => profile.stepDuration;
        private float StepHeight => profile.stepHeight;
        private float FeetMountForce => profile.feetMountForce;
        private bool ForwardIsCameraDirection => profile.forwardIsCameraDirection;
        private bool AutoGetUpWhenPossible => profile.autoGetUpWhenPossible;
        private bool UseStepPrediction => profile.useStepPrediction;
        private float BalanceMargin => profile.balanceMargin;
        private bool UseCOMBasedBalance => profile.useCOMBasedBalance;

        // Animation-Target Following設定アクセサ (Phase 2)
        private float IdleBalancePriority => profile.idleBalancePriority;
        private float WalkingBalancePriority => profile.walkingBalancePriority;
        private float IdlePoseStiffnessMultiplier => profile.idlePoseStiffnessMultiplier;
        private float WalkingPoseStiffnessMultiplier => profile.walkingPoseStiffnessMultiplier;
        private float StateBlendSpeed => profile.stateBlendSpeed;

        // ダンピング設定アクセサ（微振動防止用）
        private float BalanceDamperRatio => profile.balanceDamperRatio;
        private float PoseDamperRatio => profile.poseDamperRatio;
        private float CoreDamperRatio => profile.coreDamperRatio;
        private float ReachArmInputLimit => profile.reachArmInputLimit;
        private float ReachUpperArmBasePitch => profile.reachUpperArmBasePitch;
        private float ReachUpperArmPitchPerUnit => profile.reachUpperArmPitchPerUnit;
        private float ReachUpperArmMinPitch => profile.reachUpperArmMinPitch;
        private float ReachUpperArmMaxPitch => profile.reachUpperArmMaxPitch;
        private float ReachLowerArmPitch => profile.reachLowerArmPitch;
        private float ReachUpperArmJointSpring => profile.reachUpperArmJointSpring;
        private float ReachUpperArmJointDamper => profile.reachUpperArmJointDamper;
        private float ReachUpperArmJointMaxForce => profile.reachUpperArmJointMaxForce;
        private float ReachLowerArmJointSpring => profile.reachLowerArmJointSpring;
        private float ReachLowerArmJointDamper => profile.reachLowerArmJointDamper;
        private float ReachLowerArmJointMaxForce => profile.reachLowerArmJointMaxForce;
        private float RagdollDriveOffSpring => profile.ragdollDriveOffSpring;
        private float RagdollDriveOffDamper => profile.ragdollDriveOffDamper;
        private float CarryMoveMaxForce => profile.carryMoveMaxForce;
        private float CarryHarnessSlack => profile.carryHarnessSlack;
        private float CarryHarnessLimitSpring => profile.carryHarnessLimitSpring;
        private float CarryHarnessLimitDamper => profile.carryHarnessLimitDamper;
        private float MovementVelocityLerp => profile.movementVelocityLerp;
        private float PunchImpulse => profile.punchImpulse;
        private float PunchRecoveryDelaySeconds => profile.punchRecoveryDelaySeconds;
        private float PunchRecoveryLerpSpeed => profile.punchRecoveryLerpSpeed;
        private float ProxyInertiaForceScale => profile.proxyInertiaForceScale;
        private float ProxyInertiaMaxAcceleration => profile.proxyInertiaMaxAcceleration;
        private float ProxyInertiaSmoothing => profile.proxyInertiaSmoothing;
        private float ProxySecondaryGravityScale => profile.proxySecondaryGravityScale;

        #endregion

        #region Debug Visualization

        private void OnGUI()
        {
            if (!showDebugGUI)
                return;

            if (_debugView == null)
            {
                _debugView = new RagdollDebugView();
            }

            _debugView.DrawGui(
                Object,
                _ragdollPhysics,
                CurrentState,
                IsLeftFootGrounded,
                IsRightFootGrounded);
        }

        private void OnDrawGizmos()
        {
            if (!showBalanceGizmos)
                return;

            if (_debugView == null)
            {
                _debugView = new RagdollDebugView();
            }

            _debugView.DrawGizmos(_ragdollPhysics, gizmoSphereRadius, BalanceMargin);
        }

        #endregion
    }
}
