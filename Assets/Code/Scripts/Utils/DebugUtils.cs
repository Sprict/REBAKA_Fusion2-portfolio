using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Utils
{
    public static class DebugUtils
    {
        public static void DrawRagdollDebug(Transform root, float balanceHeight, Transform centerOfMass, bool useStepPrediction)
        {
            // バランス高さの表示
            Debug.DrawRay(root.position, -root.up * balanceHeight, Color.green);

            // 重心点の表示（ステップ予測が有効な場合）
            if (useStepPrediction && centerOfMass != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(centerOfMass.position, 0.3f);
            }
        }

        public static void LogRagdollState(string message, Object context = null)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log($"[Ragdoll] {message}", context);
#endif
        }

        public static void LogRagdollWarning(string message, Object context = null)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.LogWarning($"[Ragdoll] {message}", context);
#endif
        }

        public static void LogRagdollError(string message, Object context = null)
        {
            Debug.LogError($"[Ragdoll] {message}", context);
        }
    }
}
