// DeskLink.Panel —— UAC 提权
//
// 放行入站防火墙规则必须管理员。GUI 里让用户自己"右键以管理员运行"是很差的体验，
// 这里的做法是：需要提权时用 runas 动词重启自己，然后原进程退出。
// 提权前把设置写盘，重启后自然读回来——所以状态不会丢。
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace DeskLink.Panel.Services;

public static class Elevation
{
    /// <summary>用 `runas --elevated` 标记参数重启自身（要求管理员）。成功发起后返回 true，调用方应退出。</summary>
    public const string ElevatedMarker = "--elevated";

    public static bool IsAdministrator()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>是否已经是被提权重启起来的（避免无限重启）。</summary>
    public static bool HasElevatedMarker(IReadOnlyList<string> args) =>
        args.Any(a => string.Equals(a, ElevatedMarker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 以管理员身份重启自身。返回 true 表示"已发起重启，调用方应该退出"；
    /// 返回 false 表示提权失败（用户点了取消或策略拒绝），调用方应继续以普通权限运行。
    /// </summary>
    public static bool TryRestartElevated(string exePath, IReadOnlyList<string> extraArgs)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,          // runas 动词必须走 ShellExecute
            Verb = "runas",                   // → UAC 提权提示
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory,
        };
        foreach (var a in extraArgs) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            return proc is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223 = 用户取消了 UAC 对话框。
            return false;
        }
    }
}
