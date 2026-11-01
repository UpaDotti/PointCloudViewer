using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// OpcLoader（MonoBehaviour）が用意した描画データを、RendererFeature側のPassへ渡す受け渡し口。
/// URPがFeatureを生成するため両者は直接参照できない。毎フレームOpcLoaderが更新し、Passが読む。
/// </summary>
public static class OpcSplitRenderBridge
{
    // 分割描画が有効か（OpcLoaderが毎フレーム更新する）
    public static bool Active;

    // 今フレームのコマンドを詰め終えたレンダラー（PrepareでMPB/QuadIndex/コマンド数が確定済み）
    public static MultiDrawRenderer Renderer;

    // 今フレームのコマンド数（ロード済みノード数）
    public static int CommandCount;

    // 今フレームでPrepareされた合計描画点数
    public static long PointCount;
}

/// <summary>
/// 点群を点キャッシュ方式で描くFeature。
/// 受け持ちの点を1画素1点に整列して点一覧（ワールド座標・色・半径）へ詰め、
/// 点一覧を毎フレーム現在のカメラで円として描き直す。
/// 点一覧はN本持ち、毎フレーム1本だけを受け持ちの点（1/N）で作り直す。表示は全N本を描く。
/// </summary>
public class OpcSplitRenderFeature : ScriptableRendererFeature
{
    // 整列パス用マテリアル（Custom/PointCloud_SplitFill）
    [SerializeField]
    private Material _fillMaterial;

    // 表示パス用マテリアル（Custom/PointCloud_CacheSplat）
    [SerializeField]
    private Material _splatMaterial;

    // 詰め直しパス用compute（PointCloudCacheCompact.compute）
    [SerializeField]
    private ComputeShader _compactShader;

    // 何フレームに分けて全点を整列するか（=保持する点一覧の本数）
    [SerializeField]
    [Range(1, 8)]
    private int _splitCount = 4;

    private OpcSplitRenderPass _pass;

    public override void Create()
    {
        _pass = new OpcSplitRenderPass(_fillMaterial, _splatMaterial, _compactShader)
        {
            // URPのシーン描画が一通り終わった後に点群を描く
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 分割描画が無効、またはマテリアル/compute未設定なら何もしない
        if (!OpcSplitRenderBridge.Active || _fillMaterial == null || _splatMaterial == null || _compactShader == null) return;

        // ゲームビューのメインカメラだけを対象にする（Sceneビュー等が点一覧を汚さないため）
        ref CameraData cameraData = ref renderingData.cameraData;
        if (cameraData.cameraType != CameraType.Game) return;
        if (cameraData.camera != Camera.main) return;

        // 描画するコマンドが無ければ積まない
        if (OpcSplitRenderBridge.Renderer == null || OpcSplitRenderBridge.CommandCount == 0) return;

        // 分割数を反映（Inspectorでの変更を毎フレーム拾う）
        _pass.SplitCount = Mathf.Max(1, _splitCount);

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
        _pass = null;
    }
}

/// <summary>
/// 整列（1画素1点）→ 詰め直し（点一覧へ）→ 表示（全点一覧を円で描く）の3パスを積むパス。
/// </summary>
public class OpcSplitRenderPass : ScriptableRenderPass
{
    private readonly Material _fillMaterial;
    private readonly Material _splatMaterial;
    private readonly ComputeShader _compactShader;
    private readonly int _resetArgsKernel;
    private readonly int _compactKernel;

    // 何フレームに分けて全点を整列するか（Featureから毎フレーム設定される）
    public int SplitCount = 4;

    // スライスごとの点一覧（容量=画素数）と、その点数を入れる間接描画の引数（フレームを越えて保持する）
    private GraphicsBuffer[] _caches;
    private GraphicsBuffer[] _cacheArgs;
    // その点一覧を一度でも作ったか（未作成のスライスは表示しない）
    private bool[] _cacheValid;
    // 表示パスでスライスの点一覧を束縛するMPB（1枚を使い回すと実行時に上書きされるため別々に持つ）
    private MaterialPropertyBlock[] _splatMpbs;
    private int _cacheWidth;
    private int _cacheHeight;
    private int _allocatedSplit;
    // 詰め直しで画素をばらけた順に走査するための係数（画素数と互いに素）
    private int _scrambleMultiplier;

