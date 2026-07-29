using System.Collections;
using Fusion;
using MyFolder.Scripts.Diagnostics;
using MyFolder.Scripts.Network;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Utils;
using UnityEngine;

/// <summary>
/// プレイヤーの手の接触を検出し、オブジェクトを掴む機能を実装するコンポーネント
/// </summary>
public class RagdollHandContact : NetworkBehaviour
{
    #region Serialized Fields

    [SerializeField] private RagdollController controller;

    [Tooltip("左手の場合はtrue、右手の場合はfalse")]
    [SerializeField] private bool isLeftHand;

    #endregion

    #region Private Fields

    // 掴み用のジョイント
    private Joint _attachJoint;

    // 最後に接触したオブジェクト情報
    private Rigidbody _lastContactedRigidbody;
    private NetworkId _lastContactedObjectId;

    // ジョイント破壊フラグ（非同期処理用）
    private bool _jointWasDestroyed;

    // ホストが読み取った最新の掴み/パンチ入力（OnCollisionEnter は物理コールバックで
    // GetInput を安全に呼べないため、FixedUpdateNetwork でキャッシュした値を参照する）
    private bool _grabButtonHeld;
    private bool _punchButtonHeld;

    #endregion

    #region Networked Properties

    // 掴んでいるかどうか
    [Networked] private NetworkBool HasGrabbed { get; set; }

    /// <summary>デバッグ表示用（NetworkDebugHud）: 現在何かを掴んでいるか。read-only。</summary>
    public bool IsGrabbing => HasGrabbed;

    /// <summary>
    /// 現在掴んでいる NetworkObject の Id（掴んでいなければ default）。
    /// [Networked] なので全ピアで読める。両手持ち判定（左右の手が同一 Id を掴んでいるか）に使う。
    /// </summary>
    public NetworkId GrabbedNetworkId => HasGrabbed ? GrabbedObjectId : default;

    /// <summary>デバッグ表示用（NetworkDebugHud）: 掴んでいる剛体名。掴んでいなければ null。</summary>
    public string GrabbedBodyName =>
        _attachJoint != null && _attachJoint.connectedBody != null
            ? _attachJoint.connectedBody.name
            : null;

    // 掴んでいるオブジェクトへの参照（ネットワーク同期用）
    [Networked] private NetworkId GrabbedObjectId { get; set; }

    #endregion

    #region Unity Lifecycle Methods

    public override void Spawned()
    {
        // コントローラーが設定されていない場合は親から取得
        if (controller == null)
        {
            controller = GetComponentInParent<RagdollController>();
            if (controller == null)
            {
                DebugUtils.LogRagdollError("HandContactに接続するRagdollControllerが見つかりません", this);
                enabled = false;
            }
        }

        // 両手持ち判定（IsTwoHandedHold）用に自身をコントローラーへ登録する。
        // APR_Root は後で NO ルートから detach されるため、階層検索に頼らず登録制にする。
        if (controller != null)
        {
            controller.RegisterHandContact(this, isLeftHand);
        }

        // 初期状態ではオブジェクトを掴んでいない
        HasGrabbed = false;
        GrabbedObjectId = default;
    }

    public override void FixedUpdateNetwork()
    {
        // ホスト権威化: グラブの検出・判定・実行はすべて StateAuthority(ホスト) で行う。
        // プロキシ側で Cube が kinematic 化されるため、クライアント側の dynamic 衝突検出に
        // 依存できなくなった（kinematic×kinematic は OnCollisionEnter が発火しない）。
        // ホストでは全プレイヤーの ragdoll と Cube が dynamic で実際に衝突するため検出可能。
        if (!HasStateAuthority) return;

        // ジョイントが破壊された（breakForce 超過など）場合の解放（ホストのローカル状態）
        if (HasGrabbed && (_attachJoint == null || _jointWasDestroyed))
        {
            ReleaseGrab();
            _jointWasDestroyed = false;
            return;
        }

        // 入力権限クライアントが送信した入力を、ホストが GetInput で読み取る。
        // 入力が届かない tick（パケットロス等）では誤解放を避けるため、
        // GetInput が成功した時だけ解放判定・キャッシュ更新を行う。
        if (GetInput(out NetworkInputData inputData))
        {
            if (isLeftHand)
            {
                _grabButtonHeld = inputData.Buttons.IsSet(ButtonUtils.ButtonGrab_L);
                _punchButtonHeld = inputData.Buttons.IsSet(ButtonUtils.ButtonPunch_L);
            }
            else
            {
                _grabButtonHeld = inputData.Buttons.IsSet(ButtonUtils.ButtonGrab_R);
                _punchButtonHeld = inputData.Buttons.IsSet(ButtonUtils.ButtonPunch_R);
            }

            // 掴みボタンを放したら解放
            if (HasGrabbed && !_grabButtonHeld)
            {
                ReleaseGrab();
            }
        }
    }

