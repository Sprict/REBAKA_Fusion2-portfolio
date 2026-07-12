using System;
using MyFolder.Scripts.Diagnostics;
using MyFolder.Scripts.Network;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollHostSimulationOrchestrator
    {
        private readonly IHostSimulationContext _context;
        private readonly UnityEngine.Object _diagnosticsContext;

        public RagdollHostSimulationOrchestrator(
            IHostSimulationContext context,
            UnityEngine.Object diagnosticsContext)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _diagnosticsContext = diagnosticsContext;
        }

        public void RunFixedUpdate()
        {
            _context.EmitSyncDiagnostics("fixed");

            if (!_context.TryGetInput(out NetworkInputData data))
            {
                if (RagdollNetDiagnostics.IsEnabled)
                {
                    RagdollNetDiagnostics.Log(
                        "host_input",
                        $"role=Host phase=fixed has_input=false inputAuthority={_context.HasInputAuthority} " +
                        $"runner_is_resim={_context.IsResimulation}",
                        _diagnosticsContext,
                        0.2f,
                        $"host_input_missing_{_context.InstanceId}");
                }

                return;
            }

            if (_context.InputHandler == null || _context.PhysicsHandler == null)
            {
                return;
            }

            _context.InputHandler.ProcessInput(data);
            RagdollCommand command = _context.InputHandler.CurrentCommand;

            if (RagdollNetDiagnostics.IsEnabled)
            {
                RagdollNetDiagnostics.Log(
                    "host_input",
                    $"role=Host phase=fixed has_input=true inputAuthority={_context.HasInputAuthority} " +
                    $"move_mag={command.MoveDirection.magnitude:F3} facing_mag={command.FacingDirection.magnitude:F3} " +
                    $"look_mag={command.LookDirection.magnitude:F3} roll={command.BodyRoll:F1} jump={command.IsJumping} " +
                    $"grab_l={command.IsGrabbingLeft} grab_r={command.IsGrabbingRight} " +
                    $"punch_l={command.IsPunchingLeft} punch_r={command.IsPunchingRight}",
                    _diagnosticsContext,
                    0.2f,
                    $"host_input_{_context.InstanceId}");
            }

            _context.MoveDirection = command.MoveDirection;
            _context.FacingDirection = command.FacingDirection;
            _context.LookDirection = command.LookDirection;
            _context.BodyRoll = command.BodyRoll;

            _context.ResolvePlayerState(command);
            _context.PhysicsHandler.UpdatePhysics(
                _context.CurrentState,
                command,
                _context.DeltaTime);
        }
    }
}
