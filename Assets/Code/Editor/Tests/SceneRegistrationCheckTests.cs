using System.Collections.Generic;
using MyFolder.Editor.Preflight;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class SceneRegistrationCheckTests
    {
        private static readonly string[] Required = { "Test_Playground", "MapNetworkSandbox" };

        [Test]
        public void Evaluate_PassesWhenAllRequiredScenesEnabled()
        {
            var scenes = new List<(string, bool)>
            {
                ("Test_Playground", true),
                ("MapNetworkSandbox", true),
                ("Main_Backup", false),
            };
            var result = SceneRegistrationCheck.Evaluate(scenes, Required);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_FailsWhenSceneMissing_AndNamesIt()
        {
            var scenes = new List<(string, bool)> { ("Test_Playground", true) };
            var result = SceneRegistrationCheck.Evaluate(scenes, Required);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("MapNetworkSandbox"));
        }

        [Test]
        public void Evaluate_FailsWhenSceneRegisteredButDisabled()
        {
            var scenes = new List<(string, bool)>
            {
                ("Test_Playground", true),
                ("MapNetworkSandbox", false),
            };
            var result = SceneRegistrationCheck.Evaluate(scenes, Required);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain("MapNetworkSandbox"));
        }
    }
}
