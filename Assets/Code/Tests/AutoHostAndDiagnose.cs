using UnityEngine;
using MyFolder.Scripts.Network;

public static class AutoHostAndDiagnose
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var sm = Object.FindFirstObjectByType<SessionManager>();
        if (sm == null)
            return "ERROR: SessionManager not found.";

        if (!sm.IsSessionActive)
            sm.StartSession(Fusion.GameMode.Host, "DiagTest");

        // SpecTest + ClientSimTest を遅延起動
        if (Object.FindFirstObjectByType<RunSpecTest>() == null)
        {
            var go = new GameObject("__TestLauncher__");
            go.AddComponent<RunSpecTest>();
        }

        return "OK: Host + Tests scheduled.";
    }
}
