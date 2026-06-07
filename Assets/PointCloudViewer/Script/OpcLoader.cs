using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

public class OpcLoader : MonoBehaviour
{
    [Header("File")]
    [SerializeField]
    private string _filePath = "Assets/sample.opc";

    [Header("Rendering")]
    [SerializeField]
    private Material _pointMaterial;

    [Header("Point Size")]
    [SerializeField]
    private float _minPixelSize = 0.1f;

    [SerializeField]
    private float _maxPixelSize = 8.0f;

    [SerializeField]
    private float _pointSizeScale = 1.0f;

    [Header("LOD / Streaming")]
    [SerializeField]
    private float _maxPointsPerPixel = 1.0f;

    [SerializeField]
    private int _pointBudget = 2_000_000;

    [SerializeField]
    private int _maxLoadsPerFrame = 10;

    [SerializeField]
    private int _unloadDelayFrames = 30;

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
    private List<VisibleNode> _visibleNodes = new List<VisibleNode>();
    private List<uint> _desiredCounts = new List<uint>();
    private List<uint> _finalAllocs = new List<uint>();
    private List<int> _activeIndices = new List<int>();
    private Queue<int> _branchIndicesToCheck = new Queue<int>();
    private List<NodeState> _leafStates = new List<NodeState>();
    private List<NodeState> _branchStates = new List<NodeState>();
    private List<int> _loadedLeafIndices = new List<int>();
    private List<int> _loadedBranchIndices = new List<int>();
    private List<VisibleNode> _pendingLoads = new List<VisibleNode>();
    private GraphicsBuffer _quadIndex;
    private MaterialPropertyBlock _mpb;
    private RenderParams _rp;
    private readonly Plane[] _planes = new Plane[6];
    private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _indirectArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[1];

    // ファイル読み込みとパックを担当するワーカースレッド
    private OpcPointLoadWorker _loadWorker;

    // フレームごとのカメラ情報キャッシュ（ノードごとのネイティブ呼び出しを避ける）
    private Vector3 _camPos;
    private Vector3 _camFwd;
    private float _worldToPixel;
    private float _minDepth;
    private float _maxAreaPx;

    // シェーダプロパティID（毎フレームの文字列ハッシュ計算を避ける）
    private static readonly int _pointsId = Shader.PropertyToID("_Points");
    private static readonly int _chunkMinId = Shader.PropertyToID("_ChunkMin");
    private static readonly int _chunkMaxId = Shader.PropertyToID("_ChunkMax");
    private static readonly int _pointWorldRadiusId = Shader.PropertyToID("_PointWorldRadius");
    private static readonly int _worldToPixelId = Shader.PropertyToID("_WorldToPixel");
    private static readonly int _minPixelSizeId = Shader.PropertyToID("_MinPixelSize");
    private static readonly int _maxPixelSizeId = Shader.PropertyToID("_MaxPixelSize");

    // GPUアップロードの所要時間（1フレーム分を合算してベンチマークに記録する）
    private double _frameGpuUploadMs;

    // Profiler用マーカー（Unity ProfilerのCPU Usageに"OpcLoader.～"として表示される）
    private static readonly ProfilerMarker _markerGetVisibleNodes = new ProfilerMarker("OpcLoader.GetVisibleNodes");
    private static readonly ProfilerMarker _markerApplyPointBudget = new ProfilerMarker("OpcLoader.ApplyPointBudget");
    private static readonly ProfilerMarker _markerSyncStreaming = new ProfilerMarker("OpcLoader.SyncNodeStreaming");
    private static readonly ProfilerMarker _markerDrawPointCloud = new ProfilerMarker("OpcLoader.DrawPointCloud");
    private static readonly ProfilerMarker _markerGpuUpload = new ProfilerMarker("OpcLoader.Load.GpuUpload");



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
        CreateLoadWorker();
        SetupRendering();

