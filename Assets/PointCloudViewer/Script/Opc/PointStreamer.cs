using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>LOD選択の結果1件（このフレームでどのノードを何点描くか）</summary>
public struct NodeSelection
{
    public int nodeIndex;
    public uint drawCount;
    public float areaPx;
}

/// <summary>
/// ノード点データのストリーミングを管理する。
/// 可視ノードの選択結果（drawCount）を受け取り、ワーカーへのロード要求・完了結果のGPUアップロード・
/// 不要ノードのアンロードを行う。ノードごとの常駐状態は明示的な状態機械（ResidencyState）で持つ。
/// </summary>
public class PointStreamer : IDisposable
{
    public struct Options
    {
        public int maxLoadsPerFrame;
        public int unloadDelayFrames;
        public float fallbackQuality;
    }

    /// <summary>ノードの常駐状態</summary>
    public enum ResidencyState : byte
    {
        // プール上にデータが無い
        Unloaded,

        // データ無しでワーカーの結果待ち（二重要求を防ぐ）
        Loading,

        // プール上にデータがある
        Resident,

        // データを持ったまま追加ロードの結果待ち（旧データで描画を継続する）
        Refreshing,
    }

    /// <summary>ノード1つ分の常駐情報</summary>
    public struct NodeResidency
    {
        public ResidencyState state;

        // 共有点プール内の確保位置（点単位、データ無しは -1）
        public int poolStart;

        // 共有プールにロード済みの点数（部分ロードのため実点数より少ないことがある）
        public int loadedCount;

        // 最後に描画対象として要求されたフレーム
        public int lastWantedFrame;

        // このフレームまでロード再要求を抑止する（プール満杯/ファイル不足時のスラッシング防止）
        public int retryAfterFrame;

        // 今フレームで描画する点数（先頭からこの数だけ描画）
        public uint drawCount;

        // データを持っているか（ロード済みリストの所属と一致させる）
        public bool HasData => poolStart >= 0;
    }

    // ロード要求待ちの候補（画面で目立つ順に処理するためareaPxを持つ）
    private struct PendingLoad
    {
        public int nodeIndex;
        public float areaPx;
    }

    // 描画必要数に対する先読み係数（多めにロードしてカメラ接近のたびの取り直しを減らす）
    private const int PartialLoadMargin = 2;

    // 1ノードの最低ロード点数（drawCountが極小でも取り直しが頻発しない程度を確保する）
    private const int MinLoadCount = 1024;
    private const float BaseFallbackPointsPerPixel = 0.35f;
    private const int BaseFallbackMaxDrawCount = 65_536;
    private const float FallbackLoadPriority = 2.0f;
    private const int FallbackAncestorLevels = 2;

    // プール満杯やファイル不足でロードを見送ったノードの再要求間隔（毎フレームの取り直しを防ぐ）
    private const int RetryDelayFrames = 30;

    private readonly OpcFile _file;
    private readonly GpuPointPool _pool;
    private readonly OpcPointLoadWorker _worker;
    private readonly int _maxLoadsPerFrame;
    private readonly int _unloadDelayFrames;
    private readonly float _fallbackPointsPerPixel;
    private readonly int _fallbackMaxDrawCount;

    private readonly NodeResidency[] _residency;
    private readonly List<int> _loadedNodeIndices = new List<int>();
    private readonly List<PendingLoad> _pendingLoads = new List<PendingLoad>();
    private readonly HashSet<int> _pendingNodeIndices = new HashSet<int>();
    private bool _poolFullWarned;

    // GPUアップロードの所要時間（1フレーム分を合算してベンチマークに記録する）
    private double _frameGpuUploadMs;

    public PointStreamer(
        OpcFile file,
        string fullPath,
        GpuPointPool pool,
        Options options)
    {
        _file = file;
        _pool = pool;
        _maxLoadsPerFrame = options.maxLoadsPerFrame;
        _unloadDelayFrames = options.unloadDelayFrames;
        float fallbackQuality = Mathf.Clamp(options.fallbackQuality, 0.25f, 2.0f);
        _fallbackPointsPerPixel = BaseFallbackPointsPerPixel * fallbackQuality;
        _fallbackMaxDrawCount = Math.Max(MinLoadCount, Mathf.RoundToInt(BaseFallbackMaxDrawCount * fallbackQuality));

        // 常駐情報を初期化（poolStart=-1 がデータ無し）
        _residency = new NodeResidency[file.Nodes.Length];
        for (int i = 0; i < _residency.Length; i++)
        {
            _residency[i].poolStart = -1;
            _residency[i].lastWantedFrame = -1;
        }

        // 点データ読み込み用のワーカースレッドを起動する（極小ファイルでも最低限のバッファは確保する）
        int maxPointCount = Math.Max(1024, file.MaxNodePointCount);
        _worker = new OpcPointLoadWorker(fullPath, file.FileHeader.pointStrideBytes, maxPointCount);

        PreloadRootFallback();
    }

