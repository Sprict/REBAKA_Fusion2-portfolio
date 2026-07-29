using System.IO;
using UnityEngine;

namespace MyFolder.Editor.Preflight
{
    /// <summary>
    /// チェック#2: config の AssembliesToWeave にゲームアセンブリが含まれるか。
    /// 欠けると [Networked] が weave されず、症状は "has not been weaved" ログ + 全スポーン死。
    /// JSON から読み取れない場合は判定不能として Warning（誤緑回避）。
    /// </summary>
    public sealed class WeaveAssembliesCheck : IPreflightCheck
    {
        public const string RequiredAssembly = "MyProject.Scripts";

        // JsonUtility で AssembliesToWeave フィールドだけを部分デシリアライズするための DTO。
        [System.Serializable]
        private sealed class WeaveDto
        {
            public string[] AssembliesToWeave;
        }

        public string Name => "AssembliesToWeave 登録";

        public PreflightResult Run()
        {
            if (!File.Exists(ConfigUniquenessCheck.CanonicalPath))
            {
                return PreflightResult.Fail(
                    $"正本 config が存在しません: {ConfigUniquenessCheck.CanonicalPath}",
                    "先に「NetworkProjectConfig 一意性」チェックの Fail を解決する。");
            }
            return Evaluate(File.ReadAllText(ConfigUniquenessCheck.CanonicalPath), RequiredAssembly);
        }

        public static PreflightResult Evaluate(string configJson, string requiredAssembly)
        {
            WeaveDto dto = null;
            try
            {
                dto = JsonUtility.FromJson<WeaveDto>(configJson);
            }
            catch (System.Exception)
            {
                // 判定不能 → Warning に倒す（下の null ガードに合流）
            }

            if (dto?.AssembliesToWeave == null || dto.AssembliesToWeave.Length == 0)
            {
                return PreflightResult.Warn(
                    "AssembliesToWeave を config から読み取れませんでした（判定不能）。",
                    "NetworkProjectConfig を Unity Inspector で開き、Assemblies To Weave を目視確認する。");
            }

            if (System.Array.IndexOf(dto.AssembliesToWeave, requiredAssembly) >= 0)
            {
                return PreflightResult.Pass($"{requiredAssembly} は weave 対象です。");
            }

            return PreflightResult.Fail(
                $"{requiredAssembly} が AssembliesToWeave にありません。[Networked] が動作しません。",
                $"NetworkProjectConfig の Assemblies To Weave に {requiredAssembly} を追加する" +
                "（症状: 'has not been weaved' ログ）。");
        }
    }
}
