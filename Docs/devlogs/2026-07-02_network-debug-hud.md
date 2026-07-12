# ネットワーク同期デバッグHUD（NetworkDebugHud、Play中F1）

> 作成日: 2026-07-02
> ブランチ: `feature/dev-tools-preflight-hud`
> 関連: `docs/FABLE5_COMPLETION_PRIORITIES_2026-07.md` P0-4（複数人Treasure運搬の実機検証）支援

## 問題

2-client 検証のたびに「どちらのピアで・どのオブジェクトが・どの権限で・どう見えているか」をログと Inspector で掘っており、症状の切り分けが遅い。client proxy visual gap の調査（devlog 2026-06-22）では「プレイヤー独自補間 vs NetworkRigidbody 補間の実効遅延不一致」に辿り着くまでに、権限と kinematic 状態の確認だけで相当の時間を使った。

今後の敵AI・救出・ラウンド実装はすべて 2-client 検証を伴うため、状態可視化を常備ツール化する。

## なぜこのアプローチか

- **OnGUI（IMGUI）ベース**: uGUI/UI Toolkit のシーン配線・Canvas・EventSystem が不要で、ParrelSync クローン側でも確実に表示される。既存の Host/Join メニュー（SessionManager.OnGUI）と同じ描画系。
- **`RuntimeInitializeOnLoadMethod` による自動生成**: スペック時点では「シーンに空 GameObject 常駐」の予定だったが、シーンファイルを一切汚さない（.unity の diff が出ない）自動生成へ変更した。`#if UNITY_EDITOR || DEVELOPMENT_BUILD` ガードでリリースビルドには入らない。
- **完全 read-only**: シミュレーション状態には一切書き込まない。表示バグが物理バグに化ける事故を構造的に排除する。
- **GC/負荷対策**: オブジェクト走査は 0.5 秒間隔のキャッシュ更新、文字列は StringBuilder 再利用。検証対象の物理挙動へノイズを入れない。

### 不採用案

- **(a) uGUI Canvas**: シーンごとに配線が必要で、クローン側の表示保証が面倒。
- **(b) Fusion Statistics ウィンドウ**: 帯域・snapshot 系の統計は出るが、「オブジェクト毎の入力/状態権限と kinematic 状態」というこのプロジェクトの頻出論点が見えない。
- **(c) カスタム EditorWindow**: ビルド・クローンで使えない。検証は Game ビュー内で完結させたい。

## 仕組み

`Assets/Code/Scripts/Debugging/NetworkDebugHud.cs`（1ファイル、read-only）。

1. **Runner 発見**: `NetworkRunner.Instances` から `IsRunning` の runner を取得。無ければ「No running NetworkRunner」を表示するだけの無害な常駐。
2. **Runner 情報**: `IsServer`（HOST/SERVER / CLIENT）、`LocalPlayer`、`Tick.Raw`、`GetPlayerRtt(PlayerRef.None) * 1000`（ms）、`ActivePlayers` 数。
3. **オブジェクト表**: `runner.GetAllNetworkObjects(list)` からルート NObj のみ列挙し、`InputAuthority` / `StateAuthority` / ローカル視点フラグ（`[MYINPUT]` / `[AUTH]` / `[proxy]`）/ 代表 Rigidbody の `kin`/`dyn` / ルート座標を表示。
4. **グラブ状態**: `runner.GetAllBehaviours<RagdollHandContact>` を列挙し、掴み中の手→対象名を表示。`RagdollHandContact` に read-only アクセサ `IsGrabbing` / `GrabbedBodyName` を追加した（`[Networked] HasGrabbed` の読み取り公開のみ。書き込み経路は追加していない）。

## 実装中に踏んだバグ（学習ポイント）

**旧 `UnityEngine.Input` は本プロジェクトでは実行時例外になる。** F1 トグルを `Input.GetKeyDown` で書いたところ、コンパイルは通るが Play 中に毎フレーム `InvalidOperationException: You are trying to read Input using the UnityEngine.Input class, but you have switched active Input handling to Input System package` が発生した。本プロジェクトは Input System 専用設定のため、`UnityEngine.InputSystem.Keyboard.current[Key.F1].wasPressedThisFrame` へ修正（commit `fix(network): NetworkDebugHudのキー入力をInput System APIへ移行`）。

教訓: **compile-only では入力系の設定不整合は捕まらない**。「コンパイル 0/0 = 完了」としない運用（FABLE5 ドキュメント §2 / AGENTS.md）がそのまま効いた実例。

## 検証

