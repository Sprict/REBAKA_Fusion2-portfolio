using UnityEngine;
using Fusion;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MyFolder.Scripts.Player;

/// <summary>
/// クライアント側でラグドール挙動の品質を自動計測する診断コンポーネント。
/// プレファブにアタッチして使う。
///
/// 動作:
///   - ホスト (StateAuthority 側) では何もしない。
///   - クライアント側で <see cref="SamplingDurationSeconds"/> 秒間サンプリングし、
///     終了時に評価結果を 1 つのログにまとめて出力する。
///
/// 評価項目:
///   NO_VIBRATION      静止窓中のルート Y 振幅が小さい
///   NO_RUBBERBANDING  位置進行方向の急反転が頻発していない
///   ROTATION_FOLLOW   ルートの向きが計測中に十分変化している
///   NO_GROUND_SINK    ルートが地面にめり込んでいない
///   NO_TELEPORT       1 フレーム間で位置が急にジャンプしていない
///   LEG_ANIMATION     脚ジョイントの targetRotation が動いている (歩行時の確認)
/// </summary>
public class ClientSideDiagnostic : NetworkBehaviour
{
    // --- サンプリング窓 ---------------------------------------------------
    private const float SamplingDurationSeconds = 12f;        // 計測全体の長さ
    private const float WarmupSeconds = 3f;                   // スポーン直後の落下・安定化を無視する時間
    private const float IdleWindowEndSeconds = 5f;            // 静止判定に使う窓の終端 (WarmupSeconds〜IdleWindowEndSeconds が静止窓)

    // --- 評価に必要な最小サンプル数 --------------------------------------
    private const int MinTotalSampleCount = 20;               // 評価を実行する最小サンプル数
    private const int MinSteadySampleCount = 10;              // 安定化後 (Warmup 以降) の最小サンプル数
    private const int MinIdleSampleCount = 5;                 // 静止窓内の最小サンプル数

    // --- 合否しきい値 -----------------------------------------------------
    private const float MaxIdleYAmplitudeMeters = 0.05f;      // 静止窓中の Y 振動の許容振幅
    private const float MaxReversalsPerSecond = 3f;           // 1 秒あたりに許容する方向反転回数
    private const float MinRotationChangeDegrees = 10f;       // 必要な総回転変化量
    private const float MinGroundClearanceMeters = 0.3f;      // 地面から必要なクリアランス
    private const float MaxFrameJumpMeters = 1f;              // 1 フレーム間移動の許容上限
    private const float MinLegAnimationRange = 0.1f;          // 脚アニメ振幅の最小値

    // --- 内部ロジックの定数 ----------------------------------------------
    private const float ReversalDotThreshold = -0.5f;         // 2 つの移動ベクトルがほぼ逆向きとみなす内積
    private const float MovementSqrMagEpsilon = 0.0001f;      // 微小移動 (ノイズ) を反転判定から除外する閾値

    // --- 探索対象のオブジェクト名 ----------------------------------------
    private const string RootRigidbodyName = "APR_Root";
    private const string LegJointNameToken = "UpperRightLeg";

    /// <summary>1 フレーム分の観測サンプル。</summary>
    private struct Sample
    {
        public float TimeSinceStart;
        public Vector3 Position;
        public Quaternion Rotation;
        public float LegTargetRotationX;
        public bool IsKinematic;
        public bool UseGravity;
    }

    private bool _isMeasuring;
    private bool _hasEvaluated;
    private float _measurementStartTime;
    private Rigidbody _rootRb;
    private ConfigurableJoint _legJoint;
    private readonly List<Sample> _samples = new(1024);

    public override void Spawned()
    {
        // ホスト側は計測対象外
        if (Object.HasStateAuthority)
        {
            _isMeasuring = false;
            return;
        }

        _isMeasuring = true;
        _hasEvaluated = false;
        _measurementStartTime = Time.time;
        _rootRb = FindChildRigidbodyByName(RootRigidbodyName);
        _legJoint = FindChildJointContaining(LegJointNameToken);

        Debug.Log($"[ClientDiag] STARTED on client. rootRb={(_rootRb != null ? _rootRb.name : "NULL")} " +
                  $"isKinematic={_rootRb != null && _rootRb.isKinematic} " +
                  $"inputAuth={Object.HasInputAuthority}");
    }

