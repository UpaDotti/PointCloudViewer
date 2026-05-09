using System;
using System.Collections.Generic;
using System.Text;
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

    public event Action OnFinished;

    private readonly Dictionary<string, List<double>> _samples = new();
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
    }

    public void Begin()
    {
        if (_running) return;
        _samples.Clear();
        _frameCount = 0;
        if (_captureFps > 0) Time.captureFramerate = _captureFps;
        _running = true;
    }

    public void End()
    {
        if (!_running) return;
        _running = false;
        Time.captureFramerate = 0;
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
        _frameCount++;
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
