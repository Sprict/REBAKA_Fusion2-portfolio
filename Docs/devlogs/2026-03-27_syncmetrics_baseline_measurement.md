# SyncMetricsRecorder計測精度改善と当時方式のベースライン測定

> **公開時注記（2026-07-22）:** 本文中の「現行方式」は2026年3月時点のkinematic＋PID補正を指します。この方式は後に全身スナップショット補間へ置き換えられ、PID関連コードも削除されました。本資料はForecast Physicsとの当時計測条件と比較結果を残す歴史資料です。

**日付**: 2026-03-27
**種別**: fix + 検証結果
**関連ドキュメント**: `2026-03-24_forecast_physics_ab_test_setup.md`

## 問題

SyncMetricsRecorderの初回計測で以下の問題が発生し、データが信頼できなかった:

1. **スポーン直後のテレポートがM1/M2/M3を汚染**: クライアント側でmaxFrameDelta=6.203m、M2振幅=5.534m
2. **kinematicモードでのidle誤検出**: クライアントのRigidbodyはkinematic→`linearVelocity`が常に0→サンプルの96%が「idle」と誤判定
3. **Fusion Nested NetworkObjectによるRigidbody検出失敗**: APR_Rootがシーンルートに分離されるため`GetComponentsInChildren`で見つからない

## 修正内容

### 1. ウォームアップスキップ（3秒）

初期化後、`warmupSeconds`（デフォルト3秒）間はデータ収集をスキップ。
スポーン直後の位置ジャンプ・安定化をまたぐ。ウォームアップ終了後にタイマーリセット。

**なぜ3秒か**: ラグドールのスポーン→物理安定化に通常1-2秒。余裕をもって3秒。
Inspectorで調整可能。

### 2. idle検出の改善

変更前:
```csharp
bool isIdle = localVel.magnitude < idleVelocityThreshold;
// kinematicモードではlocalVelが常に0 → 全フレームidle判定
```

変更後:
```csharp
float effectiveSpeed = Mathf.Max(
    netVel.magnitude,                                    // ネットワーク同期された速度
    frameDelta / Mathf.Max(Time.fixedDeltaTime, 0.001f)); // フレーム間移動量から推定
bool isIdle = effectiveSpeed < idleVelocityThreshold;
```

**なぜ2つの速度ソースか**: kinematicモードでは`Rigidbody.linearVelocity`が0。
`NetRootLinearVelocity`はホスト側の実速度が同期されるので信頼できる。
`frameDelta`はフォールバック（ネットワーク速度が遅延する場合のカバー）。

### 3. Root Rigidbody検出（前セッションの修正含む）

変更前:
```csharp
var rbs = _controller.GetComponentsInChildren<Rigidbody>();
// Fusion Nested NetworkObjectにより APR_Root はCloneの子ではなくシーンルートに分離
// → GetComponentsInChildrenで見つからない
```

変更後:
```csharp
var bodyRbs = _controller.BodyRigidbodies; // SerializeField参照（分離に影響されない）
_rootRb = bodyRbs[0];
```

### 4. ホスト自己計測の警告

ホスト側ではlocalPos == NetRootPosition（自分が設定した値を自分で読んでいる）なので、
M3/M5が常に0。評価出力に注意文を追加。

## ベースライン計測結果（現行方式: kinematic+PID補正）

### 条件
- `RagdollProfile.useForecastPhysics = false`
- 2インスタンス（ParrelSync）、30秒計測
- ウォームアップ3秒後に計測開始

### ホスト側（5/6 PASSED）

| 指標 | 値 | 判定 |
|---|---|---|
| M1 テレポートなし | maxDelta=0.433m | PASS |
| M2 振動なし | Y振幅=0.269m | **FAIL** |
| M3 位置追従 | avg=0.000m | PASS（自己計測） |
| M4 ラバーバンドなし | 0回/秒 | PASS |
| M5 回転追従 | avg=0.0deg | PASS（自己計測） |
| M6 帯域 | 9.8KB/s | PASS |
| M7 衝突ドリフト | サンプル不足 | SKIP |

### クライアント側（6/6 PASSED）★重要

| 指標 | 値 | 判定 |
|---|---|---|
| M1 テレポートなし | maxDelta=0.683m | PASS |
| M2 振動なし | Y振幅=0.049m | PASS |
| M3 位置追従 | avg=0.000m, max=0.072m | PASS |
| M4 ラバーバンドなし | 0回/秒 | PASS |
| M5 回転追従 | avg=0.3deg, max=3.6deg | PASS |
| M6 帯域 | 9.8KB/s | PASS |
| M7 衝突ドリフト | サンプル不足 | SKIP |

