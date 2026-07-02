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
}

/// <summary>
/// 点群を分割描画方式（N枚個別保持＋再投影合成）で描くFeature。
/// 各フレームは1スライスだけを自分のバッファへ現在姿勢で新規描画し、表示時に全スライスを合成する。
/// 各スライスは撮影VP→現在VPへ1回だけ再投影するため、誤差が累積しない。
/// </summary>
public class OpcSplitRenderFeature : ScriptableRendererFeature
{
    // 分割描画パス用の点群マテリアル（Custom/PointCloud_CircleSplit）
    [SerializeField]
    private Material _pointSplitMaterial;

    // スライス合成用マテリアル（Custom/PointCloudSplitComposite）
    [SerializeField]
    private Material _compositeMaterial;

    // 何フレームに分けて全点を描くか（=保持するスライスバッファ数）
    [SerializeField]
    [Range(1, 8)]
    private int _splitCount = 4;

    // 再投影の往復UV許容誤差（小さいほどdisocclusionで穴が開きやすく、大きいほど残像が残りやすい）
    [SerializeField]
    [Range(0.002f, 0.1f)]
    private float _warpTolerance = 0.02f;

    private OpcSplitRenderPass _pass;

    public override void Create()
    {
        _pass = new OpcSplitRenderPass(_pointSplitMaterial, _compositeMaterial)
        {
            // URPのシーン描画が一通り終わった後に点群を合成する
            renderPassEvent = RenderPassEvent.AfterRenderingTransparents,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        // 分割描画が無効、またはマテリアル未設定なら何もしない
        if (!OpcSplitRenderBridge.Active || _pointSplitMaterial == null || _compositeMaterial == null) return;

        // ゲームビューのメインカメラだけを対象にする（Sceneビュー等が蓄積を汚さないため）
        ref CameraData cameraData = ref renderingData.cameraData;
        if (cameraData.cameraType != CameraType.Game) return;
        if (cameraData.camera != Camera.main) return;

        // 描画するコマンドが無ければ積まない
        if (OpcSplitRenderBridge.Renderer == null || OpcSplitRenderBridge.CommandCount == 0) return;

        // 分割数・再投影しきい値を反映（Inspectorでの変更を毎フレーム拾う）
        _pass.SplitCount = Mathf.Max(1, _splitCount);
        _pass.WarpTolerance = _warpTolerance;

        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
        _pass = null;
    }
}

/// <summary>
/// N枚のスライスバッファへ「1フレーム1スライス」を新規描画し、全スライスを深度合成で画面へ出すパス。
/// 各スライスは自分の撮影VPを保持し、Stage2bでその姿勢→現在姿勢へ再投影する（compounding無し）。
/// </summary>
public class OpcSplitRenderPass : ScriptableRenderPass
{
    private readonly Material _pointSplitMaterial;
    private readonly Material _compositeMaterial;

    // 何フレームに分けて全点を描くか（=スライス数。Featureから毎フレーム設定される）
    public int SplitCount = 4;

    // 再投影の往復UV許容誤差（これを超える画素はdisocclusionとして捨てる。Featureから設定）
    public float WarpTolerance = 0.02f;

    // スライスごとの色/深度バッファ（永続。フレームを越えて保持し、表示で合成する）
    private RTHandle[] _sliceColor;
    private RTHandle[] _sliceDepth;
    // スライスを描いた時のVP（撮影姿勢。Stage2bの再投影で使う）
    private Matrix4x4[] _sliceVP;
    // そのスライスを一度でも描いたか（未描画スライスは合成しない）
    private bool[] _sliceValid;
    // 合成のフルスクリーン描画に渡すスライス別MPB（1枚を使い回すとRG実行時にエイリアスするため別々に持つ）
    private MaterialPropertyBlock[] _compositeMpb;

    // 合成のZTestに使う自前深度（毎フレームfarでクリアし、スライス間で最前面を競う）
    private RTHandle _compositeDepth;

    private int _rtWidth;
    private int _rtHeight;
    private int _allocatedSplit;

    private static readonly int _commandIdProp = Shader.PropertyToID("_CommandId");
    private static readonly int _sliceOffsetProp = Shader.PropertyToID("_SliceOffset");
    private static readonly int _sliceStrideProp = Shader.PropertyToID("_SliceStride");
    private static readonly int _sliceColorProp = Shader.PropertyToID("_SliceColor");
    private static readonly int _sliceDepthProp = Shader.PropertyToID("_SliceDepth");
    private static readonly int _curVPProp = Shader.PropertyToID("_CurVP");
    private static readonly int _sliceInvVPProp = Shader.PropertyToID("_SliceInvVP");
    private static readonly int _warpToleranceProp = Shader.PropertyToID("_WarpTolerance");

