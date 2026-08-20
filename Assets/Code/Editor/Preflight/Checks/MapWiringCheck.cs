using ArgumentNullException = System.ArgumentNullException;
using System.Collections.Generic;
using System.Linq;
using Rebaka.Map;
using UnityEditor;
using UnityEngine;

namespace Rebaka.Editor.Preflight
{
    /// <summary>
    /// チェック#6: Map 系コンポーネントのシーン配線（必須参照が設定されているか）。
    /// 対象は「開いているシーン」のみ。Map 系が1つも無い場合は未検査として Warning
    /// （MapNetworkSandbox を開いて再実行してもらう。誤緑回避原則）。
    /// private [SerializeField] は SerializedObject 経由で読む（runtime クラス無変更）。
    /// </summary>
    public sealed class MapWiringCheck : IPreflightCheck
    {
        public struct Snapshot
        {
            public int BuilderCount;
            public int SpawnerCount;
            public int DistributorCount;
            public bool BuilderCatalogMissing;
            public bool SpawnerPrefabMissing;
        }

        public string Name => "Map 系シーン配線";
        public string ExpectedScenePath { get; }

        public MapWiringCheck(string expectedScenePath)
        {
            ExpectedScenePath = expectedScenePath ?? throw new ArgumentNullException(nameof(expectedScenePath));
        }

        public PreflightResult Run()
        {
            MapBuilder[] builders = Object.FindObjectsByType<MapBuilder>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(IsInExpectedScene)
                .ToArray();
            MapTreasureSpawner[] spawners = Object.FindObjectsByType<MapTreasureSpawner>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(IsInExpectedScene)
                .ToArray();
            MapNetworkDistributor[] distributors = Object.FindObjectsByType<MapNetworkDistributor>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(IsInExpectedScene)
                .ToArray();

            var snapshot = new Snapshot
            {
                BuilderCount = builders.Length,
                SpawnerCount = spawners.Length,
                DistributorCount = distributors.Length,
                BuilderCatalogMissing = AnyReferenceMissing(builders, "_catalogAsset"),
                SpawnerPrefabMissing = AnyReferenceMissing(spawners, "_treasurePrefab"),
            };
            return Evaluate(snapshot);
        }

        private bool IsInExpectedScene(Component component)
        {
            return ActiveSceneCheck.MatchesExpectedScenePath(
                component.gameObject.scene.path,
                ExpectedScenePath);
        }

        public static PreflightResult Evaluate(Snapshot s)
        {
            var missingRoles = new List<string>();
            if (s.BuilderCount == 0) missingRoles.Add("MapBuilder");
            if (s.SpawnerCount == 0) missingRoles.Add("MapTreasureSpawner");
            if (s.DistributorCount == 0) missingRoles.Add("MapNetworkDistributor");

            if (missingRoles.Count > 0)
            {
                return PreflightResult.Fail(
                    "対象シーンに必須コンポーネントがありません: " + string.Join(", ", missingRoles),
                    "MapNetworkSandbox に3役を各1個以上配置してください。");
            }

            var problems = new List<string>();
            if (s.BuilderCatalogMissing) problems.Add("MapBuilder の Catalog Asset が未設定");
            if (s.SpawnerPrefabMissing) problems.Add("MapTreasureSpawner の Treasure Prefab が未設定");
            if (problems.Count == 0)
            {
                return PreflightResult.Pass(
                    $"Map 系配線 OK (Builder={s.BuilderCount}, Spawner={s.SpawnerCount}, " +
                    $"Distributor={s.DistributorCount})");
            }

            return PreflightResult.Fail(
                string.Join(" / ", problems),
                "各コンポーネントの Inspector で参照を設定する。");
        }

        // private [SerializeField] の参照フィールドが null のものが1つでもあるか。
        private static bool AnyReferenceMissing(Component[] components, string fieldName)
        {
            foreach (Component component in components)
            {
                SerializedProperty prop = new SerializedObject(component).FindProperty(fieldName);
                if (prop == null)
                {
                    // フィールド名が変わった場合は「無い」扱いにせず missing 扱い（誤緑回避）。
                    Debug.LogWarning($"[MapWiringCheck] {component.GetType().Name} に {fieldName} が見つかりません。");
                    return true;
                }
                if (prop.objectReferenceValue == null) return true;
            }
            return false;
        }
    }
}
