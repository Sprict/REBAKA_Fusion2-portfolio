using System;
using Fusion;
using MyFolder.Scripts.Diagnostics;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// ラグドール同期とルートポーズ補正に関する診断情報を出力します。
    /// </summary>
    internal sealed class RagdollDiagnosticsReporter
    {
        private float _nextDiagnosticsAt;

        public RagdollDiagnosticsReporter()
        {
        }

        public void EmitSyncDiagnostics(
            string phase,
            NetworkBehaviour behaviour,
            NetworkRunner runner,
            NetworkObject networkObject,
            Rigidbody[] rigidbodies,
            bool useHybridProxySimulation,
            bool proxyBootstrapApplied,
            bool enableRootPosePrediction,
            bool hasRootNetworkRigidbody,
            bool useLegacyCustomRootCorrection)
        {
            if (!RagdollNetDiagnostics.IsEnabled)
                return;
            if (runner == null || networkObject == null || rigidbodies == null)
                return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextDiagnosticsAt)
                return;
            _nextDiagnosticsAt = now + 0.2f;

            int total = 0;
            int nonKinematic = 0;
            int useGravityCount = 0;
            int sphereNonKinematic = 0;

            for (int i = 0; i < rigidbodies.Length; i++)
            {
                Rigidbody rb = rigidbodies[i];
                if (rb == null)
                    continue;

                total++;
                if (!rb.isKinematic)
                {
                    nonKinematic++;
                    if (rb.gameObject.name.StartsWith("Sphere", StringComparison.Ordinal))
                    {
                        sphereNonKinematic++;
                    }
                }

                if (rb.useGravity)
                {
                    useGravityCount++;
                }
            }

            string role = networkObject.HasStateAuthority ? "Host" : "Client";
            float tickEstimate = runner.DeltaTime > 0f ? Time.timeSinceLevelLoad / runner.DeltaTime : 0f;
            RenderTimeframe renderTimeframe = networkObject.RenderTimeframe;
            RenderSource renderSource = networkObject.RenderSource;
            bool forceRemote = networkObject.ForceRemoteRenderTimeframe;

            RagdollNetDiagnostics.Log(
                "ragdoll_sync",
                $"role={role} phase={phase} tick_est={tickEstimate:F1} rb_total={total} " +
                $"rb_non_kinematic={nonKinematic} sphere_non_kinematic={sphereNonKinematic} " +
                $"use_gravity_count={useGravityCount} stateAuthority={networkObject.HasStateAuthority} " +
                $"inputAuthority={networkObject.HasInputAuthority} runner_is_resim={runner.IsResimulation} " +
                $"render_timeframe={renderTimeframe} render_source={renderSource} force_remote={forceRemote} " +
                $"hybrid_mode={useHybridProxySimulation} proxy_bootstrap={proxyBootstrapApplied} " +
                $"root_prediction={enableRootPosePrediction} " +
                $"root_network_rigidbody={hasRootNetworkRigidbody} " +
                $"legacy_custom_root_correction={useLegacyCustomRootCorrection}",
                behaviour);

            if (!networkObject.HasStateAuthority && !useHybridProxySimulation && nonKinematic > 0)
            {
                RagdollNetDiagnostics.LogKinematicLeak(
                    $"role={role} phase={phase} rb_non_kinematic={nonKinematic} " +
                    $"sphere_non_kinematic={sphereNonKinematic}",
                    behaviour,
                    0.2f,
                    $"kinematic_leak_{behaviour.GetInstanceID()}");
            }
        }

        public void RecordCsvSample(
            NetworkRunner runner,
            PlayerState currentState,
            Vector3 moveDirection,
            bool isPlayerGrounded,
            string role,
            float rootError,
            float correctionMag,
            int snapCount,
            bool snapThisTick,
            Vector3 actualRootPos,
            Vector3 actualRootVel,
            Vector3 netRootPos,
            Vector3 netRootVel)
        {
            if (!RagdollCsvProfiler.IsProfilingEnabled || runner == null)
                return;

            RagdollCsvProfiler.Record(new RagdollProfileSample
            {
                time = Time.realtimeSinceStartup,
                tick = runner.Tick.Raw,
                role = role,
                state = currentState.ToString(),
                isResim = runner.IsResimulation,
                rootPosX = actualRootPos.x,
                rootPosY = actualRootPos.y,
                rootPosZ = actualRootPos.z,
                rootVelX = actualRootVel.x,
                rootVelY = actualRootVel.y,
                rootVelZ = actualRootVel.z,
                netRootPosX = netRootPos.x,
                netRootPosY = netRootPos.y,
                netRootPosZ = netRootPos.z,
                netRootVelX = netRootVel.x,
                netRootVelY = netRootVel.y,
                netRootVelZ = netRootVel.z,
                rootError = rootError,
                correctionMag = correctionMag,
                snapCount = snapCount,
                snapThisTick = snapThisTick,
                moveInputMag = moveDirection.magnitude,
                isGrounded = isPlayerGrounded
            });
        }
    }
}
