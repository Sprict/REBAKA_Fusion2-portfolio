using System;
using System.Collections.Generic;
using MyFolder.Scripts.Network;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollClientProxyRuntime
    {
        private readonly IClientProxyRuntimeContext _context;
        private ClientProxyCorrection _correction;
        private RagdollSnapshotPoseInterpolator _snapshotInterpolator;

        public RagdollClientProxyRuntime(IClientProxyRuntimeContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Initialize()
        {
            switch (_context.SyncMode)
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
            _context.EmitSyncDiagnostics("fixed");

            // Forecast Physicsモード: kinematic化せず、フル物理計算を実行
            // 補正はNetworkRigidbody3D（Fusionフレームワーク）に任せる
            if (_context.SyncMode == ProxySyncMode.Forecast)
            {
                RunForecastPhysics();
                return;
            }

            // SnapshotInterpolation モード: tick ドメインでは物理状態の維持のみ。
            // 描画は RunRender() の純粋な視覚補間が担う。
            if (_context.SyncMode == ProxySyncMode.SnapshotInterpolation)
            {
                EnforceSnapshotPhysicsState();
                return;
            }

            if (!_context.UseHybridProxySimulation)
            {
                ForceClientKinematic();
                return;
            }

            EnsureCorrection();
            if (_correction == null || !_correction.EnsureBootstrap())
            {
                return;
            }

            _context.ProxyBootstrapApplied = true;
            UpdateVisualProxyPhysics();
            _correction.ApplyCorrection(_context.DeltaTime);
        }

        public void RunRender()
        {
            _context.EmitSyncDiagnostics("render");

            // Forecast Physicsモード: kinematic化不要
            if (_context.SyncMode == ProxySyncMode.Forecast) return;

            // SnapshotInterpolation モード: 毎描画フレーム、スナップショット間を補間して transform を更新
            if (_context.SyncMode == ProxySyncMode.SnapshotInterpolation)
            {
                EnsureSnapshotInterpolator();
                _snapshotInterpolator?.RunRender();
                return;
            }

            if (!_context.UseHybridProxySimulation)
            {
                ForceClientKinematic();
            }
        }

        public int RunBeforeTick()
        {
            // Forecast Physicsモード: kinematic化しない
            if (_context.SyncMode == ProxySyncMode.Forecast) return 0;

            // SnapshotInterpolation モード: resimulation 前にも物理状態を保証する
            if (_context.SyncMode == ProxySyncMode.SnapshotInterpolation)
            {
                return EnforceSnapshotPhysicsState();
            }

            if (_context.UseHybridProxySimulation)
            {
                return 0;
            }

            return ForceClientKinematic();
        }

        private void EnsureSnapshotInterpolator()
        {
            if (_snapshotInterpolator != null)
            {
                return;
            }

            _snapshotInterpolator = _context.CreateSnapshotPoseInterpolator();
        }

        /// <summary>
        /// Forecast Physicsモード:
        /// クライアントでもフル物理計算を実行する。
        /// kinematic化も補正もせず、同一入力から同一モーター計算を行い、
        /// 姿勢の自然な収束に任せる。NetworkRigidbody3Dが差分を補正する。
        /// </summary>
        private void RunForecastPhysics()
        {
            if (_context.PhysicsHandler == null) return;

            RagdollCommand command = BuildProxyCommandFromNetworkState();

            // 入力権を持つクライアントはローカル入力を使用
            if (_context.HasInputAuthority &&
                _context.TryGetInput(out NetworkInputData localInput) &&
                _context.InputHandler != null)
            {
                _context.InputHandler.ProcessInput(localInput);
                command = _context.InputHandler.CurrentCommand;
            }

            // フル物理計算（HasAuthoritativePhysics()がtrueを返すため、力もAddForceも適用される）
            _context.PhysicsHandler.UpdatePhysics(
                _context.CurrentState,
                command,
                _context.DeltaTime);
        }

        private void EnsureCorrection()
        {
            if (_correction != null)
            {
                return;
            }

            _correction = _context.CreateClientProxyCorrection();
            if (_correction != null)
            {
                _correction.SetInitialState(_context.ProxyBootstrapApplied);
            }
        }

        private int ForceClientKinematic()
        {
            Rigidbody[] rigidbodies = _context.KinematicTargetRigidbodies;
            if (rigidbodies == null)
            {
                return 0;
            }

            int forcedCount = 0;
            foreach (Rigidbody rb in rigidbodies)
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

            return forcedCount;
        }

        private HashSet<Rigidbody> _poseDrivenSet;

        /// <summary>
        /// SnapshotInterpolation モードの物理状態を維持する:
        /// - ポーズ同期対象の15パーツ: kinematic（Render 補間が transform を書く）
        /// - それ以外（装飾用 Sphere 等）: dynamic + 重力あり
        ///   ジョイントで kinematic な本体に繋がっているため、補間で動く本体に
        ///   追従して揺れる（ホストと同等の二次運動を帯域ゼロでローカル再現）
        /// </summary>
        private int EnforceSnapshotPhysicsState()
        {
            Rigidbody[] poseDriven = _context.PoseDrivenRigidbodies;
            Rigidbody[] all = _context.KinematicTargetRigidbodies;
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
            if (_context.PhysicsHandler == null)
            {
                return;
            }

            RagdollCommand command = BuildProxyCommandFromNetworkState();

            if (_context.HasInputAuthority &&
                _context.TryGetInput(out NetworkInputData localInput) &&
                _context.InputHandler != null)
            {
                _context.InputHandler.ProcessInput(localInput);
                command = _context.InputHandler.CurrentCommand;

                if (command.MoveDirection.sqrMagnitude < 0.0001f)
                {
                    command.MoveDirection = _context.MoveDirection;
                }

                if (command.FacingDirection.sqrMagnitude < 0.0001f)
                {
                    command.FacingDirection = _context.FacingDirection;
                }

                if (command.LookDirection == Vector2.zero)
                {
                    command.LookDirection = _context.LookDirection;
                }
            }

            _context.PhysicsHandler.UpdatePhysicsVisualOnly(
                _context.CurrentState,
                command,
                _context.DeltaTime);
        }

        private RagdollCommand BuildProxyCommandFromNetworkState()
        {
            Transform fallback = _context.ProxyFacingFallbackTransform;
            Vector3 fallbackForward = fallback != null ? fallback.forward : Vector3.forward;
            Vector3 facing = _context.FacingDirection.sqrMagnitude > 0.0001f
                ? _context.FacingDirection
                : fallbackForward;

            bool isPunching = _context.CurrentState == PlayerState.Punching;
            bool isReaching = _context.CurrentState == PlayerState.Reaching;

            return new RagdollCommand
            {
                MoveDirection = _context.MoveDirection,
                FacingDirection = facing,
                LookDirection = _context.LookDirection,
                BodyRoll = _context.BodyRoll,
                IsJumping = _context.CurrentState == PlayerState.Jumping,
                IsGrabbingLeft = isReaching,
                IsGrabbingRight = isReaching,
                IsPunchingLeft = isPunching,
                IsPunchingRight = isPunching
            };
        }
    }
}
