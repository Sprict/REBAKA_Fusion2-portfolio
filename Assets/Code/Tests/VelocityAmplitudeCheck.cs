using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// 各軸の速度振幅を測定する。微小振動かどうかを判定する。
/// </summary>
public static class VelocityAmplitudeCheck
{
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

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
        if (rootRb == null) return "ERROR: APR_Root not found.";

        var vel = rootRb.linearVelocity;
        var angVel = rootRb.angularVelocity;
        var pos = rootRb.position;

        return $"pos={pos} vel={vel} |vel|={vel.magnitude:F4} angVel={angVel} |angVel|={angVel.magnitude:F4}";
    }
}
