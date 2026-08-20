using Rebaka.Utils;
using NUnit.Framework;

namespace Rebaka.Tests.EditMode
{
    /// <summary>
    /// 安定クライアント ID のハッシュ部分の検証。
    ///
    /// ここで守りたい性質は「同じ seed からは、いつ・どのプロセスで呼んでも同じ値が出る」こと。
    /// これが崩れると再接続時に別人と判定され、スポーンスロットの復帰が働かなくなる。
    /// string.GetHashCode() を使わず FNV-1a を明示実装しているのはこのため。
    /// </summary>
    public class StableClientIdTests
    {
        [Test]
        public void Fnv1a64_同じ入力なら常に同じ値を返す()
        {
            const string seed = "0123456789abcdef0123456789abcdef";

            long first = StableClientId.Fnv1a64(seed);
            long second = StableClientId.Fnv1a64(seed);

            Assert.AreEqual(first, second);
        }

        [Test]
        public void Fnv1a64_既知のベクタと一致する()
        {
            // FNV-1a 64bit の公表テストベクタ。実装を差し替えたときに
            // 「動くが値が変わる」変更を検出する（＝既存プレイヤーの ID が全て変わる事故）。
            // offset basis 14695981039346656037 / prime 1099511628211
            Assert.AreEqual(unchecked((long)0xcbf29ce484222325UL), StableClientId.Fnv1a64(""));
            Assert.AreEqual(unchecked((long)0xaf63dc4c8601ec8cUL), StableClientId.Fnv1a64("a"));
            Assert.AreEqual(unchecked((long)0x85944171f73967e8UL), StableClientId.Fnv1a64("foobar"));
        }

        [Test]
        public void Fnv1a64_異なる入力は異なる値になる()
        {
            // ParrelSync の本体とクローンは dataPath だけが違う。
            // 末尾の差分がきちんと効くことを確認する。
            long main = StableClientId.Fnv1a64("seed@C:/Projects/REBAKA_Fusion2/Assets");
            long clone0 = StableClientId.Fnv1a64("seed@C:/Projects/REBAKA_Fusion2_clone_0/Assets");
            long clone1 = StableClientId.Fnv1a64("seed@C:/Projects/REBAKA_Fusion2_clone_1/Assets");

            Assert.AreNotEqual(main, clone0);
            Assert.AreNotEqual(main, clone1);
            Assert.AreNotEqual(clone0, clone1);
        }

        [Test]
        public void Get_ゼロを返さない()
        {
            // 0 は Fusion の PlayerUniqueId の「未設定」既定値なので、
            // 偶然 0 になった場合でも返してはいけない。
            Assert.AreNotEqual(0L, StableClientId.Get());
        }

        [Test]
        public void Get_同一プロセス内で呼び出しごとに変わらない()
        {
            Assert.AreEqual(StableClientId.Get(), StableClientId.Get());
        }
    }
}
