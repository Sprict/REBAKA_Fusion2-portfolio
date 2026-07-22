// Assets/Code/Scripts/Map/ModuleDefinition.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// モジュールの Unity 側オーサリング表現（段階 B）。Inspector で footprint / socket / パスノードを置き、
    /// 見た目の prefab を割り当てる。生成コアは <see cref="ModuleSpec"/>（純粋データ）しか知らないため、
    /// このアセットは <see cref="ToSpec"/> で spec へ写してからジェネレータに渡す。
    ///
    /// 座標はすべてモジュールローカルの整数セル。pivot 規約: prefab のローカル原点 = セル (0,0,0)、
    /// 回転 0 の向きで、1 セル = MapBuilder の cellSize（m）。<see cref="MapBuilder"/> はこの規約で Instantiate する。
    /// </summary>
    [CreateAssetMenu(menuName = "REBAKA/Map/Module Definition", fileName = "Module")]
    public sealed class ModuleDefinition : ScriptableObject
    {
        /// <summary>接続口のオーサリング表現（readonly struct の <see cref="MapSocket"/> は直接シリアライズできないため別途定義）。</summary>
        [System.Serializable]
        public struct SocketAuthoring
        {
            [Tooltip("戸口があるローカルセル。")]
            public Vector3Int localCell;

            [Tooltip("この口からモジュールの外へ出る向き。")]
            public MapDirection facing;

            [Tooltip("接続種別。異なる channel 同士は接続できない（MVP は 0 単一）。")]
            public int channel;

            [Tooltip("この戸口の通行幅（セル）。1 以上。隣接モジュールと噛み合った辺の clearance に効く。")]
            public int clearance;
        }

        /// <summary>モジュール内パスノードを結ぶ辺のオーサリング表現（ノード index はローカル）。</summary>
        [System.Serializable]
        public struct EdgeAuthoring
        {
            [Tooltip("ローカルパスノード index（PathNodes の添字）。")]
            public int nodeA;
            public int nodeB;

            [Tooltip("この通り道の通行幅（セル）。1 以上。")]
            public int clearance;
        }

        [Header("識別")]
        [Tooltip("カタログ内で安定な ID（manifest checksum に効く。並び替えに強い）。空ならアセット名を使う。")]
        [SerializeField] private string _id = "";

        [SerializeField] private ModuleRole _role = ModuleRole.Body;

        [Tooltip("生成時の選ばれやすさ（Body の重み付き抽選用）。1 以上。")]
        [SerializeField, Min(1)] private int _weight = 1;

        [Header("形状")]
        [Tooltip("占有するローカルセル集合（footprint）。")]
        [SerializeField] private List<Vector3Int> _footprintCells = new List<Vector3Int> { Vector3Int.zero };

        [Tooltip("接続口。")]
        [SerializeField] private List<SocketAuthoring> _sockets = new List<SocketAuthoring>();

        [Header("ナビ（N1 埋め込みパスグラフ）")]
        [Tooltip("モジュール内に手置きするパスノードのローカルセル。戸口セルに置くと隣接モジュールと跨ぎ辺で結ばれる。")]
        [SerializeField] private List<Vector3Int> _pathNodes = new List<Vector3Int>();

        [Tooltip("パスノード間の内部辺（ローカル index 参照）。多セルモジュールの通り道。")]
        [SerializeField] private List<EdgeAuthoring> _internalEdges = new List<EdgeAuthoring>();

        [Header("見た目")]
        [Tooltip("配置時に Instantiate する prefab。未割当なら MapBuilder がプレースホルダを生成する。")]
        [SerializeField] private GameObject _prefab;

        /// <summary>配置時に Instantiate する prefab（未割当なら null）。</summary>
        public GameObject Prefab => _prefab;

        /// <summary>カタログ用の安定 ID。未設定ならアセット名を使う。</summary>
        public string ResolvedId => string.IsNullOrEmpty(_id) ? name : _id;

        /// <summary>オーサリング内容を生成コア用の純粋 <see cref="ModuleSpec"/> へ写す。</summary>
        public ModuleSpec ToSpec()
        {
            var sockets = new List<MapSocket>(_sockets.Count);
            foreach (SocketAuthoring s in _sockets)
                sockets.Add(new MapSocket(s.localCell, s.facing, s.channel, s.clearance < 1 ? 1 : s.clearance));

            var edges = new List<ModulePathEdge>(_internalEdges.Count);
            foreach (EdgeAuthoring e in _internalEdges)
                edges.Add(new ModulePathEdge(e.nodeA, e.nodeB, new TraversalProfile(e.clearance < 1 ? 1 : e.clearance)));

            return new ModuleSpec(
                ResolvedId,
                _role,
                new List<Vector3Int>(_footprintCells),
                sockets,
                _weight,
                new List<Vector3Int>(_pathNodes),
                edges);
        }
    }
}
