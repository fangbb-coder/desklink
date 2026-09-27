using System.Text.Json;
using System.Text.Json.Serialization;
using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Codec;
using DeskLink.DesktopAgent.Input;
using DeskLink.DesktopAgent.Pipe;
using DeskLink.DesktopAgent.Pipeline;
using DeskLink.DesktopAgent.Session;

using DeskLink.DesktopAgent.Runtime;

namespace DeskLink.DesktopAgent;

/// <summary>
/// DeskLinkDesktop 会话内代理入口。
///
/// 职责：解析命令行 → 采集会话/输入/编码器状态 → 输出一行 JSON 状态，便于脚本断言。
/// 设计上刻意保持"启动即可自证"：`--test-pattern` 会在不碰真实桌面的前提下
/// 跑完整条回环管线并报告像素校验结果，让集成测试可以脱离图形栈验证 P7。
///
/// 输入环路防护（DESIGN 风险回顾 #4）：
///   `--inject` 表示"允许真实 SendInput"，`--no-inject` 表示"注入桩，空操作"。
///   两者同时出现时以 `--no-inject` 为准（安全优先）；
///   只给 `--inject` 而没有 `--no-inject` 时**拒绝启动**并返回非零退出码——
///   这是 Service 侧守卫的镜像，防止自控自场景下输入回环。
/// </summary>
public static class Program
{
    public const string AgentVersion = "P7";
    public const string DefaultPipeName = "DeskLink.Agent.default";

    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitRuntimeFailure = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static int Main(string[] args)
    {
        AgentOptions options;
        try
        {
            options = AgentOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return ExitUsage;
        }

        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return ExitOk;
        }

        // —— 输入环路守卫（镜像 Service 侧）——
        if (IsInjectionGuardViolation(options, out string guardMessage))
        {
            Console.Error.WriteLine(guardMessage);
            return ExitUsage;
        }

