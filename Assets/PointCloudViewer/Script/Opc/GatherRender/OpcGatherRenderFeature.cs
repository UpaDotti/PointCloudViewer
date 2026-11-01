using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Experimental.Rendering;

/// <summary>
/// OpcLoader（MonoBehaviour）が用意した描画データを、集め直し方式のPassへ渡す受け渡し口。
/// URPがFeatureを生成するため両者は直接参照できない。毎フレームOpcLoaderが更新し、Passが読む。
/// </summary>
public static class OpcGatherRenderBridge
{
    // 集め直し方式が有効か（OpcLoaderが毎フレーム更新する）
    public static bool Active;

    // 今フレームのコマンドを詰め終えたレンダラー（PrepareでMPBとコマンド数が確定済み）
    public static MultiDrawRenderer Renderer;

    // 今フレームのコマンド数（ロード済みノード数）
    public static int CommandCount;

    // 先頭から何個のコマンドが「円が大きいノード」か（OpcLoaderがQuadで描くので、点描きパスでは飛ばす）
    public static int LargeCommandCount;
}

/// <summary>
/// 点群を「1画素点＋画面空間の集め直し」で描くFeature。
/// 全点を1画素の点として作業テクスチャへ描き（1画素1点）、全画面パスで周りの点の円を集めて画面へ描く。
/// Quad描画の頂点処理（1点4頂点）を、1点1頂点＋画素数に比例する処理へ置き換えるための試作。
/// </summary>
public class OpcGatherRenderFeature : ScriptableRendererFeature
{
    // 点描きパス用マテリアル（Custom/PointCloud_GatherFill）
    [SerializeField]
    private Material _fillMaterial;

    // 集め直しパス用マテリアル（Custom/PointCloud_GatherResolve）
    [SerializeField]
    private Material _resolveMaterial;

    // 集め直しをcompute（共有メモリ版）で行うか。falseなら全画面のフラグメント版を使う
    [SerializeField]
    private bool _useComputeResolve = true;

    // compute版の集め直し（PointCloudGatherResolve.compute）
    [SerializeField]
    private ComputeShader _resolveCompute;

    // compute版の結果をカメラへ書き写す合成パス用マテリアル（Custom/PointCloud_GatherComposite）
    [SerializeField]
    private Material _compositeMaterial;

    private OpcGatherRenderPass _pass;

    public override void Create()
    {
        _pass = new OpcGatherRenderPass(_fillMaterial, _resolveMaterial, _resolveCompute, _compositeMaterial)
        {
            // 分割描画と同じく、不透明と空を描いた後・半透明の前に点群を描く
            // （半透明が点群の深度で前後判定され、点群の手前にある半透明が点群に上書きされないようにする）
            renderPassEvent = RenderPassEvent.BeforeRenderingTransparents,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 集め直し方式が無効、またはマテリアル未設定なら何もしない
        if (!OpcGatherRenderBridge.Active || _fillMaterial == null || _resolveMaterial == null) return;

        // ゲームビューのメインカメラだけを対象にする
        ref CameraData cameraData = ref renderingData.cameraData;
        if (cameraData.cameraType != CameraType.Game) return;
        if (cameraData.camera != Camera.main) return;

        // 描画するコマンドが無ければ積まない
        if (OpcGatherRenderBridge.Renderer == null || OpcGatherRenderBridge.CommandCount == 0) return;

        // compute版を使うか（Inspectorでの変更を毎フレーム拾う。compute側が未設定ならフラグメント版）
        _pass.UseComputeResolve = _useComputeResolve && _resolveCompute != null && _compositeMaterial != null;

        renderer.EnqueuePass(_pass);
    }
}

/// <summary>
/// 点描き（1画素1点）→ 集め直し（全画面で円を再構成）の2パスを積むパス。
/// </summary>
public class OpcGatherRenderPass : ScriptableRenderPass
{
    private readonly Material _fillMaterial;
    private readonly Material _resolveMaterial;
    private readonly ComputeShader _resolveCompute;
    private readonly Material _compositeMaterial;
    private readonly int _resolveKernel;

    // 集め直しをcompute版で行うか（Featureから毎フレーム設定される）
    public bool UseComputeResolve;

    // compute版の1スレッドグループの一辺（compute側のTILE_SIZEと一致させる）
    private const int ResolveTileSize = 8;

    // パスごとのGPU時間を取るための計測区間（RenderGraph既定の区間はGPU時間を集めないため自前で渡す）
    private static readonly ProfilingSampler _fillSampler = new ProfilingSampler("OpcGatherFill");
    private static readonly ProfilingSampler _resolveSampler = new ProfilingSampler("OpcGatherResolve");
    private static readonly ProfilingSampler _compositeSampler = new ProfilingSampler("OpcGatherComposite");

