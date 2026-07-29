// Assets/Code/Scripts/Map/ModuleSpec.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// マップ生成上のモジュールの役割。生成器が接続順序を組むときに使う。
    /// </summary>
    public enum ModuleRole
    {
        /// <summary>開始部屋。レイアウトの起点に 1 個だけ置く。</summary>
        Start,
        /// <summary>終了部屋。一本道の終端に置く。</summary>
        Goal,
        /// <summary>通常の中継モジュール（直線・コーナー・部屋など）。</summary>
        Body,
        /// <summary>行き止まり。分岐の末端を塞ぐのに使う。</summary>
        DeadEnd,
        /// <summary>
        /// ループ閉じ用の連結器（十字路など）。通常成長の重み抽選には乗らず、
        /// ループ閉じパスが「複数モジュールが向かい合う空きセル」に置いて閉路を作るのに使う。
        /// </summary>
        Connector,
        /// <summary>ダンジョン外への出口（裏口）。生成後に最深の開口へ置く。</summary>
        Exit,
    }

    /// <summary>
    /// モジュールの「論理定義」。プレハブ参照や見た目は持たず、生成に必要な幾何情報だけを持つ純粋データ。
    /// Unity 層（ScriptableObject ModuleDefinition）から変換して与えられる想定。
    /// 座標はすべてモジュールローカルの整数セル。
    /// </summary>
    public sealed class ModuleSpec
    {
        /// <summary>カタログ内で安定な識別子（manifest checksum に効く。並び替えに強い）。</summary>
        public readonly string Id;

        public readonly ModuleRole Role;

        /// <summary>占有するローカルセル集合（footprint）。回転・平行移動して衝突判定に使う。</summary>
        public readonly IReadOnlyList<Vector3Int> FootprintCells;

        /// <summary>接続口。</summary>
        public readonly IReadOnlyList<MapSocket> Sockets;

        /// <summary>生成時の選ばれやすさ（Body の重み付き抽選用）。1 以上。</summary>
        public readonly int Weight;

        /// <summary>
        /// モジュール内に手置きするパスノードのローカルセル（devlog §6 N1 埋め込みパスグラフ）。
        /// 戸口セルにノードを置くと、隣接モジュールと噛み合った際にそのノード同士が跨ぎ辺で結ばれ、
        /// 「レイアウト連結 = パスグラフ連結」が成立する。空ならこのモジュールはナビ非対象。
        /// </summary>
        public readonly IReadOnlyList<Vector3Int> PathNodes;

        /// <summary>モジュール内のパスノードを結ぶ辺（ローカル index 参照）。多セルモジュールの通り道を表す。</summary>
        public readonly IReadOnlyList<ModulePathEdge> InternalEdges;

        public ModuleSpec(
            string id,
            ModuleRole role,
            IReadOnlyList<Vector3Int> footprintCells,
            IReadOnlyList<MapSocket> sockets,
            int weight = 1,
            IReadOnlyList<Vector3Int> pathNodes = null,
            IReadOnlyList<ModulePathEdge> internalEdges = null)
        {
            Id = id;
            Role = role;
            FootprintCells = footprintCells ?? System.Array.Empty<Vector3Int>();
            Sockets = sockets ?? System.Array.Empty<MapSocket>();
            Weight = weight < 1 ? 1 : weight;
            PathNodes = pathNodes ?? System.Array.Empty<Vector3Int>();
            InternalEdges = internalEdges ?? System.Array.Empty<ModulePathEdge>();
        }

        public int SocketCount => Sockets.Count;
    }

    /// <summary>
    /// 生成に使えるモジュールの集合。役割別の参照を提供する。
    /// 並び順は安定（manifest の moduleIndex がこの順序に依存するため、配布側と受信側で同一カタログが前提）。
    /// </summary>
    public sealed class ModuleCatalog
    {
        private readonly List<ModuleSpec> _modules;

        public ModuleCatalog(IEnumerable<ModuleSpec> modules)
        {
            _modules = new List<ModuleSpec>(modules);
        }

        public IReadOnlyList<ModuleSpec> Modules => _modules;
        public int Count => _modules.Count;
        public ModuleSpec this[int index] => _modules[index];

        public int IndexOf(ModuleSpec spec) => _modules.IndexOf(spec);

        public IEnumerable<int> IndicesWithRole(ModuleRole role)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i].Role == role)
                    yield return i;
            }
        }

        /// <summary>役割を持つ最初のモジュールの index。無ければ -1。</summary>
        public int FirstIndexWithRole(ModuleRole role)
        {
            for (int i = 0; i < _modules.Count; i++)
            {
                if (_modules[i].Role == role)
                    return i;
            }
            return -1;
        }
    }
}
