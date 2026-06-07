using System.Collections.Generic;

/// <summary>
/// 共有GPUバッファ内の点領域を管理するフリーリスト方式のアロケータ。
/// 空き領域を開始位置順に保持し、解放時は隣接する空き領域と結合して断片化を抑える。
/// </summary>
public class PointPoolAllocator
{
    // 空き領域（開始位置と点数）
    private struct FreeBlock
    {
        public int start;
        public int count;
    }

    // 開始位置の昇順で並んだ空き領域リスト
    private readonly List<FreeBlock> _freeBlocks = new List<FreeBlock>();

    public PointPoolAllocator(int capacity)
    {
        // 初期状態は全域が1つの空き領域
        _freeBlocks.Add(new FreeBlock { start = 0, count = capacity });
    }

    /// <summary>空き領域の合計点数（診断用）</summary>
    public int TotalFreeCount
    {
        get
        {
            int total = 0;
            for (int i = 0; i < _freeBlocks.Count; i++) total += _freeBlocks[i].count;
            return total;
        }
    }

    /// <summary>連続で確保できる最大点数（断片化の診断用）</summary>
    public int LargestFreeBlockCount
    {
        get
        {
            int largest = 0;
            for (int i = 0; i < _freeBlocks.Count; i++)
            {
                if (_freeBlocks[i].count > largest) largest = _freeBlocks[i].count;
            }
            return largest;
        }
    }

    /// <summary>count分の連続領域を確保する。確保できなければfalseを返しstartは-1。</summary>
    public bool TryAllocate(int count, out int start)
    {
        // first-fit: 先頭から最初に収まる空き領域を使う
        for (int i = 0; i < _freeBlocks.Count; i++)
        {
            FreeBlock block = _freeBlocks[i];
            if (block.count < count) continue;

            start = block.start;

            // ぴったりなら空き領域ごと削除、余るなら先頭を詰めて残す
            if (block.count == count)
            {
                _freeBlocks.RemoveAt(i);
            }
            else
            {
                _freeBlocks[i] = new FreeBlock { start = block.start + count, count = block.count - count };
            }
            return true;
        }

        start = -1;
        return false;
    }

    /// <summary>確保済み領域を解放する。前後の空き領域と連続していれば結合する。</summary>
    public void Free(int start, int count)
    {
        // 開始位置順を保てる挿入位置を探す
        int index = LowerBound(start);

        // 直前の空き領域と連続していれば結合
        bool mergedIntoPrev = false;
        if (index > 0)
        {
            FreeBlock prev = _freeBlocks[index - 1];
            if (prev.start + prev.count == start)
            {
                prev.count += count;
                _freeBlocks[index - 1] = prev;
                mergedIntoPrev = true;
            }
        }

        // 直後の空き領域と連続していれば結合
        if (index < _freeBlocks.Count)
        {
            FreeBlock next = _freeBlocks[index];
            if (start + count == next.start)
            {
                if (mergedIntoPrev)
                {
                    // 前後両方と連続: 前の領域に後ろも吸収させる
                    FreeBlock prev = _freeBlocks[index - 1];
                    prev.count += next.count;
                    _freeBlocks[index - 1] = prev;
                    _freeBlocks.RemoveAt(index);
                }
                else
                {
                    _freeBlocks[index] = new FreeBlock { start = start, count = count + next.count };
                }
                return;
            }
        }

        // どちらとも結合しなければ新しい空き領域として挿入
        if (!mergedIntoPrev)
        {
            _freeBlocks.Insert(index, new FreeBlock { start = start, count = count });
        }
    }

    // start以上の開始位置を持つ最初の空き領域のインデックスを二分探索で返す
    private int LowerBound(int start)
    {
        int lo = 0;
        int hi = _freeBlocks.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_freeBlocks[mid].start < start) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