    private static readonly int _commandIdProp = Shader.PropertyToID("_CommandId");
    private static readonly int _gatherPackedProp = Shader.PropertyToID("_GatherPacked");
    private static readonly int _gatherDepthProp = Shader.PropertyToID("_GatherDepth");
    private static readonly int _gatherResultProp = Shader.PropertyToID("_GatherResult");
    private static readonly int _gatherSizeProp = Shader.PropertyToID("_GatherSize");
    private static readonly int _reversedZProp = Shader.PropertyToID("_ReversedZ");

    // 点描きパスへ渡すデータ
    private class FillPassData
    {
        public Material material;
        public MultiDrawRenderer renderer;
        public int commandCount;
        public int firstCommand; // 先頭の「円が大きいノード」はQuadで描かれるので、この番号から描く
    }

    // 集め直しパスへ渡すデータ
    private class ResolvePassData
    {
        public Material material;
        public TextureHandle packed;
        public TextureHandle depth;
    }

    // compute版の集め直しパスへ渡すデータ
    private class ComputeResolvePassData
    {
        public ComputeShader shader;
        public int kernel;
        public TextureHandle packed;
        public TextureHandle depth;
        public TextureHandle result;
        public int width;
        public int height;
    }

    // 合成パスへ渡すデータ
    private class CompositePassData
    {
        public Material material;
        public TextureHandle result;
    }

    public OpcGatherRenderPass(Material fillMaterial, Material resolveMaterial, ComputeShader resolveCompute, Material compositeMaterial)
    {
        _fillMaterial = fillMaterial;
        _resolveMaterial = resolveMaterial;
        _resolveCompute = resolveCompute;
        _compositeMaterial = compositeMaterial;
        if (_resolveCompute != null)
        {
            _resolveKernel = _resolveCompute.FindKernel("Resolve");
        }
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        MultiDrawRenderer renderer = OpcGatherRenderBridge.Renderer;
        int commandCount = OpcGatherRenderBridge.CommandCount;
        if (renderer == null || commandCount == 0) return;

        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        if (!resourceData.activeColorTexture.IsValid() || !resourceData.activeDepthTexture.IsValid()) return;

        // 作業テクスチャ（このフレームで使い切るのでRenderGraphの一時テクスチャにする）
        RenderTextureDescriptor cameraDesc = cameraData.cameraTargetDescriptor;
        int width = cameraDesc.width;
        int height = cameraDesc.height;
        TextureHandle packed = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, new RenderTextureDescriptor(width, height, GraphicsFormat.R32_UInt, GraphicsFormat.None),
            "_OpcGatherPacked", false);
        TextureHandle depthColor = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, new RenderTextureDescriptor(width, height, GraphicsFormat.R32_SFloat, GraphicsFormat.None),
            "_OpcGatherDepth", false);
        TextureHandle depthBuffer = UniversalRenderer.CreateRenderGraphTexture(
            renderGraph, new RenderTextureDescriptor(width, height, GraphicsFormat.None, GraphicsFormat.D32_SFloat),
            "_OpcGatherDepthBuffer", false);

        RecordFill(renderGraph, renderer, commandCount, packed, depthColor, depthBuffer);

        // フラグメント版はカメラへ直接書く
        if (!UseComputeResolve)
        {
            RecordResolve(renderGraph, resourceData, packed, depthColor);
            return;
        }

