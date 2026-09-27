using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DeskLink.Client.Media;

/// <summary>一帧 BGRA32 像素（可直接喂给 <c>WriteableBitmap</c>）。</summary>
public sealed record BgraFrame(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Media Foundation H.264 解码器（客户端侧）：Annex-B H.264 → BGRA32。
///
/// 与 Agent 侧解码器的差别：Agent 只需 NV12 做像素校验，客户端要显示，
/// 所以这里在 MF 输出 NV12 后做一次 CPU 的 NV12→BGRA 转换（WPF 的 WriteableBitmap
/// 不认 NV12）。只枚举**同步** MFT（硬件解码器多为异步，v1 不支持），
/// 与 Agent 的策略一致。
///
/// 降级：MF 不可用 / 无可用解码器时 <see cref="TryCreate"/> 返回 null 并给出原因，
/// 由上层退回 <see cref="NullFrameSource"/> 而不是崩溃——UI 必须仍能打开。
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    private readonly IMFTransform _transform;
    private readonly List<IMFActivate> _activates;
    private readonly bool _providesSamples;
    // 非 readonly：流格式变化（远端改分辨率/旋转）后按新尺寸扩容，
    // 否则按初始尺寸固定的缓冲装不下更大的帧，解码输出被截断。
    private int _outputBufferSize;
    private int _width;
    private int _height;
    private bool _outputTypeSet;
    private bool _disposed;

    private H264Decoder(IMFTransform transform, List<IMFActivate> activates, string backendName,
        int width, int height, bool outputTypeSet)
    {
        _transform = transform;
        _activates = activates;
        BackendName = backendName;
        _width = width;
        _height = height;
        _outputTypeSet = outputTypeSet;

        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputBufferSize = info.Size > 0 ? info.Size : Math.Max(width * height * 3, 1024 * 1024);
    }

    public string BackendName { get; }
    public int Width => _width;
    public int Height => _height;

    /// <summary>创建解码器；失败返回 null 并输出原因（不抛，交给上层降级）。</summary>
    public static H264Decoder? TryCreate(int width, int height, out string? error)
    {
        error = null;
        try
        {
            MediaFoundationRuntime.EnsureStarted();

            var activates = MediaFoundationRuntime.EnumSyncTransforms(
                TransformCategoryGuids.VideoDecoder, VideoFormatGuids.H264, VideoFormatGuids.NV12);

            foreach (var activate in activates)
            {
                IMFTransform? transform = null;
                try
                {
                    transform = activate.ActivateObject<IMFTransform>();

                    var input = MediaFactory.MFCreateMediaType();
                    input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                    input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                    input.SetUInt64(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
                    input.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, 2);
                    transform.SetInputType(0, input, 0);

                    bool outputSet = TrySetOutputType(transform, width, height);

                    transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                    transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                    var name = MediaFoundationRuntime.GetFriendlyName(activate);
                    if (string.IsNullOrWhiteSpace(name)) name = "software MFT";

                    return new H264Decoder(transform, activates, "software: " + name, width, height, outputSet);
                }
                catch
                {
                    transform?.Dispose();
                }
            }

            foreach (var a in activates) a.Dispose();
            error = "未找到可用的 H.264 解码器 MFT";
            return null;
        }
        catch (Exception ex)
        {
            error = $"Media Foundation 初始化失败：{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static bool TrySetOutputType(IMFTransform transform, int width, int height)
    {
        try
        {
            var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            output.SetUInt64(MediaTypeAttributeKeys.FrameSize, Pack(width, height));
            output.SetUInt64(MediaTypeAttributeKeys.FrameRate, Pack(30, 1));
            output.SetUInt64(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, 2);
            transform.SetOutputType(0, output, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解码一个 Annex-B 访问单元；没有产出（需要更多输入）时返回 null。</summary>
    public BgraFrame? Decode(byte[] annexB)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(annexB);
        if (annexB.Length == 0) return null;

        using var sample = CreateSample(annexB);
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            return null;
        }

        return DrainOutput();
    }

    /// <summary>冲刷解码器，取出内部缓冲的剩余帧（输出重排序）。</summary>
    public BgraFrame? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        }
        catch
        {
            return null;
        }
        return DrainOutput();
    }

    private BgraFrame? DrainOutput()
    {
        for (int guard = 0; guard < 32; guard++)
        {
            var outputBuffer = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = null!,
                Status = 0,
                Events = null!,
            };

            IMFSample? allocated = null;
            if (!_providesSamples)
            {
                allocated = MediaFactory.MFCreateSample();
                var buffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                allocated.AddBuffer(buffer);
                outputBuffer.Sample = allocated;
            }

            Result result;
            try
            {
                result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref outputBuffer, out _);
            }
            catch (SharpGenException ex)
            {
                result = ex.ResultCode;
            }

            if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                allocated?.Dispose();
                NegotiateOutputType();
                continue;
            }

            if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                allocated?.Dispose();
                return null;
            }

            if (result.Failure)
            {
                allocated?.Dispose();
                return null;
            }

            var outSample = outputBuffer.Sample;
            BgraFrame? frame = null;
            if (outSample is not null)
            {
                var nv12 = ReadSample(outSample);
                var yPlane = _width * _height;
                if (nv12.Length >= yPlane + yPlane / 2)
                {
                    frame = new BgraFrame(_width, _height, Nv12ToBgra(nv12, _width, _height));
                }
            }

            allocated?.Dispose();
            if (frame is not null) return frame;
            if (outSample is null) return null;
        }

        return null;
    }

    /// <summary>
    /// NV12 → BGRA32 的 CPU 转换。
    /// 用 BT.709 limited-range 的定点系数（整数运算，避免每像素浮点开销）。
    /// 必须与 Agent 端 FrameConverter.BgraToNv12Cpu 的 BT.709（Kr=0.2126/
    /// Kg=0.7152/Kb=0.0722）配对：早期这里误用 BT.601 系数，编码端 709、
    /// 解码端 601，端到端色相/饱和度系统性偏移（饱和色偏移可达几十级）。
    /// 这段是纯 CPU 的已知热点，若将来成为瓶颈应换成 D3D11 VideoProcessor（GPU 转换）。
    /// </summary>
    public static byte[] Nv12ToBgra(ReadOnlySpan<byte> nv12, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        var yPlane = width * height;
        var uvPlane = yPlane;

        for (int y = 0; y < height; y++)
        {
            var yRow = y * width;
            var uvRow = uvPlane + (y / 2) * width;
            for (int x = 0; x < width; x++)
            {
                int yy = nv12[yRow + x];
                int uvIndex = uvRow + (x & ~1);
                int u = nv12[uvIndex] - 128;
                int v = nv12[uvIndex + 1] - 128;

                // limited-range Y:16..235 → 0..255（BT.709 色度定点系数：
                // R=1.5748V, G=-0.1873U-0.4681V, B=1.8556U，全部 ×256 取整）
                int c = Math.Max(0, yy - 16) * 298;
                int r = (c + 403 * v + 128) >> 8;
                int g = (c - 48 * u - 120 * v + 128) >> 8;
                int b = (c + 475 * u + 128) >> 8;

                int o = (yRow + x) * 4;
                bgra[o + 0] = Clamp(b);
                bgra[o + 1] = Clamp(g);
                bgra[o + 2] = Clamp(r);
                bgra[o + 3] = 255;
            }
        }

        return bgra;
    }

    private static byte Clamp(int v) => v < 0 ? (byte)0 : v > 255 ? (byte)255 : (byte)v;

    private void NegotiateOutputType()
    {
        if (TrySetOutputType(_transform, _width, _height))
        {
            _outputTypeSet = true;
            return;
        }

        for (int i = 0; i < 32; i++)
        {
            IMFMediaType? type = null;
            try
            {
                type = _transform.GetOutputAvailableType(0, i);
            }
            catch
            {
                return;
            }

            if (type is null) return;
            try
            {
                if (type.MajorType != MediaTypeGuids.Video) continue;

                var info = type.Get<RegisterTypeInfo>(MediaTypeAttributeKeys.Subtype);
                if (info.GuidSubtype != VideoFormatGuids.NV12) continue;

                ulong frameSize = type.GetUInt64(MediaTypeAttributeKeys.FrameSize);
                int w = (int)(frameSize >> 32);
                int h = (int)(frameSize & 0xFFFFFFFF);
                if (w > 0 && h > 0)
                {
                    _width = w;
                    _height = h;
                }

                _transform.SetOutputType(0, type, 0);
                _outputTypeSet = true;
                RefreshOutputBufferSize();
                return;
            }
            catch
            {
                // 继续尝试下一个可用类型
            }
            finally
            {
                type.Dispose();
            }
        }
    }

    /// <summary>按当前输出类型重查 MFT 要求的输出缓冲大小（流格式变化后调用）。</summary>
    private void RefreshOutputBufferSize()
    {
        var info = _transform.GetOutputStreamInfo(0);
        _outputBufferSize = info.Size > 0 ? info.Size : Math.Max(_width * _height * 3, 1024 * 1024);
    }

    private static IMFSample CreateSample(byte[] data)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(data.Length);
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = data.Length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = 0;
        sample.SampleDuration = 10_000_000 / 30;
        return sample;
    }

    private static byte[] ReadSample(IMFSample sample)
    {
        using var contiguous = sample.ConvertToContiguousBuffer();
        contiguous.Lock(out var ptr, out _, out int currentLength);
        try
        {
            if (currentLength <= 0) return Array.Empty<byte>();
            var data = new byte[currentLength];
            Marshal.Copy(ptr, data, 0, currentLength);
            return data;
        }
        finally
        {
            contiguous.Unlock();
        }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_outputTypeSet)
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            }
        }
        catch { /* 释放路径，忽略 */ }

        _transform.Dispose();
        foreach (var a in _activates) a.Dispose();
        MediaFoundationRuntime.Shutdown();
    }
}
