namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// Forecast Physicsモード用のクライアントプロキシ戦略。
    /// ジョイントドライブを維持し、物理シミュレーションを全クライアントで実行する。
    /// Legacy/HybridStrategyと異なり、ドライブの無効化やkinematic化を行わない。
    /// </summary>
    internal sealed class ForecastClientProxyModeStrategy : IClientProxyModeStrategy
    {
        private readonly IClientBootstrapContext _context;

        public ForecastClientProxyModeStrategy(IClientBootstrapContext context)
        {
            _context = context;
        }

        public void Apply()
        {
            // Forecast Physicsモードではジョイントドライブを維持する。
            // クライアントでもフル物理計算を実行するため、
            // ドライブの無効化やkinematic化は行わない。
            _context.LogClientBootstrap(
                "forecast_strategy",
                "Forecast Physics mode: keeping joint drives active for full client physics",
                1f);
        }
    }
}
