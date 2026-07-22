using System.Collections.Generic;
using System.Linq;
using Fusion;
using UnityEditor;
using UnityEngine;

namespace MyFolder.Editor.Preflight
{
    /// <summary>
    /// チェック#4: 開いているシーンに直接配置された NetworkObject の一覧を出す。
    /// 過去事故: Obs_Cube が worldPrefabs 登録とシーン配置の両方に存在し二重スポーン。
    /// 「二重かどうか」の完全自動判定は spawner 側の動的挙動に依存して誤緑リスクがあるため、
    /// 機械判定せず一覧を Warning で出し、人間が目視確認する設計（誤緑回避原則）。
    /// </summary>
    public sealed class ScenePlacedObjectsCheck : IPreflightCheck
    {
        public string Name => "シーン配置 NetworkObject（二重スポーン目視）";

        public PreflightResult Run()
        {
            var infos = new List<(string objectName, string prefabName)>();
            NetworkObject[] all = Object.FindObjectsByType<NetworkObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (NetworkObject no in all)
            {
                // ルート NObj のみ対象（ネスト NObj は親の行と一緒に目視すればよい）
                if (no.transform.parent != null &&
                    no.transform.parent.GetComponentInParent<NetworkObject>() != null)
                {
                    continue;
                }

                GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(no.gameObject);
                infos.Add((no.gameObject.name, source != null ? source.name : null));
            }

            return Evaluate(infos);
        }

        public static PreflightResult Evaluate(
            IReadOnlyList<(string objectName, string prefabName)> scenePlaced)
        {
            if (scenePlaced.Count == 0)
            {
                return PreflightResult.Pass("開いているシーンに配置済み NetworkObject はありません。");
            }

            IEnumerable<string> lines = scenePlaced.Select(i =>
                "  - " + i.objectName +
                (i.prefabName != null ? $" (prefab: {i.prefabName})" : " (prefab元なし)"));

            return PreflightResult.Warn(
                $"シーン配置 NetworkObject {scenePlaced.Count} 件:\n" + string.Join("\n", lines),
                "spawner がスポーンする prefab と同一のものが二重にないか目視確認する" +
                "（過去事故: Obs_Cube の worldPrefabs 二重スポーン）。");
        }
    }
}
