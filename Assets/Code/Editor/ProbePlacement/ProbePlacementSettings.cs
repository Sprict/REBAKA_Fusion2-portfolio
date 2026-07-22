using System;
using System.Collections.Generic;
using UnityEngine;

namespace MyProject.Tools.ProbePlacement.Editor
{
    [Serializable]
    public class ProbePlacementSettings
    {
        public GameObject root;

        public LayerMask floorMask = ~0;
        public LayerMask wallMask = ~0;

        [Min(0.5f)] public float cellSize = 2.5f;

        public List<float> verticalLayers = new List<float> { 0.2f, 1.6f, 2.8f };

        [Range(1.2f, 2.5f)] public float maxGapMultiplier = 1.8f;

        [Min(0.01f)] public float wallProximityRadius = 0.6f;

        [Min(0.0f)] public float maxFloorHeightJump = 1.0f;

        public bool requireCeiling = true;
        public LayerMask ceilingMask = ~0;
        [Min(0.5f)] public float maxCeilingHeight = 8.0f;
        [Min(0.0f)] public float minCeilingHeight = 0.5f;

        public bool enableReflectionProbes = true;
        public int reflectionProbeResolution = 256;
        [Range(0.3f, 1.0f)] public float smoothnessThreshold = 0.7f;
        public bool reflectionHdr = true;
        [Range(0.0f, 1.0f)] public float reflectionBlendDistance = 0.0f;

        public const string AutoProbePrefix = "[AutoProbe]";
        public const string AutoProbeGroupName = "[AutoProbe] LightProbeGroup";

        public ProbePlacementSettings Clone()
        {
            return new ProbePlacementSettings
            {
                root = this.root,
                floorMask = this.floorMask,
                wallMask = this.wallMask,
                cellSize = this.cellSize,
                verticalLayers = new List<float>(this.verticalLayers),
                maxGapMultiplier = this.maxGapMultiplier,
                wallProximityRadius = this.wallProximityRadius,
                maxFloorHeightJump = this.maxFloorHeightJump,
                requireCeiling = this.requireCeiling,
                ceilingMask = this.ceilingMask,
                maxCeilingHeight = this.maxCeilingHeight,
                minCeilingHeight = this.minCeilingHeight,
                enableReflectionProbes = this.enableReflectionProbes,
                reflectionProbeResolution = this.reflectionProbeResolution,
                smoothnessThreshold = this.smoothnessThreshold,
                reflectionHdr = this.reflectionHdr,
                reflectionBlendDistance = this.reflectionBlendDistance,
            };
        }
    }
}
