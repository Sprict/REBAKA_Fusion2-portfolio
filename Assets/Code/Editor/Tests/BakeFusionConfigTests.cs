using System;
using System.IO;
using MyFolder.Editor;
using NUnit.Framework;

namespace MyFolder.Editor.Tests
{
    public sealed class BakeFusionConfigTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "BakeFusionConfigTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        [Test]
        public void ShouldBakeForMppm_ReturnsTrueWhenBackupIsMissing()
        {
            string sourcePath = WriteFile("NetworkProjectConfig.fusion", DateTime.UtcNow);
            string backupPath = Path.Combine(_tempDir, "NetworkProjectConfigBackup.json");

            Assert.That(BakeFusionConfig.ShouldBakeForMppm(sourcePath, backupPath), Is.True);
        }

        [Test]
        public void ShouldBakeForMppm_ReturnsTrueWhenSourceIsNewerThanBackup()
        {
            string sourcePath = WriteFile("NetworkProjectConfig.fusion", new DateTime(2026, 6, 29, 10, 0, 0, DateTimeKind.Utc));
            string backupPath = WriteFile("NetworkProjectConfigBackup.json", new DateTime(2026, 6, 29, 9, 0, 0, DateTimeKind.Utc));

            Assert.That(BakeFusionConfig.ShouldBakeForMppm(sourcePath, backupPath), Is.True);
        }

        [Test]
        public void ShouldBakeForMppm_ReturnsFalseWhenBackupIsCurrent()
        {
            string sourcePath = WriteFile("NetworkProjectConfig.fusion", new DateTime(2026, 6, 29, 9, 0, 0, DateTimeKind.Utc));
            string backupPath = WriteFile("NetworkProjectConfigBackup.json", new DateTime(2026, 6, 29, 10, 0, 0, DateTimeKind.Utc));

            Assert.That(BakeFusionConfig.ShouldBakeForMppm(sourcePath, backupPath), Is.False);
        }

        private string WriteFile(string fileName, DateTime lastWriteTimeUtc)
        {
            string path = Path.Combine(_tempDir, fileName);
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
            return path;
        }
    }
}
