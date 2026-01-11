using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.VisualScripting;
using UnityEngine;
using static OpcReader;

public class OpcReader : MonoBehaviour
{
    [Header("Setting")]
    [SerializeField]
    private string _filePath = "Assets/sample.opc";

    [SerializeField]
    private float _maxPointsPerPixel = 1.0f;

    [Header("Debug")]
    [SerializeField]
    private bool _drawVisibleGizmos = false;

    [SerializeField]
    private bool _drawLodGizmos = false;

    [NonSerialized]
    private BinaryReader _reader;

    private Header _header;
    private List<Branch> _branches = new List<Branch>();
    private List<Leaf> _leaves = new List<Leaf>();
    private long _bodyStart = -1;
    private int _rootBranchIndex = -1;
    private Camera _mainCamera;
    private List<int> _visibleLeafIndices = new List<int>();
    private Queue<int> _branchIndicesToCheck = new Queue<int>();
    private List<LoadLeaf> _loadLeafs = new List<LoadLeaf>();



    private void Start()
    {
        _mainCamera = Camera.main;
        _reader = OpenFile(_filePath);
        _header = ReadHeader(_reader);
        _branches = ReadBranches(_reader, _header);
        _leaves = ReadLeaves(_reader, _header);
        _bodyStart = GetBodyStart();
        _rootBranchIndex = GetRootBranchIndex(_branches);
        BuildReference(_branches, _leaves);
    }

    private BinaryReader OpenFile(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        string fullPath = Path.Combine(Application.dataPath, shortFilePath);
        return new BinaryReader(File.OpenRead(fullPath));
    }

    private Header ReadHeader(BinaryReader reader)
    {
        // OPCファイルかをチェック
        byte[] magic = reader.ReadBytes(4);
        byte[] expected = { (byte)'O', (byte)'P', (byte)'C', 0x00 };
        if (!magic.SequenceEqual(expected))
        {
            return null;
        }

        Header header = new Header();

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
        header.branchCount = reader.ReadUInt32();
        header.leafCount = reader.ReadUInt32();
        header.totalPoints = reader.ReadUInt64();
        header.pointStrideBytes = reader.ReadUInt16();

        return header;
    }

    private List<Branch> ReadBranches(BinaryReader reader, Header header)
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

