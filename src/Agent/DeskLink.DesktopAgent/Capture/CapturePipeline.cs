using DeskLink.DesktopAgent.Codec;

namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// 一条完整的桌面发送管线：帧源 → BGRA→NV12 → H.264 编码。
///
/// 之所以把三者打包成一个可整体丢弃的对象，是为了配合
/// <see cref="AccessLostRecovery"/>：DXGI 触发 ACCESS_LOST 后，
/// 捕获设备、转换器、编码器**必须一起重建**（编码器与分辨率/色彩格式绑定，
/// 只换捕获会导致尺寸不匹配）。把"管线"作为一个生命周期单元最不容易漏掉某一环。
/// </summary>
public sealed class CapturePipeline : IDisposable
{
    private readonly GpuFrameConverter? _gpuConverter;
    private bool _disposed;

    public CapturePipeline(IFrameSource source, H264Encoder encoder, GpuFrameConverter? gpuConverter)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        _gpuConverter = gpuConverter;
    }

    public IFrameSource Source { get; }
    public H264Encoder Encoder { get; }

    /// <summary>实际使用的颜色转换后端：gpu-videoprocessor 或 cpu。</summary>
    public string ConverterBackend => _gpuConverter is null ? "cpu" : "gpu-videoprocessor";

    public int Width => Source.Width;
    public int Height => Source.Height;

    /// <summary>
    /// 抓一帧并编码。
    /// </summary>
    /// <param name="timeoutMs">等待新帧超时。</param>
    /// <param name="encoded">编码后的 Annex-B 码流；无新帧时为 null。</param>
    /// <param name="accessLost">捕获失效，调用方必须走 <see cref="AccessLostRecovery.TryRebuild"/>。</param>
    /// <param name="error">失败原因。</param>
    public bool TryEncodeNext(int timeoutMs, out byte[]? encoded, out bool accessLost, out string? error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        encoded = null;
        accessLost = false;
        error = null;

        if (!Source.TryGetFrame(timeoutMs, out var bgra, out accessLost, out error))
        {
            return false;
        }
        if (bgra is null) return false;

        var nv12 = Convert(bgra);
        if (nv12 is null)
        {
            error = "颜色转换失败";
            return false;
        }

        encoded = Encoder.Encode(nv12);
        return encoded is not null;
    }

    private Nv12Frame? Convert(BgraFrame frame)
    {
        // GPU 路径是"尽力而为"：转换失败立刻回退 CPU，绝不因为 GPU 问题丢帧。
        if (_gpuConverter is not null)
        {
            if (_gpuConverter.TryConvert(frame, out var gpuResult) && gpuResult is not null)
            {
                return gpuResult;
            }
        }

        return FrameConverter.BgraToNv12Cpu(frame.Pixels, frame.Width, frame.Height, frame.Stride);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gpuConverter?.Dispose();
        Encoder.Dispose();
        Source.Dispose();
    }
}
