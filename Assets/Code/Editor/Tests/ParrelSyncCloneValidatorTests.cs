using Rebaka.Editor;
using NUnit.Framework;

namespace Rebaka.Editor.Tests
{
    /// <summary>
    /// クローン健全性チェックの判定ロジック。
    ///
    /// 検査そのものはファイルシステムに依存するが、「存在するか」「リンクか」という
    /// 事実から結論を出す部分だけは純粋関数に切り出してあるので、ここで検証できる。
    ///
    /// 守りたいのは「実体ディレクトリを正常と誤判定しないこと」。
    /// 2026-08-07 の事故は、まさに実体ディレクトリになっていたクローンが
    /// 「作成成功」に見えていたことが原因だった。
    /// </summary>
    public class ParrelSyncCloneValidatorTests
    {
        [Test]
        public void リンクなら正常と判定する()
        {
            Assert.AreEqual(
                ParrelSyncCloneValidator.FolderStatus.Linked,
                ParrelSyncCloneValidator.Evaluate(exists: true, isReparsePoint: true));
        }

        [Test]
        public void 実体ディレクトリは異常と判定する()
        {
            // ParrelSync の LinkFolders() がリンクをスキップした状態。
            // 存在はするので Directory.Exists では気付けない ＝ この判定が唯一の検出手段。
            Assert.AreEqual(
                ParrelSyncCloneValidator.FolderStatus.RealDirectory,
                ParrelSyncCloneValidator.Evaluate(exists: true, isReparsePoint: false));
        }

        [Test]
        public void 存在しなければMissingと判定する()
        {
            Assert.AreEqual(
                ParrelSyncCloneValidator.FolderStatus.Missing,
                ParrelSyncCloneValidator.Evaluate(exists: false, isReparsePoint: false));
        }

        [Test]
        public void リンク切れはMissingと判定する()
        {
            // リンク切れの場合 Directory.Exists は false を返すが、
            // ReparsePoint 属性自体は読めることがある。存在しない側を優先する。
            Assert.AreEqual(
                ParrelSyncCloneValidator.FolderStatus.Missing,
                ParrelSyncCloneValidator.Evaluate(exists: false, isReparsePoint: true));
        }
    }
}
