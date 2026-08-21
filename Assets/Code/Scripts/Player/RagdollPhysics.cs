using System;
using Rebaka.Player.Posing;
using Rebaka.Utils;
using UnityEngine;

namespace Rebaka.Player
{
    /// <summary>
    /// バランス状態を表すenum
    /// 重心と支持基底面の関係から判定される
    /// </summary>
    public enum BalanceState
    {
        Balanced, // 安定（重心が支持基底面内）
        Forward, // 前傾（重心が前方に逸脱）
        Backward, // 後傾（重心が後方に逸脱）
        Left, // 左傾（重心が左方に逸脱）
        Right // 右傾（重心が右方に逸脱）
    }

    public class RagdollPhysics
    {
        #region Constructor

        internal RagdollPhysics(IRagdollPhysicsContext context, GameObject[] bodyParts,
            Rigidbody[] bodyRigidbodies, ConfigurableJoint[] bodyJoints)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));

            _bodyParts = bodyParts;
            _bodyRigidbodies = bodyRigidbodies;
            _bodyJoints = bodyJoints;

            // ═══════════════════════════════════════════════════════════════
            // APR_Root 原点引力バグの修正
            // ═══════════════════════════════════════════════════════════════
            // APR_Root のConfigurableJointはconnectedBody=null（ワールド接続）。
            // この設計はAPR方式では正常だが、以下の条件が揃うと原点に引き寄せられる:
            //   1. configuredInWorldSpace=false → アンカーがスポーン時のワールド原点に固定
            //   2. xDrive/yDrive/zDrive に positionSpring > 0 → 位置ドライブで原点に引っ張る
            // 修正: configuredInWorldSpace=true + 位置ドライブを完全ゼロクリア
            if (_bodyJoints != null && _bodyJoints.Length > IndexRoot && _bodyJoints[IndexRoot] != null)
            {
                _bodyJoints[IndexRoot].configuredInWorldSpace = true;

                // 位置ドライブを完全無効化（connectedBody=nullなのでワールド原点に引っ張られる原因）
                var zeroDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
                _bodyJoints[IndexRoot].xDrive = zeroDrive;
                _bodyJoints[IndexRoot].yDrive = zeroDrive;
                _bodyJoints[IndexRoot].zDrive = zeroDrive;
                _bodyJoints[IndexRoot].slerpDrive = zeroDrive;
            }

            // 地面レイヤーマスクをキャッシュ（毎tick文字列ルックアップを回避）
            int groundLayerIndex = LayerMask.NameToLayer("Ground");
            _groundLayerMask = groundLayerIndex >= 0 ? 1 << groundLayerIndex : 0;

            InitializeJointDrives();
            StoreOriginalPoses();

            _balanceMargin = _context.Profile.balanceMargin;

            // スポーン直後にバランスドライブを適用
            // (DeactivateRagdoll() はバランス復帰時にしか呼ばれないため、
            //  初期フレームでドライブが未適用→即ActivateRagdoll の問題を防止)
            ApplyInitialDrives();
        }

        #endregion

        #region Physics Update

        /// <summary>
        ///     メインの物理更新ループ
        ///     moveDirection: 移動方向（WASD入力、カメラ基準）
        ///     facingDirection: 回転先方向（カメラ前方 or 移動方向、モードで切替）
        /// </summary>
        public void UpdatePhysics(PlayerState state, RagdollCommand command, float deltaTime)
        {
            _wantsPunchRight = command.IsPunchingRight;
            _wantsPunchLeft = command.IsPunchingLeft;
            _wantsReachRight = command.IsGrabbingRight;
            _wantsReachLeft = command.IsGrabbingLeft;

            // reach終了検出: state に関わらず毎tick実行して確実にドライブを復元
            if (!_wantsReachRight && _wasReachingRight)
            {
                _wasReachingRight = false;
                RestoreArmDrives(true);
                ResetArmTargetToOriginal(IndexUpperRightARM);
                ResetArmTargetToOriginal(IndexLowerRightARM);
            }

            if (!_wantsReachLeft && _wasReachingLeft)
            {
                _wasReachingLeft = false;
                RestoreArmDrives(false);
                ResetArmTargetToOriginal(IndexUpperLeftARM);
                ResetArmTargetToOriginal(IndexLowerLeftARM);
            }

            // 掴まり中は接地扱い: 手で何かを掴んでいる間はバランス喪失（ラグドール化）させない。
            // ぶら下がった瞬間にラグドール化すると Reach 系ドライブが丸ごと停止し、
            // 腕が脱力してよじ登れない（HFF 同様、懸垂中は「支持あり」とみなす）。
            bool isGrounded = IsGrounded() || (_context != null && _context.IsAnyHandGrabbing);

            // Forecast Physicsモードでクライアント側の場合:
            // バランス判定とラグドール状態フリップはホストのCurrentStateに委ねる。
            // クライアントで独立にバランス判定すると状態フリップ→JointDrive振動の原因になる。
            bool isHostAuthority = _context != null && _context.HasStateAuthority;
            bool forecastClientMode = _context != null && _context.UseForecastPhysics && !isHostAuthority;

            if (forecastClientMode)
            {
                // ホストの状態を信頼してラグドール状態を同期
                bool isRagdollFromHost = state == PlayerState.Ragdoll;
                if (isRagdollFromHost != _isRagdoll)
                {
                    _isRagdoll = isRagdollFromHost;
                    if (_isRagdoll)
                        ActivateRagdoll();
                    else
                        DeactivateRagdoll();
                }

                _balanced = !_isRagdoll;
            }
            else
            {
                _balanced = CalculateBalanceState(isGrounded, state);

                // バランス状態に応じたラグドールの自動切り替え
                if (_balanced && _isRagdoll)
                    DeactivateRagdoll();
                else if (!_balanced && !_isRagdoll) ActivateRagdoll();
            }

            if (!_isRagdoll)
            {
                UpdateStateBlending(state, deltaTime);
                ApplyBlendedJointDrives();
                UpdatePunchRecovery(deltaTime);

                // 回転制御（facingDirectionベース = 移動方向由来の体ヨー）
                UpdateRootRotation(command.FacingDirection, deltaTime);

                // 体の上下（マウスY由来の胴体ベンド）とロール（Alt+MouseX）を常時適用。
                // LookDirection.x = 胴体ベンド(APR MouseYAxisBody 相当, ±0.9)
                UpdateBodyLook(command.LookDirection.x, command.BodyRoll);
            }

            // ジャンプ初速の再武装: 足の接地状態（LastFootGrounded）を再武装の合図に使う方式は
            // 2段階とも破綻した。
            // 1) 離陸エッジ(false化)を待つ旧方式: 足が何らかの理由で接地判定に固着すると
            //    false エッジが二度と来ずラッチが永久に解除されない（2026-07-09 実機、バグ6）。
            // 2) 最低滞空時間+着地ポーリング方式: 走行中の踏み出し足はジャンプ入力の瞬間も
            //    実際にまだ地面へ接触しているため、ガード時間を過ぎても LastFootGrounded が
            //    "残留" ではなく素で true のままになり、ボタン長押し中に誤って再武装されて
            //    2段ジャンプが発生した（2026-07-09 実機で確認）。
            //
            // 足の接地状態はジャンプ回数の制御に使う信号として不適切（歩行中は常に何らかの
            // 形で true になりうる）。本来「1回の押下につき初速は1回」はボタンの押下/解放と
            // 一対一であるべきで、地面判定とは独立した話。そこでボタンが離された時にのみ
            // ラッチを解除する方式に変更する。これなら足の固着状態と無関係に毎回正しく
            // 解除されるため、上記1)2)いずれの故障モードにも構造的に陥らない。
            //
            // 挙動変更の注意: 長押しでのバニーホップ（着地即再ジャンプの連打）はできなくなり、
            // ボタンを離して押し直す必要がある。これは今回のバグ報告（長押しで意図せず2段
            // ジャンプする）が求めていた挙動そのものでもある。
            if (!command.IsJumping)
                _jumpVelocityApplied = false;

            float movementControlMultiplier = isGrounded ? 1f : _context.Profile.airControlMultiplier;

            // 状態に基づいた物理制御（移動力・ジャンプ等）
            switch (state)
            {
                case PlayerState.Idle:
                    if (!_isRagdoll)
                        ProcessWalking(command.MoveDirection, deltaTime);
                    break;
                case PlayerState.Walking:
                    ApplyMovementForce(command.MoveDirection, movementControlMultiplier);
                    if (!_isRagdoll)
                        ProcessWalking(command.MoveDirection, deltaTime);
                    break;
                case PlayerState.Jumping:
                    ProcessJumpingPhysics();
                    ApplyMovementForce(command.MoveDirection, movementControlMultiplier);
                    break;
                case PlayerState.Reaching:
                    // つかみ(Reaching)中も移動・歩行を許可し、物を持ち運べるようにする。
                    // 状態評価で Grabbing は Walking より優先されるため(RagdollStateEvaluator)、
                    // Walking と同じ移動力・歩行処理をここでも適用しないと運搬中に静止してしまう。
                    ApplyMovementForce(command.MoveDirection, movementControlMultiplier);
                    if (!_isRagdoll)
                        ProcessWalking(command.MoveDirection, deltaTime);
                    ProcessReachingPhysics(command.LookDirection);
                    break;
                case PlayerState.Punching:
                    ProcessPunchingPhysics();
                    break;
                case PlayerState.Ragdoll:
                    if (isGrounded)
                        DeactivateRagdoll();
                    break;
            }

            // ポーズオーサリングのプレビュー: 状態/入力に依らず、指定側の Reach ポーズを
            // 最後に上書き適用する。ツール側がアセットを編集すると次tickで反映され、
            // 重力下の実機ポーズとして即座に確認できる。
            if (_posePreviewActive && !_isRagdoll) ApplyReachPose(_posePreviewRight, 0f, 0f);
        }

        #endregion

        #region Root Rotation

        /// <summary>
        ///     ルートの回転制御（facingDirectionベース）
        ///     facingDirection が zero の場合は向き維持（移動方向モードでidle時）
        /// </summary>
        private void UpdateRootRotation(Vector3 facingDirection, float deltaTime)
        {
            if (_bodyJoints == null || !_bodyJoints[IndexRoot])
                return;

            // facingDirection が zero → 向き維持（移動方向モードでidle時）
            if (facingDirection.sqrMagnitude < 0.01f)
                return;

            var lookRotation = Quaternion.LookRotation(facingDirection.normalized, Vector3.up);
            _bodyJoints[IndexRoot].targetRotation = Quaternion.Slerp(
                _bodyJoints[IndexRoot].targetRotation,
                Quaternion.Inverse(lookRotation),
                _context.Profile.turnSpeed * deltaTime
            );
        }

        #endregion

        #region Fields

        private readonly IRagdollPhysicsContext _context;

        // 毎tick・多数個所で型ごとに一括操作をするため、型別のフィールドにキャッシュする
        private GameObject[] _bodyParts;
        private Rigidbody[] _bodyRigidbodies;
        private ConfigurableJoint[] _bodyJoints;

        private JointDrive _balanceOn; //
        private JointDrive _poseOn;
        private JointDrive _coreStiffness;
        private JointDrive _driveOff;

        // オリジナルポーズ
        private Quaternion[] _originalRotations;

        // 状態フラグ
        private bool _balanced = true;
        private bool _isRagdoll = false;

        // ジャンプ制御（ラッチ）: 1回のジャンプ入力につき初速は1tickだけ与える。
        // Jumping 状態は接地中ずっと継続するため、これが無いと毎tick linearVelocity が
        // jumpForce に再設定され、スペースの押下時間でジャンプ高さが変わってしまう。
        private bool _jumpVelocityApplied;

        // 足の接地状態
        private bool _isAnyFootGrounded = false;

        // バランス計算用
        private BalanceState _currentBalanceState = BalanceState.Balanced;
        private Vector3 _centerOfMass; // 重心位置
        private Vector3 _supportPolygonCenter; // 支持基底面の中心
        private readonly float _balanceMargin = 0.15f; // バランス判定のマージン（メートル）
        private readonly int _groundLayerMask; // キャッシュ済みの地面レイヤーマスク

        // 歩行ステップサイクル（APR方式）— tick をまたいで持ち越す状態のみ
        private bool _stepRight;
        private bool _stepLeft;
        private float _stepRTimer;
        private float _stepLTimer;
        private bool _alertLegRight;
        private bool _alertLegLeft;

        // Animation-Target Following (Phase 2)
        private float _currentBalancePriority = 0.8f;
        private float _currentPoseStiffnessMultiplier = 1f;
        private float _lastAppliedPoseMultiplier = -1f; // BN-2: Joint書き込みスキップ用キャッシュ

        // Punch control
        private bool _punchingRight;
        private bool _punchingLeft;
        private float _rightPunchRecoveryDelay;
        private float _leftPunchRecoveryDelay;
        private bool _wantsPunchRight;
        private bool _wantsPunchLeft;
        private bool _wantsReachRight;
        private bool _wantsReachLeft;
        private bool _wasReachingRight;
        private bool _wasReachingLeft;

        // ポーズオーサリング用プレビュー（Editor のツールから設定）。
        // 有効時は入力/状態を無視して、指定側の Reach ポーズを毎tick上書き適用する。
        private bool _posePreviewActive;
        private bool _posePreviewRight;
        private ActionPoseAsset _posePreviewAsset;


        // インデックス定数。値の正本は LogicalJoint enum（リテラルを二重に書かない）。
        // enum 側を変えれば全ての添字が追従し、片方だけ直して骨がズレる事故を構造的に防ぐ。
        private const int IndexRoot = (int)LogicalJoint.Root;
        private const int IndexBody = (int)LogicalJoint.Body;
        private const int IndexHead = (int)LogicalJoint.Head;
        private const int IndexUpperRightARM = (int)LogicalJoint.UpperRightArm;
        private const int IndexLowerRightARM = (int)LogicalJoint.LowerRightArm;
        private const int IndexUpperLeftARM = (int)LogicalJoint.UpperLeftArm;
        private const int IndexLowerLeftARM = (int)LogicalJoint.LowerLeftArm;
        private const int IndexUpperRightLeg = (int)LogicalJoint.UpperRightLeg;
        private const int IndexLowerRightLeg = (int)LogicalJoint.LowerRightLeg;
        private const int IndexUpperLeftLeg = (int)LogicalJoint.UpperLeftLeg;
        private const int IndexLowerLeftLeg = (int)LogicalJoint.LowerLeftLeg;
        private const int IndexRightFoot = (int)LogicalJoint.RightFoot;
        private const int IndexLeftFoot = (int)LogicalJoint.LeftFoot;
        private const int IndexRightHand = (int)LogicalJoint.RightHand;
        private const int IndexLeftHand = (int)LogicalJoint.LeftHand;

        #endregion

        #region Properties

        public bool IsRagdoll => _isRagdoll;
        public bool IsBalanced => _balanced;
        public bool LastRaycastHit { get; private set; }
        public bool LastFootGrounded => _isAnyFootGrounded;
        public BalanceState CurrentBalanceState => _currentBalanceState;
        public Vector3 CenterOfMass => _centerOfMass;
        public Vector3 SupportPolygonCenter => _supportPolygonCenter;
        public float CurrentBalancePriority => _currentBalancePriority;
        public float CurrentPoseStiffnessMultiplier => _currentPoseStiffnessMultiplier;

        #endregion

        #region Initialization Methods

        /// <summary>
        /// スポーン時にDeactivateRagdoll()と同一のドライブ配置を適用
        /// 初期フレームからジョイントが機能し、即座にラグドール化するのを防止
        /// </summary>
        private void ApplyInitialDrives()
        {
            if (_bodyJoints == null)
                return;

            // Root: バランスドライブ
            _bodyJoints[IndexRoot].angularXDrive = _balanceOn;
            _bodyJoints[IndexRoot].angularYZDrive = _balanceOn;

            // Body: コアスティフネス
            if (IndexBody < _bodyJoints.Length && _bodyJoints[IndexBody] != null)
            {
                _bodyJoints[IndexBody].angularXDrive = _coreStiffness;
                _bodyJoints[IndexBody].angularYZDrive = _coreStiffness;
            }

            // Head: ポーズ
            _bodyJoints[IndexHead].angularXDrive = _poseOn;
            _bodyJoints[IndexHead].angularYZDrive = _poseOn;

            // Arms: ポーズ
            _bodyJoints[IndexUpperRightARM].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperRightARM].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerRightARM].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerRightARM].angularYZDrive = _poseOn;
            _bodyJoints[IndexUpperLeftARM].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperLeftARM].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerLeftARM].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerLeftARM].angularYZDrive = _poseOn;

            // Legs: ポーズ
            _bodyJoints[IndexUpperRightLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperRightLeg].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerRightLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerRightLeg].angularYZDrive = _poseOn;
            _bodyJoints[IndexUpperLeftLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperLeftLeg].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerLeftLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerLeftLeg].angularYZDrive = _poseOn;

            // Feet: ポーズ
            if (IndexRightFoot < _bodyJoints.Length && _bodyJoints[IndexRightFoot] != null)
            {
                _bodyJoints[IndexRightFoot].angularXDrive = _poseOn;
                _bodyJoints[IndexRightFoot].angularYZDrive = _poseOn;
            }

            if (IndexLeftFoot < _bodyJoints.Length && _bodyJoints[IndexLeftFoot] != null)
            {
                _bodyJoints[IndexLeftFoot].angularXDrive = _poseOn;
                _bodyJoints[IndexLeftFoot].angularYZDrive = _poseOn;
            }

            // Hands: ポーズ
            if (IndexRightHand < _bodyJoints.Length && _bodyJoints[IndexRightHand] != null)
            {
                _bodyJoints[IndexRightHand].angularXDrive = _poseOn;
                _bodyJoints[IndexRightHand].angularYZDrive = _poseOn;
            }

            if (IndexLeftHand < _bodyJoints.Length && _bodyJoints[IndexLeftHand] != null)
            {
                _bodyJoints[IndexLeftHand].angularXDrive = _poseOn;
                _bodyJoints[IndexLeftHand].angularYZDrive = _poseOn;
            }
        }

        private void InitializeJointDrives()
        {
            // ダンパー比率を適用（振動防止）
            // RagdollProfile に定義済みの damperRatio を使用する。
            // damper = spring * ratio で臨界減衰に近づける。
            float balanceDamper = _context.Profile.balanceStrength * _context.Profile.balanceDamperRatio;
            float limbDamper = _context.Profile.limbStrength * _context.Profile.poseDamperRatio;
            float coreDamper = _context.Profile.coreStrength * _context.Profile.coreDamperRatio;

            _balanceOn = JointConfigurator.CreateJointDrive(_context.Profile.balanceStrength, balanceDamper);
            _poseOn = JointConfigurator.CreateJointDrive(_context.Profile.limbStrength, limbDamper);
            _coreStiffness = JointConfigurator.CreateJointDrive(_context.Profile.coreStrength, coreDamper);
            _driveOff = JointConfigurator.CreateJointDrive(_context.Profile.ragdollDriveOffSpring,
                _context.Profile.ragdollDriveOffDamper);
        }

        private void StoreOriginalPoses()
        {
            _originalRotations = new Quaternion[_bodyJoints.Length];
            for (int i = 0; i < _bodyJoints.Length; i++)
            {
                _originalRotations[i] = _bodyJoints[i] != null
                    ? _bodyJoints[i].targetRotation
                    : Quaternion.identity;
            }
        }

        #endregion

        #region Ragdoll Control

        private void ActivateRagdoll()
        {
            _isRagdoll = true;
            _balanced = false;

            for (int j = 0; j < _bodyJoints.Length; j++)
            {
                if (_bodyJoints[j] != null)
                {
                    // APR_Root（connectedBody=null）には位置ドライブを設定しない
                    // 設定するとワールド原点に引き寄せられるバグが発生する
                    if (j == IndexRoot)
                    {
                        _bodyJoints[j].angularXDrive = _driveOff;
                        _bodyJoints[j].angularYZDrive = _driveOff;
                    }
                    else
                    {
                        _bodyJoints[j].slerpDrive = _driveOff;
                        _bodyJoints[j].xDrive = _driveOff;
                        _bodyJoints[j].yDrive = _driveOff;
                        _bodyJoints[j].zDrive = _driveOff;
                    }
                }
            }

            for (int i = 0; i < _bodyRigidbodies.Length; i++)
            {
                if (_bodyRigidbodies[i] != null)
                {
                    _bodyRigidbodies[i].isKinematic = false;
                    _bodyRigidbodies[i].useGravity = true;
                    if (i == IndexRoot)
                    {
                        _bodyRigidbodies[i].constraints = RigidbodyConstraints.None;
                    }

                    _bodyRigidbodies[i].WakeUp();
                }
            }

            // 歩行ステップをリセット
            _stepRight = false;
            _stepLeft = false;
            _stepRTimer = 0f;
            _stepLTimer = 0f;
            _alertLegRight = false;
            _alertLegLeft = false;
        }

        private void DeactivateRagdoll()
        {
            _isRagdoll = false;
            _balanced = true;

            if (_bodyRigidbodies[IndexRoot] != null)
            {
                _bodyRigidbodies[IndexRoot].isKinematic = false;
                _bodyRigidbodies[IndexRoot].useGravity = true;
            }

            for (int i = 0; i < _bodyRigidbodies.Length; i++)
            {
                if (_bodyRigidbodies[i] != null)
                {
                    _bodyRigidbodies[i].isKinematic = false;
                    _bodyRigidbodies[i].useGravity = true;
                }
            }

            // Root: バランスドライブ（回転のみ、位置ドライブはゼロ）
            _bodyJoints[IndexRoot].angularXDrive = _balanceOn;
            _bodyJoints[IndexRoot].angularYZDrive = _balanceOn;
            // 位置ドライブをゼロクリア（connectedBody=nullなので原点に引っ張られる防止）
            var zeroLinearDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
            _bodyJoints[IndexRoot].xDrive = zeroLinearDrive;
            _bodyJoints[IndexRoot].yDrive = zeroLinearDrive;
            _bodyJoints[IndexRoot].zDrive = zeroLinearDrive;

            // Body: コアスティフネス（APRのResetPlayerPose対応）
            if (IndexBody < _bodyJoints.Length && _bodyJoints[IndexBody] != null)
            {
                _bodyJoints[IndexBody].angularXDrive = _coreStiffness;
                _bodyJoints[IndexBody].angularYZDrive = _coreStiffness;
            }

            // Head
            _bodyJoints[IndexHead].angularXDrive = _poseOn;
            _bodyJoints[IndexHead].angularYZDrive = _poseOn;

            // Arms
            _bodyJoints[IndexUpperRightARM].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperRightARM].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerRightARM].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerRightARM].angularYZDrive = _poseOn;
            _bodyJoints[IndexUpperLeftARM].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperLeftARM].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerLeftARM].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerLeftARM].angularYZDrive = _poseOn;

            // Legs
            _bodyJoints[IndexUpperRightLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperRightLeg].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerRightLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerRightLeg].angularYZDrive = _poseOn;
            _bodyJoints[IndexUpperLeftLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexUpperLeftLeg].angularYZDrive = _poseOn;
            _bodyJoints[IndexLowerLeftLeg].angularXDrive = _poseOn;
            _bodyJoints[IndexLowerLeftLeg].angularYZDrive = _poseOn;

            // Feet
            if (IndexRightFoot < _bodyJoints.Length && _bodyJoints[IndexRightFoot] != null)
            {
                _bodyJoints[IndexRightFoot].angularXDrive = _poseOn;
                _bodyJoints[IndexRightFoot].angularYZDrive = _poseOn;
            }

            if (IndexLeftFoot < _bodyJoints.Length && _bodyJoints[IndexLeftFoot] != null)
            {
                _bodyJoints[IndexLeftFoot].angularXDrive = _poseOn;
                _bodyJoints[IndexLeftFoot].angularYZDrive = _poseOn;
            }

            // Hands
            if (IndexRightHand < _bodyJoints.Length && _bodyJoints[IndexRightHand] != null)
            {
                _bodyJoints[IndexRightHand].angularXDrive = _poseOn;
                _bodyJoints[IndexRightHand].angularYZDrive = _poseOn;
            }

            if (IndexLeftHand < _bodyJoints.Length && _bodyJoints[IndexLeftHand] != null)
            {
                _bodyJoints[IndexLeftHand].angularXDrive = _poseOn;
                _bodyJoints[IndexLeftHand].angularYZDrive = _poseOn;
            }

            ResetPose();
        }

        #endregion

        #region Animation-Target Following (Phase 2)

        private void UpdateStateBlending(PlayerState state, float deltaTime)
        {
            float targetBalancePriority;
            float targetPoseStiffness;

            switch (state)
            {
                case PlayerState.Walking:
                    targetBalancePriority = _context.Profile.walkingBalancePriority;
                    targetPoseStiffness = _context.Profile.walkingPoseStiffnessMultiplier;
                    break;
                case PlayerState.Idle:
                default:
                    targetBalancePriority = _context.Profile.idleBalancePriority;
                    targetPoseStiffness = _context.Profile.idlePoseStiffnessMultiplier;
                    break;
            }

            float blendSpeed = _context.Profile.stateBlendSpeed * deltaTime;
            _currentBalancePriority = Mathf.Lerp(_currentBalancePriority, targetBalancePriority, blendSpeed);
            _currentPoseStiffnessMultiplier =
                Mathf.Lerp(_currentPoseStiffnessMultiplier, targetPoseStiffness, blendSpeed);
        }

        private void ApplyBlendedJointDrives()
        {
            if (_bodyJoints == null || _isRagdoll)
                return;

            // 前tick値と変化がなければPhysXへの書き込みをスキップ（BN-2対策）
            if (Mathf.Abs(_currentPoseStiffnessMultiplier - _lastAppliedPoseMultiplier) < 0.001f)
                return;
            _lastAppliedPoseMultiplier = _currentPoseStiffnessMultiplier;

            float adjustedLimbStrength = _context.Profile.limbStrength * _currentPoseStiffnessMultiplier;
            float adjustedPoseDamper = adjustedLimbStrength * _context.Profile.poseDamperRatio;
            JointDrive adjustedPoseOn = JointConfigurator.CreateJointDrive(adjustedLimbStrength, adjustedPoseDamper);
            ApplyJointDrive(IndexUpperRightARM, adjustedPoseOn);
            ApplyJointDrive(IndexLowerRightARM, adjustedPoseOn);
            ApplyJointDrive(IndexUpperLeftARM, adjustedPoseOn);
            ApplyJointDrive(IndexLowerLeftARM, adjustedPoseOn);

            ApplyJointDrive(IndexUpperRightLeg, adjustedPoseOn);
            ApplyJointDrive(IndexLowerRightLeg, adjustedPoseOn);
            ApplyJointDrive(IndexUpperLeftLeg, adjustedPoseOn);
            ApplyJointDrive(IndexLowerLeftLeg, adjustedPoseOn);

            // Hands
            ApplyJointDrive(IndexRightHand, adjustedPoseOn);
            ApplyJointDrive(IndexLeftHand, adjustedPoseOn);
        }

        private void ApplyJointDrive(int index, JointDrive drive)
        {
            if (index < _bodyJoints.Length && _bodyJoints[index] != null)
            {
                _bodyJoints[index].angularXDrive = drive;
                _bodyJoints[index].angularYZDrive = drive;
            }
        }

        #endregion

        #region Motion Control Methods

        /// <summary>
        /// APR方式: 直接速度操作（Velocity Lerp）
        /// PID制御を廃止し、linearVelocityをLerpで目標速度に近づける
        /// </summary>
        internal static Vector3 CalculateMovementTargetVelocity(
            Vector3 currentVelocity,
            Vector3 moveDirection,
            float speed,
            float controlMultiplier)
        {
            Vector3 currentHorizontalVelocity = new Vector3(currentVelocity.x, 0f, currentVelocity.z);
            Vector3 desiredHorizontalVelocity = new Vector3(moveDirection.x, 0f, moveDirection.z) * speed;
            Vector3 controlledHorizontalVelocity = Vector3.Lerp(
                currentHorizontalVelocity,
                desiredHorizontalVelocity,
                Mathf.Clamp01(controlMultiplier));
            return controlledHorizontalVelocity + new Vector3(0f, currentVelocity.y, 0f);
        }

        internal static float CalculateWalkInputAmount(Vector3 moveDirection)
        {
            return Mathf.Clamp01(new Vector2(moveDirection.x, moveDirection.z).magnitude);
        }

        private void ApplyMovementForce(Vector3 moveDirection, float controlMultiplier)
        {
            if (!HasAuthoritativePhysics())
                return;

            var rb = _bodyRigidbodies[IndexRoot];

            float speed = _context.MoveSpeed;
            Vector3 targetVel = CalculateMovementTargetVelocity(
                rb.linearVelocity,
                moveDirection,
                speed,
                controlMultiplier);

            // 入力から実際の速度変化までの遅延（1.0f:ロボット的、0.8f:バランスポイント、0.3f:氷の上を滑るような感触）
            rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, targetVel, _context.Profile.movementVelocityLerp);
        }

        /// <summary>
        /// APR方式の歩行ステップサイクル
        /// 右脚/左脚を交互にtargetRotationで曲げ、タイマーで切り替え
        /// 足には下方向の力（FeetMountForce）を常に適用して接地を保つ
        /// </summary>
        private void ProcessWalking(Vector3 moveDirection, float deltaTime)
        {
            // 前進/後退の判定: ルートの前方ベクトルと移動方向の内積で決定
            // APRController L418-536 の forwardIsCameraDirection モードを拡張
            // この2つは tick をまたいで持ち越さない（毎回ここで決まる）のでローカル変数。
            // ステップサイクルを保持する _stepRight/_stepLeft/_stepRTimer/_stepLTimer/_alertLeg* とは性質が違う。
            bool walkForward = false;
            bool walkBackward = false;
            if (moveDirection.sqrMagnitude > 0.01f)
            {
                Vector3 rootForward = _bodyRigidbodies[IndexRoot].transform.forward;
                rootForward.y = 0f;
                float dot = Vector3.Dot(moveDirection.normalized, rootForward.normalized);

                if (dot >= 0f)
                {
                    walkForward = true;
                }
                else
                {
                    walkBackward = true;
                }
            }

            // APRController L355-363: 移動していない時はステップ状態をリセット
            if (!walkForward && !walkBackward)
            {
                _stepRight = false;
                _stepLeft = false;
                _stepRTimer = 0;
                _stepLTimer = 0;
                _alertLegRight = false;
                _alertLegLeft = false;
            }

            // 足のZ座標は以下の4判定で参照するが、その間に足自体は動かない（変わるのは bool フラグだけ）。
            // GameObject.transform → Transform.position のネイティブ取得を8回から2回に減らす。
            float rightFootZ = _bodyParts[IndexRightFoot].transform.position.z;
            float leftFootZ = _bodyParts[IndexLeftFoot].transform.position.z;

            // APRController L900-917: 前進時 — 後ろにある足をステップさせる
            if (walkForward)
            {
                // right leg
                if (rightFootZ < leftFootZ && !_stepLeft && !_alertLegRight)
                {
                    _stepRight = true;
                    _alertLegRight = true;
                    _alertLegLeft = true;
                }

                // left leg
                if (rightFootZ > leftFootZ && !_stepRight && !_alertLegLeft)
                {
                    _stepLeft = true;
                    _alertLegLeft = true;
                    _alertLegRight = true;
                }
            }

            // APRController L919-936: 後退時 — 前にある足をステップさせる（前後逆）
            if (walkBackward)
            {
                // right leg
                if (rightFootZ > leftFootZ && !_stepLeft && !_alertLegRight)
                {
                    _stepRight = true;
                    _alertLegRight = true;
                    _alertLegLeft = true;
                }

                // left leg
                if (rightFootZ < leftFootZ && !_stepRight && !_alertLegLeft)
                {
                    _stepLeft = true;
                    _alertLegLeft = true;
                    _alertLegRight = true;
                }
            }

            float stepHeight = _context.Profile.stepHeight;
            float feetForce = _context.Profile.feetMountForce;
            float stepDuration = _context.Profile.stepDuration;
            float walkInputAmount = CalculateWalkInputAmount(moveDirection);
            float stepDeltaTime = deltaTime * walkInputAmount;
            float scaledStepHeight = stepHeight * walkInputAmount;

            // 左右のステップは、脚のインデックス・タイマー・アイドル復帰の Lerp 速度が違うだけで
            // 手順も係数も同一。ローカル関数に畳んで、片側だけ直す事故を防ぐ。
            // 上の共有ローカル（walkForward / scaledStepHeight 等）はクロージャで捕捉する。
            //
            // 【アイドル復帰の Lerp 速度が左右非対称なことについて】
            //   右 = 8f / 17f、左 = 7f / 18f。これは元アセット APRController.cs の
            //   L979-980 / L1029-1030 が最初から非対称で、それを忠実に移植したもの。
            //   このプロジェクトでの調整ミスでも転記ミスでもない（2026-08-08 に原本と照合して確認）。
            //   [※未確認] 上流の作者が意図したのか単なる打ち間違いかは不明。
            //   対称化すると歩行の見た目が変わるうえ原本との差分が増えるため、値は変えていない。
            //   変えるなら、差が体感できるかを実測してから判断すること。
            void ProcessStepSide(
                ref bool isStepping,
                ref float stepTimer,
                ref bool nextSideStep,
                int upperLeg,
                int lowerLeg,
                int oppositeUpperLeg,
                int steppingFoot,
                float idleLerpUpper,
                float idleLerpLower)
            {
                if (isStepping)
                {
                    stepTimer += stepDeltaTime;

                    // 踏み出す側の足を下へ押さえる
                    AddFeetDownForce(steppingFoot, feetForce, deltaTime);

                    if (walkForward)
                    {
                        AddTargetPitch(upperLeg, 0.09f * scaledStepHeight);
                        AddTargetPitch(lowerLeg, -0.09f * scaledStepHeight * 2);
                        AddTargetPitch(oppositeUpperLeg, -0.12f * scaledStepHeight / 2);
                    }

                    if (walkBackward)
                    {
                        AddTargetPitch(lowerLeg, -0.07f * scaledStepHeight * 2);
                        AddTargetPitch(oppositeUpperLeg, 0.02f * scaledStepHeight / 2);
                    }

                    // ステップ時間を使い切ったら反対側へ渡す
                    if (stepTimer > stepDuration)
                    {
                        stepTimer = 0;
                        isStepping = false;

                        if (walkForward || walkBackward)
                        {
                            nextSideStep = true;
                        }
                    }
                }
                else
                {
                    // reset to idle
                    _bodyJoints[upperLeg].targetRotation = Quaternion.Lerp(
                        _bodyJoints[upperLeg].targetRotation, _originalRotations[upperLeg], idleLerpUpper * deltaTime);
                    _bodyJoints[lowerLeg].targetRotation = Quaternion.Lerp(
                        _bodyJoints[lowerLeg].targetRotation, _originalRotations[lowerLeg], idleLerpLower * deltaTime);

                    // feet force down（非ステップ側では両足に掛ける。元実装どおり）
                    AddFeetDownForce(IndexRightFoot, feetForce, deltaTime);
                    AddFeetDownForce(IndexLeftFoot, feetForce, deltaTime);
                }
            }

            // APRController L939-984: Step right
            ProcessStepSide(
                ref _stepRight, ref _stepRTimer, ref _stepLeft,
                IndexUpperRightLeg, IndexLowerRightLeg, IndexUpperLeftLeg, IndexRightFoot,
                idleLerpUpper: 8f, idleLerpLower: 17f);

            // APRController L989-1034: Step left
            ProcessStepSide(
                ref _stepLeft, ref _stepLTimer, ref _stepRight,
                IndexUpperLeftLeg, IndexLowerLeftLeg, IndexUpperRightLeg, IndexLeftFoot,
                idleLerpUpper: 7f, idleLerpLower: 18f);
        }

        /// <summary>
        /// ジョイントの targetRotation の x 成分（ピッチ相当）だけを加算する。
        ///
        /// 元は 1行200文字超の <c>new Quaternion(joint.targetRotation.x + delta, joint.targetRotation.y, ...)</c>
        /// が並んでおり、同じ targetRotation を1文の中で4回読み直していた。
        /// 意味は変えずに、読める長さと1回の読み取りに落としている。
        /// </summary>
        private void AddTargetPitch(int jointIndex, float delta)
        {
            Quaternion target = _bodyJoints[jointIndex].targetRotation;
            _bodyJoints[jointIndex].targetRotation = new Quaternion(target.x + delta, target.y, target.z, target.w);
        }

        /// <summary>
        /// クライアントの視覚再現用更新。
        /// ホストの UpdatePhysics() とは異なり、以下を行わない:
        ///   - バランス判定（IsGrounded / CalculateBalanceState）
        ///   - ラグドール状態の切り替え（ActivateRagdoll / DeactivateRagdoll）
        ///   - JointDrive の動的変更（ApplyBlendedJointDrives）
        /// これらはホスト側の [Networked] CurrentState で同期されるため、
        /// クライアントで独立に判定すると状態フリップ→JointDrive振動の原因になる。
        ///
        /// クライアントで行うのは:
        ///   - ルート回転の追従（facingDirection ベース）
        ///   - 歩行ステップサイクルの視覚再現
        ///   - パンチポーズの視覚再現
        /// </summary>
        public void UpdatePhysicsVisualOnly(PlayerState state, RagdollCommand command, float deltaTime)
        {
            _wantsPunchRight = command.IsPunchingRight;
            _wantsPunchLeft = command.IsPunchingLeft;
            _wantsReachRight = command.IsGrabbingRight;
            _wantsReachLeft = command.IsGrabbingLeft;

            // ── バランス判定・ラグドール切り替えはスキップ ──
            // ホストの CurrentState を信頼する
            bool isRagdollFromHost = (state == PlayerState.Ragdoll);

            // ラグドール状態が変化した場合のみJointDriveを切り替え
            if (isRagdollFromHost != _isRagdoll)
            {
                _isRagdoll = isRagdollFromHost;
                if (_isRagdoll)
                {
                    // ラグドール化: ドライブを弱める（ActivateRagdoll相当）
                    for (int j = 0; j < _bodyJoints.Length; j++)
                    {
                        if (_bodyJoints[j] == null)
                            continue;
                        if (j == IndexRoot)
                        {
                            _bodyJoints[j].angularXDrive = _driveOff;
                            _bodyJoints[j].angularYZDrive = _driveOff;
                        }
                        else
                        {
                            _bodyJoints[j].slerpDrive = _driveOff;
                        }
                    }
                }
                else
                {
                    // 復帰: ポーズドライブを再適用（DeactivateRagdoll相当）
                    ApplyInitialDrives();
                }
            }

            if (!_isRagdoll)
            {
                // クライアント側の視覚再現:
                //   ルート回転 → ApplySoftRootCorrection が担当（ここでは変更しない）
                //     理由: UpdateRootRotation がルートジョイント(spring=5000)の
                //     targetRotation を変更 → 巨大トルク → クライアントは重力OFF/接地なし
                //     → ルートが大きく動く → プロキシ補正が引き戻す → ラバーバンディング
                //
                //   脚アニメーション → ProcessWalking で脚のtargetRotationのみ変更
                //     脚ジョイント(spring=250)はルートの1/20なので反作用は小さい
                //     AddFeetDownForce はHasAuthoritativePhysics()ガードで実行されない
                switch (state)
                {
                    case PlayerState.Walking:
                        ProcessWalking(command.MoveDirection, deltaTime);
                        break;
                    case PlayerState.Idle:
                        ProcessWalking(command.MoveDirection, deltaTime);
                        break;
                }
            }
        }

        // ジャンプ直後の上昇中はこの速度を超えていれば「まだ跳んでいる最中」とみなし、
        // 再発火をブロックする（連打による空中2段ジャンプ対策）。バランス維持の
        // 上下方向の揺れはこれよりずっと小さいため、通常時の誤検知は起きない。
        private const float AscendingVelocityGuard = 1.5f;

        private void ProcessJumpingPhysics()
        {
            // ラッチ: このジャンプで既に初速を与えていたら何もしない。
            // 接地中 Jumping が続く限り毎tick呼ばれるが、初速付与は1回だけにする。
            if (_jumpVelocityApplied)
                return;

            var rigidBody = _bodyRigidbodies[IndexRoot];

            if (HasAuthoritativePhysics())
            {
                // 発火直前のガード: 離陸直後はコヨーテタイム(0.1s)とラグドール足の物理的な
                // 遅れにより isPlayerGrounded が一瞬 true を維持し続ける。この間にボタンを
                // 離して押し直す（連打）と、ラッチは release で既に解除されているため
                // 再発火してしまい、上昇中に2回目の初速が加算されて大ジャンプになる
                // （2026-07-09 実機で確認）。足の接地状態は信号源として信用できないため、
                // 「上昇中は再ジャンプできない」という物理的に自明な制約を発火条件に直接
                // 追加する。これは再武装ロジックとは独立したガードなので、再武装の仕組みを
                // どう変えても揺らがない。
                if (rigidBody.linearVelocity.y > AscendingVelocityGuard)
                    return;

                var v3 = rigidBody.transform.up * _context.Profile.jumpForce;
                v3.x = rigidBody.linearVelocity.x;
                v3.z = rigidBody.linearVelocity.z;
                rigidBody.linearVelocity = v3;
                _jumpVelocityApplied = true;
            }
        }

        /// <summary>
        /// 胴体ベンド（マウスY由来）と胴体ロール（Alt+MouseX由来）を常時適用。非ラグドール時に毎tick呼ばれる。
        /// APR の PlayerReach Body Bending（APR_Parts[1].targetRotation = new Quaternion(MouseYAxisBody,0,0,1)）に対応。
        /// bodyBend は APR と同じ ±0.9 の絶対累積値（InputCollector で生成、LookDirection.x 経由）。
        /// bodyRollDegrees は Alt+MouseX で累積した度数（profile 既定で ±60度）。
        /// 当方リグの baseline を尊重するため original に合成する。
        /// </summary>
        private void UpdateBodyLook(float bodyBend, float bodyRollDegrees)
        {
            if (IndexBody >= _bodyJoints.Length || _bodyJoints[IndexBody] == null)
                return;

            _bodyJoints[IndexBody].targetRotation =
                _originalRotations[IndexBody]
                * new Quaternion(bodyBend, 0f, 0f, 1f)
                * Quaternion.Euler(0f, 0f, bodyRollDegrees);
        }

        private void ProcessReachingPhysics(Vector2 lookDirection)
        {
            if (_isRagdoll || _bodyJoints == null || _bodyRigidbodies == null)
                return;

            // 上腕ベース角: 8f = パンチrelease上腕X = 前方90度相当（当方リグ検証済み規約）
            float upperArmBasePitch = _context.Profile.reachUpperArmBasePitch;
            // 腕の上下: APR MouseYAxisArms(LookDirection.y) で base から振る
            float armInputLimit = Mathf.Max(0f, _context.Profile.reachArmInputLimit);
            float upperArmPitchPerUnit = _context.Profile.reachUpperArmPitchPerUnit;
            float upperArmMinPitch = _context.Profile.reachUpperArmMinPitch;
            float upperArmMaxPitch = _context.Profile.reachUpperArmMaxPitch;
            float lowerArmPitch = _context.Profile.reachLowerArmPitch;

            float armReach = Mathf.Clamp(lookDirection.y, -armInputLimit, armInputLimit);
            float upperArmPitch = Mathf.Clamp(
                upperArmBasePitch - armReach * upperArmPitchPerUnit,
                upperArmMinPitch, upperArmMaxPitch);

            // ReachPose アセット使用時の上下スイング角(度)。
            // 軸はアセットデルタ自身の回転軸（rest→リーチポーズの「腕を上げる」軸）を使うため、
            // ここでは角度だけを計算する。正=ポーズの延長方向へさらに上げる、負=restへ戻す方向へ下げる。
            // armReach は ±armInputLimit で既にクランプ済みなので、振り幅は自然に有界。
            float armSwingDegrees = armReach * upperArmPitchPerUnit;

            if (_wantsReachRight)
            {
                _wasReachingRight = true;
                ApplyReachPose(true, upperArmPitch, lowerArmPitch, armSwingDegrees);
            }

            if (_wantsReachLeft)
            {
                _wasReachingLeft = true;
                ApplyReachPose(false, upperArmPitch, lowerArmPitch, armSwingDegrees);
            }
        }

        // 左右の腕を ActionPoseAsset の「rest 相対デルタ」で駆動する（データ駆動ポーズ）。
        // 左右はミラー計算せず、アセットに両側(UpperRightArm/UpperLeftArm 等)を明示登録する方針。
        // → モデルごとに joint ローカル軸が違っても、各側を実機で録り直せば正しく決まる。
        // アセット未割当・該当骨未登録なら、従来のパラメトリック値にフォールバックする。
        private void ApplyReachPose(bool isRight, float upperArmPitch, float lowerArmPitch, float armSwingDegrees = 0f)
        {
            LogicalJoint upperJoint = isRight ? LogicalJoint.UpperRightArm : LogicalJoint.UpperLeftArm;
            LogicalJoint lowerJoint = isRight ? LogicalJoint.LowerRightArm : LogicalJoint.LowerLeftArm;
            int upperArmIndex = (int)upperJoint;
            int lowerArmIndex = (int)lowerJoint;
            float side = isRight ? 1f : -1f;

            // Reach中は現在の profile 値から毎回 drive を作る。Play中の tuning と有限 maximumForce を即反映するため。
            JointDrive upperReachDrive = JointConfigurator.CreateJointDrive(
                _context.Profile.reachUpperArmJointSpring,
                _context.Profile.reachUpperArmJointDamper,
                _context.Profile.reachUpperArmJointMaxForce);
            JointDrive lowerReachDrive = JointConfigurator.CreateJointDrive(
                _context.Profile.reachLowerArmJointSpring,
                _context.Profile.reachLowerArmJointDamper,
                _context.Profile.reachLowerArmJointMaxForce);

            if (upperArmIndex < _bodyJoints.Length && _bodyJoints[upperArmIndex] != null)
            {
                _bodyJoints[upperArmIndex].angularXDrive = upperReachDrive;
                _bodyJoints[upperArmIndex].angularYZDrive = upperReachDrive;
            }

            if (lowerArmIndex < _bodyJoints.Length && _bodyJoints[lowerArmIndex] != null)
            {
                _bodyJoints[lowerArmIndex].angularXDrive = lowerReachDrive;
                _bodyJoints[lowerArmIndex].angularYZDrive = lowerReachDrive;
            }

            if (upperArmIndex < _bodyJoints.Length && _bodyJoints[upperArmIndex] != null)
            {
                // swing のミラーは行わない（左右のアセットデルタが各側の軸を持つため自動で正しくなる）
                _bodyJoints[upperArmIndex].targetRotation =
                    _originalRotations[upperArmIndex]
                    * ResolveReachDelta(
                        upperJoint,
                        Quaternion.Euler(upperArmPitch * side, 0f, 0f),
                        armSwingDegrees);
            }

            if (lowerArmIndex < _bodyJoints.Length && _bodyJoints[lowerArmIndex] != null)
            {
                // 下腕（肘）はアセットポーズ固定のまま（スイングは上腕のみ。第一歩として安全側）
                _bodyJoints[lowerArmIndex].targetRotation =
                    _originalRotations[lowerArmIndex]
                    * ResolveReachDelta(lowerJoint, Quaternion.Euler(lowerArmPitch * side, 0f, 0f), 0f);
            }
        }

        // ActionPoseAsset に登録があればその rest 相対デルタ(Euler)を、無ければ fallback を返す。
        // プレビュー中はツール指定のアセットを優先する（編集中の値を即反映するため）。
        //
        // swingDegrees: マウスY/右スティックY 由来の腕上下スイング角(度)。
        // 回転軸は「アセットデルタ自身の回転軸」を使う。アセットのデルタは rest→リーチポーズ
        // （腕を垂らした姿勢→腕を前方へ上げた姿勢）への回転なので、その軸がこのリグにおける
        // 「腕を上げ下げする軸」そのもの。固定軸（rest X 等）で回すと、リグの joint 軸の向き次第で
        // 開閉やクロスに化ける（実プレイで2回確認済み）。同軸回転なので合成順は可換で、
        // 正=ポーズの延長方向へさらに上げる / 負=rest へ戻す方向へ下げる、が保証される。
        private Quaternion ResolveReachDelta(LogicalJoint joint, Quaternion fallback, float swingDegrees)
        {
            ActionPoseAsset reachPose = _posePreviewActive && _posePreviewAsset != null
                ? _posePreviewAsset
                : _context.ReachPose;

            if (reachPose != null && reachPose.TryGetDelta(joint, out Vector3 eulerDelta))
            {
                Quaternion assetDelta = Quaternion.Euler(eulerDelta);

                if (Mathf.Abs(swingDegrees) > 0.01f)
                {
                    assetDelta.ToAngleAxis(out float assetAngle, out Vector3 assetAxis);
                    // デルタがほぼ無回転だと軸が不定になるため、その場合はスイングを適用しない
                    if (assetAngle > 1f && !float.IsNaN(assetAxis.x) && !float.IsInfinity(assetAxis.x))
                    {
                        return Quaternion.AngleAxis(swingDegrees, assetAxis) * assetDelta;
                    }
                }

                return assetDelta;
            }

            return fallback;
        }

        /// <summary>
        /// ポーズオーサリングツール用: Reach ポーズのプレビューを ON/OFF する。
        /// ON の間は入力/状態に関係なく、指定側の腕に <paramref name="asset"/> のポーズを毎tick適用する。
        /// OFF にすると両腕のドライブと targetRotation を通常状態へ戻す。
        /// </summary>
        public void SetReachPosePreview(bool active, ActionPoseAsset asset, bool isRight)
        {
            _posePreviewActive = active;
            _posePreviewAsset = asset;
            _posePreviewRight = isRight;

            if (!active && _bodyJoints != null)
            {
                RestoreArmDrives(true);
                RestoreArmDrives(false);
                ResetArmTargetToOriginal(IndexUpperRightARM);
                ResetArmTargetToOriginal(IndexLowerRightARM);
                ResetArmTargetToOriginal(IndexUpperLeftARM);
                ResetArmTargetToOriginal(IndexLowerLeftARM);
            }
        }

        private void ResetArmTargetToOriginal(int index)
        {
            if (index < _bodyJoints.Length && _bodyJoints[index] != null)
            {
                _bodyJoints[index].targetRotation = _originalRotations[index];
            }
        }

        private void RestoreArmDrives(bool isRight)
        {
            int upperArmIndex = isRight ? IndexUpperRightARM : IndexUpperLeftARM;
            int lowerArmIndex = isRight ? IndexLowerRightARM : IndexLowerLeftARM;
            if (upperArmIndex < _bodyJoints.Length && _bodyJoints[upperArmIndex] != null)
            {
                _bodyJoints[upperArmIndex].angularXDrive = _poseOn;
                _bodyJoints[upperArmIndex].angularYZDrive = _poseOn;
            }

            if (lowerArmIndex < _bodyJoints.Length && _bodyJoints[lowerArmIndex] != null)
            {
                _bodyJoints[lowerArmIndex].angularXDrive = _poseOn;
                _bodyJoints[lowerArmIndex].angularYZDrive = _poseOn;
            }
        }

        private void ProcessPunchingPhysics()
        {
            if (_isRagdoll || _bodyJoints == null || _bodyRigidbodies == null)
            {
                Debug.LogWarning("[PUNCH_DEBUG] Cannot process punching physics: Ragdoll or body joints are null.");
                return;
            }

            if (!_punchingRight && _wantsPunchRight)
            {
                Debug.Log("[PUNCH_DEBUG] Punch windup (right)");
                _punchingRight = true;
                ApplyPunchWindup(true);
            }
            else if (_punchingRight && !_wantsPunchRight)
            {
                Debug.Log("[PUNCH_DEBUG] Punch release (right)");
                _punchingRight = false;
                ApplyPunchRelease(true);
            }

            if (!_punchingLeft && _wantsPunchLeft)
            {
                Debug.Log("[PUNCH_DEBUG] Punch windup (left)");
                _punchingLeft = true;
                ApplyPunchWindup(false);
            }
            else if (_punchingLeft && !_wantsPunchLeft)
            {
                Debug.Log("[PUNCH_DEBUG] Punch release (left)");
                _punchingLeft = false;
                ApplyPunchRelease(false);
            }
        }

        private void ApplyPunchWindup(bool isRight)
        {
            int upperArmIndex = isRight ? IndexUpperRightARM : IndexUpperLeftARM;
            int lowerArmIndex = isRight ? IndexLowerRightARM : IndexLowerLeftARM;
            float side = isRight ? 1f : -1f;

            if (upperArmIndex < _bodyJoints.Length && _bodyJoints[upperArmIndex] != null)
            {
                _bodyJoints[upperArmIndex].targetRotation =
                    _originalRotations[upperArmIndex] * Quaternion.Euler(-20f, 25f * side, 0f);
            }

            if (lowerArmIndex < _bodyJoints.Length && _bodyJoints[lowerArmIndex] != null)
            {
                _bodyJoints[lowerArmIndex].targetRotation =
                    _originalRotations[lowerArmIndex] * Quaternion.Euler(-70f, 0f, 0f);
            }
        }

        private void ApplyPunchRelease(bool isRight)
        {
            int upperArmIndex = isRight ? IndexUpperRightARM : IndexUpperLeftARM;
            int lowerArmIndex = isRight ? IndexLowerRightARM : IndexLowerLeftARM;
            float side = isRight ? 1f : -1f;

            if (upperArmIndex < _bodyJoints.Length && _bodyJoints[upperArmIndex] != null)
            {
                _bodyJoints[upperArmIndex].targetRotation =
                    _originalRotations[upperArmIndex] * Quaternion.Euler(8f, -10f * side, 0f);
            }

            if (lowerArmIndex < _bodyJoints.Length && _bodyJoints[lowerArmIndex] != null)
            {
                _bodyJoints[lowerArmIndex].targetRotation =
                    _originalRotations[lowerArmIndex] * Quaternion.Euler(30f, 0f, 0f);
            }

            if (lowerArmIndex < _bodyRigidbodies.Length && _bodyRigidbodies[lowerArmIndex] != null)
            {
                if (HasAuthoritativePhysics())
                {
                    Vector3 forward = _bodyRigidbodies[IndexRoot] != null
                        ? _bodyRigidbodies[IndexRoot].transform.forward
                        : Vector3.forward;
                    _bodyRigidbodies[lowerArmIndex]
                        .AddForce(forward * _context.Profile.punchImpulse, ForceMode.Impulse);
                }
            }

            if (isRight)
            {
                _rightPunchRecoveryDelay = _context.Profile.punchRecoveryDelaySeconds;
            }
            else
            {
                _leftPunchRecoveryDelay = _context.Profile.punchRecoveryDelaySeconds;
            }
        }

        private void UpdatePunchRecovery(float deltaTime)
        {
            _rightPunchRecoveryDelay = Mathf.Max(0f, _rightPunchRecoveryDelay - deltaTime);
            _leftPunchRecoveryDelay = Mathf.Max(0f, _leftPunchRecoveryDelay - deltaTime);

            if (!_punchingRight && _rightPunchRecoveryDelay <= 0f)
            {
                LerpArmToOriginal(IndexUpperRightARM, deltaTime);
                LerpArmToOriginal(IndexLowerRightARM, deltaTime);
            }

            if (!_punchingLeft && _leftPunchRecoveryDelay <= 0f)
            {
                LerpArmToOriginal(IndexUpperLeftARM, deltaTime);
                LerpArmToOriginal(IndexLowerLeftARM, deltaTime);
            }
        }

        private void LerpArmToOriginal(int jointIndex, float deltaTime)
        {
            if (jointIndex < _bodyJoints.Length && _bodyJoints[jointIndex] != null)
            {
                _bodyJoints[jointIndex].targetRotation = Quaternion.Lerp(
                    _bodyJoints[jointIndex].targetRotation,
                    _originalRotations[jointIndex],
                    _context.Profile.punchRecoveryLerpSpeed * deltaTime
                );
            }
        }

        private bool HasAuthoritativePhysics()
        {
            if (_context == null)
                return false;
            // Forecast Physicsモード: 全クライアントで物理計算を実行
            if (_context.UseForecastPhysics)
                return true;
            return _context.HasStateAuthority;
        }

        private void AddFeetDownForce(int footIndex, float feetForce, float deltaTime)
        {
            if (!HasAuthoritativePhysics())
                return;

            if (footIndex < 0 || footIndex >= _bodyRigidbodies.Length)
                return;

            var rb = _bodyRigidbodies[footIndex];
            if (rb == null)
                return;

            rb.AddForce(-Vector3.up * feetForce * deltaTime, ForceMode.Impulse);
        }

        #endregion

        #region Balance Calculation Methods

        /// <summary>
        /// 全Rigidbodyの質量加重平均から重心（Center of Mass）を計算
        /// </summary>
        private Vector3 CalculateCenterOfMass()
        {
            if (_bodyRigidbodies == null)
                return Vector3.zero;

            Vector3 com = Vector3.zero;
            float totalMass = 0f;

            foreach (var rb in _bodyRigidbodies)
            {
                if (rb != null)
                {
                    com += rb.worldCenterOfMass * rb.mass;
                    totalMass += rb.mass;
                }
            }

            if (totalMass > 0f)
            {
                com /= totalMass;
            }

            return com;
        }

        /// <summary>
        /// 両足の位置から支持基底面（Support Polygon）の中心を計算
        /// </summary>
        private Vector3 CalculateSupportPolygonCenter()
        {
            if (_bodyParts == null)
                return Vector3.zero;

            Vector3 leftFootPos = _bodyParts[IndexLeftFoot]?.transform.position ?? Vector3.zero;
            Vector3 rightFootPos = _bodyParts[IndexRightFoot]?.transform.position ?? Vector3.zero;

            Vector3 center = (leftFootPos + rightFootPos) * 0.5f;

            return center;
        }

        /// <summary>
        /// 重心と支持基底面の関係から詳細なBalanceStateを計算
        /// Gizmo描画用にCOM解析は残すが、判定自体はAPR式（Raycast + velocity）
        /// </summary>
        private BalanceState CalculateDetailedBalanceState()
        {
            _centerOfMass = CalculateCenterOfMass();
            _supportPolygonCenter = CalculateSupportPolygonCenter();

            if (_bodyRigidbodies == null || _bodyRigidbodies[IndexRoot] == null)
            {
                return BalanceState.Balanced;
            }

            Transform rootTransform = _bodyRigidbodies[IndexRoot].transform;

            Vector3 comToSupport = _supportPolygonCenter - _centerOfMass;
            comToSupport.y = 0f;

            Vector3 localOffset = rootTransform.InverseTransformDirection(comToSupport);

            float forwardOffset = localOffset.z;
            float sideOffset = localOffset.x;

            float margin = _balanceMargin;

            if (Mathf.Abs(forwardOffset) > margin || Mathf.Abs(sideOffset) > margin)
            {
                if (Mathf.Abs(forwardOffset) >= Mathf.Abs(sideOffset))
                {
                    return forwardOffset > 0 ? BalanceState.Backward : BalanceState.Forward;
                }
                else
                {
                    return sideOffset > 0 ? BalanceState.Right : BalanceState.Left;
                }
            }

            return BalanceState.Balanced;
        }

        #endregion

        #region Utility Methods

        public bool IsGrounded()
        {
            // フット接触が確認済みの場合はRaycastをスキップ（毎tick不要）
            if (_isAnyFootGrounded)
            {
                LastRaycastHit = false;
                return true;
            }

            Ray ray = new Ray(_bodyParts[IndexRoot].transform.position, Vector3.down);
            bool raycastHit = Physics.Raycast(ray, _context.Profile.balanceHeight, _groundLayerMask);
            LastRaycastHit = raycastHit;

            return raycastHit;
        }

        /// <summary>
        /// APR式バランス判定: Raycast地面検知 + velocity
        /// COM解析結果はGizmo描画用に更新するが、判定自体はシンプルに保つ
        /// </summary>
        public bool CalculateBalanceState(bool isGrounded, PlayerState state)
        {
            if (_bodyRigidbodies == null || _bodyRigidbodies[IndexRoot] == null)
            {
                _currentBalanceState = BalanceState.Balanced;
                return isGrounded && state != PlayerState.Ragdoll;
            }

#if UNITY_EDITOR
            // Gizmo描画用にCOM解析は実行（ビルドでは不要）
            _currentBalanceState = CalculateDetailedBalanceState();
#endif

            if (_balanced)
            {
                // 空中(!isGrounded)ではバランス喪失させない（2026-07-03 変更）。
                // 空中ラグドール化はジャンプ→ロープ掴みのターザン等のアクションを潰すため、
                // 自動ラグドール化は外部から明示的に Ragdoll 状態にされた場合のみとする。
                bool shouldLoseBalance = state == PlayerState.Ragdoll;
                return !shouldLoseBalance;
            }
            else
            {
                // 速度判定はこの分岐でしか使わないので、平方根計算もここまで遅らせる。
                // （sqrMagnitude 化は境界の丸めが変わるため、あえて magnitude のまま）
                bool isLowVelocity = _bodyRigidbodies[IndexRoot].linearVelocity.magnitude < 1f;
                return isGrounded && isLowVelocity && state != PlayerState.Ragdoll;
            }
        }

        /// <summary>
        /// 外部から強制的にラグドール状態を解除する（ジャンプキーで起き上がる用）
        /// </summary>
        public void ForceDeactivateRagdoll()
        {
            DeactivateRagdoll();
        }

        /// <summary>
        /// ラグドール復帰時にポーズをリセット
        /// ルートのtargetRotation（向き）は保持し、四肢+Bodyを初期ポーズに戻す
        /// </summary>
        private void ResetPose()
        {
            for (int i = 0; i < _bodyJoints.Length; i++)
            {
                // ルートの向きは保持（復帰時に正面リセットされるのを防止）
                if (i == IndexRoot)
                    continue;

                if (_bodyJoints[i] == null)
                    continue;

                _bodyJoints[i].targetRotation = _originalRotations[i];
            }
        }

        /// <summary>
        /// 物理側が使うのは「どちらかの足が接地しているか」だけ。
        /// 左右別の接地は RagdollController の [Networked] IsLeftFootGrounded / IsRightFootGrounded が持つ。
        /// </summary>
        public void SetFootGroundedInfo(bool anyFootGrounded)
        {
            _isAnyFootGrounded = anyFootGrounded;
        }

        #endregion
    }
}