    // 詰め直しの1スレッドグループのスレッド数（compute側のnumthreadsと一致させる）
    private const int CompactThreadsPerGroup = 64;
    // 1次元あたりのスレッドグループ数の上限（超える分は2次元目へ回す）
    private const int MaxGroupsPerDimension = 65535;
    // かき混ぜ係数の目安（画素数×黄金比の端数。連続する番号が画面全体に散らばる）
    private const double GoldenRatioFraction = 0.6180339887;
    // compute側の剰余乗算が扱える画素数の上限（2^24）
    private const int MaxScramblePixelCount = 1 << 24;

    // 点一覧の1点の大きさ（float3 位置 + uint 色 + float 半径）
    private const int CachePointStride = 20;
    // 間接描画の引数（IndirectDrawIndexedArgs）のuint数
    private const int IndexedArgsCount = 5;

    private static readonly int _commandIdProp = Shader.PropertyToID("_CommandId");
    private static readonly int _sliceOffsetProp = Shader.PropertyToID("_SliceOffset");
    private static readonly int _sliceStrideProp = Shader.PropertyToID("_SliceStride");
    private static readonly int _fillPositionRadiusProp = Shader.PropertyToID("_FillPositionRadius");
    private static readonly int _fillColorProp = Shader.PropertyToID("_FillColor");
    private static readonly int _fillSizeProp = Shader.PropertyToID("_FillSize");
    private static readonly int _cacheProp = Shader.PropertyToID("_Cache");
    private static readonly int _argsProp = Shader.PropertyToID("_Args");
    private static readonly int _scrambleMultiplierProp = Shader.PropertyToID("_ScrambleMultiplier");
    private static readonly int _groupsXProp = Shader.PropertyToID("_GroupsX");
    private static readonly int _worldToPixelProp = Shader.PropertyToID("_WorldToPixel");
    private static readonly int _minPixelSizeProp = Shader.PropertyToID("_MinPixelSize");
    private static readonly int _maxPixelSizeProp = Shader.PropertyToID("_MaxPixelSize");

    // 整列パスへ渡すデータ
    private class FillPassData
    {
        public Material material;
        public MultiDrawRenderer renderer;
        public int commandCount;
        public int sliceIndex;   // 今フレームで整列するスライス番号
        public int splitCount;   // スライス間隔（stride）
    }

    // 詰め直しパスへ渡すデータ
    private class CompactPassData
    {
        public ComputeShader shader;
        public int resetArgsKernel;
        public int compactKernel;
        public TextureHandle fillPositionRadius;
        public TextureHandle fillColor;
        public BufferHandle cache;
        public BufferHandle cacheArgs;
        public int width;
        public int height;
        public int scrambleMultiplier;
    }

    // 表示パスへ渡すデータ
    private class SplatPassData
    {
        public Material material;
        public MaterialPropertyBlock[] mpbs;
        public GraphicsBuffer quadIndex;
        public GraphicsBuffer[] cacheArgs;
        public bool[] cacheValid;
    }

