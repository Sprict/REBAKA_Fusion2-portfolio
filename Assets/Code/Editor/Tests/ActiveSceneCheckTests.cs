using NUnit.Framework;
using Rebaka.Editor.Preflight;

namespace Rebaka.Editor.Tests
{
    public sealed class ActiveSceneCheckTests
    {
        [TestCase("Assets/Level/Scenes/Test_Playground.unity", "Assets/Level/Scenes/Test_Playground.unity", PreflightStatus.Pass)]
        [TestCase("Assets\\Level\\Scenes\\Test_Playground.unity", "Assets/Level/Scenes/Test_Playground.unity", PreflightStatus.Pass)]
        [TestCase("Assets/Other/Test_Playground.unity", "Assets/Level/Scenes/Test_Playground.unity", PreflightStatus.Fail)]
        [TestCase("assets/Level/Scenes/Test_Playground.unity", "Assets/Level/Scenes/Test_Playground.unity", PreflightStatus.Fail)]
        [TestCase("", "Assets/Level/Scenes/Test_Playground.unity", PreflightStatus.Fail)]
        public void Evaluate_UsesNormalizedFullAssetPath(
            string actualPath,
            string expectedPath,
            PreflightStatus expectedStatus)
        {
            PreflightResult result = ActiveSceneCheck.Evaluate(actualPath, expectedPath);

            Assert.That(result.Status, Is.EqualTo(expectedStatus));
        }
    }
}
