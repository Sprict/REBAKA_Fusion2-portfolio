using UnityEngine;

public static class CheckFootGrounded
{
    public static string Execute()
    {
        if (!Application.isPlaying) return "ERROR: Not in Play Mode.";

        var contacts = Object.FindObjectsByType<RagdollFootContact>(FindObjectsSortMode.None);
        if (contacts.Length == 0) return "ERROR: No RagdollFootContact found.";

        var sb = new System.Text.StringBuilder();
        foreach (var c in contacts)
        {
            // groundLayer はSerializeField（private）なのでリフレクションで読む
            var field = typeof(RagdollFootContact).GetField("groundLayer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            int mask = field != null ? ((LayerMask)field.GetValue(c)).value : -999;
            bool grounded = c.GetIsGrounded();

            sb.AppendLine($"  {c.gameObject.name}: groundLayer={mask} grounded={grounded} pos={c.transform.position}");
        }

        return sb.ToString();
    }
}
