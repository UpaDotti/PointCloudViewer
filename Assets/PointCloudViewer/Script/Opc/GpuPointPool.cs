using System;
using Unity.Profiling;
using UnityEngine;

/// <summary>
/// 全ノード共有のGPU点プール。GraphicsBufferと空き領域アロケータをまとめ、
/// 「確保してアップロード」「解放」だけを提供する（どのノードを載せるかの判断は呼び出し側が行う）。
/// </summary>
public class GpuPointPool : IDisposable
{
    // PackedPoint12のバイト数（uint×3）
    private const int PointStrideBytes = 12;

    private GraphicsBuffer _buffer;
    private PointPoolAllocator _allocator;

    // Profiler用マーカー（Unity ProfilerのCPU Usageに"OpcLoader.～"として表示される）
    private static readonly ProfilerMarker _markerGpuUpload = new ProfilerMarker("OpcLoader.Load.GpuUpload");

    /// <summary>シェーダへ渡す点プール本体</summary>
    public GraphicsBuffer Buffer => _buffer;

    /// <summary>プール容量（点数）</summary>
    public int Capacity { get; }

    /// <summary>空き領域の合計点数（診断用）</summary>
    public int TotalFreeCount => _allocator.TotalFreeCount;

    /// <summary>連続で確保できる最大点数（断片化の診断用）</summary>
    public int LargestFreeBlockCount => _allocator.LargestFreeBlockCount;

    /// <summary>直近のTryUploadでGPU転送にかかった時間（ms）。ベンチマーク集計用。</summary>
    public double LastUploadMs { get; private set; }

    public GpuPointPool(int capacity)
    {
        Capacity = capacity;
        _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, PointStrideBytes);
        _allocator = new PointPoolAllocator(capacity);
    }

    /// <summary>連続領域を確保して点データをアップロードする。空きが無ければfalseを返す。</summary>
    public bool TryUpload(PackedPoint12[] packed, int count, out int start)
    {
        LastUploadMs = 0;

        // 連続した空き領域を確保（無ければ何もしない）
        if (!_allocator.TryAllocate(count, out start))
        {
            return false;
        }

        // 確保した位置へ点データをアップロード（プール配列は余剰領域があるので count 個だけ転送）
        long tickUploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        using (_markerGpuUpload.Auto())
        {
            _buffer.SetData(packed, 0, start, count);
        }
        LastUploadMs = TicksToMs(System.Diagnostics.Stopwatch.GetTimestamp() - tickUploadStart);
        return true;
    }

    /// <summary>確保済み領域を解放する</summary>
    public void Free(int start, int count)
    {
        _allocator.Free(start, count);
    }

    // Stopwatchのティック数をミリ秒に変換する
    private static double TicksToMs(long ticks)
    {
        return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
        _allocator = null;
    }
}
