namespace MyFolder.Scripts.Settings
{
    public static class SettingsMenuState
    {
        public static bool IsGameplayInputBlocked { get; private set; }

        public static void Open()
        {
            IsGameplayInputBlocked = true;
        }

        public static void Close()
        {
            IsGameplayInputBlocked = false;
        }
    }
}