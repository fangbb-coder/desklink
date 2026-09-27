using System.Buffers.Binary;
using System.IO;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>媒体通道定长头编解码 + 流式剥帧（MediaFrameReader）。</summary>
public class MediaFramingTests
{
    [Fact]
    public void Encode_Writes_BigEndian_Length_Then_FrameType()
    {
        var payload = new byte[] { 0xAA, 0xBB, 0xCC };

        var frame = MediaChannelFraming.Encode(ProtocolConstants.FrameType.DesktopConfig, payload);

        // [u32 len BE = 1 + payload][u8 frameType][payload]
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x04 }, frame.AsSpan(0, 4).ToArray());
        Assert.Equal((byte)ProtocolConstants.FrameType.DesktopConfig, frame[4]);
        Assert.Equal(payload, frame.AsSpan(5).ToArray());
        Assert.Equal(MediaChannelFraming.HeaderSize + payload.Length, frame.Length);
    }

    [Fact]
    public void Encode_Rejects_Payload_Over_Wire_Limit()
    {
        var payload = new byte[MediaChannelFraming.MaxPayloadSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaChannelFraming.Encode(ProtocolConstants.FrameType.DesktopVideo, payload));
    }

    [Fact]
    public void TryReadHeader_Rejects_Zero_And_OutOfRange_Length()
    {
        // len = 0（非法：至少要有 frameType 那 1 字节）
        var zero = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10 };
        Assert.False(MediaChannelFraming.TryReadHeader(zero, out _, out _));

        // len = 1 + Max + 1（越界）
        var tooBig = new byte[5];
        BinaryPrimitives.WriteUInt32BigEndian(tooBig.AsSpan(0, 4), (uint)(1 + MediaChannelFraming.MaxPayloadSize + 1));
        tooBig[4] = 0x10;
        Assert.False(MediaChannelFraming.TryReadHeader(tooBig, out _, out _));

        // 头不完整
        Assert.False(MediaChannelFraming.TryReadHeader(new byte[3], out _, out _));
    }

    [Fact]
    public void Reader_Reassembles_Fragmented_Arrival()
    {
        var payload = new byte[100];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var frame = MediaChannelFraming.Encode(ProtocolConstants.FrameType.DesktopVideo, payload);

        var reader = new MediaFrameReader();

        // 先给前 3 字节：连头都不够。
        reader.Append(frame.AsSpan(0, 3));
        Assert.False(reader.TryRead(out _, out _));

        // 再给到头部完整但负载不全。
        reader.Append(frame.AsSpan(3, 10));
        Assert.False(reader.TryRead(out _, out _));

        // 补齐剩余。
        reader.Append(frame.AsSpan(13));
        Assert.True(reader.TryRead(out var type, out var got));
        Assert.Equal(ProtocolConstants.FrameType.DesktopVideo, type);
        Assert.Equal(payload, got);

        // 取完就没了。
        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void Reader_Handles_Multiple_Frames_In_One_Append()
    {
        var a = MediaChannelFraming.Encode(ProtocolConstants.FrameType.DesktopConfig, new byte[] { 1 });
        var b = MediaChannelFraming.Encode(ProtocolConstants.FrameType.InputMouseMove, new byte[] { 2, 3 });
        var combined = a.Concat(b).ToArray();

        var reader = new MediaFrameReader();
        reader.Append(combined);

        Assert.True(reader.TryRead(out var t1, out var p1));
        Assert.Equal(ProtocolConstants.FrameType.DesktopConfig, t1);
        Assert.Equal(new byte[] { 1 }, p1);

        Assert.True(reader.TryRead(out var t2, out var p2));
        Assert.Equal(ProtocolConstants.FrameType.InputMouseMove, t2);
        Assert.Equal(new byte[] { 2, 3 }, p2);

        Assert.False(reader.TryRead(out _, out _));
    }

    [Fact]
    public void Reader_Throws_On_Illegal_Length_Prefix()
    {
        var reader = new MediaFrameReader();
        // len = 0 → 头非法，无法恢复（长度前缀不可信），必须抛而不是静默错位。
        reader.Append(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10 });
        Assert.Throws<InvalidDataException>(() => reader.TryRead(out _, out _));
    }

    [Fact]
    public void Reader_Keeps_Remainder_After_Partial_Frame_Then_Completes()
    {
        var one = MediaChannelFraming.Encode(ProtocolConstants.FrameType.DesktopVideo, new byte[] { 9, 9 });
        var reader = new MediaFrameReader();

        // 一次性喂入"完整帧 + 下一帧的头 2 字节"。
        reader.Append(one);
        reader.Append(new byte[] { 0x00, 0x00 });
        Assert.True(reader.TryRead(out var t, out var p));
        Assert.Equal(ProtocolConstants.FrameType.DesktopVideo, t);
        Assert.Equal(new byte[] { 9, 9 }, p);
        Assert.False(reader.TryRead(out _, out _));
    }
}
