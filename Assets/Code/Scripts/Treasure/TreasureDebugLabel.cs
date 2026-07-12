// Assets/Code/Scripts/Treasure/TreasureDebugLabel.cs
using UnityEngine;

namespace MyFolder.Scripts.Treasure
{
    /// <summary>
    /// Editor 上の手動プレイテスト用ラベル。
    /// Treasure の現在の掴み人数と質量を画面に表示する。
    /// MVP の挙動確認専用で、本番ビルドでも害は無いが UI/UX 用ではない。
    /// </summary>
    [RequireComponent(typeof(Treasure))]
    public class TreasureDebugLabel : MonoBehaviour
    {
        [Tooltip("Treasure の transform.position から見たラベル表示位置のオフセット (ワールド座標)")]
        [SerializeField] private Vector3 worldOffset = new Vector3(0f, 1.5f, 0f);

        private Treasure _treasure;
        private UnityEngine.Camera _camera;

        private void Awake()
        {
            _treasure = GetComponent<Treasure>();
        }

        private void OnGUI()
        {
            if (_treasure == null) return;
            if (_camera == null) _camera = UnityEngine.Camera.main;
            if (_camera == null) return;

            Vector3 worldPos = transform.position + worldOffset;
            Vector3 screenPos = _camera.WorldToScreenPoint(worldPos);
            if (screenPos.z < 0f) return;

            string label = string.Format("Grabbers: {0}\nMass: {1:F1} kg",
                _treasure.GrabberCount, _treasure.CurrentMass);

            GUI.Label(
                new Rect(screenPos.x - 60f, Screen.height - screenPos.y - 30f, 120f, 40f),
                label);
        }
    }
}
