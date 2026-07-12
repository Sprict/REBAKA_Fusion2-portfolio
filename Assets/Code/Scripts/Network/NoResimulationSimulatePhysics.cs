using Fusion.Addons.Physics;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// resimulation 中は Physics.Simulate を実行しない RunnerSimulatePhysics。
    /// 同梱アドオンには resim ガードがなく、クライアントの入力予測（1フレーム複数tick再実行）で
    /// 装飾 RB のローカル物理が resim 回数分不規則に加速しジャダー化する障害の対策
    /// （経緯は Docs/devlogs/2026-06-10_client_snapshot_interpolation.md 参照）。
    ///
    /// アドオン側のファイルは編集せず、継承で挙動を差し替える。
    /// NetworkRigidbody.SetupPhysicsBody の自動追加は基底型の TryGetComponent で
    /// 既存チェックするため、本クラスが先に登録されていれば二重追加されない。
    /// </summary>
    public sealed class NoResimulationSimulatePhysics : RunnerSimulatePhysics
    {
        public override void FixedUpdateNetwork()
        {
            if (Runner.IsResimulation)
            {
                return;
            }

            base.FixedUpdateNetwork();
        }
    }
}
