using DeskLink.DesktopAgent.Codec;

namespace DeskLink.Agent.Tests;

/// <summary>
/// P9 验收：软件 MFT 兜底下的 720p 硬上限。
///
/// v1 约定：当回落到软件 H.264 编码器时，输出尺寸按"短边 720、长边按比例但不超 1280"
/// 等比缩放，避免中端 CPU 软编 1080p 时的"卡顿+模糊双重降级"（DESIGN 性能与降级）。
/// 硬件 MFT 不受此限（能编 4K 也不卡）。
///
/// 这些用例不依赖 Media Foundation 实际存在 —— 它们只测纯函数 <see cref="H264EncoderSettings.ClampForSoftwareEncoder"/>，
/// 因此在 CI / 无媒体组件环境也能跑。
/// </summary>
public class H264EncoderSettingsCapTests
{
    [Fact]
    public void Clamp_NoOp_WhenAlreadyUnderLimit()
    {
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(1280, 720);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Fact]
    public void Clamp_NoOp_WhenSmallerThanLimit()
    {
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(640, 360);
        Assert.Equal(640, w);
        Assert.Equal(360, h);
    }

    [Fact]
    public void Clamp_Downscales_1920x1080_To_720pShortEdge()
    {
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(1920, 1080);
        // 短边 1080 → 720，长边 1920 → 1280（1280*720 / 1080 = 853.33 → 853；再按整数四舍五入到 1280 附近的 16 对齐）
        // 实际算法：scale = 720/1080 = 0.6667，newW = 1280, newH = 720
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Fact]
    public void Clamp_Downscales_2560x1440_To_720pShortEdge()
    {
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(2560, 1440);
        // 短边 1440 → 720，长边 2560 → 1280
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Fact]
    public void Clamp_PreservesAspectRatio_ForVerticalDisplay()
    {
        // 纵向 1080x1920 → 720p 纵向 720x1280
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(1080, 1920);
        Assert.Equal(720, w);
        Assert.Equal(1280, h);
    }

    [Fact]
    public void Clamp_ForcesEvenDimensions()
    {
        // 1001x1001 → 短边 1001→720，scale=0.7193，newW=720.7,newH=720.7 → 720
        // 这里主要验证：若数学结果让任一维变成奇数，clamp 必须夹紧为偶数
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(1001, 1001);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
    }

    [Fact]
    public void Clamp_NeverEnlarges_SmallerInput()
    {
        // 已经比 720p 小的不应被放大
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(800, 450);
        Assert.Equal(800, w);
        Assert.Equal(450, h);
    }

    [Fact]
    public void Clamp_HandlesZeroOrNegative_AsIdentity()
    {
        // 无效输入直接透传（让上层 NV12 检查报错），不要在这里抛
        Assert.Equal((0, 0), H264EncoderSettings.ClampForSoftwareEncoder(0, 0));
    }
}
