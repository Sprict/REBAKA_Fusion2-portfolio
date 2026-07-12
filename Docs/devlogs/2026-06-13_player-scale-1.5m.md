# プレイヤーサイズを3m→1.5mに変更（ルートスケール0.5 + メートル依存パラメータ調整）

日付: 2026-06-13
ブランチ: feature/concept-v3-spec
種別: fix(player)

## 問題

プレイヤーキャラクターの実寸が約3m（実測: 本体コライダー身長 3.08m）と、コンセプト上の正しいサイズ 1.5m の2倍になっていた。レベル（障害物キューブ等）との相対スケールが破綻するため、正しいサイズに修正する必要があった。

実測は uloop の dynamic-code で `PrefabUtility.InstantiatePrefab` → コライダーバウンズ集計で行った。装飾のスフィアチェーン（`Other/Sphere (5)〜(10)`）を除外した本体のみの値:

- 変更前: コライダー身長 3.08m（足裏 y=0.009 〜 頭頂 y=3.088）
- 変更後: コライダー身長 1.54m（足裏 y=0.005 〜 頭頂 y=1.544）≒ 目標 1.5m

## 重要な前提知識: 実際にスポーンされるプレハブはどれか

CLAUDE.md には `Assets/Level/Prefabs/APR_Root.prefab` がメインプレハブと書かれているが、**Test_Playground シーンの `PlayerSpawner.playerPrefab`（NetworkPrefabRef の RawGuidValue）が指しているのは `newAPRPlayer.prefab`**（guid: 4d3a8c6e6a770564e9484f82e4738834）。プレハブ変更時は必ずシーンの参照 GUID から逆引きして対象を確定すること。

- 確認方法: シーン YAML の `RawGuidValue` を `Assets/**/*.meta` の `guid:` と突き合わせる
- 両プレハブとも profile は同一の `Assets/Settings/MainPlayer_AprProfile.asset` を参照

## なぜこのアプローチか（代替案との比較）

採用: **プレハブルート `newAPRPlayer` の localScale を (1,1,1) → (0.5,0.5,0.5)**

1. newAPRPlayer 内部は「巨大スケール×微小スケール」が交互に打ち消し合う多段構造（Armature S=100 → APR_Root S≈0.004 → Root S≈288 → …）。内部のどこか1段を直すのは連鎖崩壊のリスクが高い
2. ルートの一様スケールなら、Unityはコライダー寸法・ジョイントアンカーをローカル空間ごと一様に縮小するため、相対構造が完全に保存される [※理論]
3. Rigidbody の質量・JointDrive のばね定数はトランスフォームスケールでは変化しない（Unityはスケールを物理パラメータに伝播しない）ため、別途プロファイル側の調整が必要になる（後述）

不採用案:
- モデル再インポート（スケールファクタ修正): FBX由来でなくプレハブ内手調整の構造のため影響範囲が読めない
- 内部の Armature スケール修正: 上記の通り多段打ち消し構造が壊れる

## パラメータ調整の判断基準（次元解析）

「メートル次元を持つ値だけを×0.5、無次元・時間次元は据え置き」を原則とした。各パラメータの根拠:

### MainPlayer_AprProfile.asset（変更）

| パラメータ | 旧 | 新 | 根拠 |
|---|---|---|---|
| balanceHeight | 2.1 | 1.05 | ルートからの下向き Raycast 距離（メートル）。`RagDollPhysics.cs:1256` で接地判定に使用。据え置くとジャンプ中も「接地」扱いになる |
| balanceMargin | 0.15 | 0.075 | COM-支持点の許容距離（メートル）。`RagDollPhysics.cs:52` のコメントに明記 |
| moveSpeed | 10 | 5 | 目標水平速度（m/s）。`RagDollPhysics.cs:728` で `targetVel = horizontal * speed`。体長比の速度感を維持するため半減 |
| jumpForce | 22 | 15.5 | **名前に反して力ではなく鉛直速度**（`RagDollPhysics.cs:1001` で `velocity = up * JumpForce`）。[※理論] 跳躍高 h ∝ v²/2g なので、相対跳躍高を保つには v を √0.5 ≈ 0.707 倍 → 22×0.707 ≈ 15.5 |
| proxyInertiaMaxAcceleration | 10 | 5 | プロキシ慣性補正の加速度上限（m/s²）。距離スケールに比例して補正量も半減 |
| poseTeleportDetectThreshold | 2 | 1 | テレポート検出距離（メートル） |

