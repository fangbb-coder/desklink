using System.Runtime.InteropServices;
using DeskLink.DesktopAgent.Capture;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace DeskLink.DesktopAgent.Codec;

/// <summary>H.264 编码参数。</summary>
public sealed record H264EncoderSettings(
    int Width,
    int Height,
    int Fps,
    int BitrateBps,
    bool PreferHardware,
    int KeyFrameInterval)
{
    /// <summary>
    /// 软件 MFT 兜底下的分辨率硬上限（短边 720，长边按比例但不超过 1280）。
    ///
    /// 为什么需要：1080p 软件 H.264 在中端 CPU 上常常吃不满 30fps（前置缓冲带来的延迟
    /// 加上编码本身），强行让它"撑住"只会让远端画面一卡一卡。直接缩到 720p 短边，
    /// 带宽与码率都压到合理范围，画面更顺、输入也更跟手——这与 DESIGN「先降码率帧率，
    /// 后降分辨率」并不冲突：自适应降级是**链路**侧的妥协，而这里是**编码器**侧
    /// 的硬上限（v1 软件同步 MFT 的实际能力上限）。硬件 MFT 不受限。
    /// </summary>
    public const int SoftwareMaxShortEdge = 720;
    public const int SoftwareMaxLongEdge = 1280;

    /// <summary>按分辨率与帧率给一个保守的默认码率（桌面内容比自然图像好压）。</summary>
    public static H264EncoderSettings Default(int width, int height, bool preferHardware = true)
    {
        int fps = 30;
        long pixels = (long)width * height;
        // 经验值：约 0.09 bit/pixel/frame，1080p30 落在 5~6 Mbps。
        int bitrate = (int)Math.Clamp(pixels * fps * 9 / 100, 800_000, 20_000_000);
        return new H264EncoderSettings(width, height, fps, bitrate, preferHardware, KeyFrameInterval: 60);
    }

    /// <summary>
    /// 把请求的尺寸按软件 MFT 上限等比缩放（保持长宽比、最长边不超 1280、短边不超 720）。
    /// 已经更小的输入不会被放大。
    /// </summary>
    /// <remarks>
    /// 缩放后宽度/高度必须为偶数（NV12 要求）。软件 MFT 一律是奇数边缘的常见来源是
    /// 1280x720 这种"刚到上限"的组合本身已经是偶数；但"原始 1920x1080 缩到 720p"
    /// 按 0.6666 算出来 1280x720 整好是偶数。为防止极端输入下出现奇数边，结尾夹紧到偶数。
    /// </remarks>
    public static (int Width, int Height) ClampForSoftwareEncoder(int width, int height)
    {
        if (width <= 0 || height <= 0) return (width, height);
        int longEdge = Math.Max(width, height);
        int shortEdge = Math.Min(width, height);

        if (shortEdge <= SoftwareMaxShortEdge && longEdge <= SoftwareMaxLongEdge)
        {
            return (MakeEven(width), MakeEven(height));
        }

        // 以短边 = 720 为基准做等比缩放，长边自动按比例（必定 <= 2 * 720 = 1440，再夹到 1280）。
        double scale = (double)SoftwareMaxShortEdge / shortEdge;
        int newW = (int)Math.Round(width * scale);
        int newH = (int)Math.Round(height * scale);
        if (Math.Max(newW, newH) > SoftwareMaxLongEdge)
        {
            double longScale = (double)SoftwareMaxLongEdge / Math.Max(newW, newH);
            newW = (int)Math.Round(newW * longScale);
            newH = (int)Math.Round(newH * longScale);
        }
        return (MakeEven(Math.Max(2, newW)), MakeEven(Math.Max(2, newH)));
    }

    private static int MakeEven(int v) => v % 2 == 0 ? v : Math.Max(2, v - 1);
}

/// <summary>
/// Media Foundation H.264 编码器（MFTEnumEx → IMFTransform，可选 ICodecAPI 调参）。
///
/// 工作方式：
///   - 输入 NV12，输出 Annex-B 形式的 H.264（MFVideoFormat_H264 的输出本身就是
///     带起始码的 Annex-B，SPS/PPS 随 IDR 一起出现在码流里，不需要额外封装）。
///   - 启动时**先试同步硬件 MFT，再回落同步软件 MFT**，并通过 <see cref="IsHardware"/>
///     与 <see cref="BackendName"/> 暴露实际用的是哪一个（DESIGN 要求会话状态里标注）。
///   - 首帧强制 IDR，并把提取出的 SPS/PPS 缓存在 <see cref="ParameterSets"/>，
///     满足"会话开始先发 SPS/PPS"。
///
/// 已知限制（诚实声明）：异步硬件 MFT（Windows 上 GPU 编码器的常见形态）需要
/// MF_TRANSFORM_ASYNC_UNLOCK + IMFMediaEventGenerator 事件驱动，v1 未实现，
/// 因此本机若只有异步硬件 MFT，会落到软件 MFT。
/// </summary>
public sealed class H264Encoder : IDisposable
{
    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    private readonly IMFTransform _transform;
    private readonly CodecApi? _codecApi;
    private readonly List<IMFActivate> _activates;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly int _keyFrameInterval;
    private readonly int _bitrateBps;
    private readonly bool _preferHardware;
    private readonly bool _providesSamples;
    private readonly int _outputBufferSize;
    private bool _started;
    private bool _disposed;

