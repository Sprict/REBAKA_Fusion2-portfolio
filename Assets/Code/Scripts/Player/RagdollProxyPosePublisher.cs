using System;
using UnityEngine;

namespace Rebaka.Player
{
    /// <summary>
    /// 1回の発行で <see cref="RagdollController"/> の Networked 状態へ渡す、
    /// Root・頭・左右の手の姿勢と速度をまとめた通常の C# データ。
    /// </summary>
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

    /// <summary>
    /// State Authority側でシミュレーションしたラグドールの姿勢を
    /// Networkedプロパティへ発行し、クライアント側の表示・補間に利用できる状態として同期する。
    /// </summary>
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

        /// <summary>
        /// 現在の State Authority 側 Rigidbody から姿勢を採取し、
        /// <see cref="IProxyPosePublisherContext.ApplyProxyPoseSnapshot"/> を介して Networked 状態へ発行する。
        /// 呼び出し側の <see cref="RagdollController"/> が、Spawn 直後と物理シミュレーション完了後に実行する。
        /// </summary>
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

            // 各 Rigidbody 参照はインターフェース越しの取得＋Unity のネイティブ null 比較を伴うため、
            // 位置・回転で二度引かずローカルに退避してから読む。
            Rigidbody head = _context.HeadRigidbody;
            snapshot.HeadPosition = head != null ? head.position : snapshot.RootPosition;
            snapshot.HeadRotation = head != null ? head.rotation : snapshot.RootRotation;

            Rigidbody leftHand = _context.LeftHandRigidbody;
            snapshot.LeftHandPosition = leftHand != null ? leftHand.position : snapshot.RootPosition;
            snapshot.LeftHandRotation = leftHand != null ? leftHand.rotation : snapshot.RootRotation;

            Rigidbody rightHand = _context.RightHandRigidbody;
            snapshot.RightHandPosition = rightHand != null ? rightHand.position : snapshot.RootPosition;
            snapshot.RightHandRotation = rightHand != null ? rightHand.rotation : snapshot.RootRotation;

            // RagdollController 側の実装が、この通常データを Networked プロパティへコピーする橋渡し点。
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
