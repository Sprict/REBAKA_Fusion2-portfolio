using System;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    internal struct ProxyPoseSnapshotData
    {
        public Vector3 RootPosition;
        public Quaternion RootRotation;
        public Vector3 RootLinearVelocity;
        public Vector3 RootAngularVelocity;
        public Vector3 HeadPosition;
        public Quaternion HeadRotation;
        public Vector3 LeftHandPosition;
        public Quaternion LeftHandRotation;
        public Vector3 RightHandPosition;
        public Quaternion RightHandRotation;
        public bool IsInitialized;
    }

    internal sealed class RagdollProxyPosePublisher
    {
        private readonly IProxyPosePublisherContext _context;

        // テレポート自動検出用: 前回発行時の Root 位置
        private Vector3 _lastPublishedRootPosition;
        private bool _hasLastPublishedRootPosition;

        public RagdollProxyPosePublisher(IProxyPosePublisherContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Publish()
        {
            _context.EnsureProxyBodyReferences();
            Rigidbody root = _context.RootRigidbody;
            if (root == null)
            {
                return;
            }

            DetectTeleport(root.position);

            ProxyPoseSnapshotData snapshot = new ProxyPoseSnapshotData
            {
                RootPosition = root.position,
                RootRotation = root.rotation,
                RootLinearVelocity = root.linearVelocity,
                RootAngularVelocity = root.angularVelocity,
                IsInitialized = true
            };

            snapshot.HeadPosition = _context.HeadRigidbody != null
                ? _context.HeadRigidbody.position
                : snapshot.RootPosition;
            snapshot.HeadRotation = _context.HeadRigidbody != null
                ? _context.HeadRigidbody.rotation
                : snapshot.RootRotation;

            snapshot.LeftHandPosition = _context.LeftHandRigidbody != null
                ? _context.LeftHandRigidbody.position
                : snapshot.RootPosition;
            snapshot.LeftHandRotation = _context.LeftHandRigidbody != null
                ? _context.LeftHandRigidbody.rotation
                : snapshot.RootRotation;

            snapshot.RightHandPosition = _context.RightHandRigidbody != null
                ? _context.RightHandRigidbody.position
                : snapshot.RootPosition;
            snapshot.RightHandRotation = _context.RightHandRigidbody != null
                ? _context.RightHandRigidbody.rotation
                : snapshot.RootRotation;

            _context.ApplyProxyPoseSnapshot(snapshot);

            if (_context.PublishFullPose)
            {
                PublishRelativePartPoses(root);
            }

            _context.RecordHostGroundTruthSample(snapshot.RootPosition, snapshot.RootLinearVelocity);
        }

        /// <summary>
        /// 1 tick で閾値を超える Root 移動をテレポートとみなし、TeleportKey をインクリメントする。
        /// MyRespawn 等の明示呼び出し（RequestPoseTeleport）のフォールバックとして機能する。
        /// </summary>
        private void DetectTeleport(Vector3 currentRootPosition)
        {
            if (_hasLastPublishedRootPosition)
            {
                float movedDistance = Vector3.Distance(currentRootPosition, _lastPublishedRootPosition);
                if (movedDistance > _context.PoseTeleportDetectThreshold)
                {
                    _context.IncrementPoseTeleportKey();
                }
            }

            _lastPublishedRootPosition = currentRootPosition;
            _hasLastPublishedRootPosition = true;
        }

        /// <summary>
        /// bodyRigidbodies[1..14] の Root 相対ポーズを NetworkArray スロット 0..13 へ発行する。
        /// 相対表現にすることで Root の移動・回転と分離され、補間時の合成が安定する。
        /// </summary>
        private void PublishRelativePartPoses(Rigidbody root)
        {
            Quaternion inverseRootRotation = Quaternion.Inverse(root.rotation);
            Vector3 rootPosition = root.position;

            for (int slot = 0; slot < RagdollPoseSync.RelativePartCount; slot++)
            {
                int bodyIndex = slot + RagdollPoseSync.FirstRelativePartIndex;
                Rigidbody part = _context.GetBodyRigidbody(bodyIndex);
                if (part == null)
                {
                    _context.ApplyPartPose(slot, Vector3.zero, Quaternion.identity);
                    continue;
                }

                Vector3 relativePosition = inverseRootRotation * (part.position - rootPosition);
                Quaternion relativeRotation = inverseRootRotation * part.rotation;
                _context.ApplyPartPose(slot, relativePosition, relativeRotation);
            }
        }
    }
}
