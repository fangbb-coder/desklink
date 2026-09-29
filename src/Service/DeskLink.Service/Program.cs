// DeskLink.Service 入口。
//
// 模式：
//   --console   走 GenericHost（开发 / 联调）
//   缺省        走 HostBuilder.UseWindowsService()（LocalSystem 开机启动）
//
// 强制约束：
//   - 命令行解析失败 → 退出码 2 + stderr
//   - --print-config / --help → 退出码 0
//   - 其他启动异常 → 退出码 1 + 详细日志
//   - 输入环路防护：AgentLauncher.Start(inject=true, noInject=false) 会抛
//     AgentInjectSafetyException；本入口在 Program.Main 顶层不捕获，让其退出非零
//     并把异常消息打到 stderr（DESIGN.md 风险回顾 #4）
using DeskLink.Service.Configuration;
using DeskLink.Service.Direct;
using DeskLink.Service.Pipe;
using DeskLink.Service.Process;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskLink.Protocol.Pipe;

using DeskLink.Service.Media;

namespace DeskLink.Service;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1) 命令行解析
        var parse = CommandLineParser.Parse(args);
        if (parse.Error != null)
        {
            await Console.Error.WriteLineAsync($"error: {parse.Error}");
            await Console.Error.WriteLineAsync();
            await Console.Error.WriteLineAsync(CommandLineParser.HelpText);
            return parse.ExitCode ?? 2;
        }
        if (parse.HelpRequested)
        {
            await Console.Out.WriteLineAsync(CommandLineParser.HelpText);
            return 0;
        }

        var options = parse.Options;

        // 2) --print-config：打印后退出 0
        if (options.PrintConfig)
        {
            await Console.Out.WriteLineAsync(options.ToString());
            try
            {
                var ks = new KeyStore(options.DataDir);
                ks.EnsureDataDir();
                var keys = ks.LoadOrCreate();
                await Console.Out.WriteLineAsync($"KeyStore: {ks.Path}");
                await Console.Out.WriteLineAsync($"  ed25519_pub_b64 = {Convert.ToBase64String(keys.KeyPair.Ed25519Public)}");
                await Console.Out.WriteLineAsync($"  x25519_pub_b64  = {Convert.ToBase64String(keys.KeyPair.X25519Public)}");
                await Console.Out.WriteLineAsync($"  device_id_hint  = {Convert.ToBase64String(keys.DeviceIdHint)}");
                // device_id（hex）：= BLAKE3("desklink/device/v1" || ed25519_pub)。
                // 供冒烟脚本/运维直接引用（中继 --peer、直连 --peer 都用它）。
                await Console.Out.WriteLineAsync(
                    $"  device_id       = {Convert.ToHexString(E2ESessionHost.ComputeDeviceId(keys.KeyPair.Ed25519Public))}");
                // 环境能力：QUIC 是否可用（决定 quic:// 能否使用）。
                // 冒烟脚本据此决定"跑不跑 QUIC 用例"，而不是靠猜 OS 版本。
                await Console.Out.WriteLineAsync($"  quic_available  = {QuicTransport.IsAvailable().ToString().ToLowerInvariant()}");
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"KeyStore error: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            return 0;
        }

        // 2.5) --firewall-status：打印防火墙/直连状态 JSON 后退出 0（只读，不需提权）
        if (options.PrintFirewallStatus)
        {
            try
            {
                var fw = new FirewallHelper(msg => Console.Out.WriteLine(msg));
                var enabled = fw.QueryEnabled(options.DirectPort);
                var tcp = fw.QueryRule(FirewallHelper.TcpRuleName);
                var udp = fw.QueryRule(FirewallHelper.UdpRuleName);
                // 手写 JSON（字段名与 StatusResult 对齐，便于脚本/运维统一解析）。
                await Console.Out.WriteLineAsync(
                    "{" +
                    $"\"direct_port\":{options.DirectPort}," +
                    $"\"direct_enabled\":{(enabled ? "true" : "false")}," +
                    $"\"elevated\":{(FirewallHelper.IsElevated() ? "true" : "false")}," +
                    "\"netsh_rules_implemented\":true," +
                    $"\"rule_tcp_present\":{(tcp.Present ? "true" : "false")}," +
                    $"\"rule_udp_present\":{(udp.Present ? "true" : "false")}," +
                    $"\"rule_tcp_port\":{(tcp.LocalPort.HasValue ? tcp.LocalPort.Value.ToString() : "null")}," +
                    $"\"rule_udp_port\":{(udp.LocalPort.HasValue ? udp.LocalPort.Value.ToString() : "null")}" +
                    "}");
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"firewall-status error: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            return 0;
        }

        // 2.55) --firewall-set / --firewall-repair：安装程序与修复入口（需要管理员）
        //
        // 放在这里而不是让 install.ps1 自己拼 netsh：规则命名/幂等/端口变更/注册表状态
        // 的唯一真相在 FirewallHelper（13 个单测覆盖）。脚本只负责提权调用本入口。
        if (options.FirewallSet.HasValue || options.FirewallRepair.HasValue)
        {
            var fw = new FirewallHelper(msg => Console.Out.WriteLine(msg));
            if (!FirewallHelper.IsElevated())
            {
                await Console.Error.WriteLineAsync(
                    "error: --firewall-set/--firewall-repair 需要管理员权限（netsh 与 HKLM 写入都要提权）");
                return 3;
            }

            bool ok;
            if (options.FirewallSet.HasValue)
            {
                var (port, enable) = options.FirewallSet.Value;
                ok = fw.SetEnabled(port, enable);
                await Console.Out.WriteLineAsync(
                    $"firewall_set port={port} enabled={enable.ToString().ToLowerInvariant()} ok={ok.ToString().ToLowerInvariant()}");
            }
            else
            {
                var port = options.FirewallRepair!.Value;
                ok = fw.Repair(port);
                await Console.Out.WriteLineAsync($"firewall_repair port={port} ok={ok.ToString().ToLowerInvariant()}");
            }
            return ok ? 0 : 1;
        }

        // 2.6) --pair-peer-pub：把对端公钥加入本机配对列表后退出 0（运维/联调入口）
        if (!string.IsNullOrWhiteSpace(options.PairPeerPub))
        {
            try
            {
                var pub = ParsePubKey(options.PairPeerPub!);
                if (pub is null)
                {
                    await Console.Error.WriteLineAsync("--pair-peer-pub: expected 64-char hex or 32-byte base64 Ed25519 public key");
                    return 2;
                }
                var store = new PairingStore(options.DataDir);
                store.Add(pub, "cli");
                await Console.Out.WriteLineAsync(
                    $"paired: peer_pub_b64={Convert.ToBase64String(pub)} count={store.Count} data_dir={options.DataDir}");
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"pair-peer-pub error: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            return 0;
        }

        // 2.7) --direct-probe：作为控制端发起一次局域网直连探测，打印 JSON 后退出
        if (!string.IsNullOrWhiteSpace(options.DirectProbe))
        {
            return await RunDirectProbeAsync(options);
        }

        // 3) 构造宿主
        try
        {
            var host = BuildHost(options);
            await host.RunAsync();
            return 0;
        }
        catch (AgentInjectSafetyException ex)
        {
            // DESIGN.md 风险回顾 #4：输入环路防护触发
            await Console.Error.WriteLineAsync($"FATAL: {ex.Message}");
            return 64; // EX_USAGE 风格
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static IHost BuildHost(ServiceOptions options)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        // Windows 服务模式（生产）
        if (!options.ConsoleMode)
        {
            builder.Services.AddWindowsService(opt =>
            {
                opt.ServiceName = "DeskLinkService";
            });
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss.fff ";
        });

        // 单例装配
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(sp => new KeyStore(options.DataDir));
        builder.Services.AddSingleton(sp => new PairingStore(options.DataDir));
        builder.Services.AddSingleton(sp => ResolveAgentExePath(options));
        builder.Services.AddSingleton<AgentLauncher>(sp =>
        {
            var path = sp.GetRequiredService<string>();
            var lf = sp.GetRequiredService<ILoggerFactory>();
            // 代理输出落到数据目录的 logs 子目录：常驻代理会持续写日志，
            // 必须重定向并持续读取，否则子进程会被写满的管道卡死（详见 AgentLauncher）。
            var logDir = Path.Combine(options.DataDir, "logs");
            return new AgentLauncher(path, logDir, msg => lf.CreateLogger("AgentLauncher").LogInformation("{Msg}", msg));
        });
        builder.Services.AddSingleton<FirewallHelper>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            return new FirewallHelper(msg => lf.CreateLogger("Firewall").LogInformation("{Msg}", msg));
        });

        // RelayClient（P5 TOFU）：配置 RelayUrl 时注册；未配置时**注册一个 null 实例**。
        //
        // 为什么是"注册 null"而不是"不注册"：.NET 的 DI **不会**为未注册的引用类型参数
        // 注入 null，ServiceHost 的构造会直接抛 InvalidOperationException
        // （实测：Service 启动即 FATAL）。注册一个 null 实例后，
        // `sp.GetService<RelayClient>()` 得到 null，语义正确且能启动。
        //
        // CS8634 的原因：AddSingleton<TService> 有 class 约束，而这里 TService 是可空引用类型。
        // 这是"可选依赖"在 DI 里的固有摩擦，无法用类型系统干净表达，因此就地抑制并说明。
// 两个警告都来自"可选依赖"这一写法本身：CS8634（可空类型与 class 约束不匹配）
// 与 CS8621（lambda 返回 null 与委托的返回可空性不匹配）。
#pragma warning disable CS8634, CS8621
        builder.Services.AddSingleton(sp =>
        {
            if (options.RelayUrl == null) return null;

            var lf = sp.GetRequiredService<ILoggerFactory>();
            var log = (string msg) => lf.CreateLogger("RelayClient").LogInformation("{Msg}", msg);

            // 中继 TLS 信任策略（P5 TOFU）：
            //   默认 RelayTrustPolicy（首次记录证书指纹，之后必须一致）；
            //   --insecure-relay-tls 显式关闭 → AcceptAny（仅本机联调）。
            // 两者都通过具名对象注入，代码里可 grep 出所有"放行点"。
            IRelayTrustPolicy trust = options.InsecureRelayTls
                ? RelayTrustPolicy.AcceptAny
                : new RelayTrustPolicy(
                    new RelayPinStore(options.DataDir),
                    msg => lf.CreateLogger("RelayTrust").LogInformation("{Msg}", msg));

            var relay = new RelayClient(
                options.RelayUrl,
                new BackoffPolicy(options.BackoffBaseMs, options.BackoffMaxMs),
                onReady: null,
                trust: trust,
                log: log);
            // OnState / OnReady 回调由 ServiceHost 在启动时挂上（见 ServiceHost.StartAsync）
            return relay;
        });
#pragma warning restore CS8634, CS8621

        // E2E 会话驱动器（P4/P5）：把传输就绪推进为加密会话。
        // 仅在配置了 relay 时才有意义，但注册本身无副作用。
        builder.Services.AddSingleton(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var log = (string msg) => lf.CreateLogger("RelaySession").LogInformation("{Msg}", msg);
            return new RelaySessionRunner(
                sp.GetRequiredService<KeyStore>(),
                sp.GetRequiredService<PairingStore>(),
                options,
                log);
        });

        // 局域网直连服务端（P5.5）：被控端角色。
        // 仅作为对象注册；是否真正监听由 ServiceHost 依据 --enable-direct /
        // 注册表 DirectEnabled 决定（避免"注册即监听"的副作用）。
        builder.Services.AddSingleton(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var log = (string msg) => lf.CreateLogger("DirectServer").LogInformation("{Msg}", msg);
            return new DirectServer(
                sp.GetRequiredService<PairingStore>(),
                sp.GetRequiredService<KeyStore>(),
                options,
                options.DirectPort,
                log);
        });

        // 局域网直连**出站**拨号器（P5.5 控制端角色）。
        //
        // 与 DirectServer 的区别：不需要监听端口，因此**无条件注册**——
        // 控制端机台上没有入站防火墙规则也必须能主动连出去。
        // 这正是 WPF 客户端"局域网直连"所缺的那一环。
        builder.Services.AddSingleton(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var log = (string msg) => lf.CreateLogger("DirectDialer").LogInformation("{Msg}", msg);
            return new DirectDialer(
                sp.GetRequiredService<KeyStore>(),
                sp.GetRequiredService<PairingStore>(),
                options,
                log);
        });

        // IServiceCore（管道 RPC 业务实现）
        builder.Services.AddSingleton<IServiceCore>(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            var log = (string msg) => lf.CreateLogger("ServiceCore").LogInformation("{Msg}", msg);
            return new ServiceCore(
                sp.GetRequiredService<KeyStore>(),
                sp.GetRequiredService<PairingStore>(),
                sp.GetRequiredService<AgentLauncher>(),
                sp.GetRequiredService<FirewallHelper>(),
                options,
                sp.GetService<RelayClient>(),   // null 安全：未配置 relay 时返回 null
                sp.GetRequiredService<RelaySessionRunner>(),
                sp.GetRequiredService<DirectServer>(),
                sp.GetRequiredService<DirectDialer>(),
                sp.GetRequiredService<MediaPipeServer>(),
                log);
        });

        // MediaPipeServer（本地媒体通道转发泵）
        builder.Services.AddSingleton(sp =>
        {
            var lf = sp.GetRequiredService<ILoggerFactory>();
            return new MediaPipeServer(
                options.ClientMediaPipeName,
                options.AgentMediaPipeName,
                options: null,
                log: msg => lf.CreateLogger("MediaPipe").LogInformation("{Msg}", msg));
        });

        // PipeServer
        builder.Services.AddSingleton(sp =>
        {
            var core = sp.GetRequiredService<IServiceCore>();
            var lf = sp.GetRequiredService<ILoggerFactory>();
            return new PipeServer(
                options.ClientPipeName,
                options.AgentPipeName,
                core,
                msg => lf.CreateLogger("PipeServer").LogInformation("{Msg}", msg));
        });

        builder.Services.AddHostedService<ServiceHost>();

        return builder.Build();
    }

    /// <summary>解析 64 字符 hex 或 32 字节 base64 公钥。</summary>
    private static byte[]? ParsePubKey(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 64 && t.All(Uri.IsHexDigit))
        {
            try { return Convert.FromHexString(t); } catch (FormatException) { return null; }
        }
        try
        {
            var b = Convert.FromBase64String(t);
            if (b.Length == 32) return b;
        }
        catch (FormatException) { /* fallthrough */ }
        return null;
    }

    /// <summary>
    /// 作为控制端对 host:port 发起一次局域网直连会话，打印 JSON 结果后退出。
    /// 对端 device_id 取 --peer；缺省回退"唯一配对条目"由公钥推导。
    /// </summary>
    private static async Task<int> RunDirectProbeAsync(ServiceOptions options)
    {
        var target = options.DirectProbe!.Trim();
        var idx = target.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(target[(idx + 1)..], out var port) || port <= 0 || port > 65535)
        {
            await Console.Error.WriteLineAsync("--direct-probe: expected host:port");
            return 2;
        }
        var host = target[..idx];

        // 解析对端 device_id
        byte[]? peerId = null;
        if (!string.IsNullOrWhiteSpace(options.PeerDeviceId))
        {
            peerId = RelaySessionRunner.ParseDeviceId(options.PeerDeviceId!);
        }
        if (peerId is null)
        {
            var pairings = new PairingStore(options.DataDir).List();
            if (pairings.Count == 1)
            {
                var pub = Convert.FromBase64String(pairings[0].PeerPubB64);
                if (pub.Length == 32) peerId = E2ESessionHost.ComputeDeviceId(pub);
            }
        }
        if (peerId is null)
        {
            await Console.Error.WriteLineAsync(
                "--direct-probe: cannot resolve peer device_id (use --peer, or have exactly one pairing)");
            return 2;
        }

        var keys = new KeyStore(options.DataDir);
        keys.EnsureDataDir();
        var client = new DirectClient(keys, msg => Console.Error.WriteLine(msg));

        var probe = new SessionControlProbe(msg => Console.Error.WriteLine(msg));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await client.ConnectAsync(host, port, peerId, cts.Token);

        var roundTrip = false;
        var rttMs = -1L;
        if (result.Ok && result.Pump is not null)
        {
            probe.Attach(result.Pump);
            await probe.SendProbeAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !probe.RoundTripOk)
            {
                await Task.Delay(50, cts.Token);
            }
            roundTrip = probe.RoundTripOk;
            rttMs = await result.Pump.MeasureRttAsync(TimeSpan.FromSeconds(3), cts.Token) ?? -1;
            try { await result.Pump.DisposeAsync(); } catch { /* best-effort */ }
            result.Session?.Dispose();
        }

        await Console.Out.WriteLineAsync(
            "{" +
            $"\"ok\":{(result.Ok ? "true" : "false")}," +
            $"\"detail\":\"{Escape(result.Detail ?? "")}\"," +
            $"\"peer_device_id\":\"{Convert.ToHexString(peerId)}\"," +
            $"\"control_round_trip\":{(roundTrip ? "true" : "false")}," +
            $"\"rtt_ms\":{rttMs}," +
            $"\"fingerprint_prompt\":false" + // 直连复用已配对公钥，按设计不弹指纹确认
            "}");
        return result.Ok ? 0 : 1;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ResolveAgentExePath(ServiceOptions options)
    {
        // 1) 生产布局：与 Service 同目录（install.ps1 会把两个 exe 都部署到
        //    %ProgramFiles%\DeskLink）。
        var baseDir = AppContext.BaseDirectory;
        var installed = Path.Combine(baseDir, "DeskLink.DesktopAgent.exe");
        if (File.Exists(installed)) return installed;

        // 2) 开发布局：代理是 net9.0-windows 目标，输出目录与 Service 不同，
        //    所以从 bin 目录直接跑 --console 时同目录找不到代理。
        //    这里按仓库结构回溯一层（仅用于开发/冒烟，不影响生产路径）。
        //    找不到就返回安装路径，由 AgentLauncher 走 stub 模式并明确记录原因。
        // 层级：net9.0 → Debug → bin → DeskLink.Service → Service → src → 仓库根（共 6 级）。
        // 少一级就会解析到 src 目录，探测永远失败（实测踩过）。
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir,
            "..", "..", "..", "..", "..", ".."));
        foreach (var config in new[] { "Debug", "Release" })
        {
            var dev = Path.Combine(repoRoot, "src", "Agent", "DeskLink.DesktopAgent",
                "bin", config, "net9.0-windows", "DeskLink.DesktopAgent.exe");
            if (File.Exists(dev)) return dev;
        }

        return installed;
    }
}
