using System.Buffers.Binary;
using DeskLink.Protocol.Common;

namespace DeskLink.Protocol.Frames;

// 内层帧（已认证加密前的明文视图；加密版本见 Crypto.SealedFrame）。
// 格式: [u8 type][u16 payloadLen][payload bytes...]
// 注：TCP mux 模式下，外层还会包 [u32 len][u8 sid] 头。
//
// 字节序（与 Go 侧 src/vps/internal/proto/frames.go / proto.go 严格一致）：
//   多字节整数一律 **big-endian**。
//   Go proto.go 明确约定"多字节整数统一 big-endian"，HandshakeMessages（C#）亦用
//   BigEndian。早先本文件误用 little-endian，导致 C# ↔ Go 编同一帧得到不同字节
//   （E2EAlignmentTests 长期失败）。2026-09-26 更正为 big-endian。
public readonly record struct Frame(ProtocolConstants.FrameType Type, ReadOnlyMemory<byte> Payload)
{
    public const int HeaderSize = 1 + 2;
}

public static class FrameCodec
{
    /// <summary>
    /// 帧负载的**线上格式**上限：帧头长度字段是 u16，故最多 65535 字节。
    ///
    /// 为什么必须单独存在（与 Go 侧 MaxFramePayloadWireSize 对齐的真 bug 记录）：
    ///   本方法早期只校验 <see cref="ProtocolConstants.MaxFramePayloadSize"/>（256KB），
    ///   而写长度用的是 <c>(ushort)payload.Length</c>。于是 65536..262144 之间的负载会
    ///   **静默回绕**长度字段（70000 → 4464），但 payload 字节仍然全量写出：
    ///   接收端按错误的 len 切片，剩余字节被当成下一帧的帧头，整条流从此错位且不报错。
    ///
    /// 需要更大的负载时必须由上层分片（DESIGN.md 的 FileChunk / DesktopVideo 分片语义），
    /// 而不是把长度字段撑爆。
    /// </summary>
    public const int MaxFramePayloadWireSize = 65535;

    // 帧负载必须 ≤ MaxFramePayloadWireSize（u16 长度字段的硬约束），否则抛协议错误。
    //
    // 注意：这里**不能**用 ProtocolConstants.MaxFramePayloadSize(256KB) 作为判据——
    // 那个常量只是缓冲区/DoS 保护上界，比线上格式宽松，用它做校验正是上面那个 bug 的成因。
    public static byte[] Encode(in Frame frame)
    {
        var payload = frame.Payload;
        if (payload.Length > MaxFramePayloadWireSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"payload {payload.Length} > u16 wire limit {MaxFramePayloadWireSize} (needs fragmentation)");
        }

        var buf = new byte[Frame.HeaderSize + payload.Length];
        buf[0] = (byte)frame.Type;
        // u16 payloadLen：big-endian（高字节在前），与 Go binary.BigEndian.PutUint16 对齐
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1, 2), (ushort)payload.Length);
        payload.Span.CopyTo(buf.AsSpan(Frame.HeaderSize));
        return buf;
    }

    // 尝试从缓冲区解码一帧；返回消费字节数，失败返回 0。
    public static int TryDecode(ReadOnlySpan<byte> src, out Frame frame, out int consumed)
    {
        frame = default;
        consumed = 0;
        if (src.Length < Frame.HeaderSize)
        {
            return 0;
        }

        // u16 payloadLen：big-endian，与 Encode 对称
        var len = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(1, 2));
        // 注：len 是 ushort，天然 ≤ MaxFramePayloadWireSize(65535)，此处无需再比较上限
        // （比较 MaxFramePayloadSize 还会被编译器判为无意义比较 CS0652）。
        // 真正需要把关的是**编码侧**（Encode）——否则长度字段会静默回绕。

        var total = Frame.HeaderSize + len;
        if (src.Length < total)
        {
            return 0;
        }

        var type = (ProtocolConstants.FrameType)src[0];
        var payload = src.Slice(Frame.HeaderSize, len).ToArray();
        frame = new Frame(type, payload);
        consumed = total;
        return 1;
    }
}
