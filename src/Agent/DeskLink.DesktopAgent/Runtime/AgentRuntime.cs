// 桌面代理的常驻运行循环（P8/P9 收尾）。
//
// 在这之前，代理只有"一次性自检 + 状态上报"：跑一遍回环校验、打印状态、退出。
// 真正的远程桌面需要它**常驻**：持续抓屏 → 旋转 → NV12 → H.264 编码 → 发到媒体管道；
// 同时从媒体管道收输入事件 → SendInput 注入。
//
// 结构（三层循环，各自负责一件事，便于定位问题）：
//
//   1. 连接层（RunAsync 外层）：连不上/断开就退避重连。管道是本地 IPC，
//      断开通常意味着 Service 重启或客户端换实例，重连即可恢复。
//   2. 会话层（SessionLoopAsync）：一条媒体连接上跑一条管线。
//   3. 管线层（RunPipelineAsync）：帧源 + 编码器。DXGI_ERROR_ACCESS_LOST
//      （分辨率/旋转/驱动变化）只重建这一层，**不重连管道**——
//      重连管道会让客户端以为会话断了，而实际上只是显示器模式变了。
//
// 与自适应码率的闭环：
//   代理自己看不到链路状况，只能看到本地编码耗时。真正"发不动了"的信号来自
//   Service 的转发泵（它会在客户端消费不过来时丢帧），经 MediaFlow 帧回传。
//   代理把它翻译成 QualityFeedback 喂给控制器，形成闭环。
//
// 输入坐标：媒体通道传的是**归一化千分比**（0..1000），因为控制端不知道被控端分辨率。
//   这里用**被捕获显示器**的宽高还原成像素——注入目标是那块显示器，
//   用虚拟桌面尺寸会在多显示器下算错。
using System.Diagnostics;
using System.IO.Pipes;
using DeskLink.DesktopAgent.Capture;
using DeskLink.DesktopAgent.Codec;
using DeskLink.DesktopAgent.Input;
using DeskLink.DesktopAgent.Quality;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;

namespace DeskLink.DesktopAgent.Runtime;

/// <summary>常驻运行参数。</summary>
public sealed record AgentRuntimeOptions
{
    /// <summary>Service 的代理侧媒体管道名（由 Service 用 <c>--media-pipe</c> 告知）。</summary>
    public string MediaPipeName { get; init; } = "DeskLink.AgentMedia.default";

    public int MonitorIndex { get; init; }
    public int Rotation { get; init; }

    /// <summary>目标帧率上限（实际可能更低：编码跟不上时会自然降下来）。</summary>
    public int Fps { get; init; } = 30;

    public int BitrateBps { get; init; } = 8_000_000;
    public bool PreferHardware { get; init; }
    public bool NoInject { get; init; }

    /// <summary>用测试图源替代真实抓屏（无头自检 / CI）。</summary>
    public bool UseTestPattern { get; init; }
    public int PatternWidth { get; init; } = 1280;
    public int PatternHeight { get; init; } = 720;

    public int ConnectTimeoutMs { get; init; } = 3000;
    public int ReconnectDelayMs { get; init; } = 1000;

    /// <summary>访问丢失后重建管线的等待时间。</summary>
    public int RebuildDelayMs { get; init; } = 300;

    /// <summary>会话统计上报间隔。</summary>
    public TimeSpan StatsInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 最多发送多少帧后自动停止（0 = 不限）。
    ///
    /// 存在的意义：自检/冒烟需要一个**有界**的运行——否则代理常驻不退出，
    /// 测试侧只能靠超时兜底（既慢又不确定）。设了上限后，达到帧数即关闭管道，
    /// 对端读到 EOF，链路自然收敛。
    /// </summary>
    public int MaxFrames { get; init; }
}

/// <summary>运行统计（供状态上报/测试断言）。</summary>
public readonly record struct AgentRuntimeStats(
    long FramesSent,
    long BytesSent,
    long ConfigsSent,
    long InputEventsInjected,
    long InjectionDenied,
    long FramesDroppedByService,
    int QualityLevel,
    string QualityLabel,
    string EncoderBackend,
    string FrameSourceDescription);

