# Fusion統合プリフライトチェッカー（Tools > REBAKA > Preflight Check）

> 作成日: 2026-07-02
> ブランチ: `feature/dev-tools-preflight-hud`
> 関連: `docs/FABLE5_COMPLETION_PRIORITIES_2026-07.md` P0-3（統合前チェックリスト）の実体化

## 問題

Fusion統合まわりの事故は、毎回「起きた後の診断」に日単位のコストを食ってきた。

| 過去事故 | 症状 | 診断コスト |
|----------|------|-----------|
| stray NetworkProjectConfig（2026-06-19） | ホストでもスポーンせず、Editor.log に "has not been weaved" | 丸1日 |
| シーン未登録 + StartGameArgs Scene未指定（2026-06-20） | シーン配置NetworkObjectが同期しない | 半日以上 |
| Obs_Cube worldPrefabs二重登録 | 同一オブジェクトが2つスポーン | 上記と同時発覚 |

Fable 5 が 2026-07-08 以降従量課金化され、高精度の事故診断支援が使いづらくなる。そこで事故対応を「起きた後に診断」から「起きる前に検出」へ移す。

## なぜこのアプローチか

### Run() / Evaluate() 分離

各チェックは「プロジェクト状態の収集（`Run()`）」と「判定純関数（`static Evaluate(...)`）」に分離した。

- 判定ロジックだけを EditMode テストで固定できる（20件、`Assets/Code/Editor/Tests/`）。
- 副次効果として、**テストがプロジェクト不変条件の回帰テストを兼ねる**。例えば config 一意性の判定が壊れる変更はテストランナーで即赤になる。
- `TreasureGrabRegistry` や Map 純コアと同じ「純粋ロジック分離」パターンで、プロジェクト内の設計語彙に揃えた。

### 誤緑（false pass）が最悪、という非対称性

チェックが赤を見逃すと「ツールが緑だったから安心して統合 → 事故」となり、ツールがない状態より悪い。この非対称性から:

- 判定に確信が持てないケース（JSON パース不能、Map 系シーンが開かれていない等）は必ず **Warning（黄）** に倒す。
- 完全自動判定できない「二重スポーン」は無理に緑/赤を出さず、シーン配置 NetworkObject の**目視用リスト**を黄で出すに留める。
- チェック実行中の例外は **Fail 扱い**（例外で結果欄が空 = 緑に見えるのが最悪）。

### 不採用案

- **(a) CI での自動実行**: Unity ライセンス/バッチ実行環境が未整備。個人開発の現段階では YAGNI。チェック本体は分離済みなので、将来 CI に載せるときは Evaluate 群をそのまま使える。
- **(b) 二重スポーンの完全自動判定**: 「spawner がその prefab を実際に Spawn するか」は動的挙動（設定・分岐）に依存し、静的解析では誤緑リスクが高い。目視リスト方式が誠実。
- **(c) Play 直前フックでの常時強制**: 毎回走るとうるさすぎて無効化される未来が見える。統合前に意図して叩く運用（skill 化）を選んだ。

## 仕組み

```
Assets/Code/Editor/Preflight/
├── PreflightCheck.cs        … PreflightStatus(3値) / PreflightResult / IPreflightCheck
├── Checks/                  … 1事故 = 1チェッククラス（6個）
└── PreflightCheckWindow.cs  … Tools > REBAKA > Preflight Check
```

| # | チェック | 判定 | 対応事故 |
|---|---------|------|---------|
| 1 | ConfigUniquenessCheck | 正本パス以外に .fusion があれば Fail | stray config（memory: project-fusion-stray-networkprojectconfig） |
| 2 | WeaveAssembliesCheck | `MyProject.Scripts` が AssembliesToWeave に無ければ Fail。JSON 読めなければ Warn | 同上（"has not been weaved"） |
| 3 | SceneRegistrationCheck | `Test_Playground` / `MapNetworkSandbox` が Build Settings 未登録/無効なら Fail | memory: project-scene-object-sync-registration |
| 4 | ScenePlacedObjectsCheck | シーン配置 NObj があれば一覧を Warn（0件なら Pass） | Obs_Cube 二重スポーン |
| 5 | BackupFreshnessCheck | `BakeFusionConfig.ShouldBakeForMppm` 再利用。stale なら Warn | MPPM backup 再ベイク忘れ |
| 6 | MapWiringCheck | MapBuilder/_catalogAsset・MapTreasureSpawner/_treasurePrefab の null を SerializedObject で検出。Map 系がシーンに無ければ「未検査」Warn | MapTreasureSpawner 実機未検証の統合予防 |

実装メモ:

- private `[SerializeField]` の null 検査は **SerializedObject.FindProperty** で行い、runtime クラスを無変更に保った。フィールド名が見つからない場合も missing 扱い（リネームで検査が静かに無効化されるのを防ぐ）。
- ウィンドウのサマリは Fail>0 で「統合禁止」を明示。運用手順は `.claude/skills/integration-preflight/` に skill 化。

## 検証

- `uloop compile` 0 errors / 0 warnings。
- EditMode テスト 20件緑（ConfigUniqueness 4 / Weave 4 / SceneRegistration 3 / ScenePlaced 2 / BackupFreshness 2 / MapWiring 5）＋既存 BakeFusionConfigTests 3件の回帰なし。
- 実プロジェクトで全チェック実行し、判定が実態と一致することを確認（config=正本1つ→Pass、Test_Playground が開いた状態で Stage_Root が目視リストに出る→Warn、Map 系なし→未検査 Warn）。ウィンドウ描画もスクリーンショットで確認。

## 自力再実装チェックリスト

1. 3値判定（Pass/Warning/Fail）と「確信がなければ黄」の原則を説明できるか。なぜ2値では駄目か（誤緑の非対称コスト）。
2. `IPreflightCheck.Run()` と `static Evaluate()` の分離理由を、テスト容易性と回帰検知の2点で説明できるか。
3. 各チェックが対応する過去事故と、その事故の症状→根本原因を自分の言葉で言えるか。
4. private [SerializeField] を runtime クラス無変更で検査する方法（SerializedObject）と、その限界（シーンが開いている必要がある）を説明できるか。
5. 例外を Fail に落とす理由（「チェック不能」を「合格」と混同させない）を説明できるか。
