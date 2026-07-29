using UnityEngine;
using System.Collections.Generic;
using System.Text;
using Fusion;
using Fusion.Sockets;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Network;
using System;
using System.Linq;

/// <summary>
/// ホスト側ラグドールの正常動作仕様テスト。
/// ホストとしてセッションを開始し、入力を注入して
/// 仕様を満たしているかを自動検証する。
///
/// 仕様:
///   H1: 静止時の安定性 — Root Y 振幅 < 0.05m
///   H2: 接地判定 — 静止時 grounded > 80%
///   H3: 歩行アニメーション — Walking時の脚targetRotation変化幅 > 0.1
///   H4: 歩行速度 — 水平速度 > 1 m/s
///   H5: JointDriveダンパー — 全ポーズジョイントの damper > 0
///   C5: ルートへの不要な力 — UpdatePhysicsVisualOnlyがルート位置を変動させない
/// </summary>
public class RagdollSpecTest : MonoBehaviour, INetworkRunnerCallbacks
{
    private enum Phase { WaitSpawn, Idle, Walking, ClientSim, Done }

    private Phase _phase = Phase.WaitSpawn;
    private float _phaseStart;
    private NetworkRunner _runner;

    // ホスト側参照
    private Rigidbody _rootRb;
    private RagdollController _ctrl;
    private ConfigurableJoint[] _allJoints;
    private ConfigurableJoint _upperRightLegJoint;
    private ConfigurableJoint _upperLeftLegJoint;

    // フェーズ長
    private const float IdleSeconds = 6f;
    private const float WalkSeconds = 5f;
    private const float ClientSimSeconds = 3f;

    // 入力注入
    private bool _injectForward;

    // ─── 記録用 ───
    private readonly List<float> _idleRootY = new List<float>();
    private readonly List<bool> _idleGrounded = new List<bool>();
    private readonly List<float> _walkSpeed = new List<float>();
    private readonly List<float> _walkLegTargetX = new List<float>();
    private readonly List<float> _walkLegTargetXL = new List<float>();

    // C5テスト用: クライアント側シミュレーション
    // ルートにApplySoftRootCorrection以外の力がかかるかを測定
    private readonly List<float> _clientRootDrift = new List<float>();

    // 結果
    private readonly Dictionary<string, (bool pass, string detail)> _results = new Dictionary<string, (bool pass, string detail)>();
    public bool IsDone => _phase == Phase.Done;
    public string Summary { get; private set; } = "";

    private void Start()
    {
        _phaseStart = Time.time;
    }

    private void FixedUpdate()
    {
        if (_phase == Phase.Done) return;

        // Runner検索
        if (_runner == null)
        {
            _runner = FindFirstObjectByType<NetworkRunner>();
            if (_runner != null) _runner.AddCallbacks(this);
            return;
        }

        // ラグドール検索
        if (_rootRb == null)
        {
            FindRagdoll();
            if (_rootRb == null) return;
            _phase = Phase.Idle;
            _phaseStart = Time.time;
            Debug.Log("[SpecTest] Ragdoll found. Starting Idle phase.");
        }

        float elapsed = Time.time - _phaseStart;

        switch (_phase)
        {
            case Phase.Idle:
                _injectForward = false;
                RecordIdle();
                if (elapsed >= IdleSeconds)
                {
                    _phase = Phase.Walking;
                    _phaseStart = Time.time;
                    Debug.Log("[SpecTest] Walking phase.");
                }
                break;

            case Phase.Walking:
                _injectForward = true;
                RecordWalking();
                if (elapsed >= WalkSeconds)
                {
                    _injectForward = false;
                    _phase = Phase.ClientSim;
                    _phaseStart = Time.time;
                    Debug.Log("[SpecTest] ClientSim phase.");
                }
                break;

            case Phase.ClientSim:
                _injectForward = false;
                RecordClientSim();
                if (elapsed >= ClientSimSeconds)
                {
                    _phase = Phase.Done;
                    EvaluateAll();
                }
                break;
        }
    }

    // ─── 記録メソッド ───

    private RagdollFootContact[] _footContacts;

