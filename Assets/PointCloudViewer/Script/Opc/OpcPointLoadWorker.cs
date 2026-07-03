using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Unity.Profiling;

/// <summary>
/// 点群ノードのファイル読み込みとパックをワーカースレッドで行うローダー。
/// メインスレッドは要求の投入(Enqueue)と完成品の取り出し(TryDequeue)のみ行い、
/// GPUバッファの作成は呼び出し側（メインスレッド）が担当する。
/// </summary>
public class OpcPointLoadWorker : IDisposable
{
    // ロード要求（メイン→ワーカー）
    public struct LoadRequest
    {
        public int index;
        public long seekPos;
        public int count;
    }

    // ロード結果（ワーカー→メイン）。packed は使用後に ReturnPacked で必ず返却する
    public struct LoadResult
    {
        public int index;
        public PackedPoint12[] packed;
        public int count;

        // 要求された点数（count < requestedCount ならファイルが要求分に足りなかった）
        public int requestedCount;

        // 読み込みに失敗したら true（packed は null。呼び出し側はロード中フラグだけ解除する）
        public bool failed;
    }

    private readonly BlockingCollection<LoadRequest> _requests = new BlockingCollection<LoadRequest>(new ConcurrentQueue<LoadRequest>());
    private readonly ConcurrentQueue<LoadResult> _results = new ConcurrentQueue<LoadResult>();
    private readonly FileStream _stream;
    private readonly int _stride;
    private readonly Thread _thread;

    // ロード専用のArrayPool（Sharedは1MB超の配列をプールせず毎回newになりGCスパイクを招くため）
    // raw はワーカー内で完結するので1本、packed は結果キュー内で複数本が同時に生存するため多めに保持する
    private readonly ArrayPool<byte> _rawBytePool;
    private readonly ArrayPool<PackedPoint12> _packedPool;

    // Profiler用マーカー（ワーカースレッドのタイムラインに"OpcLoader.～"として表示される）
    private static readonly ProfilerMarker _markerFileRead = new ProfilerMarker("OpcLoader.Load.FileRead");
    private static readonly ProfilerMarker _markerPackPoints = new ProfilerMarker("OpcLoader.Load.PackPoints");

    public OpcPointLoadWorker(string fullPath, int stride, int maxPointCount)
    {
        if (!OpcFile.IsSupportedPointStride((ushort)stride))
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "Supported OPC point stride is 9 bytes (RGB).");
        }

        _stride = stride;
        _stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        _rawBytePool = ArrayPool<byte>.Create(maxPointCount * stride, 1);
        _packedPool = ArrayPool<PackedPoint12>.Create(maxPointCount, 16);

        // ワーカースレッドを起動（IsBackground: アプリ終了時に残っていても道連れで終了させる）
        _thread = new Thread(WorkerLoop) { IsBackground = true, Name = "OpcPointLoadWorker" };
        _thread.Start();
    }

    /// <summary>ロード要求を積む（メインスレッドから呼ぶ）</summary>
    public void Enqueue(LoadRequest request)
    {
        _requests.Add(request);
    }

    /// <summary>完成したロード結果を1件取り出す（メインスレッドから呼ぶ）</summary>
    public bool TryDequeue(out LoadResult result)
    {
        return _results.TryDequeue(out result);
    }

    /// <summary>結果のpacked配列をプールへ返却する（GPUアップロード後に必ず呼ぶ。nullは無視）</summary>
    public void ReturnPacked(PackedPoint12[] packed)
    {
        if (packed != null)
        {
            _packedPool.Return(packed);
        }
    }

    // 要求キューが閉じられるまでロードを繰り返すワーカー本体
    private void WorkerLoop()
    {
        foreach (LoadRequest request in _requests.GetConsumingEnumerable())
        {
            LoadResult result;
            try
            {
                result = LoadOne(request);
            }
            catch (Exception e)
            {
                // 失敗しても必ず失敗結果を返す（ノードをロード中のまま固まらせない）。スレッド自体は止めない。
                UnityEngine.Debug.LogException(e);
                result = new LoadResult { index = request.index, failed = true };
            }
            _results.Enqueue(result);
        }
    }

    // 1ノード分の読み込みとパックを行う
    private LoadResult LoadOne(LoadRequest request)
    {
        int totalBytes = request.count * _stride;

        // Poolを借りる（生バイト + パック済み配列）
        byte[] raw = _rawBytePool.Rent(totalBytes);
        PackedPoint12[] packed = _packedPool.Rent(request.count);

        // 1ノード分をまとめて読み込み
        int readBytes = 0;
        using (_markerFileRead.Auto())
        {
            _stream.Seek(request.seekPos, SeekOrigin.Begin);
            while (readBytes < totalBytes)
            {
                int n = _stream.Read(raw, readBytes, totalBytes - readBytes);
                if (n <= 0) break;
                readBytes += n;
            }
        }

        // 実際に読めたバイト数から点数を確定する（ファイル末尾/破損時にゴミ点を作らない）
        int actualCount = Math.Min(request.count, readBytes / _stride);

        // 点データを構築（バイト列からパック）
        using (_markerPackPoints.Auto())
        {
            for (int i = 0; i < actualCount; i++)
            {
                int p = i * _stride;
                ushort x16 = (ushort)(raw[p] | (raw[p + 1] << 8));
                ushort y16 = (ushort)(raw[p + 2] | (raw[p + 3] << 8));
                ushort z16 = (ushort)(raw[p + 4] | (raw[p + 5] << 8));

                byte r8 = raw[p + 6];
                byte g8 = raw[p + 7];
                byte b8 = raw[p + 8];
                byte a8 = byte.MaxValue;

                packed[i].xy = (uint)(x16 | (y16 << 16));
                packed[i].z = (uint)z16;
                packed[i].rgba = (uint)(r8 | (g8 << 8) | (b8 << 16) | (a8 << 24));
            }
        }

        // 生バイトはワーカー内で使い終わるのでここで返却
        _rawBytePool.Return(raw);

        return new LoadResult
        {
            index = request.index,
            packed = packed,
            count = actualCount,
            requestedCount = request.count,
        };
    }

    public void Dispose()
    {
        // 新規要求を締め切り、残りを処理し終えたスレッドを待って後始末する
        _requests.CompleteAdding();
        _thread.Join();
        _requests.Dispose();
        _stream.Dispose();

        // 未回収の結果配列をプールへ返す（失敗結果はpackedがnullなのでスキップ）
        while (_results.TryDequeue(out LoadResult result))
        {
            ReturnPacked(result.packed);
        }
    }
}

/// <summary>GPUへ渡す12バイトのパック済み点データ（xy: 16bit×2, z: 16bit, rgba: 8bit×4）</summary>
public struct PackedPoint12
{
    public uint xy;
    public uint z;
    public uint rgba;
}
