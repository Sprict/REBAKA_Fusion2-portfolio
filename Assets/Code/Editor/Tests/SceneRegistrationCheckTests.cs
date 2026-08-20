using System.Collections.Generic;
using Rebaka.Editor.Preflight;
using NUnit.Framework;

namespace Rebaka.Editor.Tests
{
    public sealed class SceneRegistrationCheckTests
    {
        private const string TestPlaygroundPath = "Assets/Level/Scenes/Test_Playground.unity";
        private const string MapNetworkSandboxPath = "Assets/Level/Scenes/MapNetworkSandbox.unity";

        private static readonly string[] Required = { TestPlaygroundPath, MapNetworkSandboxPath };

        [Test]
        public void Evaluate_PassesWhenAllRequiredScenesEnabled()
        {
            var scenes = new List<(string, bool)>
            {
                (TestPlaygroundPath, true),
                (MapNetworkSandboxPath, true),
                ("Assets/Level/Scenes/Main_Backup.unity", false),
            };
            var result = SceneRegistrationCheck.Evaluate(scenes, Required);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_FailsWhenSceneMissing_AndNamesIt()
        {
            var scenes = new List<(string, bool)> { (TestPlaygroundPath, true) };
            var result = SceneRegistrationCheck.Evaluate(scenes, Required);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain(MapNetworkSandboxPath));
        }

        [Test]
        public void Evaluate_FailsWhenSceneRegisteredButDisabled()
        {
            var scenes = new List<(string, bool)>
            {
                (TestPlaygroundPath, true),
                (MapNetworkSandboxPath, false),
            };
            var result = SceneRegistrationCheck.Evaluate(scenes, Required);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain(MapNetworkSandboxPath));
        }

        [Test]
        public void Evaluate_SameFileNameAtDifferentPath_ReturnsFail()
        {
            var scenes = new List<(string, bool)>
            {
                ("Assets/Other/Test_Playground.unity", true),
                (MapNetworkSandboxPath, true),
            };

            var result = SceneRegistrationCheck.Evaluate(scenes, Required);

            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain(TestPlaygroundPath));
        }

        [Test]
        public void Evaluate_ExpectedExactPathEnabled_ReturnsPass()
        {
            var scenes = new List<(string, bool)>
            {
                (TestPlaygroundPath, true),
                (MapNetworkSandboxPath, true),
            };

            var result = SceneRegistrationCheck.Evaluate(scenes, Required);

            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_ExpectedExactPathDisabled_ReturnsFail()
        {
            var scenes = new List<(string, bool)>
            {
                (TestPlaygroundPath, false),
                (MapNetworkSandboxPath, true),
            };

            var result = SceneRegistrationCheck.Evaluate(scenes, Required);

            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain(TestPlaygroundPath));
        }

        [Test]
        public void Evaluate_NormalizesBackslashesButNotCase()
        {
            var separatorVariant = new List<(string, bool)>
            {
                (@"Assets\Level\Scenes\Test_Playground.unity", true),
                (MapNetworkSandboxPath, true),
            };

            var separatorResult = SceneRegistrationCheck.Evaluate(separatorVariant, Required);

            Assert.That(separatorResult.Status, Is.EqualTo(PreflightStatus.Pass));

            var caseVariant = new List<(string, bool)>
            {
                ("assets/Level/Scenes/Test_Playground.unity", true),
                (MapNetworkSandboxPath, true),
            };

            var caseResult = SceneRegistrationCheck.Evaluate(caseVariant, Required);

            Assert.That(caseResult.Status, Is.EqualTo(PreflightStatus.Fail));
        }
    }
}
