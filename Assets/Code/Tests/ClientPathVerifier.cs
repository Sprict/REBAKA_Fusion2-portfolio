using UnityEngine;
using MyFolder.Scripts.Player;

/// <summary>
/// ホスト上で「クライアント側のコードパスが正しく設定されるか」を検証する。
/// 実際にクライアントにはならないが、EnsureClientProxyBootstrap で設定される
/// kinematic状態やApplyProxyPoseCorrectionの分岐を確認する。
/// </summary>
public static class ClientPathVerifier
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        // プレファブの設定を直接確認
        var prefab = Resources.Load<GameObject>("newAPRPlayer");
        // Resources では見つからないのでシーン内のインスタンスを確認
        var ctrls = Object.FindObjectsByType<RagdollController>(FindObjectsSortMode.None);
        if (ctrls.Length == 0) return "ERROR: No RagdollController found.";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== CLIENT PATH VERIFICATION ===");

        foreach (var ctrl in ctrls)
        {
            var rootRbs = ctrl.GetComponentsInChildren<Rigidbody>();
            Rigidbody rootRb = null;
            foreach (var rb in rootRbs)
            {
                if (rb.gameObject.name == "APR_Root") { rootRb = rb; break; }
            }
            if (rootRb == null) continue;

            // hasStateAuthority
            bool hasState = ctrl.Object != null && ctrl.Object.HasStateAuthority;

            sb.AppendLine($"  Player: {ctrl.gameObject.name}");
            sb.AppendLine($"    HasStateAuthority: {hasState}");
            sb.AppendLine($"    Root isKinematic: {rootRb.isKinematic}");
            sb.AppendLine($"    Root useGravity: {rootRb.useGravity}");
            sb.AppendLine($"    Root interpolation: {rootRb.interpolation}");
            sb.AppendLine($"    Root position: {rootRb.position}");

            // ConfigurableJoint のdrive状態
            var rootJoint = rootRb.GetComponent<ConfigurableJoint>();
            if (rootJoint != null)
            {
                sb.AppendLine($"    Root angXDrive: spring={rootJoint.angularXDrive.positionSpring} damper={rootJoint.angularXDrive.positionDamper}");
            }

            // 検証: ホスト側ではisKinematic=false、クライアント側ではisKinematic=true であるべき
            if (hasState)
            {
                bool correct = !rootRb.isKinematic && rootRb.useGravity;
                sb.AppendLine($"    {(correct ? "✅" : "❌")} HOST: isKinematic=false, useGravity=true");
            }
            else
            {
                bool correct = rootRb.isKinematic;
                sb.AppendLine($"    {(correct ? "✅" : "❌")} CLIENT: isKinematic=true (for MovePosition)");
            }
        }

        Debug.Log("[ClientPath] " + sb.ToString());
        return sb.ToString();
    }
}
