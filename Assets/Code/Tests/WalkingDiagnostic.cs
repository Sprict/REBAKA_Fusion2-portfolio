using UnityEngine;
using System.Collections.Generic;
using System.Text;
using Fusion;
using Fusion.Sockets;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Network;

/// <summary>
/// ホスト起動 → 移動入力注入 → 物理状態を記録 → 問題検出
/// InputCollector をバイパスして直接 NetworkInputData を注入する。
/// </summary>
public class WalkingDiagnostic : MonoBehaviour, INetworkRunnerCallbacks
{
    private enum Phase { WaitingForSpawn, Idle, Walking, Stopping, Done }

    private Phase _phase = Phase.WaitingForSpawn;
    private float _phaseStart;
    private NetworkRunner _runner;
    private RagdollController _ctrl;
    private Rigidbody _rootRb;
    private ConfigurableJoint[] _legJoints; // UpperRightLeg, LowerRightLeg, UpperLeftLeg, LowerLeftLeg

    // テスト設定
    private const float IdleDuration = 3f;
    private const float WalkDuration = 5f;
    private const float StopDuration = 2f;

    // 入力注入フラグ
    private bool _injectForwardInput;

    // 記録
    private readonly List<WalkFrame> _frames = new List<WalkFrame>(2048);

    private struct WalkFrame
    {
        public float time;
        public string phase;
        public Vector3 rootPos;
        public Vector3 rootVel;
        public float rootAngVelMag;
        public float legRDamper; // UpperRightLeg damper
        public float legRSpring;
        public float legRTargetX; // UpperRightLeg targetRotation.x
        public float speedHorizontal;
        public bool isGrounded;
    }

    public string ResultSummary { get; private set; } = "";
    public bool IsDone => _phase == Phase.Done;

    private void Start()
    {
        _phaseStart = Time.time;
    }

    private void FixedUpdate()
    {
        if (_phase == Phase.Done) return;

        // Runnerを探す
        if (_runner == null)
        {
            _runner = FindFirstObjectByType<NetworkRunner>();
            if (_runner != null)
            {
                _runner.AddCallbacks(this);
            }
            return;
        }

        // ラグドールを探す
        if (_rootRb == null)
        {
            var allRbs = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            foreach (var rb in allRbs)
            {
                if (rb.gameObject.name == "APR_Root" && rb.gameObject.layer == LayerMask.NameToLayer("Player"))
                {
                    _rootRb = rb;
                    _ctrl = rb.GetComponentInParent<RagdollController>();

                    // 脚ジョイントをキャッシュ
                    var joints = new List<ConfigurableJoint>();
                    var allJoints = rb.GetComponentsInChildren<ConfigurableJoint>();
                    foreach (var j in allJoints)
                    {
                        if (j.gameObject.name.Contains("UpperRightLeg") ||
                            j.gameObject.name.Contains("LowerRightLeg") ||
                            j.gameObject.name.Contains("UpperLeftLeg") ||
                            j.gameObject.name.Contains("LowerLeftLeg"))
                        {
                            joints.Add(j);
                        }
                    }
                    _legJoints = joints.ToArray();
                    break;
                }
            }
            if (_rootRb == null) return;
            _phase = Phase.Idle;
            _phaseStart = Time.time;
            Debug.Log("[WalkDiag] Ragdoll found. Starting idle phase.");
        }

        float elapsed = Time.time - _phaseStart;

        switch (_phase)
        {
            case Phase.Idle:
                _injectForwardInput = false;
                if (elapsed >= IdleDuration)
                {
                    _phase = Phase.Walking;
                    _phaseStart = Time.time;
                    _injectForwardInput = true;
                    Debug.Log("[WalkDiag] Walking phase started.");
                }
                break;

            case Phase.Walking:
                _injectForwardInput = true;
                if (elapsed >= WalkDuration)
                {
                    _phase = Phase.Stopping;
                    _phaseStart = Time.time;
                    _injectForwardInput = false;
                    Debug.Log("[WalkDiag] Stopping phase started.");
                }
                break;

            case Phase.Stopping:
                _injectForwardInput = false;
                if (elapsed >= StopDuration)
                {
                    _phase = Phase.Done;
                    AnalyzeResults();
                    Debug.Log("[WalkDiag] Done.");
                }
                break;
        }

        RecordFrame();
    }

