// FirewallHelper：`netsh advfirewall` 入站规则管理（P5.6 完整实现）。
//
// 职责（DESIGN.md 部署章节 / 风险回顾 #8）：
//   - 为局域网直连端口放行/回收入站规则（TCP + UDP 各一条）。
//   - 规则命名固定为 DeskLink-Direct-TCP / DeskLink-Direct-UDP（端口写在规则内容里，
//     因此端口变更 = 删旧规则 + 加新规则）。
//   - 用注册表 HKLM\SOFTWARE\DeskLink\Firewall 记录"用户是否同意启用 + 当前端口"，
//     作为后续修复入口与 QueryEnabled 的依据。
//
// 权限语义（重要，避免"假成功"）：
//   写 HKLM 与 netsh 规则都需要管理员。**未提权时本类不做任何变更并返回 false**，
//   而不是"记录意图假装成功"——后者会让 QueryEnabled 撒谎（说已启用但实际没规则），
//   用户遇到"直连不通"时无从判断。真正的启用动作由 install.ps1（提权）或
//   Settings 里的提权入口完成。
//
// 可测试性：所有 netsh 调用都经 IProcessRunner；命令拼装是纯静态函数。
using Microsoft.Win32;

namespace DeskLink.Service.Security;

/// <summary>一条防火墙规则的查询结果。</summary>
public readonly record struct FirewallRuleState(bool Present, int? LocalPort)
{
    public static FirewallRuleState Absent => new(false, null);
}

/// <summary>
/// "用户是否同意启用直连 + 当前端口"的持久化状态（默认实现读 HKLM 注册表）。
/// 抽成接口是为了让单测不碰真实注册表（HKLM 写入需要提权）。
/// </summary>
public interface IFirewallStateStore
{
    bool GetEnabled(int port);
    bool Set(bool enabled, int port);
}

/// <summary>注册表实现（HKLM\SOFTWARE\DeskLink\Firewall）。</summary>
public sealed class RegistryFirewallStateStore : IFirewallStateStore
{
    public const string RegKey = @"SOFTWARE\DeskLink\Firewall";

    private readonly Action<string>? _log;

    public RegistryFirewallStateStore(Action<string>? log = null) => _log = log;

    public bool GetEnabled(int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(RegKey, writable: false);
            if (k == null) return false;
            var en = k.GetValue("DirectEnabled");
            var p = k.GetValue("DirectPort");
            if (en is int enInt && p is int pInt)
            {
                return enInt != 0 && pInt == port;
            }
            return false;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FirewallHelper.QueryEnabled error: {ex.Message}");
            return false;
        }
    }

    public bool Set(bool enabled, int port)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var k = Registry.LocalMachine.CreateSubKey(RegKey, writable: true);
            if (k == null) return false;
            k.SetValue("DirectEnabled", enabled ? 1 : 0, RegistryValueKind.DWord);
            k.SetValue("DirectPort", port, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FirewallHelper.RecordState error: {ex.Message}");
            return false;
        }
    }
}

public sealed class FirewallHelper
{
    private readonly Action<string>? _log;
    private readonly IProcessRunner _runner;
    private readonly Func<bool> _isElevated;
    private readonly IFirewallStateStore _state;

    public FirewallHelper(
        Action<string>? log = null,
        IProcessRunner? runner = null,
        Func<bool>? isElevated = null,
        IFirewallStateStore? state = null)
    {
        _log = log;
        _runner = runner ?? new SystemProcessRunner();
        _isElevated = isElevated ?? IsElevated;
        _state = state ?? new RegistryFirewallStateStore(log);
    }

    /// <summary>TCP 入站规则名（DESIGN：规则按 DeskLink-* 命名）。</summary>
    public const string TcpRuleName = "DeskLink-Direct-TCP";

    /// <summary>UDP 入站规则名。</summary>
    public const string UdpRuleName = "DeskLink-Direct-UDP";

    // —— 命令拼装（纯函数，便于单测断言）——

    /// <summary>`netsh advfirewall firewall add rule ...` 参数。</summary>
    public static IReadOnlyList<string> BuildAddRuleArgs(string name, string protocol, int port) => new[]
    {
        "advfirewall", "firewall", "add", "rule",
        $"name={name}",
        "dir=in",
        "action=allow",
        $"protocol={protocol}",
        $"localport={port}",
        "profile=any",
        "enable=yes",
    };

    /// <summary>`netsh advfirewall firewall delete rule name=...` 参数。</summary>
    public static IReadOnlyList<string> BuildDeleteRuleArgs(string name) => new[]
    {
        "advfirewall", "firewall", "delete", "rule", $"name={name}",
    };

