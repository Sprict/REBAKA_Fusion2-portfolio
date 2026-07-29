// Assets/Code/Tests/EditMode/Map/GridRotationTests.cs
using NUnit.Framework;
using UnityEngine;
using MyFolder.Scripts.Map;

namespace MyFolder.Scripts.Tests.Map
{
    /// <summary>
    /// 配置幾何の土台。ソケット噛み合わせは「方位の回転」と「セルベクトルの回転」が一致することに依存する。
    /// この不変条件が崩れると、回転したモジュールが隣接セルでつながらず連結が壊れる。
    /// </summary>
    public class GridRotationTests
    {
        [Test]
        public void Normalize_WrapsNegativeAndLarge()
        {
            Assert.That(GridRotation.Normalize(-1), Is.EqualTo(3));
            Assert.That(GridRotation.Normalize(4), Is.EqualTo(0));
            Assert.That(GridRotation.Normalize(7), Is.EqualTo(3));
        }

        [Test]
        public void RotateCell_NinetyCw_PlusXBecomesMinusZ()
        {
            // 上から見て時計回り（Unity の Y 正回転）: +X → -Z。
            Vector3Int r = GridRotation.RotateCell(new Vector3Int(1, 0, 0), 1);
            Assert.That(r, Is.EqualTo(new Vector3Int(0, 0, -1)));
        }

        [Test]
        public void RotateCell_FourSteps_IsIdentity()
        {
            var cell = new Vector3Int(2, 1, -3);
            Assert.That(GridRotation.RotateCell(cell, 4), Is.EqualTo(cell));
        }

        [Test]
        public void RotateCell_PreservesY()
        {
            Vector3Int r = GridRotation.RotateCell(new Vector3Int(1, 5, 0), 2);
            Assert.That(r.y, Is.EqualTo(5));
        }

        [Test]
        public void Opposite_IsPlusTwo()
        {
            Assert.That(GridRotation.Opposite(MapDirection.North), Is.EqualTo(MapDirection.South));
            Assert.That(GridRotation.Opposite(MapDirection.East), Is.EqualTo(MapDirection.West));
            Assert.That(GridRotation.Opposite(MapDirection.South), Is.EqualTo(MapDirection.North));
            Assert.That(GridRotation.Opposite(MapDirection.West), Is.EqualTo(MapDirection.East));
        }

        [Test]
        public void DirectionAndVectorRotation_AreConsistent()
        {
            // 核心不変条件: ToVector(Rotate(dir, k)) == Rotate(ToVector(dir), k)。
            // これが成り立つから「ソケットの向き」と「ソケットの位置」が同じ回転で動き、噛み合う。
            foreach (MapDirection dir in System.Enum.GetValues(typeof(MapDirection)))
            {
                for (int k = 0; k < 4; k++)
                {
                    Vector3Int viaDirection = GridRotation.ToVector(GridRotation.RotateDirection(dir, k));
                    Vector3Int viaVector = GridRotation.RotateCell(GridRotation.ToVector(dir), k);
                    Assert.That(viaDirection, Is.EqualTo(viaVector),
                        $"dir={dir}, k={k} で方位回転とベクトル回転が不一致");
                }
            }
        }
    }
}
