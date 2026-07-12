// Assets/Code/Scripts/Treasure/TreasureGrabRegistry.cs
using System.Collections.Generic;

namespace MyFolder.Scripts.Treasure
{
    /// <summary>
    /// Treasure を掴んでいる「手」の集合と、それに応じた質量分配を計算する純粋ロジック。
    /// NetworkBehaviour から委譲されることを前提とし、それ自体は MonoBehaviour ではない。
    /// </summary>
    public sealed class TreasureGrabRegistry
    {
        private readonly float _baseMass;
        private readonly float _minSharedMass;
        private readonly int _maxGrabbers;
        private readonly HashSet<int> _grabberHandIds = new HashSet<int>();

        public TreasureGrabRegistry(float baseMass, float minSharedMass, int maxGrabbers)
        {
            _baseMass = baseMass;
            _minSharedMass = minSharedMass;
            _maxGrabbers = maxGrabbers;
        }

        public int GrabberCount => _grabberHandIds.Count;

        public float CurrentMass
        {
            get
            {
                if (_grabberHandIds.Count == 0)
                    return _baseMass;

                float divided = _baseMass / _grabberHandIds.Count;
                return divided < _minSharedMass ? _minSharedMass : divided;
            }
        }

        public bool TryAdd(int handId)
        {
            if (_grabberHandIds.Count >= _maxGrabbers)
                return false;
            return _grabberHandIds.Add(handId);
        }

        public bool TryRemove(int handId)
        {
            return _grabberHandIds.Remove(handId);
        }
    }
}
