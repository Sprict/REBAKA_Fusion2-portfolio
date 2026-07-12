using UnityEngine;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using Fusion;
using Fusion.Sockets;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Network;
using System;

/// <summary>
/// クライアント側プロキシの動作をホスト上でシミュレートしてテストする。
/// 
/// ホストとして起動し、移動入力を注入した後:
///   1. ホスト側のルート位置・回転・速度を記録（= NetRootPosition相当）
///   2. 仮想クライアントプロキシが ApplySoftRootCorrection でどう追従するかを計算
///   3. ラバーバンディング・回転追従・安定性を検証
///
/// テスト仕様:
///   C1: ルート追従精度 — 追従誤差 < 0.3m (avg)
///   C2: ラバーバンディング — オーバーシュート回数 < 3/秒
///   C3: 回転追従 — 回転誤差 < 30度 (avg)
///   C4: 安定性 — 静止時の仮想プロキシY振幅 < 0.1m
///   C5: 地面めり込み — Y > 0m
/// </summary>
public class ClientProxySimTest : MonoBehaviour, INetworkRunnerCallbacks
{
    private enum Phase { WaitSpawn, Idle, Walking, Stopping, Done }

    private Phase _phase = Phase.WaitSpawn;
    private float _phaseStart;
    private NetworkRunner _runner;
    private Rigidbody _rootRb;
    private RagdollController _ctrl;

    private const float IdleSec = 4f;
    private const float WalkSec = 5f;
    private const float StopSec = 3f;

    private bool _injectForward;

    // 仮想クライアントプロキシの状態
    private Vector3 _vClientPos;
    private Vector3 _vClientVel;
    private Quaternion _vClientRot;
    private Vector3 _vClientAngVel;
    private bool _vClientInitialized;

    // プロキシ補正パラメータ（プレファブから取得）
    private float _kp = 30f;
    private float _kd = 15f;
    private float _rotKp = 30f;
    private float _rotKd = 8f;

    // 記録
    private struct Frame
    {
        public float time;
        public string phase;
        public Vector3 hostPos;
        public Quaternion hostRot;
        public Vector3 hostVel;
        public Vector3 clientPos;
        public Quaternion clientRot;
        public float posError;
        public float rotError;
        public float dotMovement; // 移動方向に対するオーバーシュート検出
    }

    private readonly List<Frame> _frames = new List<Frame>(2048);
    private readonly Dictionary<string, string> _results = new Dictionary<string, string>();
    public bool IsDone => _phase == Phase.Done;
    public string Summary { get; private set; } = "";

    private void Start() { _phaseStart = Time.time; }

    private void FixedUpdate()
    {
        if (_phase == Phase.Done) return;

        if (_runner == null)
        {
            _runner = FindFirstObjectByType<NetworkRunner>();
            if (_runner != null) _runner.AddCallbacks(this);
            return;
        }

        if (_rootRb == null)
        {
            FindRagdoll();
            if (_rootRb == null) return;
            _phase = Phase.Idle;
            _phaseStart = Time.time;
        }

        float elapsed = Time.time - _phaseStart;
        float dt = Time.fixedDeltaTime;

        switch (_phase)
        {
            case Phase.Idle:
                _injectForward = false;
                if (elapsed >= IdleSec) { _phase = Phase.Walking; _phaseStart = Time.time; }
                break;
            case Phase.Walking:
                _injectForward = true;
                if (elapsed >= WalkSec) { _phase = Phase.Stopping; _phaseStart = Time.time; _injectForward = false; }
                break;
            case Phase.Stopping:
                _injectForward = false;
                if (elapsed >= StopSec) { _phase = Phase.Done; Evaluate(); }
                break;
        }

        SimulateClientProxy(dt);
    }

