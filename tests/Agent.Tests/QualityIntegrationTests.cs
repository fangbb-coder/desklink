using DeskLink.DesktopAgent;
using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Codec;
using DeskLink.DesktopAgent.Pipeline;
using DeskLink.DesktopAgent.Quality;

namespace DeskLink.Agent.Tests;

/// <summary>
/// P9：把"自适应码率的输出"与"旋转"真正接到编码管线上的集成测试。
///
/// 单独测控制器/旋转只是组件级证据；这里要回答两个更实际的问题：
///   1. 控制器降到最低档算出的分辨率，编码器**真的接受**吗？
///      （自适应降分辨率如果编不出来，等于没有降级能力。）
///   2. 旋转后的画面能走完 编码→解码→像素校验 吗？尺寸交换是否贯穿整条链路？
/// </summary>
public class QualityIntegrationTests
{
    private static AdaptiveBitrateController.Options Options() => new()
    {
        MaxBitrateBps = 8_000_000,
        MaxFps = 30,
        MinChangeInterval = TimeSpan.FromMilliseconds(1),
        RecoverSamples = 2,
    };

    private static QualityFeedback Bad()
        => new(RttMs: 900, LossPercent: 20, EncodeMsPerFrame: 50, QueueDepth: 30, InputPending: false);

    /// <summary>
    /// 降级到最低档后，缩放后的分辨率必须能被 H.264 编码器接受并产出码流。
    /// 这是"降分辨率保输入响应"这条 DESIGN 要求在实现上真正可用的前提。
    /// </summary>
    [SkippableFact]
    public void DegradedResolution_IsAcceptedByEncoder()
    {
        var opt = Options();
        var controller = new AdaptiveBitrateController(opt);
        for (var i = 0; i < AdaptiveBitrateController.MaxLevel + 2; i++)
        {
            controller.Update(Bad(), TimeSpan.FromMilliseconds(10));
        }
        Assert.Equal(AdaptiveBitrateController.MaxLevel, controller.Level);

        var settings = controller.Current;
        Assert.True(settings.Scale < 1.0, "最低档应当降了分辨率");

        // 基准 320x240，按控制器给出的缩放算实际编码尺寸（保持偶数）。
        var width = MakeEven((int)Math.Round(320 * settings.Scale));
        var height = MakeEven((int)Math.Round(240 * settings.Scale));

        var encoderSettings = new H264EncoderSettings(
            width, height, Fps: settings.Fps,
            BitrateBps: settings.BitrateBps,
            PreferHardware: false,
            KeyFrameInterval: 30);

        var encoder = H264Encoder.TryCreate(encoderSettings, out var error);
        Skip.If(encoder is null, $"Media Foundation H.264 编码器不可用：{error}");

        using (encoder)
        {
            using var source = new TestPatternSource(width, height);
            var frame = source.Render(0, animate: false);
            var nv12 = FrameConverter.BgraToNv12Cpu(frame.Pixels, frame.Width, frame.Height, frame.Stride);

            var chunk = encoder!.Encode(nv12, forceKeyFrame: true);
            var flushed = encoder.FlushChunks();

            var total = (chunk?.Length ?? 0) + flushed.Sum(c => c.Length);
            Assert.True(total > 0,
                $"降到 {width}x{height} 后编码器必须仍能产出码流（档位 {controller.Level}, {settings.Label}）");
        }
    }

    /// <summary>控制器每升一档，编码尺寸都不应增大（降级方向单调）。</summary>
    [Fact]
    public void DegradeLadder_ProducesMonotoneEncoderSettings()
    {
        var opt = Options();
        var previous = AdaptiveBitrateController.ForLevel(0, opt);
        for (var level = 1; level <= AdaptiveBitrateController.MaxLevel; level++)
        {
            var current = AdaptiveBitrateController.ForLevel(level, opt);
            Assert.True(current.BitrateBps <= previous.BitrateBps);
            Assert.True(current.Fps <= previous.Fps);
            Assert.True(current.Scale <= previous.Scale);
            previous = current;
        }
    }

    /// <summary>
    /// 旋转 90° 走完整条回环链路：编码尺寸应交换为 240x320，且解码后像素仍在容差内。
    /// 这验证了"旋转必须贯穿到编码尺寸"这件事真的做到了，而不是只在转换函数里转了。
    /// </summary>
    [SkippableFact]
    public void Loopback_WithRotation90_SwapsDimensionsAndKeepsPixelsClose()
    {
        var result = LoopbackPipeline.Run(width: 320, height: 240, frameCount: 3, rotationDegrees: 90);

        Skip.If(result.Skipped, result.Message);

        Assert.True(result.Success, result.Message);
        // 尺寸必须交换 —— 这是"纵向显示器不横躺"的直接证据。
        Assert.Equal(240, result.Width);
        Assert.Equal(320, result.Height);
        Assert.True(result.EncodedBytes > 0);
        Assert.True(result.HasParameterSets, "旋转路径同样必须在首个访问单元带 SPS/PPS");
        Assert.InRange(result.MeanAbsDiff, 0, LoopbackPipeline.DefaultMeanTolerance);
    }

    [SkippableFact]
    public void Loopback_WithRotation180_KeepsDimensions()
    {
        var result = LoopbackPipeline.Run(width: 320, height: 240, frameCount: 3, rotationDegrees: 180);
        Skip.If(result.Skipped, result.Message);

        Assert.True(result.Success, result.Message);
        Assert.Equal(320, result.Width);
        Assert.Equal(240, result.Height);
    }

    [SkippableFact]
    public void Loopback_WithRotation270_SwapsDimensions()
    {
        var result = LoopbackPipeline.Run(width: 320, height: 240, frameCount: 3, rotationDegrees: 270);
        Skip.If(result.Skipped, result.Message);

        Assert.True(result.Success, result.Message);
        Assert.Equal(240, result.Width);
        Assert.Equal(320, result.Height);
    }

    [Fact]
    public void Loopback_RejectsInvalidRotation_WithoutTouchingCodec()
    {
        // 非法角度应在进入编码器之前就被拒（返回 Skipped 并说明原因），
        // 而不是让 MFT 用一个离谱的尺寸去协商然后失败。
        var result = LoopbackPipeline.Run(width: 320, height: 240, rotationDegrees: 45);
        Assert.True(result.Skipped);
        Assert.Contains("45", result.Message);
    }

    [Fact]
    public void Loopback_OddDimensionsAfterRotation_IsSkippedWithReason()
    {
        // 321x240 旋转 90° → 240x321（高为奇数）→ NV12 无法做 2x2 色度子采样。
        var result = LoopbackPipeline.Run(width: 321, height: 240, rotationDegrees: 90);
        Assert.True(result.Skipped);
        Assert.Contains("奇数", result.Message);
    }

    // ── CLI ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Cli_Rotation_ParsesValidAngles(int degrees)
    {
        var options = Program.AgentOptions.Parse(new[] { "--rotation", degrees.ToString() });
        Assert.Equal(degrees, options.Rotation);
    }

    [Theory]
    [InlineData("45")]
    [InlineData("360")]
    [InlineData("-90")]
    public void Cli_Rotation_RejectsInvalidAngles(string value)
    {
        Assert.Throws<ArgumentException>(() => Program.AgentOptions.Parse(new[] { "--rotation", value }));
    }

    [Fact]
    public void Cli_Rotation_DefaultsToZero()
        => Assert.Equal(0, Program.AgentOptions.Parse(Array.Empty<string>()).Rotation);

    private static int MakeEven(int v) => v % 2 == 0 ? v : v + 1;
}
