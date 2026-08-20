using System;

namespace Rebaka.Player
{
    /// <summary>
    /// <see cref="RagdollController.Spawned"/> から非 State Authority 側で呼ばれ、
    /// NetworkObject の描画時刻と、同期モード別のクライアント用リグ設定を初期化する。
    /// Authority や同期モードそのものを変更するクラスではない。
    /// </summary>
    internal sealed class RagdollClientBootstrapper
    {
        private readonly IClientBootstrapContext _context;

        /// <summary>
        /// クライアント初期化に必要なコンテキストを受け取ります。
        /// </summary>
        public RagdollClientBootstrapper(IClientBootstrapContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// 描画に Remote 時刻を使うかを設定し、解決済みの
        /// <see cref="ProxySyncMode"/> に対応するプロキシ戦略を一度適用する。
        /// </summary>
        public void Initialize()
        {
            // 全プロキシへの強制設定、または Input Authority を持つ本人だけへの強制設定をまとめる。
            bool forceRemote = _context.ForceRemoteForAllClientProxies ||
                               (_context.HasInputAuthority && _context.ForceRemoteForInputAuthorityOnClient);

            _context.SetForceRemoteRenderTimeframe(forceRemote);
            _context.LogClientBootstrap(
                "render_mode",
                $"role=Client phase=spawn force_remote_render_timeframe={forceRemote} " +
                $"inputAuthority={_context.HasInputAuthority}",
                0.2f,
                $"render_mode_spawn_{_context.InstanceId}");

            _context.LogClientDebug(
                $"[RAGDOLL_CLIENT_MODE] force_remote={forceRemote} " +
                $"stateAuthority={_context.HasStateAuthority} inputAuthority={_context.HasInputAuthority}");

            // Strategy はクライアント側リグの初期設定を担当し、Authority の決定は行わない。
            _context.CreateClientProxyModeStrategy().Apply();
        }
    }
}
