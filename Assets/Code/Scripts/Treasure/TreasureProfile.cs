// Assets/Code/Scripts/Treasure/TreasureProfile.cs
using UnityEngine;

namespace MyFolder.Scripts.Treasure
{
    [CreateAssetMenu(fileName = "TreasureProfile", menuName = "REBAKA/TreasureProfile", order = 0)]
    public class TreasureProfile : ScriptableObject
    {
        [Tooltip("掴み手が 0 人の時の質量(kg)。基準値。")]
        [SerializeField] private float baseMass = 200f;

        [Tooltip("最大人数で掴んだ時の最小質量(kg)。これ以下にはならない。")]
        [SerializeField] private float minSharedMass = 30f;

        [Tooltip("同時に掴める手の最大数。これを超えた場合の挙動は Treasure 側が決める。")]
        [SerializeField] private int maxGrabbers = 6;

        [Tooltip("Treasure 掴み中の FixedJoint.breakForce を上書きする値。既定は事実上破断しない。")]
        [SerializeField] private float breakForceOverride = float.PositiveInfinity;

        public float BaseMass => baseMass;
        public float MinSharedMass => minSharedMass;
        public int MaxGrabbers => maxGrabbers;
        public float BreakForceOverride => breakForceOverride;
    }
}
