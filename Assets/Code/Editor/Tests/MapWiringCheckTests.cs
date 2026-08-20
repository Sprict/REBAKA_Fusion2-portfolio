using Rebaka.Editor.Preflight;
using NUnit.Framework;

namespace Rebaka.Editor.Tests
{
    public sealed class MapWiringCheckTests
    {
        [Test]
        public void Constructor_StoresExpectedScenePath()
        {
            const string expectedScenePath = "Assets/Level/Scenes/MapNetworkSandbox.unity";

            var check = new MapWiringCheck(expectedScenePath);

            Assert.That(check.ExpectedScenePath, Is.EqualTo(expectedScenePath));
        }

        [Test]
        public void Evaluate_FailsWhenMapBuilderIsMissing()
        {
            var result = MapWiringCheck.Evaluate(CreateWiredSnapshot(spawnerCount: 1, distributorCount: 1));

            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("MapBuilder"));
        }

        [Test]
        public void Evaluate_FailsWhenMapTreasureSpawnerIsMissing()
        {
            var result = MapWiringCheck.Evaluate(CreateWiredSnapshot(builderCount: 1, distributorCount: 1));

            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("MapTreasureSpawner"));
        }

        [Test]
        public void Evaluate_FailsWhenMapNetworkDistributorIsMissing()
        {
            var result = MapWiringCheck.Evaluate(CreateWiredSnapshot(builderCount: 1, spawnerCount: 1));

            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("MapNetworkDistributor"));
        }

        [Test]
        public void Evaluate_PassesWhenAllWired()
        {
            var s = CreateWiredSnapshot(builderCount: 1, spawnerCount: 1, distributorCount: 1);
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_FailsWhenBuilderCatalogMissing()
        {
            var s = CreateWiredSnapshot(builderCount: 1, spawnerCount: 1, distributorCount: 1);
            s.BuilderCatalogMissing = true;
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("Catalog"));
        }

        [Test]
        public void Evaluate_FailsWhenSpawnerPrefabMissing()
        {
            var s = CreateWiredSnapshot(builderCount: 1, spawnerCount: 1, distributorCount: 1);
            s.SpawnerPrefabMissing = true;
            var result = MapWiringCheck.Evaluate(s);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("Treasure Prefab"));
        }

        private static MapWiringCheck.Snapshot CreateWiredSnapshot(
            int builderCount = 0,
            int spawnerCount = 0,
            int distributorCount = 0)
        {
            return new MapWiringCheck.Snapshot
            {
                BuilderCount = builderCount,
                SpawnerCount = spawnerCount,
                DistributorCount = distributorCount,
                BuilderCatalogMissing = false,
                SpawnerPrefabMissing = false,
            };
        }
    }
}
