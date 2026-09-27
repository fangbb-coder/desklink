// 文件分块布局（P6）。
//
// 分块是断点续传的最小单位，也是 ack 位图的位序号单位。
//
// 约束（很重要，写错会静默截断）：
//   内层帧负载上限是 u16（65535 字节，见 ProtocolConstants.MaxFramePayloadWireSize）。
//   FileChunk 负载 = 头（u32 transferId + u64 chunkIndex = 12B）+ 数据，
//   因此**数据块最大 65523 字节**。默认取 48KB，给外层/加密留出余量，
//   也避免单个 QUIC 流写被切成过多小包。

namespace DeskLink.Service.FileTransfer;

/// <summary>一条传输的分块布局。</summary>
public readonly record struct ChunkLayout(long TotalSize, int ChunkSize)
{
    /// <summary>FileChunk 负载的固定头长度：[u32 transferId][u64 chunkIndex]。</summary>
    public const int ChunkHeaderSize = 4 + 8;

    /// <summary>默认块大小（48 KB）。</summary>
    public const int DefaultChunkSize = 48 * 1024;

    /// <summary>单个块的数据上限（受内层帧 u16 负载上限约束）。</summary>
    public const int MaxChunkDataSize =
        Protocol.Frames.FrameCodec.MaxFramePayloadWireSize - ChunkHeaderSize;

    /// <summary>块数量（空文件为 0）。</summary>
    public long ChunkCount => TotalSize <= 0 ? 0 : (TotalSize + ChunkSize - 1) / ChunkSize;

    /// <summary>第 index 块在文件中的字节偏移。</summary>
    public long OffsetOf(long index) => index * ChunkSize;

    /// <summary>第 index 块的字节长度（最后一块可能更短；越界返回 0）。</summary>
    public int LengthOf(long index)
    {
        if (index < 0) return 0;
        var offset = OffsetOf(index);
        var remaining = TotalSize - offset;
        if (remaining <= 0) return 0;
        return (int)Math.Min(ChunkSize, remaining);
    }

    /// <summary>校验并归一化块大小；非法抛 ArgumentOutOfRangeException。</summary>
    public static int ValidateChunkSize(int chunkSize)
    {
        if (chunkSize <= 0 || chunkSize > MaxChunkDataSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chunkSize),
                $"块大小必须在 1..{MaxChunkDataSize} 之间（受内层帧 u16 负载上限约束），实际 {chunkSize}");
        }
        return chunkSize;
    }

    /// <summary>构造（自动校验块大小）。</summary>
    public static ChunkLayout Create(long totalSize, int? chunkSize = null)
    {
        if (totalSize < 0) throw new ArgumentOutOfRangeException(nameof(totalSize), "文件大小不能为负");
        return new ChunkLayout(totalSize, ValidateChunkSize(chunkSize ?? DefaultChunkSize));
    }
}
