

using UnityEngine;
using Fusion;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// プレイヤーへの操作命令をまとめたデータコンテナ（構造体）
    /// </summary>
    public struct RagdollCommand
    {
        public Vector3 MoveDirection;
        public Vector3 FacingDirection;   // 回転先方向（カメラ前方 or 移動方向）
        public Vector2 LookDirection;
        public float BodyRoll;

        // フラグ類
        public bool IsJumping;
        public bool IsDashing;
        public bool IsCrouching;

        // 手のアクション
        public bool IsGrabbingRight;
        public bool IsGrabbingLeft;
        public bool IsPunchingRight;
        public bool IsPunchingLeft;
    }
}
