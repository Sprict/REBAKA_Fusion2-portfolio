using UnityEngine;

public class RunSpecTest : MonoBehaviour
{
    private bool _started;
    private float _waitUntil;

    private void Start()
    {
        _waitUntil = Time.time + 3f;
    }

    private void Update()
    {
        if (_started) return;
        if (Time.time < _waitUntil) return;
        _started = true;

        // SpecTest
        if (FindFirstObjectByType<RagdollSpecTest>() == null)
        {
            new GameObject("__SpecTest__").AddComponent<RagdollSpecTest>();
            Debug.Log("[RunSpecTest] SpecTest started.");
        }

        // ClientProxySimTest
        if (FindFirstObjectByType<ClientProxySimTest>() == null)
        {
            new GameObject("__ClientSimTest__").AddComponent<ClientProxySimTest>();
            Debug.Log("[RunSpecTest] ClientSimTest started.");
        }
    }
}
