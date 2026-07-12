using UnityEngine;
using MyFolder.Scripts.Utils;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// 
    /// </summary>
    public class RagdollState
    {
        #region Fields
        
        private readonly IRagdollStateContext _context;
        private PlayerState _currentState;

        // タイマー
        private float _stateTimer;
        
        #endregion

        #region Constructor
        
        internal RagdollState(IRagdollStateContext context)
        {
            _context = context;
            _currentState = PlayerState.Idle;
        }
        
        #endregion

        #region State Management
        
        public void UpdateState(RagdollCommand command, bool isGrounded)
        {
            PlayerState previousState = _currentState;

            // 状態遷移ロジック
            DetermineNextState(command, isGrounded);

            // 状態が変わった場合の処理
            if (previousState != _currentState)
            {
                OnStateExit(previousState);
                OnStateEnter(_currentState);
                _context.CurrentState = _currentState; // ネットワーク同期変数の更新
            }

            // 現在の状態の更新処理
            UpdateCurrentState(command);
        }

        private void DetermineNextState(RagdollCommand command, bool isGrounded)
        {
            // 落下時はラグドール状態に強制遷移
            if (!isGrounded && _currentState != PlayerState.Jumping)
            {
                _currentState = PlayerState.Ragdoll;
                return;
            }

            // 状態ごとの遷移ルール
            switch (_currentState)
            {
                case PlayerState.Idle:
                    if (command.IsJumping)
                        _currentState = PlayerState.Jumping;
                    else if (command.IsPunchingLeft || command.IsPunchingRight)
                        _currentState = PlayerState.Punching;
                    else if (command.IsGrabbingLeft || command.IsGrabbingRight)
                        _currentState = PlayerState.Reaching;
                    else if (command.MoveDirection.magnitude > 0.1f)
                        _currentState = PlayerState.Walking;
                    break;

                case PlayerState.Walking:
                    if (command.IsJumping)
                        _currentState = PlayerState.Jumping;
                    else if (command.MoveDirection.magnitude < 0.1f)
                        _currentState = PlayerState.Idle;
                    break;

                case PlayerState.Jumping:
                    // ジャンプ状態のタイマー処理
                    _stateTimer += Time.deltaTime;
                    if (_stateTimer > 0.5f) // ジャンプ状態の持続時間
                    {
                        _stateTimer = 0f;
                        _currentState = isGrounded ? PlayerState.Idle : PlayerState.Ragdoll;
                    }
                    break;

                case PlayerState.Ragdoll:
                    // ラグドール状態から復帰判定
                    if (isGrounded)
                    {
                        // ジャンプボタンが押されたか、一定時間経過したら回復
                        if (command.IsJumping || _stateTimer > 3.0f)
                        {
                            _currentState = PlayerState.Idle;
                        }
                        
                        // ラグドール中でも地面にいる間はタイマーを更新
                        _stateTimer += Time.deltaTime;
                    }
                    else
                    {
                        // 空中にいる間はタイマーをリセット
                        _stateTimer = 0f;
                    }
                    break;

                case PlayerState.Punching:
                    // パンチ状態のタイマー処理
                    _stateTimer += Time.deltaTime;
                    if (_stateTimer > 0.3f) // パンチ状態の持続時間
                    {
                        _stateTimer = 0f;
                        _currentState = PlayerState.Idle;
                    }
                    break;

                case PlayerState.Reaching:
                    // Reaching状態の終了判定
                    if (command is { IsGrabbingLeft: false, IsGrabbingRight: false })
                    {
                        _currentState = PlayerState.Idle;
                    }
                    break;
            }
        }
        
        private void UpdateCurrentState(RagdollCommand command)
        {
            // 各状態の継続的な処理
            switch (_currentState)
            {
                case PlayerState.Walking:
                    // 歩行中の処理
                    break;

                case PlayerState.Ragdoll:
                    // ラグドール中の処理
                    break;
            }
        }
        
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
            _stateTimer = 0f;

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
