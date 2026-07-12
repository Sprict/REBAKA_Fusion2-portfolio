using System;
using UnityEngine;

namespace MyFolder.Scripts.Settings
{
    /// <summary>
    /// 入力設定の永続化を管理するクラス
    /// </summary>
    public sealed class InputSettingsPersistence
    {
        private readonly string _settingsKey;
        private readonly string _bindingOverridesKey;

        public InputSettingsPersistence(string keyPrefix)
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
                throw new ArgumentException("A PlayerPrefs key prefix is required.", nameof(keyPrefix));

            _settingsKey = keyPrefix + ".settings";
            _bindingOverridesKey = keyPrefix + ".bindingOverrides";
        }

        public InputSettingsData Load()
        {
            if (!PlayerPrefs.HasKey(_settingsKey))
                return new InputSettingsData();

            string json = PlayerPrefs.GetString(_settingsKey);
            InputSettingsData settings = JsonUtility.FromJson<InputSettingsData>(json) ?? new InputSettingsData();
            settings.Normalize();
            return settings;
        }

        public void Save(InputSettingsData settings)
        {
            settings ??= new InputSettingsData();
            settings.Normalize();
            PlayerPrefs.SetString(_settingsKey, JsonUtility.ToJson(settings));
            PlayerPrefs.Save();
        }

        public string LoadBindingOverrides()
        {
            return PlayerPrefs.GetString(_bindingOverridesKey, string.Empty);
        }

        public void SaveBindingOverrides(string overridesJson)
        {
            PlayerPrefs.SetString(_bindingOverridesKey, overridesJson ?? string.Empty);
            PlayerPrefs.Save();
        }

        public void Clear()
        {
            PlayerPrefs.DeleteKey(_settingsKey);
            PlayerPrefs.DeleteKey(_bindingOverridesKey);
            PlayerPrefs.Save();
        }
    }
}