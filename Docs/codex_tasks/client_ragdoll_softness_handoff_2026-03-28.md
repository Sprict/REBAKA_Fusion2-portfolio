# client_ragdoll_softness handoff (2026-03-28)

## これは何のためのコミットか

このコミットは、クライアント側ラグドールの動きをホストに近づけるために、
proxy ragdoll に慣性ベースの二次運動を足した途中段階の handoff 用コミットです。

狙いは「同期精度を崩さずに、見た目の固さだけを減らす」ことです。
Forecast Physics に戻すのではなく、現行の Hybrid proxy 経路の上に装飾的な力を足す方針です。

このコミットは完了コミットではありません。
未検証点を残したまま、Claude Code が続きから再開できるように文脈を固定するための
WIP handoff です。

## 背景と制約

### 問題

- ホスト側はフル物理なので、揺れ・慣性・二次運動がある
- クライアント側は `kinematic root + proxy correction` なので、動きが固い
- 非 root パーツは dynamic だが、従来は `AddForce` 系が一切無かった
- そのため、ジョイントのバネだけで姿勢が決まり、ロボット的に見える

### 守るべき制約

- Forecast Physics は使わない
- SyncMetrics M1-M7 のクライアント 6/6 PASS を維持する
- Hybrid / Legacy / Forecast の 3 戦略は壊さない
- ルートジョイントの `targetRotation` は変更しない
- 新パラメータは `RagdollProfile` で Inspector 調整可能にする

### なぜ Forecast Physics を使わないのか

既存の A/B テストで、Forecast Physics はテレポート、ラバーバンディング、
位置・回転追従の悪化、ジョイント干渉による振動増加が確認済みです。

参照:
- `Docs/devlogs/2026-03-27_syncmetrics_baseline_measurement.md`
- `Docs/codex_tasks/client_ragdoll_softness.md`

## 現在のアーキテクチャ要点

### クライアント側の更新パス

`RagdollClientProxyRuntime.RunFixedUpdate()`

1. `EnsureCorrection()`
2. `UpdateVisualProxyPhysics()`
3. `ClientProxyCorrection.ApplyCorrection(dt)`

### 状態の整理

- root (`APR_Root`) は `isKinematic = true`
- 非 root パーツは `isKinematic = false`, `useGravity = false`
- head / hands は必要に応じて直接補正される
- root の位置・回転はネットワーク姿勢に追従している

### 触ってはいけない部分

`RagDollPhysics.UpdateRootRotation()` は、ルートジョイントの
`targetRotation` を変える経路です。

ここは `spring = 5000` の強いトルク源なので、クライアントで動かすと

1. ルートが大きく動く
2. proxy correction が引き戻す
3. ラバーバンディングになる

という失敗パターンに入ります。

今回の変更では、この経路には手を入れていません。

## 今回やったこと

### 実装の主方針

推奨アプローチ A を採用しました。

- `NetRootLinearVelocity` の差分から root 加速度を作る
- 加速度を平滑化する
- 上限 clamp を掛ける
- 反対向きの慣性加速度として非 root dynamic body にだけ `AddForce(..., ForceMode.Acceleration)` を入れる

### 実装内容

#### 1. `RagdollProfile` に調整パラメータ追加

追加したパラメータ:

- `proxyInertiaForceScale = 0.35`
- `proxyInertiaMaxAcceleration = 10`
- `proxyInertiaSmoothing = 0.25`
- `proxySecondaryGravityScale = 0`

意図:

- `proxyInertiaForceScale`: 慣性の強さ
- `proxyInertiaMaxAcceleration`: ネットワークスパイクの抑制
- `proxyInertiaSmoothing`: 急激な変化の緩和
- `proxySecondaryGravityScale`: 将来の微小重力用。今回は 0 で無効

#### 2. `RagDollController` で設定受け渡し

`CreateClientProxyCorrection()` から
`ProxyCorrectionSettings` に新パラメータを渡すようにしました。

#### 3. `ClientProxyCorrection` に二次運動注入を追加

追加した主な責務:

- 慣性用の状態保持
  - 前回の `NetRootLinearVelocity`
  - 平滑化済み加速度
- bootstrap 時の状態初期化
- hard snap 時の状態リセット
- 非 root dynamic body への加速度注入

設計上の注意:

- root には力を入れない
- `isKinematic` な body には入れない
- head / hands を直接補正している場合は、その 3 部位には力を入れない
- hard snap が起きた tick では注入せず、状態だけリセットする

#### 4. profile asset に明示値を追加

