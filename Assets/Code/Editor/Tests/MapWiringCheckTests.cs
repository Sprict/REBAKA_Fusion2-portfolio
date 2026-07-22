using MyFolder.Editor.Preflight;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class MapWiringCheckTests
    {
        [Test]
        public void Evaluate_WarnsWhenNoMapComponentsInScene()
        {
            var result = MapWiringCheck.Evaluate(default);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Warning));
        }

        [Test]
        public void Evaluate_PassesWhenAllWired()
        {
            var s = new MapWiringCheck.Snapshot
            {
                BuilderCount = 1,
                SpawnerCount = 1,
                DistributorCount = 1,
                BuilderCatalogMissing = false,
                SpawnerPrefabMissing = false,
            };
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_FailsWhenBuilderCatalogMissing()
        {
            var s = new MapWiringCheck.Snapshot { BuilderCount = 1, BuilderCatalogMissing = true };
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("Catalog"));
        }

        [Test]
        public void Evaluate_FailsWhenSpawnerPrefabMissing()
        {
            var s = new MapWiringCheck.Snapshot { SpawnerCount = 1, SpawnerPrefabMissing = true };
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
        }

        [Test]
        public void Evaluate_FailsWhenDistributorHasNoBuilder()
        {
            var s = new MapWiringCheck.Snapshot { DistributorCount = 1, BuilderCount = 0 };
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
        }
    }
}
