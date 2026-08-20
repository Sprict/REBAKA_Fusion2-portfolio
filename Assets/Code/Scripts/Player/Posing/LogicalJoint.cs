namespace Rebaka.Player.Posing
{
    /// <summary>
    /// ラグドール骨の論理ID。
    ///
    /// これまで RagdollPhysics は <c>_bodyJoints[3]</c> のようなマジックナンバーで骨へアクセスしていた。
    /// その配列は <c>GetComponentsInChildren&lt;ConfigurableJoint&gt;</c> の戻り順で作られており、
    /// 「3番目＝右上腕」が成り立つのは今のプレハブの階層順がたまたまそうだから、という暗黙依存だった。
    /// 階層構成が異なるモデルに差し替えると順序がズレて別の骨が動いてしまう ＝ これがモデル依存の正体。
    ///
    /// このenumで骨を「意味」で指すことで、ポーズデータ（<see cref="ActionPoseAsset"/>）は
    /// 論理IDをキーにでき、モデルを差し替えても対応さえ取り直せば再利用できる。
    ///
    /// この enum がボディ配列の添字の唯一の正本。RagdollPhysics / ClientProxyCorrection /
    /// RagdollRigSetup の Index* 定数はここからキャストで導出しているので、
    /// 骨の並びを変えるときはこの enum だけを直せばよい。
    ///
    /// 注: 実 Joint とのモデル別マッピングを保持する型（旧コメントの PlayerBoneMap）は未実装。
    /// 現状は「配列順 = この enum の値」という前提のまま運用している。
    /// </summary>
    public enum LogicalJoint
    {
        Root = 0,
        Body = 1,
        Head = 2,
        UpperRightArm = 3,
        LowerRightArm = 4,
        UpperLeftArm = 5,
        LowerLeftArm = 6,
        UpperRightLeg = 7,
        LowerRightLeg = 8,
        UpperLeftLeg = 9,
        LowerLeftLeg = 10,
        RightFoot = 11,
        LeftFoot = 12,
        RightHand = 13,
        LeftHand = 14,
    }
}