以下に新パラメータの初期値を明示しました。

- `Assets/Settings/MainPlayer_AprProfile.asset`
- `Assets/Settings/MainPlayer_PIDProfile.asset`

#### 5. EditMode テストを追加

追加したテスト:

- `ClientProxyCorrectionSecondaryMotionTests`

意図:

- `NetRootLinearVelocity` が変化したとき、非 root body に逆向きの二次運動が出ること
- `proxyInertiaForceScale = 0` で二次運動が無効になること

## 今回やっていないこと

### B/C 案は本格投入していない

- B: joint drive の柔軟化
- C: 微小重力の復活

今回は A 案を主実装にしました。
`proxySecondaryGravityScale` は将来の C 案用の余地ですが、初期値 0 で無効です。

### root correction の責務は変えていない

- root の `MovePosition` / `MoveRotation` 方針は維持
- root joint の `targetRotation` は未変更
- 3 戦略パターンも未変更

## 検証状況

### 確認済み

Bee が生成した `csc` の response file を使い、以下のコンパイルは通しました。

- `MyProject.Scripts`
- `MyProject.EditModeTests`

確認時の既知 warning:

- `Assets/Code/Scripts/Diagnostics/VibrationRecorder.cs(185)`
  - `prevVelY` 未使用

これは今回の変更とは無関係の既存 warning です。

### まだ終わっていないこと

#### 1. Unity batchmode の EditMode 実行がこの環境で完走していない

この CLI 環境では、Unity の batchmode 実行で以下が発生しました。

- `Recovering Scene Backups` ダイアログ
- Licensing Client の再接続失敗
- その結果、`-runTests` が完走しない

補足:

- 一度は `Package Manager` 接続失敗もあったが、`-noUpm` でそこは外れた
- それでも最終的には Licensing Client 側で止まった

つまり、今回の未検証は「コードが壊れているから」ではなく、
この実行環境の Unity 起動条件が不安定なことが原因です。

#### 2. ParrelSync 2 インスタンスでの 30 秒目視確認

未実施です。

#### 3. SyncMetrics M1-M7 の再測定

未実施です。

## Claude Code に次にやってほしいこと

### 優先度 1

Unity Editor 上で以下を実施してください。

1. `ClientProxyCorrectionSecondaryMotionTests` を EditMode で実行
2. コンパイルだけでなく、実際に test runner 上で pass を確認

### 優先度 2

ParrelSync 2 インスタンスで 30 秒確認してください。

見る点:

- ホストとクライアントの見た目差が縮まったか
- head / hands の不自然な引っ張られ方が増えていないか
- hard snap 増加の兆候がないか

### 優先度 3

`SyncMetricsRecorder` で M1-M7 を再測定してください。

完了条件:

- クライアント 6/6 PASS 維持
- 目視上の柔らかさが改善

### 優先度 4

必要なら以下を調整してください。

- `proxyInertiaForceScale`
- `proxyInertiaMaxAcceleration`
- `proxyInertiaSmoothing`

調整の考え方:

- 揺れが弱すぎる: `proxyInertiaForceScale` を少し上げる
- スパイクや暴れがある: `proxyInertiaMaxAcceleration` を下げる
- カクつく: `proxyInertiaSmoothing` を少し上げる

## 変更ファイルの要点

コード:

- `Assets/Code/Scripts/Player/ClientProxyCorrection.cs`
- `Assets/Code/Scripts/Player/RagDollController.cs`
- `Assets/Code/Scripts/Player/RagdollProfile.cs`
- `Assets/Code/Tests/EditMode/ClientProxyCorrectionSecondaryMotionTests.cs`

asset:

- `Assets/Settings/MainPlayer_AprProfile.asset`
- `Assets/Settings/MainPlayer_PIDProfile.asset`

文脈資料:

- `Docs/codex_tasks/client_ragdoll_softness.md`
- `Docs/codex_tasks/client_ragdoll_softness_handoff_2026-03-28.md`

## 引き継ぎメモ

このコミットは未検証点を含む WIP handoff です。

- 実装の主眼は「見た目の柔らかさ」
- 同期精度はまだ再計測前
- 完了条件は
  - ParrelSync 目視確認
  - SyncMetrics M1-M7 再確認

今回の設計判断は次の一文で要約できます。

> ルートの権威的な追従は維持しつつ、非 root パーツにだけ慣性由来の装飾的な力を加えて、
> 同期精度を崩さずにクライアント見た目の硬さを減らす方針を取った。