    private void RecordIdle()
    {
        if (_rootRb == null) return;
        _idleRootY.Add(_rootRb.position.y);

        // RagdollFootContact.GetIsGrounded() を直接使用
        // （[Networked] IsLeftFootGrounded のタイミング問題を回避）
        if (_footContacts == null || _footContacts.Length == 0)
            _footContacts = _rootRb.GetComponentsInChildren<RagdollFootContact>();

        bool grounded = false;
        if (_footContacts != null)
        {
            foreach (var fc in _footContacts)
            {
                if (fc != null && fc.GetIsGrounded()) { grounded = true; break; }
            }
        }
        _idleGrounded.Add(grounded);
    }

    private void RecordWalking()
    {
        if (_rootRb == null) return;
        var vel = _rootRb.linearVelocity;
        _walkSpeed.Add(new Vector2(vel.x, vel.z).magnitude);

        if (_upperRightLegJoint != null)
            _walkLegTargetX.Add(_upperRightLegJoint.targetRotation.x);
        if (_upperLeftLegJoint != null)
            _walkLegTargetXL.Add(_upperLeftLegJoint.targetRotation.x);
    }

    private void RecordClientSim()
    {
        // C5テスト: クライアント側でUpdatePhysicsVisualOnlyがルートを動かさないか
        // ホスト上でシミュレーション。ルートの「自然な」速度変化を計測。
        // 静止中にルート速度が大きくなれば不要な力が作用している。
        if (_rootRb == null) return;
        _clientRootDrift.Add(_rootRb.linearVelocity.magnitude);
    }

    // ─── 評価 ───

    private void EvaluateAll()
    {
        // H1: 静止時安定性
        if (_idleRootY.Count > 10)
        {
            // スポーンY=10からの落下→安定まで約3秒かかるためスキップ
            int skip = Mathf.Min(_idleRootY.Count * 3 / 4, 150);
            var steady = _idleRootY.Skip(skip).ToList();
            float minY = steady.Min();
            float maxY = steady.Max();
            float amplitude = maxY - minY;
            _results["H1_IDLE_STABILITY"] = (amplitude < 0.05f,
                $"Y amplitude={amplitude:F4}m (limit=0.05m) range=[{minY:F3},{maxY:F3}]");
        }
        else
        {
            _results["H1_IDLE_STABILITY"] = (false, "Insufficient data");
        }

        // H2: 接地判定
        if (_idleGrounded.Count > 10)
        {
            int skip = Mathf.Min(_idleGrounded.Count * 3 / 4, 150);
            var steady = _idleGrounded.Skip(skip).ToList();
            float ratio = (float)steady.Count(g => g) / steady.Count;
            _results["H2_GROUNDED"] = (ratio > 0.8f,
                $"grounded={ratio:P0} ({steady.Count(g => g)}/{steady.Count}) (limit>80%)");
        }
        else
        {
            _results["H2_GROUNDED"] = (false, "Insufficient data");
        }

        // H3: 歩行アニメーション（脚のtargetRotation変化幅）
        if (_walkLegTargetX.Count > 10)
        {
            float range = _walkLegTargetX.Max() - _walkLegTargetX.Min();
            float rangeL = _walkLegTargetXL.Count > 0 ? _walkLegTargetXL.Max() - _walkLegTargetXL.Min() : 0;
            float maxRange = Mathf.Max(range, rangeL);
            _results["H3_WALK_ANIMATION"] = (maxRange > 0.1f,
                $"legTargetX range R={range:F3} L={rangeL:F3} (limit>0.1)");
        }
        else
        {
            _results["H3_WALK_ANIMATION"] = (false, "Insufficient data");
        }

        // H4: 歩行速度
        if (_walkSpeed.Count > 10)
        {
            float avg = _walkSpeed.Average();
            float max = _walkSpeed.Max();
            _results["H4_WALK_SPEED"] = (avg > 1f,
                $"avg={avg:F2} max={max:F2} m/s (limit>1)");
        }
        else
        {
            _results["H4_WALK_SPEED"] = (false, "Insufficient data");
        }

        // H5: JointDriveダンパー
        if (_allJoints != null && _allJoints.Length > 0)
        {
            int zeroDamper = 0;
            var zeroDamperNames = new List<string>();
            foreach (var j in _allJoints)
            {
                if (j == null) continue;
                // バランス/コア/ポーズジョイントのみ確認（Sphere等は除外）
                if (j.gameObject.name.StartsWith("APR_") || j.gameObject.name == "APR_Root")
                {
                    if (j.angularXDrive.positionSpring > 100 && j.angularXDrive.positionDamper <= 0)
                    {
                        zeroDamper++;
                        zeroDamperNames.Add(j.gameObject.name);
                    }
                }
            }
            _results["H5_JOINT_DAMPER"] = (zeroDamper == 0,
                $"zeroDamperJoints={zeroDamper}" + (zeroDamperNames.Count > 0 ? $" [{string.Join(",", zeroDamperNames)}]" : ""));
        }
        else
        {
            _results["H5_JOINT_DAMPER"] = (false, "No joints found");
        }

        // C5: 不要なルート力（静止中のドリフト）
        if (_clientRootDrift.Count > 10)
        {
            float avg = _clientRootDrift.Average();
            _results["C5_NO_UNWANTED_FORCE"] = (avg < 0.1f,
                $"avg root speed during idle={avg:F4} m/s (limit<0.1)");
        }
        else
        {
            _results["C5_NO_UNWANTED_FORCE"] = (false, "Insufficient data");
        }

        // 結果出力
        var sb = new StringBuilder();
        sb.AppendLine("=== RAGDOLL SPEC TEST RESULTS ===");
        int passed = 0, total = 0;
        foreach (var kv in _results.OrderBy(x => x.Key))
        {
            total++;
            if (kv.Value.pass) passed++;
            string icon = kv.Value.pass ? "✅" : "❌";
            sb.AppendLine($"  {icon} {kv.Key}: {kv.Value.detail}");
        }
        sb.AppendLine($"\n  TOTAL: {passed}/{total} passed");

        Summary = sb.ToString();
        Debug.Log("[SpecTest] " + Summary);
    }

