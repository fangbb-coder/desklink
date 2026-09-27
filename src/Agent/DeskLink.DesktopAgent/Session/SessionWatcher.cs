using System.Runtime.InteropServices;

namespace DeskLink.DesktopAgent.Session;

/// <summary>
/// 当前活动会话状态快照。
/// <paramref name="IsLocked"/> 为 null 表示"无法判定"，绝不要把它当成 false。
/// </summary>
public readonly record struct SessionState(
    bool HasConsoleSession,
    int ConsoleSessionId,
    string ConnectState,
    bool? IsLocked,
    string? UserName,
    string Summary);

/// <summary>
/// WTS 会话状态检测。
///
/// 目的（DESIGN 错误处理条目）：用户锁屏或尚未登录时，agent 必须报告
/// "等待本地登录"，而**不是**去尝试捕获 Windows 登录界面 / 默认隔离的 Secure Desktop
/// （规格明确把登录界面捕获列为不提供的功能）。
///
/// 实现要点与坑：
///   - 连接状态用 WTSQuerySessionInformation(WTSConnectState)，这是最可靠的"是否已登录"信号。
///   - **锁屏状态**没有独立的 WTS_INFO_CLASS，必须取 WTSSessionInfoEx（=25），
///     读 WTSINFOEX_LEVEL1_W 里的 SessionFlags：
///       0x00000000 = LOCK，0x00000001 = UNLOCK，0xFFFFFFFF = UNKNOWN。
///     SessionFlags 在缓冲区里的偏移是 12（Level 4 字节 + SessionId 4 字节 + SessionState 4 字节）。
///     这里用显式布局结构体而不是硬编码偏移，并且先校验 Level==1 再采信 SessionFlags，
///     避免不同 Windows 版本结构变化时读出垃圾值。
///   - 任何 P/Invoke 失败都返回"未知"，不抛异常：锁屏检测失败不应让 agent 起不来。
/// </summary>
public sealed class SessionWatcher
{
    private const int WTS_CURRENT_SERVER_HANDLE = 0;
    private const int WTSUserName = 5;
    private const int WTSConnectState = 8;
    private const int WTSSessionInfoEx = 25;

    private const uint WTS_SESSIONSTATE_LOCK = 0x00000000;
    private const uint WTS_SESSIONSTATE_UNLOCK = 0x00000001;

    /// <summary>无活动控制台会话时 WTSGetActiveConsoleSessionId 的返回值。</summary>
    public const int NoConsoleSession = -1; // 0xFFFFFFFF

    /// <summary>取当前控制台会话的状态。本方法不会抛异常。</summary>
    public SessionState GetState()
    {
        uint sessionId;
        try
        {
            sessionId = WTSGetActiveConsoleSessionId();
        }
        catch
        {
            return new SessionState(false, NoConsoleSession, "unknown", null, null, "WTS 不可用");
        }

        if (sessionId == 0xFFFFFFFF)
        {
            return new SessionState(false, NoConsoleSession, "none", null, null, "无活动控制台会话（等待本地登录）");
        }

        string connectState = QueryConnectState(sessionId);
        bool? locked = QueryLocked(sessionId);
        string? user = QueryString(sessionId, WTSUserName);

        string summary;
        if (connectState is "disconnected" or "idle" or "down" or "init")
        {
            summary = "等待本地登录";
        }
        else if (locked == true)
        {
            summary = "会话已锁定（等待本地解锁）";
        }
        else if (locked is null)
        {
            summary = "会话可用（锁屏状态未知）";
        }
        else
        {
            summary = "会话可用";
        }

        return new SessionState(true, (int)sessionId, connectState, locked, user, summary);
    }

    /// <summary>
    /// 是否允许开始捕获。锁屏或未登录时返回 false —— 此时不应尝试抓 Secure Desktop。
    /// </summary>
    public bool CanCaptureDesktop(out string reason)
    {
        var state = GetState();
        if (!state.HasConsoleSession)
        {
            reason = "等待本地登录";
            return false;
        }

        if (state.ConnectState is "disconnected" or "idle" or "down" or "init" or "listen")
        {
            reason = "等待本地登录";
            return false;
        }

        if (state.IsLocked == true)
        {
            reason = "会话已锁定（等待本地解锁）";
            return false;
        }

        reason = "";
        return true;
    }

    private static string QueryConnectState(uint sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSConnectState, out buffer, out uint bytes) ||
                buffer == IntPtr.Zero || bytes < 4)
            {
                return "unknown";
            }

            int state = Marshal.ReadInt32(buffer);
            return state switch
            {
                0 => "active",
                1 => "connected",
                2 => "connect-query",
                3 => "shadow",
                4 => "disconnected",
                5 => "idle",
                6 => "listen",
                7 => "reset",
                8 => "down",
                9 => "init",
                _ => "unknown",
            };
        }
        catch
        {
            return "unknown";
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static bool? QueryLocked(uint sessionId)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTSSessionInfoEx, out buffer, out uint bytes) ||
                buffer == IntPtr.Zero || bytes < 16)
            {
                return null;
            }

            var head = Marshal.PtrToStructure<WtsInfoExHead>(buffer);
            if (head.Level != 1)
            {
                // 不是 LEVEL_1 布局，SessionFlags 的位置不可信。
                return null;
            }

            uint flags = head.SessionFlags;
            return flags switch
            {
                WTS_SESSIONSTATE_LOCK => true,
                WTS_SESSIONSTATE_UNLOCK => false,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    private static string? QueryString(uint sessionId, int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out uint bytes) ||
                buffer == IntPtr.Zero || bytes == 0)
            {
                return null;
            }
            var s = Marshal.PtrToStringUni(buffer);
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// WTSINFOEX 头部（Level + WTSINFOEX_LEVEL1_W 的前三个 ULONG）。
    /// Size 只覆盖到 SessionFlags，避免为一个字段声明整个巨型结构体。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct WtsInfoExHead
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(4)] public uint SessionId;
        [FieldOffset(8)] public int SessionState;
        [FieldOffset(12)] public uint SessionFlags;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, int wtsInfoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
