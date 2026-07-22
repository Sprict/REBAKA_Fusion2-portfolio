using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace MyFolder.Scripts.Settings
{
    public static class InputSettingsRuntime
    {
        private const string PlayerPrefsPrefix = "REBAKA.InputSettings.v1";
        private static readonly InputSettingsPersistence Persistence = new InputSettingsPersistence(PlayerPrefsPrefix);
        private static readonly HashSet<InputActionAsset> RegisteredAssets = new HashSet<InputActionAsset>();
        private static InputSettingsData _settings;

        public static InputSettingsData Current
        {
            get
            {
                if (_settings == null)
                    _settings = Persistence.Load();

                return _settings;
            }
        }

        public static void Register(InputActionAsset inputActions)
        {
            if (inputActions == null)
                return;

            RegisteredAssets.Add(inputActions);
            ApplySavedSettings(inputActions);
        }

        public static void Unregister(InputActionAsset inputActions)
        {
            if (inputActions != null)
                RegisteredAssets.Remove(inputActions);
        }

        public static bool TrySetDeviceEnabled(GameplayDeviceGroup group, bool enabled)
        {
            InputSettingsData settings = Current;
            if (!settings.TrySetDeviceEnabled(group, enabled))
                return false;

            SaveSettings(settings);
            return true;
        }
        public static void SaveSettings(InputSettingsData settings)
        {
            _settings = settings ?? new InputSettingsData();
            _settings.Normalize();
            Persistence.Save(_settings);

            foreach (InputActionAsset inputActions in RegisteredAssets)
                InputSettingsProcessor.Apply(inputActions, _settings);
        }

        public static void SaveBindingOverrides(InputActionAsset source)
        {
            if (source == null)
                return;

            Persistence.SaveBindingOverrides(InputSettingsBindingOverrides.Serialize(source));

            foreach (InputActionAsset inputActions in RegisteredAssets)
            {
                InputSettingsBindingOverrides.Restore(inputActions, Persistence.LoadBindingOverrides());
                InputSettingsProcessor.Apply(inputActions, Current);
            }
        }

        public static void ResetToDefaults()
        {
            _settings = new InputSettingsData();
            Persistence.Save(_settings);
            Persistence.SaveBindingOverrides(string.Empty);

            foreach (InputActionAsset inputActions in RegisteredAssets)
            {
                InputSettingsBindingOverrides.Restore(inputActions, string.Empty);
                InputSettingsProcessor.Apply(inputActions, _settings);
            }
        }

        private static void ApplySavedSettings(InputActionAsset inputActions)
        {
            InputSettingsBindingOverrides.Restore(inputActions, Persistence.LoadBindingOverrides());
            InputSettingsProcessor.Apply(inputActions, Current);
        }
    }
}