    private void RecordFrame()
    {
        if (_rootRb == null) return;

        float legDamper = 0f, legSpring = 0f, legTargetX = 0f;
        if (_legJoints != null && _legJoints.Length > 0)
        {
            legDamper = _legJoints[0].angularXDrive.positionDamper;
            legSpring = _legJoints[0].angularXDrive.positionSpring;
            legTargetX = _legJoints[0].targetRotation.x;
        }

        var vel = _rootRb.linearVelocity;
        _frames.Add(new WalkFrame
        {
            time = Time.time,
            phase = _phase.ToString(),
            rootPos = _rootRb.position,
            rootVel = vel,
            rootAngVelMag = _rootRb.angularVelocity.magnitude,
            legRDamper = legDamper,
            legRSpring = legSpring,
            legRTargetX = legTargetX,
            speedHorizontal = new Vector2(vel.x, vel.z).magnitude,
            isGrounded = _ctrl != null && _ctrl.IsPlayerGrounded()
        });
    }

    private void AnalyzeResults()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== WALKING DIAGNOSTIC ({_frames.Count} frames) ===");

        // フェーズ毎に分析
        AnalyzePhase(sb, "Idle");
        AnalyzePhase(sb, "Walking");
        AnalyzePhase(sb, "Stopping");

        // JointDrive 確認
        if (_frames.Count > 0)
        {
            var last = _frames[_frames.Count - 1];
            sb.AppendLine($"\n  Leg Joint: spring={last.legRSpring:F0} damper={last.legRDamper:F1}");
            sb.AppendLine($"  Damper/Spring ratio: {(last.legRSpring > 0 ? last.legRDamper / last.legRSpring : 0):F3}");
        }

        ResultSummary = sb.ToString();
        Debug.Log("[WalkDiag] " + ResultSummary);
    }

    private void AnalyzePhase(StringBuilder sb, string phaseName)
    {
        float minY = float.MaxValue, maxY = float.MinValue;
        float maxHSpeed = 0f, avgHSpeed = 0f;
        float minTargetX = float.MaxValue, maxTargetX = float.MinValue;
        int count = 0;
        int groundedCount = 0;

        foreach (var f in _frames)
        {
            if (f.phase != phaseName) continue;
            count++;
            if (f.rootPos.y < minY) minY = f.rootPos.y;
            if (f.rootPos.y > maxY) maxY = f.rootPos.y;
            if (f.speedHorizontal > maxHSpeed) maxHSpeed = f.speedHorizontal;
            avgHSpeed += f.speedHorizontal;
            if (f.legRTargetX < minTargetX) minTargetX = f.legRTargetX;
            if (f.legRTargetX > maxTargetX) maxTargetX = f.legRTargetX;
            if (f.isGrounded) groundedCount++;
        }

        if (count == 0) return;
        avgHSpeed /= count;

        sb.AppendLine($"\n  [{phaseName}] ({count} frames)");
        sb.AppendLine($"    Y range: [{minY:F3}, {maxY:F3}] (Δ={maxY - minY:F3})");
        sb.AppendLine($"    Speed: avg={avgHSpeed:F3} max={maxHSpeed:F3} m/s");
        sb.AppendLine($"    Leg targetX range: [{minTargetX:F3}, {maxTargetX:F3}]");
        sb.AppendLine($"    Grounded: {groundedCount}/{count}");
    }

    // --- INetworkRunnerCallbacks: 入力注入 ---
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        if (!_injectForwardInput) return;

        // 前方に歩行入力を注入
        var data = new NetworkInputData();
        data.direction = new Vector3(0, 0, 1); // forward
        input.Set(data);
    }

    // 未使用コールバック
    public void OnConnectedToServer(NetworkRunner r) { }
    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest req, byte[] token) { }
    public void OnConnectFailed(NetworkRunner r, NetAddress addr, NetConnectFailedReason reason) { }
    public void OnShutdown(NetworkRunner r, ShutdownReason reason) { }
    public void OnPlayerJoined(NetworkRunner r, PlayerRef p) { }
    public void OnPlayerLeft(NetworkRunner r, PlayerRef p) { }
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput input) { }
    public void OnSessionListUpdated(NetworkRunner r, System.Collections.Generic.List<SessionInfo> sessions) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, System.Collections.Generic.Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken token) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef p, ReliableKey key, System.ReadOnlySpan<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef p, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner r) { }
    public void OnSceneLoadStart(NetworkRunner r) { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef p) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef p) { }

    // --- エントリポイント ---
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var existing = FindFirstObjectByType<WalkingDiagnostic>();
        if (existing != null)
        {
            if (existing.IsDone)
                return existing.ResultSummary;
            return $"RUNNING: phase={existing._phase} frames={existing._frames.Count}";
        }

        var go = new GameObject("__WalkingDiagnostic__");
        var diag = go.AddComponent<WalkingDiagnostic>();
        return "OK: Walking diagnostic started (~10s).";
    }
}
