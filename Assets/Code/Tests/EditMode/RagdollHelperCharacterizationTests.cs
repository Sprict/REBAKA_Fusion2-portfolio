using System.Collections.Generic;
using MyFolder.Scripts.Player;
using NUnit.Framework;
using UnityEngine;

namespace MyFolder.Tests.EditMode
{
    public sealed class RagdollHelperCharacterizationTests
    {
        private readonly List<GameObject> _gameObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _gameObjects.Count; i++)
            {
                if (_gameObjects[i] != null)
                {
                    Object.DestroyImmediate(_gameObjects[i]);
                }
            }

            _gameObjects.Clear();
        }

        [Test]
        public void GroundingService_RequestsRecover_OnlyWhenBothFeetGroundedInRagdoll()
        {
            var service = new RagdollGroundingService();

            RagdollGroundingUpdate update = service.Apply(
                isLeftFoot: true,
                isGrounded: true,
                currentLeftFootGrounded: false,
                currentRightFootGrounded: true,
                currentState: PlayerState.Ragdoll,
                autoGetUpWhenPossible: true);

            Assert.That(update.LeftFootGrounded, Is.True);
            Assert.That(update.RightFootGrounded, Is.True);
            Assert.That(update.AnyFootGrounded, Is.True);
            Assert.That(update.ShouldAttemptRecover, Is.True);
        }

        [Test]
        public void ClientBootstrapper_UsesInputAuthorityRule_ToForceRemoteRender()
        {
            var strategy = new RecordingClientProxyModeStrategy();
            var context = new RecordingClientBootstrapContext
            {
                HasInputAuthorityValue = true,
                HasStateAuthorityValue = false,
                ForceRemoteForAllClientProxiesValue = false,
                ForceRemoteForInputAuthorityOnClientValue = true,
                Strategy = strategy
            };

            var bootstrapper = new RagdollClientBootstrapper(context);
            bootstrapper.Initialize();

            Assert.That(context.LastForceRemoteRenderTimeframe, Is.True);
            Assert.That(strategy.ApplyCount, Is.EqualTo(1));
            Assert.That(context.LogKeys, Does.Contain("render_mode"));
        }

        [Test]
        public void DebugView_ResolvesGuiVisibility_FromToggleInput()
        {
            Assert.That(RagdollDebugView.ResolveGuiVisibility(currentVisible: true, togglePressedThisFrame: true), Is.False);
            Assert.That(RagdollDebugView.ResolveGuiVisibility(currentVisible: false, togglePressedThisFrame: true), Is.True);
            Assert.That(RagdollDebugView.ResolveGuiVisibility(currentVisible: true, togglePressedThisFrame: false), Is.True);
            Assert.That(RagdollDebugView.ResolveGuiVisibility(currentVisible: false, togglePressedThisFrame: false), Is.False);
        }

        [Test]
        public void ProxyPosePublisher_CopiesBodyPoseIntoSnapshot_AndRecordsHostGroundTruth()
        {
            GameObject rootObject = CreateGameObject("Root");
            GameObject headObject = CreateGameObject("Head");
            GameObject leftHandObject = CreateGameObject("LeftHand");
            GameObject rightHandObject = CreateGameObject("RightHand");

            Rigidbody root = rootObject.AddComponent<Rigidbody>();
            Rigidbody head = headObject.AddComponent<Rigidbody>();
            Rigidbody leftHand = leftHandObject.AddComponent<Rigidbody>();
            Rigidbody rightHand = rightHandObject.AddComponent<Rigidbody>();

            root.position = new Vector3(1f, 2f, 3f);
            root.rotation = Quaternion.Euler(10f, 20f, 30f);
            root.linearVelocity = new Vector3(4f, 5f, 6f);
            root.angularVelocity = new Vector3(0.1f, 0.2f, 0.3f);
            head.position = new Vector3(7f, 8f, 9f);
            leftHand.position = new Vector3(-1f, -2f, -3f);
            rightHand.position = new Vector3(11f, 12f, 13f);

            var context = new RecordingProxyPosePublisherContext
            {
                Root = root,
                Head = head,
                LeftHand = leftHand,
                RightHand = rightHand
            };

            var publisher = new RagdollProxyPosePublisher(context);
            publisher.Publish();

            Assert.That(context.EnsureCalls, Is.EqualTo(1));
            Assert.That(context.LastSnapshot.IsInitialized, Is.True);
            Assert.That(context.LastSnapshot.RootPosition, Is.EqualTo(root.position));
            Assert.That(context.LastSnapshot.HeadPosition, Is.EqualTo(head.position));
            Assert.That(context.LastSnapshot.LeftHandPosition, Is.EqualTo(leftHand.position));
            Assert.That(context.LastSnapshot.RightHandPosition, Is.EqualTo(rightHand.position));
            Assert.That(context.RecordedRootPosition, Is.EqualTo(root.position));
            Assert.That(context.RecordedRootVelocity, Is.EqualTo(root.linearVelocity));
        }

        [Test]
        public void ProxyPosePublisher_PublishesRootRelativePartPoses_WhenFullPoseEnabled()
        {
            Rigidbody root = CreateRigidbody("Root");
            root.position = new Vector3(10f, 0f, 0f);
            root.rotation = Quaternion.Euler(0f, 90f, 0f);

            // bodyRigidbodies[1] 相当: Root の前方 1m（ワールドでは +X 方向が Root のローカル +Z）
            Rigidbody part = CreateRigidbody("Part1");
            part.position = new Vector3(11f, 0f, 0f);
            part.rotation = Quaternion.Euler(0f, 90f, 0f);

            var context = new RecordingProxyPosePublisherContext
            {
                Root = root,
                PublishFullPoseValue = true
            };
            context.BodyRigidbodiesByIndex[1] = part;

            var publisher = new RagdollProxyPosePublisher(context);
            publisher.Publish();

            // 全14スロットが毎回発行される（欠損パーツは identity フォールバック）
            Assert.That(context.AppliedPartPoses.Count, Is.EqualTo(14));

            (int slot, Vector3 relPos, Quaternion relRot) = context.AppliedPartPoses[0];
            Assert.That(slot, Is.EqualTo(0));
            // relPos = inv(rootRot) * (part.pos - root.pos) = ローカル +Z 1m
            Assert.That(Vector3.Distance(relPos, new Vector3(0f, 0f, 1f)), Is.LessThan(1e-4f));
            // relRot = inv(rootRot) * part.rot = identity
            Assert.That(Quaternion.Angle(relRot, Quaternion.identity), Is.LessThan(0.1f));

            // 未登録のスロットは zero/identity
            (_, Vector3 missingPos, Quaternion missingRot) = context.AppliedPartPoses[1];
            Assert.That(missingPos, Is.EqualTo(Vector3.zero));
            Assert.That(Quaternion.Angle(missingRot, Quaternion.identity), Is.LessThan(0.1f));
        }

        [Test]
        public void ProxyPosePublisher_SkipsPartPoses_WhenFullPoseDisabled()
        {
            Rigidbody root = CreateRigidbody("Root");

            var context = new RecordingProxyPosePublisherContext
            {
                Root = root,
                PublishFullPoseValue = false
            };

            var publisher = new RagdollProxyPosePublisher(context);
            publisher.Publish();

            Assert.That(context.AppliedPartPoses, Is.Empty);
        }

        [Test]
        public void ProxyPosePublisher_IncrementsTeleportKey_OnlyWhenRootJumpsBeyondThreshold()
        {
            Rigidbody root = CreateRigidbody("Root");
            root.position = Vector3.zero;

            var context = new RecordingProxyPosePublisherContext
            {
                Root = root,
                PoseTeleportDetectThresholdValue = 2f
            };

            var publisher = new RagdollProxyPosePublisher(context);

            publisher.Publish();                      // 初回: 基準位置の記録のみ
            Assert.That(context.TeleportKeyIncrements, Is.EqualTo(0));

            root.position = new Vector3(1f, 0f, 0f);  // 閾値以下の通常移動
            publisher.Publish();
            Assert.That(context.TeleportKeyIncrements, Is.EqualTo(0));

            root.position = new Vector3(10f, 0f, 0f); // 閾値超の瞬間移動
            publisher.Publish();
            Assert.That(context.TeleportKeyIncrements, Is.EqualTo(1));
        }

        private GameObject CreateGameObject(string name)
        {
            var gameObject = new GameObject(name);
            _gameObjects.Add(gameObject);
            return gameObject;
        }

        private Rigidbody CreateRigidbody(string name)
        {
            return CreateGameObject(name).AddComponent<Rigidbody>();
        }

        private sealed class RecordingClientBootstrapContext : IClientBootstrapContext
        {
            public bool HasInputAuthorityValue;
            public bool HasStateAuthorityValue;
            public bool ForceRemoteForAllClientProxiesValue;
            public bool ForceRemoteForInputAuthorityOnClientValue;
            public RecordingClientProxyModeStrategy Strategy;
            public bool LastForceRemoteRenderTimeframe;
            public readonly List<string> LogKeys = new List<string>();

            public bool HasInputAuthority => HasInputAuthorityValue;
            public bool HasStateAuthority => HasStateAuthorityValue;
            public bool ForceRemoteForAllClientProxies => ForceRemoteForAllClientProxiesValue;
            public bool ForceRemoteForInputAuthorityOnClient => ForceRemoteForInputAuthorityOnClientValue;
            public bool UseHybridProxySimulation => true;
            public int InstanceId => 99;

            public void SetForceRemoteRenderTimeframe(bool value)
            {
                LastForceRemoteRenderTimeframe = value;
            }

            public IClientProxyModeStrategy CreateClientProxyModeStrategy()
            {
                return Strategy;
            }

            public void LogClientBootstrap(string key, string message, float throttle, string dedupeKey = null)
            {
                LogKeys.Add(key);
            }

            public void LogClientDebug(string message)
            {
            }

            public void LogClientWarning(string message)
            {
            }
        }

        private sealed class RecordingClientProxyModeStrategy : IClientProxyModeStrategy
        {
            public int ApplyCount { get; private set; }

            public void Apply()
            {
                ApplyCount++;
            }
        }

        private sealed class RecordingProxyPosePublisherContext : IProxyPosePublisherContext
        {
            public int EnsureCalls { get; private set; }
            public Rigidbody Root { get; set; }
            public Rigidbody Head { get; set; }
            public Rigidbody LeftHand { get; set; }
            public Rigidbody RightHand { get; set; }
            public ProxyPoseSnapshotData LastSnapshot { get; private set; }
            public Vector3 RecordedRootPosition { get; private set; }
            public Vector3 RecordedRootVelocity { get; private set; }

            // SnapshotInterpolation モード用の記録フィールド
            public bool PublishFullPoseValue;
            public float PoseTeleportDetectThresholdValue = 2f;
            public readonly Dictionary<int, Rigidbody> BodyRigidbodiesByIndex = new Dictionary<int, Rigidbody>();
            public readonly List<(int slot, Vector3 position, Quaternion rotation)> AppliedPartPoses =
                new List<(int, Vector3, Quaternion)>();
            public int TeleportKeyIncrements { get; private set; }

            public void EnsureProxyBodyReferences()
            {
                EnsureCalls++;
            }

            public Rigidbody RootRigidbody => Root;
            public Rigidbody HeadRigidbody => Head;
            public Rigidbody LeftHandRigidbody => LeftHand;
            public Rigidbody RightHandRigidbody => RightHand;
            public bool PublishFullPose => PublishFullPoseValue;
            public float PoseTeleportDetectThreshold => PoseTeleportDetectThresholdValue;

            public Rigidbody GetBodyRigidbody(int index)
            {
                return BodyRigidbodiesByIndex.TryGetValue(index, out Rigidbody rb) ? rb : null;
            }

            public void ApplyPartPose(int slot, Vector3 relativePosition, Quaternion relativeRotation)
            {
                AppliedPartPoses.Add((slot, relativePosition, relativeRotation));
            }

            public void IncrementPoseTeleportKey()
            {
                TeleportKeyIncrements++;
            }

            public void ApplyProxyPoseSnapshot(ProxyPoseSnapshotData snapshot)
            {
                LastSnapshot = snapshot;
            }

            public void RecordHostGroundTruthSample(Vector3 actualRootPosition, Vector3 actualRootVelocity)
            {
                RecordedRootPosition = actualRootPosition;
                RecordedRootVelocity = actualRootVelocity;
            }
        }
    }
}
