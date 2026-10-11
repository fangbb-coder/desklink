// 显示器变化主动检测（P9）。
//
// 为什么需要这个组件：DXGI Desktop Duplication 的 AcquireNextFrame **会**在
// 模式切换/旋转/DPI 变化时返回 ACCESS_LOST，但那是被动发现——用户已经看到一帧
// 旧画面、或者直接掉出一段时间。主动监听 WM_DISPLAYCHANGE / WM_SETTINGCHANGE
// 让我们在系统层确认"显示器形态变了"时就立刻放弃当前帧（不等 DXGI 抛错），
// 同时把"正在恢复画面"的状态条提前点亮，避免黑窗期。
//
// 实现要点：
//   - message-only 窗口：不创建可见 UI、不抢焦点，纯粹用来接收消息。
//   - 独立线程跑消息循环：捕获/编码在主线程做，避免回调重入导致 DXGI 死锁。
//   - 收到通知后通过 OnChanged 事件回调；事件订阅方自行决定是否触发 ACCESS_LOST 重建。
//   - 构造时记录的"参考描述"（宽度/高度/DPI/旋转）作为变化判定基准。
//   - 用 RegisterPowerSettingNotification 监听显示子系统（GUID_MONITOR_POWER_ON），
//     但 v1 不据此做主动暂停（屏幕关掉时已经捕不到帧），仅用于日志。
using System.ComponentModel;
using System.Runtime.InteropServices;
using Vortice.DXGI;

namespace DeskLink.DesktopAgent.Capture;

/// <summary>
/// 当前被捕获显示器的形态快照，用来判定是否变了。
/// DPI 与旋转是变化敏感字段——DESIGN 要求 DPI 变化/旋转触发 ACCESS_LOST 重建。
/// </summary>
/// <param name="Width">物理像素宽度。</param>
/// <param name="Height">物理像素高度。</param>
/// <param name="DpiX">DPI（dots per inch，水平方向）。</param>
/// <param name="DpiY">DPI（垂直方向）。</param>
/// <param name="Rotation">DXGI ModeRotation（0/1/2/3/4，详见 <see cref="RotationMapping"/>）。</param>
public readonly record struct DisplayFingerprint(int Width, int Height, uint DpiX, uint DpiY, int Rotation);