    private H264Encoder(
        IMFTransform transform,
        CodecApi? codecApi,
        List<IMFActivate> activates,
        bool isHardware,
        string backendName,
        int width,
        int height,
        int fps,
        int keyFrameInterval,
        int bitrateBps,
        bool preferHardware)
    {
        _transform = transform;
        _codecApi = codecApi;
        _activates = activates;
        IsHardware = isHardware;
        BackendName = backendName;
        _width = width;
        _height = height;
        _fps = fps;
        _keyFrameInterval = keyFrameInterval;
        _bitrateBps = bitrateBps;
        _preferHardware = preferHardware;

        var info = _transform.GetOutputStreamInfo(0);
        _providesSamples = (info.Flags & (int)OutputStreamInfoFlags.OutputStreamProvidesSamples) != 0;
        _outputBufferSize = info.Size > 0 ? info.Size : width * height * 2;
    }

    public bool IsHardware { get; }

    /// <summary>实际使用的 MFT 描述，例如 "software: Microsoft H264 Video Encoder MFT"。</summary>
    public string BackendName { get; }

    /// <summary>首个 IDR 里提取出的 SPS+PPS（Annex-B，含起始码）；未编码前为 null。</summary>
    public byte[]? ParameterSets { get; private set; }

    public long FramesEncoded { get; private set; }