/// <summary>管线需要重建（典型：DXGI ACCESS_LOST）。不重连管道，只重建帧源与编码器。</summary>
internal sealed class PipelineRebuildException : Exception
{
    public PipelineRebuildException(string message) : base(message) { }
}

/// <summary>桌面代理的常驻运行循环。</summary>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly AgentRuntimeOptions _options;
    private readonly Action<string>? _log;
    private readonly InputInjector _injector;
    private readonly AdaptiveBitrateController _quality;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private NamedPipeClientStream? _stream;

    private long _framesSent;
    private volatile bool _maxFramesReached;
    private long _bytesSent;
    private long _configsSent;
    private long _injected;
    private long _lastDroppedReported;
    private long _lastInputDroppedReported;
    private long _framesSinceFeedback;
    private double _encodeMsEma;
    /// <summary>
    /// 本窗口内是否有输入事件被 Service 丢弃。
    ///
    /// 这是"输入开始不跟手"的直接信号（DESIGN 要求优先保输入响应），
    /// 由 MediaFlow 反馈驱动；代理据此更激进地降画质，用画面换输入。
    /// </summary>
    private volatile bool _inputPending;
    private string _encoderBackend = "n/a";
    private string _sourceDescription = "n/a";

    /// <summary>帧源工厂（测试可替换；默认按 <see cref="AgentRuntimeOptions.UseTestPattern"/> 选择）。</summary>
    public Func<AgentRuntimeOptions, IFrameSource?>? FrameSourceFactory { get; set; }

    public AgentRuntime(AgentRuntimeOptions options, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
        _injector = new InputInjector(options.NoInject);
        _quality = new AdaptiveBitrateController(new AdaptiveBitrateController.Options
        {
            MaxBitrateBps = options.BitrateBps,
            MaxFps = options.Fps,
            MinFps = Math.Max(5, options.Fps / 3),
            MinBitrateBps = Math.Max(200_000, options.BitrateBps / 20),
            MinScale = 0.5,
        });
    }

    /// <summary>当前统计快照。</summary>
    public AgentRuntimeStats GetStats() => new(
        FramesSent: Interlocked.Read(ref _framesSent),
        BytesSent: Interlocked.Read(ref _bytesSent),
        ConfigsSent: Interlocked.Read(ref _configsSent),
        InputEventsInjected: Interlocked.Read(ref _injected),
        InjectionDenied: _injector.DeniedCount,
        FramesDroppedByService: Interlocked.Read(ref _lastDroppedReported),
        QualityLevel: _quality.Level,
        QualityLabel: _quality.Current.Label,
        EncoderBackend: _encoderBackend,
        FrameSourceDescription: _sourceDescription);

    /// <summary>
    /// 常驻运行直到取消。内部自行处理重连与管线重建，**不会**因为一次失败而退出。
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;

        while (!token.IsCancellationRequested)
        {
            if (_maxFramesReached) break;

            try
            {
                await ConnectAsync(token).ConfigureAwait(false);
                _log?.Invoke($"AgentRuntime: 已连接媒体管道 {_options.MediaPipeName}");

                // 会话层：同一条连接上可能因 ACCESS_LOST 重建多次管线。
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await RunPipelineAsync(token).ConfigureAwait(false);
                        // 正常返回 = 对端关闭了管道 → 跳出重连。
                        break;
                    }
                    catch (PipelineRebuildException ex)
                    {
                        _log?.Invoke($"AgentRuntime: 重建管线（{ex.Message}）");
                        await DelayAsync(_options.RebuildDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log?.Invoke($"AgentRuntime: 会话结束 {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                await CloseStreamAsync().ConfigureAwait(false);
            }

            if (token.IsCancellationRequested) break;
            _log?.Invoke($"AgentRuntime: {_options.ReconnectDelayMs}ms 后重连");
            await DelayAsync(_options.ReconnectDelayMs, token).ConfigureAwait(false);
        }

        _log?.Invoke("AgentRuntime: 已停止");
    }

    private static async Task DelayAsync(int ms, CancellationToken ct)
    {
        try { await Task.Delay(ms, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await CloseStreamAsync().ConfigureAwait(false);
        var stream = new NamedPipeClientStream(".", _options.MediaPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(_options.ConnectTimeoutMs, ct).ConfigureAwait(false);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        _stream = stream;
    }

    private async Task CloseStreamAsync()
    {
        var s = _stream;
        _stream = null;
        if (s is null) return;
        try { await s.DisposeAsync().ConfigureAwait(false); } catch { /* 收尾 */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 管线
    // ────────────────────────────────────────────────────────────────────────

    private async Task RunPipelineAsync(CancellationToken ct)
    {
        var source = CreateFrameSource();
        if (source is null)
        {
            // 拿不到帧源（典型：会话已锁定 / 没有可捕获的显示器）。
            // 关键点：抛 PipelineRebuildException 而**不是**普通异常 ——
            // 外层只重建管线、**不重连管道**，因此 Service 侧仍看到"代理已连接"，
            // 客户端可以据此显示"等待本地登录"而不是"代理掉线"。
            // （DESIGN：锁屏时不尝试抓屏，显示等待本地登录。）
            throw new PipelineRebuildException(
                "无法创建帧源：桌面可能已锁定或没有可捕获的显示器；保持连接并等待本地登录");
        }
        using var sourceScope = source;
        _sourceDescription = source.Description;

        var (orientedW, orientedH) = FrameRotation.RotatedSize(source.Width, source.Height, _options.Rotation);
        if ((orientedW & 1) != 0 || (orientedH & 1) != 0)
        {
            // NV12 需要偶数边；奇数边直接说明原因，而不是让 MFT 报一个难懂的错。
            throw new InvalidOperationException(
                $"旋转后尺寸 {orientedW}x{orientedH} 含奇数边，NV12 无法编码");
        }

        // 输入坐标反归一化基准：物理源尺寸 + 旋转后尺寸（管线重建时刷新，
        // 显示器模式变化后 source 尺寸可能变）。
        Volatile.Write(ref _sourceWidth, Math.Max(1, source.Width));
        Volatile.Write(ref _sourceHeight, Math.Max(1, source.Height));
        Volatile.Write(ref _orientedWidth, Math.Max(1, orientedW));
        Volatile.Write(ref _orientedHeight, Math.Max(1, orientedH));

        H264Encoder? encoder = null;
        (int W, int H, int Fps, int Bitrate)? encoderKey = null;
        uint frameSeq = 0;
        var frameIntervalMs = 1000.0 / Math.Max(1, _options.Fps);
        var sw = Stopwatch.StartNew();
        long frameIndex = 0;
        var lastStatsAt = sw.Elapsed;

        // 输入/反馈读循环与编码循环并发；读循环结束时（对端关闭）取消本会话。
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readLoop = Task.Run(() => ReadLoopAsync(sessionCts.Token), sessionCts.Token);

        try
        {
            while (!sessionCts.IsCancellationRequested)
            {
                // —— 帧率节流：按"目标第 N 帧应在的时刻"等待，落后时不等待（自然降帧）——
                var targetMs = frameIndex * frameIntervalMs;
                var nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < targetMs)
                {
                    await DelayAsync((int)(targetMs - nowMs), sessionCts.Token).ConfigureAwait(false);
                }
                frameIndex++;

                // —— 取帧 ——
                if (!source.TryGetFrame(timeoutMs: 100, out var frame, out var accessLost, out var frameError))
                {
                    if (accessLost)
                    {
                        // 不是错误：显示器模式变了。只重建管线，不重连管道。
                        throw new PipelineRebuildException("DXGI ACCESS_LOST");
                    }
                    if (frameError is not null)
                    {
                        _log?.Invoke($"AgentRuntime: 取帧失败 {frameError}");
                    }
                    continue; // 桌面静止时没有新帧是正常的
                }
                if (frame is null) continue;

                // —— 按当前质量档位决定编码参数；尺寸/帧率/码率变化时重建编码器 ——
                var scale = _quality.Current.Scale;
                var desiredW = MakeEven((int)Math.Round(orientedW * scale));
                var desiredH = MakeEven((int)Math.Round(orientedH * scale));
                var desiredKey = (desiredW, desiredH, _quality.Current.Fps, _quality.Current.BitrateBps);

                if (encoderKey != desiredKey || encoder is null)
                {
                    encoder?.Dispose();
                    var settings = new H264EncoderSettings(
                        desiredW, desiredH,
                        Fps: _quality.Current.Fps,
                        BitrateBps: _quality.Current.BitrateBps,
                        PreferHardware: _options.PreferHardware,
                        KeyFrameInterval: _quality.Current.Fps);
                    encoder = H264Encoder.TryCreate(settings, out var encoderError);
                    if (encoder is null)
                    {
                        throw new InvalidOperationException($"H.264 编码器不可用：{encoderError}");
                    }
                    encoderKey = desiredKey;
                    _encoderBackend = encoder.BackendName;

                    // 编码器变了必须重发配置：客户端要据此重建解码器与画面尺寸。
                    await SendConfigAsync(desiredW, desiredH, encoder).ConfigureAwait(false);
                    _log?.Invoke($"AgentRuntime: 编码器 {encoder.BackendName} {desiredW}x{desiredH} " +
                                 $"fps={_quality.Current.Fps} bitrate={_quality.Current.BitrateBps} " +
                                 $"(档位 {_quality.Level} {_quality.Current.Label})");
                }

                // —— 旋转 → NV12 → 编码 ——
                var oriented = FrameRotation.Rotate(frame, _options.Rotation);
                var scaled = ScaleIfNeeded(oriented, desiredW, desiredH);
                var nv12 = FrameConverter.BgraToNv12Cpu(scaled.Pixels, scaled.Width, scaled.Height, scaled.Stride);

                var encodeStart = Stopwatch.GetTimestamp();
                var au = encoder.Encode(nv12, forceKeyFrame: frameSeq == 0);
                var encodeMs = (Stopwatch.GetTimestamp() - encodeStart) * 1000.0 / Stopwatch.Frequency;
                // 指数滑动平均：单帧尖峰不该立刻触发降级。
                _encodeMsEma = _encodeMsEma == 0 ? encodeMs : _encodeMsEma * 0.8 + encodeMs * 0.2;

                if (au is not null && au.Length > 0)
                {
                    var ptsMicros = (ulong)(sw.Elapsed.TotalMilliseconds * 1000);
                    await SendAccessUnitAsync(au, frameSeq, ptsMicros, sessionCts.Token).ConfigureAwait(false);
                    frameSeq++;
                }

                // 达到帧数上限：结束整个运行（不是重建管线），让管道关闭、对端读到 EOF。
                if (_maxFramesReached)
                {
                    _log?.Invoke($"AgentRuntime: 已达帧数上限 {_options.MaxFrames}，停止");
                    return;
                }

                // —— 定期上报统计 + 驱动自适应码率 ——
                if (sw.Elapsed - lastStatsAt >= _options.StatsInterval)
                {
                    var elapsed = sw.Elapsed - lastStatsAt;
                    lastStatsAt = sw.Elapsed;
                    await SendStatsAsync(elapsed).ConfigureAwait(false);

                    // 本地可见的压力：编码耗时 + 输入积压。
                    // 链路侧的压力由 Service 的 MediaFlow 帧补充（见 HandleFlowFeedback）。
                    var feedback = new QualityFeedback(
                        RttMs: 0,                 // 代理侧不知道 RTT（未来由控制端回包提供）
                        LossPercent: 0,           // 同上；丢帧率由 MediaFlow 单独喂入
                        EncodeMsPerFrame: _encodeMsEma,
                        QueueDepth: 0,            // 链路队列深度同样来自 MediaFlow
                        InputPending: _inputPending);
                    _quality.Update(feedback, elapsed);
                    // 本窗口的输入压力已消费掉；下一份 MediaFlow 会重新置位。
                    _inputPending = false;
                }
            }

            // 会话结束（读循环发现对端关闭）：把编码器缓冲吐出来，尽量不丢尾部画面。
            if (encoder is not null)
            {
                foreach (var tail in encoder.FlushChunks())
                {
                    await SendAccessUnitAsync(tail, frameSeq++, 0, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            sessionCts.Cancel();
            try { await readLoop.ConfigureAwait(false); } catch { /* 收尾 */ }
            encoder?.Dispose();
        }
    }

    private IFrameSource? CreateFrameSource()
    {
        if (FrameSourceFactory is not null) return FrameSourceFactory(_options);
        if (_options.UseTestPattern) return new TestPatternSource(_options.PatternWidth, _options.PatternHeight);

        var capture = DuplicationCapture.TryCreate(_options.MonitorIndex, out var error);
        if (capture is null) _log?.Invoke($"AgentRuntime: 无法创建桌面捕获：{error}");
        return capture;
    }

    /// <summary>质量降档会缩小编码尺寸：这里做一次最近邻缩放（比重新抓屏更省）。</summary>
    private static BgraFrame ScaleIfNeeded(BgraFrame frame, int width, int height)
    {
        if (frame.Width == width && frame.Height == height) return frame;

        var dst = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Min(frame.Height - 1, y * frame.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Min(frame.Width - 1, x * frame.Width / width);
                var si = sy * frame.Stride + sx * 4;
                var di = (y * width + x) * 4;
                dst[di + 0] = frame.Pixels[si + 0];
                dst[di + 1] = frame.Pixels[si + 1];
                dst[di + 2] = frame.Pixels[si + 2];
                dst[di + 3] = frame.Pixels[si + 3];
            }
        }
        return new BgraFrame(width, height, width * 4, dst, frame.TimestampMs);
    }

    private static int MakeEven(int v) => v % 2 == 0 ? v : Math.Max(2, v - 1);

    // ────────────────────────────────────────────────────────────────────────
    // 读循环：输入事件 + 链路反馈
    // ────────────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return;

        var reader = new MediaFrameReader();
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) return; // 对端关闭

                reader.Append(buffer.AsSpan(0, n));
                while (reader.TryRead(out var type, out var payload))
                {
                    DispatchInbound(type, payload);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log?.Invoke($"AgentRuntime: 读循环结束 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DispatchInbound(ProtocolConstants.FrameType type, byte[] payload)
    {
        try
        {
            switch (type)
            {
                case ProtocolConstants.FrameType.InputMouseMove:
                {
                    var (x, y) = InputEventCodec.DecodeMouseMove(payload);
                    // 千分比坐标定义在"客户端看到的画面"上 = 旋转后方向、
                    // 与编码缩放无关（缩放只影响画质，不影响千分比语义）。
                    // 注入需要**物理屏幕**像素坐标：先反归一化到旋转坐标系，
                    // 再按当前旋转角反旋转（映射公式为 FrameRotation.Rotate 的逆）。
                    var rw = Math.Max(1, Volatile.Read(ref _orientedWidth));
                    var rh = Math.Max(1, Volatile.Read(ref _orientedHeight));
                    var sw = Math.Max(1, Volatile.Read(ref _sourceWidth));
                    var sh = Math.Max(1, Volatile.Read(ref _sourceHeight));
                    var rx = x * rw / 1000;
                    var ry = y * rh / 1000;
                    int px, py;
                    switch (_options.Rotation)
                    {
                        case 90:  px = ry;           py = sh - 1 - rx; break;
                        case 180: px = sw - 1 - rx;  py = sh - 1 - ry; break;
                        case 270: px = sw - 1 - ry;  py = rx;          break;
                        default:  px = rx;           py = ry;          break;
                    }
                    _injector.MoveMouse(
                        Math.Clamp(px, 0, sw - 1), Math.Clamp(py, 0, sh - 1), sw, sh);
                    Interlocked.Increment(ref _injected);
                    break;
                }
                case ProtocolConstants.FrameType.InputMouseButton:
                {
                    var (button, down) = InputEventCodec.DecodeMouseButton(payload);
                    var kind = button switch
                    {
                        1 => MouseButtonKind.Right,
                        2 => MouseButtonKind.Middle,
                        _ => MouseButtonKind.Left,
                    };
                    _injector.MouseButton(kind, down);
                    Interlocked.Increment(ref _injected);
                    break;
                }
                case ProtocolConstants.FrameType.InputWheel:
                    _injector.Wheel(InputEventCodec.DecodeWheel(payload));
                    Interlocked.Increment(ref _injected);
                    break;
                case ProtocolConstants.FrameType.InputKey:
                {
                    var (scan, extended, down) = InputEventCodec.DecodeKey(payload);
                    _injector.KeyScan(scan, down, extended);
                    Interlocked.Increment(ref _injected);
                    break;
                }
                case ProtocolConstants.FrameType.MediaFlow:
                {
                    var flow = MediaFlowPayload.Decode(payload);
                    HandleFlowFeedback(flow);
                    break;
                }
                default:
                    // 其它类型（例如客户端误发）忽略，不打断会话。
                    break;
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"AgentRuntime: 处理入站帧 0x{(byte)type:x2} 失败 {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把 Service 回报的背压翻译成自适应码率的输入。
    ///
    /// 这是闭环的关键：代理自己看不到"往客户端发不动"，只有转发泵知道。
    /// 丢帧率按"本窗口内发送帧数 + 丢弃帧数"计算，因此丢得越多档位降得越快。
    /// </summary>
    private void HandleFlowFeedback(MediaFlowPayload flow)
    {
        var totalDropped = flow.DroppedFrames;
        var previous = Interlocked.Read(ref _lastDroppedReported);
        var deltaDropped = Math.Max(0, totalDropped - previous);
        Interlocked.Exchange(ref _lastDroppedReported, totalDropped);

        // 输入被丢弃 → 置位"输入积压"，让控制器把压力算上（下一个统计节拍后清除）。
        var totalInputDropped = flow.DroppedInput;
        var previousInput = Interlocked.Read(ref _lastInputDroppedReported);
        if (totalInputDropped > previousInput) _inputPending = true;
        Interlocked.Exchange(ref _lastInputDroppedReported, totalInputDropped);

        var sent = Interlocked.Read(ref _framesSinceFeedback);
        Interlocked.Exchange(ref _framesSinceFeedback, 0);

        var denominator = sent + deltaDropped;
        var lossPercent = denominator > 0 ? deltaDropped * 100.0 / denominator : 0;

        var feedback = new QualityFeedback(
            RttMs: 0,
            LossPercent: lossPercent,
            EncodeMsPerFrame: _encodeMsEma,
            QueueDepth: (int)Math.Min(int.MaxValue, flow.ClientQueueDepth),
            InputPending: _inputPending);

        // 反馈到达时立即评估（不等 1 秒的统计节拍），让降档更快。
        _quality.Update(feedback, TimeSpan.FromSeconds(0.5));
    }

    // 输入坐标反归一化基准（读循环线程读、管线线程写，int 写原子 + Volatile 保证可见）。
    // 物理捕获尺寸 = 注入坐标基准；oriented = 旋转后（客户端画面方向）尺寸。
    private int _sourceWidth = 1;
    private int _sourceHeight = 1;
    private int _orientedWidth = 1;
    private int _orientedHeight = 1;

    // ────────────────────────────────────────────────────────────────────────
    // 出站
    // ────────────────────────────────────────────────────────────────────────

    private async Task SendConfigAsync(int width, int height, H264Encoder encoder)
    {
        // 注意：这里不能把编码尺寸（缩放后）写进输入反归一化基准——
        // 千分比坐标与编码尺寸无关，鼠标坐标换算用物理源尺寸（见 DispatchInbound）。
        var payload = new DesktopConfigPayload(
            Width: (ushort)Math.Min(ushort.MaxValue, width),
            Height: (ushort)Math.Min(ushort.MaxValue, height),
            Codec: DesktopConfigPayload.CodecH264,
            Backend: encoder.IsHardware ? DesktopConfigPayload.BackendHardware : DesktopConfigPayload.BackendSoftware,
            Fps: (ushort)Math.Min(ushort.MaxValue, _quality.Current.Fps),
            BitrateBps: (uint)Math.Max(0, _quality.Current.BitrateBps),
            MonitorIndex: (ushort)Math.Min(ushort.MaxValue, Math.Max(0, _options.MonitorIndex)),
            RotationDegrees: (byte)_options.Rotation).Encode();

        await SendFrameAsync(ProtocolConstants.FrameType.DesktopConfig, payload, CancellationToken.None)
            .ConfigureAwait(false);
        Interlocked.Increment(ref _configsSent);
    }

    private async Task SendStatsAsync(TimeSpan window)
    {
        var frames = Interlocked.Read(ref _framesSent);
        var bytes = Interlocked.Read(ref _bytesSent);
        // 用"上次窗口内的增量"估算瞬时 fps/kbps，比累计平均更能反映当前状态。
        var fps = window.TotalSeconds > 0 ? _lastWindowFrames / window.TotalSeconds : 0;
        var kbps = window.TotalSeconds > 0 ? _lastWindowBytes * 8 / 1000.0 / window.TotalSeconds : 0;
        _lastWindowFrames = 0;
        _lastWindowBytes = 0;

        var payload = new SessionStatsPayload(
            FpsX10: (uint)Math.Max(0, Math.Round(fps * 10)),
            Kbps: (uint)Math.Max(0, Math.Round(kbps)),
            // -1 = 代理侧未知（真实 RTT 需要控制端回包，属后续工作）。
            RttMs: -1,
            Degraded: _quality.Degraded).Encode();

        await SendFrameAsync(ProtocolConstants.FrameType.SessionControl, payload, CancellationToken.None)
            .ConfigureAwait(false);

        _ = frames;
        _ = bytes;
    }

    private long _lastWindowFrames;
    private long _lastWindowBytes;

    private async Task SendAccessUnitAsync(byte[] accessUnit, uint seq, ulong ptsMicros, CancellationToken ct)
    {
        // 单帧负载上限受 u16 约束，1080p 关键帧远超此值 → 必须按访问单元分片。
        const int maxData = MediaChannelFraming.MaxPayloadSize - DesktopVideoChunk.FixedSize;
        var offset = 0;
        while (offset < accessUnit.Length)
        {
            var len = Math.Min(maxData, accessUnit.Length - offset);
            var isLast = offset + len >= accessUnit.Length;
            var chunk = new DesktopVideoChunk(
                FrameSeq: seq,
                PtsMicros: ptsMicros,
                IsKeyFrame: offset == 0 && AnnexB.ContainsKeyFrame(accessUnit),
                IsLastFragment: isLast,
                Data: accessUnit.AsSpan(offset, len).ToArray());

            await SendFrameAsync(ProtocolConstants.FrameType.DesktopVideo, chunk.Encode(), ct).ConfigureAwait(false);
            offset += len;
        }

        Interlocked.Increment(ref _framesSent);
        Interlocked.Add(ref _bytesSent, accessUnit.Length);
        if (_options.MaxFrames > 0 && Interlocked.Read(ref _framesSent) >= _options.MaxFrames)
        {
            _maxFramesReached = true;
        }
        Interlocked.Increment(ref _lastWindowFrames);
        Interlocked.Add(ref _lastWindowBytes, accessUnit.Length);
        Interlocked.Increment(ref _framesSinceFeedback);
    }

    private async Task SendFrameAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken ct)
    {
        var stream = _stream ?? throw new IOException("媒体管道未连接");
        var frame = MediaChannelFraming.Encode(type, payload);

        // 命名管道是字节流：并发写会把帧撕开，必须串行化。
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        await CloseStreamAsync().ConfigureAwait(false);
        _cts.Dispose();
        _writeGate.Dispose();
    }
}
