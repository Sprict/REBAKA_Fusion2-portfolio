// Assets/Code/Scripts/Map/MapManifest.cs
using System.Collections.Generic;
using UnityEngine;

namespace MyFolder.Scripts.Map
{
    /// <summary>
    /// ホストが確定したレイアウトを配布するための最小 payload（devlog 2026-06-27 §6 / §9 / E2）。
    ///
    /// 段階 A ではプレーンなシリアライズ可能データとして実装し、段階 C で固定長フィールドの
    /// [Networked] 構造体へ写す素体にする。流れるのはこの manifest（数百バイト）だけで、
    /// 地形そのものは非ネットワークの各クライアントローカル Instantiate（§1, §10）。
    ///
    /// checksum はカタログのモジュール Id 文字列まで含めて取るため、host/client のカタログ不一致
    /// （モジュール定義のズレ）を検出できる（§9「prefab checksum 不一致なら参加拒否」の論理版）。
    /// </summary>
    [System.Serializable]
    public sealed class MapManifest
    {
        /// <summary>マニフェスト形式のバージョン。互換性破壊時に上げる。</summary>
        public int Version;

        /// <summary>生成 seed（デバッグ・再生成検証用に保持）。</summary>
        public ulong Seed;

        /// <summary>配置モジュールのカタログ index 列。</summary>
        public int[] ModuleIndices;

        /// <summary>配置原点セル列（ModuleIndices と同順・同長）。</summary>
        public Vector3Int[] Origins;

        /// <summary>回転ステップ列（0..3、ModuleIndices と同順・同長）。</summary>
        public int[] Rotations;

        /// <summary>配置＋カタログ Id から計算した FNV-1a チェックサム。</summary>
        public uint Checksum;

        public const int CurrentVersion = 1;

        public int ModuleCount => ModuleIndices?.Length ?? 0;

        /// <summary>確定レイアウトから配布用マニフェストを作る（ホスト側）。</summary>
        public static MapManifest FromLayout(MapLayout layout)
        {
            int n = layout.Count;
            var manifest = new MapManifest
            {
                Version = CurrentVersion,
                Seed = layout.Seed,
                ModuleIndices = new int[n],
                Origins = new Vector3Int[n],
                Rotations = new int[n],
            };

            for (int i = 0; i < n; i++)
            {
                PlacedModule m = layout.Modules[i];
                manifest.ModuleIndices[i] = m.ModuleIndex;
                manifest.Origins[i] = m.OriginCell;
                manifest.Rotations[i] = m.RotationSteps;
            }

            manifest.Checksum = manifest.ComputeChecksum(layout.Catalog);
            return manifest;
        }

        /// <summary>
        /// 受信側でカタログを使ってレイアウトを再構築する（クライアント側）。
        /// バージョン不一致・配列長不整合・index 範囲外・checksum 不一致なら false を返し、参加拒否に使える。
        /// </summary>
        public bool TryRebuild(ModuleCatalog catalog, out MapLayout layout)
        {
            layout = null;
            if (catalog == null) return false;
            if (Version != CurrentVersion) return false;
            if (ModuleIndices == null || Origins == null || Rotations == null) return false;

            int n = ModuleIndices.Length;
            if (Origins.Length != n || Rotations.Length != n) return false;

            for (int i = 0; i < n; i++)
            {
                if (ModuleIndices[i] < 0 || ModuleIndices[i] >= catalog.Count)
                    return false;
            }

            if (ComputeChecksum(catalog) != Checksum)
                return false;

            var rebuilt = new MapLayout(Seed, catalog);
            for (int i = 0; i < n; i++)
            {
                rebuilt.Add(new PlacedModule(ModuleIndices[i], Origins[i], Rotations[i]));
            }
            layout = rebuilt;
            return true;
        }

        /// <summary>
        /// FNV-1a (32bit)。version → seed → 各配置(モジュール Id 文字列, origin, rotation) の順に混ぜる。
        /// カタログ Id を含めることで、index は同じでも中身が違うカタログを弾ける。
        /// </summary>
        public uint ComputeChecksum(ModuleCatalog catalog)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint hash = offset;

            hash = MixInt(hash, prime, Version);
            hash = MixULong(hash, prime, Seed);

            int n = ModuleIndices?.Length ?? 0;
            hash = MixInt(hash, prime, n);

            for (int i = 0; i < n; i++)
            {
                int idx = ModuleIndices[i];
                if (idx >= 0 && idx < catalog.Count)
                    hash = MixString(hash, prime, catalog[idx].Id);
                hash = MixInt(hash, prime, idx);
                hash = MixInt(hash, prime, Origins[i].x);
                hash = MixInt(hash, prime, Origins[i].y);
                hash = MixInt(hash, prime, Origins[i].z);
                hash = MixInt(hash, prime, Rotations[i]);
            }
            return hash;
        }

        private static uint MixInt(uint hash, uint prime, int value)
        {
            uint v = unchecked((uint)value);
            for (int b = 0; b < 4; b++)
            {
                hash ^= (v >> (b * 8)) & 0xFF;
                hash *= prime;
            }
            return hash;
        }

        private static uint MixULong(uint hash, uint prime, ulong value)
        {
            for (int b = 0; b < 8; b++)
            {
                hash ^= (uint)((value >> (b * 8)) & 0xFF);
                hash *= prime;
            }
            return hash;
        }

        private static uint MixString(uint hash, uint prime, string s)
        {
            if (s == null) return hash;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                hash ^= (uint)(c & 0xFF);
                hash *= prime;
                hash ^= (uint)((c >> 8) & 0xFF);
                hash *= prime;
            }
            return hash;
        }
    }
}
