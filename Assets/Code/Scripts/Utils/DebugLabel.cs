using UnityEngine;

// Unityエディタ時のみusingする必要がある
#if UNITY_EDITOR
using UnityEditor;
#endif

public class DebugLabel : MonoBehaviour
{
    // Unityエディタ時のみGizmo表示を行う
    #if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        GUIStyle style = new GUIStyle();
        style.normal.textColor = Color.black;
        style.normal.background = Texture2D.whiteTexture;
        Handles.Label(transform.position, $"{name} : {transform.position}", style);
    }
    #endif
}