    /// <summary>`netsh advfirewall firewall show rule name=...` 参数。</summary>
    public static IReadOnlyList<string> BuildShowRuleArgs(string name) => new[]
    {
        "advfirewall", "firewall", "show", "rule", $"name={name}",
    };

    /// <summary>
    /// 从 `show rule` 的输出里解析 LocalPort。
    /// 兼容英文（LocalPort:）与中文（本地端口:）两种 netsh 输出；只依赖数字，
    /// 因此即使控制台代码页与读取编码不一致也能取到值。
    /// </summary>
    public static int? ParseLocalPort(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var isPortLine =
                line.Contains("localport", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("本地端口", StringComparison.Ordinal) ||
                line.Contains("端口", StringComparison.Ordinal);
            if (!isPortLine) continue;

            var colon = line.IndexOf(':');
            var tail = colon >= 0 ? line[(colon + 1)..] : line;
            // 只取**第一段连续数字**：netsh 可能输出 "47200,47201"（多端口），
            // 若把所有数字拼起来会得到 47200247201。
            var start = -1;
            for (var i = 0; i < tail.Length; i++)
            {
                if (char.IsAsciiDigit(tail[i]))
                {
                    if (start < 0) start = i;
                }
                else if (start >= 0)
                {
                    var run = tail[start..i];
                    if (int.TryParse(run, out var p)) return p;
                    break;
                }
            }
            if (start >= 0)
            {
                var run = tail[start..];
                if (int.TryParse(run, out var p2)) return p2;
            }
        }
        return null;
    }

    // —— 规则操作 ——

    /// <summary>
    /// 确保入站规则存在且指向指定端口（幂等）。
    ///
    /// 幂等做法：**先删后加**。`netsh add rule` 在同名规则已存在时会失败，
    /// 所以"先删（忽略不存在）再添加"是唯一能同时满足"重复调用结果一致"与
    /// "端口变更生效"的写法。
    /// </summary>
    public bool EnsureInboundRule(string name, string protocol, int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log?.Invoke($"FirewallHelper: 非 Windows，跳过规则 {name}");
            return false;
        }

        // 先删（不存在时 netsh 返回非 0，属预期，不视为错误）。
        _runner.Run("netsh", BuildDeleteRuleArgs(name));

