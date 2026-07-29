using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor.Tests
{
    public class IndoorVolumeDetectorTests
    {
        private List<GameObject> spawned;

        [SetUp]
        public void SetUp()
        {
            spawned = new List<GameObject>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in spawned)
                if (go != null) Object.DestroyImmediate(go);
            spawned.Clear();
        }

        private GameObject MakeBox(string name, Vector3 pos, Vector3 scale, int layer = 0)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            go.layer = layer;
            spawned.Add(go);
            return go;
        }

        [Test]
        public void Diagnostic_PhysicsRaycastWorksInEditMode()
        {
            var floor = MakeBox("DiagFloor", Vector3.zero, new Vector3(10, 0.1f, 10));
            Physics.SyncTransforms();
            bool hit = Physics.Raycast(new Vector3(0, 5, 0), Vector3.down, out var hitInfo, 10f, ~0, QueryTriggerInteraction.Ignore);
            Assert.IsTrue(hit, $"Raycast did not hit. floor bounds={floor.GetComponent<Collider>().bounds}");
            Assert.That(hitInfo.point.y, Is.EqualTo(0.05f).Within(0.01f));
        }

        [Test]
        public void SingleRoom_ReturnsOneRoom()
        {
            var root = new GameObject("Root");
            spawned.Add(root);

            var floor = MakeBox("Floor", new Vector3(0, 0, 0), new Vector3(10, 0.1f, 10));
            floor.transform.parent = root.transform;

            var settings = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.5f,
                floorMask = ~0,
                wallMask = 0,
                requireCeiling = false,
            };

            var result = IndoorVolumeDetector.Detect(settings);

            Assert.AreEqual(1, result.roomCount, "Should detect exactly 1 room");
            Assert.GreaterOrEqual(result.cells.Length, 9, "Expect ~16 cells for 10x10 @2.5m grid");
            Assert.LessOrEqual(result.cells.Length, 25);
        }

        [Test]
        public void NoFloor_ReturnsEmpty()
        {
            var root = new GameObject("Root");
            spawned.Add(root);

            var settings = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.5f,
                floorMask = ~0,
                wallMask = 0,
                requireCeiling = false,
            };

            var result = IndoorVolumeDetector.Detect(settings);
            Assert.AreEqual(0, result.roomCount);
            Assert.AreEqual(0, result.cells.Length);
        }

        [Test]
        public void OutdoorFloor_IsExcludedWhenRequireCeilingEnabled()
        {
            var root = new GameObject("Root");
            spawned.Add(root);

            // 屋内: 床 + 天井 (両方ともデフォルトレイヤー)
            var indoorFloor = MakeBox("IndoorFloor", new Vector3(0, 0, 0), new Vector3(10, 0.1f, 10));
            indoorFloor.transform.parent = root.transform;
            var indoorCeiling = MakeBox("IndoorCeiling", new Vector3(0, 3f, 0), new Vector3(10, 0.1f, 10));
            indoorCeiling.transform.parent = root.transform;

            // 屋外: 床だけ、天井なし (同じ AABB から 20m 離れた場所)
            var outdoorFloor = MakeBox("OutdoorFloor", new Vector3(30, 0, 0), new Vector3(10, 0.1f, 10));
            outdoorFloor.transform.parent = root.transform;

            var settings = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.5f,
                floorMask = ~0,
                wallMask = ~0,
                ceilingMask = ~0,
                requireCeiling = true,
                minCeilingHeight = 0.5f,
                maxCeilingHeight = 8.0f,
            };

            var result = IndoorVolumeDetector.Detect(settings);

            Assert.AreEqual(1, result.roomCount, "天井がある屋内のみ1部屋として検出されるべき");
            foreach (var cell in result.cells)
            {
                Assert.Less(cell.floorPoint.x, 15f,
                    "屋外 (x=30付近) の床はセルに含まれてはならない");
            }
        }

        [Test]
        public void OutdoorFloor_IsIncludedWhenRequireCeilingDisabled()
        {
            var root = new GameObject("Root");
            spawned.Add(root);

            var floor = MakeBox("Floor", Vector3.zero, new Vector3(10, 0.1f, 10));
            floor.transform.parent = root.transform;

            var settings = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.5f,
                floorMask = ~0,
                wallMask = 0,
                ceilingMask = ~0,
                requireCeiling = false,
            };

            var result = IndoorVolumeDetector.Detect(settings);
            Assert.Greater(result.cells.Length, 0, "天井要件を切れば天井なしでもセルが生成されるべき");
        }

        [Test]
        public void CeilingOnSameLayerAsFloor_IsDetectedAsIndoor()
        {
            // ユーザーシーン再現: 天井用メッシュも Floor Layer 上に置かれているケース。
            // 単純な Physics.Raycast (first hit) だと天井を床として拾ってしまい、
            // 天井上に天井がないため屋外判定されて 0 セルになる問題の回帰テスト。
            var root = new GameObject("Root");
            spawned.Add(root);

            var floor = MakeBox("Floor", new Vector3(0, 0, 0), new Vector3(10, 0.1f, 10));
            floor.transform.parent = root.transform;
            // 天井も同じデフォルトレイヤー = floorMask に含まれる
            var ceiling = MakeBox("Ceiling_SameLayer", new Vector3(0, 3f, 0), new Vector3(10, 0.1f, 10));
            ceiling.transform.parent = root.transform;

            var settings = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.5f,
                floorMask = ~0,
                wallMask = 0,     // 壁なし
                ceilingMask = 0,  // ceiling レイヤーも未指定
                requireCeiling = true,
                minCeilingHeight = 0.5f,
                maxCeilingHeight = 8.0f,
            };

            var result = IndoorVolumeDetector.Detect(settings);

            Assert.GreaterOrEqual(result.cells.Length, 9,
                "床と同じレイヤー上の天井でも RaycastAll で検出されて屋内判定されるべき");
            foreach (var cell in result.cells)
            {
                Assert.That(cell.floorPoint.y, Is.EqualTo(0.05f).Within(0.02f),
                    "拾う床は最下段 (y≈0.05) であるべきで、天井 (y≈2.95) を誤って床にしてはいけない");
            }
        }

        [Test]
        public void TwoRoomsSplitByWall_ReturnsTwoRooms()
        {
            var root = new GameObject("Root");
            spawned.Add(root);

            // Floor spanning 20x10 (so the wall visibly subdivides)
            var floor = MakeBox("Floor", new Vector3(0, 0, 0), new Vector3(20, 0.1f, 10));
            floor.transform.parent = root.transform;

            // Vertical wall down the middle (x=0), full height, full depth, no door
            var wall = MakeBox("Wall", new Vector3(0, 1.5f, 0), new Vector3(0.3f, 3f, 10));
            wall.transform.parent = root.transform;

            var settings = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.5f,
                floorMask = ~0,
                wallMask = ~0,
                requireCeiling = false,
            };

            var result = IndoorVolumeDetector.Detect(settings);

            Assert.AreEqual(2, result.roomCount, "Two rooms separated by a full wall");
        }
    }
}
