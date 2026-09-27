namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// 内置测试图源：彩色竖条 + 一个随帧号水平移动的方块。
///
/// 用途：`--test-pattern` 让整条编码/解码管线在无桌面、无 GPU 的环境里跑起来，
/// 并且给出"已知内容"——回环测试可以据此校验解码后的像素。
/// 图内容完全由帧号决定（确定性），所以测试不需要等待真实时间流逝。
/// </summary>
public sealed class TestPatternSource : IFrameSource
{
    // 标准 SMPTE 风格彩条（R,G,B），顺序固定以便测试断言。
    private static readonly (byte R, byte G, byte B)[] Bars =
    {
        (255, 255, 255), // 白
        (255, 255, 0),   // 黄
        (0, 255, 255),   // 青
        (0, 255, 0),     // 绿
        (255, 0, 255),   // 品红
        (255, 0, 0),     // 红
        (0, 0, 255),     // 蓝
        (0, 0, 0),       // 黑
    };

    private readonly int _blockSize;
    private long _frameIndex;

    public TestPatternSource(int width, int height)
    {
        if (width <= 0 || (width & 1) != 0) throw new ArgumentOutOfRangeException(nameof(width), "宽度必须为正偶数");
        if (height <= 0 || (height & 1) != 0) throw new ArgumentOutOfRangeException(nameof(height), "高度必须为正偶数");
        Width = width;
        Height = height;
        _blockSize = Math.Max(8, Math.Min(width, height) / 8);
    }

    public int Width { get; }
    public int Height { get; }
    public string Description => $"test-pattern {Width}x{Height}";

    public bool TryGetFrame(int timeoutMs, out BgraFrame? frame, out bool accessLost, out string? error)
    {
        accessLost = false;
        error = null;
        frame = Render(_frameIndex++);
        return true;
    }

    /// <summary>
    /// 按帧号渲染（确定性，便于测试直接指定帧号）。
    /// </summary>
    /// <param name="frameIndex">帧号，决定方块位置。</param>
    /// <param name="animate">
    /// false 时方块固定在起始位置，所有帧内容完全一致。
    /// 回环测试必须用 false：MF 软件 H.264 编码器有前瞻缓冲，DRAIN 后码流与输入帧的
    /// 对应关系在"画面会动"时难以确定，静态图才能做无歧义的像素校验。
    /// </param>
    public BgraFrame Render(long frameIndex, bool animate = true)
    {
        int width = Width;
        int height = Height;
        int stride = width * 4;
        var pixels = new byte[stride * height];

        // 上半部分彩条，下半部分灰阶渐变：灰阶部分用来验证颜色转换是否保持中性
        // （BGRA→NV12→BGRA 在灰阶上不应引入色偏，这是最容易暴露矩阵写错的区域）。
        int barsBottom = height / 2;

        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                byte r, g, b;

                if (y < barsBottom)
                {
                    int barIndex = x * Bars.Length / width;
                    (r, g, b) = Bars[barIndex];
                }
                else
                {
                    byte v = (byte)(x * 255 / Math.Max(1, width - 1));
                    r = g = b = v;
                }

                int p = row + x * 4;
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        }

        // 方块：黑色外框 + 纯红填充，跨彩条与灰阶，用来制造锐利边缘
        // （锐利边缘是 H.264 最容易产生振铃的地方，块平均校验能容忍它）。
        int travel = Math.Max(1, width - _blockSize);
        int blockX = animate ? (int)(frameIndex * 7 % travel) : 0;
        int blockY = Math.Max(0, (height - _blockSize) / 2);
        FillRect(pixels, stride, blockX, blockY, _blockSize, _blockSize, 255, 0, 0);

        return new BgraFrame(width, height, stride, pixels, frameIndex);
    }

    private static void FillRect(byte[] pixels, int stride, int x0, int y0, int w, int h, byte r, byte g, byte b)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            int row = y * stride;
            for (int x = x0; x < x0 + w; x++)
            {
                int p = row + x * 4;
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        }
    }

    public void Dispose()
    {
        // 无原生资源
    }
}