        var r = _runner.Run("netsh", BuildAddRuleArgs(name, protocol, port));
        if (!r.Ok)
        {
            _log?.Invoke($"FirewallHelper: 添加规则失败 {name} protocol={protocol} port={port} " +
                         $"exit={r.ExitCode} err={Truncate(r.StdErr)}");
            return false;
        }
        _log?.Invoke($"FirewallHelper: 已放行入站 {name} protocol={protocol} port={port}");
        return true;
    }

    /// <summary>删除入站规则；规则不存在视为成功（幂等）。</summary>
    public bool RemoveInboundRule(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            _log?.Invoke($"FirewallHelper: 非 Windows，跳过删除规则 {name}");
            return false;
        }

        var r = _runner.Run("netsh", BuildDeleteRuleArgs(name));
        if (r.Ok)
        {
            _log?.Invoke($"FirewallHelper: 已删除入站规则 {name}");
            return true;
        }

        // netsh 在"没有匹配规则"时返回 1；这属于幂等成功。
        var combined = r.StdOut + r.StdErr;
        var notFound =
            combined.Contains("No rules match", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("没有", StringComparison.Ordinal) ||
            combined.Contains("no rules", StringComparison.OrdinalIgnoreCase);
        if (notFound)
        {
            _log?.Invoke($"FirewallHelper: 规则 {name} 不存在（幂等删除成功）");
            return true;
        }

        _log?.Invoke($"FirewallHelper: 删除规则失败 {name} exit={r.ExitCode} err={Truncate(r.StdErr)}");
        return false;
    }

    /// <summary>查询规则是否存在及其 LocalPort。</summary>
    public FirewallRuleState QueryRule(string name)
    {
        if (!OperatingSystem.IsWindows()) return FirewallRuleState.Absent;
        var r = _runner.Run("netsh", BuildShowRuleArgs(name));
        if (!r.Ok) return FirewallRuleState.Absent;
        return new FirewallRuleState(true, ParseLocalPort(r.StdOut));
    }

    /// <summary>直连端口当前是否已放行（TCP 规则存在且端口匹配）。</summary>
    public bool IsPortAllowed(int port)
    {
        var tcp = QueryRule(TcpRuleName);
        return tcp.Present && tcp.LocalPort == port;
    }

    // —— 注册表状态 ——

    public bool QueryEnabled(int port) => _state.GetEnabled(port);

    /// <summary>写入状态（需要提权；失败只记日志，不抛）。</summary>
    public bool RecordState(bool enabled, int port) => _state.Set(enabled, port);

    // —— 对外主操作 ——

    /// <summary>
    /// 启用/禁用直连端口放行。
    ///
    /// 返回 true 表示"防火墙规则与注册表状态都已按请求生效"。
    /// 未提权 → 返回 false 且**不做任何变更**（不写注册表，避免状态撒谎）。
    /// </summary>
    public bool SetEnabled(int port, bool enabled)
    {
        if (!_isElevated())
        {
            _log?.Invoke(
                $"FirewallHelper: 需要管理员权限才能修改防火墙规则（请求 enabled={enabled} port={port}）；" +
                "请在安装程序或提权的 Settings 中操作。未做任何变更。");
            return false;
        }

        bool ok;
        if (enabled)
        {
            ok = EnsureInboundRule(TcpRuleName, "TCP", port)
                 & EnsureInboundRule(UdpRuleName, "UDP", port);
        }
        else
        {
            ok = RemoveInboundRule(TcpRuleName)
                 & RemoveInboundRule(UdpRuleName);
        }

        if (ok)
        {
            RecordState(enabled, port);
            _log?.Invoke($"FirewallHelper: 直连端口放行已{(enabled ? "启用" : "禁用")} port={port}");
        }
        else
        {
            _log?.Invoke($"FirewallHelper: 直连端口放行变更未完全成功（enabled={enabled} port={port}），不更新注册表");
        }
        return ok;
    }

    /// <summary>
    /// 端口变更：删旧规则 + 加新规则（DESIGN.md 风险回顾 #8）。
    /// 仅当原端口处于启用状态时才动防火墙；否则只更新注册表记录的端口。
    /// </summary>
    public bool ApplyPortChange(int oldPort, int newPort)
    {
        if (oldPort == newPort) return true;

        var wasEnabled = QueryEnabled(oldPort);
        if (!wasEnabled)
        {
            _log?.Invoke($"FirewallHelper: 端口 {oldPort}→{newPort}（原未启用，仅更新注册表）");
            return RecordState(false, newPort);
        }

        if (!_isElevated())
        {
            _log?.Invoke(
                $"FirewallHelper: 端口 {oldPort}→{newPort} 需要管理员权限同步防火墙规则；未做变更。");
            return false;
        }

        var removed = RemoveInboundRule(TcpRuleName) & RemoveInboundRule(UdpRuleName);
        var added = EnsureInboundRule(TcpRuleName, "TCP", newPort)
                    & EnsureInboundRule(UdpRuleName, "UDP", newPort);
        var ok = removed && added;
        if (ok)
        {
            RecordState(true, newPort);
            _log?.Invoke($"FirewallHelper: 端口变更完成 {oldPort}→{newPort}");
        }
        else
        {
            _log?.Invoke($"FirewallHelper: 端口变更未完全成功 {oldPort}→{newPort}");
        }
        return ok;
    }

    /// <summary>
    /// 修复入口：把防火墙规则重新对齐到注册表记录的启用状态。
    /// 用于"进程路径移动后规则失效"或"规则被第三方清理"的场景（DESIGN 风险回顾 #8）。
    /// </summary>
    public bool Repair(int port)
    {
        var enabled = QueryEnabled(port);
        if (!enabled)
        {
            _log?.Invoke($"FirewallHelper: Repair 跳过（port={port} 未启用）");
            return true;
        }
        if (!_isElevated())
        {
            _log?.Invoke($"FirewallHelper: Repair 需要管理员权限（port={port}）");
            return false;
        }
        var ok = EnsureInboundRule(TcpRuleName, "TCP", port)
                 & EnsureInboundRule(UdpRuleName, "UDP", port);
        _log?.Invoke($"FirewallHelper: Repair port={port} ok={ok}");
        return ok;
    }

    /// <summary>
    /// 当前进程是否以管理员身份运行。
    ///
    /// 为什么需要它：本类的状态写在 HKLM（写需要提权），而真实 netsh 规则更是必须提权。
    /// 冒烟/自检脚本据此决定"能断言到哪一步"，而不是把权限问题误报成功能故障。
    /// </summary>
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // 取不到身份信息时保守返回 false（宁可少断言，不要假绿）。
            return false;
        }
    }

    private static string Truncate(string s, int max = 200)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
}
