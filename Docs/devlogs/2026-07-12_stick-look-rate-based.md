# 右スティック視点をレートベース（度/秒）へ — Look 統一版

日付: 2026-07-12

## 問題

右スティック視点が「ちょうど良い」と感じる感度が約 8.0 だった。一方でマウスは 1.0 前後で十分なことが多い。

さらに、同じスティック倒し量でもフレームレートが変わると旋回速度が変わる。60fps で調整した体感が、144fps では速く／遅くなる。

## 歴史的経緯

元の `.inputactions` には右スティック用に `ScaleVector2(x=10, y=5)` が直書きされていた。設定メニュー導入後、感度は `overrideProcessors` で適用するようになった。

Input System の `overrideProcessors` は authored processors を「置き換える」。ベース倍率 10/5 は消え、設定値 1.0 だけが残る。結果として感度 1.0 が激遅になり、ユーザーは 8.0 前後まで上げて体感を取り戻していた。

一度 `LookStick` アクションを分離してコード側で deg/s × deltaTime していたが、ハットスイッチ非対応の方針と合わせ、**視点は `Player/Look` に統一**し、デバイス差は Input System の Processor で吸収する形に戻した。

## 原理

| 入力 | 意味 | 扱い |
|------|------|------|
| マウス | 移動量デルタ | そのまま Look に載る |
| 右スティック | 倒し量＝レート | `stickToLookDelta` でデルタ等価化してから Look に載る |

コード（`OrbitCamera` / `InputCollector`）は `Look.ReadValue()` だけを見る。

## 仕組み

1. **`StickToLookDeltaProcessor`**  
   `value * unitsPerSecond * Time.deltaTime`（既定 480/秒 = 旧 感度8.0×60fps）

2. **binding**  
   `<Gamepad>/rightStick` → `Player/Look`  
   authored: `stickToLookDelta(unitsPerSecond=480)`  
   感度適用時: `stickToLookDelta(...),scaleVector2(x=...,y=...)`  
   （override が置き換えるため、レート変換を必ずチェーンする）

3. **体感の対応**  
   Orbit: `480 × orbitSensitivityX(0.2) = 96` 度/秒（感度 1.0・フル倒し）

4. **ハットスイッチ**  
   未対応。Joystick Hat バインディングは置かない。

5. **感度スライダー（ゲームパッド）**  
   Unity Slider のナビ刻みは `(max-min)*0.1` のため、実値 0.1〜10 だと約 1.0 刻みになる。
   内部を 0.1 単位の整数（`wholeNumbers`）に載せ替え、左右1回＝感度 0.1 にした。

## 移行

既存ユーザーの PlayerPrefs 保存値（例: 8.0）は自動リセットしない。  
新基準では感度 1.0 が旧 8.0 相当なので、必要ならユーザーが手動で 1.0 へ戻す。
`MaximumSensitivity` もレートベース化後の体感に合わせ 10 に調整済み。

## 自力再実装チェックリスト

- [ ] `StickToLookDeltaProcessor` を登録（Editor + RuntimeInitializeOnLoad）
- [ ] rightStick を `Look` にバインドし、authored に `stickToLookDelta` を書く
- [ ] `InputSettingsProcessor` が gamepad look に `stickToLookDelta,scaleVector2` をチェーンする
- [ ] `OrbitCamera` / `InputCollector` は `Look` のみ（デバイス分岐なし）
- [ ] 感度スライダーを 0.1 単位整数化し、ゲームパッドでも 0.1 刻みになる
- [ ] EditMode で Convert のフレームレート非依存をテスト
- [ ] Play で感度 1.0・60fps/高fps の体感を確認（ユーザー側）
