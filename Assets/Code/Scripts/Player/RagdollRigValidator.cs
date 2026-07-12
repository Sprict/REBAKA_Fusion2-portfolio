using UnityEngine;

namespace MyFolder.Scripts.Player
{
    /// <summary>
    /// Unity側のUIでラグドールリグが正常にアタッチされているか検証するクラス
    /// </summary>
    internal sealed class RagdollRigValidator
    {
        /// <summary>
        /// Inspectorで刺した参照やprefab構成が壊れていないか確認する。
        /// 問題がある場合はエラーログを出力し、成功した場合はtrueを返します。
        /// </summary>
        /// <param name="bodyParts">ラグドールの各部位のGameObject配列</param>
        /// <param name="rightHand">右手のRigidbody</param>
        /// <param name="leftHand">左手のRigidbody</param>
        /// <param name="centerOfMassPoint">質量中心のTransform</param>
        /// <param name="soundSource">音声ソース</param>
        public bool Validate(
            GameObject[] bodyParts,
            Rigidbody rightHand,
            Rigidbody leftHand,
            Transform centerOfMassPoint,
            AudioSource soundSource)
        {
            if (bodyParts == null || bodyParts.Length == 0)
            {
                Debug.LogError("Body parts array is null or empty!");
                return false;
            }

            if (rightHand == null || leftHand == null)
            {
                Debug.LogError("Hand Rigidbodies are not assigned!");
                return false;
            }

            if (centerOfMassPoint == null)
            {
                Debug.LogError("Center of mass point is not assigned!");
                return false;
            }

            foreach (GameObject part in bodyParts)
            {
                if (part == null)
                {
                    Debug.LogError("One or more bodyParts are null!");
                    return false;
                }

                if (part.GetComponent<ConfigurableJoint>() == null)
                {
                    Debug.LogError($"ConfigurableJoint missing on {part.name}!");
                    return false;
                }

                if (part.GetComponent<Rigidbody>() == null)
                {
                    Debug.LogError($"Rigidbody missing on {part.name}!");
                    return false;
                }
            }

            if (soundSource == null)
            {
                Debug.LogWarning("SoundSource is not assigned. Sound effects will not play.");
            }

            return true;
        }
    }
}
