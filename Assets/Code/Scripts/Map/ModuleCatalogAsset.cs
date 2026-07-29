// Assets/Code/Scripts/Map/ModuleCatalogAsset.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 生成に使うモジュール定義の順序付き集合（段階 B）。Inspector で <see cref="ModuleDefinition"/> を並べる。
    ///
    /// CRITICAL: 並び順がそのまま <see cref="ModuleCatalog"/> の index になり、manifest の moduleIndex が
    /// この順序に依存する（devlog 2026-06-27 §6 / E2）。配布側と受信側で同一カタログ（同一順序）が前提。
    /// 並び替え・要素挿入は checksum を変えるため、配布後の運用では避ける。
    /// </summary>
    [CreateAssetMenu(menuName = "REBAKA/Map/Module Catalog", fileName = "ModuleCatalog")]
    public sealed class ModuleCatalogAsset : ScriptableObject
    {
        [Tooltip("生成に使うモジュール定義。並び順 = カタログ index（manifest がこれに依存）。")]
        [SerializeField] private List<ModuleDefinition> _modules = new List<ModuleDefinition>();

        /// <summary>定義数（prefab 解決の index 範囲）。</summary>
        public int Count => _modules.Count;

        /// <summary>index に対応する prefab（未割当・範囲外なら null）。MapBuilder が Instantiate に使う。</summary>
        public GameObject PrefabAt(int index)
        {
            if (index < 0 || index >= _modules.Count) return null;
            return _modules[index] != null ? _modules[index].Prefab : null;
        }

        /// <summary>index に対応する役割（範囲外なら Body）。プレースホルダ着色に使う。</summary>
        public ModuleRole RoleAt(int index)
        {
            if (index < 0 || index >= _modules.Count || _modules[index] == null) return ModuleRole.Body;
            return _modules[index].ToSpec().Role;
        }

        /// <summary>
        /// 生成コア用の <see cref="ModuleCatalog"/> を組む。並び順を厳守する。
        /// null 要素はスキップせずスロットを詰めると index がずれるため、未割当があれば false を返して呼び出し側に知らせる。
        /// </summary>
        public bool TryBuildCatalog(out ModuleCatalog catalog)
        {
            catalog = null;
            var specs = new List<ModuleSpec>(_modules.Count);
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i] == null)
                    return false; // 穴あきカタログは index ズレを生むので拒否
                specs.Add(_modules[i].ToSpec());
            }
            catalog = new ModuleCatalog(specs);
            return true;
        }
    }
}