    // 1スライスを新規描画するパスへ渡すデータ
    private class DrawPassData
    {
        public Material material;
        public MultiDrawRenderer renderer;
        public int commandCount;
        public int sliceIndex;   // 今フレームで描くスライス番号
        public int splitCount;   // スライス間隔（stride）
    }

    // 1スライスを画面へ合成するパスへ渡すデータ
    private class CompositePassData
    {
        public Material material;
        public MaterialPropertyBlock mpb;
        public bool clearFirst;  // 最初の有効スライスで画面と合成深度をクリアする
    }

    public OpcSplitRenderPass(Material pointSplitMaterial, Material compositeMaterial)
    {
        _pointSplitMaterial = pointSplitMaterial;
        _compositeMaterial = compositeMaterial;
    }

    public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
    {
        MultiDrawRenderer renderer = OpcSplitRenderBridge.Renderer;
        int commandCount = OpcSplitRenderBridge.CommandCount;
        if (renderer == null || commandCount == 0 || _pointSplitMaterial == null || _compositeMaterial == null) return;

        UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
        UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
        if (!resourceData.activeColorTexture.IsValid()) return;

        // スライスバッファ群を確保（サイズ/分割数が変われば作り直す）
        int splitCount = Mathf.Max(1, SplitCount);
        RenderTextureDescriptor desc = cameraData.cameraTargetDescriptor;
        EnsureTargets(desc.width, desc.height, splitCount);

        // 今フレームで描くスライス
        int sliceIndex = Time.frameCount % splitCount;

        // このフレームのスライスを「現在のカメラ姿勢」で新規描画
        RecordSliceDraw(renderGraph, renderer, commandCount, sliceIndex, splitCount);

        // 現在のVP（GPU射影規約）。撮影姿勢として保存し、再投影では各スライスの逆VPと組で使う。
        Matrix4x4 currentVP = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(), true) * cameraData.GetViewMatrix();
        _sliceVP[sliceIndex] = currentVP;
        _sliceValid[sliceIndex] = true;

        // 全スライスを撮影VP→現在VPへ再投影して画面へ深度合成
        RecordComposite(renderGraph, resourceData, splitCount, currentVP);
    }

    // 1スライスを自分のバッファへ新規描画する（毎回クリアして描き直す＝そのスライスの素データ）
    private void RecordSliceDraw(RenderGraph renderGraph, MultiDrawRenderer renderer, int commandCount, int sliceIndex, int splitCount)
    {
        TextureHandle color = renderGraph.ImportTexture(_sliceColor[sliceIndex]);
        TextureHandle depth = renderGraph.ImportTexture(_sliceDepth[sliceIndex]);

        using (var builder = renderGraph.AddRasterRenderPass<DrawPassData>("OpcSplitDraw", out DrawPassData data))
        {
            data.material = _pointSplitMaterial;
            data.renderer = renderer;
            data.commandCount = commandCount;
            data.sliceIndex = sliceIndex;
            data.splitCount = splitCount;

            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowGlobalStateModification(true);
            builder.AllowPassCulling(false);

            builder.SetRenderFunc((DrawPassData d, RasterGraphContext context) => ExecuteDraw(d, context));
        }
    }

