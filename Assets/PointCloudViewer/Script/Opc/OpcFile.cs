using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// OPCファイルのヘッダとノード索引を読み込んで保持するフォーマット層。
/// 点データ本体は読まない（点データのストリーミングはワーカーが担当する）。
/// ファイル上は枝（代表点）と葉（実点）に分かれているが、ストリーミング・描画にとっては
/// どちらも「オフセット+点数+bounds」で同じ扱いができるため、統合した Node 配列として公開する。
/// </summary>
public class OpcFile
{
    public const ushort PointStrideRgb = 9;

    public Header FileHeader { get; private set; }

    // 統合ノード配列（先頭に枝、後ろに葉を格納する）
    public Node[] Nodes { get; private set; }

    // 枝ごとの子参照テーブル（枝1つにつき8スロット。-1は子なし）
    public int[] ChildIndices { get; private set; }
    public int[] ParentIndices { get; private set; }

    // 点データ本体のファイル先頭からの開始位置
    public long BodyStart { get; private set; }

    // ルートノード（depth==0の枝）のインデックス
    public int RootIndex { get; private set; }

    // 全ノード中の最大点数（読み込みバッファのサイズ上限決定用）
    public int MaxNodePointCount { get; private set; }

    // 全ノードの点数合計（枝の代表点+葉の実点。点プール容量の上限決定用）
    public long TotalNodePoints { get; private set; }

    // 生成は TryOpen 経由のみ（検証を通らないインスタンスを作らせない）
    private OpcFile() { }

    /// <summary>
    /// OPCファイルを開いてヘッダとノード索引を読み込む。
    /// 失敗時は false を返し、error に理由を入れる（例外は投げない）。
    /// </summary>
    public static bool TryOpen(string fullPath, out OpcFile file, out string error)
    {
        file = null;

        // 存在チェック（開く前に分かる失敗は明確なメッセージで返す）
        if (!File.Exists(fullPath))
        {
            error = $"ファイルが見つかりません: {fullPath}";
            return false;
        }

        try
        {
            using (var reader = new BinaryReader(File.OpenRead(fullPath)))
            {
                // マジックチェック（OPCファイルでなければここで弾く）
                byte[] magic = reader.ReadBytes(4);
                byte[] expected = { (byte)'O', (byte)'P', (byte)'C', 0x00 };
                if (magic.Length != 4 || !magic.SequenceEqual(expected))
                {
                    error = $"OPCファイルではありません（マジック不一致）: {fullPath}";
                    return false;
                }

                var result = new OpcFile();
                result.FileHeader = ReadHeader(reader);
                if (!IsSupportedPointStride(result.FileHeader.pointStrideBytes))
                {
                    error = $"未対応のOPC点データstrideです: {result.FileHeader.pointStrideBytes} bytes/point（対応: 9=RGB）";
                    return false;
                }

                // 枝→葉の順でレコードを読み、統合ノード配列に詰める（パスは子参照構築にだけ使う一時データ）
                int branchCount = (int)result.FileHeader.branchCount;
                int leafCount = (int)result.FileHeader.leafCount;
                result.Nodes = new Node[branchCount + leafCount];
                ulong[] paths = new ulong[branchCount + leafCount];
                ReadBranches(reader, result.Nodes, paths, branchCount);
                ReadLeaves(reader, result.Nodes, paths, branchCount, leafCount);

                result.BodyStart = reader.BaseStream.Position;
                result.ChildIndices = BuildChildIndices(result.Nodes, paths, branchCount);
                result.ParentIndices = BuildParentIndices(paths);
                result.RootIndex = FindRootIndex(result.Nodes, branchCount);
                ComputePointStats(result);

                // ルートが無い索引は走査できないのでエラー扱い
                if (result.RootIndex < 0)
                {
                    error = $"ルート枝（depth=0）が見つかりません: {fullPath}";
                    return false;
                }

                file = result;
                error = null;
                return true;
            }
        }
        catch (EndOfStreamException)
        {
            error = $"ファイルが途中で終わっています（ヘッダ/索引の読み込み中にEOF）: {fullPath}";
            return false;
        }
        catch (IOException e)
        {
            error = $"ファイルの読み込みに失敗しました: {e.Message}";
            return false;
        }
    }

    private static Header ReadHeader(BinaryReader reader)
    {
        Header header = new Header();

        // Version
        header.versionMajor = reader.ReadByte();
        header.versionMinor = reader.ReadByte();
        header.versionPatch = reader.ReadByte();

        // Bound
        header.size = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        header.rootMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        header.rootMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        // Stats
        header.branchCount = reader.ReadUInt32();
        header.leafCount = reader.ReadUInt32();
        header.totalPoints = reader.ReadUInt64();
        header.pointStrideBytes = reader.ReadUInt16();

        return header;
    }

    public static bool IsSupportedPointStride(ushort pointStrideBytes)
    {
        return pointStrideBytes == PointStrideRgb;
    }

