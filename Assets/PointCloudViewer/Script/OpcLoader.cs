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
    private readonly Plane[] _planes = new Plane[6];

    // 今フレームのカメラ情報スナップショット（LOD選択と点サイズ計算に使う）
    private CameraState _cameraState;

    // LOD選択（可視判定+Point Budget配分）と、その選択結果
    private NodeLodSelector _lodSelector;
    private List<NodeSelection> _selections = new List<NodeSelection>();

    // 全ノード共有のGPU点プールと、ロード/アンロードを管理するストリーマ
    private GpuPointPool _pointPool;
    private PointStreamer _streamer;

    // マルチドロー用のコマンド列とノードごとの描画データ（毎フレーム詰め直してGPUへ転送）
    private GraphicsBuffer _quadIndex;
    private MaterialPropertyBlock _mpb;
    private RenderParams _rp;
    private GraphicsBuffer _commandBuffer;
    private GraphicsBuffer _nodeDataBuffer;
    private GraphicsBuffer.IndirectDrawIndexedArgs[] _commandArgs;
    private NodeDrawData[] _nodeDrawData;

    // シェーダプロパティID（毎フレームの文字列ハッシュ計算を避ける）
    private static readonly int _pointsId = Shader.PropertyToID("_Points");
    private static readonly int _nodeDataId = Shader.PropertyToID("_NodeData");
    private static readonly int _worldToPixelId = Shader.PropertyToID("_WorldToPixel");
    private static readonly int _minPixelSizeId = Shader.PropertyToID("_MinPixelSize");
    private static readonly int _maxPixelSizeId = Shader.PropertyToID("_MaxPixelSize");

    // Profiler用マーカー（Unity ProfilerのCPU Usageに"OpcLoader.～"として表示される）
    private static readonly ProfilerMarker _markerGetVisibleNodes = new ProfilerMarker("OpcLoader.GetVisibleNodes");
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
        _lodSelector = new NodeLodSelector(_file, _maxPointsPerPixel);
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
        // カメラ情報を取り込み、視界に入っているノードと描画点数を選択する
        using (_markerGetVisibleNodes.Auto())
        {
            _cameraState = CameraState.Capture(_mainCamera, _planes);
            _lodSelector.Select(_cameraState, _pointBudget, _selections);
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
        _mpb.SetFloat(_worldToPixelId, _cameraState.worldToPixel);
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
}