        try
        {
            return Run(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"运行失败：{ex.GetType().Name}: {ex.Message}");
            var status = new Dictionary<string, object?>
            {
                ["agent"] = "DeskLink.DesktopAgent",
                ["version"] = AgentVersion,
                ["ok"] = false,
                ["error"] = $"{ex.GetType().Name}: {ex.Message}",
            };
            WriteJsonLine(status);
            return ExitRuntimeFailure;
        }
    }

    /// <summary>
    /// 输入环路守卫。独立成公开方法是为了让单元测试能直接断言这条安全规则，
    /// 而不必去启动进程解析退出码。
    ///
    /// 规则：<c>--inject</c> 且没有 <c>--no-inject</c> → 违规（拒绝启动）。
    /// 两者都给时以 <c>--no-inject</c> 为准（安全优先），不算违规。
    /// </summary>
    public static bool IsInjectionGuardViolation(AgentOptions options, out string message)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Inject && !options.NoInject)
        {
            message =
                "拒绝启动：检测到 --inject 但没有 --no-inject。" + Environment.NewLine +
                "在自控自（agent 抓本机桌面并注入本机输入）场景下这会造成输入回环。" + Environment.NewLine +
                "请显式传 --no-inject（注入桩，空操作），或去掉 --inject 只做状态上报。";
            return true;
        }

        message = "";
        return false;
    }

    private static int Run(AgentOptions options)
    {
        if (options.Run)
        {
            return RunResident(options);
        }

        if (options.ListMonitors)
        {
            var monitors = DuplicationCapture.ListMonitors();
            WriteJsonLine(new Dictionary<string, object?>
            {
                ["agent"] = "DeskLink.DesktopAgent",
                ["version"] = AgentVersion,
                ["ok"] = true,
                ["monitors"] = monitors,
            });
            return ExitOk;
        }

        var watcher = new SessionWatcher();
        var session = watcher.GetState();
        bool canCapture = watcher.CanCaptureDesktop(out string captureBlockReason);

        var injector = new InputInjector(options.NoInject);

        // —— 可选：无头回环自检 ——
        LoopbackResult? loopback = null;
        if (options.TestPattern)
        {
            loopback = LoopbackPipeline.Run(
                width: options.PatternWidth,
                height: options.PatternHeight,
                preferHardware: options.PreferHardware,
                rotationDegrees: options.Rotation);
        }

        // —— 可选：真实 DXGI 捕获探测（只建管线不做长期运行）——
        string? captureError = null;
        string? captureDescription = null;
        if (!options.TestPattern)
        {
            using var capture = DuplicationCapture.TryCreate(options.MonitorIndex, out captureError);
            if (capture is not null)
            {
                captureDescription = capture.Description;
                captureError = null;
            }
        }

        // —— 可选：与 Service 的 agent 管道握手 ——
        var pipeReport = TryPingService(options.PipeName, options.PipeTimeoutMs);

        var status = new Dictionary<string, object?>
        {
            ["agent"] = "DeskLink.DesktopAgent",
            ["version"] = AgentVersion,
            ["pid"] = Environment.ProcessId,
            ["pipe"] = options.PipeName,
            ["inject"] = options.Inject,
            ["no_inject"] = options.NoInject,
            ["injected_calls"] = injector.InjectedCallCount,
            ["test_pattern"] = options.TestPattern,
            ["monitor_index"] = options.MonitorIndex,
            ["rotation_degrees"] = options.Rotation,
            ["session"] = new
            {
                has_console_session = session.HasConsoleSession,
                console_session_id = session.ConsoleSessionId,
                connect_state = session.ConnectState,
                is_locked = session.IsLocked,
                user = session.UserName,
                summary = session.Summary,
                can_capture = canCapture,
                capture_block_reason = string.IsNullOrEmpty(captureBlockReason) ? null : captureBlockReason,
            },
            ["capture"] = new
            {
                description = captureDescription,
                error = captureError,
            },
            ["pipe_service"] = pipeReport,
            ["loopback"] = loopback,
        };

        // ok 的语义：本次请求的能力全部就绪。
        bool ok = true;
        if (options.TestPattern)
        {
            // 回环模式下"跳过硬编码器"不算失败（本机可能没有 MF），但失败要算失败。
            if (loopback is not null && !loopback.Skipped && !loopback.Success) ok = false;
        }
        else if (captureDescription is null)
        {
            ok = false;
        }

        status["ok"] = ok;

        WriteJsonLine(status);
        return ExitOk;
    }

    /// <summary>
    /// 常驻运行：连媒体管道，持续抓屏→编码→发送，并注入收到的输入事件。
    ///
    /// 输入环路防护在这里同样生效：若 <c>--inject</c> 且没有 <c>--no-inject</c>，
    /// 调用方在进入本方法前已拒绝启动（见 IsInjectionGuardViolation）。
    /// </summary>
    private static int RunResident(AgentOptions options)
    {
        var runtimeOptions = new AgentRuntimeOptions
        {
            MediaPipeName = options.MediaPipeName,
            MonitorIndex = options.MonitorIndex,
            Rotation = options.Rotation,
            Fps = options.Fps,
            BitrateBps = options.BitrateBps,
            PreferHardware = options.PreferHardware,
            NoInject = options.NoInject,
            UseTestPattern = options.TestPattern,
            PatternWidth = options.PatternWidth,
            PatternHeight = options.PatternHeight,
            MaxFrames = options.MaxFrames,
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            // Ctrl+C 是**正常停止**方式（Service 停止代理时也是先取消）。
            e.Cancel = true;
            cts.Cancel();
        };

        // AgentRuntime 是 IAsyncDisposable；这里是同步入口，显式等待收尾
        // （RunAsync 已返回，收尾只是关管道，不会长时间阻塞）。
        var runtime = new AgentRuntime(runtimeOptions, msg => Console.Error.WriteLine(msg));
        WriteJsonLine(new Dictionary<string, object?>
        {
            ["agent"] = AgentVersion,
            ["mode"] = "resident",
            ["ok"] = true,
            ["media_pipe"] = options.MediaPipeName,
            ["monitor_index"] = options.MonitorIndex,
            ["rotation_degrees"] = options.Rotation,
            ["fps"] = options.Fps,
            ["bitrate_bps"] = options.BitrateBps,
            ["no_inject"] = options.NoInject,
            ["test_pattern"] = options.TestPattern,
            ["max_frames"] = options.MaxFrames,
        });

        try
        {
            runtime.RunAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }

        var stats = runtime.GetStats();
        WriteJsonLine(new Dictionary<string, object?>
        {
            ["agent"] = AgentVersion,
            ["mode"] = "resident",
            ["stopped"] = true,
            ["frames_sent"] = stats.FramesSent,
            ["bytes_sent"] = stats.BytesSent,
            ["configs_sent"] = stats.ConfigsSent,
            ["input_injected"] = stats.InputEventsInjected,
            ["quality_level"] = stats.QualityLevel,
            ["encoder_backend"] = stats.EncoderBackend,
            ["frame_source"] = stats.FrameSourceDescription,
        });

        try { runtime.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); }
        catch { /* 收尾失败不影响退出码 */ }
        return ExitOk;
    }

    private static object TryPingService(string pipeName, int timeoutMs)
    {
        try
        {
            using var channel = PipeChannel.ConnectAsync(pipeName, timeoutMs).GetAwaiter().GetResult();
            var ping = channel.PingAsync().GetAwaiter().GetResult();
            var deviceInfo = channel.GetDeviceInfoAsync().GetAwaiter().GetResult();
            return new
            {
                connected = true,
                pong = ping?.Pong ?? false,
                service_version = ping?.Version,
                device_info_present = deviceInfo is not null && !string.IsNullOrEmpty(deviceInfo.Ed25519PubB64),
                error = (string?)null,
            };
        }
        catch (Exception ex)
        {
            return new
            {
                connected = false,
                pong = false,
                service_version = (string?)null,
                device_info_present = false,
                error = $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    private static void WriteJsonLine(object payload)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        Console.Out.Flush();
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("DeskLinkDesktop —— DeskLink 会话内桌面代理 (P7)");
        w.WriteLine();
        w.WriteLine("用法: DeskLink.DesktopAgent [选项]");
        w.WriteLine();
        w.WriteLine("选项:");
        w.WriteLine("  --pipe <name>         Service agent 管道名（默认 DeskLink.Agent.default）");
        w.WriteLine("  --pipe-timeout <ms>   连接管道超时（默认 2000）");
        w.WriteLine("  --inject              允许真实 SendInput 注入（必须同时给 --no-inject，否则拒绝启动）");
        w.WriteLine("  --no-inject           注入桩：所有注入调用变空操作并返回成功（自控自联调用）");
        w.WriteLine("  --test-pattern        用内置测试图源跑回环管线（源→NV12→编码→解码→像素校验），不碰真实桌面");
        w.WriteLine("  --pattern-size <WxH>  测试图尺寸（默认 320x240）");
        w.WriteLine("  --monitor <n>         选择要捕获的显示器索引（默认 0）");
        w.WriteLine("  --no-hardware-encoder 回环自检时不尝试硬件 MFT");
        w.WriteLine("  --list-monitors       列出可捕获的显示器并退出");
        w.WriteLine("  --help                显示本帮助");
        w.WriteLine();
        w.WriteLine("输出: 单行 JSON 状态到 stdout；诊断信息到 stderr。");
    }

    /// <summary>命令行选项。</summary>
    public sealed class AgentOptions
    {
        public string PipeName { get; private set; } = DefaultPipeName;
        public int PipeTimeoutMs { get; private set; } = 2000;
        public bool Inject { get; private set; }
        public bool NoInject { get; private set; }
        public bool TestPattern { get; private set; }
        public int PatternWidth { get; private set; } = 320;
        public int PatternHeight { get; private set; } = 240;
        public int MonitorIndex { get; private set; }

        /// <summary>
        /// 显示器旋转角度（0/90/180/270，P9）。旋转在编码前应用于每一帧 BGRA，
        /// 因此 90/270 会交换编码尺寸 —— 纵向显示器必须这样处理，否则远端画面横躺。
        /// </summary>
        public int Rotation { get; private set; }

        /// <summary>
        /// 常驻运行（P8/P9 收尾）：连媒体管道并持续抓屏→编码→发送，直到被取消。
        /// 不带这个开关时程序仍是"一次性自检 + 状态上报"（便于排障与 CI）。
        /// </summary>
        public bool Run { get; private set; }

        /// <summary>代理侧媒体管道名（由 Service 用 --media-pipe 告知）。</summary>
        public string MediaPipeName { get; private set; } = "DeskLink.AgentMedia.default";

        /// <summary>常驻运行的目标帧率上限。</summary>
        public int Fps { get; private set; } = 30;

        /// <summary>常驻运行的目标码率上限（自适应码率会在此基础上降档）。</summary>
        public int BitrateBps { get; private set; } = 8_000_000;

        /// <summary>最多发送多少帧后自动停止（0 = 不限）。供自检/冒烟做有界运行。</summary>
        public int MaxFrames { get; private set; }
        public bool PreferHardware { get; private set; } = true;
        public bool ListMonitors { get; private set; }
        public bool ShowHelp { get; private set; }

        public static AgentOptions Parse(string[] args)
        {
            var o = new AgentOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string? value = null;
                int eq = arg.IndexOf('=');
                if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
                {
                    value = arg[(eq + 1)..];
                    arg = arg[..eq];
                }

                switch (arg)
                {
                    case "--pipe":
                        o.PipeName = value ?? Next(args, ref i, "--pipe");
                        break;
                    case "--pipe-timeout":
                        o.PipeTimeoutMs = int.Parse(value ?? Next(args, ref i, "--pipe-timeout"));
                        break;
                    case "--inject":
                        o.Inject = true;
                        break;
                    case "--no-inject":
                        o.NoInject = true;
                        break;
                    case "--test-pattern":
                        o.TestPattern = true;
                        break;
                    case "--pattern-size":
                    {
                        var size = value ?? Next(args, ref i, "--pattern-size");
                        var parts = size.Split('x', 'X');
                        if (parts.Length != 2) throw new ArgumentException($"--pattern-size 需要 WxH 格式，收到 {size}");
                        o.PatternWidth = int.Parse(parts[0]);
                        o.PatternHeight = int.Parse(parts[1]);
                        break;
                    }
                    case "--run":
                    case "--serve":
                        o.Run = true;
                        break;
                    case "--media-pipe":
                        o.MediaPipeName = value ?? Next(args, ref i, "--media-pipe");
                        break;
                    case "--fps":
                        o.Fps = int.Parse(value ?? Next(args, ref i, "--fps"));
                        break;
                    case "--bitrate":
                        o.BitrateBps = int.Parse(value ?? Next(args, ref i, "--bitrate"));
                        break;
                    case "--max-frames":
                        o.MaxFrames = int.Parse(value ?? Next(args, ref i, "--max-frames"));
                        break;
                    case "--rotation":
                        o.Rotation = int.Parse(value ?? Next(args, ref i, "--rotation"));
                        break;
                    case "--monitor":
                        o.MonitorIndex = int.Parse(value ?? Next(args, ref i, "--monitor"));
                        break;
                    case "--no-hardware-encoder":
                        o.PreferHardware = false;
                        break;
                    case "--list-monitors":
                        o.ListMonitors = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        o.ShowHelp = true;
                        break;
                    default:
                        throw new ArgumentException($"未知参数 {arg}");
                }
            }

            if (o.PatternWidth <= 0 || o.PatternHeight <= 0)
            {
                throw new ArgumentException("--pattern-size 必须是正数");
            }
            if (o.MaxFrames < 0)
            {
                throw new ArgumentException($"--max-frames 不能为负，实际 {o.MaxFrames}");
            }
            if (o.Fps is < 1 or > 240)
            {
                throw new ArgumentException($"--fps 必须在 1..240 之间，实际 {o.Fps}");
            }
            if (o.BitrateBps < 100_000)
            {
                throw new ArgumentException($"--bitrate 至少 100000（100kbps），实际 {o.BitrateBps}");
            }
            if (!FrameRotation.IsValidAngle(o.Rotation))
            {
                // 与其他参数一致：非法取值直接抛，由上层统一转成退出码 2 + 错误消息。
                throw new ArgumentException($"--rotation 只支持 0/90/180/270，实际 {o.Rotation}");
            }
            if (o.MonitorIndex < 0)
            {
                throw new ArgumentException("--monitor 必须 >= 0");
            }
            return o;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"{flag} 缺少取值");
            return args[++i];
        }
    }
}
