using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// ラベル付きサンプルを集約し、End 時に統計を出力するセッション。
/// 開始・終了のトリガーは外部（管理クラス）から Begin/End で制御する。
/// </summary>
public class BenchmarkSession : MonoBehaviour, IBenchmarkSink
{
    [Header("Run")]
    [SerializeField] private int _captureFps = 60;       // 0 にすると実時間で進む
    [SerializeField] private int _warmupFrames = 30;     // 計測開始までに捨てるフレーム

    [Header("Profiler Markers")]
    [SerializeField] private string[] _markerNames =
    {
        // フレームに1回発火するマーカーのみ対象（複数回発火は最後の1回しか取れないため）
        "OpcLoader.GetVisibleNodes",
        "OpcLoader.SyncNodeStreaming",
        "OpcLoader.DrawPointCloud",
    };

    [SerializeField] private string[] _gpuMarkerNames =
    {
        // GPU時間を取る描画マーカー（RenderGraphのパス名。Directの点群はDrawOpaqueObjectsの中で描かれる）
        "DrawOpaqueObjects",
        "OpcSplitFill",
        "OpcSplitCompact",
        "OpcSplitSplat",
    };

    // 描画マーカー1つあたりに保持するGPU時間のサンプル数（計測フレーム数より多くする）
    private const int GpuSampleCapacity = 8192;

    public event Action OnFinished;

    private readonly Dictionary<string, List<double>> _samples = new();
    private readonly List<ProfilerRecorder> _markerRecorders = new();
    // FrameTimingManager の読み出し先（毎フレームの割り当てを避けるため使い回す）
    private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
    // GPU時間を取る描画マーカーの記録器（_gpuMarkerNames と同じ並び）
    private readonly List<ProfilerRecorder> _gpuRecorders = new();
    // 描画マーカーごとの、記録済みのサンプル数（GPU時間は数フレームおきにまとめて届くため）
    private readonly List<int> _recordedGpuSampleCounts = new();
    private int _frameCount;
    private bool _running;

    public bool IsRunning => _running;

    private void Awake()
    {
        Benchmarker.SetSink(this);
    }

    private void OnDestroy()
    {
        Benchmarker.ClearSink(this);
        if (_running) End(); // 途中で破棄されても結果は出す
        DisposeMarkerRecorders(); // 未破棄のレコーダーが残らないよう保険
    }

    public void Begin()
    {
        if (_running) return;
        _samples.Clear();
        _frameCount = 0;
        if (_captureFps > 0) Time.captureFramerate = _captureFps;

        // 計測中はウィンドウが前面に無くても止まらないようにする（Run In Backgroundがオフだと非アクティブ時に止まる）
        Application.runInBackground = true;

        // マーカーの計測を開始
        StartMarkerRecorders();

        _running = true;
    }

    public void End()
    {
        if (!_running) return;
        _running = false;
        Time.captureFramerate = 0;

        // マーカーの計測を終了
        DisposeMarkerRecorders();

        PrintStats();
        OnFinished?.Invoke();
    }

    public void Record(string label, double valueMs)
    {
        if (!_running) return;
        if (_frameCount < _warmupFrames) return;

        if (!_samples.TryGetValue(label, out var list))
        {
            list = new List<double>(256);
            _samples[label] = list;
        }
        list.Add(valueMs);
    }

    private void Update()
    {
        if (!_running) return;

        // Frame ラベルは常に積む
        Record("Frame", Time.unscaledDeltaTime * 1000.0);

        // フレーム全体のGPU時間とメインスレッド時間（取れない環境では記録しない）
        RecordFrameTimings();

        // 各マーカーの直近サンプルを記録（ナノ秒→ミリ秒）
        for (int i = 0; i < _markerRecorders.Count; i++)
        {
            ProfilerRecorder recorder = _markerRecorders[i];
            if (recorder.Valid && recorder.Count > 0)
            {
                Record(_markerNames[i], recorder.LastValue * 1e-6);
            }
        }

        // 描画マーカーのGPU時間を記録
        for (int i = 0; i < _gpuRecorders.Count; i++)
        {
            // 前回から新しく届いたサンプルだけを記録する（ナノ秒→ミリ秒）
            ProfilerRecorder recorder = _gpuRecorders[i];
            if (!recorder.Valid) continue;
            for (int sampleIndex = _recordedGpuSampleCounts[i]; sampleIndex < recorder.Count; sampleIndex++)
            {
                Record($"{_gpuMarkerNames[i]}.Gpu", recorder.GetSample(sampleIndex).Value * 1e-6);
            }
            _recordedGpuSampleCounts[i] = recorder.Count;
        }

        _frameCount++;
    }

    // FrameTimingManager から直近フレームのGPU時間とメインスレッド時間を記録する（数フレーム前の値）
    private void RecordFrameTimings()
    {
        FrameTimingManager.CaptureFrameTimings();
        if (FrameTimingManager.GetLatestTimings(1, _frameTimings) == 0) return;

        FrameTiming timing = _frameTimings[0];
        if (timing.gpuFrameTime > 0) Record("GpuFrame", timing.gpuFrameTime);
        if (timing.cpuMainThreadFrameTime > 0) Record("CpuMainThread", timing.cpuMainThreadFrameTime);
    }

    // マーカー名ごとに ProfilerRecorder を起動する
    private void StartMarkerRecorders()
    {
        DisposeMarkerRecorders();
        foreach (string markerName in _markerNames)
        {
            _markerRecorders.Add(ProfilerRecorder.StartNew(ProfilerCategory.Scripts, markerName));
        }

        // 描画マーカーはGPU時間を取るモードで起動する（計測の全サンプルが収まる容量にする）
        foreach (string markerName in _gpuMarkerNames)
        {
            _gpuRecorders.Add(ProfilerRecorder.StartNew(ProfilerCategory.Render, markerName, GpuSampleCapacity,
                ProfilerRecorderOptions.Default | ProfilerRecorderOptions.GpuRecorder));
            _recordedGpuSampleCounts.Add(0);
        }
    }

    // ProfilerRecorder を全て破棄する（ネイティブリソースのため必須）
    private void DisposeMarkerRecorders()
    {
        foreach (ProfilerRecorder recorder in _markerRecorders)
        {
            recorder.Dispose();
        }
        _markerRecorders.Clear();

        foreach (ProfilerRecorder recorder in _gpuRecorders)
        {
            recorder.Dispose();
        }
        _gpuRecorders.Clear();
        _recordedGpuSampleCounts.Clear();
    }

    private void PrintStats()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== BenchmarkSession (capture={_captureFps}fps) ===");
        sb.AppendLine($"{"Label",-32}{"n",6}{"avg",10}{"p50",10}{"p95",10}{"max",10}");

        foreach (var kv in _samples)
        {
            var data = kv.Value;
            if (data.Count == 0) continue;

            data.Sort();
            double sum = 0;
            for (int i = 0; i < data.Count; i++) sum += data[i];

            double avg = sum / data.Count;
            double p50 = data[data.Count / 2];
            double p95 = data[Mathf.Min(data.Count - 1, (int)(data.Count * 0.95))];
            double max = data[data.Count - 1];

            sb.AppendLine($"{kv.Key,-32}{data.Count,6}{avg,10:F2}{p50,10:F2}{p95,10:F2}{max,10:F2}");
        }

        UnityEngine.Debug.Log(sb.ToString());
    }
}
