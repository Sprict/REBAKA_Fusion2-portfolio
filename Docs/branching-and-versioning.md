# ブランチ運用とバージョニング

REBAKA_Fusion2 のブランチ戦略とバージョン番号の付け方をまとめる。
操作上の最小ルールは `AGENTS.md`（正本）と `.claude/CLAUDE.md` にあり、本ドキュメントはその **「なぜ」と背景** を担当する。

---

## 結論（採用方式）

**簡易 Git Flow（リリース昇格型）＋ SemVer タグ。**

- `feature/*` `fix/*` 等の短命ブランチ → `develop` に統合（既存運用のまま）
- リリース時に `develop` → `main` へ昇格する PR を出し、`main` に SemVer タグを打つ
- 出荷済み `main` の緊急バグは `hotfix/*` を `main` から切り、`main`（PATCH発番）と `develop` の両方へ戻す

既存の `feature/fix → develop → main` 構成に、**①main でのタグ発番 ②hotfix 経路 ③戻しマージの規律** を足しただけ。

```
feature/login ─┐                       ┌ hotfix/crash ┐
               ▼                       │              ▼
develop ───●───●───●───────●───────────┼──────────────●──→  (統合・最新プレイ可能)
               fix/jump    │ release    │  back-merge  │
                           ▼            │              ▼
main ──●────────────────────●───────────●──────────────●──→  (リリース済みビルド)
       v0.1.0               v0.2.0      （hotfix起点）  v0.2.1
```

---

## ブランチ不変条件：develop / main は常にプレイ可能

**最重要ルール：`develop` と `main` に上げるコミットは、必ずゲームが正常に起動・プレイできる状態であること。**

- 壊れた途中状態・コンパイルが通らない状態・起動しない状態は、`feature/*`・`fix/*`・`hotfix/*` の作業ブランチに留める。
- `develop`／`main` へ統合する前に、**Play モードで起動とプレイ可能を確認**（最低30秒。`uloop-verify-play` 等）。
- これは Git Flow でも trunk-based でも共通の核心＝「**統合ブランチは常にグリーン**」。これが崩れると、他の作業ブランチが壊れた土台の上に積み上がり、原因切り分けが一気に困難になる。
- 物理・ネットワーク変更時は特に、最低30秒の安定性／ホスト・クライアント両方の確認を統合の条件とする。

この不変条件があるため、`develop` はいつでもリリース候補に昇格でき、`main` はいつでも出荷可能、という状態が保たれる。

---

## なぜこの方式か（戦略比較）

| 戦略 | 要点 | 向く相手 |
|---|---|---|
| **Git Flow** | main/develop/feature/release/hotfix。リリースを“イベント”として扱う | 版を区切って出す製品（ゲーム/パッケージ） |
| GitHub Flow | main＋短命ブランチのみ、main から即デプロイ | 常時デプロイの Web サービス |
| Trunk-Based Development | 全員が trunk へ高頻度コミット＋feature flag | CI/CD の小さく練度の高いチーム（DORA 研究で “elite” 指標） |
| GitLab Flow | GitHub Flow＋環境/リリースブランチ | 両者の折衷 |

近年の総意は「Web の常時デプロイなら trunk-based 寄り、**Git Flow はレガシー**」（Atlassian は "legacy workflow" と表記、考案者 Vincent Driessen も CI/CD チームには非推奨と明言）。

**ただしこの批判は主に“常時デプロイの Web サービス”向け。** ゲームは「v1.2 を出す」という**離散リリースが本質**なので、release/hotfix を持つ Git Flow 系の方が素直に噛み合う。

- [※未確認] 大手スタジオの多くは Git ではなく **Perforce のメインライン運用**（巨大バイナリアセットのため）で、「ゲーム＝Git Flow が標準」と一括りにはできない。Git を使う規模では Git Flow 系が定番、という理解にとどめる。
- モダン側から**借りるべき教訓は1つ**：**ブランチを長生きさせない**（feature/fix は数日で develop に落とす）。Git Flow 最大の弊害＝長命ブランチのコンフリクト/統合遅延は、これで避けられる。