    public override void FixedUpdateNetwork()
    {
        if (!_isMeasuring || _hasEvaluated || _rootRb == null)
            return;

        float elapsedSeconds = Time.time - _measurementStartTime;
        if (elapsedSeconds > SamplingDurationSeconds)
        {
            _hasEvaluated = true;
            EvaluateAndLog();
            return;
        }

        _samples.Add(new Sample
        {
            TimeSinceStart = elapsedSeconds,
            Position = _rootRb.position,
            Rotation = _rootRb.rotation,
            LegTargetRotationX = _legJoint != null ? _legJoint.targetRotation.x : 0f,
            IsKinematic = _rootRb.isKinematic,
            UseGravity = _rootRb.useGravity
        });
    }

    // ---------- 評価本体 --------------------------------------------------

    private void EvaluateAndLog()
    {
        if (_samples.Count < MinTotalSampleCount)
        {
            Debug.Log($"[ClientDiag] INSUFFICIENT DATA: {_samples.Count} frames");
            return;
        }

        List<Sample> steady = ExtractSteadyStateSamples();
        if (steady.Count < MinSteadySampleCount)
        {
            Debug.Log("[ClientDiag] Not enough steady-state frames.");
            return;
        }

        var report = new StringBuilder();
        AppendHeader(report);
        AppendBasicInfo(report, steady);
        CheckNoIdleVibration(report, steady);
        CheckNoRubberbanding(report, steady);
        CheckRotationFollow(report, steady);
        CheckNoGroundSink(report, steady);
        CheckNoTeleport(report, steady);
        CheckLegAnimation(report, steady);
        AppendFinalPose(report, steady);

        Debug.Log("[ClientDiag] " + report.ToString());
    }

    /// <summary>Warmup 期間を除いた安定化後のサンプル列を返す。</summary>
    private List<Sample> ExtractSteadyStateSamples()
    {
        int firstSteadyIndex = _samples.FindIndex(s => s.TimeSinceStart >= WarmupSeconds);
        if (firstSteadyIndex < 0)
            firstSteadyIndex = 0;
        return _samples.GetRange(firstSteadyIndex, _samples.Count - firstSteadyIndex);
    }

    // ---------- 各評価項目 ------------------------------------------------

    /// <summary>静止窓中にルートが小刻みに上下振動していないか。</summary>
    private static void CheckNoIdleVibration(StringBuilder report, List<Sample> steady)
    {
        List<Sample> idle = steady.FindAll(s => s.TimeSinceStart < IdleWindowEndSeconds);
        if (idle.Count <= MinIdleSampleCount)
            return;

        float minY = idle.Min(s => s.Position.y);
        float maxY = idle.Max(s => s.Position.y);
        float amplitude = maxY - minY;
        bool pass = amplitude < MaxIdleYAmplitudeMeters;

        report.AppendLine($"  {Mark(pass)} NO_VIBRATION: Y amplitude={amplitude:F4}m [{minY:F3},{maxY:F3}] (limit<{MaxIdleYAmplitudeMeters}m)");
    }

    /// <summary>
    /// 位置の進行方向が頻繁に反転していないか。
    /// サーバー位置に引っ張られて行ったり来たりするラバーバンディング現象を検出する。
    /// </summary>
    private static void CheckNoRubberbanding(StringBuilder report, List<Sample> steady)
    {
        int reversalCount = 0;
        for (int i = 2; i < steady.Count; i++)
        {
            Vector3 prevDelta = steady[i - 1].Position - steady[i - 2].Position;
            Vector3 currDelta = steady[i].Position - steady[i - 1].Position;

            // 微小移動はノイズなので除外
            if (prevDelta.sqrMagnitude <= MovementSqrMagEpsilon || currDelta.sqrMagnitude <= MovementSqrMagEpsilon)
                continue;

            float directionAlignment = Vector3.Dot(prevDelta.normalized, currDelta.normalized);
            if (directionAlignment < ReversalDotThreshold)
                reversalCount++;
        }

        float windowDurationSeconds = steady[^1].TimeSinceStart - steady[0].TimeSinceStart;
        float reversalsPerSecond = windowDurationSeconds > 0f ? reversalCount / windowDurationSeconds : 0f;
        bool pass = reversalsPerSecond < MaxReversalsPerSecond;

        report.AppendLine($"  {Mark(pass)} NO_RUBBERBANDING: {reversalCount} reversals ({reversalsPerSecond:F1}/s, limit<{MaxReversalsPerSecond}/s)");
    }

