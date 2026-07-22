using UnityEngine;
using MyFolder.Scripts.Network;

/// <summary>
/// メインエディタをクライアントとしてセッションに参加させる。
/// clone_0をホストとして先に起動した後にこれを実行する。
/// </summary>
public static class JoinAsClient
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode. Play Modeにしてから実行してください。";

        var sm = Object.FindFirstObjectByType<SessionManager>();
        if (sm == null)
            return "ERROR: SessionManager not found.";

        if (sm.IsSessionActive)
            return "INFO: Session already active.";

        // Clientとして参加（clone_0がHostとして起動済みの同じセッション名に参加）
        sm.StartSession(Fusion.GameMode.Client, "TestRoom");
        return "OK: Joining as Client to room 'TestRoom'. clone_0をHostとして同じルーム名で起動してください。";
    }
}