        // 点データの読み込みはワーカーが専用ストリームで行うため、ヘッダ用リーダーはここで閉じる
        CloseReader();
    }

    // 点データ読み込み用のワーカースレッドを起動する
    private void CreateLoadWorker()
    {
        // 全ノード中の最大点数を求める（ワーカー内プールの配列サイズ上限を決めるため）
        int maxPointCount = 1024;
        foreach (Branch branch in _branches)
            maxPointCount = Math.Max(maxPointCount, (int)branch.pointCount);
        foreach (Leaf leaf in _leaves)
            maxPointCount = Math.Max(maxPointCount, (int)leaf.pointCount);

        _loadWorker = new OpcPointLoadWorker(GetFullPath(_filePath), _header.pointStrideBytes, maxPointCount);
    }

    private BinaryReader OpenFile(string filePath)
    {
        return new BinaryReader(File.OpenRead(GetFullPath(filePath)));
    }

    // "Assets/～" 形式のパスを絶対パスに変換する
    private string GetFullPath(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        return Path.Combine(Application.dataPath, shortFilePath);
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
        using (_markerGetVisibleNodes.Auto())
        {
            GetVisibleNodes();
        }

        // 点群データをロード・アンロード
        using (_markerSyncStreaming.Auto())
        {
            SyncNodeStreaming();
        }

        // 点群を描画
        using (_markerDrawPointCloud.Auto())
        {
            DrawPointCloud();
        }
    }

    private void GetVisibleNodes()
    {
        // 初期化
        _visibleNodes.Clear();
        _branchIndicesToCheck.Clear();

        // カメラの視錐台平面
        GeometryUtility.CalculateFrustumPlanes(_mainCamera, _planes);

        // カメラ情報をフレームに1回だけキャッシュ（ComputeAreaPx用）
        Transform camTransform = _mainCamera.transform;
        _camPos = camTransform.position;
        _camFwd = camTransform.forward;
        _worldToPixel = _mainCamera.projectionMatrix.m11 * Screen.height * 0.5f;
        _minDepth = _mainCamera.nearClipPlane + 1e-3f;
        _maxAreaPx = (float)Screen.width * Screen.height;

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
                _visibleNodes.Add(new VisibleNode
                {
                    index = branchIndex,
                    isLeaf = false,
                    pointCount = branch.pointCount,
                    areaPx = ComputeAreaPx(branch.bounds),
                });
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
                    // 見えている場合は候補リストに追加
                    Leaf leaf = _leaves[nodeRef.index];
                    if (GeometryUtility.TestPlanesAABB(_planes, leaf.bounds))
                    {
                        _visibleNodes.Add(new VisibleNode
                        {
                            index = nodeRef.index,
                            isLeaf = true,
                            pointCount = leaf.pointCount,
                            areaPx = ComputeAreaPx(leaf.bounds),
                        });
                    }
                }
                else
                {
                    // 探索対象として追加
                    _branchIndicesToCheck.Enqueue(nodeRef.index);
                }
            }
        }

        // Point Budgetを適用して描画対象を決定
        using (_markerApplyPointBudget.Auto())
        {
            ApplyPointBudget();
        }
    }

    private float ComputeAreaPx(Bounds bounds)
    {
        // Bounds球の半径を取得
        float r = bounds.extents.magnitude;

        // カメラ前方方向の奥行き（背後/直近はクランプして異常値を防ぐ）
        float depth = Vector3.Dot(bounds.center - _camPos, _camFwd);
        if (depth < _minDepth) depth = _minDepth;

        // ワールド半径を画面ピクセル半径へ変換（シェーダと同じ式: r × worldToPixel ÷ 奥行き）
        float rPx = r * _worldToPixel / depth;

        // 画面ピクセル数を上限とし、異常値が全体配分を支配しないようにする
        float areaPx = Mathf.PI * rPx * rPx;
        return Mathf.Clamp(areaPx, 1f, _maxAreaPx);
    }

    private void ApplyPointBudget()
    {
        // 描画対象リスト / 各種バッファを初期化
        _visibleLeafIndices.Clear();
        _visibleBranchIndices.Clear();
        _desiredCounts.Clear();
        _finalAllocs.Clear();
        _activeIndices.Clear();

        // visibleが無ければ何もしない
        if (_visibleNodes.Count == 0) return;

        // maxPointsPerPixel を自動算出
        float maxPPP = ComputeMaxPointsPerPixel();

        // 1パス目: 各ノードの希望点数を計算（pointCountキャップは water-filling 内で適用）
        for (int i = 0; i < _visibleNodes.Count; i++)
        {
            VisibleNode node = _visibleNodes[i];

            // 画面サイズに比例した希望点数（最低1点）
            uint wish = (uint)Mathf.Max(1f, node.areaPx * maxPPP);

            _desiredCounts.Add(wish);
            _finalAllocs.Add(0);
            _activeIndices.Add(i);
        }

        // 2パス目: water-filling で予算を配分
        AllocateWithWaterFilling();

        // 3パス目: 配分結果を NodeState に反映
        for (int i = 0; i < _visibleNodes.Count; i++)
        {
            VisibleNode node = _visibleNodes[i];

            // 実点数を上限としてクランプ
            uint alloc = _finalAllocs[i];
            if (alloc > node.pointCount) alloc = node.pointCount;

            // 種別ごとにNodeStateへ反映し描画対象リストに追加
            if (node.isLeaf)
            {
                _leafStates[node.index].drawCount = alloc;
                _leafStates[node.index].areaPx = node.areaPx;
                _visibleLeafIndices.Add(node.index);
            }
            else
            {
                _branchStates[node.index].drawCount = alloc;
                _branchStates[node.index].areaPx = node.areaPx;
                _visibleBranchIndices.Add(node.index);
            }
        }
    }

    private float ComputeMaxPointsPerPixel()
    {
        // visible ノードのピクセル面積を合計
        float totalAreaPx = 0f;
        for (int i = 0; i < _visibleNodes.Count; i++)
            totalAreaPx += _visibleNodes[i].areaPx;

        // 面積ゼロのフェールセーフ
        if (totalAreaPx <= 0f) return _maxPointsPerPixel;

        // Budget / 総面積で「画面に対するピクセル密度」を逆算（暴走防止のためクランプ）
        float maxPPP = _pointBudget / totalAreaPx;
        return Mathf.Clamp(maxPPP, 0.1f, 4.0f);
    }

    private void AllocateWithWaterFilling()
    {
        long remaining = _pointBudget;

        // 飽和ノードを順次確定させ、残予算を非飽和ノードに再配分するループ
        while (_activeIndices.Count > 0)
        {
            // 活性ノードの希望合計を集計
            long activeWish = 0;
            for (int j = 0; j < _activeIndices.Count; j++)
                activeWish += _desiredCounts[_activeIndices[j]];

            // 残予算で全希望を満たせるなら確定して終了
            if (activeWish <= remaining)
            {
                for (int j = 0; j < _activeIndices.Count; j++)
                {
                    int idx = _activeIndices[j];
                    uint pc = _visibleNodes[idx].pointCount;
                    uint wish = _desiredCounts[idx];
                    _finalAllocs[idx] = wish < pc ? wish : pc;
                }
                _activeIndices.Clear();
                break;
            }

            // 一律スケールを計算
            double scale = (double)remaining / activeWish;

            // pointCount で頭打ちになるノードを確定 → 残予算から引いて活性から外す
            bool anyCapped = false;
            for (int j = _activeIndices.Count - 1; j >= 0; j--)
            {
                int idx = _activeIndices[j];
                uint pc = _visibleNodes[idx].pointCount;
                double scaled = _desiredCounts[idx] * scale;
                if (scaled >= pc)
                {
                    _finalAllocs[idx] = pc;
                    remaining -= pc;
                    _activeIndices.RemoveAt(j);
                    anyCapped = true;
                }
            }

            // 誰も新たに飽和しなければスケールを確定して終了
            if (!anyCapped)
            {
                for (int j = 0; j < _activeIndices.Count; j++)
                {
                    int idx = _activeIndices[j];
                    uint scaled = (uint)Mathf.Max(1, Mathf.RoundToInt(_desiredCounts[idx] * (float)scale));
                    _finalAllocs[idx] = scaled;
                }
                _activeIndices.Clear();
                break;
            }
        }
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

        // 未ロード候補を集める（lastWantedFrame は見えている全ノードに対して更新）
        _pendingLoads.Clear();
        foreach (int leafIndex in _visibleLeafIndices)
        {
            NodeState state = _leafStates[leafIndex];
            state.lastWantedFrame = frame;
            if (state.isLoaded || state.isLoading) continue;

            _pendingLoads.Add(new VisibleNode
            {
                index = leafIndex,
                isLeaf = true,
                areaPx = state.areaPx,
            });
        }
        foreach (int branchIndex in _visibleBranchIndices)
        {
            NodeState state = _branchStates[branchIndex];
            state.lastWantedFrame = frame;
            if (state.isLoaded || state.isLoading) continue;

            _pendingLoads.Add(new VisibleNode
            {
                index = branchIndex,
                isLeaf = false,
                areaPx = state.areaPx,
            });
        }

        // ワーカーが読み終えたノードをGPUへアップロード
        UploadCompletedLoads(frame);

        // 画面で目立つ順（面積大）にワーカーへロード要求。1フレームあたり _maxLoadsPerFrame 個までで打ち切り。
        _pendingLoads.Sort((a, b) => b.areaPx.CompareTo(a.areaPx));
        int requestLimit = Mathf.Min(_pendingLoads.Count, _maxLoadsPerFrame);
        for (int i = 0; i < requestLimit; i++)
        {
            VisibleNode node = _pendingLoads[i];
            if (node.isLeaf) RequestLeafLoad(node.index);
            else RequestBranchLoad(node.index);
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

    private void RequestLeafLoad(int leafIndex)
    {
        Leaf leaf = _leaves[leafIndex];

        // ロード中フラグを立ててワーカーへ要求を積む
        _leafStates[leafIndex].isLoading = true;
        EnqueueLoadRequest(leafIndex, true, leaf.baseOffset, (int)leaf.pointCount);
    }

    private void RequestBranchLoad(int branchIndex)
    {
        Branch branch = _branches[branchIndex];

        // ロード中フラグを立ててワーカーへ要求を積む
        _branchStates[branchIndex].isLoading = true;
        EnqueueLoadRequest(branchIndex, false, branch.baseOffset, (int)branch.pointCount);
    }

    private void EnqueueLoadRequest(int index, bool isLeaf, ulong baseOffset, int count)
    {
        // ファイル先頭からのシーク位置を計算してワーカーへ渡す
        long seekPos = _bodyStart + (long)(baseOffset * (ulong)_header.pointStrideBytes);
        _loadWorker.Enqueue(new OpcPointLoadWorker.LoadRequest
        {
            index = index,
            isLeaf = isLeaf,
            seekPos = seekPos,
            count = count,
        });
    }

    // ワーカーが読み終えたノードのGPUバッファを構築する（1フレームあたり _maxLoadsPerFrame 個まで）
    private void UploadCompletedLoads(int frame)
    {
        _frameGpuUploadMs = 0;

        int uploaded = 0;
        while (uploaded < _maxLoadsPerFrame && _loadWorker.TryDequeue(out OpcPointLoadWorker.LoadResult result))
        {
            NodeState state = result.isLeaf ? _leafStates[result.index] : _branchStates[result.index];
            state.isLoading = false;

            // 読み込み中に不要になったノードは破棄（GPUバッファを作らない）
            if (frame - state.lastWantedFrame > _unloadDelayFrames)
            {
                _loadWorker.ReturnPacked(result.packed);
                continue;
            }

            // GPUバッファを構築してロード済みリストへ登録
            Bounds bounds = result.isLeaf ? _leaves[result.index].bounds : _branches[result.index].bounds;
            UploadNodeBuffers(state, result.packed, result.count, bounds);
            _loadWorker.ReturnPacked(result.packed);
            state.isLoaded = true;
            if (result.isLeaf) _loadedLeafIndices.Add(result.index);
            else _loadedBranchIndices.Add(result.index);
            uploaded++;
        }

        // アップロードがあったフレームだけ所要時間を記録（0埋めで平均を薄めない）
        if (uploaded > 0)
        {
            Benchmarker.Record("OpcLoader.Load.GpuUpload", _frameGpuUploadMs);
        }
    }

    private void UploadNodeBuffers(NodeState state, PackedPoint12[] packed, int count, Bounds bounds)
    {
        // GPUバッファ作成（プール配列は余剰領域があるので count 個だけアップロード）
        long tickUploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (_markerGpuUpload.Auto())
        {
            state.pointBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 12);
            state.pointBuffer.SetData(packed, 0, 0, count);
        }
        _frameGpuUploadMs += TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - tickUploadStart);

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

    // Stopwatchのティック数をミリ秒に変換する
    private static double TicksToMs(long ticks)
    {
        return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
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
        // ワールド半径→画面ピクセル変換係数（GetVisibleNodesでフレーム先頭にキャッシュ済み）
        float worldToPixel = _worldToPixel;

        // ロード済みの葉を描画
        foreach (int leafIndex in _loadedLeafIndices)
        {
            DrawNode(_leafStates[leafIndex], worldToPixel);
        }

        // ロード済みの枝代表点を描画
        foreach (int branchIndex in _loadedBranchIndices)
        {
            DrawNode(_branchStates[branchIndex], worldToPixel);
        }
    }

    private void DrawNode(NodeState state, float worldToPixel)
    {
        // 描画する点が無ければスキップ
        if (state.drawCount == 0) return;

        // 今フレームの描画点数で indirectArgs を更新（先頭drawCount点だけ描画される）
        _indirectArgs[0].indexCountPerInstance = 6u;
        _indirectArgs[0].instanceCount = state.drawCount;
        _indirectArgs[0].startIndex = 0u;
        _indirectArgs[0].baseVertexIndex = 0u;
        state.indirectArgs.SetData(_indirectArgs);

        // ノードの密度から1点のワールド半径を求める（奥行きに応じた画面サイズ変換はシェーダ側で行う）
        float worldRadius = ComputeWorldPointRadius(state);

        _mpb.Clear();
        _mpb.SetBuffer(_pointsId, state.pointBuffer);
        _mpb.SetVector(_chunkMinId, state.bounds.min);
        _mpb.SetVector(_chunkMaxId, state.bounds.max);
        _mpb.SetFloat(_pointWorldRadiusId, worldRadius);
        _mpb.SetFloat(_worldToPixelId, worldToPixel);
        _mpb.SetFloat(_minPixelSizeId, _minPixelSize);
        _mpb.SetFloat(_maxPixelSizeId, _maxPixelSize);

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

    private float ComputeWorldPointRadius(NodeState state)
    {
        // 描画点が無ければ0（呼び出し側で0は描画スキップ済み）
        if (state.drawCount == 0) return 0f;

        // ノードの外接球半径（中心から角までの距離）
        float r = state.bounds.extents.magnitude;

        // 断面(π r²)に drawCount 点を均した時の点間隔を半径の基準にし、_pointSizeScale で太さを調整
        return _pointSizeScale * Mathf.Sqrt(Mathf.PI * r * r / state.drawCount);
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
        // ワーカーを先に止める（停止後に新しい結果が出ないようにする）
        _loadWorker?.Dispose();
        _loadWorker = null;

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

        // ワーカーへロード要求済みで結果待ちの間 true（二重要求を防ぐ）
        public bool isLoading = false;

        public int lastWantedFrame = -1;

        // 今フレームで描画する点数（先頭からこの数だけ描画）
        public uint drawCount = 0;

        // 今フレームでノードが画面に占めるピクセル面積（点サイズ計算用）
        public float areaPx = 0f;
    }

    // Point Budget適用前の候補ノード
    private struct VisibleNode
    {
        public int index;
        public bool isLeaf;
        public uint pointCount;
        public float areaPx;
    }
}
