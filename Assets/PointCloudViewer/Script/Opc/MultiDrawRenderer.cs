using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// ロード済みノードを1回のマルチドロー間接描画で描くレンダラー。
/// 毎フレーム、描画コマンドとノード別描画データを詰め直してGPUへ転送する。
/// </summary>
public class MultiDrawRenderer : IDisposable
{
    private readonly OpcFile _file;
    private readonly GpuPointPool _pool;

    private readonly MaterialPropertyBlock _mpb;
    private RenderParams _rp;
    private GraphicsBuffer _quadIndex;
    private GraphicsBuffer _commandBuffer;
    private GraphicsBuffer _nodeDataBuffer;
    private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _commandArgs;
    private readonly NodeDrawData[] _nodeDrawData;
    private readonly Bounds[] _pointSizeBoundsByCommand;
    private readonly Dictionary<int, int> _commandIndexByNode = new Dictionary<int, int>();

    // シェーダプロパティID（毎フレームの文字列ハッシュ計算を避ける）
    private static readonly int _pointsId = Shader.PropertyToID("_Points");
    private static readonly int _nodeDataId = Shader.PropertyToID("_NodeData");
    private static readonly int _worldToPixelId = Shader.PropertyToID("_WorldToPixel");
    private static readonly int _minPixelSizeId = Shader.PropertyToID("_MinPixelSize");
    private static readonly int _maxPixelSizeId = Shader.PropertyToID("_MaxPixelSize");

