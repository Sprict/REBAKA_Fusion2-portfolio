// RagdollNetworkSetup.cs
// 一回限りのEditorユーティリティ: newAPRPlayer.prefabの全APR_*RigidbodyにNetworkRigidbodyを追加する
// 実行後: Prefabを開き NetworkObject の「Rebuild Object Table」をクリックすること

#if UNITY_EDITOR
using System.Linq;
using Fusion;
using Fusion.Addons.Physics;
using UnityEditor;
using UnityEngine;

namespace MyFolder.Editor
{
    public static class RagdollNetworkSetup
    {
        [MenuItem("Tools/REBAKA/Setup Ragdoll NetworkRigidbody")]
        public static void SetupNetworkRigidbodies()
        {
            const string prefabPath = "Assets/Level/Prefabs/newAPRPlayer.prefab";

            // プレハブをロード
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefabAsset == null)
            {
                Debug.LogError($"[RagdollNetworkSetup] プレハブが見つかりません: {prefabPath}");
                return;
            }

            // プレハブを編集モードで開く
            var prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);

            int addedCount = 0;
            int interpolationCount = 0;

            // APR_ で始まる名前の全Rigidbodyを検索
            var allRigidbodies = prefabRoot.GetComponentsInChildren<Rigidbody>(includeInactive: true);

            foreach (var rb in allRigidbodies)
            {
                // 全RigidbodyのinterpolationをInterpolateに設定（振動軽減）
                if (rb.interpolation != RigidbodyInterpolation.Interpolate)
                {
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    interpolationCount++;
                }

                // APR_ で始まるGameObjectのみ NetworkRigidbody を追加
                if (!rb.gameObject.name.StartsWith("APR_")) continue;

                // 既存のNetworkRigidbodyはスキップ（APR_Rootなど）
                if (rb.gameObject.GetComponent<NetworkRigidbody>() != null)
                {
                    Debug.Log($"[RagdollNetworkSetup] スキップ（既存）: {rb.gameObject.name}");
                    continue;
                }

                // NetworkRigidbody を追加
                var nrb = rb.gameObject.AddComponent<NetworkRigidbody>();

                // 親の位置変更は APR_Root が管理するため false
                nrb.SyncParent = false;
                nrb.SyncScale = false;

                addedCount++;
                Debug.Log($"[RagdollNetworkSetup] NetworkRigidbody を追加: {rb.gameObject.name}");
            }

            // プレハブを保存
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
            PrefabUtility.UnloadPrefabContents(prefabRoot);

            Debug.Log($"[RagdollNetworkSetup] 完了! NetworkRigidbody を {addedCount} 個追加、Interpolation を {interpolationCount} 個設定しました。");
            Debug.Log("[RagdollNetworkSetup] 次のステップ: newAPRPlayer.prefab を開き、NetworkObject の「Rebuild Object Table」をクリックしてください。");

            // エディタを更新
            AssetDatabase.Refresh();
            EditorUtility.DisplayDialog(
                "Setup 完了",
                $"NetworkRigidbody を {addedCount} 個追加しました。\n\n" +
                "次のステップ:\n" +
                "1. newAPRPlayer.prefab を開く\n" +
                "2. NetworkObject コンポーネントの\n   「Rebuild Object Table」をクリック\n" +
                "3. NetworkedBehaviours の数が増加していることを確認\n   （5個 → 19個程度）",
                "OK"
            );
        }
    }
}
#endif