    // 枝レコードを統合配列の先頭側（0〜branchCount-1）に読み込む
    private static void ReadBranches(BinaryReader reader, Node[] nodes, ulong[] paths, int branchCount)
    {
        for (int i = 0; i < branchCount; i++)
        {
            Node node = new Node();
            node.isLeaf = false;

            node.depth = reader.ReadByte();
            paths[i] = reader.ReadUInt64();

            Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            node.bounds = new Bounds(center, size);

            // 代表点の先頭オフセット
            node.baseOffset = reader.ReadUInt64();

            // 代表点の点数
            node.pointCount = reader.ReadUInt32();

            // 子参照テーブル内の自分のスロット先頭
            node.childStart = i * 8;

            nodes[i] = node;
        }
    }

    // 葉レコードを統合配列の後ろ側（branchCount〜）に読み込む
    private static void ReadLeaves(BinaryReader reader, Node[] nodes, ulong[] paths, int branchCount, int leafCount)
    {
        for (int i = 0; i < leafCount; i++)
        {
            Node node = new Node();
            node.isLeaf = true;

            // 葉レコード先頭の未使用フィールド（読み飛ばし）
            reader.ReadUInt32();

            node.depth = reader.ReadByte();
            paths[branchCount + i] = reader.ReadUInt64();

            Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            node.bounds = new Bounds(center, size);

            node.baseOffset = reader.ReadUInt64();

            node.pointCount = reader.ReadUInt32();

            // 葉に子は無い
            node.childStart = -1;

            nodes[branchCount + i] = node;
        }
    }

    // 各枝の8子スロットに統合配列内のノードインデックスを構築する
    private static int[] BuildChildIndices(Node[] nodes, ulong[] paths, int branchCount)
    {
        // パスとインデックスの辞書を作成（枝と葉でパスが衝突しうるため辞書は分け、枝優先で引く）
        var branchIndexByPath = new Dictionary<ulong, int>(branchCount);
        var leafIndexByPath = new Dictionary<ulong, int>(nodes.Length - branchCount);
        for (int i = 0; i < branchCount; i++)
        {
            branchIndexByPath[paths[i]] = i;
        }
        for (int i = branchCount; i < nodes.Length; i++)
        {
            leafIndexByPath[paths[i]] = i;
        }

        // 枝ごとに子8方向のパスを引いて参照を構築
        int[] childIndices = new int[branchCount * 8];
        for (int i = 0; i < branchCount; i++)
        {
            for (int child = 0; child < 8; child++)
            {
                ulong childPath = (paths[i] << 3) | (ulong)(uint)child;

                int childIndex = -1;
                if (branchIndexByPath.TryGetValue(childPath, out int bIndex))
                {
                    childIndex = bIndex;
                }
                else if (leafIndexByPath.TryGetValue(childPath, out int lIndex))
                {
                    childIndex = lIndex;
                }

                childIndices[i * 8 + child] = childIndex;
            }
        }

        return childIndices;
    }

    private static int[] BuildParentIndices(ulong[] paths)
    {
        var indexByPath = new Dictionary<ulong, int>(paths.Length);
        for (int i = 0; i < paths.Length; i++)
        {
            indexByPath[paths[i]] = i;
        }

        int[] parentIndices = new int[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i] == 0)
            {
                parentIndices[i] = -1;
                continue;
            }

            ulong parentPath = paths[i] >> 3;
            parentIndices[i] = indexByPath.TryGetValue(parentPath, out int parentIndex) ? parentIndex : -1;
        }

        return parentIndices;
    }

    private static int FindRootIndex(Node[] nodes, int branchCount)
    {
        for (int i = 0; i < branchCount; i++)
        {
            if (nodes[i].depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    // 最大点数と点数合計を集計する（読み込みバッファ・点プールのサイズ決定用）
    private static void ComputePointStats(OpcFile file)
    {
        int maxCount = 0;
        long totalCount = 0;
        foreach (Node node in file.Nodes)
        {
            maxCount = Math.Max(maxCount, (int)node.pointCount);
            totalCount += node.pointCount;
        }
        file.MaxNodePointCount = maxCount;
        file.TotalNodePoints = totalCount;
    }



    public class Header
    {
        // Version
        public byte versionMajor;
        public byte versionMinor;
        public byte versionPatch;

        // Bound
        public Vector3 size;
        public Vector3 rootMin;
        public Vector3 rootMax;

        // Status
        public uint leafCount;
        public uint branchCount;
        public ulong totalPoints;
        public ushort pointStrideBytes;
    }

    /// <summary>
    /// 統合ノード。枝は代表点、葉は実点を指すが、どちらも「オフセット+点数+bounds」として同じ扱いができる。
    /// </summary>
    public struct Node
    {
        public byte depth;
        public bool isLeaf;

        public Bounds bounds;

        // 点データの先頭オフセット（点単位。枝=代表点、葉=実点）
        public ulong baseOffset;
        public uint pointCount;

        // ChildIndices内の自分の子スロット先頭（枝のみ。葉は-1）
        public int childStart;
    }
}