    #endregion

    #region Collision Methods

    private void OnCollisionEnter(Collision collision)
    {
        // ホスト権威化: ホスト上では手・掴める物理オブジェクトともに dynamic なので衝突が発火する。
        // プロキシ（クライアント）では両者 kinematic のため発火しないが、それで正しい
        // （グラブは下の DoGrab までホストで完結し、結果はネット同期で各ピアに反映される）。
        if (!HasStateAuthority) return;
        if (HasGrabbed) return; // 既に掴んでいる場合は新しい接触を無視

        // 掴み入力が押されていなければ何もしない（FixedUpdateNetwork でキャッシュした値を使う。
        // OnCollisionEnter は物理コールバックのため GetInput を直接呼ばない）
        if (!_grabButtonHeld || _punchButtonHeld) return;

        // 掴めるオブジェクトか確認
        if (collision.gameObject.CompareTag("CanBeGrabbed") || collision.gameObject.CompareTag("Player"))
        {
            Rigidbody targetRigidbody = collision.gameObject.GetComponent<Rigidbody>();

            // 所有者の NetworkObject を解決する。プレイヤーのボディパーツは
            // DetachRootFromParent() で NO ルートから切り離されているため
            // GetComponentInParent では届かない。スポーン時に登録した
            // RagdollBodyOwnerRegistry（Rigidbody→NO）を第一に使い、
            // `CanBeGrabbed` タグの一般オブジェクトは親階層検索でフォールバックする
            NetworkObject netObj = RagdollBodyOwnerRegistry.GetOwner(targetRigidbody);
            if (netObj == null)
            {
                netObj = collision.gameObject.GetComponentInParent<NetworkObject>();
            }

            // 自分自身のパーツは除外（全プレイヤーが同一レイヤー(Player=6)のため、
            // レイヤー比較だと他プレイヤーまで弾いてしまう。所有 NO の同一性で判定する）
            if (netObj == Object)
            {
                return;
            }

            if (targetRigidbody != null)
            {
                // 接触情報を保存
                _lastContactedRigidbody = targetRigidbody;
                _lastContactedObjectId = netObj != null ? netObj.Id : default;

                // ホストでそのまま掴みを実行（RPC 不要）
                DoGrab(netObj != null ? netObj.Id : default);
            }
        }
    }

    #endregion

    #region Grab Logic (Host-side)

    // ホスト権威: 掴みの実行（ジョイント生成）。OnCollisionEnter(ホスト) から直接呼ばれる。
    // 以前は RPC_GrabObject(RpcSources.InputAuthority -> RpcTargets.StateAuthority) だったが、
    // グラブ検出をホスト側へ移したため RPC は不要になった。防御的に StateAuthority を確認する。
    private void DoGrab(NetworkId objId)
    {
        if (!Object.HasStateAuthority)
        {
            RagdollNetDiagnostics.LogAuthorityViolation(
                $"event=grab_execute_without_state_auth hand={(isLeftHand ? "left" : "right")} obj_id={objId}",
                this,
                0.2f,
                $"grab_no_state_{GetInstanceID()}");
            return;
        }

        if (HasGrabbed) return; // 既に何か掴んでいたら何もしない

        if (objId == default)
        {
            DebugUtils.LogRagdollWarning("DoGrab: Attempted to grab with invalid NetworkId.", this);
            return;
        }

        NetworkObject netObj = Runner.FindObject(objId);
        if (netObj == null)
        {
            DebugUtils.LogRagdollWarning($"DoGrab: NetworkObject with Id {objId} not found.", this);
            return;
        }

        // 接触したパーツの Rigidbody を優先する（プレイヤーを掴む場合、NetworkObject は
        // ルートにしか無い上に APR_Root は detach 済みのため、階層からは辿れない。
        // OnCollisionEnter で保存した接触パーツをそのまま使う）。
        Rigidbody targetRigidbody =
            _lastContactedRigidbody != null && _lastContactedObjectId == objId
                ? _lastContactedRigidbody
                : netObj.GetComponent<Rigidbody>();
        if (targetRigidbody == null)
        {
            DebugUtils.LogRagdollWarning($"DoGrab: Rigidbody not found on NetworkObject {netObj.name} (Id: {objId}).", this);
            return;
        }

        // ここまで来たら掴める対象が見つかった

        // 既存の掴み用ジョイントがあれば破棄 (安全策)
        if (_attachJoint != null)
        {
            Destroy(_attachJoint);
            _attachJoint = null; // 参照もクリア
        }

        RagdollProfile grabProfile = ResolveGrabProfile();
        if (grabProfile == null)
        {
            DebugUtils.LogRagdollWarning("DoGrab: RagdollProfile not found for grab drive.", this);
            return;
        }

        float effectiveBreakForce = grabProfile.genericGrabBreakForce;
        float effectiveBreakTorque = grabProfile.genericGrabBreakTorque;

        // プレイヤー同士・一般オブジェクトともに FixedJoint（完全拘束）。
        // 2026-07: プレイヤー同士だけ ConfigurableJoint(Free+Drive→Limited+slack) を
        // 試したが、いずれも「掴んで持つ」感触が FixedJoint に劣ると判断し撤回。
        // breakForce で「発散する前に壊れる」設計に一本化している。
        _attachJoint = gameObject.AddComponent<FixedJoint>();

        _attachJoint.breakForce = effectiveBreakForce;
        _attachJoint.breakTorque = effectiveBreakTorque;
        _attachJoint.connectedBody = targetRigidbody;

        RagdollNetDiagnostics.Log(
            "joint_create",
            $"hand={(isLeftHand ? "left" : "right")} target={targetRigidbody.gameObject.name} stateAuthority={Object.HasStateAuthority} breakForce={effectiveBreakForce}",
            this);

        // 状態を更新 (掴み成功時)
        GrabbedObjectId = objId;
        HasGrabbed = true;

        // ジョイント破壊イベントを登録 (状態更新後)
        StartCoroutine(MonitorJointStatus());

        if (controller != null)
        {
            (controller as IRagdollAudioSink)?.PlayImpactSound();
        }

        DebugUtils.LogRagdollState($"{(isLeftHand ? "左" : "右")}手で{targetRigidbody.gameObject.name}を掴みました", this);
    }

