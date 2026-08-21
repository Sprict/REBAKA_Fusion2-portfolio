using System;
using System.Collections.Generic;
using Rebaka.Network;
using UnityEngine;

namespace Rebaka.Player
{
    /// <summary>
    /// State Authority を持たない側（クライアントプロキシ）のラグドール更新を担う。
    /// ホスト側の対は <see cref="RagdollHostSimulationOrchestrator"/>。
    /// </summary>
    /// <remarks>
    /// Authority の判定は呼び出し元（<see cref="RagdollController"/> の BeforeTick /
    /// FixedUpdateNetwork / Render）が済ませている前提で、このクラス自身は再検査しない。
    /// 追従方式は <see cref="IClientProxyRuntimeContext.SyncMode"/> が決め、各モードの意味と
    /// 採否の経緯は <see cref="ProxySyncMode"/> の定義を正とする。
    /// <see cref="ProxySyncMode.Hybrid"/> では <see cref="ClientProxyCorrection"/> による
    /// 補正付きの視覚専用物理を使う。
    /// </remarks>
    internal sealed class RagdollClientProxyRuntime
    {
        private readonly IClientProxyRuntimeContext _clientProxyContext;
        private ClientProxyCorrection _clientProxyCorrection;
        private RagdollSnapshotPoseInterpolator _snapshotPoseInterpolator;
        private HashSet<Rigidbody> _poseDrivenSet;

        public RagdollClientProxyRuntime(IClientProxyRuntimeContext context)
        {
            _clientProxyContext = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Initialize()
        {
            switch (_clientProxyContext.SyncMode)
            {
                case ProxySyncMode.SnapshotInterpolation:
                    EnsureSnapshotInterpolator();
                    break;
                case ProxySyncMode.Forecast:
                    break;
                default:
                    EnsureCorrection();
                    break;
            }
        }

        public void RunFixedUpdate()
        {
            _clientProxyContext.EmitSyncDiagnostics("fixed");

            // Forecast Physicsモード: kinematic化せず、フル物理計算を実行
            // 補正はNetworkRigidbody3D（Fusionフレームワーク）に任せる
            if (_clientProxyContext.SyncMode == ProxySyncMode.Forecast)
            {
                RunForecastPhysics();
                return;
            }

            // SnapshotInterpolation モード: tick ドメインでは物理状態の維持のみ。
            // 描画は RunRender() の純粋な視覚補間が担う。
            if (_clientProxyContext.SyncMode == ProxySyncMode.SnapshotInterpolation)
            {
                EnforceSnapshotPhysicsState();
                return;
            }

            EnsureCorrection();
            if (_clientProxyCorrection == null || !_clientProxyCorrection.EnsureBootstrap())
            {
                return;
            }

            _clientProxyContext.ProxyBootstrapApplied = true;
            UpdateVisualProxyPhysics();
            _clientProxyCorrection.ApplyCorrection(_clientProxyContext.DeltaTime);
        }

        public void RunRender()
        {
            _clientProxyContext.EmitSyncDiagnostics("render");

            // Forecast Physicsモード: kinematic化不要
            if (_clientProxyContext.SyncMode == ProxySyncMode.Forecast) return;

            // SnapshotInterpolation モード: 毎描画フレーム、スナップショット間を補間して transform を更新
            if (_clientProxyContext.SyncMode == ProxySyncMode.SnapshotInterpolation)
            {
                EnsureSnapshotInterpolator();
                _snapshotPoseInterpolator?.RunRender();
            }
        }

        public int RunBeforeTick()
        {
            // Forecast Physicsモード: kinematic化しない
            if (_clientProxyContext.SyncMode == ProxySyncMode.Forecast) return 0;

            // SnapshotInterpolation モード: resimulation 前にも物理状態を保証する
            if (_clientProxyContext.SyncMode == ProxySyncMode.SnapshotInterpolation)
            {
                return EnforceSnapshotPhysicsState();
            }

            // Hybrid: ClientProxyCorrection が kinematic を管理する
            return 0;
        }

        private void EnsureSnapshotInterpolator()
        {
            if (_snapshotPoseInterpolator != null)
            {
                return;
            }

            _snapshotPoseInterpolator = _clientProxyContext.CreateSnapshotPoseInterpolator();
        }

        /// <summary>
        /// Forecast Physicsモード:
        /// クライアントでもフル物理計算を実行する。
        /// kinematic化も補正もせず、同一入力から同一モーター計算を行い、
        /// 姿勢の自然な収束に任せる。NetworkRigidbody3Dが差分を補正する。
        /// </summary>
        private void RunForecastPhysics()
        {
            if (_clientProxyContext.PhysicsHandler == null) return;

            RagdollCommand command = BuildProxyCommandFromNetworkState();

            // 入力権を持つクライアントはローカル入力を使用
            if (_clientProxyContext.HasInputAuthority &&
                _clientProxyContext.TryGetInput(out NetworkInputData localInput) &&
                _clientProxyContext.InputHandler != null)
            {
                _clientProxyContext.InputHandler.UpdateCurrentCommand(localInput);
                command = _clientProxyContext.InputHandler.CurrentCommand;
            }

            // フル物理計算（HasAuthoritativePhysics()がtrueを返すため、力もAddForceも適用される）
            _clientProxyContext.PhysicsHandler.UpdatePhysics(
                _clientProxyContext.CurrentState,
                command,
                _clientProxyContext.DeltaTime);
        }

        private void EnsureCorrection()
        {
            if (_clientProxyCorrection != null)
            {
                return;
            }

            _clientProxyCorrection = _clientProxyContext.CreateClientProxyCorrection();
            if (_clientProxyCorrection != null)
            {
                _clientProxyCorrection.SetInitialState(_clientProxyContext.ProxyBootstrapApplied);
            }
        }

        /// <summary>
        /// SnapshotInterpolation モードの物理状態を維持する:
        /// - ポーズ同期対象の15パーツ: kinematic（Render 補間が transform を書く）
        /// - それ以外（装飾用 Sphere 等）: dynamic + 重力あり
        ///   ジョイントで kinematic な本体に繋がっているため、補間で動く本体に
        ///   追従して揺れる（ホストと同等の二次運動を帯域ゼロでローカル再現）
        /// </summary>
        private int EnforceSnapshotPhysicsState()
        {
            Rigidbody[] poseDriven = _clientProxyContext.PoseDrivenRigidbodies;
            Rigidbody[] all = _clientProxyContext.KinematicTargetRigidbodies;
            if (poseDriven == null)
            {
                return 0;
            }

            if (_poseDrivenSet == null)
            {
                _poseDrivenSet = new HashSet<Rigidbody>(poseDriven);
            }

            int forcedCount = 0;
            foreach (Rigidbody rb in poseDriven)
            {
                if (rb == null)
                {
                    continue;
                }

                if (!rb.isKinematic)
                {
                    forcedCount++;
                }

                rb.isKinematic = true;
                if (rb.useGravity)
                {
                    rb.useGravity = false;
                }
            }

            if (all == null)
            {
                return forcedCount;
            }

            foreach (Rigidbody rb in all)
            {
                if (rb == null || _poseDrivenSet.Contains(rb))
                {
                    continue;
                }

                if (rb.isKinematic)
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.WakeUp();
                }
            }

            return forcedCount;
        }

