using System;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// クライアント側でラグドールの初期化方針を決めるクラス
    /// 入力権限と状態権限、リモート描画設定をもとに、
    /// どのクライアントプロキシ戦略を使うかを決定して適用する。
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
        /// クライアント側の描画モードとプロキシ戦略を初期化する。
        /// </summary>
        public void Initialize()
        {
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

            _context.CreateClientProxyModeStrategy().Apply();
        }
    }
}
