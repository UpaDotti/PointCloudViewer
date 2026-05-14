using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

public class OpcLoader : MonoBehaviour
{
    [Header("Setting")]
    [SerializeField]
    private string _filePath = "Assets/sample.opc";

    [SerializeField]
    private Material _pointMaterial;

    [SerializeField]
    private float _pointSize = 1.0f;

    [SerializeField]
    private float _maxPointsPerPixel = 1.0f;

    [SerializeField]
    private int _unloadDelayFrames = 30;

    [Header("Debug")]
    [SerializeField]
    private bool _drawVisibleGizmos = false;

    [SerializeField]
    private bool _drawStreamingGizmos = false;

    [NonSerialized]
    private BinaryReader _reader;

    private Header _header;
    private List<Branch> _branches = new List<Branch>();
    private List<Leaf> _leaves = new List<Leaf>();
    private long _bodyStart = -1;
    private int _rootBranchIndex = -1;
    private Camera _mainCamera;
    private List<int> _visibleLeafIndices = new List<int>();
    private List<int> _visibleBranchIndices = new List<int>();
    private Queue<int> _branchIndicesToCheck = new Queue<int>();
    private List<NodeState> _leafStates = new List<NodeState>();
    private List<NodeState> _branchStates = new List<NodeState>();
    private List<int> _loadedLeafIndices = new List<int>();
    private List<int> _loadedBranchIndices = new List<int>();
    private GraphicsBuffer _quadIndex;
    private MaterialPropertyBlock _mpb;
    private RenderParams _rp;
    private readonly Plane[] _planes = new Plane[6];
    private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _indirectArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[1];



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
        _leafStates = CreateNodeStates(_leaves.Count);
        _branchStates = CreateNodeStates(_branches.Count);
        SetupRendering();
    }

    private BinaryReader OpenFile(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        string fullPath = Path.Combine(Application.dataPath, shortFilePath);
        return new BinaryReader(File.OpenRead(fullPath));
    }

    private Header ReadHeader(BinaryReader reader)
    {
        // OPCファイルのマジックチェック
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

            // 代表点の先頭オフセット
            branch.baseOffset = reader.ReadUInt64();

            // 代表点の点数
            branch.pointCount = reader.ReadUInt32();

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

    private List<NodeState> CreateNodeStates(int count)
    {
        var states = new List<NodeState>(count);
        for (int i = 0; i < count; i++)
            states.Add(new NodeState());

        return states;
    }

    private void SetupRendering()
    {
        _rp = new RenderParams(_pointMaterial)
        {
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            lightProbeUsage = LightProbeUsage.Off,
            reflectionProbeUsage = ReflectionProbeUsage.Off,
            motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
        };

        _mpb = new MaterialPropertyBlock();

        uint[] index = { 0u, 1u, 2u, 2u, 1u, 3u };
        _quadIndex = new GraphicsBuffer(GraphicsBuffer.Target.Index, index.Length, sizeof(uint));
        _quadIndex.SetData(index);
    }



    private void Update()
    {
        // 視界に入っているノードを集める（葉 / 代表点で止める枝）
        GetVisibleNodes();

        // 点群データをロード・アンロード
        SyncNodeStreaming();

        // 点群を描画
        DrawPointCloud();
    }

    private void GetVisibleNodes()
    {
        // 初期化
        _visibleLeafIndices.Clear();
        _visibleBranchIndices.Clear();
        _branchIndicesToCheck.Clear();

        // カメラの視錐台平面
        GeometryUtility.CalculateFrustumPlanes(_mainCamera, _planes);

        // ルートブランチを追加
        _branchIndicesToCheck.Enqueue(_rootBranchIndex);

        // 探索ループ
        while (_branchIndicesToCheck.Count > 0)
        {
            // 処理対象の枝を取得
            int branchIndex = _branchIndicesToCheck.Dequeue();
            Branch branch = _branches[branchIndex];

            // 枝が見えているか
            if (!GeometryUtility.TestPlanesAABB(_planes, branch.bounds))
            {
                // 子の判定をスキップ
                continue;
            }

            // 枝の代表点で十分なら止める（子に降りない）
            if (CanStopAtBranch(branch))
            {
                _visibleBranchIndices.Add(branchIndex);
                continue;
            }

            // 見えている子のノードを確認
            for (int i = 0; i < branch.references.Length; i++)
            {
                NodeReference nodeRef = branch.references[i];

                // 存在しているか
                if (!nodeRef.IsValid) continue;

                // ノードは葉か
                if (nodeRef.isLeaf)
                {
                    // 見えている場合は返却リストに追加
                    Leaf leaf = _leaves[nodeRef.index];
                    if (GeometryUtility.TestPlanesAABB(_planes, leaf.bounds))
                    {
                        _visibleLeafIndices.Add(nodeRef.index);
                    }
                }
                else
                {
                    // 探索対象として追加
                    _branchIndicesToCheck.Enqueue(nodeRef.index);
                }
            }
        }
    }

    private float ComputeAreaPx(Bounds bounds)
    {
        // Bounds球の半径を取得
        float r = bounds.extents.magnitude;

        // Bounds球の半径をピクセル単位で取得
        Vector3 center = bounds.center;
        Vector3 sc = _mainCamera.WorldToScreenPoint(center);
        Vector3 sx = _mainCamera.WorldToScreenPoint(center + _mainCamera.transform.right * r);
        Vector3 sy = _mainCamera.WorldToScreenPoint(center + _mainCamera.transform.up * r);
        float rPxX = (sx - sc).magnitude;
        float rPxY = (sy - sc).magnitude;
        float rPx = Mathf.Max(rPxX, rPxY);

        // Bounds球の面積（ピクセル単位）
        return Mathf.Max(1f, Mathf.PI * rPx * rPx);
    }

    private bool CanStopAtBranch(Branch branch)
    {
        // 代表点が無い枝は止められない
        if (branch.pointCount == 0) return false;

        // 画面ピクセル面積
        float areaPx = ComputeAreaPx(branch.bounds);

        // 画面に必要な点数（面積 × 1ピクセル当たりの目標点数）
        float requiredCount = areaPx * _maxPointsPerPixel;

        // 枝の代表点が必要点数を満たすなら、葉に降りなくてよい
        return branch.pointCount >= requiredCount;
    }

    private void SyncNodeStreaming()
    {
        int frame = Time.frameCount;

        // Leaf ロード
        foreach (int leafIndex in _visibleLeafIndices)
        {
            NodeState state = _leafStates[leafIndex];
            state.lastWantedFrame = frame;

            // すでにロード済みなら何もしない
            if (state.isLoaded) continue;

            LoadLeafPoints(leafIndex);
        }

        // Branch ロード
        foreach (int branchIndex in _visibleBranchIndices)
        {
            NodeState state = _branchStates[branchIndex];
            state.lastWantedFrame = frame;

            // すでにロード済みなら何もしない
            if (state.isLoaded) continue;

            LoadBranchPoints(branchIndex);
        }

        // Leaf アンロード（後ろから走査: 内部でリストから削除されるため）
        for (int i = _loadedLeafIndices.Count - 1; i >= 0; i--)
        {
            int leafIndex = _loadedLeafIndices[i];
            NodeState state = _leafStates[leafIndex];

            if (frame - state.lastWantedFrame > _unloadDelayFrames)
            {
                UnloadLeafPoints(leafIndex);
            }
        }

        // Branch アンロード
        for (int i = _loadedBranchIndices.Count - 1; i >= 0; i--)
        {
            int branchIndex = _loadedBranchIndices[i];
            NodeState state = _branchStates[branchIndex];

            if (frame - state.lastWantedFrame > _unloadDelayFrames)
            {
                UnloadBranchPoints(branchIndex);
            }
        }
    }

    private void LoadLeafPoints(int leafIndex)
    {
        Leaf leaf = _leaves[leafIndex];
        NodeState state = _leafStates[leafIndex];

        // 点数を取得してGPUバッファを構築
        int count = (int)leaf.pointCount;
        FillNodeBuffers(state, leaf.baseOffset, count, leaf.bounds);
        state.isLoaded = true;

        // ロード済みリストに追加
        _loadedLeafIndices.Add(leafIndex);
    }

    private void LoadBranchPoints(int branchIndex)
    {
        Branch branch = _branches[branchIndex];
        NodeState state = _branchStates[branchIndex];

        // 点数を取得してGPUバッファを構築
        int count = (int)branch.pointCount;
        FillNodeBuffers(state, branch.baseOffset, count, branch.bounds);
        state.isLoaded = true;

        // ロード済みリストに追加
        _loadedBranchIndices.Add(branchIndex);
    }

    private void FillNodeBuffers(NodeState state, ulong baseOffset, int count, Bounds bounds)
    {
        // Seek
        int stride = _header.pointStrideBytes;
        long seekPos = _bodyStart + (long)(baseOffset * (ulong)stride);
        _reader.BaseStream.Seek(seekPos, SeekOrigin.Begin);

        // 読み込みバイト数
        int totalBytes = count * stride;

        // Poolを借りる（生バイト + パック済み配列）
        byte[] raw = ArrayPool<byte>.Shared.Rent(totalBytes);
        PackedPoint12[] packed = ArrayPool<PackedPoint12>.Shared.Rent(count);

        // 1ノード分をまとめて読み込み
        int readBytes = 0;
        while (readBytes < totalBytes)
        {
            int n = _reader.BaseStream.Read(raw, readBytes, totalBytes - readBytes);
            if (n <= 0) break;
            readBytes += n;
        }

        // 点データを構築（バイト列からパック）
        for (int i = 0; i < count; i++)
        {
            int p = i * stride;
            ushort x16 = (ushort)(raw[p] | (raw[p + 1] << 8));
            ushort y16 = (ushort)(raw[p + 2] | (raw[p + 3] << 8));
            ushort z16 = (ushort)(raw[p + 4] | (raw[p + 5] << 8));

            byte r8 = raw[p + 6];
            byte g8 = raw[p + 7];
            byte b8 = raw[p + 8];
            byte a8 = raw[p + 9];

            packed[i].xy = (uint)(x16 | (y16 << 16));
            packed[i].z = (uint)z16;
            packed[i].rgba = (uint)(r8 | (g8 << 8) | (b8 << 16) | (a8 << 24));
        }

        // GPUバッファ作成（プール配列は余剰領域があるので count 個だけアップロード）
        state.pointBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 12);
        state.pointBuffer.SetData(packed, 0, 0, count);

        // Poolを返す
        ArrayPool<PackedPoint12>.Shared.Return(packed);
        ArrayPool<byte>.Shared.Return(raw);

        // Indirect args
        state.indirectArgs = new GraphicsBuffer(
            GraphicsBuffer.Target.IndirectArguments,
            1,
            GraphicsBuffer.IndirectDrawIndexedArgs.size
        );

        _indirectArgs[0].indexCountPerInstance = 6u;
        _indirectArgs[0].instanceCount = (uint)count;
        _indirectArgs[0].startIndex = 0u;
        _indirectArgs[0].baseVertexIndex = 0u;
        state.indirectArgs.SetData(_indirectArgs);

        state.bounds = bounds;
    }

    private void UnloadLeafPoints(int leafIndex)
    {
        NodeState state = _leafStates[leafIndex];

        // ロード済みでなければ何もしない
        if (!state.isLoaded) return;

        DisposeNodeBuffers(state);

        // ロード済みリストから削除
        _loadedLeafIndices.Remove(leafIndex);
    }

    private void UnloadBranchPoints(int branchIndex)
    {
        NodeState state = _branchStates[branchIndex];

        // ロード済みでなければ何もしない
        if (!state.isLoaded) return;

        DisposeNodeBuffers(state);

        // ロード済みリストから削除
        _loadedBranchIndices.Remove(branchIndex);
    }

    private void DisposeNodeBuffers(NodeState state)
    {
        state.pointBuffer?.Dispose();
        state.pointBuffer = null;

        state.indirectArgs?.Dispose();
        state.indirectArgs = null;

        state.isLoaded = false;
    }

    private void DrawPointCloud()
    {
        // ロード済みの葉を描画
        foreach (int leafIndex in _loadedLeafIndices)
        {
            DrawNode(_leafStates[leafIndex]);
        }

        // ロード済みの枝代表点を描画
        foreach (int branchIndex in _loadedBranchIndices)
        {
            DrawNode(_branchStates[branchIndex]);
        }
    }

    private void DrawNode(NodeState state)
    {
        _mpb.Clear();
        _mpb.SetBuffer("_Points", state.pointBuffer);
        _mpb.SetVector("_ChunkMin", state.bounds.min);
        _mpb.SetVector("_ChunkMax", state.bounds.max);
        _mpb.SetFloat("_PointSize", _pointSize);

        _rp.matProps = _mpb;
        _rp.worldBounds = state.bounds;

        Graphics.RenderPrimitivesIndexedIndirect(
            _rp,
            MeshTopology.Triangles,
            _quadIndex,
            state.indirectArgs,
            1,
            0
        );
    }



    private void OnDisable()
    {
        Cleanup();
    }

    private void OnDestroy()
    {
        Cleanup();
    }

    private void Cleanup()
    {
        UnloadAllNodes();
        UnloadIndexBuffer();
        CloseReader();
    }

    private void UnloadAllNodes()
    {
        for (int i = 0; i < _leafStates.Count; i++)
        {
            UnloadLeafPoints(i);
        }
        for (int i = 0; i < _branchStates.Count; i++)
        {
            UnloadBranchPoints(i);
        }
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

    private void UnloadIndexBuffer()
    {
        _quadIndex?.Dispose();
        _quadIndex = null;
    }



    private void OnDrawGizmos()
    {
        DrawVisibleGizmos();
        DrawStreamingGizmos();
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

    private void DrawStreamingGizmos()
    {
        if (!_drawStreamingGizmos) return;
        if (_leaves.Count == 0 || _leafStates.Count != _leaves.Count) return;

        int frame = Time.frameCount;

        for (int i = 0; i < _leafStates.Count; i++)
        {
            NodeState state = _leafStates[i];

            // ロードされていないなら描画しない
            if (!state.isLoaded) continue;

            int ageFrames = 0;
            if (state.lastWantedFrame >= 0) ageFrames = frame - state.lastWantedFrame;

            float age01 = Mathf.Clamp01(ageFrames / (float)Mathf.Max(1, _unloadDelayFrames));

            Color c = Color.Lerp(Color.blue, Color.red, age01);

            var leaf = _leaves[i];
            Gizmos.color = c;
            Gizmos.DrawCube(leaf.bounds.center, leaf.bounds.size);
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

        // 枝の代表点用
        public ulong baseOffset;
        public uint pointCount;

        public NodeReference[] references = new NodeReference[8];
    }

    [Serializable]
    public class Leaf
    {
        public byte depth;
        public ulong path;

        public Bounds bounds;

        public ulong baseOffset;

        public uint pointCount;
    }

    [Serializable]
    public struct NodeReference
    {
        public bool isLeaf;

        public int index;

        public bool IsValid => index >= 0;
    }

    private class NodeState
    {
        public GraphicsBuffer pointBuffer;
        public GraphicsBuffer indirectArgs;
        public Bounds bounds;
        public bool isLoaded = false;
        public int lastWantedFrame = -1;
    }

    private struct PackedPoint12
    {
        public uint xy;
        public uint z;
        public uint rgba;
    }
}
