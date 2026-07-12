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

        // 内部で保持するキャッシュ用コマンド
        private RagdollCommand _currentCommand;

        // 外部（Controller）からはこのプロパティを通じてデータだけをもらう
        public RagdollCommand CurrentCommand => _currentCommand;

        public void ProcessInput(NetworkInputData input)
        {
            // 毎回新品のコマンドを作成（初期化忘れ防止）
            _currentCommand = new RagdollCommand();

            // 1. 移動入力の処理
            // カメラ基準の方向計算はクライアント側（InputCollector）で済んでいる
            Vector3 direction = input.direction;
            direction.y = 0f;
            _currentCommand.MoveDirection = Vector3.ClampMagnitude(direction, 1f);

            // 回転先方向（facingDirection）の伝搬
            Vector3 facing = input.facingDirection;
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
                Mathf.Clamp(input.bodyDir.x, -RagdollProfile.DefaultBodyBendInputLimit, RagdollProfile.DefaultBodyBendInputLimit),
                Mathf.Clamp(input.bodyDir.y, -armReachLimit, armReachLimit));
            _currentCommand.BodyRoll = Mathf.Clamp(
                input.bodyRoll,
                -RagdollProfile.DefaultBodyRollInputLimitDegrees,
                RagdollProfile.DefaultBodyRollInputLimitDegrees);

            // 3. アクション入力（boolを詰め込むだけ）
            _currentCommand.IsJumping = input.Buttons.IsSet(ButtonUtils.ButtonJump);
            _currentCommand.IsDashing = input.Buttons.IsSet(ButtonUtils.ButtonDash);
            _currentCommand.IsCrouching = input.Buttons.IsSet(ButtonUtils.ButtonCrouch);
            _currentCommand.IsGrabbingRight = input.Buttons.IsSet(ButtonUtils.ButtonMouse1);
            _currentCommand.IsGrabbingLeft = input.Buttons.IsSet(ButtonUtils.ButtonMouse0);
            _currentCommand.IsPunchingRight = input.Buttons.IsSet(ButtonUtils.ButtonRightpunch);
            _currentCommand.IsPunchingLeft = input.Buttons.IsSet(ButtonUtils.ButtonLeftpunch);
        }
    }
}
