// Assets/Code/Scripts/Map/MapGenerationVisualizer.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// マップ自動生成（B1 連結 ＋ N1 埋め込みパスグラフ）の目視確認用サンドボックス。
    ///
    /// 段階 B（プレハブ・ScriptableObject オーサリング）を待たずに動作確認できるよう、
    /// カタログをコードで組み、生成結果を Gizmo で描く。プレハブ・ネットワーク非依存。
    ///
    /// 使い方:
    /// 1) このコンポーネントを付けた空 GameObject を置く（MapGenerationSandbox シーンに同梱済み）。
    /// 2) Scene ビューを開く（Gizmos 表示 ON）。Inspector の Seed を変えると即座に再生成される。
    /// 3) 右クリック → "Regenerate" でも再生成。Play 不要で確認できる。
    ///
    /// 色分け: Start=緑 / Goal=赤 / Body=灰 / DeadEnd=黄。
    /// パスグラフ: ノード=白球 / 辺=clearance に応じた色（広い=シアン, 狭い=マゼンタ）。
    /// </summary>
    [ExecuteAlways]
    public sealed class MapGenerationVisualizer : MonoBehaviour
    {
        [Header("生成シード（変更で即再生成）")]
        [SerializeField] private int _seed = 12345;

        [Header("生成パラメータ")]
        [SerializeField, Min(0)] private int _mainPathLength = 4;
        [SerializeField, Min(0)] private int _branchCount = 2;
        [SerializeField, Min(0)] private int _branchLength = 1;
        [SerializeField, Min(1)] private int _maxPlacementTries = 16;
        [SerializeField, Min(0)] private int _maxRerolls = 16;
        [Tooltip("ツリー構築後に閉じるループ（閉路）の最大数。0 で一本道ツリー。網目状にするには増やす。")]
        [SerializeField, Min(0)] private int _loopConnections = 4;

        [Tooltip("生成後に最深の開口へ置く裏口数。0..2。")]
        [SerializeField, Range(0, 2)] private int _backDoorCount = 2;

        [Header("表示")]
        [Tooltip("1 セルあたりのワールドサイズ（m）。")]
        [SerializeField, Min(0.1f)] private float _cellSize = 2f;
        [SerializeField] private bool _drawFootprint = true;
        [SerializeField] private bool _drawPathGraph = true;

        [Header("敵サイズ別パス確認")]
        [Tooltip("この clearance 未満の戸口を通れない敵で Start→Goal を A* 探索し、見つかれば経路を強調表示。")]
        [SerializeField, Min(1)] private int _enemyClearance = 1;

        // --- 生成結果のキャッシュ（OnDrawGizmos はこれを描くだけ） ---
        private ModuleCatalog _catalog;
        private MapLayout _layout;
        private MapPathGraph _graph;
        private MapGenerationResult _lastResult;
        private List<int> _enemyPath; // node id 列（見つからなければ null）

        private void OnEnable() => Regenerate();

        // Inspector でシード/パラメータを変えたら再生成（純ロジックなのでエディタでも安全）。
        private void OnValidate() => Regenerate();

        [ContextMenu("Regenerate")]
        public void Regenerate()
        {
            _catalog = SandboxCatalog.Build();
            var config = new MapGeneratorConfig
            {
                MainPathLength = _mainPathLength,
                BranchCount = _branchCount,
                BranchLength = _branchLength,
                MaxPlacementTries = _maxPlacementTries,
                MaxRerolls = _maxRerolls,
                LoopConnections = _loopConnections,
                BackDoorCount = _backDoorCount,
            };

            var generator = new MapGenerator(_catalog);
            _lastResult = generator.Generate((ulong)_seed, config);
            _layout = _lastResult.Layout;
            _graph = MapPathGraph.Build(_layout);

            // 敵サイズ別の Start→Goal 経路（戸口幅フィルタ）。
            _enemyPath = null;
            int startSlot = SlotWithRole(_layout, ModuleRole.Start);
            int goalSlot = SlotWithRole(_layout, ModuleRole.Goal);
            if (startSlot >= 0 && goalSlot >= 0)
                _graph.TryFindPathBetweenModules(startSlot, goalSlot, _enemyClearance, out _enemyPath);
        }

        /// <summary>現在の生成状態の要約（Inspector のヘルプや手動ログ用）。</summary>
        public string Summary()
        {
            if (_layout == null) return "(未生成)";
            bool connected = _graph != null && _graph.IsConnected();
            return $"seed={_seed} modules={_layout.Count} nodes={_graph?.NodeCount} " +
                   $"connected={connected} graphConnected={connected} " +
                   $"fallback={_lastResult.UsedFallback} rerolls={_lastResult.RerollsUsed} " +
                   $"enemyPath(clr>={_enemyClearance})={(_enemyPath != null ? _enemyPath.Count + "hops" : "none")}";
        }

        [ContextMenu("Log Summary")]
        private void LogSummary() => Debug.Log("[MapGenerationVisualizer] " + Summary(), this);

        // --- Gizmo 描画 ---------------------------------------------------------

        private void OnDrawGizmos()
        {
            if (_layout == null)
                Regenerate();
            if (_layout == null)
                return;

            if (_drawFootprint)
                DrawFootprint();
            if (_drawPathGraph)
                DrawGraph();
        }

        private void DrawFootprint()
        {
            Vector3 size = new Vector3(_cellSize * 0.9f, _cellSize * 0.1f, _cellSize * 0.9f);
            foreach (PlacedModule pm in _layout.Modules)
            {
                Gizmos.color = RoleColor(_catalog[pm.ModuleIndex].Role);
                foreach (Vector3Int cell in pm.WorldFootprint(_catalog))
                    Gizmos.DrawCube(CellToWorld(cell), size);
            }
        }

        private void DrawGraph()
        {
            if (_graph == null) return;

            // 辺。
            foreach (MapPathLink link in _graph.Edges)
            {
                Vector3 a = NodeToWorld(_graph.Nodes[link.A]);
                Vector3 b = NodeToWorld(_graph.Nodes[link.B]);
                Gizmos.color = link.Profile.Clearance >= 2 ? Color.cyan : Color.magenta;
                Gizmos.DrawLine(a, b);
            }

            // ノード。
            Gizmos.color = Color.white;
            float r = _cellSize * 0.12f;
            foreach (MapPathGraphNode node in _graph.Nodes)
                Gizmos.DrawSphere(NodeToWorld(node), r);

            // 敵サイズ別の Start→Goal 経路を太線で強調。
            if (_enemyPath != null && _enemyPath.Count >= 2)
            {
                Gizmos.color = Color.green;
                for (int i = 0; i < _enemyPath.Count - 1; i++)
                {
                    Vector3 a = NodeToWorld(_graph.Nodes[_enemyPath[i]]);
                    Vector3 b = NodeToWorld(_graph.Nodes[_enemyPath[i + 1]]);
                    // 少し持ち上げて辺と重ねて見やすく。
                    Vector3 up = Vector3.up * (_cellSize * 0.05f);
                    Gizmos.DrawLine(a + up, b + up);
                }
            }
        }

        private Vector3 CellToWorld(Vector3Int cell)
        {
            return transform.position + new Vector3(cell.x, cell.y, cell.z) * _cellSize;
        }

        private Vector3 NodeToWorld(MapPathGraphNode node)
        {
            return CellToWorld(node.WorldCell) + Vector3.up * (_cellSize * 0.35f);
        }

        private static Color RoleColor(ModuleRole role)
        {
            switch (role)
            {
                case ModuleRole.Start: return new Color(0.2f, 0.8f, 0.3f, 0.85f);
                case ModuleRole.Goal: return new Color(0.85f, 0.25f, 0.25f, 0.85f);
                case ModuleRole.DeadEnd: return new Color(0.85f, 0.8f, 0.2f, 0.85f);
                case ModuleRole.Exit: return new Color(0.2f, 0.45f, 0.95f, 0.85f);
                default: return new Color(0.6f, 0.6f, 0.65f, 0.75f);
            }
        }

        private static int SlotWithRole(MapLayout layout, ModuleRole role)
        {
            for (int i = 0; i < layout.Count; i++)
            {
                if (layout.Catalog[layout.Modules[i].ModuleIndex].Role == role)
                    return i;
            }
            return -1;
        }
    }
}
