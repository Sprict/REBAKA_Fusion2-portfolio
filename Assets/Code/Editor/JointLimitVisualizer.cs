// JointLimitVisualizer.cs
// ConfigurableJoint の角度可動域を Scene / Prefab ビューに「腕の実形状に重ねて」描画する Editor ツール。
//
// なぜ要るか:
//   ConfigurableJoint の angular limit は joint の m_Axis(primary)/m_SecondaryAxis を基準にした
//   角度で、これが腕の見た目方向と一致しないため Inspector の数値だけでは直感的に詰められない。
//   このツールは joint の回転を「関節→手先(子ボーン)」方向に適用して可動域を腕に重ねて描く。
//
// 座標の正本（Unity 固定規約）:
//   joint フレーム(world) を primary=jx, secondary 由来=jy, jz=jx×jy で構築し、
//     - angularX = jx 回りの twist（low/high の非対称）
//     - angularY = jy 回りの swing（±limit）
//     - angularZ = jz 回りの swing（±limit）
//   swing(Y,Z) は jy/jz を半角とする楕円錐、twist(X) は jx 回りの弧として描く。
//   ※ 旧版は angularX を swing 楕円に混ぜていたため X が90°ずれて見えた。本版はこれを是正。
//
// 前提: アークの 0° は「現在ポーズ = joint の設定時相対姿勢」と仮定（authored prefab では概ね一致）。
//
// 使い方:
//   Tools/REBAKA/Joint Limit Visualizer をトグル ON。プレハブをダブルクリックして Prefab Mode に入り、
//   ConfigurableJoint を持つボーンを選択すると表示される（選択中はライブ再描画）。

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MyFolder.Editor
{
    [InitializeOnLoad]
    internal static class JointLimitVisualizer
    {
        private const string MenuPath = "Tools/REBAKA/Joint Limit Visualizer";
        private const string PrefKey = "REBAKA_JointLimitVisualizer_Enabled";

        // twist 軸がほぼ腕方向と平行なら、弧ではなく根本の小リングで描く（axial twist は腕先を動かさないため）。
        private const float AxialTwistDotThreshold = 0.9f;
        private const int ConeSegments = 48;

        private static readonly Color ColorLimb = new Color(1f, 1f, 1f, 0.9f);
        private static readonly Color ColorTwist = new Color(1f, 0.5f, 0.15f);   // angularX
        private static readonly Color ColorSwing = new Color(1f, 0.85f, 0.2f);   // angularY/Z 楕円錐
        private static readonly Color ColorSwingY = new Color(0.3f, 1f, 0.4f);
        private static readonly Color ColorSwingZ = new Color(0.4f, 0.6f, 1f);

        private static bool _enabled;

        static JointLimitVisualizer()
        {
            _enabled = EditorPrefs.GetBool(PrefKey, true);
            SceneView.duringSceneGui += OnSceneGUI;
            // Inspector で limit を編集しても Scene が即時再描画されないことがあるため、
            // joint を選択している間は毎エディタ tick で再描画してライブ反映する。
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnEditorUpdate()
        {
            if (!_enabled)
                return;

            foreach (GameObject go in Selection.gameObjects)
            {
                if (go != null && go.GetComponent<ConfigurableJoint>() != null)
                {
                    SceneView.RepaintAll();
                    return;
                }
            }
        }

        [MenuItem(MenuPath)]
        private static void Toggle()
        {
            _enabled = !_enabled;
            EditorPrefs.SetBool(PrefKey, _enabled);
            Menu.SetChecked(MenuPath, _enabled);
            SceneView.RepaintAll();
        }

        [MenuItem(MenuPath, true)]
        private static bool ToggleValidate()
        {
            Menu.SetChecked(MenuPath, _enabled);
            return true;
        }

        private static void OnSceneGUI(SceneView _)
        {
            if (!_enabled)
                return;

            foreach (GameObject go in Selection.gameObjects)
            {
                if (go == null)
                    continue;

                foreach (ConfigurableJoint joint in go.GetComponents<ConfigurableJoint>())
                {
                    DrawJoint(joint);
                }
            }
        }

        private static void DrawJoint(ConfigurableJoint joint)
        {
            if (joint == null)
                return;

            Transform t = joint.transform;
            Vector3 anchor = t.TransformPoint(joint.anchor);

            // joint フレームを world で正規直交化（Unity 規約: X=axis, Y=secondary(直交化), Z=X×Y）。
            Vector3 jx = t.TransformDirection(joint.axis);
            if (jx.sqrMagnitude < 1e-6f)
                return;
            jx.Normalize();

            Vector3 secondary = t.TransformDirection(joint.secondaryAxis);
            if (Vector3.Cross(jx, secondary).sqrMagnitude < 1e-6f)
                secondary = Mathf.Abs(Vector3.Dot(jx, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 jz = Vector3.Cross(jx, secondary).normalized;
            Vector3 jy = Vector3.Cross(jz, jx).normalized;

            // 「腕」= 関節→手先(子ボーンの Rigidbody)。これを各回転の適用対象（0°基準）にする。
            if (!TryGetLimb(t, anchor, out Vector3 limbDir, out float limbLen))
            {
                float s = HandleUtility.GetHandleSize(anchor) * 0.5f;
                Handles.color = ColorTwist; Handles.DrawLine(anchor, anchor + jx * s);
                Handles.color = ColorSwingY; Handles.DrawLine(anchor, anchor + jy * s);
                Handles.color = ColorSwingZ; Handles.DrawLine(anchor, anchor + jz * s);
                return;
            }

            Handles.color = ColorLimb;
            Handles.DrawLine(anchor, anchor + limbDir * limbLen);

            int row = 0;
            DrawTwist(joint, anchor, jx, jy, limbDir, limbLen, ref row);
            DrawSwing(joint, anchor, jx, jy, jz, limbDir, limbLen, ref row);
        }

        // angularX: primary 軸(jx)回りの twist。low/high の非対称。
        // ヒンジ関節(肘)では jx⊥腕なので、この twist が「折り曲げ」として腕先を弧で掃引する。
        private static void DrawTwist(ConfigurableJoint joint, Vector3 anchor, Vector3 jx, Vector3 jy, Vector3 limbDir, float limbLen, ref int row)
        {
            bool axial = Mathf.Abs(Vector3.Dot(jx, limbDir)) > AxialTwistDotThreshold;
            float radius = axial ? limbLen * 0.25f : limbLen;
            // angularX(twist) の 0° 基準は Unity 規約上 secondary 軸。この rig では向きが反対なので -jy を採る
            // （limbDir 基準だと90°、jy だと180°ズレた実測に基づく）。軸反転(-jx)と合わせて符号・向きを一致させる。
            Vector3 zeroDir = jy.sqrMagnitude > 1e-6f ? -jy.normalized : OrthoTo(jx);

            switch (joint.angularXMotion)
            {
                case ConfigurableJointMotion.Locked:
                    DrawDofLabel(anchor, limbDir, limbLen, row++, ColorTwist, "X twist: Locked");
                    break;

                case ConfigurableJointMotion.Free:
                    Handles.color = new Color(ColorTwist.r, ColorTwist.g, ColorTwist.b, 0.12f);
                    Handles.DrawWireDisc(anchor, jx, radius);
                    DrawDofLabel(anchor, limbDir, limbLen, row++, ColorTwist, "X twist: Free (limit 無効)");
                    break;

                case ConfigurableJointMotion.Limited:
                    float low = joint.lowAngularXLimit.limit;
                    float high = joint.highAngularXLimit.limit;
                    // Unity の angularX 正回転は左手系で Quaternion.AngleAxis/Handles と逆向きのため、描画軸を反転する。
                    Vector3 twistAxis = -jx;
                    Vector3 from = Quaternion.AngleAxis(low, twistAxis) * zeroDir;
                    Handles.color = new Color(ColorTwist.r, ColorTwist.g, ColorTwist.b, 0.18f);
                    Handles.DrawSolidArc(anchor, twistAxis, from, high - low, radius);
                    Handles.color = ColorTwist;
                    Handles.DrawWireArc(anchor, twistAxis, from, high - low, radius);
                    DrawDofLabel(anchor, limbDir, limbLen, row++, ColorTwist, $"X twist: {low:0}°..{high:0}° {(axial ? "(軸回り)" : "(折り曲げ)")}");
                    break;
            }
        }

        // angularY/Z: jy/jz 回りの swing(±limit)。2軸 Limited なら楕円錐、1軸なら扇。
        private static void DrawSwing(ConfigurableJoint joint, Vector3 anchor, Vector3 jx, Vector3 jy, Vector3 jz, Vector3 limbDir, float limbLen, ref int row)
        {
            bool yLim = joint.angularYMotion == ConfigurableJointMotion.Limited;
            bool zLim = joint.angularZMotion == ConfigurableJointMotion.Limited;
            float y = joint.angularYLimit.limit;
            float z = joint.angularZLimit.limit;

            if (yLim && zLim)
            {
                // 楕円錐: 腕方向(limbDir)を中心軸とし、それに直交する2軸 u,v を半角 y,z で振ってリムを作る。
                // jy/jz を直接使うと、limbDir がそれらと平行な時に片軸が腕先を動かせず「8の字」に潰れるため。
                Vector3 u = Vector3.Cross(limbDir, jx);
                if (u.sqrMagnitude < 1e-5f)
                    u = Vector3.Cross(limbDir, jy);
                u.Normalize();
                Vector3 v = Vector3.Cross(limbDir, u).normalized;

                var rim = new Vector3[ConeSegments + 1];
                for (int i = 0; i <= ConeSegments; i++)
                {
                    float phi = (i / (float)ConeSegments) * Mathf.PI * 2f;
                    Quaternion q = Quaternion.AngleAxis(y * Mathf.Cos(phi), u) * Quaternion.AngleAxis(z * Mathf.Sin(phi), v);
                    rim[i] = anchor + (q * limbDir) * limbLen;
                }
                Handles.color = ColorSwing;
                Handles.DrawAAPolyLine(3f, rim);
                Handles.color = new Color(ColorSwing.r, ColorSwing.g, ColorSwing.b, 0.5f);
                for (int i = 0; i < ConeSegments; i += ConeSegments / 8)
                    Handles.DrawLine(anchor, rim[i]);
                DrawDofLabel(anchor, limbDir, limbLen, row++, ColorSwing, $"swing cone: Y±{y:0}° / Z±{z:0}°");
                return;
            }

            if (yLim != zLim)
            {
                // 片軸 swing → 扇。
                Vector3 axis = yLim ? jy : jz;
                float lim = yLim ? y : z;
                Color col = yLim ? ColorSwingY : ColorSwingZ;
                Vector3 from = Quaternion.AngleAxis(-lim, axis) * limbDir;
                Handles.color = new Color(col.r, col.g, col.b, 0.16f);
                Handles.DrawSolidArc(anchor, axis, from, lim * 2f, limbLen);
                Handles.color = col;
                Handles.DrawWireArc(anchor, axis, from, lim * 2f, limbLen);
                DrawDofLabel(anchor, limbDir, limbLen, row++, col, $"{(yLim ? "Y" : "Z")} swing: ±{lim:0}°");
            }

            // Free の swing 軸は薄い全周で「制限が効いていない」ことを明示。
            if (joint.angularYMotion == ConfigurableJointMotion.Free)
            {
                Handles.color = new Color(ColorSwingY.r, ColorSwingY.g, ColorSwingY.b, 0.10f);
                Handles.DrawWireDisc(anchor, jy, limbLen);
                DrawDofLabel(anchor, limbDir, limbLen, row++, ColorSwingY, "Y swing: Free (limit 無効)");
            }
            if (joint.angularZMotion == ConfigurableJointMotion.Free)
            {
                Handles.color = new Color(ColorSwingZ.r, ColorSwingZ.g, ColorSwingZ.b, 0.10f);
                Handles.DrawWireDisc(anchor, jz, limbLen);
                DrawDofLabel(anchor, limbDir, limbLen, row++, ColorSwingZ, "Z swing: Free (limit 無効)");
            }
        }

        private static bool TryGetLimb(Transform t, Vector3 anchor, out Vector3 dir, out float len)
        {
            dir = Vector3.zero;
            len = 0f;
            foreach (Rigidbody rb in t.GetComponentsInChildren<Rigidbody>())
            {
                if (rb.transform == t)
                    continue;
                Vector3 d = rb.transform.position - anchor;
                if (d.sqrMagnitude < 1e-6f)
                    continue;
                len = d.magnitude;
                dir = d / len;
                return true;
            }
            return false;
        }

        private static Vector3 OrthoTo(Vector3 axis)
        {
            Vector3 r = Vector3.Cross(axis, Vector3.up);
            if (r.sqrMagnitude < 1e-6f)
                r = Vector3.Cross(axis, Vector3.right);
            return r.normalized;
        }

        private static void DrawDofLabel(Vector3 anchor, Vector3 limbDir, float limbLen, int row, Color color, string text)
        {
            Vector3 up = Vector3.Cross(limbDir, Vector3.right);
            if (up.sqrMagnitude < 1e-6f)
                up = Vector3.Cross(limbDir, Vector3.forward);
            up.Normalize();

            var style = new GUIStyle(EditorStyles.boldLabel);
            style.normal.textColor = color;
            Vector3 pos = anchor + up * limbLen * (0.35f + row * 0.18f) - limbDir * limbLen * 0.15f;
            Handles.Label(pos, text, style);
        }
    }
}
#endif
