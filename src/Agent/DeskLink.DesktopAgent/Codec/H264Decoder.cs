using System.Runtime.InteropServices;
using DeskLink.DesktopAgent.Capture;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DeskLink.DesktopAgent.Codec;

/// <summary>
/// Media Foundation H.264 解码器：Annex-B H.264 → NV12。
///
/// 存在意义：P7 的验收点是"测试图源→编码→解码→像素校验"。没有解码器就只能
/// 校验码流字节数，无法证明画面内容正确，因此解码器是验收链路的一等公民，
/// 而不是只给 P8 客户端用的附属品。
///
/// 只枚举**同步** MFT：硬件解码器（DXVA）基本都是异步 MFT，需要事件驱动，
/// v1 不支持。软件 H.264 解码器是同步 MFT，稳定可靠，正适合做像素校验。
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    private readonly IMFTransform _transform;
    private readonly List<IMFActivate> _activates;
    private readonly bool _providesSamples;
    private readonly int _outputBufferSize;
    private int _width;
    private int _height;
    private bool _outputTypeSet;
    private bool _disposed;

    private H264Decoder(IMFTransform transform, List<IMFActivate> activates, string backendName, int width, int height, bool outputTypeSet)
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

    public static H264Decoder? TryCreate(int width, int height, out string? error)
    {
        error = null;
        MediaFoundationRuntime.EnsureStarted();
        try
        {
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

                    // 解码器通常要求先看到 SPS 才接受显式输出类型；失败时留到 STREAM_CHANGE 再协商。
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
            error = "no usable H.264 decoder MFT found";
            return null;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
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

    /// <summary>
    /// 解码一个 Annex-B 访问单元，返回一帧 NV12；没有产出（需要更多输入）时返回 null。
    /// </summary>
    public Nv12Frame? Decode(byte[] annexB)
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

    /// <summary>
    /// 冲刷解码器：发送 DRAIN 后把剩余帧取出来。
    /// 有些解码器会把最后一帧压在内部（输出重排序），不冲刷就拿不到，
    /// 回环测试若只喂一帧就会误判为"解码失败"。
    /// </summary>
    public Nv12Frame? Flush()
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

    private Nv12Frame? DrainOutput()
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
            Nv12Frame? frame = null;
            if (outSample is not null)
            {
                var data = ReadSample(outSample);
                int yPlane = _width * _height;
                if (data.Length >= yPlane + yPlane / 2)
                {
                    var buffer = new byte[yPlane + yPlane / 2];
                    Buffer.BlockCopy(data, 0, buffer, 0, buffer.Length);
                    frame = new Nv12Frame(_width, _height, buffer);
                }
            }

            allocated?.Dispose();
            if (frame is not null) return frame;
            if (outSample is null) return null;
        }

        return null;
    }

    /// <summary>
    /// 解码器在真正解出第一帧后才知道尺寸/格式，会以 STREAM_CHANGE 要求重新协商输出类型。
    /// 优先用我们自己声明的 NV12 类型；失败则退回"枚举可用类型里第一个 NV12"。
    /// </summary>
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
                var major = type.MajorType;
                if (major != MediaTypeGuids.Video) continue;

                // 只接受 NV12；尺寸取解码器给出的真实值。
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
