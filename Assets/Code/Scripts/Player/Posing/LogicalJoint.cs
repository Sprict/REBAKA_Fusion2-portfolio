namespace MyFolder.Scripts.Player.Posing
{
    /// <summary>
    /// ラグドール骨の論理ID。
    ///
    /// これまで RagdollPhysics は <c>_bodyJoints[3]</c> のようなマジックナンバーで骨へアクセスしていた。
    /// その配列は <c>GetComponentsInChildren&lt;ConfigurableJoint&gt;</c> の戻り順で作られており、
    /// 「3番目＝右上腕」が成り立つのは今のプレハブの階層順がたまたまそうだから、という暗黙依存だった。
    /// 階層構成が異なるモデルに差し替えると順序がズレて別の骨が動いてしまう ＝ これがモデル依存の正体。
    ///
    /// このenumで骨を「意味」で指し、実 Joint との対応は <see cref="PlayerBoneMap"/> がモデルごとに保持する。
    /// これによりポーズデータ（<see cref="ActionPoseAsset"/>）は論理IDをキーにでき、
    /// モデルを差し替えても対応さえ取り直せば再利用できる。
    ///
    /// 数値は従来のインデックス定数（IndexRoot=0 … IndexLeftHand=14）と一致させてあるので、
    /// 既存プレハブは現状の配列順から機械的に移行できる（<see cref="PlayerBoneMap"/> の自動割当を参照）。
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
