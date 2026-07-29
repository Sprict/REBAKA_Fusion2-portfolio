using Fusion;
using MyFolder.Scripts.Diagnostics;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// Ensures non-authority ragdolls stay kinematic before each simulation tick.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ClientRagdollKinematicGuard : NetworkBehaviour, IBeforeTick
    {
        [SerializeField] private RagdollController controller;

        private Rigidbody[] _cachedRigidbodies;

        public override void Spawned()
        {
            if (controller == null)
            {
                controller = GetComponent<RagdollController>();
            }

            CacheRigidbodies();
        }

        void IBeforeTick.BeforeTick()
        {
            if (Object == null || Runner == null || Object.HasStateAuthority)
                return;

            Rigidbody[] targets = null;
            if (controller != null)
            {
                ProxySyncMode mode = controller.ResolvedProxySyncMode;

                // Forecast: クライアントも物理計算するため kinematic 化しない
                if (mode == ProxySyncMode.Forecast)
                    return;

                // Hybrid: ClientProxyCorrection が独自に kinematic を管理する（Root のみ kinematic、
                // 四肢は dynamic）ため、ここで全 RB を強制すると Hybrid の表示が壊れる
                if (mode != ProxySyncMode.SnapshotInterpolation && controller.UseHybridProxySimulation)
                    return;

                // SnapshotInterpolation: ポーズ同期対象の15パーツのみ enforcement。
                // 装飾用 Sphere 等はローカル物理（ジョイント駆動の揺れ）を残すため対象外
                if (mode == ProxySyncMode.SnapshotInterpolation)
                {
                    targets = controller.BodyRigidbodies;
                }

                // Legacy: 全 RB の kinematic を enforcement する
            }

            if (targets == null)
            {
                if (_cachedRigidbodies == null || _cachedRigidbodies.Length == 0)
                {
                    CacheRigidbodies();
                }

                targets = _cachedRigidbodies;
            }

            if (targets == null)
                return;

            var forcedCount = 0;
            for (var i = 0; i < targets.Length; i++)
            {
                var rb = targets[i];
                if (rb == null)
                    continue;

                if (!rb.isKinematic)
                {
                    rb.isKinematic = true;
                    forcedCount++;
                }

                if (rb.useGravity)
                {
                    rb.useGravity = false;
                }
            }

            if (forcedCount > 0)
            {
                RagdollNetDiagnostics.Log(
                    "pre_sim_kinematic",
                    $"role=Client phase=before_tick forced_count={forcedCount} runner_is_resim={Runner.IsResimulation}",
                    this,
                    0.2f,
                    $"pre_sim_guard_{GetInstanceID()}");
            }
        }

        private void CacheRigidbodies()
        {
            if (controller != null)
            {
                _cachedRigidbodies = controller.GetComponentsInChildren<Rigidbody>(true);
                return;
            }

            _cachedRigidbodies = GetComponentsInChildren<Rigidbody>(true);
        }
    }
}
