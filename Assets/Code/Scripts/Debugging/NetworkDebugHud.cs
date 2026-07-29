using System.Collections.Generic;
using System.Text;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MyFolder.Scripts.Debugging
{
    /// <summary>
    /// 2-client 検証用の read-only ネットワーク状態オーバーレイ。F1 でトグル。
    ///
    /// 表示は二層構成:
    /// - コーナーHUD（画面左上）: Runner 全体情報のみ（Host/Client・LocalPlayer・Tick・RTT・接続人数）。
    /// - ワールド空間ラベル: 各ルート NetworkObject の真上に権限（[MYINPUT]/[AUTH]/[proxy]）と
    ///   kinematic 状態（kin/dyn）を表示。グラブ中の手を持つオブジェクトには " -> 対象名" を追記。
    ///
    /// 設計原則:
    /// - 完全 read-only。シミュレーション状態には一切書き込まない（表示バグが物理バグに化けるのを防ぐ）。
    /// - OnGUI ベース。uGUI/UI Toolkit のシーン配線が不要で、ParrelSync クローン側でも確実に出る。
    /// - RuntimeInitializeOnLoadMethod で自動生成（シーンファイルを汚さない）。Editor/Development Build 限定。
    /// - ラベル文字列の構築は _refreshInterval 間隔のキャッシュ更新 + StringBuilder 再利用で GC を抑える。
    ///   毎フレーム実行するのは WorldToScreenPoint のみ（ルート NObj 数 ≒ 数十回の座標変換で、
    ///   物理検証へのノイズとしては無視できる）。ラベルの重なりは意図的に未解決とする
    ///   （デバッグ専用表示であり、回避レイアウトのコストに見合わない。密集時は F1 で消す）。
    /// </summary>
    public sealed class NetworkDebugHud : MonoBehaviour
    {
        // プロジェクトは Input System 専用設定のため旧 UnityEngine.Input は使えない（実行時例外）。
        [SerializeField] private Key _toggleKey = Key.F1;
        [SerializeField, Min(0.1f)] private float _refreshInterval = 0.5f;
        [SerializeField] private float _labelWorldOffsetY = 0.6f;

        private static NetworkDebugHud _instance;

        private bool _visible = false;
        private float _nextRefreshAt;
        private string _cornerText = "";
        private int _cornerLineCount = 1;
        private readonly StringBuilder _sb = new StringBuilder(1024);
        private readonly List<NetworkObject> _objects = new List<NetworkObject>(64);
        private readonly List<RagdollHandContact> _hands = new List<RagdollHandContact>(8);

        private struct WorldLabel
        {
            public Transform Target;
            public string Text;
        }

        private readonly List<WorldLabel> _labels = new List<WorldLabel>(64);
        private readonly Dictionary<Transform, int> _labelIndexByRoot = new Dictionary<Transform, int>(64);
        private readonly GUIContent _labelContent = new GUIContent();
        private GUIStyle _labelStyle; // GUIStyle は OnGUI 内でしか安全に生成できない

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_instance != null) return;
            var go = new GameObject("NetworkDebugHud");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<NetworkDebugHud>();
#endif
        }

        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[_toggleKey].wasPressedThisFrame)
            {
                _visible = !_visible;
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;

            if (Time.unscaledTime >= _nextRefreshAt)
            {
                _nextRefreshAt = Time.unscaledTime + _refreshInterval;
                RebuildCache();
            }

            DrawCornerHud();

            // GUI.Label は Repaint でしか描画されないため、座標変換も Repaint に限定して回数を半減する
            if (Event.current.type == EventType.Repaint)
            {
                DrawWorldLabels();
            }
        }

        private void DrawCornerHud()
        {
            const float width = 360f;
            float height = 16f * _cornerLineCount + 16f;
            GUI.Box(new Rect(10f, 10f, width, height), GUIContent.none);
            GUI.Label(new Rect(18f, 16f, width - 16f, height - 12f), _cornerText);
        }

        private void DrawWorldLabels()
        {
            // 同プロジェクトの MyFolder.Scripts.Camera 名前空間と衝突するため完全修飾
            UnityEngine.Camera cam = UnityEngine.Camera.main;
            if (cam == null) return;

            if (_labelStyle == null)
            {
                _labelStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 12,
                };
                _labelStyle.normal.textColor = Color.white;
            }

            foreach (WorldLabel label in _labels)
            {
                if (label.Target == null) continue; // Despawn 済み（次回リフレッシュで消える）

                Vector3 sp = cam.WorldToScreenPoint(label.Target.position + Vector3.up * _labelWorldOffsetY);
                if (sp.z < 0f) continue; // カメラ背後は描画しない

                _labelContent.text = label.Text;
                Vector2 size = _labelStyle.CalcSize(_labelContent);
                var rect = new Rect(sp.x - size.x * 0.5f - 4f, Screen.height - sp.y - size.y * 0.5f - 2f,
                    size.x + 8f, size.y + 4f);
                GUI.Box(rect, GUIContent.none);
                GUI.Label(rect, _labelContent, _labelStyle);
            }
        }

        private void RebuildCache()
        {
            _labels.Clear();
            _labelIndexByRoot.Clear();

            NetworkRunner runner = FindActiveRunner();
            if (runner == null)
            {
                _cornerText = "[NetworkDebugHud] No running NetworkRunner (F1: toggle)";
                _cornerLineCount = 1;
                return;
            }

            _sb.Length = 0;
            _sb.Append(runner.IsServer ? "HOST/SERVER" : "CLIENT")
               .Append("  LocalPlayer=").Append(runner.LocalPlayer.ToString()).Append('\n');
            _sb.Append("Tick=").Append(runner.Tick.Raw)
               .Append("  RTT=").Append((runner.GetPlayerRtt(PlayerRef.None) * 1000f).ToString("F0"))
               .Append("ms").Append('\n');

            int players = 0;
            foreach (PlayerRef _ in runner.ActivePlayers) players++;
            _sb.Append("Players=").Append(players);

            _cornerText = _sb.ToString();
            _cornerLineCount = 3;

            _objects.Clear();
            runner.GetAllNetworkObjects(_objects);
            foreach (NetworkObject no in _objects)
            {
                if (no == null) continue;
                if (no.transform.parent != null) continue; // ルートのみ（Fusion が切り離したネスト NObj は独立表示される）

                _sb.Length = 0;
                _sb.Append(no.name)
                   .Append(no.HasInputAuthority ? " [MYINPUT]" : "")
                   .Append(no.HasStateAuthority ? " [AUTH]" : " [proxy]");

                Rigidbody rb = no.GetComponentInChildren<Rigidbody>();
                if (rb != null)
                {
                    _sb.Append(rb.isKinematic ? " kin" : " dyn");
                }

                _labelIndexByRoot[no.transform] = _labels.Count;
                _labels.Add(new WorldLabel { Target = no.transform, Text = _sb.ToString() });
            }

            // グラブ状態は「掴んでいる手」が属するルート NObj のラベルへ追記する
            _hands.Clear();
            runner.GetAllBehaviours(_hands);
            foreach (RagdollHandContact hand in _hands)
            {
                if (hand == null || !hand.IsGrabbing) continue;
                if (!_labelIndexByRoot.TryGetValue(hand.transform.root, out int index)) continue;

                WorldLabel label = _labels[index];
                label.Text = label.Text + " -> " + (hand.GrabbedBodyName ?? "(pending)");
                _labels[index] = label;
            }
        }

        private static NetworkRunner FindActiveRunner()
        {
            foreach (NetworkRunner runner in NetworkRunner.Instances)
            {
                if (runner != null && runner.IsRunning)
                {
                    return runner;
                }
            }
            return null;
        }
    }
}
