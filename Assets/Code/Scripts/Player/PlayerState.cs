namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// プレイヤーの状態を表す列挙型
    /// </summary>
    public enum PlayerState
    {
        #region Player States

        /// <summary>待機状態</summary>
        Idle,

        /// <summary>歩行状態</summary>
        Walking,

        /// <summary>ジャンプ状態</summary>
        Jumping,

        /// <summary>ラグドール（物理演算）状態</summary>
        Ragdoll,

        /// <summary>パンチ動作状態</summary>
        Punching,

        /// <summary>手を伸ばして掴もうとしている状態</summary>
        Reaching

        #endregion
    }
}
