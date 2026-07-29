using UnityEngine;
using UnityEngine.InputSystem;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Settings;
using MyFolder.Scripts.Utils;

namespace MyFolder.Scripts.Camera
{
    /// <summary>
    /// 三人称オービットカメラ（2026-06-29 Human Fall Flat 風に再設計）。
    ///
    /// - Player/Look X → カメラがプレイヤーの周りを水平方向に旋回（ヨー）。
    /// - 垂直は固定角（height/distance 比で決まる見下ろし角）。Player/Look Yはカメラを動かさない
    ///   （Player/Look Y は体の上下＝リーチ狙いに使うため、InputCollector 側で消費）。
    /// - プレイヤーがラグドールで揺れてもカメラが揺れないよう、注視ピボットを強くローパスする。
    /// - UE5 スプリングアーム相当の衝突回避で壁/地面にめり込まない。
    ///
    /// カメラのヨーはローカル表示のみ（ネットワーク非同期）。移動は InputCollector が
    /// Camera.main 基準で計算するため、このカメラ旋回がそのまま移動基準になる（HFF操作感）。
    /// </summary>
    public class OrbitCamera : MonoBehaviour
    {
        [Header("Target")]
        [SerializeField] private Transform target; // 追従対象（重心 or root）
        [SerializeField] private Vector3 pivotOffset = new Vector3(0, 1.5f, 0); // 注視ピボット（頭あたり）

        [Header("Placement")]
        [SerializeField] private float distance = 5.0f; // 水平距離
        [SerializeField] private float height = 2.0f;   // ピボットからの高さ（固定の見下ろし角を決める）

        [Header("Look Orbit (horizontal only)")]
        [Tooltip("Player/Look Xでの水平旋回感度（度/Lookデルタ）。")]
        [SerializeField] private float orbitSensitivityX = 0.2f;
        [Tooltip("水平旋回を反転する。")]
        [SerializeField] private bool invertOrbitX = false;

        [Header("Follow Movement Direction")]
        [Tooltip("オンにすると、プレイヤーの進行方向の背後へカメラが自動で回り込む（Player/Look X旋回と併用可）。")]
        [SerializeField] private bool followMovementDirection = false;
        [Tooltip("進行方向へカメラが回り込む速さ。大きいほど機敏に背後へ。")]
        [SerializeField] private float movementFollowSpeed = 2f;

        [Header("Two-Handed Auto Follow")]
        [Tooltip("両手持ち中にプレイヤーの背後へ回り込む際の追従ラグ(秒)。ロケットリーグの Stiffness の逆相当: " +
                 "大きいほど体の向きの変化に遅れてゆっくり追従し（酔いにくい）、0 に近いほど機敏に張り付く。")]
        [SerializeField] private float twoHandedFollowLag = 0.35f;

        [Tooltip("体とカメラのヨー差がこの角度以内ならカメラを一切回さない（世界が静止＝酔い対策の核）。")]
        [SerializeField] private float twoHandedDeadZoneDegrees = 25f;

        [Tooltip("ヨー差の上限。これを超えたら縁までクランプして最低限の追従を保証する。")]
        [SerializeField] private float twoHandedSoftZoneDegrees = 70f;

        [Tooltip("追従時のカメラ角速度の上限(度/秒)。体を高速スピンしても画面のスイープ速度に天井をつける。")]
        [SerializeField] private float twoHandedMaxYawSpeed = 110f;

        [Tooltip("Player/Look X入力がこの秒数止まったら、真後ろへゆっくりリセンターする（射撃等で正面合わせが要る場合用）。")]
        [SerializeField] private float twoHandedRecenterDelay = 1.5f;

        [Tooltip("リセンター時の平滑時間(秒)。大きいほどゆっくり真後ろへ寄る。")]
        [SerializeField] private float twoHandedRecenterSmoothTime = 1.2f;

        [Header("Smoothing (anti-wobble)")]
        [Tooltip("注視ピボットのローパス時間（水平）。大きいほどプレイヤーの揺れを無視して安定（酔い対策）。")]
        [SerializeField] private float pivotSmoothTime = 0.18f;

        [Tooltip("注視ピボットの垂直方向のローパス時間。歩行の上下ボビングをカメラに乗せない『手振れオフ』。" +
                 "大きいほど上下揺れを完全に無視する（段差・ジャンプの高さ変化にはゆっくり追従）。")]
        [SerializeField] private float pivotVerticalSmoothTime = 0.8f;
        [Tooltip("カメラ位置追従の滑らかさ（SmoothDamp）。")]
        [SerializeField] private float followSmoothTime = 0.08f;

