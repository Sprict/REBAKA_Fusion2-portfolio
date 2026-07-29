using System;
using UnityEngine;
using MyFolder.Scripts.Utils;
using MyFolder.Scripts.Player.Posing;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// バランス状態を表すenum
    /// 重心と支持基底面の関係から判定される
    /// </summary>
    public enum BalanceState
    {
        Balanced,   // 安定（重心が支持基底面内）
        Forward,    // 前傾（重心が前方に逸脱）
        Backward,   // 後傾（重心が後方に逸脱）
        Left,       // 左傾（重心が左方に逸脱）
        Right       // 右傾（重心が右方に逸脱）
    }

    public class RagdollPhysics
    {
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
        private bool _isLeftFootGrounded = false;
        private bool _isRightFootGrounded = false;
        private bool _isAnyFootGrounded = false;

        // バランス計算用
        private BalanceState _currentBalanceState = BalanceState.Balanced;
        private Vector3 _centerOfMass;           // 重心位置
        private Vector3 _supportPolygonCenter;   // 支持基底面の中心
        private float _balanceMargin = 0.15f;    // バランス判定のマージン（メートル）
        private int _groundLayerMask;               // キャッシュ済みの地面レイヤーマスク

        // 歩行ステップサイクル（APR方式）
        private bool _stepRight;
        private bool _stepLeft;
        private float _stepRTimer;
        private float _stepLTimer;
        private bool _alertLegRight;
        private bool _alertLegLeft;
        private bool _walkForward;
        private bool _walkBackward;

        // Animation-Target Following (Phase 2)
        private float _currentBalancePriority = 0.8f;
        private float _currentPoseStiffnessMultiplier = 1f;
        private float _lastAppliedPoseMultiplier = -1f; // Joint書き込みスキップ用キャッシュ

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


        // インデックス定数
        private const int IndexRoot = 0;
        private const int IndexBody = 1;
        private const int IndexHead = 2;
        private const int IndexUpperRightARM = 3;
        private const int IndexLowerRightARM = 4;
        private const int IndexUpperLeftARM = 5;
        private const int IndexLowerLeftARM = 6;
        private const int IndexUpperRightLeg = 7;
        private const int IndexLowerRightLeg = 8;
        private const int IndexUpperLeftLeg = 9;
        private const int IndexLowerLeftLeg = 10;
        private const int IndexRightFoot = 11;
        private const int IndexLeftFoot = 12;
        private const int IndexRightHand = 13;
        private const int IndexLeftHand = 14;

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

        #region Constructor

        internal RagdollPhysics(IRagdollPhysicsContext context, GameObject[] bodyParts,
            Rigidbody[] bodyRigidbodies, ConfigurableJoint[] bodyJoints)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));

            Debug.Log(
                $"[RAGDOLL_DEBUG] RagdollPhysics constructor called. context: {(context != null ? "OK" : "NULL")}, bodyParts: {(bodyParts != null ? bodyParts.Length.ToString() : "NULL")}, bodyRigidbodies: {(bodyRigidbodies != null ? bodyRigidbodies.Length.ToString() : "NULL")}, bodyJoints: {(bodyJoints != null ? bodyJoints.Length.ToString() : "NULL")}");

            this._bodyParts = bodyParts;
            this._bodyRigidbodies = bodyRigidbodies;
            this._bodyJoints = bodyJoints;

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
                JointDrive zeroDrive = new JointDrive
                {
                    positionSpring = 0f,
                    positionDamper = 0f,
                    maximumForce = 0f
                };
                _bodyJoints[IndexRoot].xDrive = zeroDrive;
                _bodyJoints[IndexRoot].yDrive = zeroDrive;
                _bodyJoints[IndexRoot].zDrive = zeroDrive;
                _bodyJoints[IndexRoot].slerpDrive = zeroDrive;

                Debug.Log("[RAGDOLL_DEBUG] APR_Root: configuredInWorldSpace=true, position drives zeroed (origin fix)");
            }

            if (bodyRigidbodies != null && bodyRigidbodies.Length > IndexRoot && bodyRigidbodies[IndexRoot] != null)
            {
                Debug.Log(
                    $"APR_Root: isKinematic={bodyRigidbodies[IndexRoot].isKinematic}, useGravity={bodyRigidbodies[IndexRoot].useGravity}");
            }

            // 地面レイヤーマスクをキャッシュ（毎tick文字列ルックアップを回避）
            int groundLayerIndex = LayerMask.NameToLayer("Ground");
            _groundLayerMask = groundLayerIndex >= 0 ? (1 << groundLayerIndex) : 0;

            InitializeJointDrives();
            StoreOriginalPoses();

            _balanceMargin = _context.BalanceMargin;

            // スポーン直後にバランスドライブを適用
            // (DeactivateRagdoll() はバランス復帰時にしか呼ばれないため、
            //  初期フレームでドライブが未適用→即ActivateRagdoll の問題を防止)
            ApplyInitialDrives();

            Debug.Log("[RAGDOLL_DEBUG] RagdollPhysics constructor completed successfully");
        }

        #endregion

        #region Initialization Methods

        // スポーン直後は姿勢制御が安定していないため、補正開始を遅らせる（64Hz で約3秒）
        private int _groundSnapDelayTicks = 192;

        // 補正開始後、足の接地が成立するまで補正を続ける残り tick 数（64Hz で約4秒）
        private int _groundSnapTicksRemaining = 256;

        /// <summary>
        /// スポーン後の自動接地補正。
        /// バランス制御はスポーン時の高さを維持し続けるため、スポーン位置が
        /// リグの脚長に合っていないと足が床に届かず浮遊し、接地フラグが立たず
        /// 歩行不能になる（プレハブの scale 変更やシーンの床高さに依存しない根本対策）。
        /// 一回のスナップでは直後の姿勢制御で足が数cm持ち上がるため、
        /// 足の接触イベントが成立するまで毎 tick 補正を続け、成立したら終了する。
        /// </summary>
        public void EnsureSnappedToGround()
        {
            if (_groundSnapTicksRemaining <= 0 || !_context.HasStateAuthority)
            {
                return;
            }

            if (_isAnyFootGrounded)
            {
                // 接地達成 → 以降は補正しない
                _groundSnapTicksRemaining = 0;
                return;
            }

            if (_groundSnapDelayTicks > 0)
            {
                _groundSnapDelayTicks--;
                return;
            }

            _groundSnapTicksRemaining--;
            SnapRigToGround();
        }

        /// <summary>
        /// スポーン時にリグ全体を「足裏が床に接する高さ」へ平行移動する。
        /// あわせてワールド接続ジョイントのアンカーも同量ずらし、
        /// バランス制御の基準高さを接地後の姿勢に合わせる。
        /// StateAuthority のみ実行（クライアントの姿勢はネットワーク同期が決める）。
        /// </summary>
        private void SnapRigToGround()
        {
            if (!_context.HasStateAuthority || _groundLayerMask == 0 ||
                _bodyRigidbodies == null || _bodyRigidbodies.Length <= IndexRoot ||
                _bodyRigidbodies[IndexRoot] == null)
            {
                return;
            }

            // リグ全体で最も低いコライダー底面（通常は足裏）を求める
            float lowestBottomY = float.MaxValue;
            foreach (Rigidbody rb in _bodyRigidbodies)
            {
                if (rb == null)
                    continue;
                Collider col = rb.GetComponent<Collider>();
                if (col == null)
                    continue;
                lowestBottomY = Mathf.Min(lowestBottomY, col.bounds.min.y);
            }

            if (lowestBottomY >= float.MaxValue)
                return;

            // ルート直下の床面を検出（スポーン地点は床上である前提。最大10m下まで）
            Vector3 rootPosition = _bodyRigidbodies[IndexRoot].position;
            if (!Physics.Raycast(new Ray(rootPosition, Vector3.down), out RaycastHit hit, 10f, _groundLayerMask))
            {
                Debug.LogWarning("[RAGDOLL_DEBUG] SnapRigToGround: 足下に Ground が見つからないため接地補正をスキップ");
                return;
            }

            float delta = lowestBottomY - hit.point.y;
            if (Mathf.Abs(delta) < 0.01f)
                return; // 既にほぼ接地

            Vector3 shift = Vector3.down * delta;
            foreach (Rigidbody rb in _bodyRigidbodies)
            {
                if (rb != null)
                    rb.position += shift;
            }

            // ワールド接続ジョイントのアンカーも追従させる（configuredInWorldSpace=true 前提）
            if (_bodyJoints != null && _bodyJoints.Length > IndexRoot && _bodyJoints[IndexRoot] != null)
            {
                _bodyJoints[IndexRoot].connectedAnchor += shift;
            }

            // 微調整の連続ログを避け、大きな補正のみ記録する
            if (Mathf.Abs(delta) >= 0.05f)
            {
                Debug.Log($"[RAGDOLL_DEBUG] SnapRigToGround: リグを {delta:F3}m 下げて接地補正 (ground={hit.point.y:F3})");
            }
        }

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

            Debug.Log("[RAGDOLL_DEBUG] Initial drives applied (same as DeactivateRagdoll layout)");
        }

        private void InitializeJointDrives()
        {
            // ダンパー比率を適用（振動防止）
            // RagdollProfile に定義済みの damperRatio を使用する。
            // damper = spring * ratio で臨界減衰に近づける。
            float balanceDamper = _context.BalanceStrength * _context.BalanceDamperRatio;
            float poseDamper = _context.LimbStrength * _context.PoseDamperRatio;
            float coreDamper = _context.CoreStrength * _context.CoreDamperRatio;

            _balanceOn = JointConfigurator.CreateJointDrive(_context.BalanceStrength, balanceDamper);
            _poseOn = JointConfigurator.CreateJointDrive(_context.LimbStrength, poseDamper);
            _coreStiffness = JointConfigurator.CreateJointDrive(_context.CoreStrength, coreDamper);
            _driveOff = JointConfigurator.CreateJointDrive(_context.RagdollDriveOffSpring, _context.RagdollDriveOffDamper);

            Debug.Log($"[RAGDOLL_FIX] InitializeJointDrives: balance={_context.BalanceStrength}/{balanceDamper:F0} " +
                      $"pose={_context.LimbStrength}/{poseDamper:F0} core={_context.CoreStrength}/{coreDamper:F0}");
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

        #region Physics Update

        /// <summary>
        /// メインの物理更新ループ
        /// moveDirection: 移動方向（WASD入力、カメラ基準）
        /// facingDirection: 回転先方向（カメラ前方 or 移動方向、モードで切替）
        /// </summary>
        public void UpdatePhysics(PlayerState state, RagdollCommand command, float deltaTime)
        {
            // スポーン時の自動接地補正（初回の権威 tick で一度だけ実行）。
            // コンストラクタ（Spawned 中）の時点では実行条件が揃わないため、ここで行う
            EnsureSnappedToGround();

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
                bool isRagdollFromHost = (state == PlayerState.Ragdoll);
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
                {
                    DeactivateRagdoll();
                }
                else if (!_balanced && !_isRagdoll)
                {
                    ActivateRagdoll();
                }
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

            float movementControlMultiplier = isGrounded ? 1f : _context.AirControlMultiplier;

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
            if (_posePreviewActive && !_isRagdoll)
            {
                ApplyReachPose(_posePreviewRight, 0f, 0f);
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

            Debug.Log("[RAGDOLL_DEBUG] Ragdoll activated.");
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
            JointDrive zeroLinearDrive = new JointDrive
            {
                positionSpring = 0f,
                positionDamper = 0f,
                maximumForce = 0f
            };
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
                    targetBalancePriority = _context.WalkingBalancePriority;
                    targetPoseStiffness = _context.WalkingPoseStiffnessMultiplier;
                    break;
                case PlayerState.Idle:
                default:
                    targetBalancePriority = _context.IdleBalancePriority;
                    targetPoseStiffness = _context.IdlePoseStiffnessMultiplier;
                    break;
            }

            float blendSpeed = _context.StateBlendSpeed * deltaTime;
            _currentBalancePriority = Mathf.Lerp(_currentBalancePriority, targetBalancePriority, blendSpeed);
            _currentPoseStiffnessMultiplier = Mathf.Lerp(_currentPoseStiffnessMultiplier, targetPoseStiffness, blendSpeed);
        }

        private void ApplyBlendedJointDrives()
        {
            if (_bodyJoints == null || _isRagdoll)
                return;

            // 前tick値と変化がなければPhysXへの書き込みをスキップ
            if (Mathf.Abs(_currentPoseStiffnessMultiplier - _lastAppliedPoseMultiplier) < 0.001f)
                return;
            _lastAppliedPoseMultiplier = _currentPoseStiffnessMultiplier;

            float adjustedLimbStrength = _context.LimbStrength * _currentPoseStiffnessMultiplier;
            float adjustedPoseDamper = adjustedLimbStrength * _context.PoseDamperRatio;
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

        #region Root Rotation

        /// <summary>
        /// ルートの回転制御（facingDirectionベース）
        /// facingDirection が zero の場合は向き維持（移動方向モードでidle時）
        /// </summary>
        private void UpdateRootRotation(Vector3 facingDirection, float deltaTime)
        {
            if (_bodyJoints == null || !_bodyJoints[IndexRoot])
                return;

            // facingDirection が zero → 向き維持（移動方向モードでidle時）
            if (facingDirection.sqrMagnitude < 0.01f)
                return;

            Quaternion lookRotation = Quaternion.LookRotation(facingDirection.normalized, Vector3.up);
            _bodyJoints[IndexRoot].targetRotation = Quaternion.Slerp(
                _bodyJoints[IndexRoot].targetRotation,
                Quaternion.Inverse(lookRotation),
                _context.TurnSpeed * deltaTime
            );
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
            rb.linearVelocity = Vector3.Lerp(rb.linearVelocity, targetVel, _context.MovementVelocityLerp);
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
            if (moveDirection.sqrMagnitude > 0.01f)
            {
                Vector3 rootForward = _bodyRigidbodies[IndexRoot].transform.forward;
                rootForward.y = 0f;
                float dot = Vector3.Dot(moveDirection.normalized, rootForward.normalized);

                if (dot >= 0f)
                {
                    _walkForward = true;
                    _walkBackward = false;
                }
                else
                {
                    _walkForward = false;
                    _walkBackward = true;
                }
            }
            else
            {
                _walkForward = false;
                _walkBackward = false;
            }

            // APRController L355-363: 移動していない時はステップ状態をリセット
            if (!_walkForward && !_walkBackward)
            {
                _stepRight = false;
                _stepLeft = false;
                _stepRTimer = 0;
                _stepLTimer = 0;
                _alertLegRight = false;
                _alertLegLeft = false;
            }

            // APRController L900-917: 前進時 — 後ろにある足をステップさせる
            if (_walkForward)
            {
                // right leg
                if (_bodyParts[IndexRightFoot].transform.position.z < _bodyParts[IndexLeftFoot].transform.position.z && !_stepLeft && !_alertLegRight)
                {
                    _stepRight = true;
                    _alertLegRight = true;
                    _alertLegLeft = true;
                }

                // left leg
                if (_bodyParts[IndexRightFoot].transform.position.z > _bodyParts[IndexLeftFoot].transform.position.z && !_stepRight && !_alertLegLeft)
                {
                    _stepLeft = true;
                    _alertLegLeft = true;
                    _alertLegRight = true;
                }
            }

            // APRController L919-936: 後退時 — 前にある足をステップさせる（前後逆）
            if (_walkBackward)
            {
                // right leg
                if (_bodyParts[IndexRightFoot].transform.position.z > _bodyParts[IndexLeftFoot].transform.position.z && !_stepLeft && !_alertLegRight)
                {
                    _stepRight = true;
                    _alertLegRight = true;
                    _alertLegLeft = true;
                }

                // left leg
                if (_bodyParts[IndexRightFoot].transform.position.z < _bodyParts[IndexLeftFoot].transform.position.z && !_stepRight && !_alertLegLeft)
                {
                    _stepLeft = true;
                    _alertLegLeft = true;
                    _alertLegRight = true;
                }
            }

            float stepHeight = _context.StepHeight;
            float feetForce = _context.FeetMountForce;
            float stepDuration = _context.StepDuration;
            float walkInputAmount = CalculateWalkInputAmount(moveDirection);
            float stepDeltaTime = deltaTime * walkInputAmount;
            float scaledStepHeight = stepHeight * walkInputAmount;

            // APRController L939-975: Step right
            if (_stepRight)
            {
                _stepRTimer += stepDeltaTime;

                // Right foot force down
                AddFeetDownForce(IndexRightFoot, feetForce, deltaTime);

                // walk simulation
                if (_walkForward)
                {
                    _bodyJoints[IndexUpperRightLeg].targetRotation = new Quaternion(_bodyJoints[IndexUpperRightLeg].targetRotation.x + 0.09f * scaledStepHeight, _bodyJoints[IndexUpperRightLeg].targetRotation.y, _bodyJoints[IndexUpperRightLeg].targetRotation.z, _bodyJoints[IndexUpperRightLeg].targetRotation.w);
                    _bodyJoints[IndexLowerRightLeg].targetRotation = new Quaternion(_bodyJoints[IndexLowerRightLeg].targetRotation.x - 0.09f * scaledStepHeight * 2, _bodyJoints[IndexLowerRightLeg].targetRotation.y, _bodyJoints[IndexLowerRightLeg].targetRotation.z, _bodyJoints[IndexLowerRightLeg].targetRotation.w);

                    _bodyJoints[IndexUpperLeftLeg].targetRotation = new Quaternion(_bodyJoints[IndexUpperLeftLeg].targetRotation.x - 0.12f * scaledStepHeight / 2, _bodyJoints[IndexUpperLeftLeg].targetRotation.y, _bodyJoints[IndexUpperLeftLeg].targetRotation.z, _bodyJoints[IndexUpperLeftLeg].targetRotation.w);
                }

                if (_walkBackward)
                {
                    _bodyJoints[IndexLowerRightLeg].targetRotation = new Quaternion(_bodyJoints[IndexLowerRightLeg].targetRotation.x - 0.07f * scaledStepHeight * 2, _bodyJoints[IndexLowerRightLeg].targetRotation.y, _bodyJoints[IndexLowerRightLeg].targetRotation.z, _bodyJoints[IndexLowerRightLeg].targetRotation.w);

                    _bodyJoints[IndexUpperLeftLeg].targetRotation = new Quaternion(_bodyJoints[IndexUpperLeftLeg].targetRotation.x + 0.02f * scaledStepHeight / 2, _bodyJoints[IndexUpperLeftLeg].targetRotation.y, _bodyJoints[IndexUpperLeftLeg].targetRotation.z, _bodyJoints[IndexUpperLeftLeg].targetRotation.w);
                }

                // step duration
                if (_stepRTimer > stepDuration)
                {
                    _stepRTimer = 0;
                    _stepRight = false;

                    if (_walkForward || _walkBackward)
                    {
                        _stepLeft = true;
                    }
                }
            }
            else
            {
                // reset to idle (APRController L977-984)
                _bodyJoints[IndexUpperRightLeg].targetRotation = Quaternion.Lerp(_bodyJoints[IndexUpperRightLeg].targetRotation, _originalRotations[IndexUpperRightLeg], 8f * deltaTime);
                _bodyJoints[IndexLowerRightLeg].targetRotation = Quaternion.Lerp(_bodyJoints[IndexLowerRightLeg].targetRotation, _originalRotations[IndexLowerRightLeg], 17f * deltaTime);

                // feet force down
                AddFeetDownForce(IndexRightFoot, feetForce, deltaTime);
                AddFeetDownForce(IndexLeftFoot, feetForce, deltaTime);
            }


            // APRController L989-1035: Step left
            if (_stepLeft)
            {
                _stepLTimer += stepDeltaTime;

                // Left foot force down
                AddFeetDownForce(IndexLeftFoot, feetForce, deltaTime);

                // walk simulation
                if (_walkForward)
                {
                    _bodyJoints[IndexUpperLeftLeg].targetRotation = new Quaternion(_bodyJoints[IndexUpperLeftLeg].targetRotation.x + 0.09f * scaledStepHeight, _bodyJoints[IndexUpperLeftLeg].targetRotation.y, _bodyJoints[IndexUpperLeftLeg].targetRotation.z, _bodyJoints[IndexUpperLeftLeg].targetRotation.w);
                    _bodyJoints[IndexLowerLeftLeg].targetRotation = new Quaternion(_bodyJoints[IndexLowerLeftLeg].targetRotation.x - 0.09f * scaledStepHeight * 2, _bodyJoints[IndexLowerLeftLeg].targetRotation.y, _bodyJoints[IndexLowerLeftLeg].targetRotation.z, _bodyJoints[IndexLowerLeftLeg].targetRotation.w);

                    _bodyJoints[IndexUpperRightLeg].targetRotation = new Quaternion(_bodyJoints[IndexUpperRightLeg].targetRotation.x - 0.12f * scaledStepHeight / 2, _bodyJoints[IndexUpperRightLeg].targetRotation.y, _bodyJoints[IndexUpperRightLeg].targetRotation.z, _bodyJoints[IndexUpperRightLeg].targetRotation.w);
                }

                if (_walkBackward)
                {
                    _bodyJoints[IndexLowerLeftLeg].targetRotation = new Quaternion(_bodyJoints[IndexLowerLeftLeg].targetRotation.x - 0.07f * scaledStepHeight * 2, _bodyJoints[IndexLowerLeftLeg].targetRotation.y, _bodyJoints[IndexLowerLeftLeg].targetRotation.z, _bodyJoints[IndexLowerLeftLeg].targetRotation.w);

                    _bodyJoints[IndexUpperRightLeg].targetRotation = new Quaternion(_bodyJoints[IndexUpperRightLeg].targetRotation.x + 0.02f * scaledStepHeight / 2, _bodyJoints[IndexUpperRightLeg].targetRotation.y, _bodyJoints[IndexUpperRightLeg].targetRotation.z, _bodyJoints[IndexUpperRightLeg].targetRotation.w);
                }

                // Step duration
                if (_stepLTimer > stepDuration)
                {
                    _stepLTimer = 0;
                    _stepLeft = false;

                    if (_walkForward || _walkBackward)
                    {
                        _stepRight = true;
                    }
                }
            }
            else
            {
                // reset to idle (APRController L1027-1034)
                _bodyJoints[IndexUpperLeftLeg].targetRotation = Quaternion.Lerp(_bodyJoints[IndexUpperLeftLeg].targetRotation, _originalRotations[IndexUpperLeftLeg], 7f * deltaTime);
                _bodyJoints[IndexLowerLeftLeg].targetRotation = Quaternion.Lerp(_bodyJoints[IndexLowerLeftLeg].targetRotation, _originalRotations[IndexLowerLeftLeg], 18f * deltaTime);

                // feet force down
                AddFeetDownForce(IndexRightFoot, feetForce, deltaTime);
                AddFeetDownForce(IndexLeftFoot, feetForce, deltaTime);
            }
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

                var v3 = rigidBody.transform.up * _context.JumpForce;
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
            float upperArmBasePitch = _context.ReachUpperArmBasePitch;
            // 腕の上下: APR MouseYAxisArms(LookDirection.y) で base から振る
            float armInputLimit = Mathf.Max(0f, _context.ReachArmInputLimit);
            float upperArmPitchPerUnit = _context.ReachUpperArmPitchPerUnit;
            float upperArmMinPitch = _context.ReachUpperArmMinPitch;
            float upperArmMaxPitch = _context.ReachUpperArmMaxPitch;
            float lowerArmPitch = _context.ReachLowerArmPitch;

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
                _context.ReachUpperArmJointSpring,
                _context.ReachUpperArmJointDamper,
                _context.ReachUpperArmJointMaxForce);
            JointDrive lowerReachDrive = JointConfigurator.CreateJointDrive(
                _context.ReachLowerArmJointSpring,
                _context.ReachLowerArmJointDamper,
                _context.ReachLowerArmJointMaxForce);

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
                    _bodyRigidbodies[lowerArmIndex].AddForce(forward * _context.PunchImpulse, ForceMode.Impulse);
                }
            }

            if (isRight)
            {
                _rightPunchRecoveryDelay = _context.PunchRecoveryDelaySeconds;
            }
            else
            {
                _leftPunchRecoveryDelay = _context.PunchRecoveryDelaySeconds;
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
                    _context.PunchRecoveryLerpSpeed * deltaTime
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
            bool raycastHit = Physics.Raycast(ray, _context.BalanceHeight, _groundLayerMask);
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

            float velocity = _bodyRigidbodies[IndexRoot].linearVelocity.magnitude;
            bool isLowVelocity = velocity < 1f;

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

        public void SetFootGroundedInfo(bool isLeftFoot, bool isGrounded, bool anyFootGrounded)
        {
            if (isLeftFoot)
            {
                _isLeftFootGrounded = isGrounded;
            }
            else
            {
                _isRightFootGrounded = isGrounded;
            }

            _isAnyFootGrounded = anyFootGrounded;
        }

        #endregion
    }
}
