using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Network
{
    [System.Serializable]
    public struct NetworkInputData : INetworkInput
    {
        /// <summary>移動方向（カメラ基準、正規化していない合成ベクトル）</summary>
        public Vector3 direction;

        /// <summary>体を向かせたい方向。ゼロベクトルは現在の向きを維持する合図として扱われる</summary>
        public Vector3 facingDirection;

        /// <summary>x=胴体ベンド、y=腕リーチ上下。InputCollector で累積済みの絶対値</summary>
        public Vector2 bodyDir;

        /// <summary>胴体ロール角（度）。InputCollector で累積済みの絶対値</summary>
        public float bodyRoll;

        public NetworkButtons Buttons;
    }
}
