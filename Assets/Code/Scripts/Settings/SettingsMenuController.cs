using System;
using System.Collections;
using System.Collections.Generic;
using Fusion;
using MyFolder.Scripts.Network;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace MyFolder.Scripts.Settings
{
    [Serializable]
    public sealed class SettingsTabPage
    {
        [Tooltip("タブボタン（例: ControlsTabButton）")]
        public Button tabButton;

        [Tooltip("このタブで表示するページルート")]
        public GameObject pageRoot;

        [Tooltip("このタブを開いた／決定したときにフォーカスする先頭（例: KeyboardMouseToggle / QuitButton）")]
        public Selectable firstFocus;

        [Tooltip("このタブ表示中、初期設定に戻す／閉じる から上へ進む先")]
        public Selectable footerUpTarget;
    }

    public sealed class SettingsMenuController : MonoBehaviour
    {
        [Header("Editor-authored view")]
        [SerializeField] private GameObject menuRoot;
        [SerializeField] private GameObject quitConfirmation;
        [SerializeField] private ScrollRect controlsScrollRect;
        [SerializeField] private Toggle keyboardMouseToggle;
        [SerializeField] private Toggle gamepadToggle;
        [SerializeField] private Button resetButton;
        [SerializeField] private Button closeButton;
        [SerializeField] private Button leaveSessionButton;
        [SerializeField] private Button quitButton;
        [SerializeField] private Button quitCancelButton;
        [SerializeField] private Button quitConfirmButton;
        [SerializeField] private Text savedMessage;
        [SerializeField] private float savedMessageVisibleSeconds = 2f;

        [Header("Tabs (add one entry per tab)")]
        [SerializeField] private SettingsTabPage[] tabs;

        private static readonly Color SavedMessageSuccessColor = new Color(1f, 0.81f, 0.18f, 1f);
        private static readonly Color SavedMessageFailureColor = new Color(1f, 0.35f, 0.35f, 1f);
        private const string SavedMessageSuccessText = "設定は保存されました";
        private const string SavedMessageFailureText = "設定の保存に失敗しました";

        private readonly List<RebindTarget> _rebindTargets = new List<RebindTarget>();
        private readonly List<Action> _sensitivityUiRefreshers = new List<Action>();
        private REBAKA_Fusion2 _actions;
        private InputActionRebindingExtensions.RebindingOperation _rebindOperation;
        private GameObject _lastSelection;
        private bool _isRebinding;
        private int _currentTabIndex;
        private Coroutine _hideSavedMessageRoutine;

        private void Awake()
        {
            ResolveViewReferences();

            _actions = new REBAKA_Fusion2();
            InputSettingsRuntime.Register(_actions.asset);
            _actions.UI.Enable();
            ConfigureInputSystemUiModule();

            WireButtons();

            // 感度UIの生成失敗で Awake が止まるとメニューが出っぱなし＋Esc無効になるため分離する
            try
            {
                WireSensitivitySliders();
            }
            catch (Exception exception)
            {
                Debug.LogError("[SettingsMenu] WireSensitivitySliders failed: " + exception);
            }

            try
            {
                WireRebindButtons();
            }
            catch (Exception exception)
            {
                Debug.LogError("[SettingsMenu] WireRebindButtons failed: " + exception);
            }

            RefreshView();
            SetOpen(false);
        }

        private void OnEnable()
        {
            if (_actions != null)
            {
                _actions.UI.ToggleSettings.performed += OnToggleSettings;
                _actions.UI.Cancel.performed += OnCancel;
            }
        }

        private void OnDisable()
        {
            if (_actions != null)
            {
                _actions.UI.ToggleSettings.performed -= OnToggleSettings;
                _actions.UI.Cancel.performed -= OnCancel;
            }
        }

        private void OnDestroy()
        {
            _rebindOperation?.Dispose();
            if (_actions != null)
            {
                InputSettingsRuntime.Unregister(_actions.asset);
                _actions.Dispose();
            }
        }

        private void Update()
        {
            if (menuRoot == null || !menuRoot.activeSelf || EventSystem.current == null)
                return;

            UiInputMode.Poll();

            GameObject selected = EventSystem.current.currentSelectedGameObject;

            // マウス操作中は Select 状態を隠す（ホバー色と同色のため混同する）。
            // Navigate系入力が来たら _lastSelection から復元する。
            if (!UiInputMode.NavigationSelectionVisible)
            {
                if (selected != null)
                {
                    _lastSelection = selected;
                    // InputField 編集中に選択を外すと編集フォーカスまで切れるので維持する
                    if (!IsEditingInputField(selected))
                        EventSystem.current.SetSelectedGameObject(null);
                }

                return;
            }

            if (selected != null)
            {
                _lastSelection = selected;
                EnsureSelectedVisible(selected);
            }
            else
            {
                Select(_lastSelection != null && _lastSelection.activeInHierarchy
                    ? _lastSelection
                    : CurrentTabFirstFocusObject());
            }
        }

        private static bool IsEditingInputField(GameObject selected)
        {
            InputField field = selected.GetComponent<InputField>();
            return field != null && field.isFocused;
        }

        private void ResolveViewReferences()
        {
            // UnityEngine.Object には ??= を使わない（破棄済み参照の偽 null をすり抜ける）
            if (menuRoot == null) menuRoot = Find("SettingsCanvas");
            if (quitConfirmation == null) quitConfirmation = Find("QuitConfirmation");
            if (controlsScrollRect == null) controlsScrollRect = FindComponent<ScrollRect>("ControlsScrollView");
            if (keyboardMouseToggle == null) keyboardMouseToggle = FindComponent<Toggle>("KeyboardMouseToggle");
            if (gamepadToggle == null) gamepadToggle = FindComponent<Toggle>("GamepadToggle");
            if (resetButton == null) resetButton = FindComponent<Button>("ResetButton");
            if (closeButton == null) closeButton = FindComponent<Button>("CloseButton");
            if (leaveSessionButton == null) leaveSessionButton = FindComponent<Button>("LeaveSessionButton");
            if (quitButton == null) quitButton = FindComponent<Button>("QuitButton");
            if (quitCancelButton == null) quitCancelButton = FindComponent<Button>("QuitCancelButton");
            if (quitConfirmButton == null) quitConfirmButton = FindComponent<Button>("QuitConfirmButton");
            if (savedMessage == null)
            {
                GameObject messageObject = Find("SavedMessage");
                if (messageObject != null)
                    savedMessage = messageObject.GetComponent<Text>();
            }

            HideSaveFeedback();
        }

        private void ConfigureInputSystemUiModule()
        {
            EventSystem eventSystem = FindFirstObjectByType<EventSystem>();
            if (eventSystem == null)
            {
                Debug.LogError("[SettingsMenu] EventSystem is missing from the scene.");
                return;
            }

            InputSystemUIInputModule module = eventSystem.GetComponent<InputSystemUIInputModule>();
            if (module == null)
            {
                Debug.LogError("[SettingsMenu] InputSystemUIInputModule is missing from EventSystem.");
                return;
            }

            module.actionsAsset = _actions.asset;
        }

        private void WireButtons()
        {
            WireTabs();
            resetButton?.onClick.AddListener(ResetSettings);
            closeButton?.onClick.AddListener(() => SetOpen(false));
            leaveSessionButton?.onClick.AddListener(LeaveSession);
            quitButton?.onClick.AddListener(ShowQuitConfirmation);
            quitCancelButton?.onClick.AddListener(HideQuitConfirmation);
            quitConfirmButton?.onClick.AddListener(QuitGame);

            if (keyboardMouseToggle != null)
                keyboardMouseToggle.onValueChanged.AddListener(value => ChangeDevice(GameplayDeviceGroup.KeyboardMouse, value));
            if (gamepadToggle != null)
                gamepadToggle.onValueChanged.AddListener(value => ChangeDevice(GameplayDeviceGroup.Gamepad, value));
        }

        private void WireTabs()
        {
            if (tabs == null || tabs.Length == 0)
            {
                Debug.LogError("[SettingsMenu] tabs が空です。Inspector で SettingsTabPage を1つ以上登録してください。");
                return;
            }

            for (int i = 0; i < tabs.Length; i++)
            {
                SettingsTabPage tab = tabs[i];
                if (tab == null || tab.tabButton == null)
                    continue;

                int index = i;
                tab.tabButton.onClick.AddListener(() => ShowTab(index, focusPageContent: true));
                WireTabSelect(tab.tabButton, index);
            }
        }

        private void WireTabSelect(Button tabButton, int tabIndex)
        {
            if (tabButton == null)
                return;

            EventTrigger trigger = tabButton.gameObject.GetComponent<EventTrigger>();
            if (trigger == null)
                trigger = tabButton.gameObject.AddComponent<EventTrigger>();

            var entry = new EventTrigger.Entry { eventID = EventTriggerType.Select };
            entry.callback.AddListener(_ => ShowTab(tabIndex, focusPageContent: false));
            trigger.triggers.Add(entry);
        }

        private void WireSensitivitySliders()
        {
            WireSlider("MouseLookXSlider", () => InputSettingsRuntime.Current.MouseLookX, value => InputSettingsRuntime.Current.MouseLookX = value);
            WireSlider("MouseLookYSlider", () => InputSettingsRuntime.Current.MouseLookY, value => InputSettingsRuntime.Current.MouseLookY = value);
            WireSlider("GamepadLookXSlider", () => InputSettingsRuntime.Current.GamepadLookX, value => InputSettingsRuntime.Current.GamepadLookX = value);
            WireSlider("GamepadLookYSlider", () => InputSettingsRuntime.Current.GamepadLookY, value => InputSettingsRuntime.Current.GamepadLookY = value);
            WireSlider("GamepadMoveXSlider", () => InputSettingsRuntime.Current.GamepadMoveX, value => InputSettingsRuntime.Current.GamepadMoveX = value);
            WireSlider("GamepadMoveYSlider", () => InputSettingsRuntime.Current.GamepadMoveY, value => InputSettingsRuntime.Current.GamepadMoveY = value);
        }

        private void WireSlider(string name, Func<float> read, Action<float> write)
        {
            Slider slider = FindComponent<Slider>(name);
            if (slider == null)
                return;

            // スライダーは常用域を粗く（0.1刻み）、数値入力は全域を細かく（0.01刻み）扱う。
            // スライダー範囲外の値は数値入力でのみ設定でき、スライダーのつまみは端に張り付く。
            //
            // Unity UI Slider はゲームパッド/キーの左右で
            //   wholeNumbers=true  → 1
            //   wholeNumbers=false → (max-min)*0.1
            // だけ動かす。実値 0.1〜10 をそのまま載せると後者が約 0.99≒1.0 刻みになる。
            // そのためスライダー内部は「0.1 単位の整数」（1〜100）で持ち、Commit 時に実値へ戻す。
            const float sliderUiStep = 0.1f;
            slider.wholeNumbers = true;
            slider.minValue = ToSensitivitySliderUnits(InputSettingsData.SliderMinimumSensitivity, sliderUiStep);
            slider.maxValue = ToSensitivitySliderUnits(InputSettingsData.SliderMaximumSensitivity, sliderUiStep);

            GameObject valueObject = Find(name + "Value");
            Text legacyLabel = valueObject != null ? valueObject.GetComponent<Text>() : null;
            InputField inputField = null;
            Text inputText = null;

            // InputField の text は同一GOの Textだと壊れるため、子Textへ移す。
            // 失敗しても感度スライダー自体は動かす（Awake全体を止めない）。
            if (valueObject != null)
            {
                try
                {
                    inputField = EnsureSensitivityInputField(valueObject, legacyLabel, out inputText);
                }
                catch (Exception exception)
                {
                    Debug.LogError("[SettingsMenu] Failed to create sensitivity InputField for " + name + ": " + exception.Message);
                    inputField = null;
                    inputText = null;
                }
            }

            void ApplyUi(float value)
            {
                float sliderClamped = Mathf.Clamp(
                    value,
                    InputSettingsData.SliderMinimumSensitivity,
                    InputSettingsData.SliderMaximumSensitivity);
                slider.SetValueWithoutNotify(ToSensitivitySliderUnits(sliderClamped, sliderUiStep));
                string text = FormatSensitivity(value);
                if (inputField != null)
                    inputField.SetTextWithoutNotify(text);
                else if (inputText != null)
                    inputText.text = text;
                else if (legacyLabel != null)
                    legacyLabel.text = text;
            }

            void Commit(float value)
            {
                value = Mathf.Clamp(value, InputSettingsData.MinimumSensitivity, InputSettingsData.MaximumSensitivity);
                write(value);
                TrySaveSettings();
                ApplyUi(value);
            }

            ApplyUi(read());
            _sensitivityUiRefreshers.Add(() => ApplyUi(read()));

            slider.onValueChanged.AddListener(units =>
                Commit(FromSensitivitySliderUnits(units, sliderUiStep)));
            if (inputField != null)
            {
                inputField.onEndEdit.AddListener(text =>
                {
                    if (!TryParseSensitivity(text, out float value))
                    {
                        ApplyUi(read());
                        return;
                    }

                    Commit(RoundToStep(value, 0.01f));
                });
            }
        }

        /// <summary>感度実値 → Slider 内部単位（0.1 刻みの整数）。</summary>
        internal static float ToSensitivitySliderUnits(float sensitivity, float step = 0.1f)
        {
            return Mathf.Round(sensitivity / step);
        }

        /// <summary>Slider 内部単位 → 感度実値。</summary>
        internal static float FromSensitivitySliderUnits(float units, float step = 0.1f)
        {
            return units * step;
        }

        private static InputField EnsureSensitivityInputField(GameObject host, Text legacyLabel, out Text inputText)
        {
            InputField existing = host.GetComponent<InputField>();
            if (existing != null && existing.textComponent != null)
            {
                inputText = existing.textComponent;
                return existing;
            }

            // Text と Image はどちらも Graphic なので同一GOに共存できない。
            // 先に子へ Textを複製し、ホストの Text を DestroyImmediate してから Image を付ける。
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            int fontSize = 19;
            FontStyle fontStyle = FontStyle.Normal;
            Color color = Color.white;
            TextAnchor alignment = TextAnchor.MiddleLeft;
            HorizontalWrapMode horizontalOverflow = HorizontalWrapMode.Wrap;
            VerticalWrapMode verticalOverflow = VerticalWrapMode.Truncate;
            string initialText = "1.00x";

            if (legacyLabel != null)
            {
                font = legacyLabel.font;
                fontSize = legacyLabel.fontSize;
                fontStyle = legacyLabel.fontStyle;
                color = legacyLabel.color;
                alignment = legacyLabel.alignment;
                horizontalOverflow = legacyLabel.horizontalOverflow;
                verticalOverflow = legacyLabel.verticalOverflow;
                initialText = legacyLabel.text;
                DestroyImmediate(legacyLabel);
            }

            Image graphic = host.GetComponent<Image>();
            if (graphic == null)
            {
                graphic = host.AddComponent<Image>();
                graphic.color = new Color(1f, 1f, 1f, 0.12f);
            }

            Transform textTransform = host.transform.Find("InputText");
            GameObject textObject = textTransform != null ? textTransform.gameObject : null;
            if (textObject == null)
            {
                textObject = new GameObject("InputText", typeof(RectTransform));
                textObject.transform.SetParent(host.transform, false);
                RectTransform textRect = textObject.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;
            }

            inputText = textObject.GetComponent<Text>();
            if (inputText == null)
                inputText = textObject.AddComponent<Text>();

            inputText.font = font;
            inputText.fontSize = fontSize;
            inputText.fontStyle = fontStyle;
            inputText.color = color;
            inputText.alignment = alignment;
            inputText.horizontalOverflow = horizontalOverflow;
            inputText.verticalOverflow = verticalOverflow;
            inputText.raycastTarget = false;
            inputText.text = initialText;

            InputField field = existing != null ? existing : host.AddComponent<InputField>();
            field.targetGraphic = graphic;
            field.textComponent = inputText;
            field.contentType = InputField.ContentType.Standard;
            field.lineType = InputField.LineType.SingleLine;
            field.interactable = true;
            field.text = initialText;
            // ゲームパッドの Select 移動対象から外す（マウス／クリックでの数値編集は可能）
            field.navigation = new Navigation { mode = Navigation.Mode.None };
            return field;
        }

        private static float RoundToStep(float value, float step)
        {
            return Mathf.Round(value / step) * step;
        }

        private static string FormatSensitivity(float value)
        {
            return value.ToString("0.00") + "x";
        }

        private static bool TryParseSensitivity(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            if (trimmed.EndsWith("x", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(0, trimmed.Length - 1).Trim();

            return float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        private void WireRebindButtons()
        {
            AddRebindTarget("Move", "MoveUp", "up", false);
            AddRebindTarget("Move", "MoveUp", null, true);
            AddRebindTarget("Move", "MoveDown", "down", false);
            AddRebindTarget("Move", "MoveDown", null, true);
            AddRebindTarget("Move", "MoveLeft", "left", false);
            AddRebindTarget("Move", "MoveLeft", null, true);
            AddRebindTarget("Move", "MoveRight", "right", false);
            AddRebindTarget("Move", "MoveRight", null, true);

            string[] actions = { "Jump", "Crouch", "Dash", "LeftGrab", "RightGrab", "LeftPunch", "RightPunch" };
            foreach (string actionName in actions)
            {
                AddRebindTarget(actionName, actionName, null, false);
                AddRebindTarget(actionName, actionName, null, true);
            }
        }

        private void AddRebindTarget(string actionName, string buttonName, string compositePart, bool gamepad)
        {
            Button button = FindComponent<Button>("Bind_" + buttonName + "_" + (gamepad ? "Gamepad" : "KBM"));
            InputAction action = _actions.asset.FindAction("Player/" + actionName, false);
            if (button == null || action == null)
                return;

            string group = gamepad ? "Gamepad" : "Keyboard&Mouse";
            int bindingIndex = FindBinding(action, group, compositePart);
            var target = new RebindTarget(action, bindingIndex, button);
            button.interactable = bindingIndex >= 0;
            button.onClick.AddListener(() => StartRebind(target));
            _rebindTargets.Add(target);
        }

        private static int FindBinding(InputAction action, string group, string compositePart)
        {
            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                bool partMatches = string.IsNullOrEmpty(compositePart)
                    ? !binding.isComposite && !binding.isPartOfComposite
                    : binding.isPartOfComposite && string.Equals(binding.name, compositePart, StringComparison.OrdinalIgnoreCase);
                if (partMatches && !string.IsNullOrEmpty(binding.groups) && binding.groups.Contains(group))
                    return i;
            }

            return -1;
        }

        private void StartRebind(RebindTarget target)
        {
            if (_isRebinding || target.BindingIndex < 0)
                return;

            _isRebinding = true;
            target.Label.text = "入力待機中…";
            bool wasEnabled = target.Action.enabled;
            target.Action.Disable();

            var operation = target.Action.PerformInteractiveRebinding(target.BindingIndex)
                .WithCancelingThrough("<Keyboard>/escape")
                .WithControlsExcluding("<Gamepad>/start")
                .WithControlsExcluding("<Gamepad>/select")
                .OnCancel(op => FinishRebind(target, wasEnabled, false, op))
                .OnComplete(op => FinishRebind(target, wasEnabled, true, op));
            _rebindOperation = operation.Start();
        }

        private void FinishRebind(RebindTarget target, bool wasEnabled, bool save, InputActionRebindingExtensions.RebindingOperation operation)
        {
            operation.Dispose();
            _rebindOperation = null;
            _isRebinding = false;
            if (wasEnabled)
                target.Action.Enable();
            if (save)
                TrySaveBindingOverrides();
            RefreshRebindLabels();
            Select(target.Button.gameObject);
        }

        private void OnToggleSettings(InputAction.CallbackContext context)
        {
            if (_isRebinding)
                return;

            // Esc は ToggleSettings の専有。確認ダイアログ中はメニュー全体を閉じず確認だけ戻す
            if (quitConfirmation != null && quitConfirmation.activeSelf)
            {
                HideQuitConfirmation();
                return;
            }

            SetOpen(menuRoot == null || !menuRoot.activeSelf);
        }

        private void OnCancel(InputAction.CallbackContext context)
        {
            if (_isRebinding)
            {
                _rebindOperation?.Cancel();
                return;
            }

            // Escape は ToggleSettings と二重発火するため、Cancel 側では無視する（閉じる→すぐ開くを防ぐ）
            if (IsKeyboardEscape(context))
                return;

            if (quitConfirmation != null && quitConfirmation.activeSelf)
                HideQuitConfirmation();
            else if (menuRoot != null && menuRoot.activeSelf)
                SetOpen(false);
        }

        private static bool IsKeyboardEscape(InputAction.CallbackContext context)
        {
            InputControl control = context.control;
            if (control == null || !(control.device is Keyboard))
                return false;

            return control.name == "escape"
                   || control.path.EndsWith("/escape", StringComparison.OrdinalIgnoreCase);
        }

        private void SetOpen(bool open)
        {
            if (menuRoot == null)
            {
                Debug.LogError("[SettingsMenu] menuRoot is null");
                return;
            }

            menuRoot.SetActive(open);
            if (open)
            {
                HideSaveFeedback();
                SettingsMenuState.Open();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
                ShowTab(0, focusPageContent: true);
            }
            else
            {
                HideSaveFeedback();
                SettingsMenuState.Close();
                if (quitConfirmation != null)
                    quitConfirmation.SetActive(false);
            }
        }

        private void ShowTab(int tabIndex, bool focusPageContent = true)
        {
            if (tabs == null || tabs.Length == 0)
                return;

            tabIndex = Mathf.Clamp(tabIndex, 0, tabs.Length - 1);
            _currentTabIndex = tabIndex;

            for (int i = 0; i < tabs.Length; i++)
            {
                SettingsTabPage tab = tabs[i];
                if (tab?.pageRoot != null)
                    tab.pageRoot.SetActive(i == tabIndex);
            }

            RefreshView();
            ApplyFooterUpNavigation();

            if (focusPageContent)
                Select(CurrentTabFirstFocusObject());
        }

        /// <summary>
        /// Explicit の Up は1つしか持てないため、表示中タブに応じて
        /// Inspector で指定した行き先へ差し替える。Left/Right/Down は Prefab の設定を維持する。
        /// </summary>
        private void ApplyFooterUpNavigation()
        {
            Selectable upTarget = ResolveFooterUpTarget(GetCurrentTab());
            SetSelectOnUp(resetButton, upTarget);
            SetSelectOnUp(closeButton, upTarget);
        }

        private SettingsTabPage GetCurrentTab()
        {
            if (tabs == null || tabs.Length == 0)
                return null;
            int index = Mathf.Clamp(_currentTabIndex, 0, tabs.Length - 1);
            return tabs[index];
        }

        private Selectable ResolveFooterUpTarget(SettingsTabPage tab)
        {
            if (tab == null)
                return null;

            if (leaveSessionButton != null
                && leaveSessionButton.gameObject.activeInHierarchy
                && leaveSessionButton.IsInteractable()
                && tab.pageRoot != null
                && leaveSessionButton.transform.IsChildOf(tab.pageRoot.transform))
                return leaveSessionButton;

            return tab.footerUpTarget;
        }

        private GameObject CurrentTabFirstFocusObject()
        {
            SettingsTabPage tab = GetCurrentTab();
            if (tab == null)
                return null;

            Selectable focus = ResolveFirstFocus(tab);
            return focus != null ? focus.gameObject : tab.tabButton != null ? tab.tabButton.gameObject : null;
        }

        private Selectable ResolveFirstFocus(SettingsTabPage tab)
        {
            if (tab == null)
                return null;

            if (tab.firstFocus != null
                && tab.firstFocus.IsActive()
                && tab.firstFocus.IsInteractable())
                return tab.firstFocus;

            if (leaveSessionButton != null
                && leaveSessionButton.gameObject.activeInHierarchy
                && leaveSessionButton.IsInteractable()
                && tab.pageRoot != null
                && leaveSessionButton.transform.IsChildOf(tab.pageRoot.transform))
                return leaveSessionButton;

            return tab.firstFocus;
        }

        private static void SetSelectOnUp(Selectable selectable, Selectable upTarget)
        {
            if (selectable == null)
                return;

            Navigation navigation = selectable.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnUp = upTarget;
            selectable.navigation = navigation;
        }

        private void ChangeDevice(GameplayDeviceGroup group, bool enabled)
        {
            // 拒否されてもトグル表示を実際の設定値へ戻すため、成否に関わらず再描画する
            if (InputSettingsRuntime.TrySetDeviceEnabled(group, enabled))
                ShowSaveFeedback(success: true);
            RefreshDeviceToggles();
        }

        private void RefreshView()
        {
            RefreshDeviceToggles();
            RefreshRebindLabels();
            SessionManager session = FindFirstObjectByType<SessionManager>();
            if (leaveSessionButton != null)
                leaveSessionButton.gameObject.SetActive(session != null && session.IsSessionActive);

            ApplyFooterUpNavigation();
        }

        private void RefreshDeviceToggles()
        {
            InputSettingsData data = InputSettingsRuntime.Current;
            keyboardMouseToggle?.SetIsOnWithoutNotify(data.KeyboardMouseEnabled);
            gamepadToggle?.SetIsOnWithoutNotify(data.GamepadEnabled);
            if (keyboardMouseToggle != null)
                keyboardMouseToggle.interactable = data.GamepadEnabled;
            if (gamepadToggle != null)
                gamepadToggle.interactable = data.KeyboardMouseEnabled;
        }

        private void RefreshRebindLabels()
        {
            foreach (RebindTarget target in _rebindTargets)
            {
                target.Label.text = target.BindingIndex < 0
                    ? "未設定"
                    : target.Action.GetBindingDisplayString(target.BindingIndex);
            }
        }

        private void ResetSettings()
        {
            try
            {
                InputSettingsRuntime.ResetToDefaults();
                foreach (Action refresh in _sensitivityUiRefreshers)
                    refresh();
                RefreshView();
                ShowSaveFeedback(success: true);
            }
            catch (Exception exception)
            {
                Debug.LogError("[SettingsMenu] ResetToDefaults failed: " + exception);
                ShowSaveFeedback(success: false);
            }
        }

        private bool TrySaveSettings()
        {
            try
            {
                InputSettingsRuntime.SaveSettings(InputSettingsRuntime.Current);
                ShowSaveFeedback(success: true);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[SettingsMenu] SaveSettings failed: " + exception);
                ShowSaveFeedback(success: false);
                return false;
            }
        }

        private bool TrySaveBindingOverrides()
        {
            try
            {
                InputSettingsRuntime.SaveBindingOverrides(_actions.asset);
                ShowSaveFeedback(success: true);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError("[SettingsMenu] SaveBindingOverrides failed: " + exception);
                ShowSaveFeedback(success: false);
                return false;
            }
        }

        /// <summary>
        /// 保存は PlayerPrefs 同期で一瞬で終わるため、「変更中は非表示」にすると
        /// スライダー操作などで表示が高速点滅する。代わりに最短表示時間を設け、
        /// 連続保存時はタイマーを延長して「直近の保存完了」を示す。
        /// </summary>
        private void ShowSaveFeedback(bool success)
        {
            if (savedMessage == null)
                return;

            savedMessage.text = success ? SavedMessageSuccessText : SavedMessageFailureText;
            savedMessage.color = success ? SavedMessageSuccessColor : SavedMessageFailureColor;
            savedMessage.gameObject.SetActive(true);

            if (_hideSavedMessageRoutine != null)
                StopCoroutine(_hideSavedMessageRoutine);
            _hideSavedMessageRoutine = StartCoroutine(HideSaveFeedbackAfterDelay());
        }

        private void HideSaveFeedback()
        {
            if (_hideSavedMessageRoutine != null)
            {
                StopCoroutine(_hideSavedMessageRoutine);
                _hideSavedMessageRoutine = null;
            }

            if (savedMessage != null)
                savedMessage.gameObject.SetActive(false);
        }

        private IEnumerator HideSaveFeedbackAfterDelay()
        {
            float seconds = Mathf.Max(0.5f, savedMessageVisibleSeconds);
            yield return new WaitForSecondsRealtime(seconds);
            _hideSavedMessageRoutine = null;
            if (savedMessage != null)
                savedMessage.gameObject.SetActive(false);
        }

        private void LeaveSession()
        {
            FindFirstObjectByType<SessionManager>()?.LeaveSession();
            SetOpen(false);
        }

        private void ShowQuitConfirmation()
        {
            _lastSelection = EventSystem.current?.currentSelectedGameObject;
            quitConfirmation?.SetActive(true);
            Select(quitCancelButton?.gameObject);
        }

        private void HideQuitConfirmation()
        {
            quitConfirmation?.SetActive(false);
            Select(_lastSelection ?? quitButton?.gameObject);
        }

        private async void QuitGame()
        {
            foreach (NetworkRunner runner in new List<NetworkRunner>(NetworkRunner.Instances))
            {
                if (runner != null && runner.IsRunning)
                    await runner.Shutdown();
            }
            Application.Quit();
        }

        private void EnsureSelectedVisible(GameObject selected)
        {
            if (controlsScrollRect == null || !selected.transform.IsChildOf(controlsScrollRect.content))
                return;

            Canvas.ForceUpdateCanvases();
            RectTransform selectedRect = selected.GetComponent<RectTransform>();
            RectTransform viewport = controlsScrollRect.viewport;
            if (selectedRect == null || viewport == null)
                return;

            Vector3[] corners = new Vector3[4];
            selectedRect.GetWorldCorners(corners);
            Vector3 bottom = viewport.InverseTransformPoint(corners[0]);
            Vector3 top = viewport.InverseTransformPoint(corners[1]);
            Rect rect = viewport.rect;
            Vector2 position = controlsScrollRect.content.anchoredPosition;
            if (top.y > rect.yMax)
                position.y += top.y - rect.yMax;
            else if (bottom.y < rect.yMin)
                position.y -= rect.yMin - bottom.y;
            position.y = Mathf.Max(0f, position.y);
            controlsScrollRect.content.anchoredPosition = position;
        }

        private void Select(GameObject target)
        {
            if (target != null && target.activeInHierarchy)
                EventSystem.current?.SetSelectedGameObject(target);
        }

        private GameObject Find(string objectName)
        {
            foreach (Transform child in GetComponentsInChildren<Transform>(true))
                if (child.name == objectName)
                    return child.gameObject;
            return null;
        }

        private T FindComponent<T>(string objectName) where T : Component
        {
            GameObject found = Find(objectName);
            return found != null ? found.GetComponent<T>() : null;
        }

        private sealed class RebindTarget
        {
            public readonly InputAction Action;
            public readonly int BindingIndex;
            public readonly Button Button;
            public readonly Text Label;

            public RebindTarget(InputAction action, int bindingIndex, Button button)
            {
                Action = action;
                BindingIndex = bindingIndex;
                Button = button;
                Label = button.GetComponentInChildren<Text>();
            }
        }
    }
}
