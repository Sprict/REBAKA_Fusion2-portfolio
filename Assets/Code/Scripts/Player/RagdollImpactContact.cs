using MyFolder.Scripts.Player;
using MyFolder.Scripts.Utils;
using UnityEngine;

/// <summary>
/// プレイヤーの衝撃を検出し、ノックアウト状態やサウンド再生を制御するコンポーネント
/// </summary>
public class RagdollImpactContact : MonoBehaviour
{

    #region Collision Methods

    private void OnCollisionEnter(Collision collision)
    {
        // controllerがnullの場合は処理をスキップ
        // （階層構造変更後やStart前に呼ばれる可能性がある）
        if (controller == null) return;

        // Fusionの再シミュレーション中はスキップ
        // 再シミュレーションでは同一衝突が複数Tickにわたって検知され、
        // 累計でknockoutForceを超えてしまうのを防ぐ
        if (controller.Runner != null && controller.Runner.IsResimulation) return;

        // Gang Beasts方式: 衝突判定はホスト（StateAuthority）のみ実行
        // クライアントでも衝突コールバックは発火するが、[Networked]プロパティへの
        // ローカル書き込みはホスト状態と競合して振動を引き起こすためガードする
        if (!controller.Object.HasStateAuthority) return;

        // 相対速度の大きさを取得
        float impactForce = collision.relativeVelocity.magnitude;

        // 強い衝撃でノックアウト状態にする
        if (impactForce > knockoutForce && controller.CurrentState != PlayerState.Ragdoll)
        {
            // 状態をRagdollに変更（次のFixedUpdateNetworkで適用される）
            controller.CurrentState = PlayerState.Ragdoll;

            // ヒット音を再生
            (controller as IRagdollAudioSink)?.PlayHitSound();

            DebugUtils.LogRagdollState($"衝撃（{impactForce}）によりノックアウト状態になりました", this);
        }
        // 中程度の衝撃でサウンドのみ再生
        else if (impactForce > minImpactForce)
        {
            // 衝撃音を再生
            (controller as IRagdollAudioSink)?.PlayImpactSound();

            DebugUtils.LogRagdollState($"衝撃（{impactForce:F2}）を検知: 相手={collision.gameObject.name}", this);
        }
    }

    #endregion

    #region Serialized Fields

    [SerializeField] private RagdollController controller;

    [Tooltip("ノックアウトに必要な衝撃の強さ")] [SerializeField]
    private float knockoutForce = 15f;

    [Tooltip("サウンドを再生する最小の衝撃")] [SerializeField]
    private float minImpactForce = 5f;

    #endregion
}
