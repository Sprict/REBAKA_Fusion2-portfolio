using Fusion;
using Rebaka.Utils;
using UnityEngine;

namespace Rebaka.Player
{
    /// <summary>
    /// プレイヤーの足の接触を検出し、着地状態を管理するコンポーネント
    /// </summary>
    public class RagdollFootContact : NetworkBehaviour
    {
        #region Networked Properties

        // 足の接地状態
        [Networked] private NetworkBool IsGrounded { get; set; }

        #endregion

        #region Serialized Fields

        [SerializeField] private RagdollController controller;
        [SerializeField] private LayerMask groundLayer; // 地面として判定するレイヤーマスク

        [Tooltip("左足の場合はtrue、右足の場合はfalse")] [SerializeField]
        private bool isLeftFoot;

        #endregion

        #region Private Fields

        // Spawned状態を追跡するフラグ
        private bool _isSpawned;

        // 接地タイマー（短時間の浮きを防止）
        private float _groundedTimer = 0f;
        private const float GroundedTimerThreshold = 0.1f;

        // 地面と接触したことがあるか（接地タイマーの起動判定に使う）
        private bool _hasLastGroundContact = false;

        #endregion

        #region Unity Lifecycle Methods

        public override void Spawned()
        {
            // Spawned状態をマーク
            _isSpawned = true;

            // コントローラーが設定されていない場合は親から取得
            if (controller == null)
            {
                controller = GetComponentInParent<RagdollController>();
                if (controller == null)
                {
                    DebugUtils.LogRagdollError("FootContactに接続するRagdollControllerが見つかりません", this);
                    enabled = false;
                    return;
                }
            }

            // 初期状態は非接地
            IsGrounded = false;
            _groundedTimer = 0f;
        }

        public override void Despawned(NetworkRunner runner, bool hasState)
        {
            _isSpawned = false;
        }

        public override void FixedUpdateNetwork()
        {
            // Gang Beasts方式: 接地判定はホスト（StateAuthority）のみ実行
            if (!Object.HasStateAuthority) return;

            // 接地タイマーの更新
            if (_hasLastGroundContact)
            {
                // 接地中はタイマーをリセット
                _groundedTimer = GroundedTimerThreshold;
            }
            else if (_groundedTimer > 0)
            {
                // 非接地だがタイマーがある場合は減少
                _groundedTimer -= Runner.DeltaTime;

                // タイマーが切れたら非接地状態に
                if (_groundedTimer <= 0)
                {
                    if (IsGrounded)
                    {
                        IsGrounded = false;

                        // コントローラーに接地状態の変更を通知
                        if (controller != null)
                        {
                            (controller as IRagdollGroundingSink)?.OnFootGroundedChanged(isLeftFoot, false);
                        }
                    }
                }
            }

            // 接触フラグをリセット（次のフレームでOnCollisionStayが呼ばれなければ非接地と判断）
            _hasLastGroundContact = false;
        }

        #endregion

        #region Collision Methods

        private void OnCollisionEnter(Collision collision)
        {
            // Spawned前は処理をスキップ（Networkedプロパティにアクセスできない）
            if (!_isSpawned) return;

            // Gang Beasts方式: 接地判定はホスト（StateAuthority）のみ実行
            if (!Object.HasStateAuthority) return;

            // 地面レイヤーとの接触を検出
            if (((1 << collision.gameObject.layer) & groundLayer) != 0)
            {
                _hasLastGroundContact = true;

                // まだ接地状態でなければ、接地状態に切り替え
                if (!IsGrounded)
                {
                    IsGrounded = true;
                    _groundedTimer = GroundedTimerThreshold;

                    // コントローラーに接地状態の変更を通知
                    if (controller != null)
                    {
                        (controller as IRagdollGroundingSink)?.OnFootGroundedChanged(isLeftFoot, true);

                        // プレイヤーがジャンプ状態から接地した場合
                        if (controller.CurrentState == PlayerState.Jumping ||
                            controller.CurrentState == PlayerState.Ragdoll)
                        {
                            // 着地効果音を再生
                            (controller as IRagdollAudioSink)?.PlayImpactSound();
                            DebugUtils.LogRagdollState("地面に着地しました", this);
                        }
                    }
                }
            }
        }

        private void OnCollisionStay(Collision collision)
        {
            // Spawned前は処理をスキップ
            if (!_isSpawned) return;

            // Gang Beasts方式: 接地判定はホスト（StateAuthority）のみ実行
            if (!Object.HasStateAuthority) return;

            // 地面レイヤーとの継続的な接触
            if (((1 << collision.gameObject.layer) & groundLayer) != 0)
            {
                _hasLastGroundContact = true;
            }
        }

        #endregion

        #region Public Methods

        // 外部からの接地状態確認用
        public bool GetIsGrounded()
        {
            return IsGrounded;
        }

        #endregion
    }
}
