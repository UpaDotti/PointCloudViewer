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
    // 1画素点で描く時の描画パラメータ（点用マテリアルが未設定ならnull）
    private RenderParams? _pointRp;
    private GraphicsBuffer _quadIndex;
    private GraphicsBuffer _commandBuffer;
    // 1画素点用のコマンド（1点=1頂点の非Indexed形式。Quad用コマンドと同じ並び）
    private GraphicsBuffer _pointCommandBuffer;
    private GraphicsBuffer _nodeDataBuffer;
    private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _commandArgs;
    private readonly GraphicsBuffer.IndirectDrawArgs[] _pointCommandArgs;
    // 円が大きいノードを先頭へ集める時の作業配列（毎フレームの割り当てを避けるため使い回す）
    private readonly GraphicsBuffer.IndirectDrawIndexedArgs[] _sortedCommandArgs;
    private readonly GraphicsBuffer.IndirectDrawArgs[] _sortedPointCommandArgs;
    private readonly NodeDrawData[] _sortedNodeDrawData;
    private readonly NodeDrawData[] _nodeDrawData;
    private readonly Bounds[] _pointSizeBoundsByCommand;
    private readonly Dictionary<int, int> _commandIndexByNode = new Dictionary<int, int>();

    // シェーダプロパティID（毎フレームの文字列ハッシュ計算を避ける）
    private static readonly int _pointsId = Shader.PropertyToID("_Points");
    private static readonly int _nodeDataId = Shader.PropertyToID("_NodeData");
    private static readonly int _worldToPixelId = Shader.PropertyToID("_WorldToPixel");
    private static readonly int _minPixelSizeId = Shader.PropertyToID("_MinPixelSize");
    private static readonly int _maxPixelSizeId = Shader.PropertyToID("_MaxPixelSize");

    public MultiDrawRenderer(Material pointMaterial, Material pixelPointMaterial, OpcFile file, GpuPointPool pool)
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

        // 1画素点用の描画パラメータ（マテリアル以外はQuad用と同じ）
        if (pixelPointMaterial != null)
        {
            RenderParams pointRp = _rp;
            pointRp.material = pixelPointMaterial;
            _pointRp = pointRp;
        }

        // 点1つを表すQuad（三角形2枚）のインデックスバッファ
        uint[] index = { 0u, 1u, 2u, 2u, 1u, 3u };
        _quadIndex = new GraphicsBuffer(GraphicsBuffer.Target.Index, index.Length, sizeof(uint));
        _quadIndex.SetData(index);

        // マルチドロー用のコマンドバッファとノード描画データバッファを作成（最大数=全ノード数）
        int maxNodes = file.Nodes.Length;
        _commandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, maxNodes, GraphicsBuffer.IndirectDrawIndexedArgs.size);
        _nodeDataBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxNodes, System.Runtime.InteropServices.Marshal.SizeOf<NodeDrawData>());
        _pointCommandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, maxNodes, GraphicsBuffer.IndirectDrawArgs.size);
        _commandArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[maxNodes];
        _pointCommandArgs = new GraphicsBuffer.IndirectDrawArgs[maxNodes];
        _sortedCommandArgs = new GraphicsBuffer.IndirectDrawIndexedArgs[maxNodes];
        _sortedPointCommandArgs = new GraphicsBuffer.IndirectDrawArgs[maxNodes];
        _sortedNodeDrawData = new NodeDrawData[maxNodes];
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

    /// <summary>直近のPrepareで先頭へ集めた「円が大きいノード」のコマンド数（並べ替えなしなら0）</summary>
    public int LargeCommandCount { get; private set; }

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
    /// <param name="largePixelRadius">
    /// 0より大きければ、画面半径がこの値を超えうるノードのコマンドを先頭へ集め、その数を LargeCommandCount に入れる。
    /// 0以下なら並べ替えず、LargeCommandCount は0。
    /// </param>
    public int Prepare(PointStreamer streamer, List<NodeSelection> selections, in CameraState camera, float minPixelSize, float maxPixelSize, float pointSizeScale,
        float largePixelRadius = 0f)
    {
        // ロード済みノードの描画コマンドとノード描画データを詰める
        int commandCount = 0;
        LastPreparedPointCount = 0;
        LargeCommandCount = 0;
        _commandIndexByNode.Clear();
        for (int i = 0; i < selections.Count; i++)
        {
            commandCount = AppendSelectionOrFallback(streamer, selections[i].nodeIndex, pointSizeScale, commandCount);
        }

        // 描画対象が無ければ転送もしない
        if (commandCount == 0) return 0;

        // 円が大きいノードを先頭へ集める（集め直し方式では先頭の範囲だけQuadで描くため）
        if (largePixelRadius > 0f)
        {
            LargeCommandCount = MoveLargeCommandsToFront(commandCount, camera, maxPixelSize, largePixelRadius);
        }

        // 今フレーム分のコマンドとノード描画データをGPUへ転送
        _commandBuffer.SetData(_commandArgs, 0, 0, commandCount);
        _pointCommandBuffer.SetData(_pointCommandArgs, 0, 0, commandCount);
        _nodeDataBuffer.SetData(_nodeDrawData, 0, 0, commandCount);

        // 全ノード共通のパラメータを設定（ノード別パラメータは _NodeData から引く）
        _mpb.Clear();
        _mpb.SetBuffer(_pointsId, _pool.Buffer);
        _mpb.SetBuffer(_nodeDataId, _nodeDataBuffer);
        _mpb.SetFloat(_worldToPixelId, camera.worldToPixel);
        _mpb.SetFloat(_minPixelSizeId, minPixelSize);
        _mpb.SetFloat(_maxPixelSizeId, maxPixelSize);
        _rp.matProps = _mpb;
        if (_pointRp.HasValue)
        {
            RenderParams pointRp = _pointRp.Value;
            pointRp.matProps = _mpb;
            _pointRp = pointRp;
        }

        return commandCount;
    }

    /// <summary>
    /// ノード中心での画面半径がしきい値を超えるコマンドを、順番を保ったまま先頭へ集め、その数を返す。
    /// 半径はシェーダと同じ式（ワールド半径 × 変換係数 ÷ 奥行き、上限maxPixelSize）で、奥行きはノード中心の値を使う。
    /// </summary>
    private int MoveLargeCommandsToFront(int commandCount, in CameraState camera, float maxPixelSize, float largePixelRadius)
    {
        // 大きいコマンド → 小さいコマンドの順に作業配列へ写す（2回走査して順番を保つ）
        int writeIndex = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            bool wantLarge = pass == 0;
            for (int i = 0; i < commandCount; i++)
            {
                if (IsLargeCommand(i, camera, maxPixelSize, largePixelRadius) != wantLarge) continue;
                _sortedCommandArgs[writeIndex] = _commandArgs[i];
                _sortedPointCommandArgs[writeIndex] = _pointCommandArgs[i];
                _sortedNodeDrawData[writeIndex] = _nodeDrawData[i];
                writeIndex++;
            }
            if (wantLarge) LargeCommandCount = writeIndex;
        }

        // 作業配列を本体へ戻す
        Array.Copy(_sortedCommandArgs, _commandArgs, commandCount);
        Array.Copy(_sortedPointCommandArgs, _pointCommandArgs, commandCount);
        Array.Copy(_sortedNodeDrawData, _nodeDrawData, commandCount);
        return LargeCommandCount;
    }

    // コマンドのノードで、中心の奥行きでの画面半径がしきい値を超えるか
    // （手前側の奥行きで判定すると近くのノードがほぼ全部Quadに回り、点の半数以上がQuadになったため中心で判定する）
    private bool IsLargeCommand(int commandIndex, in CameraState camera, float maxPixelSize, float largePixelRadius)
    {
        NodeDrawData node = _nodeDrawData[commandIndex];
        Vector3 center = (node.boundsMin + node.boundsMax) * 0.5f;

        // ノード中心の奥行き（カメラの近く・背後は下限でクランプ）
        float centerDepth = Vector3.Dot(center - camera.position, camera.forward);
        if (centerDepth < camera.minDepth) centerDepth = camera.minDepth;

        float pixelRadius = Mathf.Min(node.worldRadius * camera.worldToPixel / centerDepth, maxPixelSize);
        return pixelRadius > largePixelRadius;
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
        DrawDirectRange(0, commandCount);
    }

    /// <summary>Prepareで詰めたコマンドのうち、startCommandからcommandCount個だけQuadで描画する</summary>
    public void DrawDirectRange(int startCommand, int commandCount)
    {
        if (commandCount <= 0) return;
        // 指定範囲のノードを1回のマルチドローで描画
        Graphics.RenderPrimitivesIndexedIndirect(
            _rp,
            MeshTopology.Triangles,
            _quadIndex,
            _commandBuffer,
            commandCount,
            startCommand
        );
    }

    /// <summary>
    /// Prepareで詰めたコマンドを、全点1画素の点（1点=1頂点）として1回のマルチドローで描画する。
    /// 点用マテリアルが未設定なら何もせずfalseを返す。
    /// </summary>
    public bool DrawDirectPixelPoints(int commandCount)
    {
        if (!_pointRp.HasValue) return false;

        Graphics.RenderPrimitivesIndirect(
            _pointRp.Value,
            MeshTopology.Points,
            _pointCommandBuffer,
            commandCount,
            0
        );
        return true;
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

        // 1画素点用の同じノードのコマンド（頂点番号=点番号）
        _pointCommandArgs[commandCount] = new GraphicsBuffer.IndirectDrawArgs
        {
            vertexCountPerInstance = instanceCount,
            instanceCount = 1u,
            startVertex = 0u,
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

        _pointCommandBuffer?.Dispose();
        _pointCommandBuffer = null;

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
