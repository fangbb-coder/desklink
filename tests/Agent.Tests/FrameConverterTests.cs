using DeskLink.DesktopAgent.Capture;

namespace DeskLink.Agent.Tests;

/// <summary>
/// BGRA → NV12（以及反向）颜色转换的正确性。
///
/// 期望值不是"跑一遍代码把结果抄下来"，而是按 BT.709 有限范围的公开公式手工算出后
/// 硬编码在测试里，所以这些用例能真正抓到矩阵写错、通道顺序颠倒、范围缩放错误。
///   Y  = 16 + (219/255) * (0.2126R + 0.7152G + 0.0722B)
///   Cb = 128 + (224/255) * (B - Yf) / 1.8556
///   Cr = 128 + (224/255) * (R - Yf) / 1.5748
/// </summary>
public class FrameConverterTests
{
    /// <summary>构造一张纯色 BGRA 图。</summary>
    private static byte[] SolidBgra(int width, int height, byte r, byte g, byte b)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            px[i * 4] = b;
            px[i * 4 + 1] = g;
            px[i * 4 + 2] = r;
            px[i * 4 + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void BgraToNv12_Black2x2_IsLimitedRangeBlack()
    {
        var px = SolidBgra(2, 2, 0, 0, 0);
        var nv12 = FrameConverter.BgraToNv12Cpu(px, 2, 2, 2 * 4);

        // 2x2 → Y 平面 4 字节 + 一组 UV
        Assert.Equal(6, nv12.TotalSize);
        Assert.Equal(new byte[] { 16, 16, 16, 16 }, nv12.Buffer[..4]);
        // 纯灰阶的色差必须精确落在 128，不能有半点偏移（否则说明矩阵不对称）
        Assert.Equal(128, nv12.Buffer[4]);
        Assert.Equal(128, nv12.Buffer[5]);
    }

    [Fact]
    public void BgraToNv12_White2x2_IsLimitedRangeWhite()
    {
        var px = SolidBgra(2, 2, 255, 255, 255);
        var nv12 = FrameConverter.BgraToNv12Cpu(px, 2, 2, 2 * 4);

        Assert.Equal(new byte[] { 235, 235, 235, 235 }, nv12.Buffer[..4]);
        Assert.Equal(128, nv12.Buffer[4]);
        Assert.Equal(128, nv12.Buffer[5]);
    }

    [Theory]
    // 灰阶阶梯：v → Y（按 BT.709 有限范围手工计算）
    [InlineData(0, 16)]
    [InlineData(51, 60)]
    [InlineData(102, 104)]
    [InlineData(153, 147)]
    [InlineData(204, 191)]
    [InlineData(255, 235)]
    public void BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral(byte v, byte expectedY)
    {
        // 4x4 全部同色，便于检查 2x2 色度平均后的结果
        var px = SolidBgra(4, 4, v, v, v);
        var nv12 = FrameConverter.BgraToNv12Cpu(px, 4, 4, 4 * 4);

        Assert.Equal(24, nv12.TotalSize); // 16 (Y) + 8 (UV)
        for (int i = 0; i < 16; i++)
        {
            Assert.InRange(nv12.Buffer[i], expectedY - 1, expectedY + 1);
        }
        for (int i = 16; i < 24; i++)
        {
            Assert.InRange(nv12.Buffer[i], 127, 129);
        }
    }

    [Theory]
    // 纯色 → (Y, Cb, Cr)，按 BT.709 有限范围手工计算
    [InlineData(255, 0, 0, 63, 102, 240)] // 红
    [InlineData(0, 255, 0, 173, 42, 26)]  // 绿
    [InlineData(0, 0, 255, 32, 240, 118)] // 蓝
    public void BgraToNv12_Primaries_MatchExpectedNv12(
        byte r, byte g, byte b, int expectedY, int expectedCb, int expectedCr)
    {
        var px = SolidBgra(4, 4, r, g, b);
        var nv12 = FrameConverter.BgraToNv12Cpu(px, 4, 4, 4 * 4);

        for (int i = 0; i < 16; i++)
        {
            Assert.InRange(nv12.Buffer[i], expectedY - 1, expectedY + 1);
        }
        for (int i = 16; i < 24; i += 2)
        {
            Assert.InRange(nv12.Buffer[i], expectedCb - 1, expectedCb + 1);
            Assert.InRange(nv12.Buffer[i + 1], expectedCr - 1, expectedCr + 1);
        }
    }

    [Fact]
    public void BgraToNv12_ChromaIsAveragedOver2x2Block()
    {
        // 2x2 里放红、绿、蓝、白四色，检查色度确实做了 4 像素平均而不是取其中一个。
        var px = new byte[2 * 2 * 4];
        void Set(int i, byte r, byte g, byte b)
        {
            px[i * 4] = b; px[i * 4 + 1] = g; px[i * 4 + 2] = r; px[i * 4 + 3] = 255;
        }
        Set(0, 255, 0, 0);
        Set(1, 0, 255, 0);
        Set(2, 0, 0, 255);
        Set(3, 255, 255, 255);

        var nv12 = FrameConverter.BgraToNv12Cpu(px, 2, 2, 2 * 4);

        // 四个像素的 Cb 期望值：102.3 / 41.7 / 240.0 / 128.0 → 平均 128.0
        // 四个像素的 Cr 期望值：240.0 / 26.3 / 117.7 / 128.0 → 平均 128.0
        Assert.InRange(nv12.Buffer[4], 127, 129);
        Assert.InRange(nv12.Buffer[5], 127, 129);
    }

    [Fact]
    public void BgraToNv12_RespectsRowStride()
    {
        // stride > width*4：行尾有填充，转换必须按 stride 寻址，否则颜色会错位。
        const int w = 2, h = 2, stride = 16;
        var px = new byte[stride * h];
        // 有效像素：第一行红、第二行蓝；填充区填垃圾值（若被读到会让 Y 变怪）
        void Set(int x, int y, byte r, byte g, byte b)
        {
            int p = y * stride + x * 4;
            px[p] = b; px[p + 1] = g; px[p + 2] = r; px[p + 3] = 255;
        }
        Set(0, 0, 255, 0, 0);
        Set(1, 0, 255, 0, 0);
        Set(0, 1, 0, 0, 255);
        Set(1, 1, 0, 0, 255);
        for (int i = 8; i < stride; i++) px[i] = 0xFF; // 行尾填充

        var nv12 = FrameConverter.BgraToNv12Cpu(px, w, h, stride);

        // 第一行红 → Y≈63，第二行蓝 → Y≈32
        Assert.InRange(nv12.Buffer[0], 62, 64);
        Assert.InRange(nv12.Buffer[1], 62, 64);
        Assert.InRange(nv12.Buffer[2], 31, 33);
        Assert.InRange(nv12.Buffer[3], 31, 33);
    }

    [Theory]
    [InlineData(3, 4)]
    [InlineData(4, 3)]
    [InlineData(0, 4)]
    public void BgraToNv12_RejectsOddOrZeroDimensions(int width, int height)
    {
        var px = new byte[Math.Max(1, width * height * 4)];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameConverter.BgraToNv12Cpu(px, width, height, Math.Max(4, width * 4)));
    }

