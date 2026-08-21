using System.Reflection;
using Rebaka.Player;
using Rebaka.Network;
using Rebaka.Player.Posing;
using Rebaka.Camera;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Rebaka.Tests.EditMode
{
    public sealed class RagdollProfileTuningTests
    {
        [Test]
        public void NewProfile_ExposesRagdollPhysicsTuningDefaults()
        {
            var profile = ScriptableObject.CreateInstance<RagdollProfile>();

            try
            {
                Assert.That(profile.reachUpperArmJointSpring, Is.EqualTo(2000f));
                Assert.That(profile.reachUpperArmJointDamper, Is.EqualTo(200f));
                Assert.That(profile.reachLowerArmJointSpring, Is.EqualTo(2000f));
                Assert.That(profile.reachLowerArmJointDamper, Is.EqualTo(200f));
                Assert.That(GetProfileFloat(profile, "reachUpperArmJointMaxForce"), Is.EqualTo(1000f));
                Assert.That(GetProfileFloat(profile, "reachLowerArmJointMaxForce"), Is.EqualTo(1000f));
                Assert.That(profile.ragdollDriveOffSpring, Is.EqualTo(25f));
                Assert.That(profile.ragdollDriveOffDamper, Is.EqualTo(5f));
                Assert.That(profile.movementVelocityLerp, Is.EqualTo(0.8f));
                Assert.That(profile.airControlMultiplier, Is.EqualTo(RagdollProfile.DefaultAirControlMultiplier));
                Assert.That(profile.punchImpulse, Is.EqualTo(10f));
                Assert.That(profile.punchRecoveryDelaySeconds, Is.EqualTo(0.15f));
                Assert.That(profile.punchRecoveryLerpSpeed, Is.EqualTo(12f));
                Assert.That(profile.crouchSpeedMultiplier, Is.EqualTo(0.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void MainPlayerProfile_ContainsRagdollPhysicsTuningValues()
        {
            const string assetPath = "Assets/Settings/MainPlayer_AprProfile.asset";
            var profile = AssetDatabase.LoadAssetAtPath<RagdollProfile>(assetPath);

            Assert.That(profile, Is.Not.Null,
                $"{assetPath} をロードできなかった。アセットが移動/削除された可能性がある。");
            Assert.That(profile.bodyRollInputLimitDegrees, Is.EqualTo(60f));
            Assert.That(profile.reachUpperArmJointSpring, Is.GreaterThan(0f));
            Assert.That(profile.reachUpperArmJointDamper, Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.reachLowerArmJointSpring, Is.GreaterThan(0f));
            Assert.That(profile.reachLowerArmJointDamper, Is.GreaterThanOrEqualTo(0f));
            Assert.That(GetProfileFloat(profile, "reachUpperArmJointMaxForce"), Is.GreaterThanOrEqualTo(0f));
            Assert.That(GetProfileFloat(profile, "reachLowerArmJointMaxForce"), Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.ragdollDriveOffSpring, Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.ragdollDriveOffDamper, Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.movementVelocityLerp, Is.InRange(0f, 1f));
            Assert.That(profile.airControlMultiplier, Is.InRange(0f, 1f));
            Assert.That(profile.punchImpulse, Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.punchRecoveryDelaySeconds, Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.punchRecoveryLerpSpeed, Is.GreaterThanOrEqualTo(0f));
            Assert.That(profile.crouchSpeedMultiplier, Is.InRange(0f, 1f));
        }

        /// <summary>
        /// 物理層がチューニング値へ到達できることを守るテスト。
        ///
        /// 2026-08-07 以前は IRagdollPhysicsContext が値ごとに専用プロパティを持っていたため、
        /// interface 上の存在を検査していた。現在はチューニング値を RagdollProfile 経由で
        /// 渡すため、検査対象を「契約が Profile を公開していること」＋
        /// 「Profile に該当フィールドがあること」へ移した。守っている性質は同じ。
        /// </summary>
        [Test]
        public void PhysicsContext_ExposesProfile()
        {
            Assert.That(typeof(IRagdollPhysicsContext).GetProperty("Profile"), Is.Not.Null,
                "物理層はチューニング値を Profile 経由で読む。この経路が消えると全パラメータが届かなくなる。");
        }

        [Test]
        public void Profile_ExposesReachJointMaxForceFields()
        {
            Assert.That(typeof(RagdollProfile).GetField("reachUpperArmJointMaxForce"), Is.Not.Null);
            Assert.That(typeof(RagdollProfile).GetField("reachLowerArmJointMaxForce"), Is.Not.Null);
        }

        [Test]
        public void Profile_ExposesAirControlMultiplier()
        {
            Assert.That(typeof(RagdollProfile).GetField("airControlMultiplier"), Is.Not.Null);
        }

        /// <summary>
        /// MoveSpeed だけは Profile 直読みで代用できない（Dash/Crouch 倍率を掛けた計算値）。
        /// 契約から消すと、コンパイルは通ったままダッシュとしゃがみが無効になる。
        /// </summary>
        [Test]
        public void PhysicsContext_KeepsComputedMoveSpeed()
        {
            Assert.That(typeof(IRagdollPhysicsContext).GetProperty("MoveSpeed"), Is.Not.Null,
                "MoveSpeed は profile.moveSpeed × Dash倍率 × Crouch倍率 の計算値であり、Profile 直読みでは代用できない。");
        }

        [Test]
        public void MovementTargetVelocity_ScalesHorizontalInputByControlMultiplier()
        {
            Vector3 currentVelocity = new Vector3(1f, 7f, -2f);
            Vector3 fullControl = RagdollPhysics.CalculateMovementTargetVelocity(
                currentVelocity,
                Vector3.forward,
                speed: 5f,
                controlMultiplier: 1f);
            Vector3 halfControl = RagdollPhysics.CalculateMovementTargetVelocity(
                currentVelocity,
                Vector3.forward,
                speed: 5f,
                controlMultiplier: 0.5f);
            Vector3 noControl = RagdollPhysics.CalculateMovementTargetVelocity(
                currentVelocity,
                Vector3.forward,
                speed: 5f,
                controlMultiplier: 0f);

            Assert.That(fullControl, Is.EqualTo(new Vector3(0f, 7f, 5f)));
            Assert.That(halfControl, Is.EqualTo(new Vector3(0.5f, 7f, 1.5f)));
            Assert.That(noControl, Is.EqualTo(currentVelocity));
        }

        [Test]
        public void ProcessWalking_ScalesLegStepMotionByMoveInputMagnitude()
        {
            using var fullInput = RagdollPhysicsHarness.Create(new TestRagdollPhysicsContext());
            using var halfInput = RagdollPhysicsHarness.Create(new TestRagdollPhysicsContext());
            fullInput.ArrangeForwardRightStep();
            halfInput.ArrangeForwardRightStep();

            fullInput.ProcessWalking(Vector3.forward, deltaTime: 0.02f);
            halfInput.ProcessWalking(Vector3.forward * 0.5f, deltaTime: 0.02f);

            Assert.That(
                halfInput.UpperRightLegTargetX,
                Is.EqualTo(fullInput.UpperRightLegTargetX * 0.5f).Within(0.0001f));
            Assert.That(
                halfInput.StepRTimer,
                Is.EqualTo(fullInput.StepRTimer * 0.5f).Within(0.0001f));
        }

        [Test]
        public void ApplyReachPose_RebuildsReachDrivesFromCurrentContextValues()
        {
            var context = new TestRagdollPhysicsContext();
            context.Profile.reachUpperArmJointSpring = 1200f;
            context.Profile.reachUpperArmJointDamper = 120f;
            context.Profile.reachUpperArmJointMaxForce = 345f;
            context.Profile.reachLowerArmJointSpring = 1400f;
            context.Profile.reachLowerArmJointDamper = 140f;
            context.Profile.reachLowerArmJointMaxForce = 456f;

            using var harness = RagdollPhysicsHarness.Create(context);

            harness.ApplyReachPose(isRight: true);

            AssertReachDrive(harness.Joints[3].angularXDrive, 1200f, 120f, 345f);
            AssertReachDrive(harness.Joints[4].angularYZDrive, 1400f, 140f, 456f);

            // 途中で Profile の値を変えても、次の ApplyReachPose で反映されることを確認する
            // （Drive をキャッシュせず毎回組み直している、というのがこのテストの本題）
            context.Profile.reachUpperArmJointSpring = 2200f;
            context.Profile.reachUpperArmJointDamper = 220f;
            context.Profile.reachUpperArmJointMaxForce = 789f;
            context.Profile.reachLowerArmJointSpring = 2400f;
            context.Profile.reachLowerArmJointDamper = 240f;
            context.Profile.reachLowerArmJointMaxForce = 890f;

            harness.ApplyReachPose(isRight: true);

            AssertReachDrive(harness.Joints[3].angularXDrive, 2200f, 220f, 789f);
            AssertReachDrive(harness.Joints[4].angularYZDrive, 2400f, 240f, 890f);
        }

        [Test]
        public void NewProfile_ExposesBodyRollInputLimitDefault()
        {
            var profile = ScriptableObject.CreateInstance<RagdollProfile>();

            try
            {
                Assert.That(profile.bodyRollInputLimitDegrees, Is.EqualTo(RagdollProfile.DefaultBodyRollInputLimitDegrees));
                Assert.That(profile.bodyRollInputLimitDegrees, Is.EqualTo(60f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void RagdollInput_PreservesPitchReachAndClampsBodyRoll()
        {
            var input = new NetworkInputData
            {
                bodyDir = new Vector2(0.4f, -0.7f),
                bodyRoll = 999f,
            };
            var handler = new RagdollInput();

            handler.UpdateCurrentCommand(input);

            Assert.That(handler.CurrentCommand.LookDirection.x, Is.EqualTo(0.4f));
            Assert.That(handler.CurrentCommand.LookDirection.y, Is.EqualTo(-0.7f));
            Assert.That(handler.CurrentCommand.BodyRoll, Is.EqualTo(RagdollProfile.DefaultBodyRollInputLimitDegrees));
        }

        [Test]
        public void InputCollector_ReturnsBodyRollToZeroWhenAltIsReleased()
        {
            float released = InputCollector.CalculateBodyRoll(
                currentRoll: 45f,
                lookDeltaX: 10f,
                sensitivity: 0.15f,
                rollLimit: 60f,
                rollModifierPressed: false);

            Assert.That(released, Is.EqualTo(0f));
        }

        [Test]
        public void OrbitCamera_UsesLookXForHorizontalOrbitYaw()
        {
            float yaw = OrbitCamera.CalculateOrbitYawFromLook(
                currentYaw: 10f,
                lookDeltaX: 3f,
                sensitivity: 0.2f,
                invertOrbitX: false);

            Assert.That(yaw, Is.EqualTo(10.6f).Within(0.0001f));
        }

        [Test]
        public void OrbitCamera_ResetsQuietTimeWhenLookXIsActive()
        {
            float moving = OrbitCamera.CalculateLookQuietTime(
                currentQuietTime: 0.5f,
                lookDeltaX: 0.02f,
                deltaTime: 0.016f);
            float still = OrbitCamera.CalculateLookQuietTime(
                currentQuietTime: 0.5f,
                lookDeltaX: 0f,
                deltaTime: 0.016f);

            Assert.That(moving, Is.EqualTo(0f));
            Assert.That(still, Is.EqualTo(0.516f).Within(0.0001f));
        }

        [Test]
        public void UpdateBodyLook_CombinesPitchAndRollOnBodyJoint()
        {
            using var harness = RagdollPhysicsHarness.Create(new TestRagdollPhysicsContext());

            harness.UpdateBodyLook(bodyBend: 0.25f, bodyRollDegrees: 30f);

            Quaternion expected = Quaternion.identity * new Quaternion(0.25f, 0f, 0f, 1f) * Quaternion.Euler(0f, 0f, 30f);
            Assert.That(Quaternion.Angle(harness.Joints[1].targetRotation, expected), Is.LessThan(0.001f));
        }
        private static float GetProfileFloat(RagdollProfile profile, string fieldName)
        {
            FieldInfo field = typeof(RagdollProfile).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, $"RagdollProfile.{fieldName} が公開されていない。");
            return (float)field.GetValue(profile);
        }

        private static void AssertReachDrive(JointDrive drive, float spring, float damper, float maxForce)
        {
            Assert.That(drive.positionSpring, Is.EqualTo(spring));
            Assert.That(drive.positionDamper, Is.EqualTo(damper));
            Assert.That(drive.maximumForce, Is.EqualTo(maxForce));
        }

        private sealed class RagdollPhysicsHarness : System.IDisposable
        {
            private static readonly MethodInfo ApplyReachPoseMethod = typeof(RagdollPhysics).GetMethod(
                "ApplyReachPose", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly MethodInfo ProcessWalkingMethod = typeof(RagdollPhysics).GetMethod(
                "ProcessWalking", BindingFlags.Instance | BindingFlags.NonPublic);
            private static readonly FieldInfo StepRTimerField = typeof(RagdollPhysics).GetField(
                "_stepRTimer", BindingFlags.Instance | BindingFlags.NonPublic);
            private const int UpperRightLegIndex = 7;
            private const int RightFootIndex = 11;
            private const int LeftFootIndex = 12;

            private readonly GameObject[] _parts;
            public ConfigurableJoint[] Joints { get; }
            public RagdollPhysics Physics { get; }
            public float UpperRightLegTargetX => Joints[UpperRightLegIndex].targetRotation.x;
            public float StepRTimer => (float)StepRTimerField.GetValue(Physics);

            private RagdollPhysicsHarness(TestRagdollPhysicsContext context)
            {
                Assert.That(ApplyReachPoseMethod, Is.Not.Null);
                Assert.That(ProcessWalkingMethod, Is.Not.Null);
                Assert.That(StepRTimerField, Is.Not.Null);

                _parts = new GameObject[15];
                var rigidbodies = new Rigidbody[15];
                Joints = new ConfigurableJoint[15];

                for (int i = 0; i < _parts.Length; i++)
                {
                    _parts[i] = new GameObject($"RagdollPhysicsHarness_{i}");
                    rigidbodies[i] = _parts[i].AddComponent<Rigidbody>();
                    Joints[i] = _parts[i].AddComponent<ConfigurableJoint>();
                }

                Physics = new RagdollPhysics(context, _parts, rigidbodies, Joints);
            }

            public static RagdollPhysicsHarness Create(TestRagdollPhysicsContext context) => new(context);

            public void ArrangeForwardRightStep()
            {
                _parts[RightFootIndex].transform.position = new Vector3(0f, 0f, -0.1f);
                _parts[LeftFootIndex].transform.position = new Vector3(0f, 0f, 0.1f);
            }

            public void ProcessWalking(Vector3 moveDirection, float deltaTime)
            {
                ProcessWalkingMethod.Invoke(Physics, new object[] { moveDirection, deltaTime });
            }

            public void ApplyReachPose(bool isRight)
            {
                // 第4引数 armSwingDegrees はリフレクション経由では省略できないため明示する
                ApplyReachPoseMethod.Invoke(Physics, new object[] { isRight, 8f, 30f, 0f });
            }

            public void UpdateBodyLook(float bodyBend, float bodyRollDegrees)
            {
                var method = typeof(RagdollPhysics).GetMethod("UpdateBodyLook", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null);
                method.Invoke(Physics, new object[] { bodyBend, bodyRollDegrees });
            }

            public void Dispose()
            {
                foreach (GameObject part in _parts)
                {
                    if (part != null)
                        Object.DestroyImmediate(part);
                }
            }
        }

        /// <summary>
        /// 物理層へ渡すテストダブル。
        ///
        /// 2026-08-07 の契約変更で、チューニング値は個別メンバーではなく
        /// <see cref="RagdollProfile"/> 経由で渡すようになった。ここでは
        /// ScriptableObject の Profile をテスト用に生成し、従来と同じ既定値を入れている
        /// （既定値を変えるとテストの前提が変わるため、値は変更していない）。
        /// </summary>
        private sealed class TestRagdollPhysicsContext : IRagdollPhysicsContext
        {
            public RagdollProfile Profile { get; }

            public TestRagdollPhysicsContext()
            {
                Profile = ScriptableObject.CreateInstance<RagdollProfile>();
                Profile.balanceHeight = 2.5f;
                Profile.balanceStrength = 5000f;
                Profile.coreStrength = 1500f;
                Profile.limbStrength = 500f;
                Profile.moveSpeed = 5f;
                Profile.turnSpeed = 10f;
                Profile.jumpForce = 10f;
                Profile.stepDuration = 0.2f;
                Profile.stepHeight = 1.7f;
                Profile.feetMountForce = 25f;
                Profile.balanceMargin = 0.15f;
                Profile.idleBalancePriority = 0.8f;
                Profile.walkingBalancePriority = 0.5f;
                Profile.idlePoseStiffnessMultiplier = 1f;
                Profile.walkingPoseStiffnessMultiplier = 0.5f;
                Profile.stateBlendSpeed = 5f;
                Profile.balanceDamperRatio = 0.1f;
                Profile.poseDamperRatio = 0.1f;   // 旧 LimbDamperRatio（改名転送だった）
                Profile.coreDamperRatio = 0.1f;
                Profile.reachArmInputLimit = 1.2f;
                Profile.reachUpperArmBasePitch = 8f;
                Profile.reachUpperArmPitchPerUnit = 35f;
                Profile.reachUpperArmMinPitch = -60f;
                Profile.reachUpperArmMaxPitch = 70f;
                Profile.reachLowerArmPitch = 30f;
                Profile.ragdollDriveOffSpring = 25f;
                Profile.ragdollDriveOffDamper = 5f;
                Profile.movementVelocityLerp = 0.8f;
                Profile.airControlMultiplier = RagdollProfile.DefaultAirControlMultiplier;
                Profile.punchImpulse = 10f;
                Profile.punchRecoveryDelaySeconds = 0.15f;
                Profile.punchRecoveryLerpSpeed = 12f;
                // Reach の Joint 値は既定 0。個別テストが Profile へ直接設定する
            }

            // Profile から直接読めないものだけを持つ
            public float MoveSpeed => Profile.moveSpeed;
            public ActionPoseAsset ReachPose { get; set; }
            public bool IsAnyHandGrabbing { get; set; }
            public bool HasStateAuthority { get; set; } = true;
            public bool UseForecastPhysics { get; set; }
        }
    }
}