    /// <summary>ノードの常駐情報を取得する（描画側がpoolStart/loadedCount/drawCountを参照する）</summary>
    public NodeResidency GetResidency(int nodeIndex)
    {
        return _residency[nodeIndex];
    }

    public bool TryResolveDrawableNode(int nodeIndex, out int drawableNodeIndex)
    {
        if (_residency[nodeIndex].HasData)
        {
            drawableNodeIndex = nodeIndex;
            return true;
        }

        int parentIndex = _file.ParentIndices[nodeIndex];
        while (parentIndex >= 0)
        {
            if (_residency[parentIndex].HasData)
            {
                drawableNodeIndex = parentIndex;
                return true;
            }

            parentIndex = _file.ParentIndices[parentIndex];
        }

        drawableNodeIndex = -1;
        return false;
    }

    /// <summary>
    /// 1フレーム分のストリーミングを進める。
    /// 選択結果の反映 → 完了結果のアップロード → 新規ロード要求 → 期限切れノードのアンロードの順に行う。
    /// </summary>
    public void Update(List<NodeSelection> selections, int frame)
    {
        // 選択結果を反映し、ロードが必要なノードを集める
        CollectPendingLoads(selections, frame);

        // ワーカーが読み終えたノードをGPUへアップロード
        UploadCompletedLoads(frame);

        // 画面で目立つ順（面積大）にワーカーへロード要求。1フレームあたり _maxLoadsPerFrame 個までで打ち切り。
        _pendingLoads.Sort((a, b) => b.areaPx.CompareTo(a.areaPx));
        int requestLimit = Mathf.Min(_pendingLoads.Count, _maxLoadsPerFrame);
        for (int i = 0; i < requestLimit; i++)
        {
            RequestLoad(_pendingLoads[i].nodeIndex);
        }

        // 一定フレーム要求されなかったノードをアンロード（後ろから走査: 内部でリストから削除されるため）
        for (int i = _loadedNodeIndices.Count - 1; i >= 0; i--)
        {
            int nodeIndex = _loadedNodeIndices[i];
            if (frame - _residency[nodeIndex].lastWantedFrame > _unloadDelayFrames)
            {
                UnloadNode(nodeIndex);
            }
        }
    }

    // 選択結果をdrawCount/lastWantedFrameに反映し、ロードが必要なノードを_pendingLoadsへ集める
    private void CollectPendingLoads(List<NodeSelection> selections, int frame)
    {
        _pendingLoads.Clear();
        _pendingNodeIndices.Clear();

        for (int i = 0; i < selections.Count; i++)
        {
            NodeSelection selection = selections[i];
            ref NodeResidency res = ref _residency[selection.nodeIndex];

            // 見えているノードの要求情報を更新
            res.drawCount = selection.drawCount;
            res.lastWantedFrame = frame;

            KeepFallbackAncestorsWarm(selection, frame);

            // 結果待ち中は次の要求を出さない
            if (res.state == ResidencyState.Loading || res.state == ResidencyState.Refreshing) continue;

            // 常駐済みで描画必要数も足りていればロード不要
            if (res.state == ResidencyState.Resident && !NeedsMorePoints(res, _file.Nodes[selection.nodeIndex].pointCount)) continue;

            // 直前にプール満杯などで見送ったノードはクールダウン中は再要求しない
            if (frame < res.retryAfterFrame) continue;

            AddPendingLoad(selection.nodeIndex, selection.areaPx);
        }
    }

    private void KeepFallbackAncestorsWarm(NodeSelection selection, int frame)
    {
        int parentIndex = _file.ParentIndices[selection.nodeIndex];
        int warmedLevels = 0;
        while (parentIndex >= 0 && warmedLevels < FallbackAncestorLevels)
        {
            WarmFallbackNode(parentIndex, selection.areaPx, frame);
            parentIndex = _file.ParentIndices[parentIndex];
            warmedLevels++;
        }

        if (_file.RootIndex >= 0 && _file.RootIndex != selection.nodeIndex)
        {
            WarmFallbackNode(_file.RootIndex, selection.areaPx, frame);
        }
    }

