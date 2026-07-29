using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Utils
{
    public static class JointConfigurator
    {
        public static void ConfigureJoint(ConfigurableJoint joint, JointDrive drive)
        {
            joint.angularXDrive = drive;
            joint.angularYZDrive = drive;
        }

        public static void ConfigureArmJoints(GameObject[] parts, int upperArmIndex, int lowerArmIndex, JointDrive drive)
        {
            ConfigureJoint(parts[upperArmIndex].GetComponent<ConfigurableJoint>(), drive);
            ConfigureJoint(parts[lowerArmIndex].GetComponent<ConfigurableJoint>(), drive);
        }

        public static void ConfigureLegJoints(GameObject[] parts, int upperLegIndex, int lowerLegIndex, JointDrive drive)
        {
            ConfigureJoint(parts[upperLegIndex].GetComponent<ConfigurableJoint>(), drive);
            ConfigureJoint(parts[lowerLegIndex].GetComponent<ConfigurableJoint>(), drive);
        }

        public static JointDrive CreateJointDrive(float spring, float damper = 0f, float maxForce = float.MaxValue)
        {
            JointDrive drive = new JointDrive();
            drive.positionSpring = spring;
            drive.positionDamper = damper;
            drive.maximumForce = maxForce;
            return drive;
        }
    }
}
