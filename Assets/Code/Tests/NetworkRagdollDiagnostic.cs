using UnityEngine;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Play Mode中にラグドールの物理状態を自動監視する診断ツール。
/// APR_Root のRigidbodyを直接検索して監視する。
/// </summary>
public class NetworkRagdollDiagnostic : MonoBehaviour
{
    [Header("Detection Thresholds")]
    public float groundSinkThresholdY = -0.5f;
    public float vibrationFreqThreshold = 5f;
    public float maxAngularVelocityThreshold = 10f;
    public float diagnosticDuration = 10f;

    private float _startTime;
    private bool _done;
    private Rigidbody _rootRb;
    private ConfigurableJoint[] _joints;
    private readonly List<DiagFrame> _frames = new List<DiagFrame>(1024);
    private readonly List<string> _issues = new List<string>();
    public bool IsDone => _done;
    public string ResultSummary { get; private set; } = "";

    private struct DiagFrame
    {
        public float time;
        public Vector3 rootPos;
        public Vector3 rootVel;
        public Vector3 rootAngVel;
        public bool isKinematic;
        public bool useGravity;
        public float balanceDamper;
        public float poseDamper;
    }

    private void Start()
    {
        _startTime = Time.time;
        _done = false;
        FindRagdoll();
        Debug.Log($"[RagdollDiag] Started. rootRb={((_rootRb != null) ? _rootRb.name : "NULL")}, joints={(_joints != null ? _joints.Length : 0)}");
    }

    private void FindRagdoll()
    {
        // APR_Root を名前で検索
        var allRbs = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
        foreach (var rb in allRbs)
        {
            if (rb.gameObject.name == "APR_Root")
            {
                _rootRb = rb;
                _joints = rb.GetComponentsInChildren<ConfigurableJoint>();
                return;
            }
        }
        // フォールバック: Player レイヤーの最初のRigidbody
        foreach (var rb in allRbs)
        {
            if (rb.gameObject.layer == LayerMask.NameToLayer("Player"))
            {
                _rootRb = rb;
                _joints = rb.GetComponentsInChildren<ConfigurableJoint>();
                return;
            }
        }
    }

    private void FixedUpdate()
    {
        if (_done) return;

        if (_rootRb == null)
        {
            FindRagdoll();
            if (_rootRb == null) return;
            _startTime = Time.time; // リスタート
        }

        if (Time.time - _startTime > diagnosticDuration)
        {
            FinishDiagnostic();
            return;
        }

        // バランスジョイント（APR_Root直接のConfigurableJoint）のダンパー
        float balanceDamper = 0f;
        float poseDamper = 0f;
        var rootJoint = _rootRb.GetComponent<ConfigurableJoint>();
        if (rootJoint != null)
            balanceDamper = rootJoint.angularXDrive.positionDamper;

        // 脚のジョイントからポーズダンパーを取得
        if (_joints != null && _joints.Length > 7)
            poseDamper = _joints[7].angularXDrive.positionDamper;

        _frames.Add(new DiagFrame
        {
            time = Time.time - _startTime,
            rootPos = _rootRb.position,
            rootVel = _rootRb.linearVelocity,
            rootAngVel = _rootRb.angularVelocity,
            isKinematic = _rootRb.isKinematic,
            useGravity = _rootRb.useGravity,
            balanceDamper = balanceDamper,
            poseDamper = poseDamper
        });
    }