    // ─── ヘルパー ───

    private void FindRagdoll()
    {
        foreach (var rb in FindObjectsByType<Rigidbody>(FindObjectsSortMode.None))
        {
            if (rb.gameObject.name == "APR_Root" && rb.gameObject.layer == LayerMask.NameToLayer("Player"))
            {
                _rootRb = rb;
                _ctrl = rb.GetComponentInParent<RagdollController>();
                _allJoints = rb.GetComponentsInChildren<ConfigurableJoint>();

                foreach (var j in _allJoints)
                {
                    if (j.gameObject.name.Contains("UpperRightLeg")) _upperRightLegJoint = j;
                    if (j.gameObject.name.Contains("UpperLeftLeg")) _upperLeftLegJoint = j;
                }
                break;
            }
        }
    }

    // ─── Fusion入力注入 ───
    public void OnInput(NetworkRunner runner, NetworkInput input)
    {
        if (!_injectForward) return;
        var data = new NetworkInputData { direction = new Vector3(0, 0, 1) };
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
    public void OnSessionListUpdated(NetworkRunner r, List<SessionInfo> sessions) { }
    public void OnCustomAuthenticationResponse(NetworkRunner r, Dictionary<string, object> data) { }
    public void OnHostMigration(NetworkRunner r, HostMigrationToken token) { }
    public void OnReliableDataReceived(NetworkRunner r, PlayerRef p, ReliableKey key, ReadOnlySpan<byte> data) { }
    public void OnReliableDataProgress(NetworkRunner r, PlayerRef p, ReliableKey key, float progress) { }
    public void OnSceneLoadDone(NetworkRunner r) { }
    public void OnSceneLoadStart(NetworkRunner r) { }
    public void OnObjectExitAOI(NetworkRunner r, NetworkObject obj, PlayerRef p) { }
    public void OnObjectEnterAOI(NetworkRunner r, NetworkObject obj, PlayerRef p) { }

    // ─── エントリポイント ───
    public static string Execute()
    {
        if (!Application.isPlaying)
            return "ERROR: Not in Play Mode.";

        var existing = FindFirstObjectByType<RagdollSpecTest>();
        if (existing != null)
        {
            if (existing.IsDone) return existing.Summary;
            return $"RUNNING: phase={existing._phase}";
        }

        var go = new GameObject("__SpecTest__");
        go.AddComponent<RagdollSpecTest>();
        return "OK: Spec test started (~12s).";
    }
}