---

## バージョン番号（SemVer）の付け方

SemVer は本来 **X.Y.Z ＝ Major.Minor.Patch** で「API の後方互換」を表す規約（semver.org）。これは**ライブラリ用の定義**で、ゲームは“公開 API”を持たないため**そのままは当てはまらない**。ゲーム流の読み替え：

| 状態 | ルール |
|---|---|
| **0.x.y（1.0 前）** | 「開発中、いつ何を変えてもよい」期間。**公開ローンチ＝1.0** まで MAJOR は 0 据え置き。MINOR=機能/コンテンツのまとまり、PATCH=バグ修正・微調整 |
| **1.0 以降** | MAJOR=大型刷新/セーブ破壊級、MINOR=機能・コンテンツ更新、PATCH=修正パッチ |

**番号は「どのブランチに入れたか」ではなく「今回のリリースに何を含むか」で決まる：**

| 今回のリリースに含むもの | 上げる桁 | 例 |
|---|---|---|
| 新機能・新コンテンツ | MINOR | 0.7.3 → 0.8.0 |
| バグ修正・微調整だけ | PATCH | 0.8.0 → 0.8.1 |
| 大型刷新・セーブ破壊級・節目 | MAJOR | 0.x → 1.0 |

**現在のバージョン：`v0.1.0`**（Unity `ProjectSettings` の `bundleVersion` と一致。2026-06-20 にベースラインタグを発番）。

---

## 運用フロー（手順）

### 通常開発
1. 最新 `develop` から `feature/<topic>` か `fix/<topic>` を切る
2. 小さくコミット → `develop` へマージ → ブランチ削除
3. ブランチは**短命に**保つ（数日以内に統合）

### リリース（develop → main 昇格）
1. `develop` が出せる状態になったら **`develop` → `main` の PR** を作成
2. マージ後、`main` で `git tag -a vX.Y.Z -m "..."` → `git push origin vX.Y.Z`
3. `bundleVersion` も同じ番号に更新
4. 安定化に時間が要る場合のみ `release/0.8.0` を develop から切り、そこでバグ取り→main（ソロなら省略可）

### hotfix（出荷済み main の緊急バグ）
1. `main` から `hotfix/<topic>` を切る
2. 修正 → `main` へマージ → PATCH タグ発番
3. **`develop` にも必ず戻しマージ**（取りこぼし・先祖返り防止）

### 鉄則
- release/hotfix で `main` に入った修正は**必ず `develop` にも戻す**
- `main` への直接 push・force push は禁止（`main` への反映は PR のみ。hotfix も hotfix ブランチ経由）

---

## よくある誤解

- **「小さな変更は develop に溜めるだけ」ではない。** PATCH もいずれ `main` に出してこそプレイヤーに届く。develop はあくまで「次リリースまでの待機所」。
- **番号はブランチでは決まらない。** リリース時の中身で MINOR/PATCH/MAJOR を選ぶ。
- **0.x の間は MINOR が“大きい区切り”の役割。** 1.0 までは MAJOR を上げない。

---

## コマンド早見表

```bash
# リリースタグ（main 上で）
git tag -a v0.2.0 -m "release: 屋内ライティングと運搬機能"
git push origin v0.2.0

# タグ一覧 / 直近タグ確認
git tag -l
git describe --tags --abbrev=0

# 間違えたタグの削除（ローカル / リモート）
git tag -d v0.2.0
git push origin :refs/tags/v0.2.0

# hotfix の起点（main から）
git switch main && git switch -c hotfix/crash-on-join
```

---

## 参考

- [Semantic Versioning 2.0.0](https://semver.org/)
- [Atlassian — Branching strategies / Gitflow workflow](https://www.atlassian.com/agile/software-development/branching)
- [Trunk-Based Development vs Gitflow（Flagsmith）](https://www.flagsmith.com/blog/trunk-based-development-vs-gitflow)
- [Git Branching Strategies 2026（DeployHQ）](https://www.deployhq.com/blog/5-effective-git-branching-strategies-for-streamlined-development)