        [Header("Spring Arm Collision (UE5-style)")]
        [Tooltip("カメラ衝突判定の球半径")]
        [SerializeField] private float collisionRadius = 0.3f;
        [Tooltip("衝突点からカメラを手前に逃がす余白")]
        [SerializeField] private float collisionSkin = 0.2f;
        [Tooltip("衝突判定の対象レイヤー（壁/地面など）。プレイヤー自身は target.root で自動除外。")]
        [SerializeField] private LayerMask collisionMask = ~0;

        // 内部状態
        private float _orbitYaw;                 // Look X 累積の水平旋回角（度）
        private float _orbitYawVelocity;         // 両手持ち追従の SmoothDampAngle 用速度
        private bool _twoHandedFollowActive;     // 前フレームで両手持ち追従だったか
        private float _quietTime;                // Look X入力が静止している累積秒（リセンター判定用）
        private Transform _heldLeftRoot;         // 左手が掴んでいるオブジェクト（衝突除外用）
        private Transform _heldRightRoot;        // 右手が掴んでいるオブジェクト（衝突除外用）
        private ILocalPlayerViewSource _viewSource; // SetTarget で受け取るビュー情報（両手持ち判定・体の向き）
        private Vector3 _smoothedPivot;          // ローパス済みピボット（揺れ吸収）
        private Vector3 _pivotVelocity;
        private float _pivotYVelocity;           // 垂直ピボットの SmoothDamp 用速度（水平と時定数分離）
        private Vector3 _positionVelocity;
        private Vector3 _prevTargetPos;          // 進行方向追従用の前フレーム位置
        private bool _initialized;
        private REBAKA_Fusion2 _inputActions;

        // 進行方向追従の発火しきい値（1フレームの水平移動量、sqrMagnitude比較用に二乗で保持）
        private const float MoveThresholdPerFrame = 0.01f;
        private const float MoveThresholdSqr = MoveThresholdPerFrame * MoveThresholdPerFrame;

        // target.root のキャッシュ（SetTarget 時に更新。LateUpdate 毎フレームの階層 traversal を回避）
        private Transform _targetRoot;

        // SphereCast の GC を避けるための再利用バッファ
        private readonly RaycastHit[] _hitBuffer = new RaycastHit[16];

        private void Awake()
        {
            _inputActions = new REBAKA_Fusion2();
            ParrelSyncInputUtil.RestrictDevices(_inputActions.asset);
            InputSettingsRuntime.Register(_inputActions.asset);
        }

        private void OnEnable()
        {
            EnsureInputActions();
            _inputActions.Player.Enable();
        }

        private void OnDisable()
        {
            _inputActions?.Player.Disable();
        }

        private void OnDestroy()
        {
            InputSettingsRuntime.Unregister(_inputActions?.asset);
            _inputActions?.Dispose();
            _inputActions = null;
        }

