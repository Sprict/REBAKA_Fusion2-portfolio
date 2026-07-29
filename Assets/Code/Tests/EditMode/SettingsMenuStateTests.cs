using MyFolder.Scripts.Settings;
using NUnit.Framework;

namespace MyFolder.Tests.EditMode
{
    public sealed class SettingsMenuStateTests
    {
        [SetUp]
        public void SetUp()
        {
            SettingsMenuState.Close();
        }

        [Test]
        public void Open_BlocksGameplayInputUntilClosed()
        {
            Assert.That(SettingsMenuState.IsGameplayInputBlocked, Is.False);

            SettingsMenuState.Open();

            Assert.That(SettingsMenuState.IsGameplayInputBlocked, Is.True);

            SettingsMenuState.Close();

            Assert.That(SettingsMenuState.IsGameplayInputBlocked, Is.False);
        }

        [Test]
        public void RepeatedOpenAndClose_LeaveTheExpectedState()
        {
            SettingsMenuState.Open();
            SettingsMenuState.Open();
            SettingsMenuState.Close();
            SettingsMenuState.Close();

            Assert.That(SettingsMenuState.IsGameplayInputBlocked, Is.False);
        }
    }
}