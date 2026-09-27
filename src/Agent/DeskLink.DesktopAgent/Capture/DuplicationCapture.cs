using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskLink.DesktopAgent.Capture;

/// <summary>一块可用于捕获的显示器的描述（状态 JSON 与 `--list-monitors` 使用）。</summary>
/// <param name="RotationDegrees">
/// 该显示器的旋转角度（0/90/180/270，来自 DXGI 的 <c>ModeRotation</c>）。
///
/// 为什么必须上报它：DXGI Desktop Duplication 抓到的是**物理扫描方向**的帧；
/// 纵向显示器（90/270）若不旋转，远端会看到横躺的画面，而且编码器尺寸也要跟着
/// 交换宽高，否则 MFT 会因输入尺寸与声明不符而拒绝。
/// 默认 0 是为了让不关心旋转的调用点（如测试）不必传该参数。
/// </param>
public sealed record MonitorInfo(
    int Index,
    string AdapterName,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool IsPrimary,
    int RotationDegrees = 0);

/// <summary>DXGI <c>ModeRotation</c> → 角度。无法识别时按 0（不旋转）处理。</summary>
internal static class RotationMapping
{
    /// <summary>
    /// DXGI 枚举值：Unspecified=0, Identity=1, Rotate90=2, Rotate180=3, Rotate270=4。
    /// 用整数值判断而不是引用 Vortice 的枚举类型，避免这里与 Vortice 版本耦合。
    /// </summary>
    public static int ToDegrees(int modeRotation) => modeRotation switch
    {
        2 => 90,
        3 => 180,
        4 => 270,
        _ => 0, // Unspecified / Identity / 未知
    };
}

/// <summary>
/// DXGI Desktop Duplication 抓屏。
///
/// 关键设计：
///   - 只抓一块显示器（v1 规格：多显示器只支持选择单个），默认第一块。
///   - 抓到的 GPU 纹理通过一张 Staging 纹理拷回 CPU（`CopyResource` + `Map`），
///     因为编码输入是 CPU 侧 NV12 缓冲区。DESIGN 里说"NV12 90MB/s 绝不过管道"，
///     这里的一次 CPU 往返只发生在编码之前，不会进管道。
///   - <c>DXGI_ERROR_ACCESS_LOST</c> **不是错误**：它意味着模式切换 / DPI 变化 /
///     驱动重置，必须整管线重建。这里只把信号交给调用方（<see cref="IFrameSource.TryGetFrame"/>
///     的 accessLost 输出），绝不抛异常，也不自行重建——重建由
///     <see cref="AccessLostRecovery"/> 统一负责，避免"捕获层偷偷自愈"导致状态条不同步。
/// </summary>
public sealed class DuplicationCapture : IFrameSource
{
    // 0x887A0026 = DXGI_ERROR_ACCESS_LOST；0x887A0027 = DXGI_ERROR_WAIT_TIMEOUT。
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

    private IDXGIFactory1? _factory;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput1? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    private DuplicationCapture(int width, int height, string deviceName, int monitorIndex)
    {
        Width = width;
        Height = height;
        DeviceName = deviceName;
        MonitorIndex = monitorIndex;
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DeviceName { get; }
    public int MonitorIndex { get; }
    public string Description => $"dxgi-duplication monitor={MonitorIndex} ({DeviceName}) {Width}x{Height}";

    /// <summary>当前使用的 D3D11 设备；供 GPU 转换器复用，避免重复建设备。</summary>
    public ID3D11Device? Device => _device;

    /// <summary>
    /// 枚举当前可捕获的显示器。只读操作，不需要提权，也不会独占桌面。
    /// </summary>
    public static IReadOnlyList<MonitorInfo> ListMonitors()
    {
        var list = new List<MonitorInfo>();
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int index = 0;
            for (int a = 0; ; a++)
            {
                IDXGIAdapter1 adapter;
                var adapterResult = factory.EnumAdapters1(a, out adapter);
                if (adapterResult.Failure || adapter is null) break;

                using (adapter)
                {
                    string adapterName;
                    try { adapterName = adapter.Description1.Description ?? ""; }
                    catch { adapterName = ""; }

                    for (int o = 0; ; o++)
                    {
                        IDXGIOutput output;
                        var outputResult = adapter.EnumOutputs(o, out output);
                        if (outputResult.Failure || output is null) break;

                        using (output)
                        {
                            var desc = output.Description;
                            var rect = desc.DesktopCoordinates;
                            // 旋转来自 ModeRotation；取不到时按 0 处理（不旋转）而不是抛异常——
                            // 枚举显示器是只读诊断操作，不该因为一个可选属性失败就整体失败。
                            int rotationDegrees = 0;
                            try { rotationDegrees = RotationMapping.ToDegrees((int)desc.Rotation); }
                            catch { rotationDegrees = 0; }

                            list.Add(new MonitorInfo(
                                Index: index,
                                AdapterName: adapterName,
                                DeviceName: desc.DeviceName ?? "",
                                Left: rect.Left,
                                Top: rect.Top,
                                Width: rect.Right - rect.Left,
                                Height: rect.Bottom - rect.Top,
                                IsPrimary: index == 0,
                                RotationDegrees: rotationDegrees));
                            index++;
                        }
                    }
                }
            }
        }
        catch
        {
            // 枚举失败（例如无桌面会话）时返回空列表，由调用方决定如何报告。
        }

        return list;
    }

