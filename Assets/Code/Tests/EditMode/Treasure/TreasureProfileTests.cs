// Assets/Code/Tests/EditMode/Treasure/TreasureProfileTests.cs
using NUnit.Framework;
using UnityEngine;
using MyFolder.Scripts.Treasure;

namespace MyFolder.Scripts.Tests.Treasure
{
    public class TreasureProfileTests
    {
        private TreasureProfile _profile;

        [TearDown]
        public void TearDown()
        {
            if (_profile != null)
            {
                Object.DestroyImmediate(_profile);
                _profile = null;
            }
        }

        [Test]
        public void DefaultValues_AreSafeForMvp()
        {
            _profile = ScriptableObject.CreateInstance<TreasureProfile>();

            Assert.That(_profile.BaseMass, Is.GreaterThan(0f));
            Assert.That(_profile.MinSharedMass, Is.GreaterThan(0f));
            Assert.That(_profile.MinSharedMass, Is.LessThanOrEqualTo(_profile.BaseMass));
            Assert.That(_profile.MaxGrabbers, Is.GreaterThanOrEqualTo(1));
            Assert.That(float.IsPositiveInfinity(_profile.BreakForceOverride),
                "MVP の既定は事実上破断しない設定であるべき");
        }
    }
}