        // compute版は作業テクスチャへ書き、合成パスでカメラへ書き写す
        RenderTextureDescriptor resultDesc = new RenderTextureDescriptor(width, height, GraphicsFormat.R32G32_UInt, GraphicsFormat.None)
        {
            enableRandomWrite = true,
        };
        TextureHandle result = UniversalRenderer.CreateRenderGraphTexture(renderGraph, resultDesc, "_OpcGatherResult", false);
        RecordComputeResolve(renderGraph, packed, depthColor, result, width, height);
        RecordComposite(renderGraph, resourceData, result);
    }

    // 区画ごとに共有メモリへ読み込んで、円を集め直した結果を作業テクスチャへ書く
    private void RecordComputeResolve(RenderGraph renderGraph, TextureHandle packed, TextureHandle depthColor, TextureHandle result, int width, int height)
    {
        using (var builder = renderGraph.AddComputePass<ComputeResolvePassData>("OpcGatherResolve", out ComputeResolvePassData data, _resolveSampler))
        {
            data.shader = _resolveCompute;
            data.kernel = _resolveKernel;
            data.packed = packed;
            data.depth = depthColor;
            data.result = result;
            data.width = width;
            data.height = height;

            builder.UseTexture(packed, AccessFlags.Read);
            builder.UseTexture(depthColor, AccessFlags.Read);
            builder.UseTexture(result, AccessFlags.Write);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((ComputeResolvePassData d, ComputeGraphContext context) => ExecuteComputeResolve(d, context));
        }
    }

    // 集め直しの結果をカメラの色と深度へ書き写す
    private void RecordComposite(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle result)
    {
        using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("OpcGatherComposite", out CompositePassData data, _compositeSampler))
        {
            data.material = _compositeMaterial;
            data.result = result;

            builder.UseTexture(result, AccessFlags.Read);
            builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((CompositePassData d, RasterGraphContext context) => ExecuteComposite(d, context));
        }
    }

    // 全点を1画素の点として作業テクスチャへ描く（深度テストで1画素1点＝最も手前に絞る）
    private void RecordFill(RenderGraph renderGraph, MultiDrawRenderer renderer, int commandCount,
        TextureHandle packed, TextureHandle depthColor, TextureHandle depthBuffer)
    {
        using (var builder = renderGraph.AddRasterRenderPass<FillPassData>("OpcGatherFill", out FillPassData data, _fillSampler))
        {
            data.material = _fillMaterial;
            data.renderer = renderer;
            data.commandCount = commandCount;
            data.firstCommand = Mathf.Clamp(OpcGatherRenderBridge.LargeCommandCount, 0, commandCount);

            builder.SetRenderAttachment(packed, 0, AccessFlags.Write);
            builder.SetRenderAttachment(depthColor, 1, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depthBuffer, AccessFlags.Write);
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((FillPassData d, RasterGraphContext context) => ExecuteFill(d, context));
        }
    }

    // 作業テクスチャから円を集め直して、カメラの色と深度へ書く
    private void RecordResolve(RenderGraph renderGraph, UniversalResourceData resourceData, TextureHandle packed, TextureHandle depthColor)
    {
        using (var builder = renderGraph.AddRasterRenderPass<ResolvePassData>("OpcGatherResolve", out ResolvePassData data, _resolveSampler))
        {
            data.material = _resolveMaterial;
            data.packed = packed;
            data.depth = depthColor;

            builder.UseTexture(packed, AccessFlags.Read);
            builder.UseTexture(depthColor, AccessFlags.Read);
            builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.ReadWrite);
            builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.ReadWrite);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((ResolvePassData d, RasterGraphContext context) => ExecuteResolve(d, context));
        }
    }

    // 作業テクスチャをクリアし、ノードごとに全点を1画素の点で描く（CommandBufferにはマルチドローが無いため）
    private static void ExecuteFill(FillPassData data, RasterGraphContext context)
    {
        RasterCommandBuffer cmd = context.cmd;

        // 0=点なし / 深度=最遠でクリア（反転ZはUnity内部処理）
        cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1f, 0);

        MaterialPropertyBlock mpb = data.renderer.MaterialProperties;
        for (int i = data.firstCommand; i < data.commandCount; i++)
        {
            int pointCount = data.renderer.GetInstanceCount(i);
            if (pointCount == 0) continue;

            cmd.SetGlobalInt(_commandIdProp, i);
            cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Points, pointCount, 1, mpb);
        }
    }

    // 画面全体を覆う三角形1枚で集め直しシェーダを走らせる
    private static void ExecuteResolve(ResolvePassData data, RasterGraphContext context)
    {
        data.material.SetTexture(_gatherPackedProp, data.packed);
        data.material.SetTexture(_gatherDepthProp, data.depth);
        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1);
    }

    // 8x8画素の区画ごとに1スレッドグループでcomputeを走らせる
    private static void ExecuteComputeResolve(ComputeResolvePassData data, ComputeGraphContext context)
    {
        ComputeCommandBuffer cmd = context.cmd;
        cmd.SetComputeTextureParam(data.shader, data.kernel, _gatherPackedProp, data.packed);
        cmd.SetComputeTextureParam(data.shader, data.kernel, _gatherDepthProp, data.depth);
        cmd.SetComputeTextureParam(data.shader, data.kernel, _gatherResultProp, data.result);
        cmd.SetComputeIntParams(data.shader, _gatherSizeProp, data.width, data.height);
        cmd.SetComputeIntParam(data.shader, _reversedZProp, SystemInfo.usesReversedZBuffer ? 1 : 0);

        int groupsX = (data.width + ResolveTileSize - 1) / ResolveTileSize;
        int groupsY = (data.height + ResolveTileSize - 1) / ResolveTileSize;
        cmd.DispatchCompute(data.shader, data.kernel, groupsX, groupsY, 1);
    }

    // 画面全体を覆う三角形1枚で合成シェーダを走らせる
    private static void ExecuteComposite(CompositePassData data, RasterGraphContext context)
    {
        data.material.SetTexture(_gatherResultProp, data.result);
        context.cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1);
    }
}
