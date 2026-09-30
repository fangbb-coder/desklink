// DeskLink.Panel —— 与 DeskLink.Service.exe 的进程外契约
//
// 面板从不"内嵌" Service：一切都是「拼参数 → 起子进程 → 读 stdout/stderr」。
// 这样 Service 的命令行语义仍然是唯一事实来源，面板只是把它 GUI 化。
//
// **参数装配全部是纯函数**（Build*Args），可以脱离进程单测——
// 这是本文件最重要的设计约束：GUI 里最容易出的错是把某个 flag 拼错了却看不出来。
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DeskLink.Panel;

/// <summary>一次性子命令的执行结果。</summary>
public sealed record OneShotResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;

    /// <summary>把 stdout+stderr 合成一段给人看的文本（失败时通常错误信息在 stderr）。</summary>
    public string Combined => string.IsNullOrWhiteSpace(StdErr) ? StdOut : $"{StdOut}\n{StdErr}".Trim();
}

public static class ServiceCli
{
    /// <summary>
    /// 探测 DeskLink.Service.exe 的位置。顺序：
    ///   1. 设置里手动指定的路径
    ///   2. 面板同目录（发布产物常把两者打平）
    ///   3. 面板同目录的 ../Service/（本仓库 dist 布局：Panel\ 与 Service\ 平级）
    ///   4. 仓库内 dev 输出（src/Tools/../Service/bin/...）
    /// </summary>
    public static string? ResolveServiceExe(PanelSettings settings, string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;

        if (!string.IsNullOrWhiteSpace(settings.ServiceExePath) && File.Exists(settings.ServiceExePath))
            return Path.GetFullPath(settings.ServiceExePath);

        var candidates = new List<string>
        {
            Path.Combine(baseDir, "DeskLink.Service.exe"),
            Path.Combine(baseDir, "..", "Service", "DeskLink.Service.exe"),
            Path.Combine(baseDir, "..", "..", "Service", "DeskLink.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "src", "Service", "DeskLink.Service", "bin", "Debug", "net9.0", "win-x64", "DeskLink.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "src", "Service", "DeskLink.Service", "bin", "Debug", "net9.0", "DeskLink.Service.exe"),
            Path.Combine(baseDir, "..", "..", "..", "src", "Service", "DeskLink.Service", "bin", "Release", "net9.0", "win-x64", "DeskLink.Service.exe"),
        };

        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>探测 WPF 客户端可执行文件（控制端点"打开控制界面"时用）。</summary>
    public static string? ResolveClientExe(PanelSettings settings, string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;

        if (!string.IsNullOrWhiteSpace(settings.ClientExePath) && File.Exists(settings.ClientExePath))
            return Path.GetFullPath(settings.ClientExePath);

        var candidates = new List<string>
        {
            Path.Combine(baseDir, "DeskLink.Client.exe"),
            Path.Combine(baseDir, "..", "Client", "DeskLink.Client.exe"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    /// <summary>
    /// 长时间运行的 Service 参数。
    /// 永远带 <c>--console</c>：面板自己就是前台进程，不需要 Windows 服务宿主。
    /// </summary>
    public static IReadOnlyList<string> BuildRunArgs(PanelSettings s)
    {
        var args = new List<string> { "--console", "--data-dir", s.DataDir, "--direct-port", s.DirectPort.ToString() };

        if (s.EnableDirect) args.Add("--enable-direct");
        if (s.InjectAgent) args.Add("--inject-agent");
        if (s.InsecureRelayTls) args.Add("--insecure-relay-tls");
        if (!string.IsNullOrWhiteSpace(s.RelayUrl)) { args.Add("--relay-url"); args.Add(s.RelayUrl); }
        foreach (var root in s.FileScopeRoots)
        {
            if (!string.IsNullOrWhiteSpace(root)) { args.Add("--file-scope"); args.Add(root); }
        }

        // 0 = 主显示器 = Service 的默认行为，不传；>0 才显式给 --monitor。
        // 这是"多显示器没有 UI 入口"的修法：以前面板拼不出这个参数。
        if (s.MonitorIndex > 0) { args.Add("--monitor"); args.Add(s.MonitorIndex.ToString()); }
        return args;
    }

    /// <summary>取本机配置/公钥（一次性，退出码 0）。</summary>
    public static IReadOnlyList<string> BuildPrintConfigArgs(PanelSettings s) =>
        new[] { "--data-dir", s.DataDir, "--print-config" };

    /// <summary>与对端配对（一次性，退出码 0）。直连必须两边都配，否则 SIGMA 前就被对端断开。</summary>
    public static IReadOnlyList<string> BuildPairArgs(PanelSettings s, string peerPubB64) =>
        new[] { "--data-dir", s.DataDir, "--pair-peer-pub", peerPubB64 };

    /// <summary>放开/关闭入站防火墙规则（一次性，退出码 0）。**需要管理员权限**。</summary>
    public static IReadOnlyList<string> BuildFirewallSetArgs(int port, bool enable) =>
        new[] { "--firewall-set", port.ToString(), enable ? "on" : "off" };

    /// <summary>只读查询防火墙状态（JSON，不需要管理员）。</summary>
    public static IReadOnlyList<string> BuildFirewallStatusArgs(int port) =>
        new[] { "--direct-port", port.ToString(), "--firewall-status" };

    /// <summary>起一次性子命令并收集输出。</summary>
    public static async Task<OneShotResult> RunOneShotAsync(
        string exePath, IReadOnlyList<string> args, int timeoutMs = 20000, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            return new OneShotResult(-1, "", $"无法启动 {exePath}：{ex.Message}");
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(timeoutMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            return new OneShotResult(-1, stdout.ToString(),
                $"命令执行超时（{timeoutMs}ms）：{string.Join(' ', args)}");
        }

        return new OneShotResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // 进程可能刚好自己退了；杀不掉不影响调用方拿到"超时"结论。
        }
    }
}