    // 全スライスを撮影VP→現在VPへ再投影し、順に画面へ重ねる（SV_Depth＋ZTestで最前面が勝つ）
    private void RecordComposite(RenderGraph renderGraph, UniversalResourceData resourceData, int splitCount, Matrix4x4 currentVP)
    {
        TextureHandle activeColor = resourceData.activeColorTexture;
        TextureHandle compositeDepth = renderGraph.ImportTexture(_compositeDepth);

        bool firstDone = false;
        for (int j = 0; j < splitCount; j++)
        {
            if (!_sliceValid[j]) continue;

            // スライスの色/深度と再投影行列をMPBへ束縛（スライスごとに別MPB＝実行時エイリアス回避）
            _compositeMpb[j].SetTexture(_sliceColorProp, _sliceColor[j]);
            _compositeMpb[j].SetTexture(_sliceDepthProp, _sliceDepth[j]);
            _compositeMpb[j].SetMatrix(_curVPProp, currentVP);
            _compositeMpb[j].SetMatrix(_sliceInvVPProp, _sliceVP[j].inverse);
            _compositeMpb[j].SetFloat(_warpToleranceProp, WarpTolerance);

            TextureHandle sliceColor = renderGraph.ImportTexture(_sliceColor[j]);
            TextureHandle sliceDepth = renderGraph.ImportTexture(_sliceDepth[j]);

            using (var builder = renderGraph.AddRasterRenderPass<CompositePassData>("OpcSplitComposite", out CompositePassData data))
            {
                data.material = _compositeMaterial;
                data.mpb = _compositeMpb[j];
                data.clearFirst = !firstDone;

                builder.UseTexture(sliceColor, AccessFlags.Read);
                builder.UseTexture(sliceDepth, AccessFlags.Read);
                builder.SetRenderAttachment(activeColor, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(compositeDepth, AccessFlags.Write);
                builder.AllowPassCulling(false);

                builder.SetRenderFunc((CompositePassData d, RasterGraphContext context) => ExecuteComposite(d, context));
            }

            firstDone = true;
        }
    }

    // 今フレームのスライスをクリアして描き直す
    private static void ExecuteDraw(DrawPassData data, RasterGraphContext context)
    {
        RasterCommandBuffer cmd = context.cmd;

        // スライスは毎回まるごと描き直すので全クリア（depth=far=1。反転ZはUnity内部処理）
        cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1f, 0);

        // このスライスが受け持つ点（offset番目からstride間隔）をシェーダへ伝える
        cmd.SetGlobalInt(_sliceOffsetProp, data.sliceIndex);
        cmd.SetGlobalInt(_sliceStrideProp, data.splitCount);

        MaterialPropertyBlock mpb = data.renderer.MaterialProperties;
        GraphicsBuffer quadIndex = data.renderer.QuadIndex;
        for (int i = 0; i < data.commandCount; i++)
        {
            int fullCount = data.renderer.GetInstanceCount(i);
            if (data.sliceIndex >= fullCount) continue;
            int sliceCount = (fullCount - data.sliceIndex + data.splitCount - 1) / data.splitCount;
            if (sliceCount == 0) continue;

            cmd.SetGlobalInt(_commandIdProp, i);
            cmd.DrawProcedural(quadIndex, Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 6, sliceCount, mpb);
        }
    }

    // 1スライスをフルスクリーンで画面へ重ねる
    private static void ExecuteComposite(CompositePassData data, RasterGraphContext context)
    {
        RasterCommandBuffer cmd = context.cmd;

        // 最初の有効スライスで画面と合成深度をクリア
        if (data.clearFirst)
        {
            cmd.ClearRenderTarget(RTClearFlags.All, Color.clear, 1f, 0);
        }

        // フルスクリーン三角形（3頂点）でスライスを合成
        cmd.DrawProcedural(Matrix4x4.identity, data.material, 0, MeshTopology.Triangles, 3, 1, data.mpb);
    }

    // スライスバッファ群を必要に応じて確保し直す
    private void EnsureTargets(int width, int height, int splitCount)
    {
        if (_sliceColor != null && _rtWidth == width && _rtHeight == height && _allocatedSplit == splitCount) return;

        ReleaseTargets();

        _sliceColor = new RTHandle[splitCount];
        _sliceDepth = new RTHandle[splitCount];
        _sliceVP = new Matrix4x4[splitCount];
        _sliceValid = new bool[splitCount];
        _compositeMpb = new MaterialPropertyBlock[splitCount];

        for (int i = 0; i < splitCount; i++)
        {
            _sliceColor[i] = RTHandles.Alloc(
                width, height,
                colorFormat: GraphicsFormat.R8G8B8A8_UNorm,
                filterMode: FilterMode.Point,
                name: $"_OpcSliceColor{i}");
            _sliceDepth[i] = RTHandles.Alloc(
                width, height,
                depthBufferBits: DepthBits.Depth32,
                colorFormat: GraphicsFormat.None,
                name: $"_OpcSliceDepth{i}");
            _compositeMpb[i] = new MaterialPropertyBlock();
            _sliceValid[i] = false;
        }

        // 合成のZTest用深度
        _compositeDepth = RTHandles.Alloc(
            width, height,
            depthBufferBits: DepthBits.Depth32,
            colorFormat: GraphicsFormat.None,
            name: "_OpcCompositeDepth");

        _rtWidth = width;
        _rtHeight = height;
        _allocatedSplit = splitCount;
    }

    private void ReleaseTargets()
    {
        if (_sliceColor != null)
        {
            for (int i = 0; i < _sliceColor.Length; i++)
            {
                _sliceColor[i]?.Release();
                _sliceDepth[i]?.Release();
            }
        }
        _sliceColor = null;
        _sliceDepth = null;
        _compositeDepth?.Release();
        _compositeDepth = null;
    }

    public void Dispose()
    {
        ReleaseTargets();
    }
}