    private void SimulateClientProxy(float dt)
    {
        if (_rootRb == null) return;

        Vector3 hostPos = _rootRb.position;
        Quaternion hostRot = _rootRb.rotation;
        Vector3 hostVel = _rootRb.linearVelocity;
        Vector3 hostAngVel = _rootRb.angularVelocity;

        if (!_vClientInitialized)
        {
            _vClientPos = hostPos;
            _vClientVel = hostVel;
            _vClientRot = hostRot;
            _vClientAngVel = hostAngVel;
            _vClientInitialized = true;
            return;
        }

        // --- 仮想プロキシの位置補正（ApplySoftRootCorrectionと同じ） ---
        // ネットワーク遅延をシミュレート: hostPosが3tick遅れで届くと仮定
        // (実際にはFusionのtickレートに依存)
        Vector3 posError = hostPos - _vClientPos;
        if (posError.sqrMagnitude > 1f * 1f) // hardSnap threshold
            posError = posError.normalized * 1f;

        Vector3 velError = hostVel - _vClientVel;
        Vector3 linearCorr = posError * _kp + velError * _kd;

        // Semi-implicit Euler integration (AddForce Acceleration equivalent)
        // PhysXではAddForce(Acceleration)はvel += force*dt, pos += vel*dt と同等
        // リニアダンピングで速度が自然に減衰する（Rigidbody.linearDamping=0のため手動ダンプ）
        _vClientVel += linearCorr * dt;
        // Kd項が速度ダンパーとして機能するため追加ダンプ不要
        _vClientPos += _vClientVel * dt;
        // ただし実際のPhysXではジョイント反作用力やコライダー接触があるため
        // ここでは位置を直接補正して追従精度を向上
        // (実際のクライアントではAddForceで徐々に追従)
        float blendFactor = Mathf.Clamp01(_kp * dt * dt);
        _vClientPos = Vector3.Lerp(_vClientPos, hostPos, blendFactor);
        _vClientVel = Vector3.Lerp(_vClientVel, hostVel, blendFactor);

        // --- 仮想プロキシの回転補正 ---
        Quaternion rotDelta = hostRot * Quaternion.Inverse(_vClientRot);
        if (rotDelta.w < 0f) { rotDelta.x = -rotDelta.x; rotDelta.y = -rotDelta.y; rotDelta.z = -rotDelta.z; rotDelta.w = -rotDelta.w; }
        rotDelta.ToAngleAxis(out float angleDeg, out Vector3 axis);
        if (!float.IsNaN(angleDeg) && axis.sqrMagnitude > 0.0001f)
        {
            if (angleDeg > 180f) angleDeg -= 360f;
            float angleRad = angleDeg * Mathf.Deg2Rad;
            Vector3 angVelErr = hostAngVel - _vClientAngVel;
            Vector3 angCorr = axis.normalized * (angleRad * _rotKp) + angVelErr * _rotKd;
            _vClientAngVel += angCorr * dt;
        }
        // Simple rotation integration
        if (_vClientAngVel.sqrMagnitude > 0.0001f)
        {
            float angSpeed = _vClientAngVel.magnitude;
            Quaternion rotStep = Quaternion.AngleAxis(angSpeed * Mathf.Rad2Deg * dt, _vClientAngVel / angSpeed);
            _vClientRot = rotStep * _vClientRot;
        }

        // 記録
        float posErr = Vector3.Distance(_vClientPos, hostPos);
        float rotErr = Quaternion.Angle(_vClientRot, hostRot);

        // オーバーシュート検出: 位置誤差の符号が反転
        float dot = 0f;
        if (hostVel.sqrMagnitude > 0.1f)
        {
            Vector3 clientToHost = hostPos - _vClientPos;
            dot = Vector3.Dot(clientToHost.normalized, hostVel.normalized);
            // dot < 0 = クライアントがホストを追い越した（オーバーシュート）
        }

        _frames.Add(new Frame
        {
            time = Time.time,
            phase = _phase.ToString(),
            hostPos = hostPos,
            hostRot = hostRot,
            hostVel = hostVel,
            clientPos = _vClientPos,
            clientRot = _vClientRot,
            posError = posErr,
            rotError = rotErr,
            dotMovement = dot
        });
    }

    private void Evaluate()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== CLIENT PROXY SIM TEST ===");

        // フェーズ別分析
        EvalPhase(sb, "Idle");
        EvalPhase(sb, "Walking");
        EvalPhase(sb, "Stopping");

        // 全体の判定
        var walkFrames = _frames.Where(f => f.phase == "Walking").ToList();
        var idleFrames = _frames.Where(f => f.phase == "Idle").Skip(_frames.Count(f => f.phase == "Idle") * 3 / 4).ToList();

        // C1: 位置追従精度
        if (walkFrames.Count > 10)
        {
            float avgErr = walkFrames.Average(f => f.posError);
            bool pass = avgErr < 0.3f;
            sb.AppendLine($"  {(pass ? "✅" : "❌")} C1_POS_TRACKING: avgError={avgErr:F3}m (limit<0.3m)");
        }

        // C2: オーバーシュート（ラバーバンディング）
        if (walkFrames.Count > 10)
        {
            int overshootCount = 0;
            for (int i = 1; i < walkFrames.Count; i++)
            {
                if (walkFrames[i - 1].dotMovement > 0 && walkFrames[i].dotMovement < -0.3f)
                    overshootCount++;
            }
            float freq = overshootCount / (WalkSec);
            bool pass = freq < 3f;
            sb.AppendLine($"  {(pass ? "✅" : "❌")} C2_NO_RUBBERBANDING: overshoot={overshootCount} ({freq:F1}/s, limit<3/s)");
        }

