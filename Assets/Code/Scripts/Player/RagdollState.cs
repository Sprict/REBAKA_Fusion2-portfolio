using UnityEngine;
using Rebaka.Utils;

namespace Rebaka.Player
{
    /// <summary>
    /// ラグドール状態からの復帰試行と、状態遷移時のログ出力だけを担う。
    /// 状態決定そのものの正本は <see cref="RagdollStateEvaluator.Resolve"/>。
    /// （かつてこのクラスが持っていた状態遷移マシンは呼び出し元が無く、
    ///   「非接地なら強制 Ragdoll」という廃止済み仕様を含んでいたため削除した）
    /// </summary>
    public class RagdollState
    {
        #region Fields
        
        private readonly IRagdollStateContext _context;
        private PlayerState _currentState;

        #endregion

        #region Constructor
        
        internal RagdollState(IRagdollStateContext context)
        {
            _context = context;
            _currentState = PlayerState.Idle;
        }
        
        #endregion

        #region State Management

        // ラグドール状態からの回復試行（RagdollControllerから呼び出される）
        public void TryRecoverFromRagdoll()
        {
            if (_currentState == PlayerState.Ragdoll)
            {
                // 回復条件をチェック（速度が十分に遅いか）
                const float velocityThreshold = 3.0f; // 回復可能な最大速度 (元の1.0fから緩和)
                bool canRecover = true;
                
                // コントローラーのリジッドボディの速度をチェック
                if (_context != null)
                {
                    Rigidbody rootRb = _context.RootRigidbody;
                    if (rootRb != null && rootRb.linearVelocity.magnitude > velocityThreshold)
                    {
                        canRecover = false;
                    }
                }
                
                if (canRecover)
                {
                    // 回復処理
                    OnStateExit(PlayerState.Ragdoll);
                    _currentState = PlayerState.Idle;
                    OnStateEnter(PlayerState.Idle);
                    _context.CurrentState = PlayerState.Idle;
                    
                    DebugUtils.LogRagdollState("ラグドール状態から回復しました");
                }
            }
        }
        
        #endregion

        #region State Events
        
        private void OnStateEnter(PlayerState state)
        {
            switch (state)
            {
                case PlayerState.Jumping:
                    // ジャンプ開始処理
                    DebugUtils.LogRagdollState("Jumping started");
                    break;

                case PlayerState.Ragdoll:
                    // ラグドール開始処理
                    DebugUtils.LogRagdollState("Ragdoll activated");
                    break;

                case PlayerState.Punching:
                    // パンチ開始処理
                    DebugUtils.LogRagdollState("Punch started");
                    break;
            }
        }

        private void OnStateExit(PlayerState state)
        {
            switch (state)
            {
                case PlayerState.Ragdoll:
                    // ラグドール終了処理
                    DebugUtils.LogRagdollState("Ragdoll deactivated");
                    break;
            }
        }
        
        #endregion
    }
}