### newAPRPlayer.prefab の RagdollController（変更）

| パラメータ | 旧 | 新 | 根拠 |
|---|---|---|---|
| maxRootPredictionDistance | 1.5 | 0.75 | ルート予測の距離クランプ（メートル） |
| proxyHardSnapRootThreshold | 1 | 0.5 | ハードスナップ発動距離（メートル） |
| proxyHardSnapPartThreshold | 0.6 | 0.3 | 同上（パーツ単位） |
| gizmoSphereRadius | 0.05 | 0.025 | デバッグ表示の球サイズ（メートル、見た目合わせ） |

### 据え置いたもの（理由つき）

- **stepHeight (1.7)**: 名前に反してメートルではなく、ジョイント targetRotation の係数（`RagDollPhysics.cs:831` で `0.09f * stepHeight` のように quaternion 成分に乗算）。無次元なので据え置き
- **stepDuration (0.2s)**: 時間次元。moveSpeed と歩幅が両方半減するため歩行周波数は不変でよい
- **turnSpeed (6)**: 回転速度（角度次元）。スケール不変
- **balanceStrength/coreStrength/limbStrength (5000/2000/500)**: JointDrive のばね値。[※理論] 質量据え置きで体長半減 → 慣性モーメント I=mL² は1/4、重力トルク mgL は1/2 になるため、同じばね値は「相対的に固く・強く」効く。安定側に倒れるためまず据え置き、Play検証で問題なし（ジッタが出たら下げ方向に調整）
- **Rigidbody 質量（1 / 0.5 / 0.01 kg）**: Unityはスケール変更で質量を変えない。質量を変えるとジョイント調整が全崩壊するため据え置き
- **uprightPidKp/Ki/Kd, movementPid系**: RagDollController にプロパティ定義はあるが現在未使用（デッドパラメータ）。調整不要
- **proxyRootPositionKp (30) 等のプロキシゲイン**: [※理論] 位置誤差[m]→加速度[m/s²]のゲインは時定数のみを決め、誤差も補正量も同率で縮むためスケール不変

## 検証（Test_Playground、Host モード）

1. プレハブ再計測: 本体コライダー身長 1.5395m ✓
2. スポーン後30秒以上静置: 直立維持（IsBalanced=True, State=Idle）、maxVel=0.06m/s、NaNなし、バランス崩壊・振動なし ✓
3. W入力で前進: +24.5m 移動、転倒なし、停止後に直立復帰 ✓
4. エラーログ: 本変更起因のものはゼロ（既存の EditorツールバーUIの AssertionException と Main_Backup の LightingData エラーのみ）✓

**未検証（手動確認が必要）**: ホスト/クライアント複数起動での同期挙動。ネットワーク補正しきい値（maxRootPredictionDistance 等）を半減したため、ParrelSync で2クライアント検証を行うこと。

## 検証時のハマりどころ（運用メモ）

- Console の **Error Pause が有効**だと、Unity エディタのツールバーUIバグ（`PlayModeButtonsExtension` の AssertionException）→ Error Pause 発動 → pause切替でまたUI再構築 → 再エラー、の**無限一時停止ループ**になる。uloop の `control-play-mode` で Play しても frame=1 で止まったままになる
- 解除は `ConsoleWindow.SetConsoleErrorPause(false)`（internal static）。uloop Restricted では `Assembly.GetType`/`Type.GetType` がブロックされるが、`typeof(EditorWindow).Assembly.GetTypes()` で型を列挙する経路は通る
- 検証後は Error Pause を元（True）に戻した

## 自力再実装チェックリスト

- [ ] シーンの NetworkPrefabRef RawGuidValue から実スポーンプレハブを特定できる
- [ ] 「ルート一様スケールで何が変わり（コライダー寸法・アンカー）、何が変わらないか（質量・ばね・力）」を説明できる
- [ ] 各プロファイルパラメータの次元（メートル/速度/時間/無次元）をコードの使用箇所から判定できる
- [ ] jumpForce が実際は速度であることをコードで確認し、√スケール則（v ∝ √L）を導出できる
- [ ] 慣性モーメント I=mL² と重力トルク mgL のスケール則から、ばね値据え置きが「安定側」である理由を説明できる
