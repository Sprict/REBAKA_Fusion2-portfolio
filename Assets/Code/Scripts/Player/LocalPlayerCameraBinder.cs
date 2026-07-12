using OrbitCamera = MyFolder.Scripts.Camera.OrbitCamera;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    [DisallowMultipleComponent]
    public sealed class LocalPlayerCameraBinder : MonoBehaviour
    {
        [SerializeField] private UnityEngine.Camera targetCamera;

        /// <summary>
        /// ローカルプレイヤー（自分が入力権限を持つプレイヤー）の Transform / 向き情報への参照。
        /// <see cref="Bind"/> 実行時に設定される。実体は RagdollController。
        /// </summary>
        // Runner にぶら下がりシーンをまたいで常駐する InputCollector が、
        // シーンスポーン時に生成される一時的なプレイヤーオブジェクトの
        // 両手持ち状態・体の向きを参照するために、static な置き場として公開している。
        //
        // 破棄済みかどうかの判定は LocalPlayerViewUtil.IsDestroyedOrMissing() を使うこと
        // （素の null 比較では Unity 側の Destroy を検知できないため）。
        public static ILocalPlayerViewSource LocalView { get; private set; }

        public static void EnsureAndBind(GameObject owner, ILocalPlayerViewSource source)
        {
            if (owner == null || source == null || !source.HasInputAuthority)
                return;

            var binder = owner.GetComponent<LocalPlayerCameraBinder>();
            if (binder == null)
            {
                binder = owner.AddComponent<LocalPlayerCameraBinder>();
            }

            binder.Bind(source);
        }

        public void Bind(ILocalPlayerViewSource source)
        {
            if (source == null || !source.HasInputAuthority)
                return;

            UnityEngine.Camera cam = targetCamera != null ? targetCamera : UnityEngine.Camera.main;
            if (cam == null)
                return;

            OrbitCamera orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null)
                return;

            LocalView = source;

            Transform target = source.CenterOfMassPoint != null ? source.CenterOfMassPoint : source.Transform;
            orbit.SetTarget(target, source);
            Debug.Log("OrbitCamera target set to local Player", this);
        }
    }
}