    /// <summary>ルートの向きが計測中にちゃんと変化しているか (向きが固定されていれば「死んでいる」と判定)。</summary>
    private static void CheckRotationFollow(StringBuilder report, List<Sample> steady)
    {
        float totalRotationDegrees = 0f;
        for (int i = 1; i < steady.Count; i++)
            totalRotationDegrees += Quaternion.Angle(steady[i - 1].Rotation, steady[i].Rotation);

        bool pass = totalRotationDegrees > MinRotationChangeDegrees;
        report.AppendLine($"  {Mark(pass)} ROTATION_FOLLOW: totalChange={totalRotationDegrees:F1}° (limit>{MinRotationChangeDegrees}°)");
    }

    /// <summary>ルートが地面に沈み込んでいないか。</summary>
    private static void CheckNoGroundSink(StringBuilder report, List<Sample> steady)
    {
        float minY = steady.Min(s => s.Position.y);
        bool pass = minY > MinGroundClearanceMeters;
        report.AppendLine($"  {Mark(pass)} NO_GROUND_SINK: minY={minY:F3}m (limit>{MinGroundClearanceMeters}m)");
    }

    /// <summary>フレーム間で急なジャンプ移動が起きていないか (強制位置補正の検出)。</summary>
    private static void CheckNoTeleport(StringBuilder report, List<Sample> steady)
    {
        float maxJumpDistance = 0f;
        int teleportCount = 0;
        for (int i = 1; i < steady.Count; i++)
        {
            float distance = Vector3.Distance(steady[i].Position, steady[i - 1].Position);
            if (distance > maxJumpDistance)
                maxJumpDistance = distance;
            if (distance > MaxFrameJumpMeters)
                teleportCount++;
        }

        bool pass = teleportCount == 0;
        report.AppendLine($"  {Mark(pass)} NO_TELEPORT: maxJump={maxJumpDistance:F3}m teleports={teleportCount} (limit=0)");
    }

    /// <summary>脚ジョイントの targetRotation が動いているか (アニメーション同期が効いているか)。</summary>
    private static void CheckLegAnimation(StringBuilder report, List<Sample> steady)
    {
        float minLegX = steady.Min(s => s.LegTargetRotationX);
        float maxLegX = steady.Max(s => s.LegTargetRotationX);
        float range = maxLegX - minLegX;
        bool pass = range > MinLegAnimationRange;

        report.AppendLine($"  {Mark(pass)} LEG_ANIMATION: legTargetX range={range:F3} (limit>{MinLegAnimationRange})");
    }

    // ---------- レポート整形 ----------------------------------------------

    private static void AppendHeader(StringBuilder report)
    {
        report.AppendLine("╔══════════════════════════════════════════╗");
        report.AppendLine("║   CLIENT-SIDE DIAGNOSTIC RESULTS         ║");
        report.AppendLine("╚══════════════════════════════════════════╝");
    }

    private void AppendBasicInfo(StringBuilder report, List<Sample> steady)
    {
        report.AppendLine($"  Frames: {_samples.Count} total, {steady.Count} steady-state");
        report.AppendLine($"  Root isKinematic: {steady[0].IsKinematic}");
        report.AppendLine($"  Root useGravity: {steady[0].UseGravity}");
    }

    private static void AppendFinalPose(StringBuilder report, List<Sample> steady)
    {
        Sample last = steady[^1];
        report.AppendLine($"  Final: pos={last.Position} rot={last.Rotation.eulerAngles}");
    }

    // ---------- ユーティリティ --------------------------------------------

    private Rigidbody FindChildRigidbodyByName(string targetName)
        => GetComponentsInChildren<Rigidbody>().FirstOrDefault(rb => rb.gameObject.name == targetName);

    private ConfigurableJoint FindChildJointContaining(string nameToken)
        => GetComponentsInChildren<ConfigurableJoint>().FirstOrDefault(j => j.gameObject.name.Contains(nameToken));

    private static string Mark(bool pass) => pass ? "✅" : "❌";
}
