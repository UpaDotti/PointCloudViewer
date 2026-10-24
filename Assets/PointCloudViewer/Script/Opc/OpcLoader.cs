using System;
using System.IO;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// OPC点群ビューアの合成ルート（MonoBehaviour）。
/// ファイル読み込み・LOD選択・ストリーミング・描画の各部品を組み立て、毎フレーム順に呼び出す。
/// </summary>
public class OpcLoader : MonoBehaviour
{
    private enum RenderMode
    {
        Direct,
        Split,
        // 全点を1画素の点（1点=1頂点）で直接描画する比較用モード
        DirectPixelPoints,
    }
    [Header("File")]
    [SerializeField]
    private string _filePath = "Assets/sample.opc";

    [Header("Rendering")]
    [SerializeField]
    private Material _pointMaterial;

    // DirectPixelPointsモード用のマテリアル（Custom/PointCloud_Point）
    [SerializeField]
    private Material _pixelPointMaterial;

    [SerializeField]
    private RenderMode _renderMode = RenderMode.Split;

    [Header("Quality")]
    [SerializeField]
    [Min(1)]
    private int _pointBudget = 2_000_000;

    [SerializeField]
    [Range(0.25f, 2.0f)]
    private float _fallbackQuality = 1.0f;

    [Header("Point Size")]
    [SerializeField]
    [Range(0.1f, 2.0f)]
    private float _pointSizeScale = 1.0f;

    // 共有点プール容量の自動算出に使う余裕係数（部分ロードの必要量 ≈ 予算×先読み係数 に視界外残留分の余裕を加える）
    private const int PoolCapacityBudgetMultiplier = 4;
    private const float MinPixelSize = 0.1f;
    private const float MaxPixelSize = 8.0f;
    private const float MaxPointsPerPixel = 1.0f;
    private const int MaxLoadsPerFrame = 10;
    private const int UnloadDelayFrames = 30;

    // 開いたOPCファイル（ヘッダ+統合ノード索引）。読み込み失敗時はnullのまま。
    private OpcFile _file;
    private Camera _mainCamera;
    private readonly Plane[] _planes = new Plane[6];

    // 今フレームのカメラ情報スナップショット（LOD選択と点サイズ計算に使う）
    private CameraState _cameraState;

    // LOD選択（可視判定+Point Budget配分）と、その選択結果
    private NodeLodSelector _lodSelector;
    private List<NodeSelection> _selections = new List<NodeSelection>();

    // 全ノード共有のGPU点プール / ロードとアンロードを管理するストリーマ / マルチドロー描画
    private GpuPointPool _pointPool;
    private PointStreamer _streamer;
    private MultiDrawRenderer _renderer;

    // 点用マテリアル未設定の警告を出したか
    private bool _warnedMissingPixelPointMaterial;

    // Profiler用マーカー（Unity ProfilerのCPU Usageに"OpcLoader.～"として表示される）
    private static readonly ProfilerMarker _markerGetVisibleNodes = new ProfilerMarker("OpcLoader.GetVisibleNodes");
    private static readonly ProfilerMarker _markerSyncStreaming = new ProfilerMarker("OpcLoader.SyncNodeStreaming");
    private static readonly ProfilerMarker _markerDrawPointCloud = new ProfilerMarker("OpcLoader.DrawPointCloud");


    private void OnValidate()
    {
        _pointBudget = Math.Max(1, _pointBudget);
        _fallbackQuality = Mathf.Clamp(_fallbackQuality, 0.25f, 2.0f);
        _pointSizeScale = Mathf.Clamp(_pointSizeScale, 0.1f, 2.0f);
    }


    private void Start()
    {
        _mainCamera = Camera.main;

        // Unity CLIからの自動計測用に、引数で描画モードとファイルパスを上書きする
        ApplyCommandLineOverrides();

        // OPCファイルのヘッダとノード索引を読み込む（失敗したらエラーを出してこのコンポーネントを止める）
        if (!OpcFile.TryOpen(GetFullPath(_filePath), out _file, out string error))
        {
            Debug.LogError($"OpcLoader: OPCファイルを開けませんでした。{error}");
            enabled = false;
            return;
        }

        // 各部品を組み立てる（依存は LodSelector / Streamer / Renderer → File / Pool の一方向）
        _lodSelector = new NodeLodSelector(_file, MaxPointsPerPixel);
        _pointPool = new GpuPointPool(ComputePointPoolCapacity());
        _streamer = new PointStreamer(
            _file,
            GetFullPath(_filePath),
            _pointPool,
            new PointStreamer.Options
            {
                maxLoadsPerFrame = MaxLoadsPerFrame,
                unloadDelayFrames = UnloadDelayFrames,
                fallbackQuality = _fallbackQuality,
            }
        );
        _renderer = new MultiDrawRenderer(_pointMaterial, _pixelPointMaterial, _file, _pointPool);
    }