            branches.Add(branch);
        }

        return branches;
    }

    private List<Leaf> ReadLeaves(BinaryReader reader, Header header)
    {
        var leaves = new List<Leaf>((int)header.leafCount);

        for (int i = 0; i < header.leafCount; i++)
        {
            var leaf = new Leaf();

            leaf.depth = reader.ReadByte();
            leaf.path = reader.ReadUInt64();

            Vector3 min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 center = (min + max) * 0.5f;
            Vector3 size = max - min;
            leaf.bounds = new Bounds(center, size);

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

    private long GetBodyStart()
    {
        return _reader.BaseStream.Position;
    }

    private int GetRootBranchIndex(List<Branch> branches)
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

    private void BuildReference(List<Branch> branches, List<Leaf> leaves)
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
                else
                {

                }

                branch.references[child] = nodeRef;
            }
        }
    }



    private void Update()
    {
        // 視界に入っているか
        GetVisibleLeafIndices();

        // LODを選択
        ChooseLeafLod();
    }

    private void GetVisibleLeafIndices()
    {
        // 初期化
        _visibleLeafIndices.Clear();
        _branchIndicesToCheck.Clear();

        // 可視ボックス
        Plane[] planes = GeometryUtility.CalculateFrustumPlanes(_mainCamera);

        // ルートブランチを追加
        _branchIndicesToCheck.Enqueue(_rootBranchIndex);

        // 判定ループ
        while (_branchIndicesToCheck.Count > 0)
        {
            // 判定対象の枝を取得
            int branchIndex = _branchIndicesToCheck.Dequeue();
            Branch branch = _branches[branchIndex];

            // 枝が見えているか
            if (!GeometryUtility.TestPlanesAABB(planes, branch.bounds))
            {
                // 子の判定をスキップ
                continue;
            }

            // 見えている子のノードを処理
            for (int i = 0; i < branch.references.Length; i++)
            {
                NodeReference nodeRef = branch.references[i];

                // 存在するか
                if (!nodeRef.IsValid) continue;

                // ノードは葉か
                if (nodeRef.isLeaf)
                {
                    // 見える場合は返すリストに追加
                    Leaf leaf = _leaves[nodeRef.index];
                    if (GeometryUtility.TestPlanesAABB(planes, leaf.bounds))
                    {
                        _visibleLeafIndices.Add(nodeRef.index);
                    }
                }
                else
                {
                    // 判定処理として追加
                    _branchIndicesToCheck.Enqueue(nodeRef.index);
                }
            }
        }
    }

    private void ChooseLeafLod()
    {
        _loadLeafs.Clear();

        foreach (int leafIndex in _visibleLeafIndices)
        {
            int lod = ChooseLod(_leaves[leafIndex]);
            var loadLeaf = new LoadLeaf { leafIndex = leafIndex, lod = lod };
            _loadLeafs.Add(loadLeaf);
        }
    }

    private int ChooseLod(Leaf leaf)
    {
        Bounds bounds = leaf.bounds;

        // Bounds円の半径を取得
        float r = bounds.extents.magnitude;

        // Bounds円の半径をピクセル数で取得
        Vector3 center = bounds.center;
        Vector3 sc = _mainCamera.WorldToScreenPoint(center);
        Vector3 sx = _mainCamera.WorldToScreenPoint(center + _mainCamera.transform.right * r);
        Vector3 sy = _mainCamera.WorldToScreenPoint(center + _mainCamera.transform.up * r);
        float rPxX = (sx - sc).magnitude;
        float rPxY = (sy - sc).magnitude;
        float rPx = Mathf.Max(rPxX, rPxY);

        // Bounds円の面積をピクセル数で取得
        float areaPx = Mathf.Max(1f, Mathf.PI * rPx * rPx);

        // 閾値より低い場合にLODを返す
        for (int i = 0; i < leaf.lodCounts.Length; i++)
        {
            uint count = leaf.lodCounts[i];
            float density = count / areaPx;
            if (density <= _maxPointsPerPixel) return i;
        }

        // 最も粗いLODを返す
        return leaf.lodCounts.Length - 1;
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

    private void OnDrawGizmos()
    {
        DrawVisibleGizmos();
        DrawLodGizmos();
    }

    private void DrawVisibleGizmos()
    {
        if (!_drawVisibleGizmos) return;

        foreach (int leafIndex in _visibleLeafIndices)
        {
            Leaf leaf = _leaves[leafIndex];

            Vector3 center = leaf.bounds.center;
            Vector3 size = leaf.bounds.size;

            float t = Mathf.InverseLerp(0, 10, leaf.depth);

            Color color = Color.HSVToRGB(t, 1f, 1f);

            color.a = 0.7f;

            Gizmos.color = color;
            Gizmos.DrawCube(center, size);
        }
    }

    private void DrawLodGizmos()
    {
        if (!_drawLodGizmos) return;

        foreach (var loadLeaf in _loadLeafs)
        {
            Leaf leaf = _leaves[loadLeaf.leafIndex];

            Vector3 center = leaf.bounds.center;
            Vector3 size = leaf.bounds.size;

            int maxLodIndex = _header.lodCount - 1;
            float t = (maxLodIndex == 0) ? 0f : (loadLeaf.lod / (float)maxLodIndex);

            Color color = Color.HSVToRGB(t, 1f, 1f);

            color.a = 0.7f;

            Gizmos.color = color;
            Gizmos.DrawCube(center, size);
        }
    }



    [Serializable]
    public class Header
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
        public uint branchCount;
        public ulong totalPoints;
        public ushort pointStrideBytes;
    }

    [Serializable]
    public class Branch
    {
        public byte depth;
        public ulong path;

        public Bounds bounds;

        public NodeReference[] references = new NodeReference[8];
    }

    [Serializable]
    public class Leaf
    {
        public byte depth;
        public ulong path;

        public Bounds bounds;

        public ulong baseOffset;

        public uint[] lodCounts;
    }

    [Serializable]
    public struct NodeReference
    {
        public bool isLeaf;

        public int index;

        public bool IsValid => index >= 0;
    }

    [Serializable]
    public struct LoadLeaf
    {
        public int leafIndex;
        public int lod;
    }
}