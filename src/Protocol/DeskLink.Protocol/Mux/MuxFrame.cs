using System.Buffers.Binary;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Frames;

namespace DeskLink.Protocol.Mux;

// TCP 路径上的 mux 外层头：
//   [u32 len][u8 sid][内层帧 bytes...]
// 其中 len 包含 sid 字节 + 内层帧总长（便于一次 read 解开完整包）。
//
// 两种用法（len 的语义相同，都是 "1 + 后面的字节数"）：
//   1. 明文内层帧：EncodeTcp(sid, frame) → 后面是 [u8 type][u16 len][payload]
//   2. E2E 密文：  EncodeTcpRaw(sid, sealed) → 后面是 [u64 counter][cipher][tag16]
// 也就是说，**QUIC 路径同样使用这个外层头**：QUIC 流只保证有序字节，不保留消息
// 边界，接收侧必须靠显式长度前缀切帧（见 SessionPump.TryExtract）。
// （早期注释写的是"QUIC 不写这个头"，与实现不符，已更正。）
//
// 字节序：u32 len 为 **big-endian**，与 Go src/vps/internal/proto/frames.go 的
// binary.BigEndian.PutUint32 对齐（见 proto.go "多字节整数统一 big-endian"）。
public static class MuxFrame
{
    public const int TcpHeaderSize = 4 + 1;

    /// <summary>
    /// mux 外层 len 的合法上界（宽松的 DoS 保护，不小于任何合法帧）。
    ///
    /// 合法最大情况是 E2E 密文：sid(1) + 计数器(8) + 帧头(3) + 负载(65535) + tag(16)。
    /// 这里取 <see cref="ProtocolConstants.MaxFramePayloadSize"/>（256KB）作上界，
    /// 比上述合法值宽松——真正的负载上限由 <see cref="FrameCodec.Encode"/> 把关。
    /// </summary>
    private static readonly uint MaxOuterLen = (uint)(ProtocolConstants.MaxFramePayloadSize + 1);

    public static byte[] EncodeTcp(byte sid, in Frame frame)
    {
        return EncodeTcpRaw(sid, FrameCodec.Encode(frame));
    }

    /// <summary>
    /// 把"已经编码好的内层帧字节"包上 mux 外层头。
    ///
    /// 为什么需要这个重载：E2E 会话先对整个内层帧做 AEAD 加密，再把**密文**放进
    /// mux 外层（[u32 len][u8 sid][密文]）。此时已无 Frame 对象可用，只有裸字节。
    /// 注意 len 字段语义：len = sid(1) + payload 总长（与 EncodeTcp(Frame) 一致）。
    /// </summary>
    public static byte[] EncodeTcpRaw(byte sid, ReadOnlySpan<byte> innerBytes)
    {
        if (sid > ProtocolConstants.MaxStreamId)
        {
            throw new ArgumentOutOfRangeException(nameof(sid));
        }
        var len = 1 + innerBytes.Length;
        var buf = new byte[4 + len];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), (uint)len);
        buf[4] = sid;
        innerBytes.CopyTo(buf.AsSpan(5));
        return buf;
    }

    public static int TryDecodeTcp(ReadOnlySpan<byte> src, out byte sid, out Frame frame, out int consumed)
    {
        sid = 0;
        frame = default;
        consumed = 0;
        if (src.Length < 5)
        {
            return 0;
        }

        var len = BinaryPrimitives.ReadUInt32BigEndian(src.Slice(0, 4));
        if (len == 0 || len > MaxOuterLen)
        {
            throw new InvalidDataException($"mux tcp len {len} invalid");
        }

        if (src.Length < 4 + (int)len)
        {
            return 0;
        }

        sid = src[4];
        var innerBytes = src.Slice(5, (int)len - 1);
        if (FrameCodec.TryDecode(innerBytes, out frame, out _) == 0)
        {
            throw new InvalidDataException("inner frame decode failed inside mux");
        }

        consumed = 4 + (int)len;
        return 1;
    }
}