- `uloop compile` 0 errors / 0 warnings。
- ホスト単体 Play（Test_Playground）: HUD 自動生成 → Host 開始後に `HOST/SERVER / LocalPlayer=[Player:1] / Tick 進行 / Players=1`、`newAPRPlayer(Clone)` に `[MYINPUT] [AUTH]`、`Treasure_Heavy(Clone)` / `Stage_Root` に `[AUTH] dyn` を表示。プレイヤーは正常スポーン（退行なし）。Error ログ 0 件。スクリーンショット確認済み。

## 未検証・残タスク（完了扱いにしない）

- **2-client（Host+Clone）での両ピア表示確認は未実施**。ユーザーの実機確認が必要:
  - クライアント画面で自プレイヤーに `[MYINPUT]`、他が `[proxy]` になるか
  - クライアント側 proxy の全パーツが `kin`（純補間方針）、ホスト側が `dyn` か
  - グラブ表示（`-- Grab --` 行）が両ピアで同じ対象を指すか
  - 手順は `.claude/skills/two-client-verification/` を参照
- 既存の RagDollController デバッグ表示（Phase 2 Blending 等）と画面左上で重なる。邪魔なら HUD の Rect 位置調整（将来の微修正）。

## 2026-07-03 更新: コーナーHUD + ワールド空間ラベル併用へ変更

### なぜ併用にしたか

初版はコーナーHUDに全情報（Runner情報＋オブジェクト一覧＋グラブ状態）をテキスト表で詰めていたが、2-client検証では「画面のどのオブジェクトがどの権限か」を**目で対応付ける**作業が本質で、名前の文字列と3D空間上の物体を頭の中で照合するコストが高い。そこで情報を性質で分離した:

- **コーナーHUD（画面左上）**: オブジェクトに紐付かない Runner 全体情報のみ（HOST/CLIENT・LocalPlayer・Tick・RTT・接続人数）。一覧表は撤去。
- **ワールド空間ラベル**: 各ルート NetworkObject の真上（`Camera.main.WorldToScreenPoint`、`z < 0` で背後・画面外は非描画）に、権限（`[MYINPUT]`/`[AUTH]`/`[proxy]`）と kinematic 状態（`kin`/`dyn`）を表示。「見えている物体そのもの」に状態が付くので照合が不要になる。
- **グラブ状態**: 独立セクションではなく、掴んでいる手が属するルート NObj のラベルへ ` -> Treasure_Heavy(Clone)` のように追記（Treasure_Heavy の既存ワールドラベルと同じ流儀）。

read-only / F1 トグル / `RuntimeInitializeOnLoadMethod` 自動生成 / Editor・Development Build 限定という初版の設計原則は変更なし。

### 設計判断（重なり・パフォーマンス）

- **ラベル文字列の構築は従来どおり 0.5 秒間隔キャッシュ**（StringBuilder 再利用）。毎フレーム実行するのは `WorldToScreenPoint` のみで、ルート NObj 数 ≒ 数十回の座標変換は物理検証へのノイズとして無視できる。さらに IMGUI の `OnGUI` は 1 フレームに複数イベントで呼ばれるため、座標変換と描画は `EventType.Repaint` に限定して呼び出し回数を半減。
- **ラベルの重なりは意図的に未解決**。デバッグ専用表示であり、回避レイアウトのコストに見合わない（密集時は F1 で消すか、カメラを寄せる運用）。

### 踏んだバグ（追加）

`UnityEngine.Camera` が本プロジェクトの `MyFolder.Scripts.Camera` 名前空間（OrbitCamera 等）と衝突し、`Camera.main` が CS0118 になる。`UnityEngine.Camera.main` と完全修飾して解決。

### 検証（2026-07-03）

- `uloop compile` 0 errors / 0 warnings。
- ホスト単体 Play（Test_Playground）: コーナーHUDに `HOST/SERVER LocalPlayer=[Player:1] / Tick / RTT / Players=1`、ワールドラベルに `newAPRPlayer(Clone) [MYINPUT] [AUTH]`・`Treasure_Heavy(Clone) [AUTH] dyn` 等を確認。Error ログ 0 件。スクリーンショット確認済み。
- 2-client（Host+Clone）の両ピア表示確認は引き続き未実施（上記「未検証・残タスク」と同じ観点で要確認）。

## 自力再実装チェックリスト

1. input authority / state authority / proxy の違いを、HUD のどの表示がどれに対応するかで説明できるか。
2. `NetworkRunner.Instances` → `GetAllNetworkObjects` → 権限プロパティ、という Fusion の情報取得経路を説明できるか。
3. 「表示系を read-only にする」ことが何の事故を防ぐか説明できるか。
4. `RuntimeInitializeOnLoadMethod` 自動生成とシーン常駐の トレードオフ（シーン diff 汚染 vs Inspector 調整可能性）を説明できるか。
5. Input System 専用プロジェクトで旧 Input を使うと何が起きるか、なぜコンパイルでは捕まらないかを説明できるか。
