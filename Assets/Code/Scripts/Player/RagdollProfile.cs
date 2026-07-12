using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// クライアントプロキシ（非StateAuthority）の同期表示方式。
    /// </summary>
    public enum ProxySyncMode
    {
        /// <summary>従来方式: Root を kinematic 化し MovePosition/PID で追従、四肢はローカル物理</summary>
        Hybrid = 0,

        /// <summary>全身ポーズのスナップショット補間: 全パーツ kinematic + Render() で純粋な視覚補間（推奨）</summary>
        SnapshotInterpolation = 1,

        /// <summary>全クライアントで物理計算（2026-03 A/B テストで棄却済み・参考用）</summary>
        Forecast = 2
    }

    /// <summary>
    /// ラグドールの物理パラメータ群（ScriptableObject）
    /// </summary>
    [CreateAssetMenu(fileName = "RagdollProfile", menuName = "Ragdoll/Profile")]
    public class RagdollProfile : ScriptableObject
    {
        public const float DefaultBodyBendInputLimit = 0.9f;
        public const float DefaultReachArmInputLimit = 1.2f;
        public const float DefaultBodyRollInputLimitDegrees = 60f;
        public const float DefaultAirControlMultiplier = 0.35f;

        // --- Reach (Grab) Arm Drive のチューニング原点 ---
        // Play 中に spring/damper/maxForce を弄って分からなくなったら、Inspector 右上の
        // 歯車メニュー → "Reset Reach Arm Tuning" でこの値群へ戻せる（ResetReachArmTuning）。
        public const float DefaultReachUpperArmJointSpring = 2000f;
        public const float DefaultReachUpperArmJointDamper = 200f;
        public const float DefaultReachUpperArmJointMaxForce = 1000f;
        public const float DefaultReachLowerArmJointSpring = 2000f;
        public const float DefaultReachLowerArmJointDamper = 200f;
        public const float DefaultReachLowerArmJointMaxForce = 1000f;

        [Header("Movement Properties")]
        [Tooltip("true: 進行方向がカメラ前方。false: 体の向きを前方とする。")]
        public bool forwardIsCameraDirection = true;

        [Tooltip("移動速度(m/s)。Root へ加える力の目標速度。")]
        public float moveSpeed = 10f;

        [Tooltip("Dashボタン押下中の移動速度倍率。1=加速なし、2=2倍速。moveSpeed に掛かる。")]
        [Min(1f)]
        public float dashSpeedMultiplier = 1.8f;

        [Tooltip("しゃがみ中の移動速度倍率。0.5=半分。Dash と同時押しでは両方の倍率が掛かる。")]
        [Range(0f, 1f)]
        public float crouchSpeedMultiplier = 0.5f;

        [Tooltip("体の向きを変える角速度(deg/s 相当)。大きいほど素早くターン。")]
        public float turnSpeed = 6f;

        [Tooltip("ジャンプ時に Root へ加える瞬間力(N)。大きいほど高く跳ぶ。")]
        public float jumpForce = 54f;

        [Tooltip("空中で入力方向へ水平速度を寄せる強さ。0=空中入力なし、1=地上と同じ制御。")]
        [Range(0f, 1f)]
        public float airControlMultiplier = DefaultAirControlMultiplier;

        [Header("Walking Properties")]
        [Tooltip("1歩あたりの所要時間(秒)。小さいほど足が速く動く。")]
        public float stepDuration = 0.2f;

        [Tooltip("足を持ち上げる高さ(m)。大きいほど足が高く上がる。")]
        public float stepHeight = 1.7f;

        [Tooltip("足を地面に押しつける力。大きいほど滑りにくいが振動しやすい。")]
        public float feetMountForce = 25f;

        [Header("Mouse Body/Reach Input")]
        [Tooltip("胴体ベンド入力の上下限。APR MouseYAxisBody 相当。")]
        [Min(0f)]
        public float bodyBendInputLimit = DefaultBodyBendInputLimit;

        [Tooltip("腕リーチ入力の上下限。APR MouseYAxisArms 相当。")]
        [Min(0f)]
        public float reachArmInputLimit = DefaultReachArmInputLimit;

        [Tooltip("Alt+マウスXで操作する胴体ロール角の左右上限(度)。")]
        [Min(0f)]
        public float bodyRollInputLimitDegrees = DefaultBodyRollInputLimitDegrees;

        [Header("Reach (Grab) Arm Pose")]
        [Tooltip("リーチ時の上腕ベース角(度)。8f = パンチrelease相当（前方90度）。当方リグ検証済み規約。")]
        public float reachUpperArmBasePitch = 8f;

        [Tooltip("マウスY(腕リーチ ±1.2)1単位あたりの上腕角度変化(度)。大きいほど上下に大きく振れる。")]
        public float reachUpperArmPitchPerUnit = 35f;

        [Tooltip("リーチ時の上腕ピッチ下限(度)。腕が上がり過ぎる/潜り過ぎるのを抑える。")]
        public float reachUpperArmMinPitch = -60f;

        [Tooltip("リーチ時の上腕ピッチ上限(度)。腕が下がり過ぎる/背面へ回るのを抑える。")]
        public float reachUpperArmMaxPitch = 70f;

        [Tooltip("リーチ時の下腕(肘)角度(度)。30f = パンチrelease相当（肘ほぼ伸び切り）。")]
        public float reachLowerArmPitch = 30f;

        [Tooltip("プレイヤー同士・一般オブジェクト(Treasure以外)を掴むFixedJointのbreakForce/breakTorque。低いほど早く壊れる。プレイヤー同士の引っ張り合いで体が歪む前に外れてほしい場合は下げる。")]
        [Min(0f)]
        public float genericGrabBreakForce = 1400f;

        [Header("Reach (Grab) Arm Drive")]
        [Tooltip("リーチ/掴み中の上腕JointDriveのスプリング。肩側を目標ポーズへ戻す力。")]
        [Min(0f)]
        public float reachUpperArmJointSpring = DefaultReachUpperArmJointSpring;

        [Tooltip("リーチ/掴み中の上腕JointDriveのダンパー。肩側の振動を抑える。")]
        [Min(0f)]
        public float reachUpperArmJointDamper = DefaultReachUpperArmJointDamper;

        [Tooltip("リーチ/掴み中の上腕JointDriveの最大筋力(maximumForce)。小さいほど重い物に負けて腕が後方へ流れる。掴みbreakForceより十分小さくする。")]
        [Min(0f)]
        public float reachUpperArmJointMaxForce = DefaultReachUpperArmJointMaxForce;

        [Tooltip("リーチ/掴み中の下腕JointDriveのスプリング。大きいほど持っている物に肘を折り曲げられにくい。")]
        [Min(0f)]
        public float reachLowerArmJointSpring = DefaultReachLowerArmJointSpring;

        [Tooltip("リーチ/掴み中の下腕JointDriveのダンパー。肘側の振動を抑えるが、上げ過ぎると反応が重くなる。")]
        [Min(0f)]
        public float reachLowerArmJointDamper = DefaultReachLowerArmJointDamper;

        [Tooltip("リーチ/掴み中の下腕JointDriveの最大筋力(maximumForce)。肘の可動範囲はangular limitに任せ、ここは重さに負ける上限として調整する。")]
        [Min(0f)]
        public float reachLowerArmJointMaxForce = DefaultReachLowerArmJointMaxForce;

        [Header("Treasure Grab Drive")]
        [Tooltip("Treasureを掴むConfigurableJointの位置スプリング。大きいほど手元へ強く引き戻す。")]
        [Min(0f)]
        public float grabDriveSpring = 10000f;

        [Tooltip("Treasureを掴むConfigurableJointの位置ダンパー。大きいほど握りの振動を抑える。")]
        [Min(0f)]
        public float grabDriveDamper = 500f;

        [Tooltip("Treasureを掴むConfigurableJointの最大駆動力。重すぎる対象に引き勝てないとき、この力で飽和させる。")]
        [Min(0f)]
        public float grabDriveMaxForce = 500f;

        [Tooltip("Treasureを持っている間にRootへ加える移動駆動力の上限(N)。小さいほど重い物に引き止められやすくなる。")]
        [Min(0f)]
        public float carryMoveMaxForce = 150f;

        [Tooltip("Treasure運搬ハーネスの余白距離(m)。RootとTreasureの現在距離から追加で許す距離。")]
        [Min(0f)]
        public float carryHarnessSlack = 0.35f;

        [Tooltip("Treasure運搬ハーネスの距離制限スプリング。大きいほど限界距離で強く引き戻す。")]
        [Min(0f)]
        public float carryHarnessLimitSpring = 4000f;

        [Tooltip("Treasure運搬ハーネスの距離制限ダンパー。大きいほど限界距離付近の揺れを抑える。")]
        [Min(0f)]
        public float carryHarnessLimitDamper = 400f;

        [Header("Balance Properties")]
        [Tooltip("転倒後、自力起き上がりが可能になったら自動で起き上がるか。")]
        public bool autoGetUpWhenPossible = true;

        [Tooltip("次の着地点を予測してバランスを補正するステップ予測を使うか。")]
        public bool useStepPrediction = true;

        [Tooltip("バランス目標とする重心の高さ(m)。キャラの身長に合わせる。")]
        public float balanceHeight = 2.5f;

        [Tooltip("体幹バランス用 JointDrive の positionSpring 強度。大きいほど直立が強い。")]
        public float balanceStrength = 5000f;

        [Tooltip("胴体(コア)ポーズ用 JointDrive の positionSpring 強度。")]
        public float coreStrength = 1500f;

        [Tooltip("四肢ポーズ用 JointDrive の positionSpring 強度。")]
        public float limbStrength = 500f;

        [Header("Joint Damping Settings")]
        [Tooltip("バランス用JointDriveのダンピング係数比率（balanceStrength * この値）")]
        [Range(0f, 0.5f)]
        public float balanceDamperRatio = 0.1f;

        [Tooltip("ポーズ用JointDriveのダンピング係数比率（limbStrength * この値）")]
        [Range(0f, 0.5f)]
        public float poseDamperRatio = 0.15f;

        [Tooltip("コア用JointDriveのダンピング係数比率（coreStrength * この値）")]
        [Range(0f, 0.5f)]
        public float coreDamperRatio = 0.1f;

        [Header("Balance Calculation - COM Analysis")]
        [Tooltip("バランス判定のマージン（メートル）。重心がこの範囲内なら安定と判定")]
        public float balanceMargin = 0.15f;

        [Tooltip("重心計算を有効にするか")] public bool useCOMBasedBalance = true;

        [Header("Animation-Target Following (Phase 2)")]
        [Tooltip("Idle時のバランス（直立トルク）優先度 (0-1)。1=完全にバランス優先")]
        [Range(0f, 1f)]
        public float idleBalancePriority = 0.8f;

        [Tooltip("Walking時のバランス優先度 (0-1)。0=完全にアニメーション追従優先")]
        [Range(0f, 1f)]
        public float walkingBalancePriority = 0.6f;

        [Tooltip("Idle時のポーズ剛性乗数")] public float idlePoseStiffnessMultiplier = 0.5f;

        [Tooltip("Walking時のポーズ剛性乗数")] public float walkingPoseStiffnessMultiplier = 1.2f;

        [Tooltip("状態間の補間速度")] public float stateBlendSpeed = 5f;

        [Header("Ragdoll Drive Off")]
        [Tooltip("ラグドール化中に残す弱いJointDriveのスプリング。0に近いほど完全に脱力し、大きいほど姿勢が残る。")]
        [Min(0f)]
        public float ragdollDriveOffSpring = 25f;

        [Tooltip("ラグドール化中に残す弱いJointDriveのダンパー。大きいほどラグドール中の揺れを抑える。")]
        [Min(0f)]
        public float ragdollDriveOffDamper = 5f;

        [Header("Motion Responsiveness")]
        [Tooltip("入力方向の目標速度へRoot速度を寄せるLerp係数。1に近いほど即応、0に近いほど滑る。")]
        [Range(0f, 1f)]
        public float movementVelocityLerp = 0.8f;

        [Header("Punch")]
        [Tooltip("パンチ解放時に下腕へ加える瞬間力。大きいほどパンチが強く前へ出る。")]
        [Min(0f)]
        public float punchImpulse = 10f;

        [Tooltip("パンチ解放後、腕を元ポーズへ戻し始めるまでの待ち時間(秒)。")]
        [Min(0f)]
        public float punchRecoveryDelaySeconds = 0.15f;

        [Tooltip("パンチ後に腕を元ポーズへ戻す補間速度。大きいほど素早く戻る。")]
        [Min(0f)]
        public float punchRecoveryLerpSpeed = 12f;

        [Header("Client Proxy Secondary Motion")]
        [Tooltip("クライアントの非rootパーツに加える慣性加速度の強さ。0で無効。")]
        [Min(0f)]
        public float proxyInertiaForceScale = 0.35f;

        [Tooltip("root速度差分から計算した加速度の上限。ネットワークスパイクを抑える。")]
        [Min(0f)]
        public float proxyInertiaMaxAcceleration = 10f;

        [Tooltip("慣性用加速度の平滑化率。1で生値、低いほどなめらか。")]
        [Range(0f, 1f)]
        public float proxyInertiaSmoothing = 0.25f;

        [Tooltip("見た目用にだけ加える弱い重力倍率。0で無効。")]
        [Min(0f)]
        public float proxySecondaryGravityScale = 0f;

        [Header("Proxy Sync Mode (A/B Test)")]
        [Tooltip("クライアントプロキシの同期方式。Hybrid=kinematic+PID補正(従来)、SnapshotInterpolation=全身ポーズのスナップショット補間(推奨)、Forecast=全クライアント物理(棄却済み・参考用)")]
        public ProxySyncMode proxySyncMode = ProxySyncMode.Hybrid;

        [Tooltip("1tickでこの距離(m)を超えるRoot移動をテレポートとみなし、クライアント側の補間をスキップさせる")]
        [Min(0.5f)]
        public float poseTeleportDetectThreshold = 2f;

        [Header("Client Decoration Rendering")]
        [Tooltip("クライアント装飾（Other/配下のローカル物理RB）の描画用ローパスフィルタ時定数(秒)。" +
                 "大きいほど微振動が消えるが動きの遅れが増える。0で平滑化なし。目安: 0.03〜0.15")]
        [Range(0f, 0.3f)]
        public float decorationSmoothingTau = 0.05f;

        [Header("Forecast Physics (A/B Test)")]
        [Tooltip("[Deprecated] proxySyncMode = Forecast を使用すること。後方互換のため残置: 有効かつ proxySyncMode が Hybrid の場合は Forecast 扱いになる。")]
        public bool useForecastPhysics;

        /// <summary>
        /// 後方互換を考慮した実効同期モードを返す。
        /// 旧 useForecastPhysics フラグのみが設定された既存アセットを壊さない。
        /// </summary>
        public ProxySyncMode ResolveProxySyncMode()
        {
            if (proxySyncMode == ProxySyncMode.Hybrid && useForecastPhysics)
            {
                return ProxySyncMode.Forecast;
            }

            return proxySyncMode;
        }

        [Header("Audio Resources")]
        [Tooltip("衝突・着地時に再生するSEクリップ群。ランダムに1つ選ばれる。")]
        public AudioClip[] impactSounds;

        [Tooltip("ダメージ・被弾時に再生するSEクリップ群。ランダムに1つ選ばれる。")]
        public AudioClip[] hitSounds;

        /// <summary>
        /// リーチ(掴み)腕ドライブの spring/damper/maxForce だけをチューニング原点へ戻す。
        /// Inspector 右上の歯車メニュー → "Reset Reach Arm Tuning" から実行する。
        /// 全体 Reset と違い、移動・バランス等の他パラメータには触れない。
        /// </summary>
        [ContextMenu("Reset Reach Arm Tuning")]
        public void ResetReachArmTuning()
        {
            reachUpperArmJointSpring = DefaultReachUpperArmJointSpring;
            reachUpperArmJointDamper = DefaultReachUpperArmJointDamper;
            reachUpperArmJointMaxForce = DefaultReachUpperArmJointMaxForce;
            reachLowerArmJointSpring = DefaultReachLowerArmJointSpring;
            reachLowerArmJointDamper = DefaultReachLowerArmJointDamper;
            reachLowerArmJointMaxForce = DefaultReachLowerArmJointMaxForce;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}
