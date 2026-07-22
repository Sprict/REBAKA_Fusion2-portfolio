namespace MyFolder.Scripts.Player
{
    internal readonly struct RagdollGroundingUpdate
    {
        public RagdollGroundingUpdate(bool leftFootGrounded, bool rightFootGrounded, bool anyFootGrounded, bool shouldAttemptRecover)
        {
            LeftFootGrounded = leftFootGrounded;
            RightFootGrounded = rightFootGrounded;
            AnyFootGrounded = anyFootGrounded;
            ShouldAttemptRecover = shouldAttemptRecover;
        }

        public bool LeftFootGrounded { get; }
        public bool RightFootGrounded { get; }
        public bool AnyFootGrounded { get; }
        public bool ShouldAttemptRecover { get; }
    }

    internal sealed class RagdollGroundingService
    {
        public RagdollGroundingUpdate Apply(
            bool isLeftFoot,
            bool isGrounded,
            bool currentLeftFootGrounded,
            bool currentRightFootGrounded,
            PlayerState currentState,
            bool autoGetUpWhenPossible)
        {
            bool leftFootGrounded = currentLeftFootGrounded;
            bool rightFootGrounded = currentRightFootGrounded;

            if (isLeftFoot)
            {
                leftFootGrounded = isGrounded;
            }
            else
            {
                rightFootGrounded = isGrounded;
            }

            bool bothFeetGrounded = leftFootGrounded && rightFootGrounded;
            bool anyFootGrounded = leftFootGrounded || rightFootGrounded;
            bool shouldAttemptRecover = currentState == PlayerState.Ragdoll &&
                                        bothFeetGrounded &&
                                        autoGetUpWhenPossible;

            return new RagdollGroundingUpdate(
                leftFootGrounded,
                rightFootGrounded,
                anyFootGrounded,
                shouldAttemptRecover);
        }
    }
}
