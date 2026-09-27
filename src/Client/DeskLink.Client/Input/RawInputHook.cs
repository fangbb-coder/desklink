using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskLink.Client.Services;

namespace DeskLink.Client.Input;

/// <summary>
/// 全屏 Raw Input 键盘/鼠标捕获（控制端 → 远端）。
///
/// 为什么用 Raw Input 而不是 WPF 的 KeyDown/MouseMove：
///   - WPF 路由事件经过输入系统处理，会吞掉系统键、丢失 scancode，且受键盘布局影响；
///   - Raw Input（WM_INPUT）直接给出**物理 scancode**，正好匹配远端 SendInput 的
///     KEYEVENTF_SCANCODE 注入（见 MediaChannelContract.InputEventCodec 的说明）。
///
/// ⚠️ 需要真机手工验证：本类无法在无桌面/无真实输入设备的测试宿主里验证。
/// 已验证的部分仅限"能编译、能附加/分离、字段布局按 Win32 头文件对齐"。
/// 未验证：真机 scancode 是否与远端注入一致、扩展键（E0）判定、DPI 缩放下坐标归一化。
///
/// 设计约束：
///   - 只在**本窗口为前台**时才转发（不注册 RIDEV_INPUTSINK），因此弹窗（模态对话框）
///     获得焦点时天然收不到输入，不会被误转发到远端。
///   - <see cref="Detach"/> 必须能安全解绑（退出全屏/断开会话时）。
/// </summary>
public sealed class RawInputHook : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;
    private const uint MAPVK_VK_TO_VSC_EX = 4;
    private const ushort RI_KEY_E0 = 0x02;
    private const ushort RI_KEY_BREAK = 0x01;

    // RAWMOUSE.usButtonFlags
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
    private const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
    private const ushort RI_MOUSE_WHEEL = 0x0400;

    private readonly MediaChannelClient _media;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _attached;
    private bool _disposed;

    public RawInputHook(MediaChannelClient media)
    {
        _media = media ?? throw new ArgumentNullException(nameof(media));
    }

    /// <summary>是否已附加。未附加时不消费任何输入。</summary>
    public bool IsAttached => _attached;

    /// <summary>附加到指定窗口，注册键盘与鼠标 Raw Input。</summary>
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_attached) return;

        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd);
        if (_source is null) throw new InvalidOperationException("无法获取窗口的 HwndSource");

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = 0, // 不设 INPUTSINK：仅当前台窗口时收输入，弹窗夺焦即自动停发
                hwndTarget = _hwnd,
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = 0,
                hwndTarget = _hwnd,
            },
        };

        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new InvalidOperationException($"RegisterRawInputDevices 失败：{Marshal.GetLastWin32Error()}");
        }

        _source.AddHook(WndProc);
        _attached = true;
    }

    /// <summary>解绑并注销 Raw Input。可重复调用。</summary>
    public void Detach()
    {
        if (!_attached) return;

        _source?.RemoveHook(WndProc);
        _source = null;

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            },
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_REMOVE,
                hwndTarget = IntPtr.Zero,
            },
        };
        RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        _attached = false;
        _hwnd = IntPtr.Zero;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_INPUT || !_attached) return IntPtr.Zero;

        // 双保险：即便消息到了，也只在"本窗口是前台"时才转发，
        // 避免对话框/其它窗口聚焦期间误把输入送到远端。
        if (GetForegroundWindow() != hwnd) return IntPtr.Zero;

        try
        {
            HandleRawInput(lParam);
        }
        catch
        {
            // 输入转发失败不能把 UI 线程打崩；静默丢弃这一条。
        }

        return IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr lParam)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0) return;
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            var payload = IntPtr.Add(buffer, (int)headerSize);

            if (header.dwType == 1) // RIM_TYPEKEYBOARD
            {
                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(payload);
                ForwardKey(kb);
            }
            else if (header.dwType == 0) // RIM_TYPEMOUSE
            {
                var mouse = Marshal.PtrToStructure<RAWMOUSE>(payload);
                ForwardMouse(mouse);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ForwardKey(RAWKEYBOARD kb)
    {
        // VKey 0xFF 是"假键"（Pause 等按下/抬起配对中的占位），跳过。
        if (kb.VKey == 0xFF) return;

        // 用 MapVirtualKey(VK, MAPVK_VK_TO_VSC_EX) 取 scancode：
        // 返回值的**高字节为 0xE0/0xE1 表示扩展键**，低字节才是真正的 scancode。
        uint mapped = MapVirtualKey(kb.VKey, MAPVK_VK_TO_VSC_EX);
        ushort scanCode = (ushort)(mapped & 0xFF);
        bool extended = (mapped & 0xFF00) == 0xE000 || (kb.Flags & RI_KEY_E0) != 0;

        // 注：也可以直接用 kb.MakeCode（物理 scancode）与 kb.Flags 的 E0 位，
        // 那更贴近硬件；这里按需求用 MapVirtualKey，并用 Flags 交叉校正扩展位。
        bool down = (kb.Flags & RI_KEY_BREAK) == 0;
        if (scanCode == 0) return;

        _media.SendKey(scanCode, extended, down);
    }

    private void ForwardMouse(RAWMOUSE mouse)
    {
        // 鼠标移动：用当前光标位置（相对窗口客户区）归一化为千分比，
        // 而不是用 raw 的相对增量——客户端不知道远端分辨率，
        // 归一化坐标可避免"客户端缩放显示时坐标错位"（见 InputEventCodec 说明）。
        if (mouse.lLastX != 0 || mouse.lLastY != 0)
        {
            if (TryGetNormalizedCursor(out int nx, out int ny))
            {
                _media.SendMouseMove(nx, ny);
            }
        }

        var flags = mouse.usButtonFlags;
        if (flags == 0) return;

        if ((flags & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) _media.SendMouseButton(0, true);
        if ((flags & RI_MOUSE_LEFT_BUTTON_UP) != 0) _media.SendMouseButton(0, false);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) _media.SendMouseButton(1, true);
        if ((flags & RI_MOUSE_RIGHT_BUTTON_UP) != 0) _media.SendMouseButton(1, false);
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) _media.SendMouseButton(2, true);
        if ((flags & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) _media.SendMouseButton(2, false);
        if ((flags & RI_MOUSE_WHEEL) != 0) _media.SendWheel((short)mouse.usButtonData);
    }

    private bool TryGetNormalizedCursor(out int xPermille, out int yPermille)
    {
        xPermille = 0;
        yPermille = 0;
        if (_hwnd == IntPtr.Zero) return false;

        if (!GetCursorPos(out var screen)) return false;
        if (!GetClientRect(_hwnd, out var client)) return false;

        var origin = new POINT { X = 0, Y = 0 };
        if (!ClientToScreen(_hwnd, ref origin)) return false;

        int w = client.Right - client.Left;
        int h = client.Bottom - client.Top;
        if (w <= 0 || h <= 0) return false;

        int localX = screen.X - origin.X;
        int localY = screen.Y - origin.Y;

        // 夹到 0..1000：光标可能在窗口外（比如点击边缘），越界值由 EncodeMouseMove 兜底。
        xPermille = Math.Clamp(localX * 1000 / w, 0, 1000);
        yPermille = Math.Clamp(localY * 1000 / h, 0, 1000);
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }

    // —— Win32 互操作 ——

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        // winuser.h 中是 ULONG（4 字节），声明成 ulong 会让托管结构比原生
        // 多出 4 字节，缓冲区末尾的 PtrToStructure 会越界读。
        public uint ExtraInformation;
    }

    // 布局与 winuser.h 的 RAWMOUSE 一致（合计 24 字节）：
    //   ULONG usFlags（4B，非 USHORT！）
    //   union { ULONG ulButtons; struct { USHORT usButtonFlags; USHORT usButtonData; } }（4B）
    //   ULONG ulRawButtons / LONG lLastX / LONG lLastY / ULONG ulExtraInformation
    // 旧声明把 usFlags 写成 ushort 且漏掉 union 对齐，导致 usButtonFlags 实际
    // 读到的是 padding（恒 0），所有按键/滚轮事件被静默丢弃。
    [StructLayout(LayoutKind.Sequential)]
    private struct RAWMOUSE
    {
        public uint usFlags;
        public ushort usButtonFlags;
        public ushort usButtonData;
        public uint ulRawButtons;
        public int lLastX;
        public int lLastY;
        public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
}
