// Assets/Code/Scripts/Map/MapPrimitives.cs
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// グリッド上の 4 方位。Y 軸まわり 90° 時計回り（上から見た向き、Unity の正回転方向）に並べてある。
    /// この並びにより「90° 回転 = (dir + rotationSteps) mod 4」「逆向き = (dir + 2) mod 4」で扱える。
    /// 座標は Unity 左手 Y-up・平面 XZ を前提とする。
    /// </summary>
    public enum MapDirection
    {
        North = 0, // +Z
        East = 1,  // +X
        South = 2, // -Z
        West = 3,  // -X
    }

    /// <summary>
    /// 整数グリッド上の離散回転・方位演算をまとめた純粋ヘルパ。
    /// 浮動小数を一切使わないため、シードが同じなら全プラットフォームでビット同一の結果になる
    /// （devlog 2026-06-27 §9「離散配置厳守」ガードの実体）。
    /// </summary>
    public static class GridRotation
    {
        /// <summary>回転ステップ数（0..3）。範囲外も mod 4 で正規化する。</summary>
        public static int Normalize(int rotationSteps)
        {
            int r = rotationSteps % 4;
            return r < 0 ? r + 4 : r;
        }

        /// <summary>
        /// セルオフセットを Y 軸まわりに 90°×steps 回転する。
        /// 1 ステップ = 上から見て時計回り = (x, z) → (z, -x)。Y は不変。
        /// </summary>
        public static Vector3Int RotateCell(Vector3Int cell, int rotationSteps)
        {
            int steps = Normalize(rotationSteps);
            int x = cell.x;
            int z = cell.z;
            for (int i = 0; i < steps; i++)
            {
                int nx = z;
                int nz = -x;
                x = nx;
                z = nz;
            }
            return new Vector3Int(x, cell.y, z);
        }

        /// <summary>方位を 90°×steps 回転する。</summary>
        public static MapDirection RotateDirection(MapDirection dir, int rotationSteps)
        {
            int r = Normalize((int)dir + rotationSteps);
            return (MapDirection)r;
        }

        /// <summary>真逆の方位（North↔South, East↔West）。</summary>
        public static MapDirection Opposite(MapDirection dir)
        {
            return (MapDirection)Normalize((int)dir + 2);
        }

        /// <summary>方位の単位セルベクトル。N=+Z, E=+X, S=-Z, W=-X。</summary>
        public static Vector3Int ToVector(MapDirection dir)
        {
            switch (dir)
            {
                case MapDirection.North: return new Vector3Int(0, 0, 1);
                case MapDirection.East: return new Vector3Int(1, 0, 0);
                case MapDirection.South: return new Vector3Int(0, 0, -1);
                case MapDirection.West: return new Vector3Int(-1, 0, 0);
                default: return Vector3Int.zero;
            }
        }

        /// <summary>回転ステップ数を度（0/90/180/270）に変換する。プレハブ Instantiate 時の Quaternion 用。</summary>
        public static float ToDegrees(int rotationSteps)
        {
            return Normalize(rotationSteps) * 90f;
        }
    }

    /// <summary>
    /// 辺の通行プロファイル（devlog 2026-06-27 §6「辺に敵サイズ別の通行プロファイル」）。
    /// MVP では「通行に必要な最小空き幅（セル）」だけを持つ。敵半径でパス探索をフィルタするのに使う
    /// （大きい敵は狭い戸口を通れない）。将来は段差高さ・水/溶岩などの属性を足す拡張余地を残す。
    /// </summary>
    public readonly struct TraversalProfile
    {
        /// <summary>通行に必要な最小空き幅（セル単位）。1 以上。</summary>
        public readonly int Clearance;

        public TraversalProfile(int clearance)
        {
            Clearance = clearance < 1 ? 1 : clearance;
        }

        /// <summary>標準プロファイル（clearance = 1）。</summary>
        public static TraversalProfile Default => new TraversalProfile(1);

        /// <summary>2 つのプロファイルの厳しい方（clearance の小さい方）を返す。戸口の実効幅計算に使う。</summary>
        public TraversalProfile Tighter(TraversalProfile other)
        {
            return new TraversalProfile(Clearance < other.Clearance ? Clearance : other.Clearance);
        }
    }

    /// <summary>
    /// モジュール内のパスノード間を結ぶ辺（オーサリング側・ノード index はモジュールローカル）。
    /// 配置後にワールドのパスグラフへ展開され、モジュール内の通り道を表す（devlog §6 N1 埋め込みパスグラフ）。
    /// </summary>
    public readonly struct ModulePathEdge
    {
        /// <summary>モジュールローカルのパスノード index。</summary>
        public readonly int NodeA;
        public readonly int NodeB;

        /// <summary>この通り道の通行プロファイル。</summary>
        public readonly TraversalProfile Profile;

        public ModulePathEdge(int nodeA, int nodeB, TraversalProfile profile)
        {
            NodeA = nodeA;
            NodeB = nodeB;
            Profile = profile;
        }

        public ModulePathEdge(int nodeA, int nodeB)
            : this(nodeA, nodeB, TraversalProfile.Default)
        {
        }
    }

    /// <summary>
    /// モジュールの接続口。モジュールローカル座標で定義し、配置時に回転＋平行移動してワールドへ写す。
    /// facing は「この口からモジュールの外へ出る向き」。channel は接続種別（MVP は 0 単一で運用）。
    /// </summary>
    public readonly struct MapSocket
    {
        /// <summary>モジュールローカルのセル座標（このセルの外縁に口がある）。</summary>
        public readonly Vector3Int LocalCell;

        /// <summary>外向きの向き（ローカル）。</summary>
        public readonly MapDirection Facing;

        /// <summary>接続種別。異なる channel 同士は接続できない（鍵ゲート等の拡張余地）。</summary>
        public readonly int Channel;

        /// <summary>この戸口の通行幅（セル）。隣接モジュールと噛み合った辺の clearance に効く。1 以上。</summary>
        public readonly int Clearance;

        public MapSocket(Vector3Int localCell, MapDirection facing, int channel = 0, int clearance = 1)
        {
            LocalCell = localCell;
            Facing = facing;
            Channel = channel;
            Clearance = clearance < 1 ? 1 : clearance;
        }
    }

    /// <summary>ワールドへ写したソケット（配置後の実座標・実方位）。接続判定に使う。</summary>
    public readonly struct WorldSocket
    {
        public readonly Vector3Int Cell;
        public readonly MapDirection Facing;
        public readonly int Channel;
        public readonly int Clearance;

        public WorldSocket(Vector3Int cell, MapDirection facing, int channel, int clearance = 1)
        {
            Cell = cell;
            Facing = facing;
            Channel = channel;
            Clearance = clearance < 1 ? 1 : clearance;
        }

        /// <summary>この口が接続を求める「隣のセル」（口の外側の 1 マス）。</summary>
        public Vector3Int NeighborCell => Cell + GridRotation.ToVector(Facing);
    }
}
