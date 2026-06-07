using System;
using System.Collections.Generic;
using System.IO;
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

    // 描画必要数に対する先読み係数（多めにロードしてカメラ接近のたびの取り直しを減らす）
    private const int PartialLoadMargin = 2;

    // 1ノードの最低ロード点数（drawCountが極小でも取り直しが頻発しない程度を確保する）
    private const int MinLoadCount = 1024;

    // 共有点プール容量の自動算出に使う余裕係数（部分ロードの必要量 ≈ 予算×先読み係数 に視界外残留分の余裕を加える）
    private const int PoolCapacityBudgetMultiplier = 4;

    // 共有点プールの容量（点数）。Start時にファイル情報と予算から自動算出される。
    private int _pointPoolCapacity;

    // 開いたOPCファイル（ヘッダ+ノード索引）。読み込み失敗時はnullのまま。
    private OpcFile _file;
    private OpcFile.Header _header;
    private List<OpcFile.Branch> _branches = new List<OpcFile.Branch>();
    private List<OpcFile.Leaf> _leaves = new List<OpcFile.Leaf>();
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

    // 全ノード共有の点プールと、その空き領域を管理するアロケータ
    private GraphicsBuffer _pointPool;
    private PointPoolAllocator _pointPoolAllocator;
    private bool _poolFullWarned;

    // マルチドロー用のコマンド列とノードごとの描画データ（毎フレーム詰め直してGPUへ転送）
    private GraphicsBuffer _commandBuffer;
    private GraphicsBuffer _nodeDataBuffer;
    private GraphicsBuffer.IndirectDrawIndexedArgs[] _commandArgs;
    private NodeDrawData[] _nodeDrawData;

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
    private static readonly int _nodeDataId = Shader.PropertyToID("_NodeData");
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

        // OPCファイルのヘッダとノード索引を読み込む（失敗したらエラーを出してこのコンポーネントを止める）
        if (!OpcFile.TryOpen(GetFullPath(_filePath), out _file, out string error))
        {
            Debug.LogError($"OpcLoader: OPCファイルを開けませんでした。{error}");
            enabled = false;
            return;
        }

        _header = _file.FileHeader;
        _branches = _file.Branches;
        _leaves = _file.Leaves;
        _bodyStart = _file.BodyStart;
        _rootBranchIndex = _file.RootBranchIndex;
        _leafStates = CreateNodeStates(_leaves.Count);
        _branchStates = CreateNodeStates(_branches.Count);
        CreateLoadWorker();
        SetupRendering();
    }

    // 点データ読み込み用のワーカースレッドを起動する
    private void CreateLoadWorker()
    {
        // 全ノード中の最大点数を求める（ワーカー内プールの配列サイズ上限を決めるため）
        int maxPointCount = 1024;
        foreach (OpcFile.Branch branch in _branches)
            maxPointCount = Math.Max(maxPointCount, (int)branch.pointCount);
        foreach (OpcFile.Leaf leaf in _leaves)
            maxPointCount = Math.Max(maxPointCount, (int)leaf.pointCount);

        _loadWorker = new OpcPointLoadWorker(GetFullPath(_filePath), _header.pointStrideBytes, maxPointCount);
    }

    // "Assets/～" 形式のパスを絶対パスに変換する
    private string GetFullPath(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        return Path.Combine(Application.dataPath, shortFilePath);
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

        // 全ノードを1回のマルチドローで描くため、描画範囲はルート全体で固定
        Vector3 rootCenter = (_header.rootMin + _header.rootMax) * 0.5f;
        _rp.worldBounds = new Bounds(rootCenter, _header.rootMax - _header.rootMin);

        uint[] index = { 0u, 1u, 2u, 2u, 1u, 3u };
        _quadIndex = new GraphicsBuffer(GraphicsBuffer.Target.Index, index.Length, sizeof(uint));
        _quadIndex.SetData(index);

        // 共有点プールの容量を自動算出し、点プールと空き領域アロケータを作成
        _pointPoolCapacity = ComputePointPoolCapacity();
        _pointPool = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _pointPoolCapacity, 12);
        _pointPoolAllocator = new PointPoolAllocator(_pointPoolCapacity);

        // マルチドロー用のコマンドバッファとノード描画データバッファを作成（最大数=全ノード数）
        int maxNodes = _branches.Count + _leaves.Count;
        _commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, maxNodes, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        _nodeDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, System.Runtime.InteropServices.Marshal.SizeOf<NodeDrawData>());
        _commandArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[maxNodes];
        _nodeDrawData = new NodeDrawData[maxNodes];
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
            OpcFile.Branch branch = _branches[branchIndex];

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
                OpcFile.NodeReference nodeRef = branch.references[i];

                // 存在しているか
                if (!nodeRef.IsValid) continue;

                // ノードは葉か
                if (nodeRef.isLeaf)
                {
                    // 見えている場合は候補リストに追加
                    OpcFile.Leaf leaf = _leaves[nodeRef.index];
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

    private bool CanStopAtBranch(OpcFile.Branch branch)
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
            if (state.isLoading) continue;

            // ロード済みでも描画必要数がロード済み点数を超えたら追加ロード（全点ロード済みなら不要）
            if (state.isLoaded && !NeedsMorePoints(state, _leaves[leafIndex].pointCount)) continue;

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
            if (state.isLoading) continue;

            // ロード済みでも描画必要数がロード済み点数を超えたら追加ロード（全点ロード済みなら不要）
            if (state.isLoaded && !NeedsMorePoints(state, _branches[branchIndex].pointCount)) continue;

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

    // 描画必要数に対してロード済み点数が不足しているか（全点ロード済みなら常にfalse）
    private static bool NeedsMorePoints(NodeState state, uint nodePointCount)
    {
        if (state.loadedCount >= (int)nodePointCount) return false;
        return state.drawCount > (uint)state.loadedCount;
    }

    // ロードする点数を決める（描画必要数の先読み係数倍を読み、実点数で頭打ち）
    private static int ComputeLoadCount(uint drawCount, uint nodePointCount)
    {
        long desired = Math.Max(MinLoadCount, (long)drawCount * PartialLoadMargin);
        return (int)Math.Min(desired, nodePointCount);
    }

    private void RequestLeafLoad(int leafIndex)
    {
        OpcFile.Leaf leaf = _leaves[leafIndex];
        NodeState state = _leafStates[leafIndex];

        // ロード中フラグを立ててワーカーへ要求を積む（先頭から必要分だけ読む）
        state.isLoading = true;
        EnqueueLoadRequest(leafIndex, true, leaf.baseOffset, ComputeLoadCount(state.drawCount, leaf.pointCount));
    }

    private void RequestBranchLoad(int branchIndex)
    {
        OpcFile.Branch branch = _branches[branchIndex];
        NodeState state = _branchStates[branchIndex];

        // ロード中フラグを立ててワーカーへ要求を積む（先頭から必要分だけ読む）
        state.isLoading = true;
        EnqueueLoadRequest(branchIndex, false, branch.baseOffset, ComputeLoadCount(state.drawCount, branch.pointCount));
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

            // 共有プールへ点データをアップロード（追加ロード時は内部で旧領域を解放して差し替え）
            Bounds bounds = result.isLeaf ? _leaves[result.index].bounds : _branches[result.index].bounds;
            bool wasLoaded = state.isLoaded;
            bool uploadSucceeded = UploadNodePoints(state, result.packed, result.count, bounds);
            _loadWorker.ReturnPacked(result.packed);

            // プール満杯で確保できなかったノードは見送る（ロード済みなら旧データのまま描画継続）
            if (!uploadSucceeded) continue;

            // 新規ロードのときだけロード済みリストへ登録（差し替え時は登録済み）
            state.isLoaded = true;
            if (!wasLoaded)
            {
                if (result.isLeaf) _loadedLeafIndices.Add(result.index);
                else _loadedBranchIndices.Add(result.index);
            }
            uploaded++;
        }

        // アップロードがあったフレームだけ所要時間を記録（0埋めで平均を薄めない）
        if (uploaded > 0)
        {
            Benchmarker.Record("OpcLoader.Load.GpuUpload", _frameGpuUploadMs);
        }
    }

    // 共有プールに領域を確保して点データをアップロードする。プール満杯なら false を返す。
    private bool UploadNodePoints(NodeState state, PackedPoint12[] packed, int count, Bounds bounds)
    {
        // プールから連続領域を確保。足りなければ画面に不要なノードを先に解放して再試行する
        if (!_pointPoolAllocator.TryAllocate(count, out int poolStart))
        {
            EvictStaleLoadedNodes();
            if (!_pointPoolAllocator.TryAllocate(count, out poolStart))
            {
                // それでも満杯ならこのノードのロードは見送る（警告は1回だけ。空き状況も診断用に出す）
                if (!_poolFullWarned)
                {
                    Debug.LogWarning(
                        $"OpcLoader: 点プールが満杯のためノードのロードを見送ります。" +
                        $"要求={count} 空き合計={_pointPoolAllocator.TotalFreeCount} 最大連続空き={_pointPoolAllocator.LargestFreeBlockCount} 容量={_pointPoolCapacity:N0}。" +
                        $"頻発する場合は PoolCapacityBudgetMultiplier を増やしてください。");
                    _poolFullWarned = true;
                }
                return false;
            }
        }

        // 確保した位置へ点データをアップロード（プール配列は余剰領域があるので count 個だけ転送）
        long tickUploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (_markerGpuUpload.Auto())
        {
            _pointPool.SetData(packed, 0, poolStart, count);
        }
        _frameGpuUploadMs += TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - tickUploadStart);

        // 追加ロードの差し替え時は旧領域を解放する（新確保が成功してから解放するので失敗時も安全）
        if (state.poolStart >= 0)
        {
            _pointPoolAllocator.Free(state.poolStart, state.loadedCount);
        }

        // 確保情報とboundsをノードに記録
        state.poolStart = poolStart;
        state.loadedCount = count;
        state.bounds = bounds;
        return true;
    }

    // 共有点プールの容量（点数）を自動算出する。
    // 部分ロードの必要量（≈ 予算×先読み係数 + 視界外残留分）を賄える係数を予算に掛け、ファイル内の全実点数を上限とする。
    private int ComputePointPoolCapacity()
    {
        // ファイル内の全ノードの実点数合計（プールがこれ以上必要になることはない）
        long totalNodePoints = 0;
        foreach (OpcFile.Branch branch in _branches) totalNodePoints += branch.pointCount;
        foreach (OpcFile.Leaf leaf in _leaves) totalNodePoints += leaf.pointCount;

        // 小さいファイルは全点数ぴったり、大きいファイルは予算×係数で頭打ち
        long capacity = Math.Min(totalNodePoints, (long)PoolCapacityBudgetMultiplier * _pointBudget);
        capacity = Math.Min(capacity, int.MaxValue);

        Debug.Log($"OpcLoader: 点プール容量を {capacity:N0} 点（約 {capacity * 12 / (1024 * 1024)} MB）に自動設定しました。");
        return (int)capacity;
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

        FreeNodePoints(state);

        // ロード済みリストから削除
        _loadedLeafIndices.Remove(leafIndex);
    }

    private void UnloadBranchPoints(int branchIndex)
    {
        NodeState state = _branchStates[branchIndex];

        // ロード済みでなければ何もしない
        if (!state.isLoaded) return;

        FreeNodePoints(state);

        // ロード済みリストから削除
        _loadedBranchIndices.Remove(branchIndex);
    }

    // 今フレーム見えていないロード済みノードを解放してプールに空きを作る（メモリ圧迫時の早期アンロード）
    private void EvictStaleLoadedNodes()
    {
        int frame = Time.frameCount;

        // 後ろから走査（内部でロード済みリストから削除されるため）
        for (int i = _loadedLeafIndices.Count - 1; i >= 0; i--)
        {
            int leafIndex = _loadedLeafIndices[i];
            if (_leafStates[leafIndex].lastWantedFrame < frame)
            {
                UnloadLeafPoints(leafIndex);
            }
        }
        for (int i = _loadedBranchIndices.Count - 1; i >= 0; i--)
        {
            int branchIndex = _loadedBranchIndices[i];
            if (_branchStates[branchIndex].lastWantedFrame < frame)
            {
                UnloadBranchPoints(branchIndex);
            }
        }
    }

    // 共有プールに確保していた領域を解放してロード済みフラグを下ろす
    private void FreeNodePoints(NodeState state)
    {
        if (state.poolStart >= 0)
        {
            _pointPoolAllocator.Free(state.poolStart, state.loadedCount);
            state.poolStart = -1;
            state.loadedCount = 0;
        }

        state.isLoaded = false;
    }

    private void DrawPointCloud()
    {
        // ロード済みノードの描画コマンドとノード描画データを詰める
        int commandCount = 0;
        foreach (int leafIndex in _loadedLeafIndices)
        {
            commandCount = AppendDrawCommand(_leafStates[leafIndex], commandCount);
        }
        foreach (int branchIndex in _loadedBranchIndices)
        {
            commandCount = AppendDrawCommand(_branchStates[branchIndex], commandCount);
        }

        // 描画対象が無ければ何もしない
        if (commandCount == 0) return;

        // 今フレーム分のコマンドとノード描画データをGPUへ転送
        _commandBuffer.SetData(_commandArgs, 0, 0, commandCount);
        _nodeDataBuffer.SetData(_nodeDrawData, 0, 0, commandCount);

        // 全ノード共通のパラメータを設定（ノード別パラメータは _NodeData から引く）
        _mpb.Clear();
        _mpb.SetBuffer(_pointsId, _pointPool);
        _mpb.SetBuffer(_nodeDataId, _nodeDataBuffer);
        _mpb.SetFloat(_worldToPixelId, _worldToPixel);
        _mpb.SetFloat(_minPixelSizeId, _minPixelSize);
        _mpb.SetFloat(_maxPixelSizeId, _maxPixelSize);
        _rp.matProps = _mpb;

        // 全ノードを1回のマルチドローで描画
        Graphics.RenderPrimitivesIndexedIndirect(
            _rp,
            MeshTopology.Triangles,
            _quadIndex,
            _commandBuffer,
            commandCount,
            0
        );
    }

    // 1ノード分の描画コマンドとノード描画データを配列に積み、積んだ後のコマンド数を返す
    private int AppendDrawCommand(NodeState state, int commandCount)
    {
        // 描画点数はロード済み点数を上限とする（追加ロードが追いつくまでの間はある分だけ描く）
        uint instanceCount = state.drawCount;
        if (instanceCount > (uint)state.loadedCount) instanceCount = (uint)state.loadedCount;

        // 描画する点が無ければスキップ
        if (instanceCount == 0) return commandCount;

        // 先頭instanceCount点だけ描画するコマンド
        _commandArgs[commandCount] = new GraphicsBuffer.IndirectDrawIndexedArgs
        {
            indexCountPerInstance = 6u,
            instanceCount = instanceCount,
            startIndex = 0u,
            baseVertexIndex = 0u,
            startInstance = 0u,
        };

        // シェーダがコマンドIDで引くノード別パラメータ
        _nodeDrawData[commandCount] = new NodeDrawData
        {
            boundsMin = state.bounds.min,
            worldRadius = ComputeWorldPointRadius(state.bounds, instanceCount),
            boundsMax = state.bounds.max,
            pointStart = (uint)state.poolStart,
        };

        return commandCount + 1;
    }

    private float ComputeWorldPointRadius(Bounds bounds, uint drawCount)
    {
        // 描画点が無ければ0（呼び出し側で0は描画スキップ済み）
        if (drawCount == 0) return 0f;

        // ノードの外接球半径（中心から角までの距離）
        float r = bounds.extents.magnitude;

        // 断面(π r²)に drawCount 点を均した時の点間隔を半径の基準にし、_pointSizeScale で太さを調整
        return _pointSizeScale * Mathf.Sqrt(Mathf.PI * r * r / drawCount);
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
        DisposeSharedBuffers();
    }

    // 共有点プールとマルチドロー用バッファを破棄する
    private void DisposeSharedBuffers()
    {
        _pointPool?.Dispose();
        _pointPool = null;

        _commandBuffer?.Dispose();
        _commandBuffer = null;

        _nodeDataBuffer?.Dispose();
        _nodeDataBuffer = null;

        _pointPoolAllocator = null;
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

    private void UnloadIndexBuffer()
    {
        _quadIndex?.Dispose();
        _quadIndex = null;
    }



    private class NodeState
    {
        // 共有点プール内の確保位置（点単位、未ロード時は -1）
        public int poolStart = -1;

        // 共有プールにロード済みの点数（部分ロードのため実点数より少ないことがある）
        public int loadedCount = 0;

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

    // シェーダへ渡すノードごとの描画データ（シェーダ側のNodeDrawDataと並びを一致させる）
    private struct NodeDrawData
    {
        public Vector3 boundsMin;   // ノードboundsの最小（位置復元用）
        public float worldRadius;   // 1点が覆うべきワールド半径
        public Vector3 boundsMax;   // ノードboundsの最大（位置復元用）
        public uint pointStart;     // 共有点プール内の先頭位置
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
