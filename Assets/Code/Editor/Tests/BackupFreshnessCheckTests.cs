using MyFolder.Editor.Preflight;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class BackupFreshnessCheckTests
    {
        [Test]
        public void Evaluate_PassesWhenBackupIsFresh()
        {
            var result = BackupFreshnessCheck.Evaluate(shouldBake: false);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Pass));
        }

        [Test]
        public void Evaluate_WarnsWhenBackupIsMissingOrStale()
        {
            var result = BackupFreshnessCheck.Evaluate(shouldBake: true);
            Assert.That(result.Status, Is.EqualTo(PreflightStatus.Warning));
        }
    }
}
