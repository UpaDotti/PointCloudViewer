using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public class OpcReader : MonoBehaviour
{
    // 設定
    [SerializeField]
    private string _filePath = "Assets/sample.opc";

    // 状態
    [NonSerialized]
    private BinaryReader _reader;

    [SerializeField]
    private OpcHeader _header;

    [SerializeField]
    private List<OpcLeaf> _leaves;



    private void Start()
    {
        _reader = OpenFile(_filePath);
        _header = ReadHeader(_reader);
        _leaves = ReadLeaves(_reader, _header);
    }

    private BinaryReader OpenFile(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        string fullPath = Path.Combine(Application.dataPath, shortFilePath);
        return new BinaryReader(File.OpenRead(fullPath));
    }

    private OpcHeader ReadHeader(BinaryReader reader)
    {
        // OPCファイルかをチェック
        byte[] magic = reader.ReadBytes(4);
        byte[] expected = { (byte)'O', (byte)'P', (byte)'C', 0x00 };
        if (!magic.SequenceEqual(expected))
        {
            return null;
        }

        OpcHeader header = new OpcHeader();

        // Version
        header.versionMajor = reader.ReadByte();
        header.versionMinor = reader.ReadByte();
        header.versionPatch = reader.ReadByte();

        // Bound
        header.size = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        header.rootMin = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        header.rootMax = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        // LOD
        header.lodCount = reader.ReadByte();
        header.lodSteps = new uint[header.lodCount];
        for (int i = 0; i < header.lodCount; i++)
        {
            header.lodSteps[i] = reader.ReadUInt32();
        }

        // Stats
        header.leafCount = reader.ReadUInt32();
        header.totalPoints = reader.ReadUInt64();

        return header;
    }

    private List<OpcLeaf> ReadLeaves(BinaryReader reader, OpcHeader header)
    {
        var leaves = new List<OpcLeaf>((int)header.leafCount);

        for (int i = 0; i < header.leafCount; i++)
        {
            var leaf = new OpcLeaf();

            leaf.depth = reader.ReadByte();
            leaf.path = reader.ReadUInt64();

            leaf.min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            leaf.max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            leaf.baseOffset = reader.ReadUInt64();

            leaf.lodCounts = new uint[header.lodCount];
            for (int lod = 0; lod < header.lodCount; lod++)
            {
                leaf.lodCounts[lod] = reader.ReadUInt32();
            }

            leaves.Add(leaf);
        }

        return leaves;
    }

    private void OnDisable()
    {
        CloseReader();
    }

    private void OnDestroy()
    {
        CloseReader();
    }

    private void CloseReader()
    {
        if (_reader != null)
        {
            _reader.Close();
            _reader.Dispose();
            _reader = null;
        }
    }
}

[Serializable]
public class OpcHeader
{
    [Header("Vsersion")]
    public byte versionMajor;
    public byte versionMinor;
    public byte versionPatch;

    [Header("Bound")]
    public Vector3 size;
    public Vector3 rootMin;
    public Vector3 rootMax;

    [Header("LOD")]
    public byte lodCount;
    public uint[] lodSteps;

    [Header("Status")]
    public uint leafCount;
    public ulong totalPoints;
}

[Serializable]
public class OpcLeaf
{
    public byte depth;
    public ulong path; // 10進法

    public Vector3 min;
    public Vector3 max;

    public ulong baseOffset;

    public uint[] lodCounts;
}