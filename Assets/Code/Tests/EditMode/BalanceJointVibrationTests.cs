using System.Collections.Generic;
using MyFolder.Scripts.Player;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MyFolder.Tests.EditMode
{
    /// <summary>
    /// BN-1 回帰防止テスト: バランス用 ConfigurableJoint の damper ratio による減衰挙動を検証する。
    ///
    /// 背景:
    ///   RagdollProfile.balanceStrength = 5000, balanceDamperRatio = 0.1 の場合
    ///   balanceDamper = 500 となりホストY軸振幅 0.269m で M2 FAIL（2026-03-27 計測）。
    ///   balanceDamperRatio = 0.15 に変更し balanceDamper = 750 で改善を期待。
    ///
    /// テスト方針:
    ///   Physics.simulationMode = Script で決定論的にシミュレーションし、
    ///   初期角速度を与えた ConfigurableJoint の減衰挙動を計測する。
    ///   damper=500 と damper=750 を直接比較することで、回帰が入ればテストが落ちる。
    ///
    /// 注意 (慣性テンソルの調整):
    ///   1kg の球で balanceSpring=5000 を掛けると ζ&gt;1（overdamped）となり両者とも
    ///   振動しないため、実ラグドール同等のトルソー相当（I=30 kg·m²）の慣性テンソルを
    ///   手動設定して damper=500 時に ζ≈0.65（underdamped）となるよう調整している。
    ///   この I はダンパー比率の影響を観測可能にするためのテスト条件で、実プレイヤーの
    ///   正確な慣性再現ではない。
    /// </summary>
    public sealed class BalanceJointVibrationTests
    {
        // RagdollProfile 実パラメータ
        private const float BalanceSpring       = 5000f;
        private const float DamperUnderdamped   = 500f;  // ratio=0.1 (BN-1 前)
        private const float DamperImproved      = 750f;  // ratio=0.15 (BN-1 後)

        // シミュレーション設定
        private const float DeltaTime           = 0.02f;  // Fusion の FixedUpdate 相当
        private const int   SimulationSteps     = 150;    // 3.0 秒
        private const float InitialAngularVelocity = 3f;  // rad/s ≈ 172°/s の撹乱

        // テスト用慣性テンソル (ζ<1 となるよう選定)
        private const float InertiaTensorValue  = 30f;

        // 判定閾値
        private const float UnderdampedZeroCrossMin = 1;      // damper=500 は少なくとも1回反転
        private const float ImprovedMaxAngleRadians = 0.35f;  // damper=750 は最大偏差 ~20° 以下

        private readonly List<GameObject> _gameObjects = new();
        private SimulationMode _originalSimulationMode;

        [SetUp]
        public void SetUp()
        {
            _originalSimulationMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
        }

        [TearDown]
        public void TearDown()
        {
            Physics.simulationMode = _originalSimulationMode;

            for (int i = 0; i < _gameObjects.Count; i++)
            {
                if (_gameObjects[i] != null)
                {
                    Object.DestroyImmediate(_gameObjects[i]);
                }
            }
            _gameObjects.Clear();
        }

        /// <summary>
        /// A/B 比較: damper=750 は damper=500 より最大角度オーバーシュートが小さい。
        /// これが逆転すれば BN-1 修正の効果がなくなったことを意味する（回帰）。
        /// </summary>
        [Test]
        public void DamperImproved_ReducesOvershoot_ComparedToUnderdamped()
        {
            SimulationResult under = SimulateAngularOscillation(BalanceSpring, DamperUnderdamped);
            SimulationResult improved = SimulateAngularOscillation(BalanceSpring, DamperImproved);

            Assert.That(
                improved.MaxAngleMagnitudeRadians,
                Is.LessThan(under.MaxAngleMagnitudeRadians),
                $"damper={DamperImproved} は damper={DamperUnderdamped} より小さいオーバーシュートを示すべき。" +
                $" under={under.MaxAngleMagnitudeRadians:F4}rad, improved={improved.MaxAngleMagnitudeRadians:F4}rad");
        }

        /// <summary>
        /// A/B 比較: damper=750 は damper=500 より角速度ゼロクロス（振動反転）回数が少ない。
        /// 3秒も回すと両方定常値(角速度0)に収束するため、最終値比較ではなく「振動回数」で評価する。
        /// </summary>
        [Test]
        public void DamperImproved_ShowsFewerOscillations_ComparedToUnderdamped()
        {
            SimulationResult under = SimulateAngularOscillation(BalanceSpring, DamperUnderdamped);
            SimulationResult improved = SimulateAngularOscillation(BalanceSpring, DamperImproved);

            Assert.That(
                improved.AngularVelocityZeroCrossings,
                Is.LessThan(under.AngularVelocityZeroCrossings),
                $"damper={DamperImproved} は damper={DamperUnderdamped} より少ない振動回数を示すべき。" +
                $" under={under.AngularVelocityZeroCrossings}, improved={improved.AngularVelocityZeroCrossings}");
        }

        /// <summary>
        /// ドキュメンタリーテスト: 修正前 (damper=500) は underdamped であり振動する。
        /// このテストがパスすれば、テストリグが確かに underdamped 領域で動作していることを意味する。
        /// </summary>
        [Test]
        public void DamperUnderdamped_ExhibitsOscillation_AtTunedInertia()
        {
            SimulationResult under = SimulateAngularOscillation(BalanceSpring, DamperUnderdamped);

            Assert.That(
                under.AngularVelocityZeroCrossings,
                Is.GreaterThanOrEqualTo(UnderdampedZeroCrossMin),
                $"damper={DamperUnderdamped} は underdamped であり少なくとも {UnderdampedZeroCrossMin} 回角速度反転するはず。" +
                $" 実測:{under.AngularVelocityZeroCrossings}");
        }

        /// <summary>
        /// アセット値ガード: MainPlayer_AprProfile.balanceDamperRatio が BN-1 修正値以上に保たれる。
        /// 誰かが誤って 0.1 に戻した場合、このテストが即座に失敗する。
        /// </summary>
        [Test]
        public void MainPlayerProfile_BalanceDamperRatio_IsAtLeastImprovedValue()
        {
            const string assetPath = "Assets/Settings/MainPlayer_AprProfile.asset";
            var profile = AssetDatabase.LoadAssetAtPath<RagdollProfile>(assetPath);

            Assert.That(profile, Is.Not.Null,
                $"{assetPath} をロードできなかった。アセットが移動/削除された可能性がある。");
            Assert.That(profile.balanceDamperRatio, Is.GreaterThanOrEqualTo(0.15f),
                $"BN-1 修正により balanceDamperRatio は 0.15 以上に保つ必要がある。" +
                $" 現在値: {profile.balanceDamperRatio}。過去の FAIL値(0.1)に戻されている可能性がある。");
            Assert.That(profile.balanceStrength * profile.balanceDamperRatio, Is.GreaterThanOrEqualTo(DamperImproved),
                $"balanceStrength * balanceDamperRatio = {profile.balanceStrength * profile.balanceDamperRatio} は " +
                $"{DamperImproved} 以上であるべき。");
        }

        /// <summary>
        /// 修正後 (damper=750) は臨界減衰に近く、3秒後には最大角度偏差が閾値以下に収まる。
        /// </summary>
        [Test]
        public void DamperImproved_StaysWithinAngleThreshold()
        {
            SimulationResult improved = SimulateAngularOscillation(BalanceSpring, DamperImproved);

            Assert.That(
                improved.MaxAngleMagnitudeRadians,
                Is.LessThan(ImprovedMaxAngleRadians),
                $"damper={DamperImproved} は最大角度偏差 < {ImprovedMaxAngleRadians}rad を満たすべき。" +
                $" 実測:{improved.MaxAngleMagnitudeRadians:F4}rad");
        }

        // ─── ヘルパー ────────────────────────────────────────────────

        /// <summary>
        /// 初期角速度を与えたダイナミックボディを angularDrive 付き ConfigurableJoint で
        /// kinematic アンカーに接続し、SimulationSteps ステップ分シミュレーションする。
        /// </summary>
        private SimulationResult SimulateAngularOscillation(float spring, float damper)
        {
            Rigidbody anchor = CreateKinematicAnchor(Vector3.zero);
            Rigidbody body   = CreateDynamicBody(Vector3.zero, mass: 1f);

            // 慣性テンソルを手動設定（ζ<1 となる領域で観測するため）
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.inertiaTensor = new Vector3(InertiaTensorValue, InertiaTensorValue, InertiaTensorValue);

            ConfigurableJoint joint = body.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = anchor;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

            JointDrive drive = new JointDrive
            {
                positionSpring = spring,
                positionDamper = damper,
                maximumForce   = float.MaxValue,
            };
            joint.angularXDrive  = drive;
            joint.angularYZDrive = drive;

            // 初期角速度で撹乱を与える（AngularVelocity は ConfigurableJoint 作成後に設定）
            body.angularVelocity = new Vector3(InitialAngularVelocity, 0f, 0f);

            // 決定論的シミュレーション
            float maxAngleMag = 0f;
            int zeroCrossings = 0;
            bool prevPositive = body.angularVelocity.x >= 0f;

            for (int step = 0; step < SimulationSteps; step++)
            {
                Physics.Simulate(DeltaTime);

                // 現在角度を -π..π に正規化して評価
                float angle = NormalizeAngleRadians(body.transform.eulerAngles.x * Mathf.Deg2Rad);
                float absAngle = Mathf.Abs(angle);
                if (absAngle > maxAngleMag) maxAngleMag = absAngle;

                bool curPositive = body.angularVelocity.x >= 0f;
                if (curPositive != prevPositive) zeroCrossings++;
                prevPositive = curPositive;
            }

            return new SimulationResult(
                maxAngleMagnitudeRadians:     maxAngleMag,
                angularVelocityZeroCrossings: zeroCrossings,
                finalAngularSpeed:            body.angularVelocity.magnitude);
        }

        private Rigidbody CreateKinematicAnchor(Vector3 pos)
        {
            var go = new GameObject("TestAnchor");
            go.transform.position = pos;
            _gameObjects.Add(go);

            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity  = false;
            return rb;
        }

        private Rigidbody CreateDynamicBody(Vector3 pos, float mass)
        {
            var go = new GameObject("TestBody");
            go.transform.position = pos;
            _gameObjects.Add(go);

            var rb = go.AddComponent<Rigidbody>();
            rb.mass           = mass;
            rb.useGravity     = false;  // 重力の影響を排除し、JointDrive のみで挙動を観測
            rb.linearDamping  = 0f;
            rb.angularDamping = 0f;
            return rb;
        }

        private static float NormalizeAngleRadians(float radians)
        {
            while (radians >  Mathf.PI) radians -= 2f * Mathf.PI;
            while (radians < -Mathf.PI) radians += 2f * Mathf.PI;
            return radians;
        }

        private readonly struct SimulationResult
        {
            public readonly float MaxAngleMagnitudeRadians;
            public readonly int   AngularVelocityZeroCrossings;
            public readonly float FinalAngularSpeed;

            public SimulationResult(float maxAngleMagnitudeRadians, int angularVelocityZeroCrossings, float finalAngularSpeed)
            {
                MaxAngleMagnitudeRadians     = maxAngleMagnitudeRadians;
                AngularVelocityZeroCrossings = angularVelocityZeroCrossings;
                FinalAngularSpeed            = finalAngularSpeed;
            }
        }
    }
}
