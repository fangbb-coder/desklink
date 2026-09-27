// 帧旋转（P9）：多显示器/DPI/旋转支持的一部分。
//
// 为什么需要它：显示器可能被设为纵向（90°/270°）或倒装（180°）。DXGI Desktop Duplication
// 抓到的是**物理扫描方向**的帧，而用户期望看到的是**逻辑方向**的画面。不处理旋转的话，
// 纵向显示器在远端会显示成横躺的画面。
//
// 实现选择：在 **BGRA 阶段**做旋转，然后再转 NV12。
//   - 备选方案是直接旋转 NV12，但那要同时处理 Y 平面与 UV 平面的 2x2 色度对，
//     代码复杂且极易在色度错位上出错（一旦错位，整幅画面偏色且难以定位）。
//   - BGRA 旋转是纯粹的行列置换，逻辑简单、可以逐像素断言。
//   代价是多一次内存搬运；旋转只发生在**模式切换时**（不是每帧），因此可以接受。
//
// 注意：旋转会交换宽高（90°/270°）。调用方必须用**旋转后**的宽高去配编码器，
// 否则 MFT 会因为输入尺寸与声明不符而拒绝。
namespace DeskLink.DesktopAgent.Capture;

/// <summary>BGRA 帧的直角旋转。</summary>
public static class FrameRotation
{
    /// <summary>合法旋转角度。</summary>
    public static bool IsValidAngle(int degrees) => degrees is 0 or 90 or 180 or 270;

    /// <summary>该角度是否交换宽高。</summary>
    public static bool SwapsDimensions(int degrees) => degrees is 90 or 270;

    /// <summary>旋转后的一帧尺寸。</summary>
    public static (int Width, int Height) RotatedSize(int width, int height, int degrees)
    {
        if (!IsValidAngle(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "只支持 0/90/180/270");
        return SwapsDimensions(degrees) ? (height, width) : (width, height);
    }

    /// <summary>
    /// 把一帧 BGRA 顺时针旋转指定角度，返回**紧凑排列**（stride = width*4）的新帧。
    /// 0° 时直接返回原帧（不复制）。
    /// </summary>
    public static BgraFrame Rotate(BgraFrame source, int degrees)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!IsValidAngle(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "只支持 0/90/180/270");
        if (degrees == 0) return source;

        var (dw, dh) = RotatedSize(source.Width, source.Height, degrees);
        var dst = new byte[dw * dh * 4];
        var sw = source.Width;
        var sh = source.Height;
        var src = source.Pixels;
        var srcStride = source.Stride;

        for (var y = 0; y < dh; y++)
        {
            for (var x = 0; x < dw; x++)
            {
                // 目标像素 (x, y) 对应源像素 (sx, sy)。三个角度分别推导：
                //   90° 顺时针 ：源左上 → 目标右上 ⇒ sx = y,          sy = sh-1-x
                //   180°       ：源左上 → 目标右下 ⇒ sx = sw-1-x,     sy = sh-1-y
                //   270° 顺时针：源左上 → 目标左下 ⇒ sx = sw-1-y,     sy = x
                int sx, sy;
                switch (degrees)
                {
                    case 90: sx = y; sy = sh - 1 - x; break;
                    case 180: sx = sw - 1 - x; sy = sh - 1 - y; break;
                    default: sx = sw - 1 - y; sy = x; break; // 270
                }

                var srcIdx = sy * srcStride + sx * 4;
                var dstIdx = (y * dw + x) * 4;
                dst[dstIdx + 0] = src[srcIdx + 0]; // B
                dst[dstIdx + 1] = src[srcIdx + 1]; // G
                dst[dstIdx + 2] = src[srcIdx + 2]; // R
                dst[dstIdx + 3] = src[srcIdx + 3]; // A
            }
        }

        return new BgraFrame(dw, dh, dw * 4, dst, source.TimestampMs);
    }

    /// <summary>旋转后再转 NV12（编码器的输入格式）。</summary>
    public static Nv12Frame RotateToNv12(BgraFrame source, int degrees)
    {
        var rotated = Rotate(source, degrees);
        return FrameConverter.BgraToNv12Cpu(rotated.Pixels, rotated.Width, rotated.Height, rotated.Stride);
    }
}
