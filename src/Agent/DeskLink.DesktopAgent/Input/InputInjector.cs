using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DeskLink.DesktopAgent.Input;

/// <summary>注入结果。UIPI 拒绝必须与一般失败区分开，因为 UI 提示完全不同。</summary>
public enum InjectionStatus
{
    Success = 0,

    /// <summary>SendInput 返回 0 且 GetLastError()==ERROR_ACCESS_DENIED：被 UIPI 拦下。</summary>
    DeniedByUipi = 1,

    /// <summary>其它失败。</summary>
    Failed = 2,
}

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    X1,
    X2,
}

/// <summary>
/// 鼠标键盘注入（SendInput P/Invoke）。
///
/// 两个硬性要求（来自 DESIGN）：
///   1. **`--no-inject` 模式**：所有注入调用变成"直接返回成功"的空操作。
///      这是自控自联调（agent 抓本机桌面、输入又回到本机）时防止输入环路的前提，
///      Service 启动 agent 时若启用注入分支而没传 `--no-inject` 会 hard-fail。
///      <see cref="InjectedCallCount"/> 在 no-inject 模式下**不增长**，
///      测试据此可以证明"确实没有发生注入"，而不是只看返回值。
///   2. **UIPI 拒绝要单独上报**：SendInput 返回 0 且 GetLastError()==ERROR_ACCESS_DENIED
///      时返回 <see cref="InjectionStatus.DeniedByUipi"/>，会话状态据此提示
///      "被控端需要重新登录以恢复控制"。
///
/// 键盘一律走 scancode（KEYEVENTF_SCANCODE），因为很多游戏/全屏应用只看 scancode；
/// 扩展键（如方向键、右 Ctrl）必须带 KEYEVENTF_EXTENDEDKEY，否则会被当成小键盘键。
/// </summary>
public sealed class InputInjector
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_XDOWN = 0x0080;
    private const uint MOUSEEVENTF_XUP = 0x0100;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;

    private const int ERROR_ACCESS_DENIED = 5;

    private const uint XBUTTON1 = 0x0001;
    private const uint XBUTTON2 = 0x0002;

    private readonly bool _noInject;

    public InputInjector(bool noInject)
    {
        _noInject = noInject;
    }

    /// <summary>true 表示所有注入调用都是空操作（仅用于自控自联调）。</summary>
    public bool NoInject => _noInject;

    /// <summary>真正调用过 SendInput 的次数（no-inject 模式下恒为 0）。</summary>
    public long InjectedCallCount { get; private set; }

    /// <summary>被 UIPI 拒绝的次数。</summary>
    public long DeniedCount { get; private set; }

    /// <summary>最近一次注入的结果。</summary>
    public InjectionStatus LastStatus { get; private set; } = InjectionStatus.Success;

    /// <summary>最近一次失败的原因（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 移动鼠标到虚拟桌面坐标 (x, y)。
    /// 使用 ABSOLUTE|VIRTUALDESK，坐标是虚拟桌面的绝对像素坐标（多显示器也能覆盖）。
    /// </summary>
    public InjectionStatus MoveMouse(int x, int y, int virtualDesktopWidth, int virtualDesktopHeight)
    {
        if (virtualDesktopWidth <= 0 || virtualDesktopHeight <= 0)
        {
            return Record(InjectionStatus.Failed, "invalid virtual desktop size");
        }

        // SendInput 的绝对坐标是 0..65535 的归一化值，按虚拟桌面尺寸换算。
        int nx = (int)Math.Round((double)x * 65535 / Math.Max(1, virtualDesktopWidth - 1));
        int ny = (int)Math.Round((double)y * 65535 / Math.Max(1, virtualDesktopHeight - 1));

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    mouseData = 0,
                    dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        return Send(input);
    }

    public InjectionStatus MouseButton(MouseButtonKind button, bool down)
    {
        uint flag = button switch
        {
            MouseButtonKind.Left => down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
            MouseButtonKind.Right => down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
            MouseButtonKind.Middle => down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
            MouseButtonKind.X1 => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            MouseButtonKind.X2 => down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP,
            _ => 0,
        };

        uint data = button switch
        {
            MouseButtonKind.X1 => XBUTTON1,
            MouseButtonKind.X2 => XBUTTON2,
            _ => 0,
        };

        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = data,
                    dwFlags = flag,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        return Send(input);
    }

    /// <summary>滚轮。delta 为正表示向上/向右，一格是 WHEEL_DELTA=120。</summary>
    public InjectionStatus Wheel(int delta, bool horizontal = false)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = unchecked((uint)delta),
                    dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        return Send(input);
    }

    /// <summary>按扫描码注入按键。</summary>
    public InjectionStatus KeyScan(ushort scanCode, bool down, bool extended)
    {
        uint flags = KEYEVENTF_SCANCODE;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        if (!down) flags |= KEYEVENTF_KEYUP;

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0, // scancode 模式下 wVk 必须为 0
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };
        return Send(input);
    }

    /// <summary>
    /// 按虚拟键码注入按键：内部用 MapVirtualKey 换成扫描码后再走 scancode 路径，
    /// 保证与 <see cref="KeyScan"/> 行为一致（而不是混用 VK 与 scancode 两套语义）。
    /// </summary>
    public InjectionStatus KeyVirtual(ushort virtualKey, bool down, bool extended)
    {
        uint scan = MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);
        if (scan == 0)
        {
            return Record(InjectionStatus.Failed, $"MapVirtualKey failed for VK 0x{virtualKey:X2}");
        }
        return KeyScan((ushort)scan, down, extended);
    }

    private const uint MAPVK_VK_TO_VSC = 0;

    private InjectionStatus Send(INPUT input)
    {
        if (_noInject)
        {
            // 关键：no-inject 模式下**不**调用 SendInput，也不增加 InjectedCallCount，
            // 否则测试无法区分"真注入成功"和"空操作假装成功"。
            return Record(InjectionStatus.Success, null);
        }

        var inputs = new[] { input };
        uint sent = SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        InjectedCallCount++;

        if (sent == 1)
        {
            return Record(InjectionStatus.Success, null);
        }

        int err = Marshal.GetLastWin32Error();
        if (err == ERROR_ACCESS_DENIED)
        {
            return Record(InjectionStatus.DeniedByUipi, "SendInput denied (UIPI)");
        }

        return Record(InjectionStatus.Failed, new Win32Exception(err).Message);
    }

    private InjectionStatus Record(InjectionStatus status, string? error)
    {
        LastStatus = status;
        LastError = error;
        if (status == InjectionStatus.DeniedByUipi) DeniedCount++;
        return status;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint numberOfInputs, [In] INPUT[] inputs, int sizeOfInputStructure);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    // 三个结构体共享同一块内存：用 Explicit + FieldOffset(0) 表达 C 里的 union。
    // 不要改成 Sequential 嵌套，那样每个成员会各占一段空间，INPUT 尺寸会翻倍。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}
