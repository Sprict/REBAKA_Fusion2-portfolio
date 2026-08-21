using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Rebaka.Tests.Vibration
{
    /// <summary>
    /// 多関節ラグドール（5セグメント連鎖）で複合要因の共鳴振動を再現・検証するテスト。
    /// 
    /// テスト構成:
    ///   M1: 全要因ON（修正前の状態を再現）
    ///       - ダンパー=0, JointDrive+AddForce同時, バランスフリップ, 予測先読み
    ///   M2: 修正後（ダンパーあり, AddForce補正のみ, フリップなし, 先読みなし）
    ///   M3: 修正後 + 先読みあり（先読みだけ戻して影響を確認）
    /// </summary>
    public class MultiJointVibrationTest : MonoBehaviour
    {
        [Header("Test Configuration")]
        public float testDurationSeconds = 5f;
        public float cooldownSeconds = 0.5f;

        [Header("Chain Rig Settings")]
        public int chainSegments = 5;
        public float segmentMass = 1f;
        public float springStrength = 5000f;
        public float damperRatio = 0.1f;

        [Header("Proxy Correction")]
        public float correctionKp = 45f;
        public float correctionKd = 8f;
        public float correctionKpFixed = 20f;
        public float correctionKdFixed = 15f;
        public float predictionLead = 0.06f;

        [Header("Balance Flip")]
        public float flipFrequencyHz = 25f;

        // ─── ステートマシン ──────────────────────────────────────
        private enum Phase { Idle, Running, Cooldown, Done }
        private Phase _phase = Phase.Idle;
        private int _currentTestIndex;
        private float _phaseTimer;

        private MultiJointTestCase _currentCase;
        private ChainRigContext _currentCtx;
        private MultiJointSampler _currentSampler;

        private readonly List<MultiJointResult> _results = new List<MultiJointResult>();
        private List<MultiJointTestCase> _testCases;
        private string _statusMessage = "Press [M] or call RunTests() to start multi-joint tests";
        private Vector2 _scrollPos;

        public bool IsRunning => _phase == Phase.Running || _phase == Phase.Cooldown;
        public IReadOnlyList<MultiJointResult> Results => _results;

        private void Awake()
        {
            BuildTestCases();
        }

        private void BuildTestCases()
        {
            _testCases = new List<MultiJointTestCase>
            {
                new MultiJointTestCase(
                    "M1_AllBugs_NoDamper_ForceFight_Flip_Predict",
                    "全要因ON（修正前再現）: ダンパー=0, JointDrive+AddForce同時, バランスフリップ, 予測先読み",
                    () => CreateChainRig(0f),
                    ctx => TickM1(ctx)),

                new MultiJointTestCase(
                    "M2_Fixed_Damper_NoForceFight_NoFlip_NoPredict",
                    "修正後: ダンパーあり, AddForce補正のみ(JointDrive安定), フリップなし, 先読みなし",
                    () => CreateChainRig(springStrength * damperRatio),
                    ctx => TickM2(ctx)),

                new MultiJointTestCase(
                    "M3_Fixed_WithPredict",
                    "修正後 + 先読みあり: 先読みだけ戻して影響確認",
                    () => CreateChainRig(springStrength * damperRatio),
                    ctx => TickM3(ctx)),
            };
        }

        // ─── 公開API ─────────────────────────────────────────────
        public void RunTests()
        {
            if (_phase != Phase.Idle && _phase != Phase.Done) return;
            _results.Clear();
            _currentTestIndex = 0;
            _phaseTimer = 0f;
            BeginNextTest();
        }

        private void Update()
        {
            if (Keyboard.current != null &&
                Keyboard.current.mKey.wasPressedThisFrame &&
                (_phase == Phase.Idle || _phase == Phase.Done))
            {
                RunTests();
            }
        }

        private void FixedUpdate()
        {
            switch (_phase)
            {
                case Phase.Running:
                    _phaseTimer += Time.fixedDeltaTime;
                    _currentCase?.PerFixedUpdate?.Invoke(_currentCtx);
                    if (_currentCtx != null)
                        _currentSampler.Sample(_currentCtx, _phaseTimer);
                    if (_phaseTimer >= testDurationSeconds)
                        EndCurrentTest();
                    break;

                case Phase.Cooldown:
                    _phaseTimer += Time.fixedDeltaTime;
                    if (_phaseTimer >= cooldownSeconds)
                    {
                        _phaseTimer = 0f;
                        if (_currentTestIndex < _testCases.Count)
                            BeginNextTest();
                        else
                            FinishAllTests();
                    }
                    break;
            }
        }

        private void BeginNextTest()
        {
            if (_currentTestIndex >= _testCases.Count) { FinishAllTests(); return; }
            _currentCase = _testCases[_currentTestIndex];
            _currentCtx = _currentCase.RigFactory();
            _currentSampler = new MultiJointSampler(_currentCtx.Bodies.Length);
            _phaseTimer = 0f;
            _phase = Phase.Running;
            _statusMessage = $"Running [{_currentTestIndex + 1}/{_testCases.Count}]: {_currentCase.Name}";
            Debug.Log($"[MultiJointTest] ▶ START {_currentCase.Name}: {_currentCase.Description}");
        }

        private void EndCurrentTest()
        {
            _currentCtx?.Destroy();
            var result = _currentSampler.Compute(_currentCase.Name, _currentCase.Description);
            _results.Add(result);
            string v = result.IsVibrating ? "⚠ VIBRATING" : "✓ STABLE";
            Debug.Log($"[MultiJointTest] ■ END {_currentCase.Name}: {v}\n{result.ToDetailString()}");
            _currentTestIndex++;
            _phaseTimer = 0f;
            _phase = Phase.Cooldown;
        }

        private void FinishAllTests()
        {
            _phase = Phase.Done;
            _statusMessage = $"All {_results.Count} multi-joint tests complete!";
            PrintSummary();
        }

        // ═══════════════════════════════════════════════════════════
        // チェーンリグ生成（多関節ラグドール模倣）
        // ═══════════════════════════════════════════════════════════
        private ChainRigContext CreateChainRig(float damper)
        {
            Vector3 basePos = new Vector3(0, 4, 0);
            var bodies = new Rigidbody[chainSegments];
            var joints = new ConfigurableJoint[chainSegments];

            for (int i = 0; i < chainSegments; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = $"Chain_{i}";
                go.transform.position = basePos + Vector3.down * i * 0.6f;
                go.transform.localScale = new Vector3(0.2f, 0.3f, 0.2f);

                var rb = go.AddComponent<Rigidbody>();
                rb.mass = segmentMass;
                rb.useGravity = true;
                rb.linearDamping = 0.05f;
                rb.angularDamping = 0.05f;
                bodies[i] = rb;

                if (i == 0)
                {
                    // ルートは初期角度オフセットを与える
                    go.transform.rotation = Quaternion.Euler(15f, 0f, 10f);
                }

                var joint = go.AddComponent<ConfigurableJoint>();
                if (i > 0)
                {
                    joint.connectedBody = bodies[i - 1];
                }
                else
                {
                    // ルートはワールド接続（APR_Rootと同じ）
                    joint.connectedBody = null;
                    joint.configuredInWorldSpace = true;
                    // 位置ドライブはゼロ（原点引力防止）
                    var zeroDrive = new JointDrive { positionSpring = 0f, positionDamper = 0f, maximumForce = 0f };
                    joint.xDrive = zeroDrive;
                    joint.yDrive = zeroDrive;
                    joint.zDrive = zeroDrive;
                }

                joint.xMotion = ConfigurableJointMotion.Locked;
                joint.yMotion = ConfigurableJointMotion.Locked;
                joint.zMotion = ConfigurableJointMotion.Locked;

                var drive = new JointDrive
                {
                    positionSpring = springStrength,
                    positionDamper = damper,
                    maximumForce = float.MaxValue
                };
                joint.angularXDrive = drive;
                joint.angularYZDrive = drive;
                joints[i] = joint;
            }

            // 同一チェーン内のコライダー衝突を無効化
            var colliders = new List<Collider>();
            foreach (var b in bodies)
                if (b != null) colliders.AddRange(b.GetComponents<Collider>());
            for (int i = 0; i < colliders.Count; i++)
                for (int j = i + 1; j < colliders.Count; j++)
                    Physics.IgnoreCollision(colliders[i], colliders[j], true);

            return new ChainRigContext
            {
                Bodies = bodies,
                Joints = joints,
                TargetRootPosition = basePos,
                SimulatedNetVelocity = Vector3.zero
            };
        }

        // ═══════════════════════════════════════════════════════════
        // M1: 全要因ON（修正前再現）
        // ═══════════════════════════════════════════════════════════
        private void TickM1(ChainRigContext ctx)
        {
            if (ctx.Bodies[0] == null) return;
            var root = ctx.Bodies[0];

            // 1. AddForce補正（Kp=45, Kd=8 — 減衰不足）
            Vector3 targetPos = ctx.TargetRootPosition;
            // 予測先読み
            ctx.SimulatedNetVelocity = new Vector3(0.5f, 0f, 0f);
            targetPos += ctx.SimulatedNetVelocity * predictionLead;
            ctx.TargetRootPosition += ctx.SimulatedNetVelocity * Time.fixedDeltaTime;

            var posErr = targetPos - root.position;
            if (posErr.sqrMagnitude > 1f) posErr = posErr.normalized;
            var velErr = ctx.SimulatedNetVelocity - root.linearVelocity;
            root.AddForce(posErr * correctionKp + velErr * correctionKd, ForceMode.Acceleration);

            // 2. バランスフリップ（JointDrive ON/OFF高速切り替え）
            float period = 1f / flipFrequencyHz;
            bool driveOn = (Time.fixedTime % period) < (period * 0.5f);
            var flipDrive = new JointDrive
            {
                positionSpring = driveOn ? springStrength : 25f,
                positionDamper = 0f, // ダンパー=0（修正前）
                maximumForce = float.MaxValue
            };
            for (int i = 0; i < ctx.Joints.Length; i++)
            {
                if (ctx.Joints[i] == null) continue;
                ctx.Joints[i].angularXDrive = flipDrive;
                ctx.Joints[i].angularYZDrive = flipDrive;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // M2: 修正後（ダンパーあり, AddForce補正のみ, フリップなし, 先読みなし）
        // ═══════════════════════════════════════════════════════════
        private void TickM2(ChainRigContext ctx)
        {
            if (ctx.Bodies[0] == null) return;
            var root = ctx.Bodies[0];

            // AddForce補正のみ（改善Kp/Kd）
            Vector3 targetPos = ctx.TargetRootPosition;
            // 先読みなし
            ctx.SimulatedNetVelocity = new Vector3(0.5f, 0f, 0f);
            ctx.TargetRootPosition += ctx.SimulatedNetVelocity * Time.fixedDeltaTime;

            var posErr = targetPos - root.position;
            if (posErr.sqrMagnitude > 1f) posErr = posErr.normalized;
            var velErr = ctx.SimulatedNetVelocity - root.linearVelocity;
            root.AddForce(posErr * correctionKpFixed + velErr * correctionKdFixed, ForceMode.Acceleration);

            // JointDriveは安定（フリップなし、ダンパーはリグ生成時に設定済み）
        }

        // ═══════════════════════════════════════════════════════════
        // M3: 修正後 + 先読みあり
        // ═══════════════════════════════════════════════════════════
        private void TickM3(ChainRigContext ctx)
        {
            if (ctx.Bodies[0] == null) return;
            var root = ctx.Bodies[0];

            Vector3 targetPos = ctx.TargetRootPosition;
            ctx.SimulatedNetVelocity = new Vector3(0.5f, 0f, 0f);
            // 先読みあり
            targetPos += ctx.SimulatedNetVelocity * predictionLead;
            ctx.TargetRootPosition += ctx.SimulatedNetVelocity * Time.fixedDeltaTime;

            var posErr = targetPos - root.position;
            if (posErr.sqrMagnitude > 1f) posErr = posErr.normalized;
            var velErr = ctx.SimulatedNetVelocity - root.linearVelocity;
            root.AddForce(posErr * correctionKpFixed + velErr * correctionKdFixed, ForceMode.Acceleration);
        }

        // ─── サマリ出力 ───────────────────────────────────────────
        private void PrintSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n╔══════════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║       MULTI-JOINT VIBRATION TEST RESULTS                        ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════╝");

            foreach (var r in _results)
            {
                string v = r.IsVibrating ? "⚠ VIBRATING" : "✓ STABLE   ";
                sb.AppendLine($"  [{v}] {r.TestName}");
                sb.AppendLine($"    {r.Description}");
                sb.AppendLine($"    oscFreq={r.RootOscFreqHz:F1}Hz  maxAngVel={r.MaxChainAngVel:F2}rad/s  " +
                              $"rootPosRange={r.RootPosRange:F4}m  chainAmplification={r.ChainAmplification:F2}x");
            }

            // 修正前後の比較
            var m1 = _results.Find(r => r.TestName.StartsWith("M1"));
            var m2 = _results.Find(r => r.TestName.StartsWith("M2"));
            if (m1 != null && m2 != null)
            {
                sb.AppendLine("\n── 修正効果 ──────────────────────────────────────────────");
                float oscReduction = m1.RootOscFreqHz > 0.001f
                    ? (1f - m2.RootOscFreqHz / m1.RootOscFreqHz) * 100f : 0f;
                float angReduction = m1.MaxChainAngVel > 0.001f
                    ? (1f - m2.MaxChainAngVel / m1.MaxChainAngVel) * 100f : 0f;
                float posReduction = m1.RootPosRange > 0.0001f
                    ? (1f - m2.RootPosRange / m1.RootPosRange) * 100f : 0f;

                sb.AppendLine($"  振動周波数: {m1.RootOscFreqHz:F1}Hz → {m2.RootOscFreqHz:F1}Hz ({oscReduction:+0.0;-0.0}%)");
                sb.AppendLine($"  最大角速度: {m1.MaxChainAngVel:F2} → {m2.MaxChainAngVel:F2} rad/s ({angReduction:+0.0;-0.0}%)");
                sb.AppendLine($"  位置振幅:   {m1.RootPosRange:F4} → {m2.RootPosRange:F4} m ({posReduction:+0.0;-0.0}%)");

                if (m1.IsVibrating && !m2.IsVibrating)
                    sb.AppendLine("  🟢 修正により振動が解消されました！");
                else if (m1.IsVibrating && m2.IsVibrating)
                    sb.AppendLine("  🟡 振動は軽減されましたが完全には解消されていません");
                else if (!m1.IsVibrating && !m2.IsVibrating)
                    sb.AppendLine("  ⚪ 両方安定（テスト条件の調整が必要かもしれません）");
            }

            Debug.Log(sb.ToString());
        }

        // ─── GUI ─────────────────────────────────────────────────
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(530, 10, 520, Screen.height - 20));
            GUILayout.BeginVertical("box");

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            GUILayout.Label("Multi-Joint Vibration Test", titleStyle);
            GUILayout.Label(_statusMessage);

            if (_phase == Phase.Idle || _phase == Phase.Done)
            {
                if (GUILayout.Button("▶ Run Multi-Joint Tests [M]"))
                    RunTests();
            }

            if (_results.Count > 0)
            {
                GUILayout.Space(6);
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(Screen.height - 200));
                var style = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11 };
                foreach (var r in _results)
                {
                    string color = r.IsVibrating ? "red" : "lime";
                    string verdict = r.IsVibrating ? "⚠ VIB" : "✓ OK ";
                    GUILayout.Label(
                        $"<color={color}>{verdict}</color> {r.TestName}\n" +
                        $"  osc={r.RootOscFreqHz:F1}Hz  angVel={r.MaxChainAngVel:F2}  " +
                        $"posRange={r.RootPosRange:F4}m  chainAmp={r.ChainAmplification:F2}x",
                        style);
                }
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }

    // ─── データ構造 ───────────────────────────────────────────────

    public class MultiJointTestCase
    {
        public string Name;
        public string Description;
        public Func<ChainRigContext> RigFactory;
        public Action<ChainRigContext> PerFixedUpdate;

        public MultiJointTestCase(string name, string desc,
            Func<ChainRigContext> factory, Action<ChainRigContext> perFixed)
        {
            Name = name;
            Description = desc;
            RigFactory = factory;
            PerFixedUpdate = perFixed;
        }
    }

    public class ChainRigContext
    {
        public Rigidbody[] Bodies;
        public ConfigurableJoint[] Joints;
        public Vector3 TargetRootPosition;
        public Vector3 SimulatedNetVelocity;

        public void Destroy()
        {
            if (Bodies == null) return;
            foreach (var b in Bodies)
                if (b != null) UnityEngine.Object.Destroy(b.gameObject);
        }
    }

    public class MultiJointSampler
    {
        private struct ChainSample
        {
            public float time;
            public float rootPosY;
            public float rootVelY;
            public float rootAngVelMag;
            public float maxChainAngVelMag;
        }

        private readonly List<ChainSample> _samples = new List<ChainSample>(512);
        private readonly int _segmentCount;

        public MultiJointSampler(int segmentCount)
        {
            _segmentCount = segmentCount;
        }

        public void Sample(ChainRigContext ctx, float time)
        {
            if (ctx.Bodies == null || ctx.Bodies[0] == null) return;

            float maxAngVel = 0f;
            for (int i = 0; i < ctx.Bodies.Length; i++)
            {
                if (ctx.Bodies[i] == null) continue;
                float av = ctx.Bodies[i].angularVelocity.magnitude;
                if (av > maxAngVel) maxAngVel = av;
            }

            _samples.Add(new ChainSample
            {
                time = time,
                rootPosY = ctx.Bodies[0].position.y,
                rootVelY = ctx.Bodies[0].linearVelocity.y,
                rootAngVelMag = ctx.Bodies[0].angularVelocity.magnitude,
                maxChainAngVelMag = maxAngVel
            });
        }

        public MultiJointResult Compute(string testName, string description)
        {
            if (_samples.Count < 2)
                return new MultiJointResult { TestName = testName, Description = description };

            float minY = float.MaxValue, maxY = float.MinValue;
            float maxRootAngVel = 0f, maxChainAngVel = 0f;
            int signChanges = 0;
            bool prevPositive = _samples[0].rootVelY >= 0f;
            float duration = _samples[_samples.Count - 1].time - _samples[0].time;
            float skipUntil = _samples[0].time + 0.5f;

            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                if (s.time < skipUntil) continue;

                if (s.rootPosY < minY) minY = s.rootPosY;
                if (s.rootPosY > maxY) maxY = s.rootPosY;
                if (s.rootAngVelMag > maxRootAngVel) maxRootAngVel = s.rootAngVelMag;
                if (s.maxChainAngVelMag > maxChainAngVel) maxChainAngVel = s.maxChainAngVelMag;

                bool curPositive = s.rootVelY >= 0f;
                if (curPositive != prevPositive) signChanges++;
                prevPositive = curPositive;
            }

            float steadyDuration = Mathf.Max(duration - 0.5f, 0.001f);
            float oscFreqHz = signChanges / (2f * steadyDuration);
            float posRange = (minY == float.MaxValue) ? 0f : (maxY - minY);
            float chainAmp = maxRootAngVel > 0.001f ? maxChainAngVel / maxRootAngVel : 1f;

            return new MultiJointResult
            {
                TestName = testName,
                Description = description,
                RootOscFreqHz = oscFreqHz,
                MaxRootAngVel = maxRootAngVel,
                MaxChainAngVel = maxChainAngVel,
                RootPosRange = posRange,
                ChainAmplification = chainAmp,
                SampleCount = _samples.Count
            };
        }
    }

    public class MultiJointResult
    {
        public string TestName;
        public string Description;
        public float RootOscFreqHz;
        public float MaxRootAngVel;
        public float MaxChainAngVel;
        public float RootPosRange;
        public float ChainAmplification;
        public int SampleCount;

        private const float OscFreqThreshold = 3f;
        private const float AngVelThreshold = 5f;

        public bool IsVibrating =>
            RootOscFreqHz > OscFreqThreshold || MaxChainAngVel > AngVelThreshold;

        public string ToDetailString() =>
            $"  oscFreq={RootOscFreqHz:F2}Hz  maxRootAngVel={MaxRootAngVel:F3}  " +
            $"maxChainAngVel={MaxChainAngVel:F3}  posRange={RootPosRange:F4}m  " +
            $"chainAmp={ChainAmplification:F2}x  samples={SampleCount}";
    }
}
