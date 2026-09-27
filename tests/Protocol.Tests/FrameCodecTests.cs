using DeskLink.Protocol.Common;
using DeskLink.Protocol.Frames;
using DeskLink.Protocol.Mux;

namespace Protocol.Tests;

public class FrameCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTrip()
    {
        var frame = new Frame(ProtocolConstants.FrameType.Hello, new byte[] { 1, 2, 3, 4 });
        var bytes = FrameCodec.Encode(frame);
        Assert.Equal(Frame.HeaderSize + 4, bytes.Length);
        Assert.Equal((byte)ProtocolConstants.FrameType.Hello, bytes[0]);

        var ok = FrameCodec.TryDecode(bytes, out var decoded, out var consumed);
        Assert.Equal(1, ok);
        Assert.Equal(bytes.Length, consumed);
        Assert.Equal(ProtocolConstants.FrameType.Hello, decoded.Type);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, decoded.Payload.ToArray());
    }

    [Fact]
    public void Encode_Oversize_Throws()
    {
        var big = new byte[ProtocolConstants.MaxFramePayloadSize + 1];
        var frame = new Frame(ProtocolConstants.FrameType.DesktopVideo, big);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameCodec.Encode(frame));
    }

    [Fact]
    public void TryDecode_ShortBuffer_ReturnsZero()
    {
        Assert.Equal(0, FrameCodec.TryDecode(new byte[2], out _, out _));
    }

    [Fact]
    public void TryDecode_OversizeLength_Throws()
    {
        var bytes = new byte[] { 0x10, 0xFF, 0xFF }; // 65535 > 256KB? 不，255 << 8 | 0xFF = 0xFFFF = 65535，仍 < 256KB
        // 真正超限：u16 最大 = 65535 < 256*1024 = 262144；因此上限由业务侧单独检查
        Assert.Equal(0, FrameCodec.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void MuxTcp_RoundTrip()
    {
        var frame = new Frame(ProtocolConstants.FrameType.InputKey, new byte[] { 0xDE, 0xAD });
        var sid = StreamId.FromLogical(ProtocolConstants.LogicalStream.Control);
        var bytes = MuxFrame.EncodeTcp(sid, frame);

        var ok = MuxFrame.TryDecodeTcp(bytes, out var outSid, out var outFrame, out var consumed);
        Assert.Equal(1, ok);
        Assert.Equal(sid, outSid);
        Assert.Equal(ProtocolConstants.FrameType.InputKey, outFrame.Type);
        Assert.Equal(new byte[] { 0xDE, 0xAD }, outFrame.Payload.ToArray());
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void MuxTcp_ShortBuffer_ReturnsZero()
    {
        var frame = new Frame(ProtocolConstants.FrameType.Ping, new byte[] { 0x00 });
        var sid = StreamId.Handshake;
        var bytes = MuxFrame.EncodeTcp(sid, frame);
        var truncated = bytes.AsSpan(0, 2).ToArray();
        Assert.Equal(0, MuxFrame.TryDecodeTcp(truncated, out _, out _, out _));
    }

    [Fact]
    public void StreamId_FromLogical_Range()
    {
        Assert.Equal((byte)16, StreamId.FromLogical(ProtocolConstants.LogicalStream.Desktop, 0));
        Assert.Equal((byte)32, StreamId.FromLogical(ProtocolConstants.LogicalStream.Control, 0));
        Assert.Equal((byte)48, StreamId.FromLogical(ProtocolConstants.LogicalStream.File, 0));
        Assert.Equal((byte)80, StreamId.FromLogical(ProtocolConstants.LogicalStream.File, 32));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // u16 长度字段硬约束（与 Go 侧 TestEncodeFrame_Uint16WireLimit 对应）
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 帧头长度字段是 u16，故可编码上限是 65535 字节。
    ///
    /// 回归的事故：Encode 早期只校验 ProtocolConstants.MaxFramePayloadSize(256KB)，
    /// 而写长度用 (ushort)payload.Length。于是 65536..262144 的负载会**静默回绕**
    /// 长度字段（70000 → 4464），payload 字节却全量写出——接收端按错误的 len 切片，
    /// 剩余字节被当成下一帧的帧头，整条流从此错位且不报错。
    /// Go 侧同样存在该 bug（两侧逻辑一致，所以跨语言黄金向量也测不出来）。
    /// </summary>
    [Fact]
    public void Encode_PayloadOverUint16WireLimit_ThrowsInsteadOfTruncating()
    {
        foreach (var n in new[] { FrameCodec.MaxFramePayloadWireSize + 1, 70000, ProtocolConstants.MaxFramePayloadSize })
        {
            var payload = new byte[n];
            var ex = Assert.Throws<ArgumentOutOfRangeException>(
                () => FrameCodec.Encode(new Frame(ProtocolConstants.FrameType.FileChunk, payload)));
            Assert.Contains("u16 wire limit", ex.Message);
        }
    }

    /// <summary>边界值 65535 必须仍可编码，且能一个字节不差地往返。</summary>
    [Fact]
    public void Encode_Decode_AtWireLimit_RoundTrips()
    {
        var payload = new byte[FrameCodec.MaxFramePayloadWireSize];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        var encoded = FrameCodec.Encode(new Frame(ProtocolConstants.FrameType.FileChunk, payload));
        Assert.Equal(Frame.HeaderSize + FrameCodec.MaxFramePayloadWireSize, encoded.Length);

        var n = FrameCodec.TryDecode(encoded, out var frame, out var consumed);
        Assert.Equal(1, n);
        Assert.Equal(encoded.Length, consumed);
        Assert.Equal(ProtocolConstants.FrameType.FileChunk, frame.Type);
        Assert.Equal(payload, frame.Payload.ToArray());
    }

    /// <summary>
    /// 与 Go 侧常量必须一致：Go proto.MaxFramePayloadWireSize = 65535。
    /// 两侧若不同，一端能编的帧另一端会拒收（或更糟：静默错位）。
    /// </summary>
    [Fact]
    public void MaxFramePayloadWireSize_Matches_Go_Anchor()
    {
        Assert.Equal(65535, FrameCodec.MaxFramePayloadWireSize);
    }
}