    public OpcSplitRenderPass(Material fillMaterial, Material splatMaterial, ComputeShader compactShader)
    {
        _fillMaterial = fillMaterial;
        _splatMaterial = splatMaterial;
        _compactShader = compactShader;
        if (_compactShader != null)
        {
            _resetArgsKernel = _compactShader.FindKernel("ResetArgs");
            _compactKernel = _compactShader.FindKernel("Compact");
        }
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        MultiDrawRenderer renderer = OpcSplitRenderBridge.Renderer;
        int commandCount = OpcSplitRenderBridge.CommandCount;
        if (renderer == null || commandCount == 0 || _fillMaterial == null || _splatMaterial == null || _compactShader == null) return;

        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        if (!resourceData.activeColorTexture.IsValid() || !resourceData.activeDepthTexture.IsValid()) return;

        Benchmarker.Record("OpcSplit.Draw.Commands", commandCount);
        Benchmarker.Record("OpcSplit.Draw.Points", OpcSplitRenderBridge.PointCount);

        // 点一覧を確保（画面サイズ/分割数が変われば作り直す）
        int splitCount = Mathf.Max(1, SplitCount);
        Benchmarker.Record("OpcSplit.SplitCount", splitCount);
        RenderTextureDescriptor cameraDesc = cameraData.cameraTargetDescriptor;
        EnsureCaches(cameraDesc.width, cameraDesc.height, splitCount);

        // 今フレームで作り直すスライス
        int sliceIndex = Time.frameCount % splitCount;

        // 整列用の作業テクスチャ（このフレームで使い切るのでRenderGraphの一時テクスチャにする）
        TextureHandle fillPositionRadius = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph,
            new RenderTextureDescriptor(_cacheWidth, _cacheHeight, GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormat.None),
            "_OpcFillPositionRadius", false);
        TextureHandle fillColor = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph,
            new RenderTextureDescriptor(_cacheWidth, _cacheHeight, GraphicsFormat.R32_UInt, GraphicsFormat.None),
            "_OpcFillColor", false);
        TextureHandle fillDepth = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph,
            new RenderTextureDescriptor(_cacheWidth, _cacheHeight, GraphicsFormat.None, GraphicsFormat.D32_SFloat),
            "_OpcFillDepth", false);

        // 今フレームのスライスの点一覧を作り直してから、全スライスを表示する
        BufferHandle cache = renderGraph.ImportBuffer(_caches[sliceIndex]);
        BufferHandle cacheArgs = renderGraph.ImportBuffer(_cacheArgs[sliceIndex]);
        RecordFill(renderGraph, renderer, commandCount, sliceIndex, splitCount, fillPositionRadius, fillColor, fillDepth);
        RecordCompact(renderGraph, fillPositionRadius, fillColor, cache, cacheArgs);
        _cacheValid[sliceIndex] = true;

        RecordSplat(renderGraph, resourceData, renderer, splitCount);
    }

    // 受け持ちの点を1画素の点として作業テクスチャへ描く（深度テストで1画素1点＝最も手前に絞る）
    private void RecordFill(RenderGraph renderGraph, MultiDrawRenderer renderer, int commandCount, int sliceIndex, int splitCount,
        TextureHandle fillPositionRadius, TextureHandle fillColor, TextureHandle fillDepth)
    {
        using (var builder = renderGraph.AddRasterRenderPass<FillPassData>("OpcSplitFill", out FillPassData data))
        {
            data.material = _fillMaterial;
            data.renderer = renderer;
            data.commandCount = commandCount;
            data.sliceIndex = sliceIndex;
            data.splitCount = splitCount;

            builder.SetRenderAttachment(fillPositionRadius, 0, AccessFlags.Write);
            builder.SetRenderAttachment(fillColor, 1, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(fillDepth, AccessFlags.Write);
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((FillPassData d, RasterGraphContext context) => ExecuteFill(d, context));
        }
    }

    // 点のある画素を点一覧へ詰め、点数を間接描画の引数へ書く
    private void RecordCompact(RenderGraph renderGraph, TextureHandle fillPositionRadius, TextureHandle fillColor,
        BufferHandle cache, BufferHandle cacheArgs)
    {
        using (var builder = renderGraph.AddComputePass<CompactPassData>("OpcSplitCompact", out CompactPassData data))
        {
            data.shader = _compactShader;
            data.resetArgsKernel = _resetArgsKernel;
            data.compactKernel = _compactKernel;
            data.fillPositionRadius = fillPositionRadius;
            data.fillColor = fillColor;
            data.cache = cache;
            data.cacheArgs = cacheArgs;
            data.width = _cacheWidth;
            data.height = _cacheHeight;
            data.scrambleMultiplier = _scrambleMultiplier;

            builder.UseTexture(fillPositionRadius, AccessFlags.Read);
            builder.UseTexture(fillColor, AccessFlags.Read);
            builder.UseBuffer(cache, AccessFlags.Write);
            builder.UseBuffer(cacheArgs, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((CompactPassData d, ComputeGraphContext context) => ExecuteCompact(d, context));
        }
    }

    // 全スライスの点一覧を現在のカメラで円として画面へ描く
    private void RecordSplat(RenderGraph renderGraph, UniversalResourceData resourceData, MultiDrawRenderer renderer, int splitCount)
    {
        // 点サイズの変換係数と上下限はレンダラーのMPBから引き継ぎ、点一覧はスライスごとに差し替える
        MaterialPropertyBlock rendererMpb = renderer.MaterialProperties;
        float worldToPixel = rendererMpb.GetFloat(_worldToPixelProp);
        float minPixelSize = rendererMpb.GetFloat(_minPixelSizeProp);
        float maxPixelSize = rendererMpb.GetFloat(_maxPixelSizeProp);

        using (var builder = renderGraph.AddRasterRenderPass<SplatPassData>("OpcSplitSplat", out SplatPassData data))
        {
            int validCount = 0;
            for (int i = 0; i < splitCount; i++)
            {
                if (!_cacheValid[i]) continue;
                validCount++;

                _splatMpbs[i].Clear();
                _splatMpbs[i].SetFloat(_worldToPixelProp, worldToPixel);
                _splatMpbs[i].SetFloat(_minPixelSizeProp, minPixelSize);
                _splatMpbs[i].SetFloat(_maxPixelSizeProp, maxPixelSize);
                _splatMpbs[i].SetBuffer(_cacheProp, _caches[i]);

                builder.UseBuffer(renderGraph.ImportBuffer(_caches[i]), AccessFlags.Read);
                builder.UseBuffer(renderGraph.ImportBuffer(_cacheArgs[i]), AccessFlags.Read);
            }
            Benchmarker.Record("OpcSplit.Splat.DrawCalls", validCount);

            data.material = _splatMaterial;
            data.mpbs = _splatMpbs;
            data.quadIndex = renderer.QuadIndex;
            data.cacheArgs = _cacheArgs;
            data.cacheValid = _cacheValid;

            builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((SplatPassData d, RasterGraphContext context) => ExecuteSplat(d, context));
        }
    }

    // 作業テクスチャをクリアし、このスライスが受け持つ点を1画素の点で描く
    private static void ExecuteFill(FillPassData data, RasterGraphContext context)
    {
        RasterCommandBuffer cmd = context.cmd;

        // 半径0=点なし / 深度=最遠でクリア（反転ZはUnity内部処理）
        cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1f, 0);

        // このスライスが受け持つ点（offset番目からstride間隔）をシェーダへ伝える
        cmd.SetGlobalInt(_sliceOffsetProp, data.sliceIndex);
        cmd.SetGlobalInt(_sliceStrideProp, data.splitCount);

        MaterialPropertyBlock mpb = data.renderer.MaterialProperties;
        int fillDrawCalls = 0;
        long fillPointCount = 0;
        for (int i = 0; i < data.commandCount; i++)
        {
            // このスライスが受け持つ点数（offset番目からstride間隔で数える）
            int fullCount = data.renderer.GetInstanceCount(i);
            if (data.sliceIndex >= fullCount) continue;
            int pointCount = (fullCount - data.sliceIndex + data.splitCount - 1) / data.splitCount;

            // 1点=1頂点の点として描く
            cmd.SetGlobalInt(_commandIdProp, i);
            cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Points, pointCount, 1, mpb);
            fillDrawCalls++;
            fillPointCount += pointCount;
        }

        Benchmarker.Record("OpcSplit.Fill.DrawCalls", fillDrawCalls);
        Benchmarker.Record("OpcSplit.Fill.Points", fillPointCount);
    }

    // 引数を初期化してから、点のある画素を点一覧へ詰める
    private static void ExecuteCompact(CompactPassData data, ComputeGraphContext context)
    {
        ComputeCommandBuffer cmd = context.cmd;

        cmd.SetComputeBufferParam(data.shader, data.resetArgsKernel, _argsProp, data.cacheArgs);
        cmd.DispatchCompute(data.shader, data.resetArgsKernel, 1, 1, 1);

        cmd.SetComputeTextureParam(data.shader, data.compactKernel, _fillPositionRadiusProp, data.fillPositionRadius);
        cmd.SetComputeTextureParam(data.shader, data.compactKernel, _fillColorProp, data.fillColor);
        cmd.SetComputeBufferParam(data.shader, data.compactKernel, _cacheProp, data.cache);
        cmd.SetComputeBufferParam(data.shader, data.compactKernel, _argsProp, data.cacheArgs);
        cmd.SetComputeIntParams(data.shader, _fillSizeProp, data.width, data.height);
        cmd.SetComputeIntParam(data.shader, _scrambleMultiplierProp, data.scrambleMultiplier);

        // 全画素を1スレッド1画素で回す（グループ数が1次元の上限を超える分は2次元目へ回す）
        int groupCount = (data.width * data.height + CompactThreadsPerGroup - 1) / CompactThreadsPerGroup;
        int groupsX = Mathf.Min(groupCount, MaxGroupsPerDimension);
        int groupsY = (groupCount + groupsX - 1) / groupsX;
        cmd.SetComputeIntParam(data.shader, _groupsXProp, groupsX);
        cmd.DispatchCompute(data.shader, data.compactKernel, groupsX, groupsY, 1);
    }

    // 有効なスライスごとに、点一覧の点数だけQuadを間接描画する
    private static void ExecuteSplat(SplatPassData data, RasterGraphContext context)
    {
        for (int i = 0; i < data.cacheArgs.Length; i++)
        {
            if (!data.cacheValid[i]) continue;
            context.cmd.DrawProceduralIndirect(data.quadIndex, Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, data.cacheArgs[i], 0, data.mpbs[i]);
        }
    }

    // スライスごとの点一覧と間接描画の引数を必要に応じて確保し直す（容量=画素数。1画素1点なので溢れない）
    private void EnsureCaches(int width, int height, int splitCount)
    {
        if (_caches != null && _cacheWidth == width && _cacheHeight == height && _allocatedSplit == splitCount) return;

        ReleaseCaches();

        _caches = new GraphicsBuffer[splitCount];
        _cacheArgs = new GraphicsBuffer[splitCount];
        _cacheValid = new bool[splitCount];
        _splatMpbs = new MaterialPropertyBlock[splitCount];
        for (int i = 0; i < splitCount; i++)
        {
            _caches[i] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, width * height, CachePointStride);
            _cacheArgs[i] = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, IndexedArgsCount, sizeof(uint));
            _splatMpbs[i] = new MaterialPropertyBlock();
        }
        _cacheWidth = width;
        _cacheHeight = height;
        _allocatedSplit = splitCount;
        _scrambleMultiplier = ComputeScrambleMultiplier(width * height);
    }

    // 画素数と互いに素で、画素数×黄金比に近い係数を求める（画素数が上限以上なら1＝行順のまま）
    private static int ComputeScrambleMultiplier(int pixelCount)
    {
        if (pixelCount >= MaxScramblePixelCount)
        {
            Debug.LogWarning($"OpcSplitRenderPass: 画素数 {pixelCount:N0} が走査順かき混ぜの上限を超えるため、行順で詰め直します（表示が遅くなります）。");
            return 1;
        }

        int multiplier = Mathf.Max(1, (int)(pixelCount * GoldenRatioFraction));
        while (GreatestCommonDivisor(multiplier, pixelCount) != 1) multiplier++;
        return multiplier;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            int remainder = a % b;
            a = b;
            b = remainder;
        }
        return a;
    }

    private void ReleaseCaches()
    {
        if (_caches != null)
        {
            for (int i = 0; i < _caches.Length; i++)
            {
                _caches[i]?.Dispose();
                _cacheArgs[i]?.Dispose();
            }
        }
        _caches = null;
        _cacheArgs = null;
        _cacheValid = null;
    }

    public void Dispose()
    {
        ReleaseCaches();
    }
}
