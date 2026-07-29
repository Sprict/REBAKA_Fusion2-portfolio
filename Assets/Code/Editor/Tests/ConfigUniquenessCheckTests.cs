using System.Collections.Generic;
using MyFolder.Editor.Preflight;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class ConfigUniquenessCheckTests
    {
        private const string Canonical = ConfigUniquenessCheck.CanonicalPath;

        [Test]
        public void Evaluate_PassesWhenOnlyCanonicalExists()
        {
            var result = ConfigUniquenessCheck.Evaluate(new List<string> { Canonical });
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_FailsWhenNoConfigFound()
        {
            var result = ConfigUniquenessCheck.Evaluate(new List<string>());
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
        }

        [Test]
        public void Evaluate_FailsWithStrayPathInMessage_WhenStrayExists()
        {
            const string stray = "Assets/Level/Photon/NetworkProjectConfig.fusion";
            var result = ConfigUniquenessCheck.Evaluate(new List<string> { Canonical, stray });
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
            Assert.That(result.Message, Does.Contain(stray));
        }

        [Test]
        public void Evaluate_FailsWhenOnlyStrayExists()
        {
            var result = ConfigUniquenessCheck.Evaluate(
                new List<string> { "Assets/Somewhere/NetworkProjectConfig.fusion" });
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Fail));
        }
    }
}
