using Fusion;
using MyFolder.Scripts.Network;
using MyFolder.Scripts.Player.Posing;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// 全身ポーズ同期（SnapshotInterpolation モード）の共有定数。
    /// bodyRigidbodies[0] = Root（ワールド座標で同期）、
    /// bodyRigidbodies[1..14] = Root 相対ポーズで NetworkArray スロット 0..13 に写像。
    /// </summary>
    internal static class RagdollPoseSync
    {
        public const int FirstRelativePartIndex = 1;
        public const int RelativePartCount = 14; // Rootを覗く全身パーツ数
    }

    internal interface IRagdollRuntimeHost
    {
        GameObject[] BodyParts { get; }
        Rigidbody[] BodyRigidbodies { get; }
        ConfigurableJoint[] BodyJoints { get; }
        IRagdollStateContext StateContext { get; }
        IRagdollPhysicsContext PhysicsContext { get; }
        PlayerState CurrentState { get; set; }
        void SetupHandJoints();
        void InitializeRigidbodies();
        void SetSubsystems(RagdollInput input, RagdollState state, RagdollPhysics physics);
    }

    internal interface IRagdollStateContext
    {
        PlayerState CurrentState { get; set; }
        Rigidbody RootRigidbody { get; }
    }

    internal interface IRagdollPhysicsContext
    {
        float BalanceHeight { get; }
        float BalanceStrength { get; }
        float CoreStrength { get; }
        float LimbStrength { get; }
        float MoveSpeed { get; }
        float TurnSpeed { get; }
        float JumpForce { get; }
        float AirControlMultiplier { get; }
        float StepDuration { get; }
        float StepHeight { get; }
        float FeetMountForce { get; }
        float BalanceMargin { get; }
        float IdleBalancePriority { get; }
        float WalkingBalancePriority { get; }
        float IdlePoseStiffnessMultiplier { get; }
        float WalkingPoseStiffnessMultiplier { get; }
        float StateBlendSpeed { get; }
        float BalanceDamperRatio { get; }
        float PoseDamperRatio { get; }
        float CoreDamperRatio { get; }
        float ReachArmInputLimit { get; }
        float ReachUpperArmBasePitch { get; }
        float ReachUpperArmPitchPerUnit { get; }
        float ReachUpperArmMinPitch { get; }
        float ReachUpperArmMaxPitch { get; }
        float ReachLowerArmPitch { get; }
        float ReachUpperArmJointSpring { get; }
        float ReachUpperArmJointDamper { get; }
        float ReachUpperArmJointMaxForce { get; }
        float ReachLowerArmJointSpring { get; }
        float ReachLowerArmJointDamper { get; }
        float ReachLowerArmJointMaxForce { get; }
        float RagdollDriveOffSpring { get; }
        float RagdollDriveOffDamper { get; }
        float MovementVelocityLerp { get; }
        float PunchImpulse { get; }
        float PunchRecoveryDelaySeconds { get; }
        float PunchRecoveryLerpSpeed { get; }

        /// <summary>
        /// Reach(到達)アクションの静的決めポーズ。論理骨ごとの rest 相対デルタを保持する。
        /// null の場合は従来のパラメトリック値にフォールバックする。
        /// </summary>
        ActionPoseAsset ReachPose { get; }

        /// <summary>
        /// 左右どちらかの手が何かを掴んでいるか。掴まり中は接地扱いにして
        /// ラグドール化（バランス喪失）を抑止する（崖よじ登り用、HFF同等の考え方）。
        /// </summary>
        bool IsAnyHandGrabbing { get; }
        bool HasStateAuthority { get; }
        bool UseForecastPhysics { get; }
    }

    internal interface IClientBootstrapContext
    {
        bool HasInputAuthority { get; }
        bool HasStateAuthority { get; }
        bool ForceRemoteForAllClientProxies { get; }
        bool ForceRemoteForInputAuthorityOnClient { get; }
        bool UseHybridProxySimulation { get; }
        int InstanceId { get; }
        void SetForceRemoteRenderTimeframe(bool value);
        IClientProxyModeStrategy CreateClientProxyModeStrategy();
        void LogClientBootstrap(string key, string message, float throttle, string dedupeKey = null);
        void LogClientDebug(string message);
        void LogClientWarning(string message);
    }

    internal interface IClientProxyRigAccess
    {
        bool RelaxClientJointsOnSpawn { get; }
        bool HasRootNetworkRigidbody { get; }
        bool UseLegacyCustomRootCorrection { get; }
        void DisableClientJointDrives();
        void DisableClientJoints();
        void SetProxyVisualsEnabled(bool enabled);
        void DisableRootNetworkRigidbody();
    }

    /// <summary>
    /// SnapshotInterpolation モードでクライアント側の Render() 補間が
    /// Fusion スナップショットバッファへアクセスするためのインターフェース。
    /// RagdollController が実装し、RagdollSnapshotPoseInterpolator が参照する。
    /// </summary>
    internal interface IPoseSnapshotAccess
    {
        bool TryGetPoseSnapshots(out NetworkBehaviourBuffer from, out NetworkBehaviourBuffer to, out float alpha);
        (Vector3 from, Vector3 to) ReadRootPosition(NetworkBehaviourBuffer from, NetworkBehaviourBuffer to);
        (Quaternion from, Quaternion to) ReadRootRotation(NetworkBehaviourBuffer from, NetworkBehaviourBuffer to);
        (int from, int to) ReadPoseTeleportKey(NetworkBehaviourBuffer from, NetworkBehaviourBuffer to);
        bool ReadPoseInitialized(NetworkBehaviourBuffer buffer);
        NetworkArrayReadOnly<Vector3> ReadPartPositions(NetworkBehaviourBuffer buffer);
        NetworkArrayReadOnly<Quaternion> ReadPartRotations(NetworkBehaviourBuffer buffer);
        Rigidbody GetBodyRigidbodyByIndex(int index);
        void SetProxyVisualsEnabled(bool enabled);

        /// <summary>
        /// ポーズ同期対象外の装飾用 Rigidbody（Other/ 配下の Sphere 等）。
        /// クライアントでもローカル物理で動かすため、補間の transform 書き込みから保護する。
        /// </summary>
        Rigidbody[] DecorationRigidbodies { get; }

        /// <summary>
        /// 最新受信 tick の確定ポーズ（[Networked] 生読み）。
        /// スナップショットバッファ補間（描画用）と違い tick ごとに均一に進む系列のため、
        /// 物理ステップ直前に本体を配置して装飾ジョイントへの励起を均一化するのに使う。
        /// </summary>
        bool IsLatestPoseInitialized { get; }
        Vector3 LatestRootPosition { get; }
        Quaternion LatestRootRotation { get; }
        Vector3 GetLatestPartRelativePosition(int slot);
        Quaternion GetLatestPartRelativeRotation(int slot);

        /// <summary>
        /// 装飾の描画用ローパスフィルタ時定数(秒)。RagdollProfile から供給され、
        /// Play 中の Inspector 調整を即反映するため毎フレーム読む。0 で平滑化なし。
        /// </summary>
        float DecorationSmoothingTau { get; }
    }

    internal interface IClientProxyRuntimeContext
    {
        ProxySyncMode SyncMode { get; }
        bool UseHybridProxySimulation { get; }
        bool UseForecastPhysics { get; }
        bool HasInputAuthority { get; }
        bool ProxyBootstrapApplied { get; set; }
        float DeltaTime { get; }
        PlayerState CurrentState { get; }
        Vector3 MoveDirection { get; }
        Vector3 FacingDirection { get; }
        Vector2 LookDirection { get; }
        float BodyRoll { get; }
        Transform ProxyFacingFallbackTransform { get; }
        RagdollInput InputHandler { get; }
        RagdollPhysics PhysicsHandler { get; }
        Rigidbody[] KinematicTargetRigidbodies { get; }

        /// <summary>
        /// SnapshotInterpolation モードでポーズ同期（kinematic 化 + Render 補間書き込み）の
        /// 対象になる 15 パーツ。装飾用 Sphere 等はここに含まれず、クライアントでも
        /// ローカル物理（ジョイント駆動の揺れ）のまま残す。
        /// </summary>
        Rigidbody[] PoseDrivenRigidbodies { get; }
        bool TryGetInput(out NetworkInputData data);
        ClientProxyCorrection CreateClientProxyCorrection();
        RagdollSnapshotPoseInterpolator CreateSnapshotPoseInterpolator();
        void EmitSyncDiagnostics(string phase);
    }

    internal interface IHostSimulationContext
    {
        bool TryGetInput(out NetworkInputData data);
        bool HasInputAuthority { get; }
        bool IsResimulation { get; }
        int InstanceId { get; }
        float DeltaTime { get; }
        RagdollInput InputHandler { get; }
        RagdollPhysics PhysicsHandler { get; }
        PlayerState CurrentState { get; set; }
        Vector3 MoveDirection { get; set; }
        Vector3 FacingDirection { get; set; }
        Vector2 LookDirection { get; set; }
        float BodyRoll { get; set; }
        void ResolvePlayerState(RagdollCommand command);
        void PublishProxyPoseSnapshot();
        void EmitSyncDiagnostics(string phase);
    }

    internal interface IProxyPosePublisherContext
    {
        void EnsureProxyBodyReferences();
        Rigidbody RootRigidbody { get; }
        Rigidbody HeadRigidbody { get; }
        Rigidbody LeftHandRigidbody { get; }
        Rigidbody RightHandRigidbody { get; }
        bool PublishFullPose { get; }
        float PoseTeleportDetectThreshold { get; }
        Rigidbody GetBodyRigidbody(int index);
        void ApplyPartPose(int slot, Vector3 relativePosition, Quaternion relativeRotation);
        void IncrementPoseTeleportKey();
        void ApplyProxyPoseSnapshot(ProxyPoseSnapshotData snapshot);
        void RecordHostGroundTruthSample(Vector3 actualRootPosition, Vector3 actualRootVelocity);
    }

    internal interface IRagdollTreasureCarryContext
    {
        bool IsGrabbingTreasure { get; }
        float CarryMoveMaxForce { get; }
        float CarryHarnessSlack { get; }
        float CarryHarnessLimitSpring { get; }
        float CarryHarnessLimitDamper { get; }
        void NotifyTreasureGrabbed(Rigidbody treasureRigidbody);
        void NotifyTreasureReleased(Rigidbody treasureRigidbody);
    }

    internal interface IRagdollAudioSink
    {
        void PlayImpactSound();
        void PlayHitSound();
    }

    internal interface IRagdollGroundingSink
    {
        void OnFootGroundedChanged(bool isLeftFoot, bool isGrounded);
    }

    public interface ILocalPlayerViewSource
    {
        bool HasInputAuthority { get; }
        Transform Transform { get; }
        Transform CenterOfMassPoint { get; }

        /// <summary>体が向いている水平方向（カメラ自動追従の背後配置に使う）。</summary>
        Vector3 FacingForward { get; }

        /// <summary>
        /// 両手で同一オブジェクトを掴んでいるか（片手ずつ別のものを持っている場合は false）。
        /// true の間はマウスX がボディヨー操作になり、カメラは背後へ自動追従する。
        /// </summary>
        bool IsTwoHandedHold { get; }

        /// <summary>
        /// 指定した手が掴んでいるオブジェクトのルート Transform（掴んでいなければ null）。
        /// カメラのスプリングアーム衝突から除外するために使う（頭上に掲げた掴み物へ
        /// カメラが引き寄せられてメッシュ内部へ入るバグの対策）。
        /// </summary>
        Transform GetHeldObjectRoot(bool isLeftHand);
    }

    /// <summary>
    /// ILocalPlayerViewSource の実体は MonoBehaviour（RagdollController）のため、
    /// インターフェース越しの素の null 比較では Unity 側の Destroy を検知できない。
    /// この判定を一箇所に集約する。
    /// </summary>
    public static class LocalPlayerViewUtil
    {
        public static bool IsDestroyedOrMissing(ILocalPlayerViewSource view)
            => view == null || (view as UnityEngine.Object) == null;
    }
}
