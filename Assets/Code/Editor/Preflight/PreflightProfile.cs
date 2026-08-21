using System;
using System.Collections.Generic;
using System.Linq;

namespace Rebaka.Editor.Preflight
{
    public enum PreflightProfile
    {
        ProductionIntegration,
        MapPrototype
    }

    public sealed class PreflightProfileDefinition
    {
        public PreflightProfile Profile { get; }
        public string Title { get; }
        public string ExpectedScenePath { get; }
        public bool BlocksDevelopIntegrationWhenFail { get; }
        public IPreflightCheck Precondition { get; }
        public IReadOnlyList<IPreflightCheck> Checks { get; }

        public PreflightProfileDefinition(
            PreflightProfile profile,
            string title,
            string expectedScenePath,
            bool blocksDevelopIntegrationWhenFail,
            IPreflightCheck precondition,
            IEnumerable<IPreflightCheck> checks)
        {
            Profile = profile;
            Title = title ?? throw new ArgumentNullException(nameof(title));
            ExpectedScenePath = expectedScenePath ?? throw new ArgumentNullException(nameof(expectedScenePath));
            BlocksDevelopIntegrationWhenFail = blocksDevelopIntegrationWhenFail;
            Precondition = precondition ?? throw new ArgumentNullException(nameof(precondition));
            Checks = (checks ?? throw new ArgumentNullException(nameof(checks))).ToArray();
        }
    }

    public sealed class PreflightRunResult
    {
        public PreflightProfile Profile { get; }
        public string Title { get; }
        public IReadOnlyList<(string Name, PreflightResult Result)> Results { get; }
        public bool HasFail => Results.Any(entry => entry.Result.Status == PreflightStatus.Fail);
        public int FailCount => Results.Count(entry => entry.Result.Status == PreflightStatus.Fail);
        public int WarningCount => Results.Count(entry => entry.Result.Status == PreflightStatus.Warning);
        public bool BlocksDevelopIntegration { get; }

        public PreflightRunResult(
            PreflightProfileDefinition definition,
            IEnumerable<(string Name, PreflightResult Result)> results)
        {
            Profile = definition.Profile;
            Title = definition.Title;
            Results = results.ToArray();
            BlocksDevelopIntegration = definition.BlocksDevelopIntegrationWhenFail && HasFail;
        }
    }

    public static class PreflightProfileCatalog
    {
        public const string ProductionScenePath = "Assets/Level/Scenes/Test_Playground.unity";
        public const string MapPrototypeScenePath = "Assets/Level/Scenes/MapNetworkSandbox.unity";

        public static PreflightProfileDefinition Create(PreflightProfile profile)
        {
            return profile switch
            {
                PreflightProfile.ProductionIntegration => new PreflightProfileDefinition(
                    profile,
                    "Production Integration",
                    ProductionScenePath,
                    true,
                    new ActiveSceneCheck(ProductionScenePath),
                    new IPreflightCheck[]
                    {
                        new ConfigUniquenessCheck(),
                        new WeaveAssembliesCheck(),
                        new SceneRegistrationCheck(ProductionScenePath),
                        new ScenePlacedObjectsCheck(ProductionScenePath),
                        new BackupFreshnessCheck()
                    }),
                PreflightProfile.MapPrototype => new PreflightProfileDefinition(
                    profile,
                    "Map Prototype",
                    MapPrototypeScenePath,
                    false,
                    new ActiveSceneCheck(MapPrototypeScenePath),
                    new IPreflightCheck[]
                    {
                        new MapWiringCheck(MapPrototypeScenePath),
                        new ScenePlacedObjectsCheck(MapPrototypeScenePath)
                    }),
                _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
            };
        }
    }
}
