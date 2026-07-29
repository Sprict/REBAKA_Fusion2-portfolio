using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Fusion;
using MyFolder.Scripts.Player;
using UnityEngine;

namespace MyFolder.Scripts.Diagnostics
{
    /// <summary>
    /// ラグドール物理同期のA/B比較検証用メトリクス収集コンポーネント。
    /// ホスト-クライアント間の同期品質を計測し、合否基準で判定する。
    ///
    /// 合否基準:
    ///   M1: テレポートしない — ルートの1フレーム移動量 &lt; 1.0m
    ///   M2: 振動しない — 静止時ルート位置振幅 &lt; 0.1m
    ///   M3: ルート追従精度 — ホスト-クライアント間位置誤差 &lt; 0.3m (avg)
    ///   M4: ラバーバンディング — 方向反転回数 &lt; 3/秒
    ///   M5: 回転追従精度 — ホスト-クライアント間回転誤差 &lt; 30度 (avg)
    ///   M6: 帯域消費 — 1プレイヤーあたり &lt; 30KB/s (推定値)
    ///   M7: 衝突時ドリフト — 壁/床接触中のホスト-クライアント差 &lt; 0.5m
    ///
    /// 使い方:
    ///   1. シーンに空のGameObjectを配置し、このコンポーネントをアタッチ
    ///   2. Playモードで自動的にラグドールを検出して計測開始
    ///   3. recordDurationSeconds 経過後にサマリーを出力
    ///   4. CSVは Application.persistentDataPath に保存
    /// </summary>
    public class SyncMetricsRecorder : MonoBehaviour
    {
        [Header("Recording Settings")]
        [SerializeField] private float recordDurationSeconds = 30f;
        [SerializeField] private bool autoStartRecording = true;
        [SerializeField] private int csvBufferSize = 100;

        [Header("Pass/Fail Thresholds")]
        [SerializeField] private float m1TeleportThreshold = 1.0f;
        [SerializeField] private float m2VibrationAmplitudeThreshold = 0.1f;
        [SerializeField] private float m3PositionErrorThreshold = 0.3f;
        [SerializeField] private float m4RubberbandingFreqThreshold = 3f;
        [SerializeField] private float m5RotationErrorThreshold = 30f;
        [SerializeField] private float m6BandwidthThresholdKBps = 30f;
        [SerializeField] private float m7CollisionDriftThreshold = 0.5f;

        [Header("Scenario Detection")]
        [Tooltip("ルート速度がこの値以下なら静止と判定")]
        [SerializeField] private float idleVelocityThreshold = 0.3f;
        [Tooltip("接触判定用: ルート近傍のRaycast距離")]
        [SerializeField] private float wallContactRayDistance = 0.5f;

        [Header("Data Quality")]
        [Tooltip("初期化後、このスポーン安定化期間（秒）はデータ収集をスキップする")]
        [SerializeField] private float warmupSeconds = 3f;

        // 状態
        private bool _recording;
        private float _recordStartTime;
        private float _warmupEndTime;
        private bool _warmupDone;
        private NetworkRunner _runner;
        private RagdollController _controller;
        private IProxyPoseSource _poseSource;
        private Rigidbody _rootRb;
        private bool _initialized;
        private bool _isHostSelfMeasurement;

        // CSV
        private StringBuilder _csvBuffer;
        private string _csvFilePath;
        private int _csvLineCount;

        // サンプルデータ
        private readonly List<MetricSample> _samples = new List<MetricSample>(4096);
        private Vector3 _prevRootPos;

        // M8: 描画フレームドメインのスムーズさ計測（Update で収集）
        private readonly List<float> _renderSpeeds = new List<float>(8192);
        private Vector3 _prevRenderPos;
        private bool _hasPrevRenderPos;

        // 結果
        public bool IsDone { get; private set; }
        public string Summary { get; private set; } = "";

        private struct MetricSample
        {
            public float time;
            public Vector3 localRootPos;
            public Quaternion localRootRot;
            public Vector3 localRootVel;
            public Vector3 netRootPos;
            public Quaternion netRootRot;
            public Vector3 netRootVel;
            public float positionError;
            public float rotationErrorDeg;
            public float frameDeltaPos;
            public float dotMovement;
            public bool isIdle;
            public bool isContactingWall;
            public string playerState;
        }

        private const string CsvHeader =
            "time,local_root_x,local_root_y,local_root_z," +
            "net_root_x,net_root_y,net_root_z," +
            "local_vel_x,local_vel_y,local_vel_z," +
            "pos_error,rot_error_deg,frame_delta_pos," +
            "dot_movement,is_idle,is_contacting_wall,state";

