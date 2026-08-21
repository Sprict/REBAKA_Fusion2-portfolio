using System;
using System.Text;
using UnityEngine;

namespace Rebaka.Utils
{
    /// <summary>
    /// 再接続しても同一プレイヤーとして扱われるための、端末単位で安定した ID を供給する。
    ///
    /// 背景:
    /// Fusion の <c>PlayerRef.PlayerId</c> は切断→再接続で**新しい値**が割り当てられる。
    /// そのため PlayerId を鍵にした再接続判定（SpawnPointManager のスロット復帰など）は
    /// 構造上ヒットしない。実測では 2 で参加したクライアントが再参加時に 4 になった。
    ///
    /// 対処:
    /// ここで作った安定 ID を <c>StartGameArgs.PlayerUniqueId</c> へ渡すと、Fusion が
    /// 再接続時も同じ <c>PlayerRef</c> を割り当てる。Fusion 同梱ドキュメントの記述:
    ///   "Unique identifier for the player in a Session.
    ///    Allows the same player to join a Session multiple times with the same PlayerRef."
    ///   （Assets/Photon/Fusion/Assemblies/Fusion.Runtime.xml の F:Fusion.StartGameArgs.PlayerUniqueId）
    /// これにより SpawnPointManager 側は一行も変えずに再接続判定が機能する。
    /// </summary>
    public static class StableClientId
    {
        private const string PrefsKey = "REBAKA.StableClientId";

        /// <summary>
        /// この端末（Editor ではこのプロジェクトディレクトリ）に固有の、再起動をまたいで不変な ID。
        /// 0 は Fusion 側の「未設定」既定値なので返さない。
        /// </summary>
        public static long Get()
        {
            string seed = GetOrCreatePersistentSeed();

#if UNITY_EDITOR
            // ParrelSync のクローンは ProjectSettings をシンボリックリンクで共有するため、
            // companyName/productName が本体と同一 → PlayerPrefs の保存先レジストリキー
            // (HKCU\Software\DefaultCompany\REBAKA_Fusion2) も共有される。
            // seed だけだと本体・clone_0・clone_1 が同じ ID になり、2-client 検証が成立しない。
            //
            // Application.dataPath はクローンごとに異なる。Assets がシンボリックリンクでも
            // リンク先へ解決されないことは、同じ前提で動いている
            // ParrelSyncInputUtil.IsCloneProject()（dataPath から .clone マーカーを探す）が
            // 実際に機能していることで裏付けられる。
            //
            // ビルドには含めない。インストール先を移動しただけで別人扱いになるのを避けるため、
            // 本番の同一性は PlayerPrefs の seed だけで決める。
            seed += "@" + Application.dataPath;
#endif

            long id = Fnv1a64(seed);
            return id == 0L ? 1L : id;
        }

        /// <summary>
        /// PlayerPrefs に永続化した GUID。初回呼び出し時に生成する。
        /// </summary>
        private static string GetOrCreatePersistentSeed()
        {
            string stored = PlayerPrefs.GetString(PrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(stored))
            {
                return stored;
            }

            string created = Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(PrefsKey, created);
            PlayerPrefs.Save();
            return created;
        }

        /// <summary>
        /// FNV-1a 64bit。<c>string.GetHashCode()</c> はプロセスごとに値が変わりうる
        /// （.NET のハッシュランダム化）ため、永続 ID の導出には使えない。
        /// 実装を固定しておけば、同じ seed からは常に同じ ID が得られる。
        /// </summary>
        internal static long Fnv1a64(string value)
        {
            const ulong offsetBasis = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            ulong hash = offsetBasis;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= prime;
            }

            return unchecked((long)hash);
        }
    }
}