        private void UpdateVisualProxyPhysics()
        {
            if (_clientProxyContext.PhysicsHandler == null)
            {
                return;
            }

            RagdollCommand command = BuildProxyCommandFromNetworkState();

            if (_clientProxyContext.HasInputAuthority &&
                _clientProxyContext.TryGetInput(out NetworkInputData localInput) &&
                _clientProxyContext.InputHandler != null)
            {
                _clientProxyContext.InputHandler.UpdateCurrentCommand(localInput);
                command = _clientProxyContext.InputHandler.CurrentCommand;

                if (command.MoveDirection.sqrMagnitude < 0.0001f)
                {
                    command.MoveDirection = _clientProxyContext.MoveDirection;
                }

                if (command.FacingDirection.sqrMagnitude < 0.0001f)
                {
                    command.FacingDirection = _clientProxyContext.FacingDirection;
                }

                if (command.LookDirection == Vector2.zero)
                {
                    command.LookDirection = _clientProxyContext.LookDirection;
                }
            }

            _clientProxyContext.PhysicsHandler.UpdatePhysicsVisualOnly(
                _clientProxyContext.CurrentState,
                command,
                _clientProxyContext.DeltaTime);
        }

        private RagdollCommand BuildProxyCommandFromNetworkState()
        {
            Transform fallback = _clientProxyContext.ProxyFacingFallbackTransform;
            Vector3 fallbackForward = fallback != null ? fallback.forward : Vector3.forward;
            Vector3 facing = _clientProxyContext.FacingDirection.sqrMagnitude > 0.0001f
                ? _clientProxyContext.FacingDirection
                : fallbackForward;

            bool isPunching = _clientProxyContext.CurrentState == PlayerState.Punching;
            bool isReaching = _clientProxyContext.CurrentState == PlayerState.Reaching;

            return new RagdollCommand
            {
                MoveDirection = _clientProxyContext.MoveDirection,
                FacingDirection = facing,
                LookDirection = _clientProxyContext.LookDirection,
                BodyRoll = _clientProxyContext.BodyRoll,
                IsJumping = _clientProxyContext.CurrentState == PlayerState.Jumping,
                IsGrabbingLeft = isReaching,
                IsGrabbingRight = isReaching,
                IsPunchingLeft = isPunching,
                IsPunchingRight = isPunching
            };
        }
    }
}
