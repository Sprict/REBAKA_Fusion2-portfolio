using UnityEngine;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollStateEvaluator
    {
        public PlayerState Resolve(RagdollCommand command, RagdollPhysics physics, Vector3 moveDirection, bool isPlayerGrounded)
        {
            if (physics == null)
                return PlayerState.Idle;

            if (command.IsJumping && physics.IsRagdoll && !isPlayerGrounded)
            {
                physics.ForceDeactivateRagdoll();
            }

            if (command.IsJumping && isPlayerGrounded)
                return PlayerState.Jumping;
            if (physics.IsRagdoll)
                return PlayerState.Ragdoll;
            if (command.IsPunchingLeft || command.IsPunchingRight)
                return PlayerState.Punching;
            if (command.IsGrabbingLeft || command.IsGrabbingRight)
                return PlayerState.Reaching;
            if (moveDirection.sqrMagnitude > 0.01f)
                return PlayerState.Walking;

            return PlayerState.Idle;
        }
    }
}