    public MultiDrawRenderer(Material pointMaterial, OpcFile file, GpuPointPool pool)
    {
        _file = file;
        _pool = pool;

        _rp = new RenderParams(pointMaterial)
        {
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            lightProbeUsage = LightProbeUsage.Off,
            reflectionProbeUsage = ReflectionProbeUsage.Off,
            motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
        };

        _mpb = new MaterialPropertyBlock();

        // 全ノードを1回のマルチドローで描くため、描画範囲はルート全体で固定
        OpcFile.Header header = file.FileHeader;
        Vector3 rootCenter = (header.rootMin + header.rootMax) * 0.5f;
        _rp.worldBounds = new Bounds(rootCenter, header.rootMax - header.rootMin);

        // 点1つを表すQuad（三角形2枚）のインデックスバッファ
        uint[] index = { 0u, 1u, 2u, 2u, 1u, 3u };
        _quadIndex = new GraphicsBuffer(GraphicsBuffer.Target.Index, index.Length, sizeof(uint));
        _quadIndex.SetData(index);

        // マルチドロー用のコマンドバッファとノード描画データバッファを作成（最大数=全ノード数）
        int maxNodes = file.Nodes.Length;
        _commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, maxNodes, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        _nodeDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, System.Runtime.InteropServices.Marshal.SizeOf<NodeDrawData>());
        _commandArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[maxNodes];
        _nodeDrawData = new NodeDrawData[maxNodes];
        _pointSizeBoundsByCommand = new Bounds[maxNodes];
    }

    // 分割描画パスが使うアクセサ（CommandBufferからノード毎に発行するため）
    /// <summary>全ノード共通パラメータを設定済みのMaterialPropertyBlock（Prepare後に有効）</summary>
    public MaterialPropertyBlock MaterialProperties => _mpb;

    /// <summary>点1つを表すQuadのインデックスバッファ</summary>
    public GraphicsBuffer QuadIndex => _quadIndex;

    /// <summary>コマンドindexのノードが描く点数（先頭からこの数だけ描く）</summary>
    public int GetInstanceCount(int commandIndex) => (int)_commandArgs[commandIndex].instanceCount;

    /// <summary>直近のPrepareで詰めた合計描画点数</summary>
    public long LastPreparedPointCount { get; private set; }

    /// <summary>ロード済みノードを1回のマルチドローでカメラへ直接描画する</summary>
    public void Draw(PointStreamer streamer, List<NodeSelection> selections, in CameraState camera, float minPixelSize, float maxPixelSize, float pointSizeScale)
    {
        int commandCount = Prepare(streamer, selections, camera, minPixelSize, maxPixelSize, pointSizeScale);
        if (commandCount == 0) return;
        DrawDirect(commandCount);
    }

    /// <summary>
    /// 描画コマンドとノード描画データを詰めてGPUへ転送し、共通パラメータを設定する。
    /// 実際の描画は行わず、コマンド数を返す（直接描画/分割描画の両方の前段で使う）。
    /// </summary>
    public int Prepare(PointStreamer streamer, List<NodeSelection> selections, in CameraState camera, float minPixelSize, float maxPixelSize, float pointSizeScale)
    {
        // ロード済みノードの描画コマンドとノード描画データを詰める
        int commandCount = 0;
        LastPreparedPointCount = 0;
        _commandIndexByNode.Clear();
        for (int i = 0; i < selections.Count; i++)
        {
            commandCount = AppendSelectionOrFallback(streamer, selections[i].nodeIndex, pointSizeScale, commandCount);
        }

        // 描画対象が無ければ転送もしない
        if (commandCount == 0) return 0;

        // 今フレーム分のコマンドとノード描画データをGPUへ転送
        _commandBuffer.SetData(_commandArgs, 0, 0, commandCount);
        _nodeDataBuffer.SetData(_nodeDrawData, 0, 0, commandCount);

        // 全ノード共通のパラメータを設定（ノード別パラメータは _NodeData から引く）
        _mpb.Clear();
        _mpb.SetBuffer(_pointsId, _pool.Buffer);
        _mpb.SetBuffer(_nodeDataId, _nodeDataBuffer);
        _mpb.SetFloat(_worldToPixelId, camera.worldToPixel);
        _mpb.SetFloat(_minPixelSizeId, minPixelSize);
        _mpb.SetFloat(_maxPixelSizeId, maxPixelSize);
        _rp.matProps = _mpb;

        return commandCount;
    }

    /// <summary>選択ノードが未ロードなら、ロード済みの親ノードを代替描画する。</summary>
    private int AppendSelectionOrFallback(PointStreamer streamer, int nodeIndex, float pointSizeScale, int commandCount)
    {
        return streamer.TryResolveDrawableNode(nodeIndex, out int drawableNodeIndex)
            ? AppendDrawCommandOnce(streamer, drawableNodeIndex, _file.Nodes[nodeIndex].bounds, pointSizeScale, commandCount)
            : commandCount;
    }

    private int AppendDrawCommandOnce(PointStreamer streamer, int nodeIndex, Bounds pointSizeBounds, float pointSizeScale, int commandCount)
    {
        if (_commandIndexByNode.TryGetValue(nodeIndex, out int existingCommandIndex))
        {
            PointStreamer.NodeResidency residency = streamer.GetResidency(nodeIndex);
            uint instanceCount = residency.drawCount;
            if (instanceCount > (uint)residency.loadedCount) instanceCount = (uint)residency.loadedCount;
            if (instanceCount == 0) return commandCount;

            Bounds combinedBounds = _pointSizeBoundsByCommand[existingCommandIndex];
            combinedBounds.Encapsulate(pointSizeBounds);
            _pointSizeBoundsByCommand[existingCommandIndex] = combinedBounds;
            _nodeDrawData[existingCommandIndex].worldRadius = ComputeWorldPointRadius(combinedBounds, instanceCount, pointSizeScale);
            return commandCount;
        }

        int nextCommandCount = AppendDrawCommand(streamer, nodeIndex, pointSizeBounds, pointSizeScale, commandCount);
        if (nextCommandCount > commandCount)
        {
            _commandIndexByNode[nodeIndex] = commandCount;
            _pointSizeBoundsByCommand[commandCount] = pointSizeBounds;
        }
        return nextCommandCount;
    }

    /// <summary>Prepareで詰めたコマンドを1回のマルチドローでカメラへ直接描画する</summary>
    public void DrawDirect(int commandCount)
    {
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
    private int AppendDrawCommand(PointStreamer streamer, int nodeIndex, Bounds pointSizeBounds, float pointSizeScale, int commandCount)
    {
        PointStreamer.NodeResidency residency = streamer.GetResidency(nodeIndex);

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
        Bounds bounds = _file.Nodes[nodeIndex].bounds;
        _nodeDrawData[commandCount] = new NodeDrawData
        {
            boundsMin = bounds.min,
            worldRadius = ComputeWorldPointRadius(pointSizeBounds, instanceCount, pointSizeScale),
            boundsMax = bounds.max,
            pointStart = (uint)residency.poolStart,
        };

        LastPreparedPointCount += instanceCount;

        return commandCount + 1;
    }

    private static float ComputeWorldPointRadius(Bounds bounds, uint drawCount, float pointSizeScale)
    {
        // 描画点が無ければ0（呼び出し側で0は描画スキップ済み）
        if (drawCount == 0) return 0f;

        // ノードの外接球半径（中心から角までの距離）
        float r = bounds.extents.magnitude;

        // 断面(π r²)に drawCount 点を均した時の点間隔を半径の基準にし、pointSizeScale で太さを調整
        return pointSizeScale * Mathf.Sqrt(Mathf.PI * r * r / drawCount);
    }

    public void Dispose()
    {
        _quadIndex?.Dispose();
        _quadIndex = null;

        _commandBuffer?.Dispose();
        _commandBuffer = null;

        _nodeDataBuffer?.Dispose();
        _nodeDataBuffer = null;
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