    private void WarmFallbackNode(int nodeIndex, float areaPx, int frame)
    {
        OpcFile.Node node = _file.Nodes[nodeIndex];
        if (node.pointCount == 0) return;

        ref NodeResidency res = ref _residency[nodeIndex];
        uint fallbackDrawCount = GetFallbackDrawCount(areaPx, node.pointCount);
        res.drawCount = Math.Max(res.drawCount, fallbackDrawCount);
        res.lastWantedFrame = frame;

        if (res.state == ResidencyState.Loading || res.state == ResidencyState.Refreshing) return;
        if (res.state == ResidencyState.Resident && !NeedsMorePoints(res, node.pointCount)) return;
        if (frame < res.retryAfterFrame) return;

        AddPendingLoad(nodeIndex, areaPx * FallbackLoadPriority);
    }

    private void AddPendingLoad(int nodeIndex, float areaPx)
    {
        if (!_pendingNodeIndices.Add(nodeIndex)) return;
        _pendingLoads.Add(new PendingLoad { nodeIndex = nodeIndex, areaPx = areaPx });
    }

    // 描画必要数に対してロード済み点数が不足しているか（全点ロード済みなら常にfalse）
    private void PreloadRootFallback()
    {
        int rootIndex = _file.RootIndex;
        if (rootIndex < 0) return;

        OpcFile.Node root = _file.Nodes[rootIndex];
        if (root.pointCount == 0) return;

        ref NodeResidency rootRes = ref _residency[rootIndex];
        rootRes.drawCount = GetFallbackDrawCount(root.pointCount);
        rootRes.lastWantedFrame = 0;
        RequestLoad(rootIndex);
    }

    private uint GetFallbackDrawCount(uint nodePointCount)
    {
        return Math.Min((uint)_fallbackMaxDrawCount, nodePointCount);
    }

    private uint GetFallbackDrawCount(float areaPx, uint nodePointCount)
    {
        float targetCount = areaPx * _fallbackPointsPerPixel;
        uint clamped = (uint)Mathf.Clamp(targetCount, MinLoadCount, _fallbackMaxDrawCount);
        return Math.Min(clamped, nodePointCount);
    }

    private static bool NeedsMorePoints(in NodeResidency res, uint nodePointCount)
    {
        if (res.loadedCount >= (int)nodePointCount) return false;
        return res.drawCount > (uint)res.loadedCount;
    }

    // ロードする点数を決める（描画必要数の先読み係数倍を読み、実点数で頭打ち）
    private static int ComputeLoadCount(uint drawCount, uint nodePointCount)
    {
        long desired = Math.Max(MinLoadCount, (long)drawCount * PartialLoadMargin);
        return (int)Math.Min(desired, nodePointCount);
    }

    // 1ノードのロード要求をワーカーへ積む（状態をLoading/Refreshingへ進める）
    private void RequestLoad(int nodeIndex)
    {
        OpcFile.Node node = _file.Nodes[nodeIndex];
        ref NodeResidency res = ref _residency[nodeIndex];

        // データを持ったままの追加ロードはRefreshing（旧データで描画継続）、新規はLoading
        res.state = res.HasData ? ResidencyState.Refreshing : ResidencyState.Loading;

        // ファイル先頭からのシーク位置を計算してワーカーへ渡す（先頭から必要分だけ読む）
        long seekPos = _file.BodyStart + (long)(node.baseOffset * (ulong)_file.FileHeader.pointStrideBytes);
        _worker.Enqueue(new OpcPointLoadWorker.LoadRequest
        {
            index = nodeIndex,
            seekPos = seekPos,
            count = ComputeLoadCount(res.drawCount, node.pointCount),
        });
    }