    [Fact]
    public void Nv12ToBgra_RoundTripsGreyExactly()
    {
        // 灰阶是最严格的自检：正向/反向若矩阵不对称，灰阶会立刻出现色偏。
        const int w = 4, h = 4;
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            byte v = (byte)(i * 15);
            px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 255;
        }

        var nv12 = FrameConverter.BgraToNv12Cpu(px, w, h, w * 4);
        var back = new byte[w * h * 4];
        FrameConverter.Nv12ToBgraCpu(nv12.Buffer, w, h, back, w * 4);

        for (int i = 0; i < w * h; i++)
        {
            Assert.InRange(back[i * 4], px[i * 4] - 2, px[i * 4] + 2);         // B
            Assert.InRange(back[i * 4 + 1], px[i * 4 + 1] - 2, px[i * 4 + 1] + 2); // G
            Assert.InRange(back[i * 4 + 2], px[i * 4 + 2] - 2, px[i * 4 + 2] + 2); // R
            Assert.Equal(255, back[i * 4 + 3]);                                 // A 固定不透明
        }
    }

    [Fact]
    public void Nv12ToBgra_RoundTripsPrimariesWithinTolerance()
    {
        const int w = 4, h = 4;
        foreach (var (r, g, b) in new[] { ((byte)255, (byte)0, (byte)0), ((byte)0, (byte)255, (byte)0), ((byte)0, (byte)0, (byte)255) })
        {
            var px = SolidBgra(w, h, r, g, b);
            var nv12 = FrameConverter.BgraToNv12Cpu(px, w, h, w * 4);
            var back = new byte[w * h * 4];
            FrameConverter.Nv12ToBgraCpu(nv12.Buffer, w, h, back, w * 4);

            Assert.InRange(back[0], b - 2, b + 2);
            Assert.InRange(back[1], g - 2, g + 2);
            Assert.InRange(back[2], r - 2, r + 2);
        }
    }
}