        private void Start()
        {
            if (autoStartRecording)
                StartRecording();
        }

        public void StartRecording()
        {
            if (_recording) return;

            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _csvFilePath = Path.Combine(Application.persistentDataPath, $"sync_metrics_{timestamp}.csv");
            _csvBuffer = new StringBuilder(4096);
            _csvBuffer.AppendLine(CsvHeader);
            _csvLineCount = 0;
            _samples.Clear();
            _renderSpeeds.Clear();
            _hasPrevRenderPos = false;
            _recording = true;
            _recordStartTime = Time.time;
            _warmupDone = false;
            _initialized = false;
            _isHostSelfMeasurement = false;
            IsDone = false;
            Summary = "";

            Debug.Log($"[SyncMetricsRecorder] Recording started. Duration={recordDurationSeconds}s, Output={_csvFilePath}");
        }

        private void FixedUpdate()
        {
            if (!_recording) return;

            // ランナー/コントローラー検出
            if (!_initialized)
            {
                if (!TryInitialize()) return;
            }

            if (_rootRb == null || _controller == null) return;

            // ウォームアップ期間: スポーン直後の位置ジャンプを除外
            if (!_warmupDone)
            {
                if (Time.time < _warmupEndTime)
                {
                    _prevRootPos = _rootRb.position;
                    return;
                }
                _warmupDone = true;
                _recordStartTime = Time.time;
                _prevRootPos = _rootRb.position;
                Debug.Log($"[SyncMetricsRecorder] Warmup done. Recording {recordDurationSeconds}s from now.");
            }

            // 記録期間終了チェック
            if (Time.time - _recordStartTime >= recordDurationSeconds)
            {
                StopAndEvaluate();
                return;
            }

            RecordSample();
        }

        private void Update()
        {
            // M8: 描画フレーム単位の Root 移動速度を収集する。
            // tick ドメイン（FixedUpdate）では見えない描画のガタつき
            // （スナップショット到着ジッタ・補間の有無）を定量化する。
            if (!_recording || !_initialized || !_warmupDone || _rootRb == null)
            {
                _hasPrevRenderPos = false;
                return;
            }

            Vector3 renderPos = _rootRb.transform.position;
            if (_hasPrevRenderPos && Time.deltaTime > 0.0001f)
            {
                _renderSpeeds.Add(Vector3.Distance(renderPos, _prevRenderPos) / Time.deltaTime);
            }

            _prevRenderPos = renderPos;
            _hasPrevRenderPos = true;
        }

        private bool TryInitialize()
        {
            _runner = FindFirstObjectByType<NetworkRunner>();
            if (_runner == null) return false;

            // クライアント側のラグドールを探す（StateAuthorityを持たないもの）
            foreach (var ctrl in FindObjectsByType<RagdollController>(FindObjectsSortMode.None))
            {
                var no = ctrl.GetComponent<NetworkObject>();
                if (no == null) continue;

                // クライアント側プロキシを優先（ホスト-クライアント差を計測するため）
                // ホスト単体テストの場合はStateAuthority持ちでもOK
                _controller = ctrl;
                _poseSource = ctrl as IProxyPoseSource;
                break;
            }

            if (_controller == null)
            {
                // フォールバック: NetworkBehaviour経由で検索
                foreach (var nb in FindObjectsByType<NetworkBehaviour>(FindObjectsSortMode.None))
                {
                    var rc = nb.GetComponent<RagdollController>();
                    if (rc != null)
                    {
                        _controller = rc;
                        _poseSource = rc as IProxyPoseSource;
                        break;
                    }
                }

                if (_controller == null) return false;
            }
            if (_poseSource == null)
            {
                Debug.LogWarning($"[SyncMetricsRecorder] IProxyPoseSource not implemented on {_controller.name}");
                return false;
            }

            // ルートRigidbody検出
            // FusionのNested NetworkObjectによりAPR_Rootはシーンルートに移動するため、
            // GetComponentsInChildrenではなくRagdollControllerのSerializeField参照を使う
            var bodyRbs = _controller.BodyRigidbodies;
            if (bodyRbs != null && bodyRbs.Length > 0 && bodyRbs[0] != null)
                _rootRb = bodyRbs[0];

            if (_rootRb == null)
            {
                Debug.LogWarning($"[SyncMetricsRecorder] Root Rigidbody not found. controller={_controller.name}, bodyRbs={bodyRbs?.Length ?? -1}");
                return false;
            }

            // ホスト自己計測判定（M3/M5が無意味になる）
            var controllerNO = _controller.GetComponent<NetworkObject>();
            _isHostSelfMeasurement = controllerNO != null && controllerNO.HasStateAuthority;

            _prevRootPos = _rootRb.position;
            _initialized = true;
            _warmupEndTime = Time.time + warmupSeconds;
            Debug.Log($"[SyncMetricsRecorder] Initialized. Controller={_controller.name}, Root={_rootRb.name}, Host={_isHostSelfMeasurement}. Warmup {warmupSeconds}s then recording {recordDurationSeconds}s.");
            return true;
        }

