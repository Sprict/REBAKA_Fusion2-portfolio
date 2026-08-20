using Fusion;
using Rebaka.Network;
using Rebaka.Player.Posing;
using UnityEngine;

namespace Rebaka.Player
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

    /// <summary>
    /// 物理層が Controller から読む値の契約。
    ///
    /// 【2026-08-07 変更】以前はチューニング値ごとに専用メンバーを持っており、
    /// 物理パラメータを1つ足すたびに4箇所（RagdollProfile → Controller の private プロパティ →
    /// この interface の宣言 → Controller の明示実装）を触る必要があった。約38メンバーが
    /// <c>X =&gt; profile.x;</c> の写経で、情報量ゼロの転送層になっていた。
    /// Profile を直接渡す形にして、その4箇所を1箇所（Profile へのフィールド追加のみ）に減らした。
    ///
    /// ここに残っているのは <b>Profile から直接読めないもの</b>だけである。
    /// 新しいチューニング値を足すときは、この interface ではなく
    /// <see cref="RagdollProfile"/> にフィールドを足して <c>_context.Profile.xxx</c> で読む。
    /// </summary>
    internal interface IRagdollPhysicsContext
    {
        /// <summary>
        /// チューニング値の正本。物理層はここから直接読む。
        /// </summary>
        RagdollProfile Profile { get; }

        /// <summary>
        /// 実効移動速度。<b>Profile.moveSpeed の直読みでは代用できない。</b>
        /// Dash / Crouch の倍率を掛けた計算値であり、直読みに置き換えると
        /// ダッシュとしゃがみがコンパイルを通ったまま無効になる。
        /// 倍率は毎tick の CurrentCommand から読むため、ホスト権威sim・
        /// クライアント予測の両経路で同一入力から同じ値になる（resim 安全）。
        /// </summary>
        float MoveSpeed { get; }

        /// <summary>
        /// Reach(到達)アクションの静的決めポーズ。論理骨ごとの rest 相対デルタを保持する。
        /// null の場合は従来のパラメトリック値にフォールバックする。
        /// Profile ではなく Controller 側の [SerializeField]（モデル別に録り直すため）。
        /// </summary>
        ActionPoseAsset ReachPose { get; }

        /// <summary>
        /// 左右どちらかの手が何かを掴んでいるか。掴まり中は接地扱いにして
        /// ラグドール化（バランス喪失）を抑止する（崖よじ登り用、HFF同等の考え方）。
        /// </summary>
        bool IsAnyHandGrabbing { get; }

        /// <summary>Fusion の状態権限。実行時状態であり Profile 由来ではない。</summary>
        bool HasStateAuthority { get; }

        /// <summary>ResolvedProxySyncMode から導出。実行時状態であり Profile 由来ではない。</summary>
        bool UseForecastPhysics { get; }
    }

    /// <summary>
    /// <see cref="RagdollClientBootstrapper"/> が必要とする Authority 情報、描画設定、
    /// 同期モード別 Strategy の生成機能だけを <see cref="RagdollController"/> から公開する契約。
    /// </summary>
    internal interface IClientBootstrapContext
    {
        bool HasInputAuthority { get; }
        bool HasStateAuthority { get; }
        /// <summary>すべてのクライアントプロキシへ Remote 描画時刻を強制する設定。</summary>
        bool ForceRemoteForAllClientProxies { get; }
        /// <summary>Input Authority を持つクライアントのプロキシだけへ Remote 描画時刻を強制する設定。</summary>
        bool ForceRemoteForInputAuthorityOnClient { get; }
        int InstanceId { get; }
        /// <summary>
        /// NetworkObject が描画に Local/Remote のどちらの時刻を使うかを設定する。
        /// シミュレーションの Authority や物理計算の担当は変更しない。
        /// </summary>
        void SetForceRemoteRenderTimeframe(bool value);
        /// <summary>解決済みの <see cref="ProxySyncMode"/> に対応するクライアント初期化 Strategy を生成する。</summary>
        IClientProxyModeStrategy CreateClientProxyModeStrategy();
        void LogClientBootstrap(string key, string message, float throttle, string dedupeKey = null);
        void LogClientDebug(string message);
        void LogClientWarning(string message);
    }

    /// <summary>
    /// プロキシ同期モード別 Strategy が、クライアント側リグの表示・Joint・
    /// Root の NetworkRigidbody 設定だけを操作するための契約。
    /// </summary>
    internal interface IClientProxyRigAccess
    {
        bool HasRootNetworkRigidbody { get; }
        bool UseLegacyCustomRootCorrection { get; }
        /// <summary>
        /// リグを画面へ描く Renderer コンポーネント群を有効化または無効化する。
        /// Rigidbody や Collider の物理状態は変更しない。
        /// </summary>
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

    /// <summary>
    /// <see cref="RagdollProxyPosePublisher"/> が物理姿勢を読み取り、
    /// <see cref="RagdollController"/> の Networked 状態へ書き戻すための契約。
    /// </summary>
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
        /// <summary>
        /// 通常の C# データである <paramref name="snapshot"/> を、同期対象の Networked プロパティへコピーする。
        /// </summary>
        void ApplyProxyPoseSnapshot(ProxyPoseSnapshotData snapshot);
        void RecordHostGroundTruthSample(Vector3 actualRootPosition, Vector3 actualRootVelocity);
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
