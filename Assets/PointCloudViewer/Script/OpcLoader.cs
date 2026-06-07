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

    // 共有点プール容量の自動算出に使う余裕係数（部分ロードの必要量 ≈ 予算×先読み係数 に視界外残留分の余裕を加える）
    private const int PoolCapacityBudgetMultiplier = 4;

    // 開いたOPCファイル（ヘッダ+統合ノード索引）。読み込み失敗時はnullのまま。
    private OpcFile _file;
    private OpcFile.Header _header;
    private OpcFile.Node[] _nodes;
    private Camera _mainCamera;
    private List<VisibleNode> _visibleNodes = new List<VisibleNode>();
    private List<NodeSelection> _selections = new List<NodeSelection>();
    private List<uint> _desiredCounts = new List<uint>();
    private List<uint> _finalAllocs = new List<uint>();
    private List<int> _activeIndices = new List<int>();
    private Queue<int> _nodeIndicesToCheck = new Queue<int>();
    private GraphicsBuffer _quadIndex;
    private MaterialPropertyBlock _mpb;
    private RenderParams _rp;
    private readonly Plane[] _planes = new Plane[6];

    // 全ノード共有のGPU点プールと、ロード/アンロードを管理するストリーマ
    private GpuPointPool _pointPool;
    private PointStreamer _streamer;

    // マルチドロー用のコマンド列とノードごとの描画データ（毎フレーム詰め直してGPUへ転送）
    private GraphicsBuffer _commandBuffer;
    private GraphicsBuffer _nodeDataBuffer;
    private GraphicsBuffer.IndirectDrawIndexedArgs[] _commandArgs;
    private NodeDrawData[] _nodeDrawData;

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

    // Profiler用マーカー（Unity ProfilerのCPU Usageに"OpcLoader.～"として表示される）
    private static readonly ProfilerMarker _markerGetVisibleNodes = new ProfilerMarker("OpcLoader.GetVisibleNodes");
    private static readonly ProfilerMarker _markerApplyPointBudget = new ProfilerMarker("OpcLoader.ApplyPointBudget");
    private static readonly ProfilerMarker _markerSyncStreaming = new ProfilerMarker("OpcLoader.SyncNodeStreaming");
    private static readonly ProfilerMarker _markerDrawPointCloud = new ProfilerMarker("OpcLoader.DrawPointCloud");



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
        _nodes = _file.Nodes;
        SetupRendering();
        CreateStreaming();
    }

    // 共有点プールとストリーマを作成する
    private void CreateStreaming()
    {
        _pointPool = new GpuPointPool(ComputePointPoolCapacity());
        _streamer = new PointStreamer(_file, GetFullPath(_filePath), _pointPool, _maxLoadsPerFrame, _unloadDelayFrames);
    }

    // 共有点プールの容量（点数）を自動算出する。
    // 部分ロードの必要量（≈ 予算×先読み係数 + 視界外残留分）を賄える係数を予算に掛け、ファイル内の全実点数を上限とする。
    private int ComputePointPoolCapacity()
    {
        // 小さいファイルは全点数ぴったり、大きいファイルは予算×係数で頭打ち
        long capacity = Math.Min(_file.TotalNodePoints, (long)PoolCapacityBudgetMultiplier * _pointBudget);
        capacity = Math.Min(capacity, int.MaxValue);

        Debug.Log($"OpcLoader: 点プール容量を {capacity:N0} 点（約 {capacity * 12 / (1024 * 1024)} MB）に自動設定しました。");
        return (int)capacity;
    }

    // "Assets/～" 形式のパスを絶対パスに変換する
    private string GetFullPath(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        return Path.Combine(Application.dataPath, shortFilePath);
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

        // マルチドロー用のコマンドバッファとノード描画データバッファを作成（最大数=全ノード数）
        int maxNodes = _nodes.Length;
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
            _streamer.Update(_selections, Time.frameCount);
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
        _nodeIndicesToCheck.Clear();

        // カメラの視錐台平面
        GeometryUtility.CalculateFrustumPlanes(_mainCamera, _planes);

        // カメラ情報をフレームに1回だけキャッシュ（ComputeAreaPx用）
        Transform camTransform = _mainCamera.transform;
        _camPos = camTransform.position;
        _camFwd = camTransform.forward;
        _worldToPixel = _mainCamera.projectionMatrix.m11 * Screen.height * 0.5f;
        _minDepth = _mainCamera.nearClipPlane + 1e-3f;
        _maxAreaPx = (float)Screen.width * Screen.height;

        // ルートノードを追加
        _nodeIndicesToCheck.Enqueue(_file.RootIndex);

        // 探索ループ
        while (_nodeIndicesToCheck.Count > 0)
        {
            // 処理対象のノードを取得
            int nodeIndex = _nodeIndicesToCheck.Dequeue();
            OpcFile.Node node = _nodes[nodeIndex];

            // ノードが見えているか
            if (!GeometryUtility.TestPlanesAABB(_planes, node.bounds))
            {
                // 子の判定をスキップ
                continue;
            }

            // 代表点では足りない枝は子へ降りる
            if (!node.isLeaf && !CanStopAtBranch(node))
            {
                for (int i = 0; i < 8; i++)
                {
                    int childIndex = _file.ChildIndices[node.childStart + i];
                    if (childIndex >= 0)
                    {
                        _nodeIndicesToCheck.Enqueue(childIndex);
                    }
                }
                continue;
            }

            // 葉、または代表点で足りる枝は描画候補に追加
            _visibleNodes.Add(new VisibleNode
            {
                index = nodeIndex,
                pointCount = node.pointCount,
                areaPx = ComputeAreaPx(node.bounds),
            });
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
        // 選択結果リスト / 各種バッファを初期化
        _selections.Clear();
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

        // 3パス目: 配分結果を選択結果リストに反映
        for (int i = 0; i < _visibleNodes.Count; i++)
        {
            VisibleNode node = _visibleNodes[i];

            // 実点数を上限としてクランプ
            uint alloc = _finalAllocs[i];
            if (alloc > node.pointCount) alloc = node.pointCount;

            _selections.Add(new NodeSelection
            {
                nodeIndex = node.index,
                drawCount = alloc,
                areaPx = node.areaPx,
            });
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

    private bool CanStopAtBranch(OpcFile.Node node)
    {
        // 代表点が無い枝は止められない
        if (node.pointCount == 0) return false;

        // 画面ピクセル面積
        float areaPx = ComputeAreaPx(node.bounds);

        // 画面に必要な点数（面積 × 1ピクセル当たりの目標点数）
        float requiredCount = areaPx * _maxPointsPerPixel;

        // 枝の代表点が必要点数を満たすなら、葉に降りなくてよい
        return node.pointCount >= requiredCount;
    }

    private void DrawPointCloud()
    {
        // ロード済みノードの描画コマンドとノード描画データを詰める
        int commandCount = 0;
        IReadOnlyList<int> loadedNodeIndices = _streamer.LoadedNodeIndices;
        for (int i = 0; i < loadedNodeIndices.Count; i++)
        {
            commandCount = AppendDrawCommand(loadedNodeIndices[i], commandCount);
        }

        // 描画対象が無ければ何もしない
        if (commandCount == 0) return;

        // 今フレーム分のコマンドとノード描画データをGPUへ転送
        _commandBuffer.SetData(_commandArgs, 0, 0, commandCount);
        _nodeDataBuffer.SetData(_nodeDrawData, 0, 0, commandCount);

        // 全ノード共通のパラメータを設定（ノード別パラメータは _NodeData から引く）
        _mpb.Clear();
        _mpb.SetBuffer(_pointsId, _pointPool.Buffer);
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
    private int AppendDrawCommand(int nodeIndex, int commandCount)
    {
        PointStreamer.NodeResidency residency = _streamer.GetResidency(nodeIndex);

        // 描画点数はロード済み点数を上限とする（追加ロードが追いつくまでの間はある分だけ描く）
        uint instanceCount = residency.drawCount;
        if (instanceCount > (uint)residency.loadedCount) instanceCount = (uint)residency.loadedCount;

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
        Bounds bounds = _nodes[nodeIndex].bounds;
        _nodeDrawData[commandCount] = new NodeDrawData
        {
            boundsMin = bounds.min,
            worldRadius = ComputeWorldPointRadius(bounds, instanceCount),
            boundsMax = bounds.max,
            pointStart = (uint)residency.poolStart,
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
        // ストリーマを先に止める（停止後に新しい結果が出ないようにする）
        _streamer?.Dispose();
        _streamer = null;

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
    }

    private void UnloadIndexBuffer()
    {
        _quadIndex?.Dispose();
        _quadIndex = null;
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
        public uint pointCount;
        public float areaPx;
    }
}