    public static H264Encoder? TryCreate(H264EncoderSettings settings, out string? error)
    {
        error = null;
        if (settings.Width <= 0 || (settings.Width & 1) != 0 ||
            settings.Height <= 0 || (settings.Height & 1) != 0)
        {
            error = "NV12 要求宽高为正偶数";
            return null;
        }

        MediaFoundationRuntime.EnsureStarted();
        try
        {
            // 一次枚举所有同步 MFT，再按"是否硬件"分组：硬件优先，软件兜底。
            var activates = MediaFoundationRuntime.EnumSyncTransforms(
                TransformCategoryGuids.VideoEncoder, VideoFormatGuids.NV12, VideoFormatGuids.H264);

            var ordered = new List<(IMFActivate Activate, bool IsHardware)>();
            if (settings.PreferHardware)
            {
                foreach (var a in activates)
                {
                    if (MediaFoundationRuntime.IsHardware(a)) ordered.Add((a, true));
                }
            }
            foreach (var a in activates)
            {
                if (!MediaFoundationRuntime.IsHardware(a)) ordered.Add((a, false));
            }

            var created = TryConfigure(ordered, settings, out error);
            if (created is not null) return created;

            foreach (var a in activates) a.Dispose();
            if (string.IsNullOrEmpty(error))
            {
                error = "no usable H.264 encoder MFT found";
            }
            return null;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static H264Encoder? TryConfigure(
        List<(IMFActivate Activate, bool IsHardware)> candidates, H264EncoderSettings settings, out string? error)
    {
        error = null;
        var allActivates = new List<IMFActivate>();

        foreach (var (activate, isHardware) in candidates)
        {
            IMFTransform? transform = null;
            CodecApi? codecApi = null;
            try
            {
                transform = activate.ActivateObject<IMFTransform>();

                // 软件 MFT 兜底下硬限 720p：先按"这个候选是软件"重新计算尺寸再去 SetTypes。
                // 不能在 TryCreate 顶层统一 clamp，那样硬件候选也会被卡到 720p。
                var effectiveSettings = isHardware
                    ? settings
                    : ClampSettingsForSoftware(settings);

                if (!TrySetTypes(transform, effectiveSettings)) continue;

                codecApi = CodecApi.TryCreate(transform.NativePointer);
                ConfigureCodecApi(codecApi, effectiveSettings);

                // 首帧强制关键帧，保证 SPS/PPS 在会话最开始就发出。
                codecApi?.TrySetUInt32(CodecApiGuids.AVEncVideoForceKeyFrame, 1);

                transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                var name = MediaFoundationRuntime.GetFriendlyName(activate);
                if (string.IsNullOrWhiteSpace(name)) name = isHardware ? "hardware MFT" : "software MFT";
                var backend = (isHardware ? "hardware: " : "software: ") + name;

                // 所有候选的 IMFActivate 都由编码器统一持有，Dispose 时一起释放。
                allActivates.AddRange(candidates.Select(c => c.Activate));

                return new H264Encoder(
                    transform, codecApi, allActivates, isHardware, backend,
                    effectiveSettings.Width, effectiveSettings.Height,
                    effectiveSettings.Fps, effectiveSettings.KeyFrameInterval,
                    effectiveSettings.BitrateBps, effectiveSettings.PreferHardware)
                {
                    _started = true,
                };
            }
            catch
            {
                codecApi?.Dispose();
                transform?.Dispose();
                // 换下一个候选 MFT
            }
        }

        error = "no usable H.264 encoder MFT found";
        return null;
    }

    /// <summary>
    /// 把 settings 按软件 MFT 720p 上限重新计算。
    /// 走 `with` 表达式保留其它字段，只覆盖尺寸（码率/帧率不变，避免"先降码率"也跑进来）。
    /// </summary>
    private static H264EncoderSettings ClampSettingsForSoftware(H264EncoderSettings settings)
    {
        var (w, h) = H264EncoderSettings.ClampForSoftwareEncoder(settings.Width, settings.Height);
        if (w == settings.Width && h == settings.Height) return settings;
        return settings with { Width = w, Height = h };
    }

    private static bool TrySetTypes(IMFTransform transform, H264EncoderSettings settings)
    {
        try
        {
            // 【注意】顺序很关键：H.264 编码器 MFT 必须先 SetOutputType 再 SetInputType。
            // 反过来 SetInputType 会返回 MF_E_TRANSFORM_TYPE_NOT_SET (0xC00D6D60)，
            // 因为编码器要先知道输出码流格式才能决定可接受的输入格式。
            // 这是最容易踩的坑：错误信息说的是"类型未设置"，很容易误判成别的问题。
            var output = MediaFactory.MFCreateMediaType();
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            output.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            output.SetUInt64(MediaTypeAttributeKeys.FrameSize, Pack(settings.Width, settings.Height));
            output.SetUInt64(MediaTypeAttributeKeys.FrameRate, Pack(settings.Fps, 1));
            output.SetUInt64(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            output.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, 2);
            output.SetUInt32(MediaTypeAttributeKeys.AvgBitrate, (uint)settings.BitrateBps);
            output.SetUInt32(MediaTypeAttributeKeys.Mpeg2Profile, 77); // eAVEncH264VProfile_Main
            transform.SetOutputType(0, output, 0);

            var input = MediaFactory.MFCreateMediaType();
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            input.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            input.SetUInt64(MediaTypeAttributeKeys.FrameSize, Pack(settings.Width, settings.Height));
            input.SetUInt64(MediaTypeAttributeKeys.FrameRate, Pack(settings.Fps, 1));
            input.SetUInt64(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            input.SetUInt32(MediaTypeAttributeKeys.InterlaceMode, 2); // MFVideoInterlace_Progressive
            transform.SetInputType(0, input, 0);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ConfigureCodecApi(CodecApi? codecApi, H264EncoderSettings settings)
    {
        if (codecApi is null) return;
        // 参数是"尽力而为"：软件 MFT 对部分属性的支持因版本而异，失败不影响可用性。
        codecApi.TrySetUInt32(CodecApiGuids.AVEncCommonRateControlMode, 2); // eAVEncCommonRateControlMode_UnconstrainedVBR
        codecApi.TrySetUInt32(CodecApiGuids.AVEncCommonMeanBitRate, (uint)settings.BitrateBps);
        codecApi.TrySetUInt32(CodecApiGuids.AVEncMPVGOPSize, (uint)settings.KeyFrameInterval);
        // 尽量压低编码器前瞻缓冲带来的延迟（实测默认缓冲约十几帧，详见 Flush 的注释）。
        codecApi.TrySetUInt32(CodecApiGuids.AVLowLatencyMode, 1);
    }

    /// <summary>
    /// 编码一帧 NV12，返回本次产生的 Annex-B 码流（可能为空，表示编码器还在缓冲）。
    /// </summary>
    public byte[]? Encode(Nv12Frame frame, bool forceKeyFrame = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"帧尺寸 {frame.Width}x{frame.Height} 与编码器 {_width}x{_height} 不一致", nameof(frame));
        }

        if (forceKeyFrame)
        {
            _codecApi?.TrySetUInt32(CodecApiGuids.AVEncVideoForceKeyFrame, 1);
        }

        long timestampMs = frame.TimestampMs > 0 ? frame.TimestampMs : FramesEncoded * 1000 / Math.Max(1, _fps);

        using var sample = CreateSample(frame.Buffer, frame.TotalSize, timestampMs);
        try
        {
            _transform.ProcessInput(0, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
        {
            // 编码器尚未就绪，本帧被丢弃；下一帧继续。
            return null;
        }

        FramesEncoded++;
        var chunks = DrainOutput();
        var result = Concat(chunks);
        if (result is null) return null;

        if (ParameterSets is null)
        {
            ParameterSets = ExtractParameterSets(result);
        }

        return result;
    }

    /// <summary>
    /// 冲刷编码器：发送 DRAIN 把内部缓冲的帧全部编码出来，并返回剩余的 Annex-B 码流。
    ///
    /// 为什么必须有这个方法：Media Foundation 的软件 H.264 编码器（"H264 Encoder MFT"）
    /// 内部有**前瞻缓冲**——实测喂 3 帧时 ProcessOutput 一直返回
    /// MF_E_TRANSFORM_NEED_MORE_INPUT，一帧码流都不产出；喂 40 帧只产出 23 帧。
    /// 必须发送 MFT_MESSAGE_COMMAND_DRAIN 才会把缓冲里的帧吐出来。
    /// 影响：
    ///   - 回环测试/短序列必须调用本方法，否则永远拿不到码流；
    ///   - 实时会话里表现为约十几帧的编码延迟，属于 v1 已知限制（见交付说明）。
    /// DRAIN 之后编码器进入"流已结束"状态，不可继续 Encode。
    /// </summary>
    public byte[]? Flush() => Concat(FlushChunks());

    /// <summary>
    /// 同 <see cref="Flush"/>，但保持每个访问单元（access unit）独立返回。
    /// 解码端一次只应喂一个访问单元，否则解码器可能只吐出一帧，
    /// 回环测试就无法确定拿到的是哪一帧（实测确实如此）。
    /// </summary>
    public List<byte[]> FlushChunks()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        }
        catch
        {
            return new List<byte[]>();
        }

        return DrainOutput();
    }

    private static byte[]? Concat(List<byte[]> chunks)
    {
        if (chunks.Count == 0) return null;
        int total = 0;
        foreach (var c in chunks) total += c.Length;
        var result = new byte[total];
        int offset = 0;
        foreach (var c in chunks)
        {
            Buffer.BlockCopy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }
        return result;
    }

    private List<byte[]> DrainOutput()
    {
        var chunks = new List<byte[]>();
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

            if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                allocated?.Dispose();
                break;
            }

            if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                allocated?.Dispose();
                // 编码器中途要求换输出类型（少见）；重新提交同一套 NV12→H264 类型后继续。
                // 注意保持当前自适应码率值：这里若不透传 bitrate（旧代码硬编码 1），
                // 协商后的实际编码码率会跌到 1 bps 量级，画质立即崩坏。
                TrySetTypes(_transform, new H264EncoderSettings(
                    _width, _height, _fps, _bitrateBps, _preferHardware, _keyFrameInterval));
                continue;
            }

            if (result.Failure)
            {
                allocated?.Dispose();
                break;
            }

            var outSample = outputBuffer.Sample;
            if (outSample is not null)
            {
                try
                {
                    var data = ReadSample(outSample);
                    if (data.Length > 0) chunks.Add(data);
                }
                finally
                {
                    // MFT 提供的输出样本是 COM 对象（OutputStreamProvidesSamples），
                    // 用完必须 Release，否则每次 ProcessOutput 泄漏一个样本引用。
                    outSample.Dispose();
                }
            }

            allocated?.Dispose();
            if (outSample is null) break;
        }

        return chunks;
    }

