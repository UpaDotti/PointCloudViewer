using System;
using System.Diagnostics;

/// <summary>
/// どこからでも計測値を投げ込める静的ファサード。
/// 実体は BenchmarkSession が保持する。Session が無い間は何もしない。
/// </summary>
public static class Benchmarker
{
    private static IBenchmarkSink _sink;

    public static void SetSink(IBenchmarkSink sink) => _sink = sink;

    public static void ClearSink(IBenchmarkSink sink)
    {
        // 別の Session を二重登録した場合の事故防止
        if (_sink == sink) _sink = null;
    }

    public static void Record(string label, double valueMs) => _sink?.Record(label, valueMs);

    /// <summary>
    /// using (Benchmarker.Sample("xxx")) { ... } で囲んだ区間の経過時間を ms で記録する。
    /// </summary>
    public static Scope Sample(string label) => new Scope(label);

    public readonly struct Scope : IDisposable
    {
        private readonly string _label;
        private readonly long _startTicks;

        public Scope(string label)
        {
            _label = label;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            // Sink が無くても Stopwatch コストはかかるが、呼び出し側で using を消せば済む
            double ms = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
            _sink?.Record(_label, ms);
        }
    }
}

public interface IBenchmarkSink
{
    void Record(string label, double valueMs);
}
