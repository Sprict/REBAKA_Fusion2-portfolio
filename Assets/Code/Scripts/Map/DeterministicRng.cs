// Assets/Code/Scripts/Map/DeterministicRng.cs
namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// 生成専用の決定論 PRNG（xorshift64*）。
    ///
    /// なぜ自前か:
    /// devlog 2026-06-27 §9 ガード「専用 PRNG を生成専用に分離（UnityEngine.Random のグローバル状態に依存しない）」。
    /// UnityEngine.Random はプロセス全体で共有される静的状態を持ち、フレーム中に他コードが引くと
    /// 生成結果が揺れる。System.Random は実装が .NET ランタイム依存で、プラットフォーム間のビット同一性を
    /// 保証しない。xorshift64* は仕様が公開された固定アルゴリズムで、同一 seed → 同一系列を保証できる。
    ///
    /// E2（ホスト配布）では生成はホストだけが走るが、E1 への将来切替や checksum 照合のため、
    /// 生成は最初から決定論で書いておく。
    /// </summary>
    public sealed class DeterministicRng
    {
        private ulong _state;

        public DeterministicRng(ulong seed)
        {
            // seed=0 だと xorshift が 0 に張り付くため、splitmix64 で 1 段撹拌してから初期化する。
            _state = SplitMix64(seed == 0 ? 0x9E3779B97F4A7C15UL : seed);
            if (_state == 0) _state = 0x9E3779B97F4A7C15UL;
        }

        /// <summary>次の 64bit 乱数。</summary>
        public ulong NextULong()
        {
            ulong x = _state;
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            _state = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        public uint NextUInt()
        {
            return (uint)(NextULong() >> 32);
        }

        /// <summary>0 以上 exclusiveMax 未満の一様整数。exclusiveMax &lt;= 0 なら 0。</summary>
        public int NextInt(int exclusiveMax)
        {
            if (exclusiveMax <= 1) return 0;
            // 剰余バイアスを避けるための棄却サンプリング。
            uint bound = (uint)exclusiveMax;
            uint threshold = (uint)(-(int)bound) % bound; // 2^32 % bound
            while (true)
            {
                uint r = NextUInt();
                if (r >= threshold)
                    return (int)(r % bound);
            }
        }

        /// <summary>minInclusive 以上 maxExclusive 未満の一様整数。</summary>
        public int NextRange(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            return minInclusive + NextInt(maxExclusive - minInclusive);
        }

        private static ulong SplitMix64(ulong z)
        {
            z += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