        private void LateUpdate()
        {
            if (!target) return;

            Vector2 look = SettingsMenuState.IsGameplayInputBlocked ? Vector2.zero : ReadLookInput();

            // 1. 両手持ち中はLook Xがボディヨー操作（InputCollector 側で消費）になるため、
            //    カメラは旋回せず、プレイヤーの背後へイーズインアウトで自動的に回り込む。
            //    twoHandedFollowLag が「カメラの時間差」: 体の傾き・回転に機敏に反応させない酔い対策。
            bool twoHanded = IsTwoHandedHold();
            if (twoHanded)
            {
                if (!_twoHandedFollowActive)
                {
                    _orbitYawVelocity = 0f; // 突入時は速度リセット（イーズイン）
                    _quietTime = 0f;
                }
                UpdateTwoHandedFollow(look.x);
            }
            _twoHandedFollowActive = twoHanded;

            // 1. Player/Look X で水平旋回（カーソルロック中のみ。メニュー中は動かさない）
            // 右スティックは Input System の stickToLookDelta でデルタ等価化済み。
            if (!twoHanded && Cursor.lockState == CursorLockMode.Locked && !_inputActions.Player.AxisModifierButton.IsPressed())
            {
                _orbitYaw = CalculateOrbitYawFromLook(_orbitYaw, look.x, orbitSensitivityX, invertOrbitX);
            }

            // 1.5 進行方向追従（オン時のみ）: プレイヤーの水平移動方向の背後へ旋回角を寄せる
            if (followMovementDirection && _initialized && !twoHanded)
            {
                Vector3 travel = target.position - _prevTargetPos;
                travel.y = 0f;
                if (travel.sqrMagnitude > MoveThresholdSqr)
                {
                    float targetYaw = Mathf.Atan2(travel.x, travel.z) * Mathf.Rad2Deg;
                    _orbitYaw = Mathf.LerpAngle(_orbitYaw, targetYaw, movementFollowSpeed * Time.deltaTime);
                }
            }
            _prevTargetPos = target.position;

            // 2. 注視ピボットを強くローパス（ラグドールの揺れを吸収して酔いを防ぐ）
            Vector3 rawPivot = target.position + pivotOffset;
            if (!_initialized)
            {
                _smoothedPivot = rawPivot;
                _prevTargetPos = target.position;
                _initialized = true;
            }
            else
            {
                // 水平と垂直でローパス時定数を分離する。歩行の上下ボビングは垂直の強い
                // ローパス（pivotVerticalSmoothTime）で吸収し、カメラの縦揺れ（手振れ）を消す。
                Vector3 horizontal = Vector3.SmoothDamp(
                    _smoothedPivot, rawPivot, ref _pivotVelocity, pivotSmoothTime);
                float vertical = Mathf.SmoothDamp(
                    _smoothedPivot.y, rawPivot.y, ref _pivotYVelocity,
                    Mathf.Max(pivotSmoothTime, pivotVerticalSmoothTime));
                _smoothedPivot = new Vector3(horizontal.x, vertical, horizontal.z);
            }

            // 3. 旋回角から背後方向を作り、理想カメラ位置を決める（垂直は固定）
            Vector3 orbitDir = Quaternion.Euler(0f, _orbitYaw, 0f) * Vector3.forward;
            Vector3 desired = _smoothedPivot - orbitDir * distance + Vector3.up * height;

            // 4. スプリングアーム: 壁/地面に当たれば手前へ。
            //    掴んでいるオブジェクト（頭上に掲げた荷物など）は遮蔽物として扱わない
            //    （扱うとカメラが荷物へ引き寄せられてメッシュ内部へ入るバグになる）
            RefreshHeldObjectExclusions();
            desired = ResolveCollision(_smoothedPivot, desired);

            // 5. 反映（位置は SmoothDamp、回転は安定ピボットを見る）
            transform.position = Vector3.SmoothDamp(
                transform.position, desired, ref _positionVelocity, followSmoothTime);
            transform.rotation = Quaternion.LookRotation((_smoothedPivot - transform.position).normalized, Vector3.up);
        }

        /// <summary>
        /// 両手持ち中のカメラヨー制御（酔い対策設計）:
        /// - デッドゾーン内はカメラを一切回さない（世界を静止させるのが最大の酔い対策）。
        /// - 出たら「デッドゾーンの縁」を目標に SmoothDamp（＋角速度上限）。操作をやめた瞬間に
        ///   誤差が縁の内側へ入り、カメラが即座に完全静止する（中心追いだとドリフトの尾を引く）。
        /// - ソフトゾーンを超えたら縁までクランプして最低限の追従を保証（Rocket League の
        ///   stiffness と swivel speed の分離に相当）。
        /// - Player/Look X入力が twoHandedRecenterDelay 秒静穏なら、真後ろへゆっくりリセンター
        ///   （射撃武器などで正面合わせが要るケース用）。
        /// </summary>
        private void UpdateTwoHandedFollow(float lookDeltaX)
        {
            Vector3 facing = _viewSource.FacingForward;
            float targetYaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
            float error = Mathf.DeltaAngle(_orbitYaw, targetYaw);

            // Player/Look X の静穏時間を計測（リセンター判定）
            _quietTime = CalculateLookQuietTime(_quietTime, lookDeltaX, Time.deltaTime);

            float deadZone = Mathf.Max(0f, twoHandedDeadZoneDegrees);
            float softZone = Mathf.Max(deadZone, twoHandedSoftZoneDegrees);

            if (_quietTime >= twoHandedRecenterDelay && Mathf.Abs(error) > 0.5f)
            {
                // ④ 静穏時リセンター: 真後ろまでゆっくり寄せる
                _orbitYaw = Mathf.SmoothDampAngle(
                    _orbitYaw, targetYaw, ref _orbitYawVelocity,
                    Mathf.Max(0.001f, twoHandedRecenterSmoothTime), twoHandedMaxYawSpeed);
            }
            else if (Mathf.Abs(error) > deadZone)
            {
                // ①② デッドゾーン外: 縁を目標に、③角速度上限つきで追従
                float goal = targetYaw - Mathf.Sign(error) * deadZone;
                _orbitYaw = Mathf.SmoothDampAngle(
                    _orbitYaw, goal, ref _orbitYawVelocity,
                    Mathf.Max(0.001f, twoHandedFollowLag), twoHandedMaxYawSpeed);

                // ③ ソフトゾーン超過は縁までクランプ（体が先行しすぎない保証）
                error = Mathf.DeltaAngle(_orbitYaw, targetYaw);
                if (Mathf.Abs(error) > softZone)
                    _orbitYaw = targetYaw - Mathf.Sign(error) * softZone;
            }
            else
            {
                // ① デッドゾーン内: 完全静止
                _orbitYawVelocity = 0f;
            }
        }

