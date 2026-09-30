// 命令行解析。轻量手写，避免引入未审查的 CommandLineParser 包。
//
// 支持：
//   --console                   以控制台模式运行（不注册为 Windows 服务）
//   --data-dir <path>           数据目录（覆盖默认 %ProgramData%\DeskLink）
//   --relay-url <url>           中继入口 URL（QUIC 优先；TCP/TLS 兜底）
//   --direct-port <port>        局域网直连端口（默认 47200）
//   --monitor <n>               捕获哪块显示器（0=主显示器；仅被控端需要）
//   --pipe-prefix <prefix>      命名管道名前缀（默认 DeskLink）
//   --inject-agent              由 Service 启动 DeskLink.DesktopAgent（P7 接入真实路径）
//   --print-config              打印解析后的配置后退出 0
//   --help / -h                 打印帮助后退出 0
//
// 注意：
//   --no-inject 不在解析范围；该开关由调用方在启动 Agent 子进程处强制
//   （DESIGN.md 风险回顾 #4：Service 启动 Agent 时若未传 --no-inject 则 hard-fail）。
using System.Net;

namespace DeskLink.Service.Configuration;

public static class CommandLineParser
{
    public sealed class ParseResult
    {
        public ServiceOptions Options { get; }
        public bool HelpRequested { get; }
        public string? Error { get; }
        public int? ExitCode { get; }

        public ParseResult(ServiceOptions options, bool help, string? error, int? exitCode)
        {
            Options = options;
            HelpRequested = help;
            Error = error;
            ExitCode = exitCode;
        }

        public bool Ok => Error == null && !HelpRequested;
    }

    /// <summary>
    /// 生产入口：先廉价取出 --data-dir，据此读出 &lt;data-dir&gt;\service.json 里的
    /// 持久化配置（文件授权目录、捕获显示器索引），再做完整解析。
    ///
    /// **命令行永远优先**于落盘值——落盘只负责"你没写时用上次那个"。
    /// </summary>
    public static ParseResult Parse(string[] args)
    {
        var dataDir = ScanDataDir(args) ?? new ServiceOptions().DataDir;
        return Parse(args, ServiceConfig.TryLoad(dataDir));
    }

