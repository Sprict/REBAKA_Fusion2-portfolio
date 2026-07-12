# FocusGuard smoke checkpoint

**日付**: 2026-05-09  
**目的**: FocusGuard の Unity project commit 判定に使える小さな project-facing 変更を残す  
**スコープ**: ドキュメントのみ（runtime code、scene、ProjectSettings は変更しない）

---

## 状態

- FocusGuard の PC / Claude Code 側セットアップは完了済みとして、この Unity repo 側に smoke checkpoint を記録する。
- 既存の作業ツリーには、`Docs/SPEC.md`、`Assets/Level/Scenes/Test_Playground.unity`、`ProjectSettings/EditorBuildSettings.asset` など別作業の未コミット変更がある。
- この checkpoint では既存変更に触れず、`Docs/devlogs/` 配下の新規 markdown だけを Unity project signal として追加する。

---

## 次の検証タスク

1. FocusGuard audit が `Docs/devlogs/2026-05-09_focusguard_smoke_checkpoint.md` を Unity project commit signal として拾うか確認する。
2. post-commit hook が既存 dirty files を stage 対象に混ぜていないか確認する。
3. Unity Editor 起動後にこの markdown の `.meta` が生成された場合は、別 checkpoint として扱うか、ignore / commit 方針を確認してから処理する。

---

## 変更しなかったもの

- runtime code
- scene file
- ProjectSettings
- Unity generated/cache folders (`Library/`, `TestResults/`, `.uloop/`, `.npm-cache/`, `.backups/`)
