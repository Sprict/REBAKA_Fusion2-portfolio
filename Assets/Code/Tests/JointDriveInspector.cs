using UnityEngine;
using System.Text;

/// <summary>
/// 全ConfigurableJointのドライブ状態を一覧表示する。
/// </summary>
public static class JointDriveInspector
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        // APR_Root を検索
        var allRbs = Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
        Rigidbody rootRb = null;
        foreach (var rb in allRbs)
        {
            if (rb.gameObject.name == "APR_Root")
            {
                rootRb = rb;
                break;
            }
        }

        if (rootRb == null)
            return "ERROR: APR_Root not found.";

        // APR_Root の親（newAPRPlayer(Clone)）から全ConfigurableJointを取得
        Transform root = rootRb.transform;
        while (root.parent != null) root = root.parent;

        var joints = root.GetComponentsInChildren<ConfigurableJoint>(true);
        var sb = new StringBuilder();
        sb.AppendLine($"=== JOINT DRIVE INSPECTION (root={root.name}, {joints.Length} joints) ===");

        for (int i = 0; i < joints.Length; i++)
        {
            var j = joints[i];
            var angX = j.angularXDrive;
            var angYZ = j.angularYZDrive;
            var slerp = j.slerpDrive;
            var xd = j.xDrive;
            var yd = j.yDrive;
            var zd = j.zDrive;

            sb.AppendLine($"  [{i:D2}] {j.gameObject.name}");
            sb.AppendLine($"       angXDrive:  spring={angX.positionSpring:F0} damper={angX.positionDamper:F1} maxF={angX.maximumForce:F0}");
            sb.AppendLine($"       angYZDrive: spring={angYZ.positionSpring:F0} damper={angYZ.positionDamper:F1} maxF={angYZ.maximumForce:F0}");
            if (slerp.positionSpring > 0 || slerp.positionDamper > 0)
                sb.AppendLine($"       slerpDrive: spring={slerp.positionSpring:F0} damper={slerp.positionDamper:F1}");
            if (xd.positionSpring > 0 || yd.positionSpring > 0 || zd.positionSpring > 0)
                sb.AppendLine($"       xyzDrive: x_spring={xd.positionSpring:F0} y={yd.positionSpring:F0} z={zd.positionSpring:F0}");

            // connectedBody
            sb.AppendLine($"       connected={j.connectedBody?.name ?? "WORLD"} | isKinematic={j.GetComponent<Rigidbody>()?.isKinematic} gravity={j.GetComponent<Rigidbody>()?.useGravity}");
        }

        // 最後にAPR_Root自体のRigidbody状態
        sb.AppendLine($"\n  APR_Root Rigidbody: pos={rootRb.position} vel={rootRb.linearVelocity} isKin={rootRb.isKinematic} gravity={rootRb.useGravity}");

        Debug.Log("[JointInspect] " + sb.ToString());
        return sb.ToString();
    }
}
