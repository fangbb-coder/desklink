namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// BGRA ⇄ NV12 颜色转换。
///
/// 两条实现路径：
///   1. <see cref="FrameConverter.BgraToNv12Cpu"/> / <see cref="FrameConverter.Nv12ToBgraCpu"/>
///      —— 纯 CPU，永远可用，且**可被单元测试精确校验**（P7 验收的像素校验依赖它）。
///   2. <see cref="FrameConverter.TryCreateGpu"/> —— D3D11 VideoProcessor 路径，
///      需要真实 D3D11 设备；初始化任一步失败即返回 null，由调用方回退 CPU。
///
/// 颜色空间选择（重要，写死并前后一致）：
///   使用 BT.709 有限范围（limited range，Y∈[16,235]，Cb/Cr∈[16,240]）。
///   正向与反向使用同一套系数，所以"编码→解码"回环不会引入色偏——
///   H.264 编解码器只做有损压缩，不重解释颜色空间。
///   若正向用 BT.601 而反向用 BT.709（或反过来），纯红会偏移几十级，
///   这是回环测试最容易踩的坑，务必保持对称。
/// </summary>
public static class FrameConverter
{
    // BT.709 luma 系数（R/G/B 权重，和为 1）
    private const float Kr = 0.2126f;
    private const float Kg = 0.7152f;
    private const float Kb = 0.0722f;

    // BT.709 色度换算分母
    private const float CbDivisor = 1.8556f;
    private const float CrDivisor = 1.5748f;

    // 有限范围的缩放因子
    private const float LumaScale = 219f / 255f;   // Yf(0..255) → Y(16..235)
    private const float ChromaScale = 224f / 255f; // 色差 → (16..240)

    /// <summary>
    /// BGRA → NV12（CPU，BT.709 有限范围）。
    /// 色度按 2x2 块"先逐像素算、再取平均"，等价于标准的 box 平均子采样。
    /// </summary>
    /// <param name="bgra">BGRA 像素，按 <paramref name="stride"/> 逐行寻址。</param>
    /// <param name="width">图像宽度，必须为偶数。</param>
    /// <param name="height">图像高度，必须为偶数。</param>
    /// <param name="stride">源行跨度（字节），必须 ≥ width*4。</param>
    public static Nv12Frame BgraToNv12Cpu(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (width <= 0 || (width & 1) != 0) throw new ArgumentOutOfRangeException(nameof(width), "宽度必须为正偶数");
        if (height <= 0 || (height & 1) != 0) throw new ArgumentOutOfRangeException(nameof(height), "高度必须为正偶数");
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride), "stride 必须 >= width*4");
        if (bgra.Length < stride * height) throw new ArgumentException("源缓冲区小于 stride*height", nameof(bgra));

        int yPlaneSize = width * height;
        int chromaWidth = width / 2;
        int chromaHeight = height / 2;

        var buffer = new byte[yPlaneSize + yPlaneSize / 2];

        // 色度累加器：交错存放 Cb(偶)/Cr(奇) 的"相对 128 的偏移"，最后除以 4 得到 2x2 平均值。
        // 用 float 累加而不是逐像素取整后再平均，是为了让灰阶输入精确落在 Cb=Cr=128 上。
        var chromaAcc = new float[chromaWidth * chromaHeight * 2];

        for (int y = 0; y < height; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * width;
            int chromaRow = (y >> 1) * chromaWidth * 2;

            for (int x = 0; x < width; x++)
            {
                int p = srcRow + x * 4;
                float b = bgra[p];
                float g = bgra[p + 1];
                float r = bgra[p + 2];

                float yf = Kr * r + Kg * g + Kb * b;
                buffer[dstRow + x] = ClampToByte(MathF.Round(16f + LumaScale * yf, MidpointRounding.AwayFromZero));

                int chromaIndex = chromaRow + (x >> 1) * 2;
                chromaAcc[chromaIndex] += ChromaScale * (b - yf) / CbDivisor;
                chromaAcc[chromaIndex + 1] += ChromaScale * (r - yf) / CrDivisor;
            }
        }

        int uvBase = yPlaneSize;
        for (int i = 0; i < chromaAcc.Length; i++)
        {
            buffer[uvBase + i] = ClampToByte(MathF.Round(128f + chromaAcc[i] / 4f, MidpointRounding.AwayFromZero));
        }

        return new Nv12Frame(width, height, buffer);
    }

    /// <summary>
    /// NV12 → BGRA（CPU，BT.709 有限范围）。与 <see cref="BgraToNv12Cpu"/> 严格互为逆运算。
    /// 色度采用"最近邻"（共址），不做插值——与正向的 box 平均配对使用，回环误差可控。
    /// </summary>
    public static void Nv12ToBgraCpu(ReadOnlySpan<byte> nv12, int width, int height, Span<byte> bgra, int stride)
    {
        if (width <= 0 || (width & 1) != 0) throw new ArgumentOutOfRangeException(nameof(width), "宽度必须为正偶数");
        if (height <= 0 || (height & 1) != 0) throw new ArgumentOutOfRangeException(nameof(height), "高度必须为正偶数");
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride), "stride 必须 >= width*4");
        int yPlaneSize = width * height;
        if (nv12.Length < yPlaneSize + yPlaneSize / 2) throw new ArgumentException("NV12 源缓冲区太小", nameof(nv12));
        if (bgra.Length < stride * height) throw new ArgumentException("目标缓冲区太小", nameof(bgra));

        int chromaWidth = width / 2;

        for (int y = 0; y < height; y++)
        {
            int yRow = y * width;
            int uvRow = yPlaneSize + (y >> 1) * width;
            int dstRow = y * stride;

            for (int x = 0; x < width; x++)
            {
                float yy = (nv12[yRow + x] - 16f) * (255f / 219f);
                int uvIndex = uvRow + (x >> 1) * 2;
                float cb = (nv12[uvIndex] - 128f) * (255f / 224f);
                float cr = (nv12[uvIndex + 1] - 128f) * (255f / 224f);

                float r = yy + CrDivisor * cr;
                float b = yy + CbDivisor * cb;
                float g = (yy - Kr * r - Kb * b) / Kg;

                int p = dstRow + x * 4;
                bgra[p] = ClampToByte(MathF.Round(b, MidpointRounding.AwayFromZero));
                bgra[p + 1] = ClampToByte(MathF.Round(g, MidpointRounding.AwayFromZero));
                bgra[p + 2] = ClampToByte(MathF.Round(r, MidpointRounding.AwayFromZero));
                bgra[p + 3] = 255;
            }
        }
    }

    /// <summary>
    /// 尝试创建 GPU 转换器（D3D11 VideoProcessor）。
    ///
    /// 注意：本机（无真实 GPU 的构建环境）**无法验证**该路径的运行时正确性，
    /// 因此它只被"实时捕获管线"使用，且任何一步失败都返回 null 让调用方回退 CPU。
    /// 回环测试与所有单测只覆盖 CPU 路径。
    /// </summary>
    public static GpuFrameConverter? TryCreateGpu(Vortice.Direct3D11.ID3D11Device device, int width, int height)
        => GpuFrameConverter.TryCreate(device, width, height);

    internal static byte ClampToByte(float v)
    {
        int i = (int)v;
        if (i < 0) return 0;
        if (i > 255) return 255;
        return (byte)i;
    }
}
