using System;

namespace MyFolder.Scripts.Player
{
    internal sealed class LegacyClientProxyModeStrategy : IClientProxyModeStrategy
    {
        private readonly IClientProxyRigAccess _rigAccess;
        private readonly IClientBootstrapContext _context;

        public LegacyClientProxyModeStrategy(IClientProxyRigAccess rigAccess, IClientBootstrapContext context)
        {
            _rigAccess = rigAccess ?? throw new ArgumentNullException(nameof(rigAccess));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public void Apply()
        {
            _rigAccess.DisableClientJointDrives();

            if (_rigAccess.RelaxClientJointsOnSpawn)
            {
                _rigAccess.DisableClientJoints();
                return;
            }

            _context.LogClientBootstrap(
                "client_joint_mode",
                "role=Client phase=spawn joint_drives_disabled=true joints_relaxed=0",
                0.2f,
                $"client_joint_mode_{_context.InstanceId}");
        }
    }
}