        private void RecordSample()
        {
            Vector3 localPos = _rootRb.position;
            Quaternion localRot = _rootRb.rotation;
            Vector3 localVel = _rootRb.linearVelocity;

            Vector3 netPos = _poseSource.NetRootPosition;
            Quaternion netRot = _poseSource.NetRootRotation;
            Vector3 netVel = _poseSource.NetRootLinearVelocity;

            float posError = Vector3.Distance(localPos, netPos);
            float rotError = Quaternion.Angle(localRot, netRot);
            float frameDelta = Vector3.Distance(localPos, _prevRootPos);

            // オーバーシュート検出
            float dot = 0f;
            if (localVel.sqrMagnitude > 0.1f)
            {
                Vector3 toNet = netPos - localPos;
                dot = Vector3.Dot(toNet.normalized, localVel.normalized);
            }

            // idle判定: kinematicモードではlocalVelが常に0なので、
            // ネットワーク速度とフレーム間移動量の両方で判定する
            float effectiveSpeed = Mathf.Max(
                netVel.magnitude,
                frameDelta / Mathf.Max(Time.fixedDeltaTime, 0.001f));
            bool isIdle = effectiveSpeed < idleVelocityThreshold;

            // 壁/床接触判定（簡易: 水平方向のRaycast）
            bool isContactingWall = false;
            if (localVel.sqrMagnitude > 0.01f)
            {
                Vector3 horizVel = new Vector3(localVel.x, 0f, localVel.z);
                if (horizVel.sqrMagnitude > 0.01f)
                {
                    isContactingWall = Physics.Raycast(
                        localPos, horizVel.normalized, wallContactRayDistance,
                        ~LayerMask.GetMask("Player"));
                }
            }

            string state = _poseSource.CurrentStateName ?? "Unknown";

            var sample = new MetricSample
            {
                time = Time.time - _recordStartTime,
                localRootPos = localPos,
                localRootRot = localRot,
                localRootVel = localVel,
                netRootPos = netPos,
                netRootRot = netRot,
                netRootVel = netVel,
                positionError = posError,
                rotationErrorDeg = rotError,
                frameDeltaPos = frameDelta,
                dotMovement = dot,
                isIdle = isIdle,
                isContactingWall = isContactingWall,
                playerState = state
            };

            _samples.Add(sample);
            WriteCsvLine(sample);

            _prevRootPos = localPos;
        }

        private void WriteCsvLine(in MetricSample s)
        {
            var ci = CultureInfo.InvariantCulture;
            _csvBuffer.Append(s.time.ToString("F4", ci)).Append(',');
            _csvBuffer.Append(s.localRootPos.x.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.localRootPos.y.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.localRootPos.z.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.netRootPos.x.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.netRootPos.y.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.netRootPos.z.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.localRootVel.x.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.localRootVel.y.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.localRootVel.z.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.positionError.ToString("F4", ci)).Append(',');
            _csvBuffer.Append(s.rotationErrorDeg.ToString("F1", ci)).Append(',');
            _csvBuffer.Append(s.frameDeltaPos.ToString("F4", ci)).Append(',');
            _csvBuffer.Append(s.dotMovement.ToString("F3", ci)).Append(',');
            _csvBuffer.Append(s.isIdle ? 1 : 0).Append(',');
            _csvBuffer.Append(s.isContactingWall ? 1 : 0).Append(',');
            _csvBuffer.AppendLine(s.playerState);

            _csvLineCount++;
            if (_csvLineCount >= csvBufferSize)
                FlushCsv();
        }

        private void FlushCsv()
        {
            if (_csvBuffer == null || _csvBuffer.Length == 0 || string.IsNullOrEmpty(_csvFilePath))
                return;

            try
            {
                File.AppendAllText(_csvFilePath, _csvBuffer.ToString());
                _csvBuffer.Clear();
                _csvLineCount = 0;
            }
            catch (Exception e)
            {
                Debug.LogError($"[SyncMetricsRecorder] CSV write failed: {e.Message}");
            }
        }