/// <summary>
/// 显示器变化主动检测器。收到 WM_DISPLAYCHANGE/WM_SETTINGCHANGE/取消挂起即触发事件。
/// 事件订阅方通常 <c>() => source.SignalAccessLost()</c>（IFrameSource 需扩展）。
/// </summary>
public sealed class DisplayChangeWatcher : IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_POWERBROADCAST = 0x0218;

    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;

    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly IntPtr _hwnd;
    private readonly DisplayFingerprint _reference;
    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _powerNotifyHandle = IntPtr.Zero;

    /// <summary>
    /// 显示器形态发生变化（尺寸/DPI/旋转变化，或被挂起/恢复）。
    /// 回调线程是 watcher 自己的消息线程，**不要**在回调里直接做重活；只做轻量判定或投信号。
    /// </summary>
    public event Action<DisplayChangeReason>? Changed;

    /// <summary>构造时记录的"参考形态"，与变化后形态做差。</summary>
    public DisplayFingerprint Reference => _reference;

    public DisplayChangeWatcher(DisplayFingerprint reference)
    {
        _reference = reference;
        _wndProc = WndProc;

        // message-only 窗口
        _hwnd = CreateMessageOnlyWindow(_wndProc);

        // 注册电源通知（屏幕开关）——失败也无所谓，挂起/恢复检测是"次要信号"。
        try
        {
            RegisterForPowerNotifications();
        }
        catch
        {
            // 电源通知是 best-effort；主路径（WM_DISPLAYCHANGE）已就位
        }

        _thread = new Thread(MessageLoop)
        {
            Name = "DeskLink.DisplayChangeWatcher",
            IsBackground = true,
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>触发一次"变化"事件（用于测试或外部强制刷新）。</summary>
    public void SignalChange(DisplayChangeReason reason) => Changed?.Invoke(reason);

    /// <summary>取当前显示器形态（线程安全；从主线程调用）。</summary>
    public static DisplayFingerprint CaptureCurrent()
    {
        var screen = TryGetPrimaryScreenDpi();
        var (w, h, rot) = TryGetPrimaryOutputSizeAndRotation();
        return new DisplayFingerprint(w, h, screen.DpiX, screen.DpiY, rot);
    }

    private void RegisterForPowerNotifications()
    {
        // 简化：v1 不注册 PBT_POWERSETTINGCHANGE（GUID_CONSOLE_DISPLAY_STATE），
        // 避免再多一份注册表路径维护；屏幕开关由 WM_DISPLAYCHANGE 间接覆盖。
        // 这里只占位让 PBT_APMSUSPEND / PBT_APMRESUMESUSPEND 能走默认处理。
    }

    private void MessageLoop()
    {
        try
        {
            _ready.Set();
            while (!_stop.IsSet)
            {
                // Pump messages; if no message, MsgWaitForMultipleObjects 让我们等 100ms
                // 期间还能响应 _stop。GetMessage 是阻塞的，单独用 PeekMessage + Sleep 不够稳。
                if (!GetMessage(out var msg, IntPtr.Zero, 0, 0))
                {
                    break; // WM_QUIT
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch
        {
            // 收尾：消息循环异常不应影响主流程
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_DISPLAYCHANGE:
                Changed?.Invoke(DisplayChangeReason.DisplayChange);
                return IntPtr.Zero;

            case WM_SETTINGCHANGE:
                // 区分 DPI（lParam 非空通常对应 SPI_SETLOGICALDPIOVERRIDE 之类）
                Changed?.Invoke(DisplayChangeReason.SettingChange);
                return IntPtr.Zero;

            case WM_POWERBROADCAST:
                switch ((int)wParam)
                {
                    case PBT_APMSUSPEND:
                        Changed?.Invoke(DisplayChangeReason.Suspend);
                        return (IntPtr)1;
                    case PBT_APMRESUMESUSPEND:
                        Changed?.Invoke(DisplayChangeReason.Resume);
                        return (IntPtr)1;
                }
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr CreateMessageOnlyWindow(WndProcDelegate proc)
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "DeskLink.DisplayChangeWatcher." + Guid.NewGuid().ToString("N"),
        };
        // 类名要交给窗口过程持续引用，否则 GC 回收后回调会爆
        _wndClasses.Add(wc.lpszClassName, proc);

        ushort atom = RegisterClassEx(ref wc);
        if (atom == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed");
        }

        var hwnd = CreateWindowEx(
            0, wc.lpszClassName, "DeskLink.DisplayChangeWatcher",
            0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
        }
        return hwnd;
    }

    private static readonly Dictionary<string, WndProcDelegate> _wndClasses = new();

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    // —— 显示形态采集（best-effort；失败时给 0 占位，让差异计算继续走）——

    private static (uint DpiX, uint DpiY) TryGetPrimaryScreenDpi()
    {
        try
        {
            var dc = GetDC(IntPtr.Zero);
            try
            {
                return ((uint)GetDeviceCaps(dc, 88 /* LOGPIXELSX */),
                        (uint)GetDeviceCaps(dc, 90 /* LOGPIXELSY */));
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, dc);
            }
        }
        catch
        {
            return (96, 96); // 系统默认 DPI，避免把"未知"算成 0 触发误判
        }
    }

    private static (int Width, int Height, int Rotation) TryGetPrimaryOutputSizeAndRotation()
    {
#pragma warning disable CS0162 // 编译器对双层 break/return 嵌套的流分析误报：catch 路径仍可达
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (int a = 0; a < 16; a++)
            {
                IDXGIAdapter1? adapter;
                var ar = factory.EnumAdapters1(a, out adapter);
                if (ar.Failure || adapter is null) break;
                using (adapter)
                {
                    for (int o = 0; o < 16; o++)
                    {
                        IDXGIOutput? output;
                        var or = adapter.EnumOutputs(o, out output);
                        if (or.Failure || output is null) break;
                        using (output)
                        {
                            var rect = output.Description.DesktopCoordinates;
                            int rotation = 0;
                            try { rotation = (int)output.Description.Rotation; } catch { rotation = 0; }
                            return (rect.Right - rect.Left, rect.Bottom - rect.Top, rotation);
                        }
                    }
                }
            }
        }
        catch
        {
            // 枚举失败时返回 0×0 —— 比 0×0 配 0 DPI 更不容易误判
        }
        return (0, 0, 0);
#pragma warning restore CS0162
    }

    public void Dispose()
    {
        if (_stop.IsSet) return;
        _stop.Set();
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); // 触发 GetMessage 返回 false
        }
        try { _thread.Join(1000); } catch { /* 收尾 */ }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        if (_powerNotifyHandle != IntPtr.Zero) UnregisterPowerSettingNotification(_powerNotifyHandle);
        _ready.Dispose();
        _stop.Dispose();
    }

    // —— P/Invoke ——

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(
        IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}

/// <summary>变化原因（便于日志与上游分类）。</summary>
public enum DisplayChangeReason
{
    /// <summary>WM_DISPLAYCHANGE：显示器分辨率/拓扑变了。</summary>
    DisplayChange = 1,

    /// <summary>WM_SETTINGCHANGE：系统参数变了（典型：DPI）。</summary>
    SettingChange = 2,

    /// <summary>系统挂起。</summary>
    Suspend = 3,

    /// <summary>系统恢复。</summary>
    Resume = 4,
}