    private void Update()
    {
        using (_markerGetVisibleNodes.Auto())
        {
            _cameraState = CameraState.Capture(_mainCamera, _planes);
            _lodSelector.Select(_cameraState, _pointBudget, _selections);
        }

        using (_markerSyncStreaming.Auto())
        {
            _streamer.Update(_selections, Time.frameCount);
        }

        using (_markerDrawPointCloud.Auto())
        {
            int commandCount = _renderer.Prepare(_streamer, _selections, _cameraState, MinPixelSize, MaxPixelSize, _pointSizeScale);
            RecordBenchmarkFrame(commandCount);

            if (commandCount == 0)
            {
                DisableSplitRenderBridge();
                return;
            }

            if (_renderMode == RenderMode.Direct)
            {
                DisableSplitRenderBridge();
                _renderer.DrawDirect(commandCount);
                Benchmarker.Record("OpcLoader.Direct.DrawCalls", 1);
                return;
            }

            if (_renderMode == RenderMode.DirectPixelPoints)
            {
                DisableSplitRenderBridge();
                // 点用マテリアルが未設定なら描けないので知らせる（毎フレーム出さないよう1回だけ）
                if (!_renderer.DrawDirectPixelPoints(commandCount) && !_warnedMissingPixelPointMaterial)
                {
                    Debug.LogWarning("OpcLoader: DirectPixelPointsモードですが、_pixelPointMaterialが未設定のため描画しません。");
                    _warnedMissingPixelPointMaterial = true;
                }
                return;
            }

            OpcSplitRenderBridge.Active = true;
            OpcSplitRenderBridge.Renderer = _renderer;
            OpcSplitRenderBridge.CommandCount = commandCount;
            OpcSplitRenderBridge.PointCount = _renderer.LastPreparedPointCount;
        }
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

    // "-renderMode Direct|Split|Compute" と "-opcPath 絶対パス" があればInspectorの設定を上書きする
    private void ApplyCommandLineOverrides()
    {
        if (BenchmarkCommandLine.TryGetValue("-renderMode", out string renderModeText))
        {
            if (Enum.TryParse(renderModeText, true, out RenderMode renderMode))
            {
                _renderMode = renderMode;
                Debug.Log($"OpcLoader: 引数により描画モードを {_renderMode} にしました。");
            }
            else
            {
                Debug.LogWarning($"OpcLoader: 不明な描画モード '{renderModeText}' のため、Inspectorの設定 {_renderMode} を使います。");
            }
        }

        if (BenchmarkCommandLine.TryGetValue("-opcPath", out string opcPath))
        {
            _filePath = opcPath;
            Debug.Log($"OpcLoader: 引数によりOPCファイルを {_filePath} にしました。");
        }
    }

    // "Assets/～" 形式のパスを絶対パスに変換する（すでに絶対パスならそのまま使う）
    private string GetFullPath(string filePath)
    {
        if (Path.IsPathRooted(filePath)) return filePath;
        string shortFilePath = filePath.Replace("Assets/", "");
        return Path.Combine(Application.dataPath, shortFilePath);
    }

    private void RecordBenchmarkFrame(int commandCount)
    {
        Benchmarker.Record("OpcLoader.Mode.Direct", _renderMode == RenderMode.Direct ? 1 : 0);
        Benchmarker.Record("OpcLoader.Mode.Split", _renderMode == RenderMode.Split ? 1 : 0);
        Benchmarker.Record("OpcLoader.Mode.DirectPixelPoints", _renderMode == RenderMode.DirectPixelPoints ? 1 : 0);
        Benchmarker.Record("OpcLoader.Draw.Commands", commandCount);
        Benchmarker.Record("OpcLoader.Draw.Points", _renderer.LastPreparedPointCount);
        Benchmarker.Record("OpcLoader.Selection.Count", _selections.Count);
    }

    private static void DisableSplitRenderBridge()
    {
        OpcSplitRenderBridge.Active = false;
        OpcSplitRenderBridge.Renderer = null;
        OpcSplitRenderBridge.CommandCount = 0;
        OpcSplitRenderBridge.PointCount = 0;
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
        // 破棄するレンダラーをRendererFeature側が参照しないようにする
        DisableSplitRenderBridge();

        // ストリーマを先に止める（停止後に新しい結果が出ないようにする）
        _streamer?.Dispose();
        _streamer = null;

        _renderer?.Dispose();
        _renderer = null;

        _pointPool?.Dispose();
        _pointPool = null;
    }
}
