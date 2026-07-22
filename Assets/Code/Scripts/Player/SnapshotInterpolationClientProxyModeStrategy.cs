using System;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// SnapshotInterpolation モードのクライアント初期化戦略。
    ///
    /// - 初の有効スナップショット受信まで visuals を非表示（Tポーズ/原点フラッシュ防止）
    /// - Root の NetworkRigidbody を無効化（Render の二重書き込み防止。
    ///   本モードでは RagdollSnapshotPoseInterpolator が唯一の transform 書き込み源になる）
    ///
    /// ジョイントドライブは無効化しない: ポーズ同期対象の15パーツは kinematic のため
    /// ドライブは元々無効果であり、装飾用 Sphere 等（dynamic のまま残す）は
    /// ジョイントのスプリングで本体に追従して揺れる必要がある。
    /// </summary>
    internal sealed class SnapshotInterpolationClientProxyModeStrategy : IClientProxyModeStrategy
    {
        private readonly IClientProxyRigAccess _rigAccess;
        private readonly IClientBootstrapContext _context;

        public SnapshotInterpolationClientProxyModeStrategy(
            IClientProxyRigAccess rigAccess, IClientBootstrapContext context)
        {
            _rigAccess = rigAccess ?? throw new ArgumentNullException(nameof(rigAccess));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Apply()
        {
            _rigAccess.SetProxyVisualsEnabled(false);
            _rigAccess.DisableRootNetworkRigidbody();

            _context.LogClientBootstrap(
                "client_joint_mode",
                "role=Client phase=spawn mode=snapshot_interpolation " +
                "joint_drives_preserved=true root_network_rigidbody_disabled=true",
                0.2f,
                $"client_joint_mode_snapshot_{_context.InstanceId}");

            _context.LogClientDebug(
                "[RAGDOLL_CLIENT_MODE] mode=snapshot_interpolation " +
                $"inputAuthority={_context.HasInputAuthority}");
        }
    }
}
