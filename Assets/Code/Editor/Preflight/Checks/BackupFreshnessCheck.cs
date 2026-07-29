namespace MyFolder.Editor.Preflight
{
    /// <summary>
    /// チェック#5: NetworkProjectConfigBackup.json（MPPM用）の鮮度。
    /// stale 判定は BakeFusionConfig.ShouldBakeForMppm を再利用（そちらでテスト済み）。
    /// Play Mode 開始時に自動ベイクされるため Fail ではなく Warning。
    /// </summary>
    public sealed class BackupFreshnessCheck : IPreflightCheck
    {
        public string Name => "MPPM Config Backup 鮮度";

        public PreflightResult Run()
        {
            bool shouldBake = BakeFusionConfig.ShouldBakeForMppm(
                BakeFusionConfig.ConfigAssetPath,
                BakeFusionConfig.OutputPath);
            return Evaluate(shouldBake);
        }

        public static PreflightResult Evaluate(bool shouldBake)
        {
            if (!shouldBake)
            {
                return PreflightResult.Pass("NetworkProjectConfigBackup.json は最新です。");
            }

            return PreflightResult.Warn(
                "backup が欠落または stale です（次の Play Mode 開始時に自動ベイクされます）。",
                "今すぐ確定するなら Tools > Fusion > Bake Config for MPPM を実行する。");
        }
    }
}
