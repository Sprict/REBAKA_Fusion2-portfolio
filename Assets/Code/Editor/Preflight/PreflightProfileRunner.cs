using System;
using System.Collections.Generic;
using UnityEngine;

namespace Rebaka.Editor.Preflight
{
    public sealed class PreflightProfileRunner
    {
        public PreflightRunResult Run(PreflightProfileDefinition definition)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));

            var results = new List<(string Name, PreflightResult Result)>();
            PreflightResult precondition = RunSafely(definition.Precondition);
            results.Add((definition.Precondition.Name, precondition));

            if (precondition.Status != PreflightStatus.Pass)
                return new PreflightRunResult(definition, results);

            foreach (IPreflightCheck check in definition.Checks)
                results.Add((check.Name, RunSafely(check)));

            return new PreflightRunResult(definition, results);
        }

        private static PreflightResult RunSafely(IPreflightCheck check)
        {
            try
            {
                return check.Run();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return PreflightResult.Fail(
                    $"検査中に例外が発生しました: {exception.GetType().Name}",
                    "Console の例外を確認してください。");
            }
        }
    }
}