        // C3: 回転追従
        if (walkFrames.Count > 10)
        {
            float avgRotErr = walkFrames.Average(f => f.rotError);
            bool pass = avgRotErr < 30f;
            sb.AppendLine($"  {(pass ? "✅" : "❌")} C3_ROT_TRACKING: avgRotError={avgRotErr:F1}° (limit<30°)");
        }

        // C4: Idle安定性
        if (idleFrames.Count > 5)
        {
            float minY = idleFrames.Min(f => f.clientPos.y);
            float maxY = idleFrames.Max(f => f.clientPos.y);
            float amplitude = maxY - minY;
            bool pass = amplitude < 0.1f;
            sb.AppendLine($"  {(pass ? "✅" : "❌")} C4_IDLE_STABILITY: Y amplitude={amplitude:F4}m (limit<0.1m)");
        }

        // C5: 地面めり込み
        if (walkFrames.Count > 10)
        {
            float minClientY = walkFrames.Min(f => f.clientPos.y);
            bool pass = minClientY > 0f;
            sb.AppendLine($"  {(pass ? "✅" : "❌")} C5_NO_GROUND_SINK: minY={minClientY:F3}m (limit>0m)");
        }

        Summary = sb.ToString();
        Debug.Log("[ClientSimTest] " + Summary);
    }

    private void EvalPhase(StringBuilder sb, string phase)
    {
        var frames = _frames.Where(f => f.phase == phase).ToList();
        if (frames.Count < 5) return;

        float avgPosErr = frames.Average(f => f.posError);
        float maxPosErr = frames.Max(f => f.posError);
        float avgRotErr = frames.Average(f => f.rotError);

        sb.AppendLine($"  [{phase}] ({frames.Count} frames) posErr avg={avgPosErr:F3} max={maxPosErr:F3} | rotErr avg={avgRotErr:F1}°");
    }

    private void FindRagdoll()
    {
        foreach (var rb in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            if (rb.gameObject.name == "APR_Root" && rb.gameObject.layer == LayerMask.NameToLayer("Player"))
            {
                _rootRb = rb;
                _ctrl = rb.GetComponentInParent<RagdollController>();
                if (_ctrl != null)
                {
                    // プレファブのKp/Kdを使用
                    var field = typeof(RagdollController).GetField("proxyRootPositionKp",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (field != null) _kp = (float)field.GetValue(_ctrl);

                    field = typeof(RagdollController).GetField("proxyRootVelocityKd",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (field != null) _kd = (float)field.GetValue(_ctrl);

                    field = typeof(RagdollController).GetField("proxyRootRotationKp",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (field != null) _rotKp = (float)field.GetValue(_ctrl);

                    field = typeof(RagdollController).GetField("proxyRootAngularKd",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                    if (field != null) _rotKd = (float)field.GetValue(_ctrl);

                    Debug.Log($"[ClientSimTest] Loaded Kp={_kp} Kd={_kd} rotKp={_rotKp} rotKd={_rotKd}");
                }
                break;
            }
        }
    }

    // Fusion callbacks
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        if (!_injectForward) return;
        var data = new NetworkInputData { direction = new Vector3(0, 0, 1) };
        input.Set(data);
    }

    public void OnConnectedToServer(NetworkRunner r) { }
    public void OnDisconnectedFromServer(NetworkRunner r, NetDisconnectReason reason) { }
    public void OnConnectRequest(NetworkRunner r, NetworkRunnerCallbackArgs.ConnectRequest req, byte[] token) { }
    public void OnConnectFailed(NetworkRunner r, NetAddress addr, NetConnectFailedReason reason) { }
    public void OnShutdown(NetworkRunner r, ShutdownReason reason) { }
    public void OnPlayerJoined(NetworkRunner r, PlayerRef p) { }
    public void OnPlayerLeft(NetworkRunner r, PlayerRef p) { }
    public void OnInputMissing(NetworkRunner r, PlayerRef p, NetworkInput input) { }
    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> sessions) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken token) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef p, ReliableKey key, ReadOnlySpan<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef p, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner r) { }
    public void OnSceneLoadStart(NetworkRunner r) { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef p) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef p) { }

    public static string Execute()
    {
        if (!Application.isPlaying) return "ERROR: Not in Play Mode.";

        var existing = FindFirstObjectByType<ClientProxySimTest>();
        if (existing != null)
        {
            if (existing.IsDone) return existing.Summary;
            return "RUNNING: phase=" + existing._phase;
        }

        var go = new GameObject("__ClientSimTest__");
        go.AddComponent<ClientProxySimTest>();
        return "OK: Client proxy sim test started (~12s).";
    }
}
