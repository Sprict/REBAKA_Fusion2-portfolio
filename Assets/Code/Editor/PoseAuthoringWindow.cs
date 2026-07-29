// PoseAuthoringWindow.cs
// Play モード中に、走行中のラグドールへ Reach ポーズを適用しながら
// ActionPoseAsset の rest 相対デルタを調整するためのオーサリングツール。
//
// 仕組み:
//   - ツールは ActionPoseAsset を SerializedObject 経由で編集する（Undo / Dirty 自動）。
//   - 「プレビュー」ON の間は RagdollController.SetReachPosePreview() を呼び、
//     走行中の RagdollPhysics が入力/状態を無視して指定側の Reach ポーズを毎tick適用する。
//   - したがってスライダを動かすと次の物理tickで実機（重力下）のポーズが更新され、
//     ゲームプレイ中の見え方をそのまま確認しながら追い込める。
//
// 重要: プレビューは Play モード専用（_ragdollPhysics は実行時に生成されるため）。

#if UNITY_EDITOR
using System.Collections.Generic;
using MyFolder.Scripts.Player;
using MyFolder.Scripts.Player.Posing;
using UnityEditor;
using UnityEngine;

namespace MyFolder.Editor
{
    public sealed class PoseAuthoringWindow : EditorWindow
    {
        private ActionPoseAsset _asset;
        private RagdollController _controller;
        private bool _previewRight = true;
        private bool _previewActive;

        private SerializedObject _serializedAsset;

        private string _saveMessage;
        private MessageType _saveMessageType = MessageType.None;

        private Vector2 _scroll;

        [MenuItem("Tools/REBAKA/Pose Authoring (Reach)")]
        private static void Open()
        {
            GetWindow<PoseAuthoringWindow>("Pose Authoring").Show();
        }

        private void OnEnable()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            StopPreview();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            // Play モードを抜けるときはプレビュー状態を確実に解除する。
            if (change == PlayModeStateChange.ExitingPlayMode)
            {
                _previewActive = false;
                _controller = null;
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("ターゲット", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _asset = (ActionPoseAsset)EditorGUILayout.ObjectField(
                "Action Pose Asset", _asset, typeof(ActionPoseAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                _serializedAsset = _asset != null ? new SerializedObject(_asset) : null;
                if (_previewActive)
                {
                    ApplyPreview(); // 編集対象アセットが変わったらプレビューにも反映
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _controller = (RagdollController)EditorGUILayout.ObjectField(
                    "Player (RagdollController)", _controller, typeof(RagdollController), true);
                if (GUILayout.Button("Find in Scene", GUILayout.Width(110f)))
                {
                    FindControllerInScene();
                }
            }

            EditorGUILayout.Space();
            DrawPreviewSection();

            EditorGUILayout.Space();
            DrawAssetEditor();
        }

        private void DrawPreviewSection()
        {
            EditorGUILayout.LabelField("プレビュー（重力下・実機）", EditorStyles.boldLabel);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "プレビューは Play モード専用です。Play 中にプレイヤーがスポーンしてから使用してください。",
                    MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(!Application.isPlaying || _asset == null))
            {
                EditorGUI.BeginChangeCheck();
                int sideIndex = EditorGUILayout.Popup("Side", _previewRight ? 0 : 1,
                    new[] { "Right", "Left" });
                bool wantPreview = EditorGUILayout.Toggle("Preview Active", _previewActive);
                if (EditorGUI.EndChangeCheck())
                {
                    _previewRight = sideIndex == 0;
                    _previewActive = wantPreview;
                    ApplyPreview();
                }
            }

            if (_previewActive)
            {
                EditorGUILayout.HelpBox(
                    "プレビュー中: スライダを動かすと実機ポーズが即更新されます。",
                    MessageType.None);
                Repaint(); // プレビュー中は連続再描画して操作感を保つ
            }
        }

        private void DrawAssetEditor()
        {
            EditorGUILayout.LabelField("ポーズデータ（rest 相対デルタ）", EditorStyles.boldLabel);

            if (_asset == null)
            {
                EditorGUILayout.HelpBox(
                    "ActionPoseAsset を割り当ててください（Create → REBAKA → Action Pose）。",
                    MessageType.Warning);
                return;
            }

            if (_serializedAsset == null)
            {
                _serializedAsset = new SerializedObject(_asset);
            }

            // --- 値編集(in-place): Update → 描画 → Apply の1サイクルで完結させる ---
            _serializedAsset.Update();
            EditorGUILayout.PropertyField(_serializedAsset.FindProperty("_actionName"),
                new GUIContent("Action Name"));
            DrawDeltaRows(_serializedAsset.FindProperty("_deltas"));
            _serializedAsset.ApplyModifiedProperties();

            // --- 構造変更(Apply 後・各自 RunMutation で隔離して安全に編集) ---
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("＋ 右腕"))
                    RunMutation(d => { AddIfMissing(d, LogicalJoint.UpperRightArm); AddIfMissing(d, LogicalJoint.LowerRightArm); });
                if (GUILayout.Button("＋ 左腕"))
                    RunMutation(d => { AddIfMissing(d, LogicalJoint.UpperLeftArm); AddIfMissing(d, LogicalJoint.LowerLeftArm); });
                if (GUILayout.Button("＋ Element"))
                    RunMutation(AddEmpty);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Mirror All  L⇄R"))
                    RunMutation(MirrorAllInto);
                if (GUILayout.Button("Clear All"))
                    RunMutation(d => d.arraySize = 0);
            }

            EditorGUILayout.HelpBox(
                "Deltas ヘッダの右クリック = リスト全体の Copy/Paste/Mirror。各 Element の右クリック（または「⋮」）= 要素単位の Copy/Paste/Mirror。",
                MessageType.None);

            EditorGUILayout.Space();
            if (GUILayout.Button("Save Asset"))
            {
                SaveAsset();
            }

            if (!string.IsNullOrEmpty(_saveMessage))
            {
                EditorGUILayout.HelpBox(_saveMessage, _saveMessageType);
            }
        }

