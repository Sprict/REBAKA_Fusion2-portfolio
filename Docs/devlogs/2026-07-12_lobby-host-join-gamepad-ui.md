# 2026-07-12 Host/Join ロビーUIの中央配置＋ゲームパッド対応（IMGUI → uGUI 移行）

## 問題

タイトル画面の Host/Join ボタンは `SessionManager.OnGUI()` の IMGUI（`GUI.Button`）で
画面左上に仮置きされていた。IMGUI には2つの問題がある:

1. **配置**: 固定ピクセル座標（`Rect(0, 0, 200, 40)`）で左上に張り付き、解像度にも追従しない。
2. **ゲームパッドで選択できない**: IMGUI は `EventSystem` を経由しないため、
   Input System の UI アクション（Navigate / Submit）が一切効かない。
   設定メニュー（uGUI）はゲームパッドで操作できるのに、その手前のロビーで詰む。

## なぜこのアプローチか（不採用案も）

採用: **実行時生成の uGUI Canvas（`LobbyMenuUi`）に置き換え**

- 設定メニューが既に `EventSystem` + `InputSystemUIInputModule` + UI アクションマップ
  （Navigate=左/右スティック・D-Pad、Submit=`*/{Submit}`=ゲームパッド buttonSouth）を配線済み。
  uGUI の `Button` + `Navigation` に乗せるだけで、ゲームパッド対応が「タダで」手に入る。
- Canvas をコードで生成するのは、**シーンファイル（Test_Playground.unity）を編集しないため**。
  シーンYAMLの差分はレビュー困難で、既に別作業の未コミット変更もシーンに載っていた。
  `SessionManager.Awake()` が `AddComponent<LobbyMenuUi>()` するだけなので、
  シーン再読み込み（ロビー復帰）でも自動で再構築される。

不採用案:

- **IMGUI のまま中央配置＋手動ゲームパッド処理**: `Rect` を中央に動かすのは簡単だが、
  フォーカス管理（どのボタンが選択中か・スティックで移動・決定ボタン）を全部自前実装する
  ことになる。EventSystem がやってくれる仕事の再発明で、将来ボタンが増えるほど負債になる。
- **シーンに Canvas をオーサリング**: 見た目の調整は Inspector でできて楽だが、
  シーン差分が重く、ロビーUIは暫定（将来 UI Toolkit 等へ移行予定）なので投資しない。

## 仕組み

- `LobbyMenuUi`（`Assets/Code/Scripts/Network/LobbyMenuUi.cs`）
  - `SessionManager.Awake()` から `AddComponent` され、`BuildUi()` で
    Canvas（ScreenSpaceOverlay, `CanvasScaler` 1920x1080 基準）＋
    Host / Join ボタン（画面中央、縦並び）＋切断メッセージ用 Text を生成。
  - 表示条件: `!SessionManager.IsSessionActive`。セッション中は Canvas ごと非表示。
  - `sortingOrder = -1` で、シーン配置の SettingsCanvas より下に描画（設定メニューを覆わない）。
  - 切断メッセージ（`LobbyMessage`）は **親 Image（座布団）+ 子 Text**。
    uGUI は 1 GO に Graphic（Image/Text）を1つまでなので同GO併載は不可。
    色は `CreateMessageText()` 内の `cushion.color` / `text.color` で調整。
  - ボタンの `Navigation` は Explicit で上下ループ（Host ⇔ Join）。
  - **フォーカス保証**: ゲームパッドの Navigate は「現在の選択」起点でしか動かないため、
    `Update()` で選択が失われていたら Host（または直前の選択）へ選択を戻す。
    設定メニューが開いている間（`SettingsMenuState.IsGameplayInputBlocked`）は
    設定メニュー側の選択管理に譲って何もしない。
  - フォーカス可視化: `ColorBlock` の normal を暗く、selected/highlighted を白にして
    ゲームパッドのカーソル位置が見えるようにした。
- `SessionManager`
  - `OnGUI()` を削除し、`LobbyMessage`（切断通知の静的メッセージ）を公開プロパティ化。
    表示責務は `LobbyMenuUi` へ移動（責務: SessionManager=セッション状態、LobbyMenuUi=表示）。

## 検証

- `uloop compile`: エラー0・警告0。
- Play モード実機確認: `Runner/LobbyCanvas/HostButton` が生成・active、
  `EventSystem.currentSelectedGameObject == HostButton`（起動直後からゲームパッドで操作可能）、
  エラーログ0件。
- 残り: 実ゲームパッドでの Navigate/Submit 操作と、Host/Join 実クリックでの
  セッション開始は人手で要確認（2クライアント検証手順に含める）。

## 自力再実装チェックリスト

- [ ] IMGUI（OnGUI）が EventSystem を通らない＝ゲームパッドUI操作不可、を説明できる
- [ ] uGUI のゲームパッド操作に必要な3点セット（EventSystem / InputSystemUIInputModule /
      UIアクションマップの Navigate・Submit バインディング）を挙げられる
- [ ] 「選択が null だと Navigate が効かない」問題と、Update での選択復元パターンを説明できる
- [ ] 実行時生成 Canvas とシーンオーサリングのトレードオフ（差分レビュー・調整のしやすさ）を説明できる
- [ ] 複数の uGUI Canvas の重なり順（sortingOrder）と選択の競合（誰が selection を管理するか）を
      設計として分離できる