    #endregion

    #region Private Methods

    private RagdollProfile ResolveGrabProfile()
    {
        if (controller == null)
        {
            controller = GetComponentInParent<RagdollController>();
        }

        return controller != null ? controller.Profile : null;
    }

    private void ReleaseGrab()
    {
        if (!Object.HasStateAuthority)
        {
            RagdollNetDiagnostics.LogAuthorityViolation(
                $"event=release_without_state_auth hand={(isLeftHand ? "left" : "right")}",
                this,
                0.2f,
                $"release_private_no_state_{GetInstanceID()}");
            return;
        }

        if (HasGrabbed)
        {
            // ジョイントを切断
            if (_attachJoint != null)
            {
                _attachJoint.connectedBody = null;
                Destroy(_attachJoint);
                _attachJoint = null;

                RagdollNetDiagnostics.Log(
                    "joint_destroy",
                    $"hand={(isLeftHand ? "left" : "right")} stateAuthority={Object.HasStateAuthority}",
                    this);
            }

            // ネットワーク状態をリセット
            HasGrabbed = false;
            GrabbedObjectId = default;

            // ローカル参照をクリア
            _lastContactedRigidbody = null;
            _lastContactedObjectId = default;

            if (controller != null)
            {
                DebugUtils.LogRagdollState($"{(isLeftHand ? "左" : "右")}手の掴みを解除しました", this);
            }
        }
    }

    private IEnumerator MonitorJointStatus()
    {
        // ジョイントの状態を監視
        while (HasGrabbed && _attachJoint != null)
        {
            yield return new WaitForFixedUpdate();
        }

        // ジョイントが存在しないのに掴み状態の場合
        if (HasGrabbed && _attachJoint == null)
        {
            _jointWasDestroyed = true;

            // 実際の解放は FixedUpdateNetwork(ホスト) が _jointWasDestroyed を見て ReleaseGrab() する
        }
    }

    #endregion

    #region Public Methods

    // 手動でジョイントを破壊した場合（別のコンポーネントから呼び出し用）
    public void BreakJoint()
    {
        if (!Object.HasStateAuthority)
        {
            RagdollNetDiagnostics.LogAuthorityViolation(
                $"event=break_joint_without_state_auth hand={(isLeftHand ? "left" : "right")}",
                this,
                0.2f,
                $"break_no_state_{GetInstanceID()}");
            return;
        }

        if (HasGrabbed && _attachJoint != null)
        {
            Destroy(_attachJoint);
            _attachJoint = null;
            _jointWasDestroyed = true;

            RagdollNetDiagnostics.Log(
                "joint_destroy",
                $"hand={(isLeftHand ? "left" : "right")} reason=break_joint_api",
                this);
        }
    }

    #endregion
}