### 考察

- **現行方式のクライアント同期品質は良好**: 6/6 PASSED
- **ホストM2のみFAIL**: Y振幅0.269mは物理的な振動（ラグドールが静止時に微妙に上下）
  - ClientSideDiagnosticのG1 (Y振幅0.48m) とも一致する傾向
  - これはSyncMetricsの問題ではなく、ラグドール物理の特性
- **M7は壁接触テストが必要**: 意図的に壁に向かって歩くテストシナリオが要る

---

## Forecast Physics A/Bテスト結果（2026-03-28）

### テスト条件

3条件で比較:
1. **現行方式**: `useForecastPhysics=false`、kinematic+PID補正（上記ベースライン）
2. **Forecast (Rootのみ)**: `useForecastPhysics=true`、APR_RootにのみNetworkRigidbody追加
3. **Forecast (全パーツ)**: `useForecastPhysics=true`、全15 APR_パーツにNetworkRigidbody追加

共通条件: NetworkProjectConfig.PhysicsForecast=true、2インスタンス（ParrelSync）、30秒計測、ウォームアップ3秒

### クライアント側 3条件比較表

| 指標 | 現行方式 (kinematic) | Forecast (Rootのみ) | Forecast (全パーツ) |
|---|---|---|---|
| M1 maxDelta | **0.683m, 0回** | 4.953m, 5回 | 2.854m, 4回 |
| M2 Y振幅 | 0.049m | **0.037m** | 0.087m |
| M3 avg / max | **0.000 / 0.072m** | 0.113 / 1.039m | 0.052 / 1.814m |
| M4 overshoot | **0回, 0.0/s** | 4回, 0.1/s | 57回, 1.9/s |
| M5 avg / max | **0.3 / 3.6deg** | 5.4 / 17.5deg | 6.5 / **134.1deg** |
| M6 帯域 | 9.8KB/s | 9.8KB/s | 9.8KB/s |
| 合計 | **6/6** | 5/6 | 5/6 |

### 分析

#### Forecast (Rootのみ) vs 現行方式
- **M1テレポート**: 5回のテレポート発生（最大4.953m）。クライアント予測が発散→Fusionが補正でスナップ
- **M3位置追従**: avg=0.113m。現行方式(0.000m)に比べ大幅に劣化
- **M5回転追従**: avg=5.4deg。現行方式(0.3deg)の18倍
- **M2振動のみ微小な改善**: 0.037m < 0.049m。フル物理の方が静止時は安定

#### 全パーツNRBで逆に悪化した指標
- **M4 overshoot**: 4回→**57回**（14倍）。複数NRBがジョイントチェーン経由で干渉し合い振動
- **M5 max**: 17.5→**134.1度**。NRB補正がジョイント結合した隣接パーツに波及して回転が暴れる
- **M3 max**: 1.039→**1.814m**。スパイク的な補正がさらに大きくなった

#### なぜ全パーツNRBが逆効果か
ラグドールの各パーツはConfigurableJointで繋がっている。
パーツAにNRB補正が入る→ジョイント経由でパーツBに力が伝わる→パーツBにも別のNRB補正が入る→互いに干渉して振動。
ジョイントで結合された剛体系に個別の位置補正を適用すると、系全体が不安定になる。

### 最終結論

**現行方式（kinematic+PID補正）がすべての条件でForecast Physicsに勝利。**

理由:
1. ラグドール物理はカオス系 — わずかな初期値の違いが急速に発散するため、クライアント予測が構造的に困難
2. 現行方式はネットワークデータを直接適用するため誤差がほぼゼロ
3. 全パーツNRBを追加してもジョイント干渉で悪化するだけ
4. Forecast Physicsは剛体1つの単純なオブジェクト向けであり、ジョイント結合されたラグドール系には適さない

**今後の方針**: 現行方式を維持。Forecast Physicsのコードパス（`useForecastPhysics`フラグ分岐）は将来の参考用に残す。

## 自力再実装チェックリスト

- [ ] kinematicモードでRigidbody.linearVelocityが0になる理由を説明できるか
- [ ] Fusion Nested NetworkObjectがランタイムで親子関係を変更する仕組みを説明できるか
- [ ] ウォームアップスキップが必要な理由（スポーンアーティファクト）を説明できるか
- [ ] ホスト側のM3/M5が常に0になる理由を説明できるか