        /// <summary>
        /// 両手持ち追従モードか（ビュー未設定・destroy 済みなら false）。
        /// </summary>
        private bool IsTwoHandedHold()
        {
            if (_viewSource == null || (_viewSource as Object) == null)
                return false;
            return _viewSource.IsTwoHandedHold;
        }

        /// <summary>
        /// 左右の手が掴んでいるオブジェクトのルートを毎フレーム更新（衝突除外用）。
        /// </summary>
        private void RefreshHeldObjectExclusions()
        {
            if (_viewSource == null || (_viewSource as Object) == null)
            {
                _heldLeftRoot = null;
                _heldRightRoot = null;
                return;
            }

            _heldLeftRoot = _viewSource.GetHeldObjectRoot(true);
            _heldRightRoot = _viewSource.GetHeldObjectRoot(false);
        }

        internal static float CalculateOrbitYawFromLook(
            float currentYaw,
            float lookDeltaX,
            float sensitivity,
            bool invertOrbitX)
        {
            float yaw = currentYaw + lookDeltaX * sensitivity * (invertOrbitX ? -1f : 1f);
            if (yaw > 360f) yaw -= 360f;
            else if (yaw < -360f) yaw += 360f;
            return yaw;
        }

        internal static float CalculateLookQuietTime(float currentQuietTime, float lookDeltaX, float deltaTime)
        {
            return Mathf.Abs(lookDeltaX) < 0.01f ? currentQuietTime + deltaTime : 0f;
        }

        private Vector2 ReadLookInput()
        {
            EnsureInputActions();
            return _inputActions.Player.Look.ReadValue<Vector2>();
        }

        private void EnsureInputActions()
        {
            if (_inputActions != null)
                return;

            _inputActions = new REBAKA_Fusion2();
            ParrelSyncInputUtil.RestrictDevices(_inputActions.asset);
            InputSettingsRuntime.Register(_inputActions.asset);
        }

        /// <summary>
        /// pivot から desired への直線を SphereCast し、プレイヤー自身以外に当たったら
        /// 衝突点の手前（collisionSkin 分）までカメラ位置を引き寄せる。
        /// </summary>
        private Vector3 ResolveCollision(Vector3 pivot, Vector3 desired)
        {
            Vector3 delta = desired - pivot;
            float maxDist = delta.magnitude;
            if (maxDist < 0.001f)
                return desired;

            Vector3 dir = delta / maxDist;

            int count = Physics.SphereCastNonAlloc(
                pivot, collisionRadius, dir, _hitBuffer, maxDist,
                collisionMask, QueryTriggerInteraction.Ignore);

            float closest = maxDist;
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _hitBuffer[i];
                if (hit.transform == null)
                    continue;
                // プレイヤー自身（target の階層下）は無視
                if (_targetRoot != null && hit.transform.IsChildOf(_targetRoot))
                    continue;
                // 掴んでいるオブジェクトも無視（左右どちらの手でも）
                if (_heldLeftRoot != null && hit.transform.IsChildOf(_heldLeftRoot))
                    continue;
                if (_heldRightRoot != null && hit.transform.IsChildOf(_heldRightRoot))
                    continue;
                if (hit.distance < closest)
                    closest = hit.distance;
            }

            if (closest < maxDist)
            {
                float pulled = Mathf.Max(0.1f, closest - collisionSkin);
                return pivot + dir * pulled;
            }

            return desired;
        }

        /// <summary>
        /// 外部（LocalPlayerCameraBinder）から追従対象を設定する。
        /// source は両手持ち判定（IsTwoHandedHold）と体の向き（FacingForward）の参照に使う。
        /// </summary>
        public void SetTarget(Transform newTarget, ILocalPlayerViewSource source = null)
        {
            target = newTarget;
            _viewSource = source;
            _targetRoot = newTarget != null ? newTarget.root : null;
            _positionVelocity = Vector3.zero;
            _pivotVelocity = Vector3.zero;
            _initialized = false;
        }
    }
}
