namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// 一帧 CPU 可读的 BGRA 图像。
///
/// 为什么要有这个类型：DXGI Desktop Duplication 产出的是 GPU 纹理（B8G8R8A8_UNorm），
/// 而编码前的颜色转换（BGRA→NV12）既可能走 D3D11 VideoProcessor（GPU），也可能走
/// CPU 回退路径。把"一帧"抽成纯托管对象后，CPU 路径与测试图源可以共用同一表示，
/// 从而让回环测试（测试图源→转换→编码→解码→像素校验）完全不依赖 GPU。
/// </summary>
/// <remarks>
/// 像素顺序固定为 BGRA（与 DXGI 的 <c>B8G8R8A8_UNorm</c> 内存布局一致）：
/// 每个像素 4 字节，依次是 B、G、R、A。
/// <see cref="Stride"/> 是行跨度（字节），可能大于 <c>Width*4</c>（GPU 纹理常见），
/// 因此所有消费方都必须按 Stride 寻址，不能假设紧凑排列。
/// </remarks>
public sealed class BgraFrame
{
    public BgraFrame(int width, int height, int stride, byte[] pixels, long timestampMs = 0)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (stride < width * 4) throw new ArgumentOutOfRangeException(nameof(stride), "stride 必须 >= width*4");
        ArgumentNullException.ThrowIfNull(pixels);
        if (pixels.Length < stride * height) throw new ArgumentException("像素缓冲区小于 stride*height", nameof(pixels));

        Width = width;
        Height = height;
        Stride = stride;
        Pixels = pixels;
        TimestampMs = timestampMs;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Pixels { get; }
    public long TimestampMs { get; }

    /// <summary>紧凑 BGRA 缓冲区的字节数（不含行尾填充）。</summary>
    public int TightLength => Width * Height * 4;
}

/// <summary>
/// 一帧 NV12（YUV 4:2:0 半平面）图像。
///
/// NV12 是 Media Foundation H.264 编码器与解码器最通用、支持面最广的输入/输出格式：
/// Y 平面 <c>width*height</c> 字节，紧随其后是交错排列的 UV 平面 <c>width*height/2</c> 字节
/// （每 2x2 像素共享一对 Cb/Cr）。
/// 宽高必须为偶数，否则 2x2 色度子采样无法整除。
/// </summary>
public sealed class Nv12Frame
{
    public Nv12Frame(int width, int height, byte[] buffer, long timestampMs = 0)
    {
        if (width <= 0 || (width & 1) != 0) throw new ArgumentOutOfRangeException(nameof(width), "NV12 宽度必须为正偶数");
        if (height <= 0 || (height & 1) != 0) throw new ArgumentOutOfRangeException(nameof(height), "NV12 高度必须为正偶数");
        ArgumentNullException.ThrowIfNull(buffer);
        int needed = width * height + (width * height / 2);
        if (buffer.Length < needed) throw new ArgumentException($"NV12 缓冲区至少需要 {needed} 字节", nameof(buffer));

        Width = width;
        Height = height;
        Buffer = buffer;
        TimestampMs = timestampMs;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Buffer { get; }
    public long TimestampMs { get; }

    public int YPlaneSize => Width * Height;
    public int UvPlaneSize => Width * Height / 2;
    public int TotalSize => YPlaneSize + UvPlaneSize;
}
