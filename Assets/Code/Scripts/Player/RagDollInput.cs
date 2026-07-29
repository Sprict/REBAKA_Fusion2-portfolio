using UnityEngine;
using MyFolder.Scripts.Network;
using MyFolder.Scripts.Utils;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// プレイヤーの入力処理を担当するクラス
    /// </summary>
    public class RagdollInput
    {
        // Profile 実効値（腕リーチ上限など）の参照元。null 許容（テスト用・旧経路互換）。
        private readonly IRagdollPhysicsContext _physicsContext;

        internal RagdollInput(IRagdollPhysicsContext physicsContext = null)
        {
            _physicsContext = physicsContext;
        }

        private RagdollCommand _currentCommand;
        public RagdollCommand CurrentCommand => _currentCommand;

        public void UpdateCurrentCommand(NetworkInputData inputData)
        {
            // 毎回新品のコマンドを作成（初期化忘れ防止）
            _currentCommand = new RagdollCommand();

            // 1. 移動入力の処理
            // カメラ基準の方向計算はクライアント側（InputCollector）で済んでいる
            Vector3 direction = inputData.direction;
            direction.y = 0f;
            _currentCommand.MoveDirection = Vector3.ClampMagnitude(direction, 1f);

            // 回転先方向（facingDirection）の伝搬
            Vector3 facing = inputData.facingDirection;
            facing.y = 0f;
            _currentCommand.FacingDirection = facing;

            // 2. 体の上下入力（APR サンプル準拠の絶対累積値・InputCollector で生成）
            // bodyDir.x = 胴体ベンド(MouseYAxisBody 相当, ±0.9)
            // bodyDir.y = 腕リーチ上下(MouseYAxisArms 相当, ±1.2)
            // Clamp は範囲保険。LookDirection.x→胴体ベンド、LookDirection.y→腕リーチ、BodyRoll→胴体ロールへ物理側で消費。
            // 腕リーチの範囲保険は Profile 実効値でクランプする（定数固定だと Profile で
            // reachArmInputLimit を上げても、ここで握り潰されてよじ登り用の腕下げが効かない）。
            float armReachLimit = _physicsContext != null
                ? Mathf.Max(0f, _physicsContext.ReachArmInputLimit)
                : RagdollProfile.DefaultReachArmInputLimit;
            _currentCommand.LookDirection = new Vector2(
                Mathf.Clamp(inputData.bodyDir.x, -RagdollProfile.DefaultBodyBendInputLimit, RagdollProfile.DefaultBodyBendInputLimit),
                Mathf.Clamp(inputData.bodyDir.y, -armReachLimit, armReachLimit));
            _currentCommand.BodyRoll = Mathf.Clamp(
                inputData.bodyRoll,
                -RagdollProfile.DefaultBodyRollInputLimitDegrees,
                RagdollProfile.DefaultBodyRollInputLimitDegrees);

            // 3. アクション入力（boolを詰め込むだけ）
            _currentCommand.IsJumping = inputData.Buttons.IsSet(ButtonUtils.ButtonJump);
            _currentCommand.IsDashing = inputData.Buttons.IsSet(ButtonUtils.ButtonDash);
            _currentCommand.IsCrouching = inputData.Buttons.IsSet(ButtonUtils.ButtonCrouch);
            _currentCommand.IsGrabbingRight = inputData.Buttons.IsSet(ButtonUtils.ButtonGrab_R);
            _currentCommand.IsGrabbingLeft = inputData.Buttons.IsSet(ButtonUtils.ButtonGrab_L);
            _currentCommand.IsPunchingRight = inputData.Buttons.IsSet(ButtonUtils.ButtonPunch_R);
            _currentCommand.IsPunchingLeft = inputData.Buttons.IsSet(ButtonUtils.ButtonPunch_L);
        }
    }
}
