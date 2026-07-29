using System;
using MyFolder.Scripts.Settings;
using NUnit.Framework;

namespace MyFolder.Tests.EditMode
{
    public sealed class InputSettingsPersistenceTests
    {
        [Test]
        public void SaveAndLoad_PreservesNormalizedSensitivityAndBindingOverrides()
        {
            string prefix = "REBAKA.Tests.InputSettings." + Guid.NewGuid().ToString("N");
            var persistence = new InputSettingsPersistence(prefix);

            try
            {
                persistence.Save(new InputSettingsData
                {
                    MouseLookX = 1.25f,
                    MouseLookY = -1f,
                    GamepadLookX = 0.75f,
                    GamepadLookY = 1.5f,
                    GamepadMoveX = 2f,
                    GamepadMoveY = 99f,
                    KeyboardMouseEnabled = false,
                    GamepadEnabled = true,
                });
                persistence.SaveBindingOverrides("{\"bindings\":[{\"id\":\"test\"}]}");

                InputSettingsData loaded = persistence.Load();

                Assert.That(loaded.MouseLookX, Is.EqualTo(1.25f));
                Assert.That(loaded.MouseLookY, Is.EqualTo(InputSettingsData.MinimumSensitivity));
                Assert.That(loaded.GamepadLookX, Is.EqualTo(0.75f));
                Assert.That(loaded.GamepadLookY, Is.EqualTo(1.5f));
                Assert.That(loaded.GamepadMoveX, Is.EqualTo(2f));
                Assert.That(loaded.GamepadMoveY, Is.EqualTo(InputSettingsData.MaximumSensitivity));
                Assert.That(loaded.KeyboardMouseEnabled, Is.False);
                Assert.That(loaded.GamepadEnabled, Is.True);
                Assert.That(persistence.LoadBindingOverrides(), Is.EqualTo("{\"bindings\":[{\"id\":\"test\"}]}"));
            }
            finally
            {
                persistence.Clear();
            }
        }
    }
}