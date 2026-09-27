// P5 FileAck 追踪器（文件传输骨架）。
//
// 职责：
//   - 接收对端发来的 FileAck 位图帧，记录哪些 chunk 已被确认。
//   - 提供 MissingChunks()：发送方据此决定哪些 chunk 需要重传。
//   - 提供 Progress()：已 ack 的 chunk 数 / 总 chunk 数（供 UI 进度条）。
//   - 线程安全：SendLoop（写）与 ReceiveLoop（读 ack）是不同 goroutine/线程。
//
// 设计约束：
//   - 不保存原始位图：转成内部已排序的区间列表，便于快速查询"第 N 个缺失 chunk"。
//   - 区间合并且保证非重叠、非相邻（相邻时合并），因此 MissingChunks 的枚举
//     复杂度是 O(区间数) 而非 O(总 chunk 数)。
//   - 支持多段 ack（超大文件拆成多个 FileAck 帧）：后到的 ack 与已有区间做并集。
//
// P6 时扩展：与磁盘上的 .part 文件做对比，实现"断点续传"（即把已写入的 chunk
// 预先填入 tracker，等同于"本地已 ack"）。

using System.Collections.Immutable;

namespace DeskLink.Service.FileTransfer;

/// <summary>追踪哪些文件 chunk 已被对端确认。</summary>
public sealed class FileAckTracker
{
    private readonly object _gate = new();
    private readonly List<ChunkRange> _acked = new();
    private ulong _totalChunks;
    private ulong _ackedCount;

    /// <summary>总 chunk 数（由发送方在启动时设定）。</summary>
    public ulong TotalChunks => _totalChunks;

    /// <summary>已被确认的 chunk 数。</summary>
    public ulong AckedCount
    {
        get { lock (_gate) { return _ackedCount; } }
    }

    /// <summary>初始化总 chunk 数。必须在任何 RecordAck 之前调用。</summary>
    public void Init(ulong totalChunks)
    {
        lock (_gate)
        {
            _totalChunks = totalChunks;
            _acked.Clear();
            _ackedCount = 0;
        }
    }

    /// <summary>
    /// 记录一段 ack 位图。
    /// baseChunk：此位图覆盖的首个 chunk 序号。
    /// bits：bool[]，bits[i]==true 表示 chunk (baseChunk+i) 已 ack。
    /// </summary>
    public void RecordAck(ulong baseChunk, ReadOnlySpan<bool> bits)
    {
        if (bits.Length == 0) return;
        lock (_gate)
        {
            for (var i = 0; i < bits.Length; i++)
            {
                if (!bits[i]) continue;
                var chunk = baseChunk + (ulong)i;
                if (chunk >= _totalChunks) continue; // 越界 ack 忽略
                InsertChunk(chunk);
            }
            _ackedCount = CountAll();
        }
    }

    /// <summary>返回前 maxCount 个尚未确认的 chunk 序号（按升序）。</summary>
    public ImmutableArray<ulong> MissingChunks(int maxCount)
    {
        lock (_gate)
        {
            var result = ImmutableArray.CreateBuilder<ulong>(Math.Min(maxCount, (int)Math.Min(int.MaxValue, _totalChunks)));
            var next = 0uL;
            foreach (var range in _acked)
            {
                while (next < range.Start && result.Count < maxCount)
                {
                    result.Add(next++);
                }
                next = Math.Max(next, range.End + 1);
                if (result.Count >= maxCount) break;
            }
            while (next < _totalChunks && result.Count < maxCount)
            {
                result.Add(next++);
            }
            return result.ToImmutable();
        }
    }

    /// <summary>
    /// 某个 chunk 是否已确认（O(log n)，基于区间二分）。
    ///
    /// 为什么不用 <see cref="MissingChunks"/> 推导：那只对"最小缺失序号之前"的
    /// chunk 成立；一旦中间有洞，后面的已 ack 块会被误判为未 ack（导致无谓重传）。
    /// </summary>
    public bool IsAcked(ulong chunk)
    {
        lock (_gate)
        {
            if (chunk >= _totalChunks) return false;
            var idx = _acked.BinarySearch(new ChunkRange(chunk, chunk));
            if (idx >= 0) return true;
            // BinarySearch 未命中时返回按位取反的插入点：前一个区间可能覆盖它。
            idx = ~idx - 1;
            return idx >= 0 && _acked[idx].End >= chunk;
        }
    }

    /// <summary>
    /// 直接构造 MSB 在前的位图（1 = 已 ack），覆盖 [baseChunk, baseChunk + bitCount)。
    ///
    /// 供 FileAck 帧使用：比"先 bool[] 再 PackBitmap"少一次大分配，
    /// 且对超大文件不会构造 chunkCount 长度的临时数组。
    /// </summary>
    public byte[] BuildBitmap(ulong baseChunk, int bitCount)
    {
        if (bitCount <= 0) return Array.Empty<byte>();
        var bytes = new byte[(bitCount + 7) / 8];
        lock (_gate)
        {
            foreach (var range in _acked)
            {
                var start = Math.Max(range.Start, baseChunk);
                var end = Math.Min(range.End, baseChunk + (ulong)bitCount - 1);
                for (var c = start; c <= end; c++)
                {
                    var off = (int)(c - baseChunk);
                    bytes[off / 8] |= (byte)(1 << (7 - (off % 8)));
                }
            }
        }
        return bytes;
    }

    /// <summary>是否全部 chunk 都已 ack。</summary>
    public bool IsComplete()
    {
        lock (_gate)
        {
            if (_totalChunks == 0) return true;
            return _ackedCount == _totalChunks;
        }
    }

    private void InsertChunk(ulong chunk)
    {
        // 找到插入位置并合并重叠/相邻区间
        var idx = _acked.BinarySearch(new ChunkRange(chunk, chunk));
        if (idx >= 0) return; // 已存在

        idx = ~idx;
        var newStart = chunk;
        var newEnd = chunk;

        // 向左合并（相邻或重叠）
        if (idx > 0 && _acked[idx - 1].End + 1 >= newStart)
        {
            newStart = _acked[idx - 1].Start;
            idx--;
            _acked.RemoveAt(idx);
        }
        // 向右合并（相邻或重叠）
        while (idx < _acked.Count && _acked[idx].Start <= newEnd + 1)
        {
            newEnd = Math.Max(newEnd, _acked[idx].End);
            _acked.RemoveAt(idx);
        }
        _acked.Insert(idx, new ChunkRange(newStart, newEnd));
    }

    private ulong CountAll()
    {
        ulong sum = 0;
        foreach (var r in _acked) sum += r.End - r.Start + 1;
        return sum;
    }

    /// <summary>闭区间 [Start, End]。</summary>
    private readonly record struct ChunkRange(ulong Start, ulong End) : IComparable<ChunkRange>
    {
        public int CompareTo(ChunkRange other) => Start.CompareTo(other.Start);
    }
}
