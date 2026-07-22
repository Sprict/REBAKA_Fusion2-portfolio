using System;

namespace UnusedAssetFinder.Editor
{
    /// <summary>
    /// スキャン結果の 1 アセットを表す行データ。
    /// UI（チェックボックス選択）と CSV エクスポートの両方で使う。
    /// </summary>
    [Serializable]
    public sealed class UnusedAssetEntry
    {
        /// <summary>"Assets/..." 形式のプロジェクト相対パス。</summary>
        public string Path;

        /// <summary>メインアセットの型名（Texture2D / Material / GameObject など）。表示・ソート用。</summary>
        public string TypeName;

        /// <summary>ファイルサイズ（バイト）。回収できる容量の集計に使う。</summary>
        public long SizeBytes;

        /// <summary>UI でユーザーが削除対象として選択しているか。</summary>
        public bool Selected;

        public UnusedAssetEntry(string path, string typeName, long sizeBytes)
        {
            Path = path;
            TypeName = string.IsNullOrEmpty(typeName) ? "(Unknown)" : typeName;
            SizeBytes = sizeBytes;
            Selected = false;
        }
    }
}
