using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// ローカルプレイヤー向けに、ラグドールの安定状態を GUI と Gizmo で可視化する補助クラスです。
    /// </summary>
    internal sealed class RagdollDebugView
    {
        internal static bool ResolveGuiVisibility(bool currentVisible, bool togglePressedThisFrame)
        {
            return togglePressedThisFrame ? !currentVisible : currentVisible;
        }

        /// <summary>
        /// 入力権限を持つプレイヤーにだけ、状態・接地・補間係数を画面左上へ表示します。
        /// </summary>
        /// <param name="networkObject">入力権限の有無を判定する対象です。権限がない場合は描画しません。</param>
        /// <param name="physics">表示元になるラグドールの物理状態です。null の場合は何も描画しません。</param>
        /// <param name="currentState">画面上に表示する現在のプレイヤー状態です。</param>
        /// <param name="leftFootGrounded">左足の接地判定結果です。</param>
        /// <param name="rightFootGrounded">右足の接地判定結果です。</param>
        public void DrawGui(
            NetworkObject networkObject,
            RagdollPhysics physics,
            PlayerState currentState,
            bool leftFootGrounded,
            bool rightFootGrounded)
        {
            if (physics == null || networkObject == null || !networkObject.HasInputAuthority)
                return;

            GUILayout.BeginArea(new Rect(10, 10, 300, 200));
            GUILayout.BeginVertical("box");

            GUILayout.Label("<b>Balance Debug</b>");
            GUILayout.Label($"State: {currentState}");
            GUILayout.Label($"BalanceState: {physics.CurrentBalanceState}");
            GUILayout.Label($"IsBalanced: {physics.IsBalanced}");
            GUILayout.Label($"IsRagdoll: {physics.IsRagdoll}");

            GUILayout.Space(5);
            GUILayout.Label("<b>Phase 2 Blending</b>");
            GUILayout.Label($"BalancePriority: {physics.CurrentBalancePriority:F2}");
            GUILayout.Label($"PoseStiffness: {physics.CurrentPoseStiffnessMultiplier:F2}");

            GUILayout.Space(5);
            GUILayout.Label($"Grounded: L={leftFootGrounded} R={rightFootGrounded}");
            GUILayout.Label($"Raycast: {physics.LastRaycastHit} FootAny: {physics.LastFootGrounded}");

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        /// <summary>
        /// 重心、支持点、重心の投影位置、安定マージンを Scene ビュー上で確認できるように描画します。
        /// </summary>
        /// <param name="physics">重心と支持点の情報を提供するラグドールの物理状態です。</param>
        /// <param name="gizmoSphereRadius">重心と支持点を示す球ギズモの半径です。</param>
        /// <param name="balanceMargin">支持点の周囲で安定とみなす許容距離です。</param>
        public void DrawGizmos(RagdollPhysics physics, float gizmoSphereRadius, float balanceMargin)
        {
            if (physics == null)
                return;

            Vector3 com = physics.CenterOfMass;
            Vector3 support = physics.SupportPolygonCenter;
            BalanceState state = physics.CurrentBalanceState;

            Color stateColor = state switch
            {
                BalanceState.Balanced => Color.green,
                BalanceState.Forward => Color.yellow,
                BalanceState.Backward => Color.cyan,
                BalanceState.Left => Color.magenta,
                BalanceState.Right => new Color(1f, 0.5f, 0f),
                _ => Color.white
            };

            Gizmos.color = Color.red;
            Gizmos.DrawSphere(com, gizmoSphereRadius);
            Gizmos.DrawWireSphere(com, gizmoSphereRadius * 1.5f);

            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(support, gizmoSphereRadius);

            Vector3 comProjected = new Vector3(com.x, support.y, com.z);
            Gizmos.color = stateColor;
            Gizmos.DrawLine(com, comProjected);
            Gizmos.DrawLine(comProjected, support);
            Gizmos.DrawWireSphere(comProjected, gizmoSphereRadius * 0.8f);

            DrawBalanceMarginCircle(support, balanceMargin, stateColor);
        }

        /// <summary>
        /// 支持点を中心に、安定判定に使う水平円を線分で近似して描画します。
        /// </summary>
        /// <param name="center">円の中心にする支持点のワールド座標です。</param>
        /// <param name="radius">安定マージンとして描画する半径です。</param>
        /// <param name="color">現在のバランス状態を示す線色です。</param>
        private static void DrawBalanceMarginCircle(Vector3 center, float radius, Color color)
        {
            Gizmos.color = color;
            const int segments = 32;
            float angleStep = 360f / segments;

            Vector3 prevPoint = center + new Vector3(radius, 0f, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float angle = i * angleStep * Mathf.Deg2Rad;
                Vector3 newPoint = center + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
                Gizmos.DrawLine(prevPoint, newPoint);
                prevPoint = newPoint;
            }
        }
    }
}
