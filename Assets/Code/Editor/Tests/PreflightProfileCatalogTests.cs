using System;
using System.Linq;
using NUnit.Framework;
using Rebaka.Editor.Preflight;

namespace Rebaka.Editor.Tests
{
    public sealed class PreflightProfileCatalogTests
    {
        [Test]
        public void Create_ProductionIntegration_HasExactChecksInOrder()
        {
            PreflightProfileDefinition definition = PreflightProfileCatalog.Create(
                PreflightProfile.ProductionIntegration);

            Assert.That(definition.ExpectedScenePath, Is.EqualTo(PreflightProfileCatalog.ProductionScenePath));
            Assert.That(definition.BlocksDevelopIntegrationWhenFail, Is.True);
            Assert.That(definition.Precondition, Is.TypeOf<ActiveSceneCheck>());
            Assert.That(definition.Checks.Select(check => check.GetType()), Is.EqualTo(new[]
            {
                typeof(ConfigUniquenessCheck),
                typeof(WeaveAssembliesCheck),
                typeof(SceneRegistrationCheck),
                typeof(ScenePlacedObjectsCheck),
                typeof(BackupFreshnessCheck)
            }));

            Assert.That(((ActiveSceneCheck)definition.Precondition).ExpectedScenePath,
                Is.EqualTo(definition.ExpectedScenePath));
            Assert.That(((SceneRegistrationCheck)definition.Checks[2]).RequiredScenePaths,
                Is.EqualTo(new[] { PreflightProfileCatalog.ProductionScenePath }));
            Assert.That(((ScenePlacedObjectsCheck)definition.Checks[3]).ExpectedScenePath,
                Is.EqualTo(definition.ExpectedScenePath));
        }

        [Test]
        public void Create_MapPrototype_HasExactChecksInOrder()
        {
            PreflightProfileDefinition definition = PreflightProfileCatalog.Create(
                PreflightProfile.MapPrototype);

            Assert.That(definition.ExpectedScenePath, Is.EqualTo(PreflightProfileCatalog.MapPrototypeScenePath));
            Assert.That(definition.BlocksDevelopIntegrationWhenFail, Is.False);
            Assert.That(definition.Precondition, Is.TypeOf<ActiveSceneCheck>());
            Assert.That(definition.Checks.Select(check => check.GetType()), Is.EqualTo(new[]
            {
                typeof(MapWiringCheck),
                typeof(ScenePlacedObjectsCheck)
            }));

            Assert.That(((ActiveSceneCheck)definition.Precondition).ExpectedScenePath,
                Is.EqualTo(definition.ExpectedScenePath));
            Assert.That(((MapWiringCheck)definition.Checks[0]).ExpectedScenePath,
                Is.EqualTo(definition.ExpectedScenePath));
            Assert.That(((ScenePlacedObjectsCheck)definition.Checks[1]).ExpectedScenePath,
                Is.EqualTo(definition.ExpectedScenePath));
        }

        [Test]
        public void Create_UnknownProfile_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                PreflightProfileCatalog.Create((PreflightProfile)999));
        }
    }
}
