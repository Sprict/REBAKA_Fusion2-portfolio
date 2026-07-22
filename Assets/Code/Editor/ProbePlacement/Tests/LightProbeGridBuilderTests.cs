using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor.Tests
{
    public class LightProbeGridBuilderTests
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

        private GameObject MakeBox(string name, Vector3 pos, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = scale;
            spawned.Add(go);
            return go;
        }

        [Test]
        public void OpenRoom_GeneratesProbesAcrossAllLayers()
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
                verticalLayers = new List<float> { 0.2f, 1.6f, 2.8f },
                maxGapMultiplier = 1.8f,
                wallProximityRadius = 0.6f,
                requireCeiling = false,
            };

            var det = IndoorVolumeDetector.Detect(settings);
            var probes = LightProbeGridBuilder.Build(det, settings);

            Assert.Greater(probes.Length, 0);
            // Upper bound: cells * layers
            Assert.LessOrEqual(probes.Length, det.cells.Length * settings.verticalLayers.Count);
        }

        [Test]
        public void LargerCellSize_ProducesFewerProbes()
        {
            var root = new GameObject("Root");
            spawned.Add(root);
            var floor = MakeBox("Floor", Vector3.zero, new Vector3(20, 0.1f, 20));
            floor.transform.parent = root.transform;

            var s1 = new ProbePlacementSettings
            {
                root = root,
                cellSize = 2.0f,
                floorMask = ~0,
                wallMask = 0,
                verticalLayers = new List<float> { 1.5f },
                requireCeiling = false,
            };
            var s2 = new ProbePlacementSettings
            {
                root = root,
                cellSize = 4.0f,
                floorMask = ~0,
                wallMask = 0,
                verticalLayers = new List<float> { 1.5f },
                requireCeiling = false,
            };

            var p1 = LightProbeGridBuilder.Build(IndoorVolumeDetector.Detect(s1), s1);
            var p2 = LightProbeGridBuilder.Build(IndoorVolumeDetector.Detect(s2), s2);

            Assert.Greater(p1.Length, p2.Length);
        }

        [Test]
        public void EmptyDetection_ReturnsEmptyArray()
        {
            var det = new DetectionResult { cells = System.Array.Empty<IndoorCell>() };
            var settings = new ProbePlacementSettings();
            var probes = LightProbeGridBuilder.Build(det, settings);
            Assert.AreEqual(0, probes.Length);
        }
    }
}