    private static IMFSample CreateSample(byte[] data, int length, long timestampMs)
    {
        var buffer = MediaFactory.MFCreateMemoryBuffer(length);
        buffer.Lock(out var ptr, out _, out _);
        try
        {
            Marshal.Copy(data, 0, ptr, length);
        }
        finally
        {
            buffer.Unlock();
        }
        buffer.CurrentLength = length;

        var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        buffer.Dispose();
        sample.SampleTime = timestampMs * 10_000; // 毫秒 → 100ns
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

    private static byte[]? ExtractParameterSets(byte[] annexB)
    {
        var sps = new List<byte>();
        foreach (var nal in AnnexB.SplitNalUnits(annexB))
        {
            int type = AnnexB.GetNalUnitType(nal);
            if (type != AnnexB.NalTypeSps && type != AnnexB.NalTypePps) continue;
            sps.AddRange(new byte[] { 0, 0, 0, 1 });
            sps.AddRange(nal);
        }
        return sps.Count > 0 ? sps.ToArray() : null;
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_started)
            {
                _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
            }
        }
        catch { /* 释放路径，忽略 */ }

        _codecApi?.Dispose();
        _transform.Dispose();
        foreach (var a in _activates) a.Dispose();
        MediaFoundationRuntime.Shutdown();
    }
}
