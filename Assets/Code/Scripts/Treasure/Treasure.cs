// Assets/Code/Scripts/Treasure/Treasure.cs
using Fusion;
using UnityEngine;
using MyFolder.Scripts.Utils;

namespace MyFolder.Scripts.Treasure
{
    /// <summary>
    /// 物理運搬対象の宝オブジェクト。
    /// 掴み手の集合を保持し、人数に応じて Rigidbody.mass を分配する。
    /// StateAuthority(=ホスト)が単一の真実源として人数集計と質量更新を行う。
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(NetworkObject))]
    public class Treasure : NetworkBehaviour
    {
        [Tooltip("質量と分配挙動を定義するプロファイル。Resources/Treasures 以下の asset を割り当てる。")]
        [SerializeField] private TreasureProfile profile;

        [Networked] public int GrabberCount { get; private set; }
        [Networked] public float CurrentMass { get; private set; }

        private TreasureGrabRegistry _registry;
        private Rigidbody _rigidbody;

        public TreasureProfile Profile => profile;

        public override void Spawned()
        {
            _rigidbody = GetComponent<Rigidbody>();

            if (profile == null)
            {
                DebugUtils.LogRagdollError("Treasure に TreasureProfile が割り当てられていません", this);
                enabled = false;
                return;
            }

            _registry = new TreasureGrabRegistry(
                baseMass: profile.BaseMass,
                minSharedMass: profile.MinSharedMass,
                maxGrabbers: profile.MaxGrabbers);

            if (Object.HasStateAuthority)
            {
                GrabberCount = 0;
                CurrentMass = profile.BaseMass;
                _rigidbody.mass = profile.BaseMass;
            }
        }

        /// <summary>
        /// 掴みが成立した時、HandContact の StateAuthority 側から呼ばれる。
        /// 掴みが受理された場合 true を返す（HandContact はこの結果に応じて FixedJoint を作るかどうか決められる）。
        /// </summary>
        public bool NotifyGrabbed(NetworkId handObjectId)
        {
            if (!Object.HasStateAuthority) return false;
            if (Runner != null && Runner.IsResimulation) return false;

            int handIdRaw = (int)handObjectId.Raw;
            bool added = _registry.TryAdd(handIdRaw);
            if (added)
            {
                ApplyMass();
            }
            return added;
        }

        /// <summary>
        /// 解放された時、HandContact の StateAuthority 側から呼ばれる。
        /// </summary>
        public void NotifyReleased(NetworkId handObjectId)
        {
            if (!Object.HasStateAuthority) return;
            if (Runner != null && Runner.IsResimulation) return;

            int handIdRaw = (int)handObjectId.Raw;
            if (_registry.TryRemove(handIdRaw))
            {
                ApplyMass();
            }
        }

        private void ApplyMass()
        {
            GrabberCount = _registry.GrabberCount;
            CurrentMass = _registry.CurrentMass;
            _rigidbody.mass = CurrentMass;
        }
    }
}