        // ─── Deltas リストの手動描画（右クリックメニュー対応） ───
        private void DrawDeltaRows(SerializedProperty deltas)
        {
            Rect headerRect = EditorGUILayout.GetControlRect();
            EditorGUI.LabelField(headerRect,
                $"Deltas ({deltas.arraySize})  — 右クリックでメニュー", EditorStyles.boldLabel);
            if (IsContextClick(headerRect))
            {
                ShowListMenu();
                Event.current.Use();
            }

            for (int i = 0; i < deltas.arraySize; i++)
            {
                SerializedProperty el = deltas.GetArrayElementAtIndex(i);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.PropertyField(el.FindPropertyRelative("joint"),
                            new GUIContent($"[{i}] Joint"));
                        if (GUILayout.Button("⋮", GUILayout.Width(24f)))
                        {
                            ShowElementMenu(i);
                        }
                    }
                    EditorGUILayout.PropertyField(el.FindPropertyRelative("eulerDelta"),
                        new GUIContent("Euler Delta"));
                }

                if (IsContextClick(GUILayoutUtility.GetLastRect()))
                {
                    ShowElementMenu(i);
                    Event.current.Use();
                }
            }
        }

        private static bool IsContextClick(Rect rect)
        {
            Event e = Event.current;
            return e.type == EventType.ContextClick && rect.Contains(e.mousePosition);
        }

        // ─── 右クリックメニュー（コールバックは後フレームで発火するため RunMutation で隔離） ───
        private void ShowListMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy All"), false, CopyAll);
            if (TryReadAllClip(out _))
            {
                menu.AddItem(new GUIContent("Paste All (Replace)"), false, () => RunMutation(d => PasteAll(d, true)));
                menu.AddItem(new GUIContent("Paste All (Merge)"), false, () => RunMutation(d => PasteAll(d, false)));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Paste All (Replace)"));
                menu.AddDisabledItem(new GUIContent("Paste All (Merge)"));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Mirror All  L⇄R"), false, () => RunMutation(MirrorAllInto));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Clear All"), false, () => RunMutation(d => d.arraySize = 0));
            menu.ShowAsContext();
        }

        private void ShowElementMenu(int index)
        {
            SerializedProperty deltas = _serializedAsset.FindProperty("_deltas");
            if (index < 0 || index >= deltas.arraySize) return;
            LogicalJoint joint = (LogicalJoint)deltas.GetArrayElementAtIndex(index)
                .FindPropertyRelative("joint").enumValueIndex;

            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy Element"), false, () => CopyElement(index));
            if (TryReadSingleClip(out _, out _))
                menu.AddItem(new GUIContent("Paste Element"), false, () => RunMutation(d => PasteElement(d, index)));
            else
                menu.AddDisabledItem(new GUIContent("Paste Element"));

            if (TryGetMirrorJoint(joint, out LogicalJoint mj))
                menu.AddItem(new GUIContent($"Mirror to {mj}"), false, () => RunMutation(d => MirrorElement(d, index)));
            else
                menu.AddDisabledItem(new GUIContent("Mirror（中心骨は不可）"));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Remove Element"), false, () => RunMutation(d => d.DeleteArrayElementAtIndex(index)));
            menu.ShowAsContext();
        }

        // SerializedObject の Update/Apply を1か所に集約し、入れ子適用を防ぐ。
        private void RunMutation(System.Action<SerializedProperty> mutation)
        {
            if (_serializedAsset == null) return;
            _serializedAsset.Update();
            mutation(_serializedAsset.FindProperty("_deltas"));
            _serializedAsset.ApplyModifiedProperties();
            EditorUtility.SetDirty(_asset);
            Repaint();
        }

        // ─── クリップボード（systemCopyBuffer に JSON 保存。Element単位/リスト全体で接頭辞を分ける） ───
        [System.Serializable]
        private struct DeltaClip
        {
            public int joint;
            public Vector3 euler;
        }

        [System.Serializable]
        private class DeltaClipList
        {
            public List<DeltaClip> items = new List<DeltaClip>();
        }

        private const string SinglePrefix = "REBAKA_POSEDELTA:";
        private const string AllPrefix = "REBAKA_POSEDELTAS:";

        private void CopyElement(int index)
        {
            _serializedAsset.Update();
            SerializedProperty deltas = _serializedAsset.FindProperty("_deltas");
            if (index < 0 || index >= deltas.arraySize) return;
            SerializedProperty el = deltas.GetArrayElementAtIndex(index);
            EditorGUIUtility.systemCopyBuffer = SinglePrefix + JsonUtility.ToJson(new DeltaClip
            {
                joint = el.FindPropertyRelative("joint").enumValueIndex,
                euler = el.FindPropertyRelative("eulerDelta").vector3Value
            });
        }

        private void CopyAll()
        {
            _serializedAsset.Update();
            SerializedProperty deltas = _serializedAsset.FindProperty("_deltas");
            var list = new DeltaClipList();
            for (int i = 0; i < deltas.arraySize; i++)
            {
                SerializedProperty el = deltas.GetArrayElementAtIndex(i);
                list.items.Add(new DeltaClip
                {
                    joint = el.FindPropertyRelative("joint").enumValueIndex,
                    euler = el.FindPropertyRelative("eulerDelta").vector3Value
                });
            }
            EditorGUIUtility.systemCopyBuffer = AllPrefix + JsonUtility.ToJson(list);
        }

        private static bool TryReadSingleClip(out int joint, out Vector3 euler)
        {
            joint = 0;
            euler = Vector3.zero;
            string buf = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(buf) || !buf.StartsWith(SinglePrefix)) return false;
            DeltaClip clip = JsonUtility.FromJson<DeltaClip>(buf.Substring(SinglePrefix.Length));
            joint = clip.joint;
            euler = clip.euler;
            return true;
        }

        private static bool TryReadAllClip(out DeltaClipList list)
        {
            list = null;
            string buf = EditorGUIUtility.systemCopyBuffer;
            if (string.IsNullOrEmpty(buf) || !buf.StartsWith(AllPrefix)) return false;
            list = JsonUtility.FromJson<DeltaClipList>(buf.Substring(AllPrefix.Length));
            return list != null && list.items != null;
        }

        // ─── ミラーリング ───
        // rest 相対デルタの左右ミラーは (x, -y, -z)。前後の曲げ(X)は左右で同じ向き、
        // 横方向成分(Y/Z)を反転する、という近似。joint ローカル軸はリグ依存なので
        // 「効くモデルでは半分の手間、効かなければ Copy/Paste で手動」という前提。
        private static Vector3 MirrorEuler(Vector3 e) => new Vector3(e.x, -e.y, -e.z);

        private static bool TryGetMirrorJoint(LogicalJoint joint, out LogicalJoint mirror)
        {
            switch (joint)
            {
                case LogicalJoint.UpperRightArm: mirror = LogicalJoint.UpperLeftArm; return true;
                case LogicalJoint.UpperLeftArm: mirror = LogicalJoint.UpperRightArm; return true;
                case LogicalJoint.LowerRightArm: mirror = LogicalJoint.LowerLeftArm; return true;
                case LogicalJoint.LowerLeftArm: mirror = LogicalJoint.LowerRightArm; return true;
                case LogicalJoint.UpperRightLeg: mirror = LogicalJoint.UpperLeftLeg; return true;
                case LogicalJoint.UpperLeftLeg: mirror = LogicalJoint.UpperRightLeg; return true;
                case LogicalJoint.LowerRightLeg: mirror = LogicalJoint.LowerLeftLeg; return true;
                case LogicalJoint.LowerLeftLeg: mirror = LogicalJoint.LowerRightLeg; return true;
                case LogicalJoint.RightFoot: mirror = LogicalJoint.LeftFoot; return true;
                case LogicalJoint.LeftFoot: mirror = LogicalJoint.RightFoot; return true;
                case LogicalJoint.RightHand: mirror = LogicalJoint.LeftHand; return true;
                case LogicalJoint.LeftHand: mirror = LogicalJoint.RightHand; return true;
                default: mirror = joint; return false; // Root / Body / Head は中心骨
            }
        }

        // ─── 純粋ミューテータ（Update/Apply は呼ばない。RunMutation が囲む） ───
        private static void AddIfMissing(SerializedProperty deltas, LogicalJoint joint)
        {
            for (int i = 0; i < deltas.arraySize; i++)
            {
                if (deltas.GetArrayElementAtIndex(i).FindPropertyRelative("joint").enumValueIndex == (int)joint)
                    return;
            }
            UpsertDelta(deltas, (int)joint, Vector3.zero);
        }

        private static void AddEmpty(SerializedProperty deltas)
        {
            deltas.arraySize++;
            SerializedProperty el = deltas.GetArrayElementAtIndex(deltas.arraySize - 1);
            el.FindPropertyRelative("eulerDelta").vector3Value = Vector3.zero;
        }

        private static void UpsertDelta(SerializedProperty deltas, int joint, Vector3 euler)
        {
            for (int i = 0; i < deltas.arraySize; i++)
            {
                SerializedProperty el = deltas.GetArrayElementAtIndex(i);
                if (el.FindPropertyRelative("joint").enumValueIndex == joint)
                {
                    el.FindPropertyRelative("eulerDelta").vector3Value = euler;
                    return;
                }
            }
            deltas.arraySize++;
            SerializedProperty added = deltas.GetArrayElementAtIndex(deltas.arraySize - 1);
            added.FindPropertyRelative("joint").enumValueIndex = joint;
            added.FindPropertyRelative("eulerDelta").vector3Value = euler;
        }

        private static void PasteElement(SerializedProperty deltas, int index)
        {
            if (index < 0 || index >= deltas.arraySize) return;
            if (!TryReadSingleClip(out int joint, out Vector3 euler)) return;
            SerializedProperty el = deltas.GetArrayElementAtIndex(index);
            el.FindPropertyRelative("joint").enumValueIndex = joint;
            el.FindPropertyRelative("eulerDelta").vector3Value = euler;
        }

        private static void PasteAll(SerializedProperty deltas, bool replace)
        {
            if (!TryReadAllClip(out DeltaClipList list)) return;
            if (replace) deltas.arraySize = 0;
            foreach (DeltaClip c in list.items)
            {
                UpsertDelta(deltas, c.joint, c.euler);
            }
        }

        private static void MirrorElement(SerializedProperty deltas, int index)
        {
            if (index < 0 || index >= deltas.arraySize) return;
            SerializedProperty el = deltas.GetArrayElementAtIndex(index);
            LogicalJoint joint = (LogicalJoint)el.FindPropertyRelative("joint").enumValueIndex;
            if (!TryGetMirrorJoint(joint, out LogicalJoint mj)) return;
            Vector3 euler = el.FindPropertyRelative("eulerDelta").vector3Value;
            UpsertDelta(deltas, (int)mj, MirrorEuler(euler));
        }

        private static void MirrorAllInto(SerializedProperty deltas)
        {
            // 先に元要素をスナップショットしてから対称側を upsert（追加分を再ミラーしない）。
            var src = new List<(int joint, Vector3 euler)>();
            for (int i = 0; i < deltas.arraySize; i++)
            {
                SerializedProperty el = deltas.GetArrayElementAtIndex(i);
                src.Add((el.FindPropertyRelative("joint").enumValueIndex,
                    el.FindPropertyRelative("eulerDelta").vector3Value));
            }
            foreach ((int joint, Vector3 euler) in src)
            {
                if (TryGetMirrorJoint((LogicalJoint)joint, out LogicalJoint mj))
                    UpsertDelta(deltas, (int)mj, MirrorEuler(euler));
            }
        }

        private void SaveAsset()
        {
            if (_asset == null || _serializedAsset == null)
            {
                SetSaveMessage("保存対象のアセットがありません。", MessageType.Error);
                return;
            }

            string time = System.DateTime.Now.ToString("HH:mm:ss");

            try
            {
                // GUI 上の未確定編集を確定させてから Dirty 判定する。
                _serializedAsset.ApplyModifiedProperties();

                if (!EditorUtility.IsDirty(_asset))
                {
                    SetSaveMessage($"変更なし（既に保存済み） {time}", MessageType.Info);
                    return;
                }

                AssetDatabase.SaveAssetIfDirty(_asset);

                if (EditorUtility.IsDirty(_asset))
                {
                    // 保存後もまだ Dirty ＝ 書き込みが反映されていない。
                    SetSaveMessage($"保存に失敗しました（まだ未保存です） {time}", MessageType.Error);
                    return;
                }

                SetSaveMessage($"保存成功: {_asset.name} ({time})", MessageType.Info);
                ShowNotification(new GUIContent("Saved ✔"));
            }
            catch (System.Exception e)
            {
                SetSaveMessage($"保存失敗: {e.Message} ({time})", MessageType.Error);
                Debug.LogError($"[PoseAuthoring] Save failed: {e}");
            }
        }

        private void SetSaveMessage(string message, MessageType type)
        {
            _saveMessage = message;
            _saveMessageType = type;
            Repaint();
        }

        private void FindControllerInScene()
        {
            RagdollController[] found =
                Object.FindObjectsByType<RagdollController>(FindObjectsSortMode.None);

            // 可能なら StateAuthority を持つホストを優先（ポーズ駆動は権威側で行われるため）。
            foreach (RagdollController candidate in found)
            {
                if (candidate.Object != null && candidate.Object.HasStateAuthority)
                {
                    _controller = candidate;
                    return;
                }
            }

            _controller = found.Length > 0 ? found[0] : null;
        }

        private void ApplyPreview()
        {
            if (_previewActive && (_controller == null))
            {
                FindControllerInScene();
            }

            if (_controller == null)
            {
                _previewActive = false;
                return;
            }

            _controller.SetReachPosePreview(_previewActive, _asset, _previewRight);
        }

        private void StopPreview()
        {
            if (_previewActive && _controller != null)
            {
                _controller.SetReachPosePreview(false, _asset, _previewRight);
            }
            _previewActive = false;
        }
    }
}
#endif
