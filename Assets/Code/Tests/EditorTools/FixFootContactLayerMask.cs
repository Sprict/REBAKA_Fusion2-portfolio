using UnityEngine;
using UnityEditor;

/// <summary>
/// newAPRPlayer プレファブの RagdollFootContact.groundLayer を Ground レイヤーに設定する。
/// </summary>
public static class FixFootContactLayerMask
{
    public static string Execute()
    {
        string prefabPath = "Assets/Level/Prefabs/newAPRPlayer.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab == null) return "ERROR: Prefab not found.";

        // Ground レイヤーのインデックスを取得
        int groundLayer = LayerMask.NameToLayer("Ground");
        if (groundLayer < 0) return "ERROR: Ground layer not found.";

        LayerMask groundMask = 1 << groundLayer;

        var footContacts = prefab.GetComponentsInChildren<RagdollFootContact>(true);
        if (footContacts == null || footContacts.Length == 0)
            return "ERROR: No RagdollFootContact found.";

        // プレファブを編集モードで開く
        string assetPath = AssetDatabase.GetAssetPath(prefab);
        var root = PrefabUtility.LoadPrefabContents(assetPath);

        var contacts = root.GetComponentsInChildren<RagdollFootContact>(true);
        int fixedCount = 0;
        foreach (var contact in contacts)
        {
            // groundLayer フィールドを SerializedObject 経由で設定
            var so = new SerializedObject(contact);
            var prop = so.FindProperty("groundLayer");
            if (prop != null)
            {
                prop.intValue = groundMask.value;
                so.ApplyModifiedProperties();
                fixedCount++;
                Debug.Log($"[FixFoot] Set groundLayer on {contact.gameObject.name} to {groundMask.value} (Ground layer={groundLayer})");
            }
            else
            {
                Debug.LogWarning($"[FixFoot] groundLayer property not found on {contact.gameObject.name}");
            }
        }

        PrefabUtility.SaveAsPrefabAsset(root, assetPath);
        PrefabUtility.UnloadPrefabContents(root);

        return $"OK: Fixed {fixedCount} RagdollFootContact(s). groundLayer={groundMask.value} (bit {groundLayer})";
    }
}
