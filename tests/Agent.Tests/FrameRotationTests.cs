using DeskLink.DesktopAgent.Capture;

namespace DeskLink.Agent.Tests;

/// <summary>
/// P9：帧旋转（多显示器 / DPI / 旋转支持的一部分）。
///
/// 用"每个像素颜色唯一编码其原坐标"的方式构造测试帧，因此可以逐像素断言映射关系，
/// 而不是只看尺寸对不对——旋转写反方向时尺寸是对的，只有逐像素校验才能发现。
/// </summary>
public class FrameRotationTests
{
    /// <summary>像素值编码原坐标：B=x*10+1，G=y*10+2，R=(x+y)*10+3，A=255。</summary>
    private static BgraFrame MakeFrame(int width, int height, int stridePadding = 0)
    {
        var stride = width * 4 + stridePadding;
        var px = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * stride + x * 4;
                px[i + 0] = (byte)(x * 10 + 1);
                px[i + 1] = (byte)(y * 10 + 2);
                px[i + 2] = (byte)((x + y) * 10 + 3);
                px[i + 3] = 255;
            }
        }
        return new BgraFrame(width, height, stride, px);
    }

    private static (byte B, byte G, byte R, byte A) Pixel(BgraFrame f, int x, int y)
    {
        var i = y * f.Stride + x * 4;
        return (f.Pixels[i], f.Pixels[i + 1], f.Pixels[i + 2], f.Pixels[i + 3]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    [InlineData(45, false)]
    [InlineData(360, false)]
    [InlineData(-90, false)]
    public void IsValidAngle_OnlyAcceptsRightAngles(int degrees, bool expected)
        => Assert.Equal(expected, FrameRotation.IsValidAngle(degrees));

    [Theory]
    [InlineData(0, false)]
    [InlineData(90, true)]
    [InlineData(180, false)]
    [InlineData(270, true)]
    public void SwapsDimensions_OnlyFor90And270(int degrees, bool expected)
        => Assert.Equal(expected, FrameRotation.SwapsDimensions(degrees));

    [Theory]
    [InlineData(3, 2, 0, 3, 2)]
    [InlineData(3, 2, 90, 2, 3)]
    [InlineData(3, 2, 180, 3, 2)]
    [InlineData(3, 2, 270, 2, 3)]
    public void RotatedSize_SwapsOnlyWhenNeeded(int w, int h, int deg, int ew, int eh)
    {
        var (aw, ah) = FrameRotation.RotatedSize(w, h, deg);
        Assert.Equal(ew, aw);
        Assert.Equal(eh, ah);
    }

    [Fact]
    public void Rotate_ZeroDegrees_ReturnsSameInstance()
    {
        var f = MakeFrame(3, 2);
        Assert.Same(f, FrameRotation.Rotate(f, 0));
    }

    [Fact]
    public void Rotate_RejectsInvalidAngle()
    {
        var f = MakeFrame(2, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameRotation.Rotate(f, 45));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameRotation.RotatedSize(2, 2, 45));
    }

    /// <summary>90° 顺时针：目标 (x,y) 取自源 (y, h-1-x)；源左上角应落到目标右上角。</summary>
    [Fact]
    public void Rotate90_Clockwise_MapsPixelsCorrectly()
    {
        const int w = 3, h = 2;
        var src = MakeFrame(w, h);
        var dst = FrameRotation.Rotate(src, 90);

        Assert.Equal(h, dst.Width);   // 宽高交换
        Assert.Equal(w, dst.Height);
        Assert.Equal(dst.Width * 4, dst.Stride); // 紧凑输出

        for (var y = 0; y < dst.Height; y++)
        {
            for (var x = 0; x < dst.Width; x++)
            {
                var expected = Pixel(src, y, h - 1 - x);
                Assert.Equal(expected, Pixel(dst, x, y));
            }
        }

        // 源左上角 → 目标右上角（这是"顺时针"的方向哨兵：写反会落到左上角）
        Assert.Equal(Pixel(src, 0, 0), Pixel(dst, dst.Width - 1, 0));
    }

    [Fact]
    public void Rotate180_MapsPixelsCorrectly()
    {
        const int w = 3, h = 2;
        var src = MakeFrame(w, h);
        var dst = FrameRotation.Rotate(src, 180);

        Assert.Equal(w, dst.Width);
        Assert.Equal(h, dst.Height);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.Equal(Pixel(src, w - 1 - x, h - 1 - y), Pixel(dst, x, y));
            }
        }
        // 源左上角 → 目标右下角
        Assert.Equal(Pixel(src, 0, 0), Pixel(dst, w - 1, h - 1));
    }

    /// <summary>270° 顺时针：目标 (x,y) 取自源 (w-1-y, x)；源左上角应落到目标左下角。</summary>
    [Fact]
    public void Rotate270_Clockwise_MapsPixelsCorrectly()
    {
        const int w = 3, h = 2;
        var src = MakeFrame(w, h);
        var dst = FrameRotation.Rotate(src, 270);

        Assert.Equal(h, dst.Width);
        Assert.Equal(w, dst.Height);

        for (var y = 0; y < dst.Height; y++)
        {
            for (var x = 0; x < dst.Width; x++)
            {
                Assert.Equal(Pixel(src, w - 1 - y, x), Pixel(dst, x, y));
            }
        }
        Assert.Equal(Pixel(src, 0, 0), Pixel(dst, 0, dst.Height - 1));
    }

    [Fact]
    public void Rotate90_FourTimes_ReturnsToOriginal()
    {
        const int w = 4, h = 3;
        var src = MakeFrame(w, h);
        var f = src;
        for (var i = 0; i < 4; i++) f = FrameRotation.Rotate(f, 90);

        Assert.Equal(w, f.Width);
        Assert.Equal(h, f.Height);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.Equal(Pixel(src, x, y), Pixel(f, x, y));
            }
        }
    }

    [Fact]
    public void Rotate90_Then270_ReturnsToOriginal()
    {
        const int w = 3, h = 5;
        var src = MakeFrame(w, h);
        var round = FrameRotation.Rotate(FrameRotation.Rotate(src, 90), 270);

        Assert.Equal(w, round.Width);
        Assert.Equal(h, round.Height);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                Assert.Equal(Pixel(src, x, y), Pixel(round, x, y));
            }
        }
    }

    /// <summary>
    /// 源帧行尾有填充（stride &gt; width*4，GPU 纹理常见）时，旋转必须按 stride 寻址，
    /// 否则每一行都会错位 —— 这类错误在紧凑缓冲的测试里看不出来。
    /// </summary>
    [Fact]
    public void Rotate_RespectsSourceStridePadding()
    {
        const int w = 3, h = 2, pad = 8;
        var src = MakeFrame(w, h, stridePadding: pad);
        Assert.Equal(w * 4 + pad, src.Stride);

        var dst = FrameRotation.Rotate(src, 90);
        for (var y = 0; y < dst.Height; y++)
        {
            for (var x = 0; x < dst.Width; x++)
            {
                Assert.Equal(Pixel(src, y, h - 1 - x), Pixel(dst, x, y));
            }
        }
    }

    [Fact]
    public void RotateToNv12_ProducesSwappedDimensionsAndCorrectBufferSize()
    {
        const int w = 4, h = 2; // 必须为偶数，NV12 色度子采样才能整除
        var src = MakeFrame(w, h);

        var nv12 = FrameRotation.RotateToNv12(src, 90);

        Assert.Equal(h, nv12.Width);
        Assert.Equal(w, nv12.Height);
        var expected = nv12.Width * nv12.Height + nv12.Width * nv12.Height / 2;
        Assert.True(nv12.Buffer.Length >= expected, $"NV12 缓冲至少 {expected} 字节");
    }

    [Fact]
    public void RotateToNv12_ZeroDegrees_KeepsDimensions()
    {
        var src = MakeFrame(4, 2);
        var nv12 = FrameRotation.RotateToNv12(src, 0);
        Assert.Equal(4, nv12.Width);
        Assert.Equal(2, nv12.Height);
    }

    /// <summary>
    /// 真实显示器枚举必须给出合法的旋转角度（0/90/180/270）。
    /// 无桌面会话时列表为空，视为通过（不是失败）。
    /// </summary>
    [SkippableFact]
    public void ListMonitors_ReportsValidRotation()
    {
        var monitors = DuplicationCapture.ListMonitors();
        Skip.If(monitors.Count == 0, "当前会话枚举不到显示器（无桌面 / 无 DXGI 适配器）");

        foreach (var m in monitors)
        {
            Assert.True(FrameRotation.IsValidAngle(m.RotationDegrees),
                $"显示器 {m.Index} 上报了非法旋转角度 {m.RotationDegrees}");
        }
    }
}
