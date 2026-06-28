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
    [Header("File")]
    [SerializeField]
    private string _filePath = "Assets/sample.opc";

    [Header("Rendering")]
    [SerializeField]
    private Material _pointMaterial;

    // 分割描画（RendererFeature経由でRTへ描く）を使うか。OFFなら従来どおりカメラへ直接描画する。
    [SerializeField]
    private bool _useSplitRender = false;

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

        // 各部品を組み立てる（依存は LodSelector / Streamer / Renderer → File / Pool の一方向）
        _lodSelector = new NodeLodSelector(_file, _maxPointsPerPixel);
        _pointPool = new GpuPointPool(ComputePointPoolCapacity());
        _streamer = new PointStreamer(_file, GetFullPath(_filePath), _pointPool, _maxLoadsPerFrame, _unloadDelayFrames);
        _renderer = new MultiDrawRenderer(_pointMaterial, _file, _pointPool);
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
            if (_useSplitRender)
            {
                // 分割描画: コマンドを詰めてBridge経由でRendererFeatureへ渡す（実描画はPassが行う）
                int commandCount = _renderer.Prepare(_streamer, _cameraState, _minPixelSize, _maxPixelSize, _pointSizeScale);
                OpcSplitRenderBridge.Active = true;
                OpcSplitRenderBridge.Renderer = _renderer;
                OpcSplitRenderBridge.CommandCount = commandCount;
            }
            else
            {
                // 直接描画: 従来どおりカメラへマルチドロー
                OpcSplitRenderBridge.Active = false;
                _renderer.Draw(_streamer, _cameraState, _minPixelSize, _maxPixelSize, _pointSizeScale);
            }
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

    // "Assets/～" 形式のパスを絶対パスに変換する
    private string GetFullPath(string filePath)
    {
        string shortFilePath = filePath.Replace("Assets/", "");
        return Path.Combine(Application.dataPath, shortFilePath);
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
        // 分割描画を止め、破棄するレンダラーをRendererFeature側が参照しないようにする
        OpcSplitRenderBridge.Active = false;
        OpcSplitRenderBridge.Renderer = null;
        OpcSplitRenderBridge.CommandCount = 0;

        // ストリーマを先に止める（停止後に新しい結果が出ないようにする）
        _streamer?.Dispose();
        _streamer = null;

        _renderer?.Dispose();
        _renderer = null;

        _pointPool?.Dispose();
        _pointPool = null;
    }
}
