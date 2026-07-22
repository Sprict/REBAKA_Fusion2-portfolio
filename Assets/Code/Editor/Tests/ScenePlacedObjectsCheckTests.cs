using System.Collections.Generic;
using MyFolder.Editor.Preflight;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class ScenePlacedObjectsCheckTests
    {
        [Test]
        public void Evaluate_PassesWhenNoScenePlacedObjects()
        {
            var result = ScenePlacedObjectsCheck.Evaluate(new List<(string, string)>());
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_WarnsAndListsObjects_WhenScenePlacedObjectsExist()
        {
            var placed = new List<(string, string)>
            {
                ("Obs_Cube", "Obs_Cube"),
                ("HandMadeThing", null),
            };
            var result = ScenePlacedObjectsCheck.Evaluate(placed);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Warning));
            Assert.That(result.Message, Does.Contain("Obs_Cube"));
            Assert.That(result.Message, Does.Contain("HandMadeThing"));
        }
    }
}