    private void FinishDiagnostic()
    {
        _done = true;
        _issues.Clear();

        if (_frames.Count < 10)
        {
            _issues.Add("INSUFFICIENT_DATA: Frames=" + _frames.Count);
            ResultSummary = string.Join("\n", _issues);
            Debug.Log("[RagdollDiag] " + ResultSummary);
            return;
        }

        float minY = float.MaxValue, maxY = float.MinValue;
        float maxAngVel = 0f;
        int sinkFrames = 0;
        int velSignChanges = 0;
        bool prevUp = _frames[0].rootVel.y >= 0;
        float totalBalDamper = 0f, totalPoseDamper = 0f;
        int sampleCount = 0;

        // X/Z方向の振動も検出
        int velXChanges = 0, velZChanges = 0;
        bool prevXPos = _frames[0].rootVel.x >= 0;
        bool prevZPos = _frames[0].rootVel.z >= 0;

        // 最初の2秒をスキップ（過渡応答）
        float skipTime = 2f;

        for (int i = 0; i < _frames.Count; i++)
        {
            var f = _frames[i];
            if (f.time < skipTime) continue;

            if (f.rootPos.y < minY) minY = f.rootPos.y;
            if (f.rootPos.y > maxY) maxY = f.rootPos.y;
            if (f.rootAngVel.magnitude > maxAngVel) maxAngVel = f.rootAngVel.magnitude;
            if (f.rootPos.y < groundSinkThresholdY) sinkFrames++;

            bool curUp = f.rootVel.y >= 0;
            if (curUp != prevUp) velSignChanges++;
            prevUp = curUp;

            bool curXPos = f.rootVel.x >= 0;
            if (curXPos != prevXPos) velXChanges++;
            prevXPos = curXPos;

            bool curZPos = f.rootVel.z >= 0;
            if (curZPos != prevZPos) velZChanges++;
            prevZPos = curZPos;

            totalBalDamper += f.balanceDamper;
            totalPoseDamper += f.poseDamper;
            sampleCount++;
        }

        float steadyDur = Mathf.Max(diagnosticDuration - skipTime, 0.001f);
        float oscYHz = velSignChanges / (2f * steadyDur);
        float oscXHz = velXChanges / (2f * steadyDur);
        float oscZHz = velZChanges / (2f * steadyDur);
        float avgBalDamper = sampleCount > 0 ? totalBalDamper / sampleCount : 0;
        float avgPoseDamper = sampleCount > 0 ? totalPoseDamper / sampleCount : 0;

        var sb = new StringBuilder();
        sb.AppendLine($"=== HOST RAGDOLL DIAGNOSTIC ({_frames.Count} frames, {diagnosticDuration}s) ===");
        sb.AppendLine($"  Root Y range: [{minY:F3}, {maxY:F3}]");
        sb.AppendLine($"  Osc Y: {oscYHz:F1}Hz | X: {oscXHz:F1}Hz | Z: {oscZHz:F1}Hz");
        sb.AppendLine($"  Max angular vel: {maxAngVel:F2} rad/s");
        sb.AppendLine($"  Avg balance damper: {avgBalDamper:F1}");
        sb.AppendLine($"  Avg pose damper: {avgPoseDamper:F1}");
        sb.AppendLine($"  Sink frames: {sinkFrames}/{sampleCount}");

        // 最終5フレームの平均位置・速度
        if (_frames.Count >= 5)
        {
            Vector3 avgPos = Vector3.zero, avgVel = Vector3.zero;
            for (int i = _frames.Count - 5; i < _frames.Count; i++)
            {
                avgPos += _frames[i].rootPos;
                avgVel += _frames[i].rootVel;
            }
            avgPos /= 5;
            avgVel /= 5;
            sb.AppendLine($"  Final position: {avgPos}");
            sb.AppendLine($"  Final velocity: {avgVel}");
        }

        // 問題検出
        if (sinkFrames > sampleCount * 0.1f)
        {
            _issues.Add($"GROUND_SINK: Y<{groundSinkThresholdY} in {sinkFrames}/{sampleCount} frames (minY={minY:F3})");
        }
        if (oscYHz > vibrationFreqThreshold)
        {
            _issues.Add($"VIBRATION_Y: {oscYHz:F1}Hz > {vibrationFreqThreshold}Hz threshold");
        }
        if (oscXHz > vibrationFreqThreshold)
        {
            _issues.Add($"VIBRATION_X: {oscXHz:F1}Hz > {vibrationFreqThreshold}Hz threshold");
        }
        if (oscZHz > vibrationFreqThreshold)
        {
            _issues.Add($"VIBRATION_Z: {oscZHz:F1}Hz > {vibrationFreqThreshold}Hz threshold");
        }
        if (maxAngVel > maxAngularVelocityThreshold)
        {
            _issues.Add($"HIGH_ANG_VEL: {maxAngVel:F2} > {maxAngularVelocityThreshold}");
        }
        if (avgBalDamper < 1f && sampleCount > 0)
        {
            _issues.Add($"LOW_BALANCE_DAMPER: avg={avgBalDamper:F1}");
        }
        if (avgPoseDamper < 1f && sampleCount > 0)
        {
            _issues.Add($"LOW_POSE_DAMPER: avg={avgPoseDamper:F1}");
        }

        foreach (var issue in _issues)
            sb.AppendLine($"  🔴 {issue}");

        if (_issues.Count == 0)
            sb.AppendLine("  🟢 No issues detected!");

        ResultSummary = sb.ToString();
        Debug.Log("[RagdollDiag] " + ResultSummary);
    }

    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var existing = FindFirstObjectByType<NetworkRagdollDiagnostic>();
        if (existing != null)
        {
            if (existing.IsDone)
                return existing.ResultSummary;
            return $"RUNNING: {existing._frames.Count} frames, rootRb={(existing._rootRb != null ? existing._rootRb.name : "NULL")}";
        }

        var go = new GameObject("__RagdollDiagnostic__");
        var diag = go.AddComponent<NetworkRagdollDiagnostic>();
        diag.diagnosticDuration = 10f;
        return "OK: Diagnostic started.";
    }
}
