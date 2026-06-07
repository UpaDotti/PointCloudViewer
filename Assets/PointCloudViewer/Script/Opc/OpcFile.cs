using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// OPCファイルのヘッダとノード索引を読み込んで保持するフォーマット層。
/// 点データ本体は読まない（点データのストリーミングはワーカーが担当する）。
/// </summary>
public class OpcFile
{
    public Header FileHeader { get; private set; }
    public List<Branch> Branches { get; private set; }
    public List<Leaf> Leaves { get; private set; }

    // 点データ本体のファイル先頭からの開始位置
    public long BodyStart { get; private set; }

    // ルート枝（depth==0）のインデックス。見つからなければ -1。
    public int RootBranchIndex { get; private set; }

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
                result.Branches = ReadBranches(reader, result.FileHeader);
                result.Leaves = ReadLeaves(reader, result.FileHeader);
                result.BodyStart = reader.BaseStream.Position;
                result.RootBranchIndex = FindRootBranchIndex(result.Branches);
                BuildReference(result.Branches, result.Leaves);

                // ルートが無い索引は走査できないのでエラー扱い
                if (result.RootBranchIndex < 0)
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

    private static List<Branch> ReadBranches(BinaryReader reader, Header header)
    {
        var branches = new List<Branch>((int)header.branchCount);

        for (int i = 0; i < header.branchCount; i++)
        {
            var branch = new Branch();

            branch.depth = reader.ReadByte();
            branch.path = reader.ReadUInt64();

            Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            branch.bounds = new Bounds(center, size);

            // 代表点の先頭オフセット
            branch.baseOffset = reader.ReadUInt64();

            // 代表点の点数
            branch.pointCount = reader.ReadUInt32();

            branches.Add(branch);
        }

        return branches;
    }

    private static List<Leaf> ReadLeaves(BinaryReader reader, Header header)
    {
        var leaves = new List<Leaf>((int)header.leafCount);

        for (int i = 0; i < header.leafCount; i++)
        {
            var leaf = new Leaf();

            // 葉レコード先頭の未使用フィールド（読み飛ばし）
            reader.ReadUInt32();

            leaf.depth = reader.ReadByte();
            leaf.path = reader.ReadUInt64();

            Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            leaf.bounds = new Bounds(center, size);

            leaf.baseOffset = reader.ReadUInt64();

            leaf.pointCount = reader.ReadUInt32();

            leaves.Add(leaf);
        }

        return leaves;
    }

    private static int FindRootBranchIndex(List<Branch> branches)
    {
        for (int i = 0; i < branches.Count; i++)
        {
            if (branches[i].depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    // 各枝の8子スロットに枝/葉への参照を構築する
    private static void BuildReference(List<Branch> branches, List<Leaf> leaves)
    {
        // パスとインデックスの辞書を作成
        var branchIndexByPath = new Dictionary<ulong, int>(branches.Count);
        var leafIndexByPath = new Dictionary<ulong, int>(leaves.Count);
        for (int i = 0; i < branches.Count; i++)
        {
            branchIndexByPath[branches[i].path] = i;
        }
        for (int i = 0; i < leaves.Count; i++)
        {
            leafIndexByPath[leaves[i].path] = i;
        }

        // 関係を構築
        for (int i = 0; i < branches.Count; i++)
        {
            Branch branch = branches[i];

            for (int child = 0; child < 8; child++)
            {
                ulong childPath = (branch.path << 3) | (ulong)(uint)child;

                NodeReference nodeRef = new NodeReference { index = -1, isLeaf = false };

                if (branchIndexByPath.TryGetValue(childPath, out int bIndex))
                {
                    nodeRef.isLeaf = false;
                    nodeRef.index = bIndex;
                }
                else if (leafIndexByPath.TryGetValue(childPath, out int lIndex))
                {
                    nodeRef.isLeaf = true;
                    nodeRef.index = lIndex;
                }

                branch.references[child] = nodeRef;
            }
        }
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

    public class Branch
    {
        public byte depth;
        public ulong path;

        public Bounds bounds;

        // 枝の代表点用
        public ulong baseOffset;
        public uint pointCount;

        public NodeReference[] references = new NodeReference[8];
    }

    public class Leaf
    {
        public byte depth;
        public ulong path;

        public Bounds bounds;

        public ulong baseOffset;

        public uint pointCount;
    }

    public struct NodeReference
    {
        public bool isLeaf;

        public int index;

        public bool IsValid => index >= 0;
    }
}