    // ワーカーが読み終えたノードをGPUプールへアップロードする（1フレームあたり _maxLoadsPerFrame 個まで）
    private void UploadCompletedLoads(int frame)
    {
        _frameGpuUploadMs = 0;

        int uploaded = 0;
        while (uploaded < _maxLoadsPerFrame && _worker.TryDequeue(out OpcPointLoadWorker.LoadResult result))
        {
            ref NodeResidency res = ref _residency[result.index];

            // 結果待ち状態を解く（データの有無で戻り先が変わる）
            ResidencyState fallbackState = res.HasData ? ResidencyState.Resident : ResidencyState.Unloaded;

            // 読み込みに失敗したノードは見送る（まだ見えていれば次のフレームで再要求される）
            if (result.failed)
            {
                res.state = fallbackState;
                continue;
            }

            // 読み込み中に不要になったノードは破棄（GPUへアップロードしない）
            if (frame - res.lastWantedFrame > _unloadDelayFrames)
            {
                _worker.ReturnPacked(result.packed);
                res.state = fallbackState;
                continue;
            }

            // プールへアップロード。空きが無ければ画面に不要なノードを先に解放して再試行する
            bool uploadSucceeded = _pool.TryUpload(result.packed, result.count, out int newStart);
            if (!uploadSucceeded)
            {
                EvictStaleNodes(frame);
                uploadSucceeded = _pool.TryUpload(result.packed, result.count, out newStart);
            }
            _worker.ReturnPacked(result.packed);

            // それでも満杯ならこのノードは見送る（クールダウンを置いて毎フレームの取り直しを防ぐ）
            if (!uploadSucceeded)
            {
                WarnPoolFullOnce(result.count);
                res.retryAfterFrame = frame + RetryDelayFrames;
                res.state = fallbackState;
                continue;
            }
            _frameGpuUploadMs += _pool.LastUploadMs;

            // 要求数より少なくしか読めなかった場合（ファイル末尾/破損）は再要求の連発を防ぐ
            if (result.count < result.requestedCount)
            {
                res.retryAfterFrame = frame + RetryDelayFrames;
            }

            // 追加ロードの差し替え時は旧領域を解放する（新確保が成功してから解放するので失敗時も安全）
            bool hadData = res.HasData;
            if (hadData)
            {
                _pool.Free(res.poolStart, res.loadedCount);
            }

            // 確保情報を更新して常駐化（新規ロードのときだけロード済みリストへ登録）
            res.poolStart = newStart;
            res.loadedCount = result.count;
            res.state = ResidencyState.Resident;
            if (!hadData)
            {
                _loadedNodeIndices.Add(result.index);
            }
            uploaded++;
        }

        // アップロードがあったフレームだけ所要時間を記録（0埋めで平均を薄めない）
        if (uploaded > 0)
        {
            Benchmarker.Record("OpcLoader.Load.GpuUpload", _frameGpuUploadMs);
        }
    }

    // プール満杯の警告を1回だけ出す（空き状況も診断用に出す）
    private void WarnPoolFullOnce(int requestedCount)
    {
        if (_poolFullWarned) return;

        Debug.LogWarning(
            $"PointStreamer: 点プールが満杯のためノードのロードを見送ります。" +
            $"要求={requestedCount} 空き合計={_pool.TotalFreeCount} 最大連続空き={_pool.LargestFreeBlockCount} 容量={_pool.Capacity:N0}。" +
            $"頻発する場合は点プール容量（PointBudgetまたは余裕係数）を増やしてください。");
        _poolFullWarned = true;
    }

    // 今フレーム見えていないロード済みノードを解放してプールに空きを作る（メモリ圧迫時の早期アンロード）
    private void EvictStaleNodes(int frame)
    {
        // 後ろから走査（内部でロード済みリストから削除されるため）
        for (int i = _loadedNodeIndices.Count - 1; i >= 0; i--)
        {
            int nodeIndex = _loadedNodeIndices[i];
            if (_residency[nodeIndex].lastWantedFrame < frame)
            {
                UnloadNode(nodeIndex);
            }
        }
    }

    // ノードのプール領域を解放してロード済みリストから外す（結果待ち中ならその状態は維持する）
    private void UnloadNode(int nodeIndex)
    {
        ref NodeResidency res = ref _residency[nodeIndex];

        // データを持っていなければ何もしない
        if (!res.HasData) return;

        _pool.Free(res.poolStart, res.loadedCount);
        res.poolStart = -1;
        res.loadedCount = 0;

        // 追加ロードの結果待ち中ならデータ無しの結果待ち（Loading）へ、それ以外は未ロードへ
        res.state = res.state == ResidencyState.Refreshing ? ResidencyState.Loading : ResidencyState.Unloaded;

        // ロード済みリストから削除
        _loadedNodeIndices.Remove(nodeIndex);
    }

    public void Dispose()
    {
        // ワーカーを止める（停止後に新しい結果が出ないようにする。未回収の結果はワーカー側で返却される）
        _worker?.Dispose();
    }
}
