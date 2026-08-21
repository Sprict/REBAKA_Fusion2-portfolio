namespace Rebaka.Player
{
    /// <summary>
    /// 選択済みのプロキシ同期モードに合わせて、クライアント側リグへ
    /// Spawn 時の表示・Joint・Root 同期設定を適用するための共通契約。
    /// </summary>
    internal interface IClientProxyModeStrategy
    {
        /// <summary>
        /// この戦略が担当するクライアント初期設定を一度適用する。
        /// </summary>
        void Apply();
    }
}
