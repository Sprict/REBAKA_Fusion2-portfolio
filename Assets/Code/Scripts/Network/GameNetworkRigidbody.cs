using Fusion.Addons.Physics;
using UnityEngine;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// プロキシ（非ホスト）側を kinematic 固定にし、ホストのスナップショット補間だけで
    /// 動かすための NetworkRigidbody 派生クラス。プレイヤーの SnapshotInterpolation と
    /// 同じ原理に揃え、ローカル物理で先行する Cube とプレイヤーの間の位置ずれを防ぐ。
    /// 経緯・実測データは Docs/devlogs/2026-06-18_peer_sync_pure_interpolation.md 参照。
    /// </summary>
    public class GameNetworkRigidbody : NetworkRigidbody
    {
        private Rigidbody _rb;

        public override void Spawned()
        {
            base.Spawned();

            _rb = GetComponent<Rigidbody>();

            if (_rb != null && !HasStateAuthority)
            {
                _rb.isKinematic = true;

                // 静止オブジェクトは基底 Render() が sleep 閾値以下で early-return し
                // （NetworkRigidbody.cs:534-536）、CopyToEngine が呼ばれず prefab 位置に
                // 取り残される。forceAwake=true を明示し、スポーン時点でホストの位置・回転へ即同期する。
                CopyToEngine(true);
            }

            // クライアントは既定でこのオブジェクトを予測するが、kinematic のため補間差分が出ず
            // カクつく／プレイヤーより1tick先行して見える。プレイヤーと同じ Remote タイムフレームに
            // 固定して揃える（見た目のみに影響、接触解決の正解は常にホストの dynamic 物理）。
            if (!HasStateAuthority && Object != null)
            {
                Object.ForceRemoteRenderTimeframe = true;
            }
        }

        protected override void CopyToEngine(bool forceAwake = false)
        {
            base.CopyToEngine(forceAwake);

            // 基底が毎回上書きするホストの isKinematic（= false）をプロキシ側で戻す
            // （NetworkRigidbody.cs:349-351）。
            if (_rb != null && !HasStateAuthority)
            {
                _rb.isKinematic = true;
            }
        }
    }
}
