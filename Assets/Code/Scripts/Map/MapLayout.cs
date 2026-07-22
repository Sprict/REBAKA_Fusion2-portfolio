// Assets/Code/Scripts/Map/MapLayout.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 配置済みモジュール 1 個分。離散配置（整数セル原点＋90°回転ステップ）のみで姿勢が決まる。
    /// 浮動小数を持たないため、host/client で同一カタログ・同一値なら Instantiate 結果がビット同一になる。
    /// </summary>
    public readonly struct PlacedModule
    {
        /// <summary>カタログ内 index。</summary>
        public readonly int ModuleIndex;

        /// <summary>ワールドのセル原点。</summary>
        public readonly Vector3Int OriginCell;

        /// <summary>Y 軸まわり 90° 回転ステップ（0..3）。</summary>
        public readonly int RotationSteps;

        public PlacedModule(int moduleIndex, Vector3Int originCell, int rotationSteps)
        {
            ModuleIndex = moduleIndex;
            OriginCell = originCell;
            RotationSteps = GridRotation.Normalize(rotationSteps);
        }

        /// <summary>このモジュールが占有するワールドセル群を列挙する。</summary>
        public IEnumerable<Vector3Int> WorldFootprint(ModuleCatalog catalog)
        {
            ModuleSpec spec = catalog[ModuleIndex];
            foreach (Vector3Int local in spec.FootprintCells)
            {
                yield return OriginCell + GridRotation.RotateCell(local, RotationSteps);
            }
        }

        /// <summary>このモジュールのソケットをワールドへ写して列挙する。</summary>
        public IEnumerable<WorldSocket> WorldSockets(ModuleCatalog catalog)
        {
            ModuleSpec spec = catalog[ModuleIndex];
            foreach (MapSocket socket in spec.Sockets)
            {
                Vector3Int cell = OriginCell + GridRotation.RotateCell(socket.LocalCell, RotationSteps);
                MapDirection facing = GridRotation.RotateDirection(socket.Facing, RotationSteps);
                yield return new WorldSocket(cell, facing, socket.Channel, socket.Clearance);
            }
        }
    }

    /// <summary>
    /// 生成結果のレイアウト。配置モジュール列と、それを再現するのに必要な seed/カタログ参照を保持する。
    /// このレイアウトから MapBuilder がローカル Instantiate し、MapManifest が配布用 payload を作る。
    /// </summary>
    public sealed class MapLayout
    {
        private readonly List<PlacedModule> _modules = new List<PlacedModule>();

        public MapLayout(ulong seed, ModuleCatalog catalog)
        {
            Seed = seed;
            Catalog = catalog;
        }

        public ulong Seed { get; }
        public ModuleCatalog Catalog { get; }
        public IReadOnlyList<PlacedModule> Modules => _modules;
        public int Count => _modules.Count;

        internal void Add(PlacedModule module) => _modules.Add(module);

        /// <summary>占有セル → そのセルを持つモジュール index の対応表。連結検証や衝突判定に使う。</summary>
        public Dictionary<Vector3Int, int> BuildOccupancy()
        {
            var occupancy = new Dictionary<Vector3Int, int>();
            for (int i = 0; i < _modules.Count; i++)
            {
                foreach (Vector3Int cell in _modules[i].WorldFootprint(Catalog))
                {
                    occupancy[cell] = i;
                }
            }
            return occupancy;
        }
    }
}
