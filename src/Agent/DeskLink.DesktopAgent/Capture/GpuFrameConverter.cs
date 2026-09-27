using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// 基于 D3D11 VideoProcessor 的 BGRA → NV12 转换（GPU 路径）。
///
/// 设计取舍（重要）：
///   本机（构建/CI 环境，无可用视频处理硬件）**无法验证**该路径的运行时正确性。
///   因此它被刻意做成"全有或全无"：<see cref="TryCreate"/> 中任何一步失败（设备不支持
///   NV12 输出、VideoProcessor 创建失败、纹理创建失败）都返回 null，调用方必须回退
///   <see cref="FrameConverter.BgraToNv12Cpu"/>。回环测试与单测只覆盖 CPU 路径，
///   另有一个可跳过的用例尝试用 WARP 软件设备跑通本路径（能跑则跑，跑不了明确 Skip）。
///
/// 为什么用 VideoProcessor 而不是自己写 compute shader：
///   VideoProcessor 是 D3D11 的标准视频处理单元，驱动侧负责 BGRA→NV12 的颜色矩阵、
///   范围缩放与色度下采样，比手写 shader 更贴近 Media Foundation 的期望，也不受
///   HLSL 编译期依赖（d3dcompiler_47）影响。
/// </summary>
public sealed class GpuFrameConverter : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorInputView _inputView;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private readonly ID3D11Texture2D _inputTexture;
    private readonly ID3D11Texture2D _outputTexture;
    private readonly ID3D11Texture2D _stagingTexture;
    private readonly VideoProcessorStream[] _streams;
    private readonly int _width;
    private readonly int _height;

    private GpuFrameConverter(
        ID3D11Device device,
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext,
        ID3D11VideoProcessor processor,
        ID3D11Texture2D inputTexture,
        ID3D11Texture2D outputTexture,
        ID3D11Texture2D stagingTexture,
        ID3D11VideoProcessorInputView inputView,
        ID3D11VideoProcessorOutputView outputView,
        int width,
        int height)
    {
        _device = device;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _processor = processor;
        _inputTexture = inputTexture;
        _outputTexture = outputTexture;
        _stagingTexture = stagingTexture;
        _inputView = inputView;
        _outputView = outputView;
        _width = width;
        _height = height;
        _streams = new[]
        {
            new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView,
            },
        };
    }

    /// <summary>宽度必须是偶数（NV12 色度 2x2 子采样）。</summary>
    public static GpuFrameConverter? TryCreate(ID3D11Device device, int width, int height)
    {
        if (device is null || width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
        {
            return null;
        }

        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        ID3D11VideoProcessorEnumerator? enumerator = null;
        ID3D11VideoProcessor? processor = null;
        ID3D11Texture2D? inputTexture = null;
        ID3D11Texture2D? outputTexture = null;
        ID3D11Texture2D? stagingTexture = null;
        ID3D11VideoProcessorInputView? inputView = null;
        ID3D11VideoProcessorOutputView? outputView = null;

        try
        {
            videoDevice = device.QueryInterface<ID3D11VideoDevice>();
            videoContext = device.ImmediateContext.QueryInterface<ID3D11VideoContext>();

            var content = new VideoProcessorContentDescription
            {
                InputFrameFormat = VideoFrameFormat.Progressive,
                InputFrameRate = new Rational(30, 1),
                InputWidth = width,
                InputHeight = height,
                OutputFrameRate = new Rational(30, 1),
                OutputWidth = width,
                OutputHeight = height,
                Usage = VideoUsage.PlaybackNormal,
            };

            enumerator = videoDevice.CreateVideoProcessorEnumerator(content);

            // 必须先确认驱动支持这两个方向的格式，否则 VideoProcessorBlt 会静默失败或直接报错。
            var inputSupport = enumerator.CheckVideoProcessorFormat(Format.B8G8R8A8_UNorm);
            if ((inputSupport & VideoProcessorFormatSupport.Input) == 0) return null;

            var outputSupport = enumerator.CheckVideoProcessorFormat(Format.NV12);
            if ((outputSupport & VideoProcessorFormatSupport.Output) == 0) return null;

            processor = videoDevice.CreateVideoProcessor(enumerator, 0);

            // 输入/输出纹理都要能作为视频处理视图绑定，因此用 Default 用途 + RenderTarget 绑定。
            inputTexture = CreateTexture(device, width, height, Format.B8G8R8A8_UNorm,
                BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None);

            outputTexture = CreateTexture(device, width, height, Format.NV12,
                BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None);

            // 读回需要 Staging 纹理（Default 用途的纹理不能 Map）。
            stagingTexture = CreateTexture(device, width, height, Format.NV12,
                BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read);

            inputView = videoDevice.CreateVideoProcessorInputView(
                inputTexture, enumerator,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });

            outputView = videoDevice.CreateVideoProcessorOutputView(
                outputTexture, enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                });

            var converter = new GpuFrameConverter(
                device, videoDevice, videoContext, processor,
                inputTexture, outputTexture, stagingTexture,
                inputView, outputView, width, height);

            // 所有权已经转移给 converter，避免 finally 里再释放一次。
            videoDevice = null; videoContext = null; enumerator = null; processor = null;
            inputTexture = null; outputTexture = null; stagingTexture = null;
            inputView = null; outputView = null;
            return converter;
        }
        catch
        {
            // GPU 路径是"尽力而为"：任何 COM 失败都当作不支持，交给 CPU 回退。
            return null;
        }
        finally
        {
            enumerator?.Dispose();
            processor?.Dispose();
            inputView?.Dispose();
            outputView?.Dispose();
            inputTexture?.Dispose();
            outputTexture?.Dispose();
            stagingTexture?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
        }
    }

    private static ID3D11Texture2D CreateTexture(
        ID3D11Device device, int width, int height, Format format,
        BindFlags bindFlags, ResourceUsage usage, CpuAccessFlags cpuAccess)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = usage,
            BindFlags = bindFlags,
            CPUAccessFlags = cpuAccess,
            MiscFlags = ResourceOptionFlags.None,
        };
        return device.CreateTexture2D(desc);
    }

    /// <summary>把 CPU 侧 BGRA 帧上传到 GPU 纹理再 Blt 成 NV12，主要用于自检。</summary>
    public bool TryConvert(BgraFrame frame, out Nv12Frame? result)
    {
        result = null;
        if (frame is null || frame.Width != _width || frame.Height != _height) return false;

        try
        {
            var context = _device.ImmediateContext;
            unsafe
            {
                fixed (byte* p = frame.Pixels)
                {
                    context.UpdateSubresource(
                        _inputTexture, 0, null, (IntPtr)p, frame.Stride, frame.Stride * _height);
                }
            }
            return BltAndReadback(out result);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>直接把捕获到的 BGRA 纹理 Blt 成 NV12（实时管线使用，避免一次 CPU 往返）。</summary>
    public bool TryConvert(ID3D11Texture2D sourceBgra, out Nv12Frame? result)
    {
        result = null;
        if (sourceBgra is null) return false;
        try
        {
            _device.ImmediateContext.CopyResource(_inputTexture, sourceBgra);
            return BltAndReadback(out result);
        }
        catch
        {
            return false;
        }
    }

    private bool BltAndReadback(out Nv12Frame? result)
    {
        result = null;
        try
        {
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, _streams);

            var context = _device.ImmediateContext;
            context.CopyResource(_stagingTexture, _outputTexture);

            var mapped = context.Map(_stagingTexture, 0, MapMode.Read, (Vortice.Direct3D11.MapFlags)0);
            try
            {
                int yPlaneSize = _width * _height;
                var buffer = new byte[yPlaneSize + yPlaneSize / 2];

                // NV12 是单个平面（Y 后紧跟交错 UV），D3D11 的 NV12 纹理 rowPitch 与逻辑行宽一致，
                // 但为了稳妥仍按 RowPitch 逐行拷贝。
                int lumaRows = _height;
                for (int y = 0; y < lumaRows; y++)
                {
                    var src = IntPtr.Add(mapped.DataPointer, y * mapped.RowPitch);
                    System.Runtime.InteropServices.Marshal.Copy(src, buffer, y * _width, _width);
                }

                int chromaOffset = yPlaneSize;
                int chromaRows = _height / 2;
                for (int y = 0; y < chromaRows; y++)
                {
                    var src = IntPtr.Add(mapped.DataPointer, (lumaRows + y) * mapped.RowPitch);
                    System.Runtime.InteropServices.Marshal.Copy(src, buffer, chromaOffset + y * _width, _width);
                }

                result = new Nv12Frame(_width, _height, buffer);
                return true;
            }
            finally
            {
                context.Unmap(_stagingTexture, 0);
            }
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _inputView.Dispose();
        _outputView.Dispose();
        _inputTexture.Dispose();
        _outputTexture.Dispose();
        _stagingTexture.Dispose();
        _processor.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
