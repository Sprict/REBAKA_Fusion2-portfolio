using UnityEngine;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollRigSetup
    {
        private const int IndexRoot = 0;
        private const int IndexHead = 2;
        private const int IndexRightHand = 13;
        private const int IndexLeftHand = 14;

        public void CacheHierarchyPhysicsComponents(
            Component owner, // プレイヤーオブジェクト
            out Rigidbody[] rigidbodies,
            out ConfigurableJoint[] joints)
        {
            rigidbodies = owner.GetComponentsInChildren<Rigidbody>(true);
            joints = owner.GetComponentsInChildren<ConfigurableJoint>(true);
        }

        public void CacheProxyBodyReferences(
            Rigidbody[] bodyRigidbodies,
            out Rigidbody rootRigidbody,
            out Rigidbody headRigidbody,
            out Rigidbody rightHandRigidbody,
            out Rigidbody leftHandRigidbody,
            out Component rootNetworkRigidbody)
        {
            rootRigidbody = TryGetBodyRigidbody(bodyRigidbodies, IndexRoot);
            headRigidbody = TryGetBodyRigidbody(bodyRigidbodies, IndexHead);
            rightHandRigidbody = TryGetBodyRigidbody(bodyRigidbodies, IndexRightHand);
            leftHandRigidbody = TryGetBodyRigidbody(bodyRigidbodies, IndexLeftHand);
            rootNetworkRigidbody = FindRootNetworkRigidbodyComponent(rootRigidbody);
        }

        public Renderer[] CacheProxyRenderers(Component owner)
        {
            return owner.GetComponentsInChildren<Renderer>(true);
        }

        /// <summary>
        /// プロキシの描画用 Renderer 群を有効化または無効化します。
        /// proxyRenderers が未設定の場合は owner から Renderer をキャッシュしてから適用します。
        /// </summary>
        /// <param name="owner">Renderer の検索元となるコンポーネント。</param>
        /// <param name="proxyRenderers">プロキシ描画用の Renderer 配列。null または空の場合はキャッシュされます。</param>
        /// <param name="enabled">有効化する場合は true、無効化する場合は false。</param>
        public void SetProxyVisualsEnabled(Component owner, ref Renderer[] proxyRenderers, bool enabled)
        {
            if (proxyRenderers == null || proxyRenderers.Length == 0)
            {
                proxyRenderers = CacheProxyRenderers(owner);
            }

            if (proxyRenderers == null)
            {
                return;
            }

            for (int i = 0; i < proxyRenderers.Length; i++)
            {
                Renderer renderer = proxyRenderers[i];
                if (renderer == null)
                {
                    continue;
                }

                renderer.enabled = enabled;
            }
        }

        public void DetachRootFromParent(Rigidbody[] bodyRigidbodies)
        {
            Rigidbody rootRigidbody = TryGetBodyRigidbody(bodyRigidbodies, IndexRoot);
            if (rootRigidbody == null)
            {
                return;
            }

            Transform rootTransform = rootRigidbody.transform;
            if (rootTransform.parent != null)
            {
                Debug.Log("Detaching APR_Root from parent to allow independent physics movement");
                rootTransform.SetParent(null, true);
            }
        }

        public Rigidbody TryGetBodyRigidbody(Rigidbody[] bodyRigidbodies, int index)
        {
            if (bodyRigidbodies == null || index < 0 || index >= bodyRigidbodies.Length)
            {
                return null;
            }

            return bodyRigidbodies[index];
        }

        private static Component FindRootNetworkRigidbodyComponent(Rigidbody rootRigidbody)
        {
            if (rootRigidbody == null)
            {
                return null;
            }

            MonoBehaviour[] behaviours = rootRigidbody.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                if (behaviour.GetType().FullName == "Fusion.Addons.Physics.NetworkRigidbody")
                {
                    return behaviour;
                }
            }

            return null;
        }
    }
}
