# 2026-04-17 uLoopMCP — Roslyn shared worker 起動失敗と one-shot fallback

## 症状

Unity Editor起動直後のClaude Codeセッションで、`uloop get-logs` を初回実行したとき以下2件のErrorが出現：

```
[uLoopMCP] execute-dynamic-code shared Roslyn worker is unavailable; falling back to one-shot compiler execution
[uLoopMCP] execute-dynamic-code shared Roslyn worker failed to operate correctly
```

以後の `uloop execute-dynamic-code` は成功（one-shot fallbackで動作）。

## 影響評価

- **機能**: 問題なし。コンパイル・実行は完遂する
- **速度**: [※推測] 毎回Roslynを新規初期化するため、shared worker運用時より初回コンパイルが数百ms〜1秒程度遅い可能性
- **連続実行**: [※未確認] 長時間セッションでワーカー起動が安定するかは未検証
- **トークン効率**: Claude側には影響なし（エラーはUnityコンソールに出るのみ）

## なぜ起きたか（仮説）

[※推測] 考えられる原因：

1. **Unity起動直後のAssembly Reload中**にuLoopMCPのshared workerを起動しようとしてAppDomainが不安定
2. **Roslyn関連DLLのロードタイミング**が遅く、ワーカー初期化時にまだ解決できていない
3. **uLoopMCP 1.7.3の既知バグ** — [※未確認] GitHub Issue未確認

## 対処（検証順）

### 優先度1: 様子見（現時点の推奨）

one-shot fallbackで動作しているため、**本番作業に支障なし**。エラーメッセージは無視してよい。

### 優先度2: Unity再起動

次セッション開始時に：
1. Unity Editorを完全終了
2. `.uloop/outputs/` 配下を削除（`.uloop/settings.permissions.json` は残す）
3. Unity再起動 → `uloop execute-dynamic-code --code "return 1;"` を最初に実行
4. エラーが消えたか `uloop get-logs` で確認

### 優先度3: uLoopMCPアップデート

```bash
# Packages/manifest.json のGit URL末尾に `#v1.7.4` 等のタグ指定を検討
# （2026-04-17時点でv1.7.3が最新 [※未確認]）
```

### 優先度4: 上流Issue化

`hatayama/uLoopMCP` リポジトリで既存Issue検索。なければ再現手順付きで報告。

## 再発防止チェックリスト（自力再実装用）

- [ ] Unity起動後、最初の `uloop execute-dynamic-code` 実行時にshared workerエラーが出ていないか確認
- [ ] 出た場合: 機能影響あるか（コンパイル失敗・結果不正）を検証
- [ ] 影響なしなら：本devlogにリンクしてスキップ、作業継続
- [ ] 影響ありなら：Unity再起動 → `.uloop/outputs/` クリア → 再試行
- [ ] それでも直らない場合：uLoopMCPを最新版に更新、あるいはIssue報告

## 代替案・不採用理由

- **IvanMurzak/Unity-MCPへの即時乗り換え**: 不採用。現時点でuLoopMCPは実用上問題なし。乗り換えはトークン効率の観点で慎重に検討（先日の比較調査参照）
- **`execute-dynamic-code` の使用回避**: 不採用。動的C#実行はFusion `[Networked]` プロパティ取得など**core value**のため諦めない

## 関連ファイル

- `Packages/manifest.json` — uLoopMCPバージョン定義
- `.uloop/settings.permissions.json` — セキュリティレベル1 (Restricted)
- `.claude/skills/uloop-execute-dynamic-code/SKILL.md` — 呼び出し定義
