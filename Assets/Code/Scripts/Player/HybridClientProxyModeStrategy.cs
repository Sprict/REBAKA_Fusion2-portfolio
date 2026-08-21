using System;

namespace Rebaka.Player
{
    /// <summary>
    /// Hybrid モードのクライアント初期化戦略。
    /// Spawn 時にプロキシ表示をいったん隠し、JointDrive は維持したまま、
    /// Root の同期手段が利用可能かを記録・検査する。
    /// </summary>
    internal sealed class HybridClientProxyModeStrategy : IClientProxyModeStrategy
    {
        private readonly IClientProxyRigAccess _rigAccess;
        private readonly IClientBootstrapContext _context;

        public HybridClientProxyModeStrategy(IClientProxyRigAccess rigAccess, IClientBootstrapContext context)
        {
            _rigAccess = rigAccess ?? throw new ArgumentNullException(nameof(rigAccess));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Apply()
        {
            _rigAccess.SetProxyVisualsEnabled(false);
            _context.LogClientBootstrap(
                "client_joint_mode",
                "role=Client phase=spawn joint_drives_preserved=true",
                0.2f,
                $"client_joint_mode_preserve_{_context.InstanceId}");

            _context.LogClientBootstrap(
                "client_root_sync_mode",
                $"role=Client phase=spawn root_network_rigidbody={_rigAccess.HasRootNetworkRigidbody} " +
                $"legacy_custom_root_correction={_rigAccess.UseLegacyCustomRootCorrection}",
                0.2f,
                $"client_root_sync_mode_{_context.InstanceId}");

            if (!_rigAccess.HasRootNetworkRigidbody && !_rigAccess.UseLegacyCustomRootCorrection)
            {
                _context.LogClientWarning(
                    "[RagdollController] APR_Root に NetworkRigidbody が見つかりません。独自補正OFF設定ではクライアント移動品質が低下します。");
            }
        }
    }
}
