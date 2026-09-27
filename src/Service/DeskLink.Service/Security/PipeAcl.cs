// 命名管道 ACL（Service 侧）。
//
// 为什么需要单独一个模块（这是一个真实缺陷的修复）：
//   PipeServer 原本只授予 SYSTEM / Administrators / CreatorOwner / 当前进程用户。
//   在**真实服务模式**下，Service 以 LocalSystem 运行：
//     - CreatorOwner = LocalSystem；
//     - Administrators 在**非提权**进程的过滤令牌里是 deny-only，不授予访问；
//   → 结果是**同机的普通（非提权）交互用户根本连不上管道**。
//   开发时用 `--console` 跑（Service 与客户端同一用户）看不出问题，
//   一装成服务就"控制端连不上服务"，且现象是超时，很难定位。
//
// 修法：显式把**当前控制台登录用户**的 SID 加进 ACL（这是唯一需要连进来的身份）。
//   解析不到控制台用户时（无人登录 / WTS 不可用 / 域账号翻译失败）退回
//   Authenticated Users，并**明确记录警告** —— 宁可可用但要留痕，
//   也不要静默地让控制端连不上。
//
// 安全边界说明（写进 KnownIssues）：
//   多用户机器上，若退回 Authenticated Users，则同机其它用户也能连上本管道。
//   缓解方式是把服务改成以特定用户运行，或按需收紧 ACL。
//   自用单用户机器上，授予控制台用户是最小必要授权。
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeskLink.Service.Security;

/// <summary>命名管道 ACL 构造。</summary>
public static class PipeAcl
{
    // WTS 信息类
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, int wtsInfoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr p);

    /// <summary>
    /// 取当前控制台登录用户的 SID。取不到返回 false（调用方应退回更宽的授权并记录警告）。
    /// </summary>
    public static bool TryGetConsoleUserSid(out SecurityIdentifier? sid)
    {
        sid = null;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            // 0xFFFFFFFF 表示"当前没有控制台会话"（无人登录 / 只有 RDP 会话）。
            if (sessionId == 0xFFFFFFFF) return false;

            var user = QueryString(sessionId, WtsUserName);
            if (string.IsNullOrEmpty(user)) return false;
            var domain = QueryString(sessionId, WtsDomainName) ?? "";

            var account = string.IsNullOrEmpty(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            sid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return true;
        }
        catch
        {
            // 翻译失败（域控制器不可达 / 账号已删除等）：交给调用方退回。
            return false;
        }
    }

    private static string? QueryString(uint sessionId, int infoClass)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out buffer, out _)) return null;
            return buffer == IntPtr.Zero ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// 计算应授予的 SID 列表（纯函数，便于单测断言"控制台用户被包含/退回被使用"）。
    /// </summary>
    /// <param name="consoleUser">控制台用户 SID；null 表示解析失败。</param>
    public static IReadOnlyList<SecurityIdentifier> BuildAllowedSids(SecurityIdentifier? consoleUser)
    {
        var list = new List<SecurityIdentifier>
        {
            // 服务本体需要访问（自己创建的管道）。
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            // 管理员（提权后可运维/排障）。
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null),
        };

        if (consoleUser is not null)
        {
            list.Add(consoleUser);
        }
        else
        {
            // 退回：任何已认证的本机用户（命名管道本身只在本机可连）。
            list.Add(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null));
        }

        return list;
    }

    /// <summary>
    /// 构造管道安全描述符。所有身份都只给 ReadWrite + CreateNewInstance，
    /// 不给 FullControl（改 ACL/删管道不是控制端该有的权限）。
    /// </summary>
    public static PipeSecurity Build(Action<string>? log = null)
    {
        var hasConsoleUser = TryGetConsoleUserSid(out var consoleUser);
        if (!hasConsoleUser)
        {
            log?.Invoke(
                "PipeAcl: 无法解析控制台登录用户（无人登录或 WTS 不可用），" +
                "退回授予 Authenticated Users。多用户机器上请注意：同机其它用户也能连上本管道。");
        }
        else
        {
            log?.Invoke($"PipeAcl: 授予控制台用户 {consoleUser!.Value} 访问权限");
        }

        var ps = new PipeSecurity();
        foreach (var sid in BuildAllowedSids(consoleUser))
        {
            ps.AddAccessRule(new PipeAccessRule(
                sid,
                PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                AccessControlType.Allow));
        }
        return ps;
    }
}
