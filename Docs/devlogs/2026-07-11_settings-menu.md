# 設定画面とInput System設定

日付: 2026-07-11 実装開始 / 2026-07-12 一区切り

## 目的

ゲーム中に UI/ToggleSettings（Escape、ゲームパッド Start）で設定画面を開き、ユーザー単位の入力設定を変更できるようにする。

対象は次のとおり。

1. マウス視点 X/Y 感度
2. 右スティック視点 X/Y、左スティック移動 X/Y 感度
3. NetworkInputData に使われるボタンの再バインド（KBM / Gamepad）
4. ゲームプレイ入力デバイス（KBM / Gamepad）の有効切替
5. Fusion セッション離脱・アプリケーション終了

## 採用した構造

- `.inputactions` の既存 UI map に `ToggleSettings` を追加。Navigate / Submit / Cancel / Point / Click と同じ asset を使う。
- `InputSettingsData` が感度とデバイス有効フラグを保持。`InputSettingsProcessor` が scaleVector2 と Player map の `bindingMask` を適用。
- `InputSettingsPersistence` が PlayerPrefs に設定と binding override JSON を保存。`InputSettingsRuntime.Register` で各 `REBAKA_Fusion2` インスタンスへ再適用。
- `SettingsMenuController` は Editor-authored の `SettingsMenu.prefab` を制御する。UI 見た目は Prefab、挙動はコード。
- メニュー中は `SettingsMenuState` でローカル入力のみ block。`Time.timeScale` は変更しない（Fusion ホストのシミュレーションと責務を分離）。
- タブは `SettingsTabPage[]`。増減は Inspector の配列要素追加で足りる。

## 人間と AI の分担（学び）

AI は Prefab の見た目配置・当たり判定・Navigation の自動推定が弱い。確実だった進め方は次。

1. 契約（参照名・タブ配列・Explicit の行き先）を先に決める
2. 人が Prefab で配置・色・Explicit Navigation を刺す
3. AI が配線・保存・開閉・テストを担当する

Unity の `GameObject` / `Component` 参照に `??=` を使わない（偽 null ですり抜ける）。

## UX / ナビの要点

- Esc は `ToggleSettings` 専有。`UI/Cancel` 側では Keyboard Escape を無視し、閉じる→すぐ開く二重発火を防ぐ。
- Explicit Navigation は方向あたり1つしか持てない。共有フッター（Reset 等）の Up 先は、表示中タブの `footerUpTarget` をコードで差し替える。
- 感度右数値は実行時に InputField 化。ゲームパッド Select 対象外（`Navigation.Mode.None`）。マウスで直接入力可。
- 保存は同期で一瞬のため、「変更中はメッセージ非表示」にするとちらつく。`SavedMessage` は最短表示時間（既定2秒）＋連続保存時はタイマー延長。

## 仕様の変遷（後半）

- CloseButton / 共通 Footer を廃止。Reset は操作設定ページ下部へ移動。
- ControlsPage に `SavedMessage` を追加（成功／失敗表示）。
- GamePage の Vertical Layout Group でボタンが離れる問題は `Child Force Expand → Height` が原因。

## Editor でのゲームパッド

`ParrelSyncInputUtil` はメイン Editor を KBM 固定、Clone を Gamepad 固定にする。設定の「ゲームパッド有効」とは別レイヤ。本番ビルドでは制限しない。

## なぜ Time.timeScale を止めないか

設定はローカル UI。ホストの `timeScale` 停止は全参加者シミュレーションに波及し得るため、止める対象はローカル入力のみ。

## 検証（一区切り時点）

- Esc で開閉できること、起動時にメニューが閉じていること
- 感度スライダーと右数値が連動し、数値編集できること
- タブ Select でページ切替、SavedMessage 表示
- EditMode: InputSettings* / SettingsMenuState テスト群

## 後続（2026-07-12）

- 右スティック視点のレートベース化: `docs/devlogs/2026-07-12_stick-look-rate-based.md`
- 感度レンジ・ゲームパッド刻み・Select 表示: `docs/devlogs/2026-07-12_sensitivity-range-and-select-visibility.md`
- Host/Join ロビー uGUI・切断メッセージ座布団: `docs/devlogs/2026-07-12_lobby-host-join-gamepad-ui.md`

## 自力再実装チェックリスト

- [ ] binding processor と binding override の役割を説明できる
- [ ] 複数 Action Asset へ同じ設定を再適用する理由を説明できる
- [ ] メニュー中に空入力を送る理由を説明できる
- [ ] Esc と Cancel の二重発火をどう避けるか説明できる
- [ ] タブ増減を `SettingsTabPage[]` で足す手順を説明できる
