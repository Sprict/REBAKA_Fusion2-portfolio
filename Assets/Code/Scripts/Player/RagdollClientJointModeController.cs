using System;
using MyFolder.Scripts.Diagnostics;
using UnityEngine;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollClientJointModeController
    {
        private readonly UnityEngine.Object _context;
        private readonly Func<int> _getInstanceId;

        public RagdollClientJointModeController(UnityEngine.Object context, Func<int> getInstanceId)
        {
            _context = context;
            _getInstanceId = getInstanceId ?? throw new ArgumentNullException(nameof(getInstanceId));
        }

        public void DisableJointDrives(ConfigurableJoint[] joints)
        {
            if (joints == null)
            {
                return;
            }

            JointDrive zeroDrive = new JointDrive
            {
                positionSpring = 0f,
                positionDamper = 0f,
                maximumForce = 0f
            };

            foreach (ConfigurableJoint joint in joints)
            {
                if (joint == null)
                {
                    continue;
                }

                joint.xDrive = zeroDrive;
                joint.yDrive = zeroDrive;
                joint.zDrive = zeroDrive;
                joint.angularXDrive = zeroDrive;
                joint.angularYZDrive = zeroDrive;
                joint.slerpDrive = zeroDrive;
            }
        }

        public void DisableJoints(ConfigurableJoint[] joints)
        {
            if (joints == null)
            {
                return;
            }

            int relaxedCount = 0;
            foreach (ConfigurableJoint joint in joints)
            {
                if (joint == null)
                {
                    continue;
                }

                joint.connectedBody = null;
                joint.xMotion = ConfigurableJointMotion.Free;
                joint.yMotion = ConfigurableJointMotion.Free;
                joint.zMotion = ConfigurableJointMotion.Free;
                joint.angularXMotion = ConfigurableJointMotion.Free;
                joint.angularYMotion = ConfigurableJointMotion.Free;
                joint.angularZMotion = ConfigurableJointMotion.Free;
                relaxedCount++;
            }

            if (relaxedCount <= 0)
            {
                return;
            }

            RagdollNetDiagnostics.Log(
                "client_joint_mode",
                $"role=Client phase=spawn joints_relaxed={relaxedCount}",
                _context,
                0.2f,
                $"client_joint_mode_{_getInstanceId()}");

            Debug.Log($"[RAGDOLL_CLIENT_MODE] joints_relaxed={relaxedCount}", _context);
        }
    }
}
