// P5 FileAck 位图帧编解码（文件传输骨架）。
//
// 帧格式（与 Go 侧 proto/fileack.go 对齐）：
//   [u64 baseChunk  BE]  // 此位图覆盖的首个 chunk 序号
//   [u32 bitCount   BE]  // 位图有多少个有效位
//   [bitmap bytes...]    // bitCount 个位，每字节 8 位，高位在前（MSB）
//
// 语义：
//   - bit i == 1  表示 chunk (baseChunk + i) 已被对端成功接收。
//   - bit i == 0  表示未确认（可能丢失、可能尚未发送）。
//   - 发送方收到 ack 后从发送窗口中移除对应 chunk，重传窗口只保留 0 位。
//   - 单个 ack 帧最多覆盖 65535*8 = 524280 个 chunk（约 512K chunks），
//     对 64KB 分块来说覆盖 32GB 文件；超大文件需要多个 ack 帧（baseChunk 递进）。
//
// 设计约束：
//   - 位图本身不压缩：文件传输场景下 ack 是稀疏还是密集取决于丢包率；
//     低丢包时大部分位是 1，run-length 压缩收益有限，反而增加复杂度。
//   - baseChunk 是 u64：支持任意大文件（chunk 序号永不会溢出）。
//   - bitCount 是 u32：单个 ack 帧最大 ~512MB 位图（极端情况），
//     实际受外层帧 u16 长度限制（65535 - 12 = 65523 字节位图 ≈ 524184 位）。

using System.Buffers.Binary;

namespace DeskLink.Protocol.Frames;

/// <summary>文件 chunk 确认位图帧。</summary>
public readonly record struct FileAckFrame(ulong BaseChunk, uint BitCount, byte[] Bitmap)
{
    /// <summary>帧头固定 12 字节：u64 + u32。</summary>
    public const int HeaderSize = 8 + 4;

    /// <summary>序列化为字节（不含内层帧头——由 FrameCodec 负责包 [type][u16 len]）。</summary>
    public byte[] Encode()
    {
        var bitmapLen = Bitmap.Length;
        var buf = new byte[HeaderSize + bitmapLen];
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(0, 8), BaseChunk);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8, 4), BitCount);
        Bitmap.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>
    /// 从 payload 解码。返回解码后的帧；失败时抛 <see cref="InvalidDataException"/>。
    /// 调用方应先用 FrameCodec 剥掉内层帧头，再把 payload 传到这里。
    /// </summary>
    public static FileAckFrame Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"FileAck payload too short: {payload.Length} < {HeaderSize}");
        }

        var baseChunk = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8));
        var bitCount = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4));
        var bitmap = payload.Slice(HeaderSize).ToArray();

        // 校验：bitCount 与 bitmap 字节数必须自洽（允许最后一个字节有未用位）。
        // bitCount > int.MaxValue 时 (bitCount+7) 会 uint 回绕成小值，先上限拒绝
        // （u16 负载约束下合法 bitmap 最多 65535*8 位，int.MaxValue 上限绰绰有余）。
        if (bitCount > (uint)int.MaxValue)
        {
            throw new InvalidDataException($"FileAck bitCount out of range: {bitCount}");
        }
        var expectedBytes = (int)((bitCount + 7) / 8);
        if (bitmap.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"FileAck bitmap length mismatch: bitCount={bitCount} expects {expectedBytes} bytes, got {bitmap.Length}");
        }

        // bitCount == 0 是合法空 ack（表示"到此为止全部收到"的边界信号）。
        return new FileAckFrame(baseChunk, bitCount, bitmap);
    }

    /// <summary>查询 chunk idx 是否被 ack（idx 必须是 BaseChunk 之后的偏移）。</summary>
    public bool IsAcked(ulong chunkIdx)
    {
        if (chunkIdx < BaseChunk) return false;
        var off = chunkIdx - BaseChunk;
        if (off >= BitCount) return false;
        var byteIdx = (int)(off / 8);
        var bitIdx = 7 - (int)(off % 8); // MSB 在前
        return (Bitmap[byteIdx] & (1 << bitIdx)) != 0;
    }

    /// <summary>把 bool[] 打包成位图字节（MSB 在前）。</summary>
    public static byte[] PackBitmap(ReadOnlySpan<bool> bits)
    {
        if (bits.Length == 0) return Array.Empty<byte>();
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (7 - (i % 8)));
            }
        }
        return bytes;
    }

    /// <summary>把位图字节解包成 bool[]（MSB 在前）。</summary>
    public static bool[] UnpackBitmap(ReadOnlySpan<byte> bitmap, int bitCount)
    {
        var bits = new bool[bitCount];
        for (var i = 0; i < bitCount; i++)
        {
            var byteIdx = i / 8;
            var bitIdx = 7 - (i % 8);
            bits[i] = (bitmap[byteIdx] & (1 << bitIdx)) != 0;
        }
        return bits;
    }
}
