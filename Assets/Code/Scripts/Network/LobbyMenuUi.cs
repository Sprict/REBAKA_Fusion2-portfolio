using Fusion;
using MyFolder.Scripts.Settings;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MyFolder.Scripts.Network
{
    /// <summary>
    /// タイトル(Host/Join)画面の uGUI メニュー。
    /// 旧 SessionManager.OnGUI の IMGUI ボタンを置き換える。
    /// IMGUI は EventSystem を通らずゲームパッドで選択できないため、
    /// EventSystem + InputSystemUIInputModule(UI/Navigate/Submit) に乗る uGUI で作る。
    /// シーン編集を要さないよう、Canvas は実行時に生成する（SessionManager が AddComponent する）。
    /// </summary>
    [RequireComponent(typeof(SessionManager))]
    public sealed class LobbyMenuUi : MonoBehaviour
    {
        private const float ButtonWidth = 320f;
        private const float ButtonHeight = 72f;
        private const float ButtonSpacing = 24f;

        private SessionManager _session;
        private GameObject _canvasRoot;
        private Button _hostButton;
        private Button _joinButton;
        private GameObject _messagePanel;
        private Text _messageText;
        private GameObject _lastSelection;

        private void Awake()
        {
            _session = GetComponent<SessionManager>();
            BuildUi();
        }

        private void Update()
        {
            if (_canvasRoot == null || _session == null)
                return;

            bool showing = !_session.IsSessionActive;
            if (_canvasRoot.activeSelf != showing)
                _canvasRoot.SetActive(showing);

            if (!showing)
            {
                _lastSelection = null;
                return;
            }

            RefreshMessage();

            // 設定メニューが開いている間は選択管理を設定メニュー側に譲る
            if (SettingsMenuState.IsGameplayInputBlocked)
                return;

            UiInputMode.Poll();

            // マウス操作中は Select 状態を隠す（ホバー色と同色のため混同する）
            if (!UiInputMode.NavigationSelectionVisible)
            {
                EventSystem eventSystem = EventSystem.current;
                GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
                if (selected != null && selected.transform.IsChildOf(_canvasRoot.transform))
                {
                    _lastSelection = selected;
                    eventSystem.SetSelectedGameObject(null);
                }

                return;
            }

            EnsureGamepadSelection();
        }

        private void RefreshMessage()
        {
            if (_messagePanel == null || _messageText == null)
                return;

            string message = _session.LobbyMessage;
            bool hasMessage = !string.IsNullOrEmpty(message);
            // 座布団（親）ごと出し入れする。Text だけだと親が非表示のまま残る／逆も起きる。
            if (_messagePanel.activeSelf != hasMessage)
                _messagePanel.SetActive(hasMessage);
            if (hasMessage && _messageText.text != message)
                _messageText.text = message;
        }

        /// <summary>
        /// ゲームパッドの Navigate は「現在の選択」起点でしか動かないため、
        /// 選択が失われたら（起動直後・設定メニューを閉じた直後など）Host へ選択を戻す。
        /// </summary>
        private void EnsureGamepadSelection()
        {
            EventSystem eventSystem = EventSystem.current;
            if (eventSystem == null)
                return;

            GameObject selected = eventSystem.currentSelectedGameObject;
            if (selected != null && selected.transform.IsChildOf(_canvasRoot.transform))
            {
                _lastSelection = selected;
                return;
            }

            if (selected == null)
            {
                GameObject fallback = _lastSelection != null && _lastSelection.activeInHierarchy
                    ? _lastSelection
                    : _hostButton != null ? _hostButton.gameObject : null;
                if (fallback != null)
                    eventSystem.SetSelectedGameObject(fallback);
            }
        }

        private void BuildUi()
        {
            _canvasRoot = new GameObject("LobbyCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvasRoot.transform.SetParent(transform, false);

            Canvas canvas = _canvasRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // シーン配置の SettingsCanvas(order 0) より下に描画し、設定メニューを覆わない
            canvas.sortingOrder = -1;

            CanvasScaler scaler = _canvasRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _hostButton = CreateButton("HostButton", "Host", new Vector2(0f, (ButtonHeight + ButtonSpacing) * 0.5f));
            _joinButton = CreateButton("JoinButton", "Join", new Vector2(0f, -(ButtonHeight + ButtonSpacing) * 0.5f));

            _hostButton.onClick.AddListener(() => _session.StartSession(GameMode.Host));
            _joinButton.onClick.AddListener(() => _session.StartSession(GameMode.Client));

            // 2ボタンの縦ループ。Automatic だと将来ボタンが増えたとき挙動が読みにくいので明示する
            SetVerticalNavigation(_hostButton, up: _joinButton, down: _joinButton);
            SetVerticalNavigation(_joinButton, up: _hostButton, down: _hostButton);

            _messageText = CreateMessageText();
        }

        private Button CreateButton(string objectName, string label, Vector2 anchoredPosition)
        {
            var buttonObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(_canvasRoot.transform, false);

            RectTransform rect = (RectTransform)buttonObject.transform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(ButtonWidth, ButtonHeight);
            rect.anchoredPosition = anchoredPosition;

            Image image = buttonObject.GetComponent<Image>();
            image.color = new Color(0.45f, 0.45f, 0.5f, 0.95f);

            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = image;
            // ゲームパッドのフォーカスが見えるよう、非選択時を暗くし選択時に明るくする
            ColorBlock colors = button.colors;
            colors.normalColor = new Color(0.5f, 0.5f, 0.5f, 1f);
            colors.highlightedColor = Color.white;
            colors.selectedColor = Color.white;
            colors.pressedColor = new Color(0.75f, 0.75f, 0.8f, 1f);
            button.colors = colors;

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(buttonObject.transform, false);
            RectTransform textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            Text text = textObject.GetComponent<Text>();
            text.font = LoadBuiltinFont();
            text.fontSize = 32;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;
            text.text = label;

            return button;
        }

        private Text CreateMessageText()
        {
            // uGUI は 1 GameObject に Graphic（Image/Text）を1つまで。
            // 同じGOに両方足すと描画・レイキャストが壊れ、ロビー全体がおかしくなることがある。
            // ボタンと同じく「親=座布団(Image) / 子=文字(Text)」にする。
            var panelObject = new GameObject("LobbyMessage", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(_canvasRoot.transform, false);

            RectTransform panelRect = (RectTransform)panelObject.transform;
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(900f, 96f);
            panelRect.anchoredPosition = new Vector2(0f, -(ButtonHeight + ButtonSpacing) * 0.5f - ButtonHeight - 24f);

            Image cushion = panelObject.GetComponent<Image>();
            // 座布団の色・透明度（アルファで濃さを調整）
            cushion.color = new Color(0f, 0f, 0f, 0.55f);
            cushion.raycastTarget = false;

            var textObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(panelObject.transform, false);
            RectTransform textRect = (RectTransform)textObject.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(16f, 8f);
            textRect.offsetMax = new Vector2(-16f, -8f);

            Text text = textObject.GetComponent<Text>();
            text.font = LoadBuiltinFont();
            text.fontSize = 24;
            text.alignment = TextAnchor.MiddleCenter;
            // 文字色（ここを触れば調整できる）
            text.color = new Color(1f, 0.81f, 0.18f, 1f);
            text.raycastTarget = false;

            _messagePanel = panelObject;
            panelObject.SetActive(false);
            return text;
        }

        private static Font LoadBuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static void SetVerticalNavigation(Button button, Button up, Button down)
        {
            if (button == null)
                return;

            Navigation navigation = button.navigation;
            navigation.mode = Navigation.Mode.Explicit;
            navigation.selectOnUp = up;
            navigation.selectOnDown = down;
            navigation.selectOnLeft = null;
            navigation.selectOnRight = null;
            button.navigation = navigation;
        }
    }
}
