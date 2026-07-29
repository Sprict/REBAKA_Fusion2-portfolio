using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// ボディパーツの Rigidbody → 所有プレイヤーの NetworkObject の対応表。
    ///
    /// なぜ必要か: APR_Root はスポーン時に DetachRootFromParent() でワールド直下へ
    /// 切り離されるため、ボディパーツから GetComponentInParent&lt;NetworkObject&gt;() を
    /// 辿っても所有者に届かない（null になる）。掴み判定などで「このパーツは誰のものか」
    /// を解決するために、スポーン時（切り離し前）に登録しておく。
    /// </summary>
    public static class RagdollBodyOwnerRegistry
    {
        private static readonly Dictionary<Rigidbody, NetworkObject> Map = new Dictionary<Rigidbody, NetworkObject>();

        /// <summary>スポーン時（階層が切り離される前）に呼ぶこと。</summary>
        public static void Register(Rigidbody[] rigidbodies, NetworkObject owner)
        {
            if (rigidbodies == null || owner == null) return;

            foreach (Rigidbody rb in rigidbodies)
            {
                if (rb != null) Map[rb] = owner;
            }
        }

        public static void Unregister(NetworkObject owner)
        {
            if (owner == null) return;

            var toRemove = new List<Rigidbody>();
            foreach (var pair in Map)
            {
                if (pair.Value == owner || pair.Key == null) toRemove.Add(pair.Key);
            }

            foreach (Rigidbody rb in toRemove) Map.Remove(rb);
        }

        /// <summary>パーツの所有者を返す。未登録なら null。</summary>
        public static NetworkObject GetOwner(Rigidbody rb)
        {
            if (rb == null) return null;
            return Map.TryGetValue(rb, out NetworkObject owner) ? owner : null;
        }
    }
}
