using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Tests.Vibration
{
    /// <summary>
    /// ネットワーク不要のスタンドアロン振動要因テストハーネス。
    /// FixedUpdate ベースのステートマシンで動作するため、
    /// Coroutine に依存せず execute_script からも確実に動く。
    ///
    /// テスト対象の5要因:
    ///   T1: JointDrive ダンパー=0 vs ダンパーあり
    ///   T2: AddForce補正 + JointDrive の力競合（UpdatePhysicsVisualOnly問題）
    ///   T3: ApplySoftRootCorrection の Kp/Kd 比（減衰不足）
    ///   T4: バランス判定フリップ（毎Tick ON/OFF切り替え）
    ///   T5: rootPredictionLeadSeconds による目標位置オーバーシュート
    /// </summary>
    public class VibrationTestHarness : MonoBehaviour
    {
        // ─── 設定 ───────────────────────────────────────────────
        [Header("Test Configuration")]
        [Tooltip("各テストの計測時間（秒）")]
        public float testDurationSeconds = 3f;
        [Tooltip("テスト間のクールダウン（秒）")]
        public float cooldownSeconds = 0.3f;

        [Header("T1: JointDrive Damper")]
        public float t1Spring = 5000f;
        public float t1DamperZero = 0f;
        public float t1DamperGood = 500f;

        [Header("T2: Force Fight (UpdatePhysicsVisualOnly)")]
        public float t2JointSpring = 5000f;
        public float t2JointDamper = 500f;
        public float t2CorrectionKp = 45f;
        public float t2CorrectionKd = 8f;

        [Header("T3: Kp/Kd Ratio")]
        public float t3KpUnderdamped = 45f;
        public float t3KdUnderdamped = 8f;
        public float t3KpCritical = 20f;
        public float t3KdCritical = 15f;

        [Header("T4: Balance Flip")]
        public float t4FlipFrequencyHz = 20f;

        [Header("T5: Prediction Lead")]
        public float t5LeadSecondsHigh = 0.06f;
        public float t5LeadSecondsZero = 0f;
        public float t5SimulatedVelocity = 5f;

        // ─── ステートマシン ──────────────────────────────────────
        private enum Phase { Idle, Running, Cooldown, Done }
        private Phase _phase = Phase.Idle;
        private int _currentTestIndex = 0;
        private float _phaseTimer = 0f;

        // 現在実行中のテスト
        private TestCase _currentCase;
        private TestRigContext _currentCtx;
        private VibrationSampler _currentSampler;

        // 結果
        private readonly List<TestResult> _results = new List<TestResult>();
        private string _statusMessage = "Press [Space] to start tests";
        private Vector2 _scrollPos;

        public bool IsRunning => _phase == Phase.Running || _phase == Phase.Cooldown;
        public IReadOnlyList<TestResult> Results => _results;

        // ─── テスト定義リスト ─────────────────────────────────────
        private List<TestCase> _testCases;

        private void Awake()
        {
            BuildTestCases();
        }

        private void BuildTestCases()
        {
            _testCases = new List<TestCase>
            {
                new TestCase("T1a_JointDrive_Damper0",
                    "JointDriveのダンパーが0（現状のInitializeJointDrives）",
                    () => CreateT1Rig(t1Spring, t1DamperZero), null),

                new TestCase("T1b_JointDrive_DamperGood",
                    "JointDriveのダンパーあり（spring*0.1）",
                    () => CreateT1Rig(t1Spring, t1DamperGood), null),

                new TestCase("T2a_JointDriveOnly",
                    "JointDriveのみ（AddForce補正なし）",
                    () => CreateT2Rig(t2JointSpring, t2JointDamper), null),

                new TestCase("T2b_JointDrive_Plus_AddForce",
                    "JointDrive + AddForce補正（力競合・UpdatePhysicsVisualOnly問題）",
                    () => CreateT2Rig(t2JointSpring, t2JointDamper),
                    ctx => ApplyT2CorrectionForce(ctx, t2CorrectionKp, t2CorrectionKd)),

                new TestCase("T3a_Kp45_Kd8_Underdamped",
                    "AddForce補正 Kp=45 Kd=8（減衰不足・現状値）",
                    CreateT3Rig,
                    ctx => ApplyT3Correction(ctx, t3KpUnderdamped, t3KdUnderdamped)),

                new TestCase("T3b_Kp20_Kd15_Critical",
                    "AddForce補正 Kp=20 Kd=15（臨界減衰に近い）",
                    CreateT3Rig,
                    ctx => ApplyT3Correction(ctx, t3KpCritical, t3KdCritical)),

                new TestCase("T4a_NoBalanceFlip",
                    "バランス判定フリップなし（安定状態）",
                    CreateT4Rig,
                    ctx => ApplyT4Drives(ctx, false)),

                new TestCase("T4b_BalanceFlip",
                    $"バランス判定フリップあり（ON/OFF切り替え）",
                    CreateT4Rig,
                    ctx => ApplyT4Drives(ctx, true)),

                new TestCase("T5a_NoPrediction",
                    "rootPredictionLeadSeconds=0（先読みなし）",
                    CreateT5Rig,
                    ctx => ApplyT5Correction(ctx, t5LeadSecondsZero, t5SimulatedVelocity)),

                new TestCase("T5b_Prediction_0_06s",
                    $"rootPredictionLeadSeconds={t5LeadSecondsHigh}（現状値）",
                    CreateT5Rig,
                    ctx => ApplyT5Correction(ctx, t5LeadSecondsHigh, t5SimulatedVelocity)),
            };
        }

        // ─── 公開API ─────────────────────────────────────────────
        public void RunAllTestsExternal()
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
                Keyboard.current.spaceKey.wasPressedThisFrame &&
                _phase == Phase.Idle)
            {
                RunAllTestsExternal();
            }
        }

        // ─── FixedUpdate ステートマシン ───────────────────────────
        private void FixedUpdate()
        {
            switch (_phase)
            {
                case Phase.Running:
                    _phaseTimer += Time.fixedDeltaTime;

                    // 毎FixedUpdateの処理
                    _currentCase?.PerFixedUpdate?.Invoke(_currentCtx);

                    // サンプリング
                    if (_currentCtx?.Root != null)
                        _currentSampler.Sample(_currentCtx.Root, _phaseTimer);

                    // テスト終了判定
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
            if (_currentTestIndex >= _testCases.Count)
            {
                FinishAllTests();
                return;
            }

            _currentCase = _testCases[_currentTestIndex];
            _currentCtx = _currentCase.RigFactory();
            _currentSampler = new VibrationSampler();
            _phaseTimer = 0f;
            _phase = Phase.Running;
            _statusMessage = $"Running [{_currentTestIndex + 1}/{_testCases.Count}]: {_currentCase.Name}";
            Debug.Log($"[VibTest] ▶ START {_currentCase.Name}: {_currentCase.Description}");
        }

        private void EndCurrentTest()
        {
            // リグ破棄
            _currentCtx?.Destroy();

            // 結果集計
            TestResult result = _currentSampler.Compute(_currentCase.Name, _currentCase.Description);
            _results.Add(result);

            string verdict = result.IsVibrating ? "⚠ VIBRATING" : "✓ STABLE";
            Debug.Log($"[VibTest] ■ END {_currentCase.Name}: {verdict}\n{result.ToDetailString()}");

            _currentTestIndex++;
            _phaseTimer = 0f;
            _phase = Phase.Cooldown;
            _statusMessage = $"Cooldown... ({_currentTestIndex}/{_testCases.Count} done)";
        }

        private void FinishAllTests()
        {
            _phase = Phase.Done;
            _statusMessage = $"All {_results.Count} tests complete!";
            PrintSummary();
        }

        // ═══════════════════════════════════════════════════════════
        // T1: JointDrive ダンパー=0 vs ダンパーあり
        // ═══════════════════════════════════════════════════════════
        private TestRigContext CreateT1Rig(float spring, float damper)
        {
            var anchor = CreateKinematicBody("T1_Anchor", new Vector3(-10, 3, 0));
            var child  = CreateDynamicBody("T1_Child",  new Vector3(-10, 1, 0), 1f);

            var joint = child.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = anchor;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

            var drive = new JointDrive
            {
                positionSpring = spring,
                positionDamper = damper,
                maximumForce   = float.MaxValue
            };
            joint.angularXDrive  = drive;
            joint.angularYZDrive = drive;

            // 初期角度オフセットで振動を誘発
            child.transform.rotation = Quaternion.Euler(20f, 0f, 0f);

            return new TestRigContext { Root = child, Anchor = anchor, Joint = joint };
        }

        // ═══════════════════════════════════════════════════════════
        // T2: JointDrive + AddForce 力競合
        // ═══════════════════════════════════════════════════════════
        private TestRigContext CreateT2Rig(float spring, float damper)
        {
            var anchor = CreateKinematicBody("T2_Anchor", new Vector3(-5, 3, 0));
            var body   = CreateDynamicBody("T2_Body",   new Vector3(-5, 1, 0), 1f);

            var joint = body.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = anchor;
            joint.xMotion = ConfigurableJointMotion.Free;
            joint.yMotion = ConfigurableJointMotion.Free;
            joint.zMotion = ConfigurableJointMotion.Free;

            var drive = new JointDrive
            {
                positionSpring = spring,
                positionDamper = damper,
                maximumForce   = float.MaxValue
            };
            joint.angularXDrive  = drive;
            joint.angularYZDrive = drive;

            return new TestRigContext
            {
                Root           = body,
                Anchor         = anchor,
                Joint          = joint,
                TargetPosition = new Vector3(-5, 1, 0)
            };
        }

        private void ApplyT2CorrectionForce(TestRigContext ctx, float kp, float kd)
        {
            if (ctx.Root == null) return;
            var posErr = ctx.TargetPosition - ctx.Root.position;
            var velErr = Vector3.zero - ctx.Root.linearVelocity;
            ctx.Root.AddForce(posErr * kp + velErr * kd, ForceMode.Acceleration);
        }

        // ═══════════════════════════════════════════════════════════
        // T3: AddForce補正 Kp/Kd 比
        // ═══════════════════════════════════════════════════════════
        private TestRigContext CreateT3Rig()
        {
            var body = CreateDynamicBody("T3_Body", new Vector3(0.5f, 3, 0), 1f);
            body.useGravity = false;
            return new TestRigContext { Root = body, TargetPosition = new Vector3(0, 3, 0) };
        }

        private void ApplyT3Correction(TestRigContext ctx, float kp, float kd)
        {
            if (ctx.Root == null) return;
            var posErr = ctx.TargetPosition - ctx.Root.position;
            // 実コードと同じハードスナップ閾値クランプ
            if (posErr.sqrMagnitude > 1f) posErr = posErr.normalized;
            var velErr = Vector3.zero - ctx.Root.linearVelocity;
            ctx.Root.AddForce(posErr * kp + velErr * kd, ForceMode.Acceleration);
        }

        // ═══════════════════════════════════════════════════════════
        // T4: バランス判定フリップ（JointDrive ON/OFF高速切り替え）
        // ═══════════════════════════════════════════════════════════
        private TestRigContext CreateT4Rig()
        {
            var anchor = CreateKinematicBody("T4_Anchor", new Vector3(5, 3, 0));
            var body   = CreateDynamicBody("T4_Body",   new Vector3(5, 1, 0), 1f);

            var joint = body.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = anchor;
            joint.xMotion = ConfigurableJointMotion.Locked;
            joint.yMotion = ConfigurableJointMotion.Locked;
            joint.zMotion = ConfigurableJointMotion.Locked;

            // 初期角度オフセット
            body.transform.rotation = Quaternion.Euler(10f, 0f, 0f);

            return new TestRigContext { Root = body, Anchor = anchor, Joint = joint };
        }

        private void ApplyT4Drives(TestRigContext ctx, bool flip)
        {
            if (ctx.Joint == null) return;

            bool driveOn;
            if (flip)
            {
                float period = 1f / t4FlipFrequencyHz;
                driveOn = (Time.fixedTime % period) < (period * 0.5f);
            }
            else
            {
                driveOn = true;
            }

            var drive = new JointDrive
            {
                positionSpring = driveOn ? 5000f : 25f,
                positionDamper = driveOn ? 500f  : 2.5f,
                maximumForce   = float.MaxValue
            };
            ctx.Joint.angularXDrive  = drive;
            ctx.Joint.angularYZDrive = drive;
        }

        // ═══════════════════════════════════════════════════════════
        // T5: rootPredictionLeadSeconds オーバーシュート
        // ═══════════════════════════════════════════════════════════
        private TestRigContext CreateT5Rig()
        {
            var body = CreateDynamicBody("T5_Body", new Vector3(10, 3, 0), 1f);
            body.useGravity = false;
            return new TestRigContext
            {
                Root                  = body,
                TargetPosition        = new Vector3(10, 3, 0),
                SimulatedNetVelocity  = new Vector3(t5SimulatedVelocity, 0, 0)
            };
        }

        private void ApplyT5Correction(TestRigContext ctx, float leadSeconds, float simVelocity)
        {
            if (ctx.Root == null) return;

            // ネットワーク受信値をシミュレート（一定速度で動く目標）
            ctx.SimulatedNetVelocity = new Vector3(simVelocity, 0, 0);
            ctx.TargetPosition      += ctx.SimulatedNetVelocity * Time.fixedDeltaTime;

            // 予測先読み（実コードと同じロジック）
            Vector3 predictedTarget = ctx.TargetPosition + ctx.SimulatedNetVelocity * leadSeconds;

            var posErr = predictedTarget - ctx.Root.position;
            if (posErr.sqrMagnitude > 1f) posErr = posErr.normalized;
            var velErr = ctx.SimulatedNetVelocity - ctx.Root.linearVelocity;
            ctx.Root.AddForce(posErr * 45f + velErr * 8f, ForceMode.Acceleration);
        }

        // ─── ユーティリティ ───────────────────────────────────────
        private Rigidbody CreateKinematicBody(string name, Vector3 pos)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 0.3f;
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity  = false;
            return rb;
        }

        private Rigidbody CreateDynamicBody(string name, Vector3 pos, float mass)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.transform.position = pos;
            go.transform.localScale = Vector3.one * 0.4f;
            var rb = go.AddComponent<Rigidbody>();
            rb.mass           = mass;
            rb.useGravity     = true;
            rb.linearDamping  = 0.05f;
            rb.angularDamping = 0.05f;
            return rb;
        }

        // ─── サマリ出力 ───────────────────────────────────────────
        private void PrintSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("\n╔══════════════════════════════════════════════════════════════╗");
            sb.AppendLine("║          VIBRATION TEST RESULTS SUMMARY                      ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════╝");

            foreach (var r in _results)
            {
                string v = r.IsVibrating ? "⚠ VIBRATING" : "✓ STABLE   ";
                sb.AppendLine($"  [{v}] {r.TestName}");
                sb.AppendLine($"             oscFreq={r.OscillationFreqHz:F1}Hz  maxAngVel={r.MaxAngularVelocity:F2}rad/s  posRange={r.PositionRange:F4}m");
            }

            sb.AppendLine("\n── 要因別判定 ──────────────────────────────────────────────");
            AppendFactorVerdict(sb, "要因1: JointDriveダンパー=0",         "T1a_JointDrive_Damper0",       "T1b_JointDrive_DamperGood");
            AppendFactorVerdict(sb, "要因2: UpdatePhysicsVisualOnly力競合", "T2a_JointDriveOnly",           "T2b_JointDrive_Plus_AddForce");
            AppendFactorVerdict(sb, "要因3: Kp/Kd減衰不足",                "T3a_Kp45_Kd8_Underdamped",    "T3b_Kp20_Kd15_Critical");
            AppendFactorVerdict(sb, "要因4: バランス判定フリップ",           "T4a_NoBalanceFlip",            "T4b_BalanceFlip");
            AppendFactorVerdict(sb, "要因5: 予測先読みオーバーシュート",     "T5a_NoPrediction",             "T5b_Prediction_0_06s");

            Debug.Log(sb.ToString());
        }

        private void AppendFactorVerdict(StringBuilder sb, string label, string baseKey, string probKey)
        {
            var b = _results.Find(r => r.TestName == baseKey);
            var p = _results.Find(r => r.TestName == probKey);
            if (b == null || p == null) { sb.AppendLine($"  {label}: データ不足"); return; }

            float freqDelta = p.OscillationFreqHz - b.OscillationFreqHz;
            float angDelta  = p.MaxAngularVelocity  - b.MaxAngularVelocity;
            string verdict;
            if (!b.IsVibrating && p.IsVibrating)
                verdict = "🔴 CONFIRMED — この要因単体で振動を引き起こす";
            else if (!b.IsVibrating && !p.IsVibrating && (freqDelta > 1.5f || angDelta > 3f))
                verdict = $"🟠 PARTIAL — 振動増加傾向あり (freqΔ={freqDelta:+0.0;-0.0}Hz, angVelΔ={angDelta:+0.0;-0.0})";
            else if (b.IsVibrating && p.IsVibrating)
                verdict = "🟡 BOTH_VIB — 両方振動（他要因が支配的）";
            else
                verdict = "⚪ NOT_CONFIRMED — 単体では振動差なし";

            sb.AppendLine($"  {label}:");
            sb.AppendLine($"    {verdict}");
            sb.AppendLine($"    baseline: osc={b.OscillationFreqHz:F2}Hz angVel={b.MaxAngularVelocity:F3}");
            sb.AppendLine($"    problem:  osc={p.OscillationFreqHz:F2}Hz angVel={p.MaxAngularVelocity:F3}");
        }

        // ─── GUI ─────────────────────────────────────────────────
        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(10, 10, 520, Screen.height - 20));
            GUILayout.BeginVertical("box");

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold };
            GUILayout.Label("Vibration Test Harness", titleStyle);
            GUILayout.Label(_statusMessage);

            if (_phase == Phase.Idle || _phase == Phase.Done)
            {
                if (GUILayout.Button("▶ Run All Tests  [Space]"))
                    RunAllTestsExternal();
            }
            else
            {
                GUILayout.Label($"  Progress: {_currentTestIndex}/{_testCases?.Count ?? 0}");
            }

            if (_results.Count > 0)
            {
                GUILayout.Space(6);
                GUILayout.Label($"Results ({_results.Count} tests):");
                _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(Screen.height - 220));
                var resultStyle = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 11 };
                foreach (var r in _results)
                {
                    string color   = r.IsVibrating ? "red" : "lime";
                    string verdict = r.IsVibrating ? "⚠ VIB" : "✓ OK ";
                    GUILayout.Label(
                        $"<color={color}>{verdict}</color> {r.TestName}\n" +
                        $"  osc={r.OscillationFreqHz:F1}Hz  angVel={r.MaxAngularVelocity:F2}  posRange={r.PositionRange:F4}m",
                        resultStyle);
                }
                GUILayout.EndScrollView();
            }

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }
    }

    // ─── テストケース定義 ─────────────────────────────────────────
    public class TestCase
    {
        public string Name;
        public string Description;
        public Func<TestRigContext> RigFactory;
        public Action<TestRigContext> PerFixedUpdate;

        public TestCase(string name, string desc,
            Func<TestRigContext> factory, Action<TestRigContext> perFixed)
        {
            Name           = name;
            Description    = desc;
            RigFactory     = factory;
            PerFixedUpdate = perFixed;
        }
    }

    // ─── テストリグコンテキスト ───────────────────────────────────
    public class TestRigContext
    {
        public Rigidbody Root;
        public Rigidbody Anchor;
        public ConfigurableJoint Joint;
        public Vector3 TargetPosition;
        public Vector3 SimulatedNetVelocity;

        public void Destroy()
        {
            if (Root   != null) UnityEngine.Object.Destroy(Root.gameObject);
            if (Anchor != null) UnityEngine.Object.Destroy(Anchor.gameObject);
        }
    }

    // ─── 振動計測サンプラー ───────────────────────────────────────
    public class VibrationSampler
    {
        private struct PhysicsSample
        {
            public float time;
            public float posY;
            public float velY;
            public float angVelMag;
        }

        private readonly List<PhysicsSample> _samples = new List<PhysicsSample>(512);

        public void Sample(Rigidbody rb, float time)
        {
            _samples.Add(new PhysicsSample
            {
                time      = time,
                posY      = rb.position.y,
                velY      = rb.linearVelocity.y,
                angVelMag = rb.angularVelocity.magnitude
            });
        }

        public TestResult Compute(string testName, string description)
        {
            if (_samples.Count < 2)
                return new TestResult { TestName = testName, Description = description };

            float minY = float.MaxValue, maxY = float.MinValue;
            float maxAngVel = 0f;
            int signChanges = 0;
            bool prevPositive = _samples[0].velY >= 0f;
            float duration = _samples[_samples.Count - 1].time - _samples[0].time;

            // 最初の0.3秒は過渡応答として除外
            float skipUntil = _samples[0].time + 0.3f;

            for (int i = 0; i < _samples.Count; i++)
            {
                var s = _samples[i];
                if (s.time < skipUntil) continue;

                if (s.posY < minY) minY = s.posY;
                if (s.posY > maxY) maxY = s.posY;
                if (s.angVelMag > maxAngVel) maxAngVel = s.angVelMag;

                bool curPositive = s.velY >= 0f;
                if (curPositive != prevPositive) signChanges++;
                prevPositive = curPositive;
            }

            float steadyDuration = Mathf.Max(duration - 0.3f, 0.001f);
            float oscFreqHz = signChanges / (2f * steadyDuration);
            float posRange  = (minY == float.MaxValue) ? 0f : (maxY - minY);

            return new TestResult
            {
                TestName           = testName,
                Description        = description,
                OscillationFreqHz  = oscFreqHz,
                MaxAngularVelocity = maxAngVel,
                PositionRange      = posRange,
                SampleCount        = _samples.Count
            };
        }
    }

    // ─── テスト結果 ───────────────────────────────────────────────
    public class TestResult
    {
        public string TestName;
        public string Description;
        public float OscillationFreqHz;
        public float MaxAngularVelocity;
        public float PositionRange;
        public int SampleCount;

        private const float OscFreqThreshold = 3f;
        private const float AngVelThreshold  = 5f;

        public bool IsVibrating =>
            OscillationFreqHz > OscFreqThreshold || MaxAngularVelocity > AngVelThreshold;

        public string ToDetailString() =>
            $"  oscFreq={OscillationFreqHz:F2}Hz (thresh>{OscFreqThreshold}Hz)  " +
            $"maxAngVel={MaxAngularVelocity:F3}rad/s (thresh>{AngVelThreshold})  " +
            $"posRange={PositionRange:F4}m  samples={SampleCount}";
    }
}