    /// <summary>
    /// 尝试在指定显示器上建立捕获。失败返回 null，并通过 <paramref name="error"/> 给出原因。
    /// 常见失败原因：无交互桌面（服务会话 / 已断开的 RDP）、驱动不支持 Desktop Duplication。
    /// </summary>
    public static DuplicationCapture? TryCreate(int monitorIndex, out string? error)
    {
        error = null;
        IDXGIFactory1? factory = null;
        IDXGIAdapter1? adapter = null;
        IDXGIOutput1? output1 = null;
        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        IDXGIOutputDuplication? duplication = null;

        try
        {
            factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

            int remaining = monitorIndex;
            IDXGIOutput? targetOutput = null;
            string deviceName = "";
            int outWidth = 0, outHeight = 0;

            for (int a = 0; targetOutput is null; a++)
            {
                var adapterResult = factory.EnumAdapters1(a, out adapter);
                if (adapterResult.Failure || adapter is null)
                {
                    error = $"monitor index {monitorIndex} out of range";
                    return null;
                }

                for (int o = 0; ; o++)
                {
                    IDXGIOutput output;
                    var outputResult = adapter.EnumOutputs(o, out output);
                    if (outputResult.Failure || output is null) break;

                    if (remaining == 0)
                    {
                        targetOutput = output;
                        break;
                    }
                    remaining--;
                    output.Dispose();
                }

                if (targetOutput is null)
                {
                    adapter.Dispose();
                    adapter = null;
                }
            }

            var outputDesc = targetOutput!.Description;
            deviceName = outputDesc.DeviceName ?? "";
            var rect = outputDesc.DesktopCoordinates;
            outWidth = rect.Right - rect.Left;
            outHeight = rect.Bottom - rect.Top;

            var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };

            // 【互操作大坑】（本机实测，务必保留注释）：
            //   D3D11CreateDevice 一旦传入**非空 adapter**，driverType 就必须是
            //   DriverType.Unknown；传 DriverType.Hardware 会直接返回 E_INVALIDARG
            //   (0x80070057)，而不是"用这块适配器建硬件设备"。
            //   实测（Intel Iris Xe / Win11 26200）：
            //     adapter + Hardware → 0x80070057
            //     adapter + Unknown  → 0x00000000 成功
            //     null    + Hardware → 0x00000000 成功
            //   这是微软文档 "If pAdapter is non-NULL, DriverType must be
            //   D3D_DRIVER_TYPE_UNKNOWN" 的硬性要求。
            //   保留一条降级链：显式适配器 → 默认硬件适配器 → WARP。
            //   （WARP 不能做 Desktop Duplication，但让它失败在 DuplicateOutput
            //     而不是在这里，错误信息对排查更有价值。）
            var createResult = D3D11.D3D11CreateDevice(
                adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                levels, out device, out context);

            if (createResult.Failure || device is null || context is null)
            {
                // 降级 1：不指定适配器，用系统默认硬件适配器。
                createResult = D3D11.D3D11CreateDevice(
                    null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    levels, out device, out context);
            }

            if (createResult.Failure || device is null || context is null)
            {
                // 降级 2：WARP 软件光栅器（仅用于给出更明确的错误，无法真正抓屏）。
                createResult = D3D11.D3D11CreateDevice(
                    null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                    levels, out device, out context);
            }

            if (createResult.Failure || device is null || context is null)
            {
                error = $"D3D11CreateDevice failed: 0x{createResult.Code:X8}";
                return null;
            }

            output1 = targetOutput.QueryInterface<IDXGIOutput1>();
            targetOutput.Dispose();
            targetOutput = null;

            // DuplicateOutput 会独占该输出；同一输出被两个进程同时复制会失败（DXGI_ERROR_NOT_CURRENTLY_AVAILABLE）。
            duplication = output1.DuplicateOutput(device);

            var capture = new DuplicationCapture(outWidth, outHeight, deviceName, monitorIndex)
            {
                _factory = factory,
                _adapter = adapter,
                _output = output1,
                _device = device,
                _context = context,
                _duplication = duplication,
            };

            // 所有权转移成功，阻止 finally 释放。
            factory = null; adapter = null; output1 = null; device = null; context = null; duplication = null;
            return capture;
        }
        catch (SharpGenException ex)
        {
            error = $"dxgi init failed: 0x{ex.ResultCode.Code:X8} {ex.ResultCode.Description}";
            return null;
        }
        catch (Exception ex)
        {
            error = $"dxgi init failed: {ex.GetType().Name}: {ex.Message}";
            return null;
        }
        finally
        {
            duplication?.Dispose();
            output1?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
            context?.Dispose();
            device?.Dispose();
        }
    }

    public bool TryGetFrame(int timeoutMs, out BgraFrame? frame, out bool accessLost, out string? error)
    {
        frame = null;
        accessLost = false;
        error = null;

        if (_disposed || _duplication is null || _device is null || _context is null)
        {
            error = "capture not initialized";
            return false;
        }

        IDXGIResource? resource = null;
        bool frameAcquired = false;
        try
        {
            Result acquire;
            try
            {
                acquire = _duplication.AcquireNextFrame(timeoutMs, out _, out resource);
            }
            catch (SharpGenException ex)
            {
                acquire = ex.ResultCode;
            }

            if (acquire.Code == DxgiErrorWaitTimeout)
            {
                // 桌面没有变化 —— 不是错误，交给上层按帧率决定是否重复上一帧。
                return false;
            }

            if (acquire.Code == DxgiErrorAccessLost)
            {
                accessLost = true;
                error = "DXGI_ERROR_ACCESS_LOST";
                return false;
            }

            if (acquire.Failure || resource is null)
            {
                error = $"AcquireNextFrame failed: 0x{acquire.Code:X8}";
                return false;
            }

            frameAcquired = true;

            using var texture = resource.QueryInterface<ID3D11Texture2D>();

            // 第一次拿到纹理时才知道真实尺寸（模式可能刚切换过），据此建/重建 Staging 纹理。
            var texDesc = texture.Description;
            EnsureStaging(texDesc);

            _context.CopyResource(_staging, texture);

            var mapped = _context.Map(_staging!, 0, MapMode.Read, (Vortice.Direct3D11.MapFlags)0);
            try
            {
                int rowBytes = Width * 4;
                int stride = Width * 4;
                var pixels = new byte[stride * Height];
                for (int y = 0; y < Height; y++)
                {
                    var src = IntPtr.Add(mapped.DataPointer, y * mapped.RowPitch);
                    Marshal.Copy(src, pixels, y * stride, rowBytes);
                }
                frame = new BgraFrame(Width, Height, stride, pixels, Environment.TickCount64);
                return true;
            }
            finally
            {
                _context.Unmap(_staging!, 0);
            }
        }
        catch (SharpGenException ex)
        {
            if (ex.ResultCode.Code == DxgiErrorAccessLost)
            {
                accessLost = true;
                error = "DXGI_ERROR_ACCESS_LOST";
                return false;
            }
            error = $"capture failed: 0x{ex.ResultCode.Code:X8}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"capture failed: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            resource?.Dispose();
            if (frameAcquired)
            {
                // 必须成对调用，否则下次 AcquireNextFrame 会一直返回 ACCESS_LOST。
                try { _duplication.ReleaseFrame(); } catch { /* 已失效，忽略 */ }
            }
        }
    }

    private void EnsureStaging(Texture2DDescription acquired)
    {
        if (Width != acquired.Width || Height != acquired.Height)
        {
            Width = acquired.Width;
            Height = acquired.Height;
            _staging?.Dispose();
            _staging = null;
        }

        if (_staging is not null) return;

        var desc = new Texture2DDescription
        {
            Width = Width,
            Height = Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = acquired.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };
        _staging = _device!.CreateTexture2D(desc);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _staging?.Dispose();
        _duplication?.Dispose();
        _output?.Dispose();
        _adapter?.Dispose();
        _factory?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
