# Forecast Physics A/B比較検証環境の構築

**日付**: 2026-03-24
**種別**: feat (検証インフラ)
**関連ドキュメント**: `forecast_physics_context.md`, `chat_with_claude.md`

## 問題

`forecast_physics_context.md` でForecast PhysicsがREBAKAに最適と結論したが、現行のkinematic+PID補正方式も十分作り込まれている。どちらが最優先条件（テレポートしない・振動しない）を満たすか、実機で比較検証する必要がある。

## アプローチ

### なぜこのアプローチか

- **A/B切替方式**: `RagdollProfile.useForecastPhysics` フラグ1つで方式を切り替え可能にした
- **コード分岐（ビッグバン移行ではない）**: 既存の現行方式コードを壊さず、Forecastモード時だけ別パスを通る
- **シーン複製ではなくプロファイル切替**: シーン複製はメンテコストが高いため、同じシーンでプロファイルのフラグを切り替える設計

### 不採用にした代替案

1. **ビッグバン移行**: 現行方式を完全に置き換え → 動かなかった場合の戻しコストが高い
2. **シーン2つ**: ForecastPhysicsTest.unity を別シーンとして作成 → プレハブやGameObject構成の差分管理が煩雑
3. **ランタイムフラグ**: NetworkProjectConfigのPhysicsForecastもランタイムで切替 → Fusionの制約上困難

## 変更内容

### 1. SyncMetricsRecorder（新規）
`Assets/Code/Scripts/Diagnostics/SyncMetricsRecorder.cs`

M1〜M7の合否基準で同期品質を自動計測するコンポーネント:
- M1: テレポートしない（1フレーム移動量 < 1.0m）
- M2: 振動しない（静止時Y振幅 < 0.1m）
- M3: ルート追従精度（位置誤差avg < 0.3m）
- M4: ラバーバンディング（方向反転 < 3/秒）
- M5: 回転追従精度（回転誤差avg < 30度）
- M6: 帯域消費（推定 < 30KB/s/player）
- M7: 衝突時ドリフト（max < 0.5m）

### 2. RagdollProfile.useForecastPhysics フラグ
ScriptableObjectにboolフラグを追加。Inspectorでトグル可能。

### 3. RagDollPhysics.HasAuthoritativePhysics() 修正
Forecastモード時は全クライアントで`true`を返す → AddForce等の物理操作が全クライアントで実行される。

### 4. RagdollClientProxyRuntime の分岐
Forecastモード時:
- kinematic化をスキップ
- ClientProxyCorrectionをスキップ
- UpdatePhysicsVisualOnly → UpdatePhysics（フル物理計算）に切替

## 検証手順

### 現行方式の検証
1. Main_Backup.unity を開く
2. RagdollProfileの `useForecastPhysics = false`（デフォルト）
3. SyncMetricsRecorder をシーンに配置
4. Playモード（2インスタンス）で30秒計測
5. CSVでM1〜M7判定

### Forecast Physicsの検証
1. NetworkProjectConfig で `PhysicsForecast = true` に変更
2. RagdollProfileの `useForecastPhysics = true`
3. APR_RootのRigidbodiesにNetworkRigidbody3Dを配置（まずRootのみ）
4. 同条件で計測

### 注意点
- NetworkProjectConfigの`PhysicsForecast`とRagdollProfileの`useForecastPhysics`は**両方**設定する必要がある
- PhysicsForecast=trueのままuseForecastPhysics=falseにすると、Fusionが物理予測を行うがクライアントのモーター計算は実行されない状態になる → 未定義動作

## 自力再実装チェックリスト

- [ ] `HasAuthoritativePhysics()` が何を判定しているか説明できるか
- [ ] Forecastモード時にクライアント側でAddForceが実行される理由を説明できるか
- [ ] kinematic化が現行方式で必要な理由（振動防止）を説明できるか
- [ ] `UpdatePhysics` と `UpdatePhysicsVisualOnly` の違いを説明できるか
