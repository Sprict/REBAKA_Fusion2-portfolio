using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Player.Posing
{
    /// <summary>
    /// 1アクション分（例: Reach の決めポーズ、Punch の引きポーズ）のターゲットポーズ。
    ///
    /// 各論理骨について「安定姿勢(rest)からの相対回転」を Euler(度) で保持する。
    /// 実行時の適用は従来ロジックを踏襲し、<c>joint.targetRotation = rest * Quaternion.Euler(eulerDelta)</c>。
    ///
    /// なぜ絶対値ではなく rest 相対デルタか:
    /// APR サンプルは <c>new Quaternion(0.58f, ...)</c> のような絶対 targetRotation を直打ちしていたが、
    /// これは APR 固有リグの joint ローカル軸に合わせて手調整された値で、別モデルでは破綻する。
    /// 「安定姿勢からどれだけ曲げるか」という相対量なら、安定姿勢が異なるモデルでも
    /// “同じ意図の曲げ”を再現しやすい（完全自動の retarget ではなく、ツールでモデルごとに録り直す前提）。
    ///
    /// このアセットは「静的な決めポーズ」だけを持つ。マウス照準による上下(Body ピッチ)や左右ミラーは
    /// コード側の薄いオーバーレイとして上に重ねる（データ＝見た目 / コード＝入力変調 の責務分離）。
    /// </summary>
    [CreateAssetMenu(fileName = "ActionPose", menuName = "REBAKA/Action Pose", order = 0)]
    public sealed class ActionPoseAsset : ScriptableObject
    {
        [Serializable]
        public struct JointDelta
        {
            public LogicalJoint joint;

            [Tooltip("安定姿勢(rest)からの相対回転。度・joint ローカル。実行時 rest * Quaternion.Euler(eulerDelta)。")]
            public Vector3 eulerDelta;
        }

        [Tooltip("アクションの識別名（デバッグ・ツール表示用）。")]
        [SerializeField] private string _actionName;

        [Tooltip("このアクションで曲げる骨のデルタ群。ここに無い骨は安定姿勢のまま。")]
        [SerializeField] private List<JointDelta> _deltas = new List<JointDelta>();

        public string ActionName => _actionName;
        public IReadOnlyList<JointDelta> Deltas => _deltas;

        /// <summary>
        /// 指定論理骨のデルタ(Euler度)を取得する。未登録なら false。
        /// </summary>
        public bool TryGetDelta(LogicalJoint joint, out Vector3 eulerDelta)
        {
            for (int i = 0; i < _deltas.Count; i++)
            {
                if (_deltas[i].joint == joint)
                {
                    eulerDelta = _deltas[i].eulerDelta;
                    return true;
                }
            }

            eulerDelta = Vector3.zero;
            return false;
        }
    }
}
