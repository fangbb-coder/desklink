using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Codec;
using DeskLink.DesktopAgent.Pipeline;

namespace DeskLink.Agent.Tests;

/// <summary>
/// P7 的核心验收用例：本地回环（测试图源 → BGRA→NV12 → H.264 编码 → H.264 解码 → 像素校验）。
///
/// Media Foundation 的编解码器不是所有机器都有（无媒体组件的 Server Core / 精简镜像），
/// 因此拿不到编解码器时用 <see cref="SkippableFactAttribute"/> 明确 Skip 并给出原因，
/// 绝不写成"占位通过"——本仓库把 Assert.True(true) 占位视为造假（见 README）。
/// </summary>
public class LoopbackRoundTripTests
{
    [SkippableFact]
    public void EncodeDecodeRoundTrip_PixelsMatchWithinTolerance()
    {
        var result = LoopbackPipeline.Run(width: 320, height: 240, frameCount: 3);

        Skip.If(result.Skipped, $"Media Foundation 不可用：{result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.False(result.Skipped);
        Assert.True(result.EncodedBytes > 0, "编码器必须产出码流");

        // 容差同时写在断言里，避免"阈值被悄悄放宽"这种退化。
        Assert.InRange(result.MeanAbsDiff, 0, LoopbackPipeline.DefaultMeanTolerance);
        Assert.InRange(result.MaxBlockMeanDiff, 0, LoopbackPipeline.DefaultBlockTolerance);
    }

    [SkippableFact]
    public void FirstAccessUnit_ContainsSpsPpsAndIdr()
    {
        // DESIGN 要求"会话开始先发 SPS/PPS"；这里直接验证编码器首个访问单元里
        // 确实有 NAL type 7/8 以及 IDR（type 5）。
        var result = LoopbackPipeline.Run(width: 320, height: 240, frameCount: 2);

        Skip.If(result.Skipped, $"Media Foundation 不可用：{result.Message}");

        Assert.True(result.Success, result.Message);
        Assert.True(result.HasParameterSets, "首个访问单元必须包含 SPS 与 PPS");
        Assert.True(result.HasKeyFrame, "首个访问单元必须是 IDR 关键帧");
    }

    [SkippableFact]
    public void Encoder_ReportsBackendAndFallsBackToSoftware()
    {
        var settings = new H264EncoderSettings(320, 240, Fps: 30, BitrateBps: 2_000_000,
            PreferHardware: true, KeyFrameInterval: 30);

        var encoder = H264Encoder.TryCreate(settings, out var error);
        Skip.If(encoder is null, $"Media Foundation H.264 编码器不可用：{error}");

        using (encoder)
        {
            // 不硬断言"一定是软件"（有机器确实有同步硬件 MFT），但必须能明确说出用的是哪个，
            // 因为 DESIGN 要求会话状态里标注编码器种类。
            Assert.False(string.IsNullOrWhiteSpace(encoder!.BackendName));
            Assert.True(
                encoder.BackendName.StartsWith("hardware:", StringComparison.Ordinal) ||
                encoder.BackendName.StartsWith("software:", StringComparison.Ordinal),
                $"BackendName 必须带硬件/软件前缀，实际为 {encoder.BackendName}");
        }
    }

    [SkippableFact]
    public void Encoder_FlushReturnsBufferedAccessUnits()
    {
        // MF 软件 H.264 编码器有前瞻缓冲：不 DRAIN 就一帧码流都拿不到。
        // 这条用例把该行为固定下来，防止将来有人"优化"掉 Flush 调用。
        var settings = new H264EncoderSettings(320, 240, Fps: 30, BitrateBps: 2_000_000,
            PreferHardware: false, KeyFrameInterval: 30);
        var encoder = H264Encoder.TryCreate(settings, out var error);
        Skip.If(encoder is null, $"Media Foundation H.264 编码器不可用：{error}");

        using (encoder)
        {
            using var source = new TestPatternSource(320, 240);
            for (int i = 0; i < 3; i++)
            {
                var frame = source.Render(i, animate: false);
                var nv12 = FrameConverter.BgraToNv12Cpu(frame.Pixels, frame.Width, frame.Height, frame.Stride);
                encoder!.Encode(nv12, forceKeyFrame: i == 0);
            }

            var chunks = encoder!.FlushChunks();
            Assert.NotEmpty(chunks);
            Assert.Contains(chunks, c => AnnexB.ContainsKeyFrame(c));
        }
    }
}