        private void StopAndEvaluate()
        {
            _recording = false;
            FlushCsv();

            if (_samples.Count < 10)
            {
                Summary = "[SyncMetricsRecorder] Not enough samples to evaluate.";
                Debug.LogWarning(Summary);
                IsDone = true;
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("=== SYNC METRICS EVALUATION ===");
            sb.AppendLine($"Samples: {_samples.Count} | Duration: {recordDurationSeconds}s | CSV: {_csvFilePath}");
            if (_isHostSelfMeasurement)
                sb.AppendLine("  NOTE: Host self-measurement — M3/M5 compare local physics with its own network state (always ~0). Use client results for sync quality.");
            sb.AppendLine();

            int passCount = 0;
            int totalMetrics = 7;

            // M1: テレポート検出
            {
                float maxFrameDelta = _samples.Max(s => s.frameDeltaPos);
                int teleportCount = _samples.Count(s => s.frameDeltaPos > m1TeleportThreshold);
                bool pass = teleportCount == 0;
                if (pass) passCount++;
                sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M1_NO_TELEPORT: maxFrameDelta={maxFrameDelta:F3}m, teleportFrames={teleportCount} (threshold={m1TeleportThreshold}m)");
            }

            // M2: 振動検出（静止時）
            {
                var idleSamples = _samples.Where(s => s.isIdle).ToList();
                if (idleSamples.Count > 10)
                {
                    float minY = idleSamples.Min(s => s.localRootPos.y);
                    float maxY = idleSamples.Max(s => s.localRootPos.y);
                    float amplitude = maxY - minY;
                    bool pass = amplitude < m2VibrationAmplitudeThreshold;
                    if (pass) passCount++;
                    sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M2_NO_VIBRATION: idleYAmplitude={amplitude:F4}m ({idleSamples.Count} idle frames, threshold={m2VibrationAmplitudeThreshold}m)");
                }
                else
                {
                    sb.AppendLine($"  SKIP M2_NO_VIBRATION: insufficient idle samples ({idleSamples.Count})");
                    totalMetrics--;
                }
            }

            // M3: ルート追従精度
            {
                float avgPosErr = _samples.Average(s => s.positionError);
                float maxPosErr = _samples.Max(s => s.positionError);
                float p95PosErr = Percentile(_samples.Select(s => s.positionError), 95);
                bool pass = avgPosErr < m3PositionErrorThreshold;
                if (pass) passCount++;
                sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M3_POS_TRACKING: avg={avgPosErr:F3}m, p95={p95PosErr:F3}m, max={maxPosErr:F3}m (threshold={m3PositionErrorThreshold}m)");
            }

            // M4: ラバーバンディング
            {
                var movingSamples = _samples.Where(s => !s.isIdle).ToList();
                if (movingSamples.Count > 10)
                {
                    int overshootCount = 0;
                    for (int i = 1; i < movingSamples.Count; i++)
                    {
                        if (movingSamples[i - 1].dotMovement > 0f && movingSamples[i].dotMovement < -0.3f)
                            overshootCount++;
                    }
                    float movingDuration = movingSamples.Last().time - movingSamples.First().time;
                    float freq = movingDuration > 0.1f ? overshootCount / movingDuration : 0f;
                    bool pass = freq < m4RubberbandingFreqThreshold;
                    if (pass) passCount++;
                    sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M4_NO_RUBBERBANDING: overshoots={overshootCount}, freq={freq:F1}/s ({movingSamples.Count} moving frames, threshold={m4RubberbandingFreqThreshold}/s)");
                }
                else
                {
                    sb.AppendLine($"  SKIP M4_NO_RUBBERBANDING: insufficient moving samples ({movingSamples.Count})");
                    totalMetrics--;
                }
            }

            // M5: 回転追従精度
            {
                float avgRotErr = _samples.Average(s => s.rotationErrorDeg);
                float maxRotErr = _samples.Max(s => s.rotationErrorDeg);
                bool pass = avgRotErr < m5RotationErrorThreshold;
                if (pass) passCount++;
                sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M5_ROT_TRACKING: avg={avgRotErr:F1}deg, max={maxRotErr:F1}deg (threshold={m5RotationErrorThreshold}deg)");
            }

            // M6: 帯域推定
            {
                // [Networked]プロパティの推定サイズ:
                // Root: pos(12) + rot(16) + linVel(12) + angVel(12) = 52 bytes
                // Head: pos(12) + rot(16) = 28 bytes
                // LeftHand: pos(12) + rot(16) = 28 bytes
                // RightHand: pos(12) + rot(16) = 28 bytes
                // State + Flags: ~20 bytes
                // 合計: ~156 bytes/tick
                float estimatedBytesPerTick = 156f;

                // SnapshotInterpolation モード: 14パーツ相対ポーズ + TeleportKey を加算
                // NetPartPositions: 14 * 12 = 168 bytes, NetPartRotations: 14 * 16 = 224 bytes, key: 4 bytes
                if (_controller != null &&
                    _controller.ResolvedProxySyncMode == ProxySyncMode.SnapshotInterpolation)
                {
                    estimatedBytesPerTick += 396f;
                }
                float tickRate = _runner != null && _runner.DeltaTime > 0f ? 1f / _runner.DeltaTime : 60f;
                float estimatedKBps = estimatedBytesPerTick * tickRate / 1024f;
                bool pass = estimatedKBps < m6BandwidthThresholdKBps;
                if (pass) passCount++;
                sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M6_BANDWIDTH: estimated={estimatedKBps:F1}KB/s @ {tickRate}Hz (threshold={m6BandwidthThresholdKBps}KB/s)");
                sb.AppendLine($"         Note: This is an estimate based on [Networked] property sizes. Actual bandwidth may differ.");
            }

            // M7: 衝突時ドリフト
            {
                var contactSamples = _samples.Where(s => s.isContactingWall).ToList();
                if (contactSamples.Count > 5)
                {
                    float avgContactErr = contactSamples.Average(s => s.positionError);
                    float maxContactErr = contactSamples.Max(s => s.positionError);
                    bool pass = maxContactErr < m7CollisionDriftThreshold;
                    if (pass) passCount++;
                    sb.AppendLine($"  {(pass ? "PASS" : "FAIL")} M7_COLLISION_DRIFT: avg={avgContactErr:F3}m, max={maxContactErr:F3}m ({contactSamples.Count} contact frames, threshold={m7CollisionDriftThreshold}m)");
                }
                else
                {
                    sb.AppendLine($"  SKIP M7_COLLISION_DRIFT: insufficient wall contact samples ({contactSamples.Count})");
                    totalMetrics--;
                }
            }

            // M8: 描画スムーズさ（情報のみ、合否判定なし）
            // モード間 A/B 比較用: 移動中の描画フレーム速度の変動係数（CV）が小さいほどスムーズ
            {
                var movingRenderSpeeds = _renderSpeeds.Where(v => v > idleVelocityThreshold).ToList();
                if (movingRenderSpeeds.Count > 30)
                {
                    float mean = movingRenderSpeeds.Average();
                    float variance = movingRenderSpeeds.Average(v => (v - mean) * (v - mean));
                    float stdev = Mathf.Sqrt(variance);
                    float cv = mean > 0.001f ? stdev / mean : 0f;
                    sb.AppendLine($"  INFO M8_RENDER_SMOOTHNESS: meanSpeed={mean:F3}m/s, stdev={stdev:F3}, cv={cv:F3} ({movingRenderSpeeds.Count} moving render frames) — モード間比較用（小さいほどスムーズ）");
                }
                else
                {
                    sb.AppendLine($"  SKIP M8_RENDER_SMOOTHNESS: insufficient moving render samples ({movingRenderSpeeds.Count})");
                }
            }

            sb.AppendLine();
            sb.AppendLine($"=== RESULT: {passCount}/{totalMetrics} PASSED ===");

            Summary = sb.ToString();
            Debug.Log("[SyncMetricsRecorder] " + Summary);
            IsDone = true;
        }

        private void OnDestroy()
        {
            if (_recording)
            {
                FlushCsv();
            }
        }

        private void OnApplicationQuit()
        {
            if (_recording)
            {
                StopAndEvaluate();
            }
        }

        /// <summary>
        /// 指定パーセンタイルの値を返す。
        /// </summary>
        private static float Percentile(IEnumerable<float> values, int percentile)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0f;
            int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
            return sorted[Mathf.Clamp(index, 0, sorted.Count - 1)];
        }

        /// <summary>
        /// 外部から手動で実行する場合のエントリポイント。
        /// </summary>
        public static string Execute(float durationSeconds = 30f)
        {
            if (!Application.isPlaying) return "ERROR: Not in Play Mode.";

            var existing = FindFirstObjectByType<SyncMetricsRecorder>();
            if (existing != null)
            {
                if (existing.IsDone) return existing.Summary;
                if (existing._recording) return $"RUNNING: {existing._samples.Count} samples collected, {existing.recordDurationSeconds - (Time.time - existing._recordStartTime):F0}s remaining";
            }

            var go = new GameObject("__SyncMetricsRecorder__");
            var recorder = go.AddComponent<SyncMetricsRecorder>();
            recorder.recordDurationSeconds = durationSeconds;
            recorder.StartRecording();
            return $"OK: Sync metrics recording started ({durationSeconds}s).";
        }
    }
}
