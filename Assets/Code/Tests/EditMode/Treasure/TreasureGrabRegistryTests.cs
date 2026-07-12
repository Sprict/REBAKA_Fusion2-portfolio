// Assets/Code/Tests/EditMode/Treasure/TreasureGrabRegistryTests.cs
using NUnit.Framework;
using MyFolder.Scripts.Treasure;

namespace MyFolder.Scripts.Tests.Treasure
{
    public class TreasureGrabRegistryTests
    {
        [Test]
        public void EmptyRegistry_ReportsBaseMass()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 6);
            Assert.That(registry.GrabberCount, Is.EqualTo(0));
            Assert.That(registry.CurrentMass, Is.EqualTo(200f).Within(0.001f));
        }

        [Test]
        public void SingleGrabber_DividesMassByOne()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 6);
            bool added = registry.TryAdd(handId: 1);
            Assert.That(added, Is.True);
            Assert.That(registry.GrabberCount, Is.EqualTo(1));
            Assert.That(registry.CurrentMass, Is.EqualTo(200f).Within(0.001f));
        }

        [Test]
        public void TwoGrabbers_HalveMass()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 6);
            registry.TryAdd(1);
            registry.TryAdd(2);
            Assert.That(registry.GrabberCount, Is.EqualTo(2));
            Assert.That(registry.CurrentMass, Is.EqualTo(100f).Within(0.001f));
        }

        [Test]
        public void ManyGrabbers_ClampedAtMinSharedMass()
        {
            // maxGrabbers を 10 にして全 10 件を受理させる。
            // 200/10=20 が minSharedMass=30 を下回るため clamp が発火する。
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 10);
            for (int handId = 1; handId <= 10; handId++)
            {
                registry.TryAdd(handId);
            }
            Assert.That(registry.CurrentMass, Is.EqualTo(30f).Within(0.001f),
                "minSharedMass 未満には絶対に下がらない");
        }

        [Test]
        public void ExceedingMaxGrabbers_RejectsAdd()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 2);
            Assert.That(registry.TryAdd(1), Is.True);
            Assert.That(registry.TryAdd(2), Is.True);
            Assert.That(registry.TryAdd(3), Is.False, "上限超過は拒否される");
            Assert.That(registry.GrabberCount, Is.EqualTo(2));
        }

        [Test]
        public void DuplicateAdd_IsIdempotent()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 6);
            Assert.That(registry.TryAdd(1), Is.True);
            Assert.That(registry.TryAdd(1), Is.False, "同じ handId を二度追加できない");
            Assert.That(registry.GrabberCount, Is.EqualTo(1));
        }

        [Test]
        public void Remove_ReducesCountAndRecalculatesMass()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 6);
            registry.TryAdd(1);
            registry.TryAdd(2);
            bool removed = registry.TryRemove(1);
            Assert.That(removed, Is.True);
            Assert.That(registry.GrabberCount, Is.EqualTo(1));
            Assert.That(registry.CurrentMass, Is.EqualTo(200f).Within(0.001f));
        }

        [Test]
        public void RemoveUnknown_ReturnsFalse()
        {
            var registry = new TreasureGrabRegistry(baseMass: 200f, minSharedMass: 30f, maxGrabbers: 6);
            registry.TryAdd(1);
            Assert.That(registry.TryRemove(99), Is.False);
            Assert.That(registry.GrabberCount, Is.EqualTo(1));
        }
    }
}
