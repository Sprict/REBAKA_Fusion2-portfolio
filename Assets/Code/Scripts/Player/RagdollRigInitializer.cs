using UnityEngine;

namespace MyFolder.Scripts.Player
{
    internal sealed class RagdollRigInitializer
    {
        public bool SetupHandJoints(
            Rigidbody leftHand,
            Rigidbody rightHand,
            Rigidbody lowerLeftArmRb,
            Rigidbody lowerRightArmRb)
        {
            ConfigurableJoint leftHandJoint = leftHand != null ? leftHand.GetComponent<ConfigurableJoint>() : null;
            ConfigurableJoint rightHandJoint = rightHand != null ? rightHand.GetComponent<ConfigurableJoint>() : null;

            if (leftHandJoint == null || rightHandJoint == null)
            {
                Debug.LogError("ConfigurableJoint is missing on hands. Ragdoll setup failed.");
                return false;
            }

            if (lowerLeftArmRb == null)
            {
                Debug.LogError("★設定忘れ: Inspectorで 'Lower Left Arm' にRigidbodyを割り当ててください！");
                return false;
            }

            if (lowerRightArmRb == null)
            {
                Debug.LogError("★設定忘れ: Inspectorで 'Lower Right Arm' にRigidbodyを割り当ててください！");
                return false;
            }

            leftHandJoint.connectedBody = lowerLeftArmRb;
            rightHandJoint.connectedBody = lowerRightArmRb;

            SetHandJointMotions(rightHandJoint, leftHandJoint);
            return true;
        }

        public void InitializeRigidbodies(
            bool hasStateAuthority,
            Rigidbody[] bodyRigidbodies,
            Rigidbody[] kinematicTargetRigidbodies)
        {
            if (bodyRigidbodies == null)
                return;

            if (hasStateAuthority)
            {
                for (int i = 0; i < bodyRigidbodies.Length; i++)
                {
                    if (bodyRigidbodies[i] == null)
                        continue;

                    if (i == 0)
                    {
                        bodyRigidbodies[i].isKinematic = false;
                    }

                    bodyRigidbodies[i].WakeUp();
                }

                return;
            }

            if (kinematicTargetRigidbodies == null)
                return;

            foreach (Rigidbody rb in kinematicTargetRigidbodies)
            {
                if (rb == null)
                    continue;

                rb.isKinematic = true;
                rb.useGravity = false;
            }
        }

        public void SetupCollisionIgnores(Collider[] colliders)
        {
            if (colliders == null)
                return;

            int ignoredCount = 0;
            for (int i = 0; i < colliders.Length; i++)
            {
                for (int j = i + 1; j < colliders.Length; j++)
                {
                    if (colliders[i] == null || colliders[j] == null)
                        continue;

                    Physics.IgnoreCollision(colliders[i], colliders[j], true);
                    ignoredCount++;
                }
            }

            Debug.Log(
                $"[RagdollController] SetupCollisionIgnores: {ignoredCount} collider pairs set to ignore collision");
        }

        private static void SetHandJointMotions(ConfigurableJoint rightHandJoint, ConfigurableJoint leftHandJoint)
        {
            rightHandJoint.xMotion = ConfigurableJointMotion.Locked;
            rightHandJoint.yMotion = ConfigurableJointMotion.Locked;
            rightHandJoint.zMotion = ConfigurableJointMotion.Locked;
            Debug.Log("Right hand joint motions set to Locked");

            leftHandJoint.xMotion = ConfigurableJointMotion.Locked;
            leftHandJoint.yMotion = ConfigurableJointMotion.Locked;
            leftHandJoint.zMotion = ConfigurableJointMotion.Locked;
            Debug.Log("Left hand joint motions set to Locked");
        }
    }
}
