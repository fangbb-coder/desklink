using System.IO;
using DeskLink.Client.Media;
using DeskLink.Client.Services;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>媒体负载编解码往返、输入合并器、分片重组、NV12→BGRA 转换。</summary>
public class MediaPayloadCodecTests
{
    [Fact]
    public void DesktopVideoChunk_RoundTrips_Flags_And_Data()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var chunk = new DesktopVideoChunk(42, 123456789, IsKeyFrame: true, IsLastFragment: false, data);

        var decoded = DesktopVideoChunk.Decode(chunk.Encode());

        Assert.Equal(42u, decoded.FrameSeq);
        Assert.Equal(123456789ul, decoded.PtsMicros);
        Assert.True(decoded.IsKeyFrame);
        Assert.False(decoded.IsLastFragment);
        Assert.Equal(data, decoded.Data);
    }

    [Fact]
    public void DesktopVideoChunk_Decode_Rejects_Short_Payload()
    {
        Assert.Throws<InvalidDataException>(() => DesktopVideoChunk.Decode(new byte[DesktopVideoChunk.FixedSize - 1]));
    }

    [Fact]
    public void DesktopConfigPayload_RoundTrips()
    {
        var cfg = new DesktopConfigPayload(1920, 1080, DesktopConfigPayload.CodecH264,
            DesktopConfigPayload.BackendSoftware, 30, 4_000_000, 1, 90);

        var decoded = DesktopConfigPayload.Decode(cfg.Encode());

        Assert.Equal(cfg, decoded);
        Assert.Equal(DesktopConfigPayload.Size, cfg.Encode().Length);
    }

    [Fact]
    public void SessionStatsPayload_RoundTrips_Including_Negative_Rtt()
    {
        var stats = new SessionStatsPayload(299, 1500, -1, Degraded: true);

        var decoded = SessionStatsPayload.Decode(stats.Encode());

        Assert.Equal(stats, decoded);
        Assert.Equal(-1, decoded.RttMs);
        Assert.True(decoded.Degraded);
    }

    [Fact]
    public void InputMouseMove_RoundTrips_And_Clamps_To_Permille()
    {
        Assert.Equal((500, 250), InputEventCodec.DecodeMouseMove(InputEventCodec.EncodeMouseMove(500, 250)));

        // 越界必须被夹到 0..1000（客户端可能算出窗口外的坐标）。
        Assert.Equal((1000, 0), InputEventCodec.DecodeMouseMove(InputEventCodec.EncodeMouseMove(2000, -5)));
        Assert.Equal((0, 1000), InputEventCodec.DecodeMouseMove(InputEventCodec.EncodeMouseMove(int.MinValue, int.MaxValue)));
    }

    [Fact]
    public void InputMouseButton_RoundTrips()
    {
        var (button, down) = InputEventCodec.DecodeMouseButton(InputEventCodec.EncodeMouseButton(2, true));
        Assert.Equal(2, button);
        Assert.True(down);
    }

    [Fact]
    public void InputWheel_Clamps_To_Int16()
    {
        Assert.Equal(120, InputEventCodec.DecodeWheel(InputEventCodec.EncodeWheel(120)));
        Assert.Equal(short.MaxValue, InputEventCodec.DecodeWheel(InputEventCodec.EncodeWheel(int.MaxValue)));
        Assert.Equal(short.MinValue, InputEventCodec.DecodeWheel(InputEventCodec.EncodeWheel(int.MinValue)));
    }

    [Fact]
    public void InputKey_RoundTrips_Scancode_And_Extended_Flag()
    {
        var (scan, extended, down) = InputEventCodec.DecodeKey(InputEventCodec.EncodeKey(0x4B, true, false));
        Assert.Equal(0x4B, scan);
        Assert.True(extended);
        Assert.False(down);
    }

    [Fact]
    public void MouseMoveCoalescer_Throttles_Burst_To_One_Send()
    {
        var c = new MouseMoveCoalescer(minIntervalMs: 5);

        // 同一毫秒内 1000 次提交：只应立刻发出第一次。
        int sent = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (c.Submit(i, i, nowMs: 5000, out _)) sent++;
        }

        Assert.Equal(1, sent);
        Assert.True(c.HasPending);

        // 窗口过后冲刷：拿到的是**最新**位置，而不是被丢弃的中间值。
        Assert.True(c.TryFlush(5005, out var latest));
        Assert.Equal((999, 999), latest);
        Assert.False(c.HasPending);
    }

    [Fact]
    public void MouseMoveCoalescer_Does_Not_Exceed_Rate()
    {
        var c = new MouseMoveCoalescer(minIntervalMs: 5);

        // 1 秒内最多约 1 + 1000/5 = 201 次发送。
        int sent = 0;
        for (long t = 0; t <= 1000; t++)
        {
            if (c.Submit((int)t, 0, t, out _)) sent++;
        }

        Assert.True(sent <= 202, $"发送次数 {sent} 超过节流上限");
        Assert.True(sent >= 100, $"发送次数 {sent} 过少，节流配置异常");
    }

    [Fact]
    public void MouseMoveCoalescer_Flush_Returns_False_When_Nothing_Pending()
    {
        var c = new MouseMoveCoalescer(5);
        Assert.False(c.TryFlush(1000, out _));
    }

    [Fact]
    public void VideoFragmentAssembler_Concatenates_Until_LastFragment()
    {
        var asm = new VideoFragmentAssembler();

        Assert.Null(asm.Add(new DesktopVideoChunk(1, 100, IsKeyFrame: true, IsLastFragment: false, new byte[] { 1, 2 })));
        var unit = asm.Add(new DesktopVideoChunk(1, 100, IsKeyFrame: false, IsLastFragment: true, new byte[] { 3, 4 }));

        Assert.NotNull(unit);
        Assert.Equal(1u, unit!.FrameSeq);
        Assert.True(unit.IsKeyFrame);       // 任一分片标关键帧，整帧即关键帧
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, unit.Data);
    }

    [Fact]
    public void VideoFragmentAssembler_Drops_Residue_When_Sequence_Changes()
    {
        var asm = new VideoFragmentAssembler();
        asm.Add(new DesktopVideoChunk(1, 100, false, IsLastFragment: false, new byte[] { 1 }));

        // 序号跳到 2：上一帧残片应被丢弃，不能拼进新帧。
        var unit = asm.Add(new DesktopVideoChunk(2, 200, false, IsLastFragment: true, new byte[] { 2 }));

        Assert.NotNull(unit);
        Assert.Equal(2u, unit!.FrameSeq);
        Assert.Equal(new byte[] { 2 }, unit.Data);
    }

    [Fact]
    public void Nv12ToBgra_Neutral_Chroma_Produces_Grey()
    {
        // Y=126, U=V=128 → 三通道相等（灰色），alpha=255。
        // NV12 大小 = w*h (Y) + w*h/2 (UV) = 16 + 8 = 24。
        var nv12 = new byte[4 * 4 + 4 * 4 / 2];
        Array.Fill(nv12, (byte)126, 0, 16);
        Array.Fill(nv12, (byte)128, 16, nv12.Length - 16);

        var bgra = H264Decoder.Nv12ToBgra(nv12, 4, 4);

        Assert.Equal(4 * 4 * 4, bgra.Length);
        for (int i = 0; i < 16; i++)
        {
            var o = i * 4;
            Assert.Equal(bgra[o], bgra[o + 1]);
            Assert.Equal(bgra[o + 1], bgra[o + 2]);
            Assert.Equal(255, bgra[o + 3]);
        }
    }

    [Fact]
    public void Nv12ToBgra_Maps_Limited_Range_Endpoints()
    {
        // Y=16 → 0（黑），Y=235 → 255（白），U=V=128 保持中性。
        var black = BuildNv12(2, 2, y: 16);
        var white = BuildNv12(2, 2, y: 235);

        Assert.Equal(0, H264Decoder.Nv12ToBgra(black, 2, 2)[0]);
        Assert.Equal(255, H264Decoder.Nv12ToBgra(white, 2, 2)[0]);
    }

    private static byte[] BuildNv12(int width, int height, byte y)
    {
        var yPlane = width * height;
        var buf = new byte[yPlane + yPlane / 2];
        Array.Fill(buf, y, 0, yPlane);
        Array.Fill(buf, (byte)128, yPlane, buf.Length - yPlane);
        return buf;
    }

    [Fact]
    public void NullFrameSource_Produces_Frames_That_Change_Over_Time()
    {
        var src = new NullFrameSource(8, 4);
        var first = src.Next();
        var second = src.Next();

        Assert.Equal(8, first.Width);
        Assert.Equal(4, first.Height);
        Assert.Equal(8 * 4 * 4, first.Pixels.Length);
        Assert.True(src.IsAvailable);
        Assert.NotEqual(first.Pixels, second.Pixels); // 画面确实在刷新
    }

    [Fact]
    public void MediaChannelFraming_Uses_FrameType_From_Shared_Constants()
    {
        // 媒体通道复用 ProtocolConstants.FrameType（而不是另定义枚举），防止两套定义漂移。
        var frame = MediaChannelFraming.Encode(ProtocolConstants.FrameType.InputKey, new byte[] { 0 });
        Assert.Equal((byte)ProtocolConstants.FrameType.InputKey, frame[4]);
    }
}
