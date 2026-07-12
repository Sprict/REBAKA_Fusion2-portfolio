# Fusion 2.1 Forecast Physics への移行

日付: 2026-01-18

## TL;DR（30秒で読める要約）

- Fusion 2.0.9 → 2.1 にアップグレード
- **Forecast Physics** で物理同期の根本的な問題を解決
- Preview版のリスクより「根本解決」を優先する判断

## 何を学んだか

### Forecast Physics とは

- **外挿（Extrapolation）ベース**の物理同期
- 各クライアントがローカルで物理を実行し、ネットワーク遅延を補正
- CPU負荷が低い（フル再シミュレーション不要）

### Fusion 2.0 vs 2.1 の違い

| 項目 | 2.0.x | 2.1 |
| ------ | ------- | ----- |
| 物理同期 | Physics Addon（別パッケージ） | NetworkTransform に統合 |
| 同期方式 | フル再シミュレーション | 外挿ベース |
| CPU負荷 | 高い | 低い |

### API変更点

- `OnReliableDataReceived` のシグネチャ変更
  - `ArraySegment<byte>` → `ReadOnlySpan<byte>`

## なぜそうしたか（技術的判断）

### 問題

- クライアント側でラグドールがガタガタ震える
- 手足が吹っ飛ぶ
- 重力が正しく動作しない

### 判断

「壊れた古いシステムを修理する時間」より「新しいシステムを導入する時間」の方が確実性が高い。

### トレードオフ

- ✅ 根本解決できる
- ⚠️ Preview版の不安定さ
- ⚠️ ドキュメントが少ない

## 想定される質問

**Q: なぜ安定版ではなくPreview版を選んだのか？**

A: 2.0.x では物理同期の根本的なアーキテクチャが問題だった。Physics Addon でパッチ的に対応するより、2.1 の Forecast Physics で根本解決する方が長期的に正しいと判断した。リスクヘッジとして Git でバックアップを取り、いつでも戻れる状態にした。

## 関連キーワード

- `Forecast Physics`, `Extrapolation`, `NetworkTransform`
- `INetworkRunnerCallbacks`, `ReadOnlySpan<byte>`
- `Active Ragdoll`, `Physics Sync`