    /// <summary>
    /// 纯函数入口：<paramref name="persisted"/> 是上一次落盘（或测试构造）的配置，
    /// 为 null 时行为与"从未持久化过"完全一致。
    /// </summary>
    public static ParseResult Parse(string[] args, ServiceConfig? persisted)
    {
        var options = new ServiceOptions();
        string? dataDirOverride = null;
        string? relayOverride = null;
        string? directPortOverride = null;
        string? monitorOverride = null;
        string? pipePrefixOverride = null;
        string? peerOverride = null;
        string? pairPeerPub = null;
        string? directProbe = null;
        string? firewallSetPort = null;
        string? firewallSetValue = null;
        string? firewallRepairPort = null;
        bool help = false;
        bool printConfig = false;
        bool firewallStatus = false;
        bool injectAgent = false;
        bool console = false;
        bool insecureRelayTls = false;
        bool enableDirect = false;
        var fileScopes = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--console":
                    console = true;
                    break;
                case "--data-dir":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--data-dir requires a path", 2);
                    }
                    dataDirOverride = args[++i];
                    break;
                case "--relay-url":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--relay-url requires a URL", 2);
                    }
                    relayOverride = args[++i];
                    break;
                case "--direct-port":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--direct-port requires a port", 2);
                    }
                    directPortOverride = args[++i];
                    break;
                case "--monitor":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--monitor requires an index", 2);
                    }
                    monitorOverride = args[++i];
                    break;
                case "--pipe-prefix":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--pipe-prefix requires a name", 2);
                    }
                    pipePrefixOverride = args[++i];
                    break;
                case "--peer":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--peer requires a device_id (hex or base64)", 2);
                    }
                    peerOverride = args[++i];
                    break;
                case "--inject-agent":
                    injectAgent = true;
                    break;
                case "--insecure-relay-tls":
                    insecureRelayTls = true;
                    break;
                case "--enable-direct":
                    enableDirect = true;
                    break;
                case "--file-scope":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--file-scope requires a directory", 2);
                    }
                    fileScopes.Add(args[++i]);
                    break;
                case "--pair-peer-pub":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--pair-peer-pub requires a public key (hex or base64)", 2);
                    }
                    pairPeerPub = args[++i];
                    break;
                case "--firewall-set":
                    if (i + 2 >= args.Length)
                    {
                        return new ParseResult(options, false, "--firewall-set requires <port> <on|off>", 2);
                    }
                    firewallSetPort = args[++i];
                    firewallSetValue = args[++i];
                    break;
                case "--firewall-repair":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--firewall-repair requires <port>", 2);
                    }
                    firewallRepairPort = args[++i];
                    break;
                case "--direct-probe":
                    if (i + 1 >= args.Length)
                    {
                        return new ParseResult(options, false, "--direct-probe requires host:port", 2);
                    }
                    directProbe = args[++i];
                    break;
                case "--print-config":
                    printConfig = true;
                    break;
                case "--firewall-status":
                    firewallStatus = true;
                    break;
                case "-h":
                case "--help":
                    help = true;
                    break;
                default:
                    return new ParseResult(options, false, $"unknown argument: {a}", 2);
            }
        }

        if (help)
        {
            return new ParseResult(options, true, null, 0);
        }

        if (dataDirOverride != null)
        {
            options.DataDir = Path.GetFullPath(dataDirOverride);
            options.InstanceId = DeriveInstanceId(options.DataDir);
        }
        if (relayOverride != null)
        {
            if (!Uri.TryCreate(relayOverride, UriKind.Absolute, out var relay))
            {
                return new ParseResult(options, false, $"--relay-url invalid: {relayOverride}", 2);
            }
            options.RelayUrl = relay;
        }
        if (directPortOverride != null)
        {
            if (!int.TryParse(directPortOverride, out var port) || port <= 0 || port > 65535)
            {
                return new ParseResult(options, false, $"--direct-port invalid: {directPortOverride}", 2);
            }
            options.DirectPort = port;
        }
        if (monitorOverride != null)
        {
            if (!int.TryParse(monitorOverride, out var mon) || mon < 0)
            {
                return new ParseResult(options, false, $"--monitor invalid (must be >= 0): {monitorOverride}", 2);
            }
            options.CaptureMonitorIndex = mon;
        }
        if (pipePrefixOverride != null)
        {
            options.PipeNamePrefix = pipePrefixOverride;
        }
        if (peerOverride != null)
        {
            // 允许 hex（64 字符）或 base64；此处只做规范化与长度校验，
            // 真正的解码在 RelaySessionRunner 里（保持解析器无密码学依赖）。
            var normalized = peerOverride.Trim();
            if (normalized.Length == 0)
            {
                return new ParseResult(options, false, "--peer must not be empty", 2);
            }
            options.PeerDeviceId = normalized;
        }

        options.ConsoleMode = console;
        options.InjectAgent = injectAgent;
        options.PrintConfig = printConfig;
        options.PrintFirewallStatus = firewallStatus;
        options.InsecureRelayTls = insecureRelayTls;
        options.EnableDirect = enableDirect;
        // 授权目录一律解析成绝对路径（scope 判定依赖绝对路径），并去重。
        // 合并规则：**命令行优先**——命令行给了 --file-scope 就完全以它为准；
        // 一个都没给时才回落到上次落盘的 service.json（这就是"持久化"）。
        var scoped = fileScopes
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => Path.GetFullPath(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (scoped.Count == 0 && persisted is not null)
        {
            scoped = new List<string>(persisted.FileScopeRoots);
        }
        options.FileScopeRoots = scoped;
        if (monitorOverride is null && persisted is not null && persisted.CaptureMonitorIndex > 0)
        {
            options.CaptureMonitorIndex = persisted.CaptureMonitorIndex;
        }
        options.PairPeerPub = pairPeerPub;
        options.DirectProbe = directProbe;

        if (firewallSetPort != null)
        {
            if (!TryParsePort(firewallSetPort, out var fp))
            {
                return new ParseResult(options, false, $"--firewall-set port invalid: {firewallSetPort}", 2);
            }
            var on = (firewallSetValue ?? "").Trim().ToLowerInvariant() switch
            {
                "on" or "1" or "true" or "enable" => true,
                "off" or "0" or "false" or "disable" => false,
                _ => (bool?)null,
            };
            if (on is null)
            {
                return new ParseResult(options, false,
                    $"--firewall-set value must be on|off, got: {firewallSetValue}", 2);
            }
            options.FirewallSet = (fp, on.Value);
        }
        if (firewallRepairPort != null)
        {
            if (!TryParsePort(firewallRepairPort, out var rp))
            {
                return new ParseResult(options, false, $"--firewall-repair port invalid: {firewallRepairPort}", 2);
            }
            options.FirewallRepair = rp;
        }
        return new ParseResult(options, false, null, null);
    }

    public static string HelpText => """
        DeskLink.Service — Windows 服务宿主（LocalSystem）+ 控制台开发模式

        Usage:
          DeskLink.Service [options]

        Options:
          --console                  以控制台模式运行（不注册为 Windows 服务）
          --data-dir <path>          数据目录（默认 %ProgramData%\DeskLink）
          --relay-url <url>          中继入口 URL(quic:// 强制 QUIC / tls:// 强制 TCP-TLS / https:// 优先 QUIC 回落 TCP-TLS)
          --direct-port <port>       局域网直连端口（默认 47200）
          --monitor <n>              捕获哪块显示器（0=主显示器；仅被控端需要；可用 DeskLink.DesktopAgent --list-monitors 查索引）
          --pipe-prefix <prefix>     命名管道名前缀（默认 DeskLink）
          --peer <device_id>         对端 device_id（hex 或 base64）；缺省回退 pairings.json
          --insecure-relay-tls       关闭中继 TLS 的 TOFU 指纹校验（仅本机联调；生产勿用）
          --enable-direct            启动局域网直连监听（被控端；默认端口 47200）
          --file-scope <dir>         文件传输授权目录（可重复；不指定则回落到上次落盘的 <data-dir>\service.json）
          --pair-peer-pub <key>      把对端 Ed25519 公钥(hex/base64)加入本机配对列表后退出(运维/联调)
          --direct-probe <host:port> 作为控制端发起一次直连探测，打印 JSON 后退出(运维/联调)
          --firewall-set <port> <on|off>  放行/回收入站规则后退出(需管理员；供安装程序调用)
          --firewall-repair <port>   按注册表状态重建入站规则后退出(需管理员；修复入口)
          --inject-agent             由 Service 启动 DeskLink.DesktopAgent
          --print-config             打印配置后退出
          --firewall-status          打印防火墙/直连状态 JSON 后退出(只读,不需提权)
          -h, --help                 打印帮助

        注意：
          --no-inject 由 AgentLauncher 在启动 Agent 子进程时强制校验，
          不作为本进程启动参数。
          授权目录与捕获显示器索引会落盘到 <data-dir>\service.json，下次启动自动读回；
          命令行显式传入时以命令行为准。
        """;

    /// <summary>
    /// 第一遍扫描：只取 --data-dir（service.json 就放在这个目录里，所以得先知道它）。
    /// 没给就用 <see cref="ServiceOptions"/> 的默认目录。
    /// </summary>
    private static string? ScanDataDir(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--data-dir" && i + 1 < args.Length) return args[i + 1];
        }
        return null;
    }

    private static bool TryParsePort(string text, out int port)
        => int.TryParse(text, out port) && port > 0 && port <= 65535;

    private static string DeriveInstanceId(string dataDir)
    {
        // 取路径末段作为实例 id；同目录重启时稳定。
        var trimmed = dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
        {
            name = "default";
        }
        // 管道名禁用字符替换为 _
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '_');
        }
        return name.ToLowerInvariant();
    }
}
