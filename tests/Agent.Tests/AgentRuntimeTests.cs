using System.IO.Pipes;
using DeskLink.DesktopAgent;
using DeskLink.DesktopAgent.Codec;
using DeskLink.DesktopAgent.Runtime;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;

namespace DeskLink.Agent.Tests;

/// <summary>
/// P8/P9 收尾：桌面代理的**常驻运行循环**。
///
/// 用测试图源 + 真命名管道跑真实链路：代理连上管道 → 编码 → 分片发送；
/// 测试侧作为"Service"收帧，并把输入事件与背压反馈发回去。
/// 这样验证的是"代理真的会持续产出可解码的码流并处理输入"，而不是只跑通一个函数。
///
/// Media Foundation 不可用时相关用例 Skip（绝不用占位断言假装通过）。
/// </summary>
public class AgentRuntimeTests
{
    /// <summary>测试侧扮演 Service 的代理侧媒体管道。</summary>
    private sealed class FakeServicePipe : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly MediaFrameReader _reader = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly byte[] _buffer = new byte[64 * 1024];
        private readonly CancellationTokenSource _cts = new();

        private FakeServicePipe(NamedPipeServerStream stream) => _stream = stream;

        public static FakeServicePipe Create(string pipeName)
            => new(new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 64 * 1024, 64 * 1024));

        public Task WaitForAgentAsync(int timeoutMs = 10000)
            => _stream.WaitForConnectionAsync(new CancellationTokenSource(timeoutMs).Token);

        public async Task SendAsync(ProtocolConstants.FrameType type, ReadOnlyMemory<byte> payload)
        {
            var frame = MediaChannelFraming.Encode(type, payload.Span);
            await _writeGate.WaitAsync();
            try
            {
                await _stream.WriteAsync(frame, _cts.Token);
                await _stream.FlushAsync(_cts.Token);
            }
            finally { _writeGate.Release(); }
        }

        public async Task<(ProtocolConstants.FrameType Type, byte[] Payload)?> ReadFrameAsync(int timeoutMs = 5000)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            cts.CancelAfter(timeoutMs);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (_reader.TryRead(out var type, out var payload)) return (type, payload);
                    int n = await _stream.ReadAsync(_buffer, cts.Token);
                    if (n == 0) return null;
                    _reader.Append(_buffer.AsSpan(0, n));
                }
            }
            catch (OperationCanceledException) { }
            return null;
        }

        /// <summary>等到收到指定类型的一帧（丢弃其它类型）。</summary>
        public async Task<byte[]?> WaitForFrameAsync(ProtocolConstants.FrameType wanted, int timeoutMs = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var frame = await ReadFrameAsync(timeoutMs: 500);
                if (frame is null) continue;
                if (frame.Value.Type == wanted) return frame.Value.Payload;
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _stream.DisposeAsync();
            _writeGate.Dispose();
            _cts.Dispose();
        }
    }

    private static string NewPipeName() => $"DeskLink.Test.AgentMedia.{Guid.NewGuid():N}";

    private static AgentRuntimeOptions TestOptions(string pipeName, int rotation = 0) => new()
    {
        MediaPipeName = pipeName,
        UseTestPattern = true,
        PatternWidth = 320,
        PatternHeight = 240,
        Rotation = rotation,
        Fps = 15,                 // 测试里不需要满帧率
        BitrateBps = 2_000_000,
        NoInject = true,          // 绝不真的注入（会动到开发桌面）
        ConnectTimeoutMs = 2000,
        ReconnectDelayMs = 200,
        StatsInterval = TimeSpan.FromMilliseconds(200),
    };

    private static void RequireEncoder()
    {
        var probe = H264Encoder.TryCreate(
            new H264EncoderSettings(320, 240, 15, 1_000_000, PreferHardware: false, KeyFrameInterval: 15),
            out var error);
        if (probe is null) Skip.If(true, $"Media Foundation H.264 编码器不可用：{error}");
        probe!.Dispose();
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_EmitsConfigAndDecodableAccessUnits()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();

        // 1) 必须先收到配置帧（客户端据此建解码器）。
        var configPayload = await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig);
        Assert.NotNull(configPayload);
        var config = DesktopConfigPayload.Decode(configPayload!);
        Assert.Equal(320, config.Width);
        Assert.Equal(240, config.Height);
        Assert.Equal(DesktopConfigPayload.CodecH264, config.Codec);
        Assert.Equal(15, config.Fps);

        // 2) 码流：把分片重组回完整访问单元，并断言首个 AU 带 SPS/PPS + IDR。
        var assembler = new Assembler();
        byte[]? firstAu = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && firstAu is null)
        {
            var payload = await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopVideo, timeoutMs: 5000);
            if (payload is null) break;
            var au = assembler.Add(DesktopVideoChunk.Decode(payload));
            if (au is not null) firstAu = au;
        }

        Assert.NotNull(firstAu);
        Assert.True(AnnexB.ContainsNalType(firstAu!, AnnexB.NalTypeSps), "首个访问单元必须含 SPS");
        Assert.True(AnnexB.ContainsNalType(firstAu!, AnnexB.NalTypePps), "首个访问单元必须含 PPS");
        Assert.True(AnnexB.ContainsKeyFrame(firstAu!), "首个访问单元必须是 IDR");

        cts.Cancel();
        await run;
    }

    /// <summary>把分片重组为访问单元（与客户端里的 VideoFragmentAssembler 同构）。</summary>
    private sealed class Assembler
    {
        private readonly MemoryStream _buf = new();
        private uint _seq;
        private bool _has;

        public byte[]? Add(DesktopVideoChunk chunk)
        {
            if (_has && chunk.FrameSeq != _seq) { _buf.SetLength(0); _has = false; }
            if (!_has) { _seq = chunk.FrameSeq; _has = true; }
            _buf.Write(chunk.Data, 0, chunk.Data.Length);
            if (!chunk.IsLastFragment) return null;
            var data = _buf.ToArray();
            _buf.SetLength(0);
            _has = false;
            return data;
        }
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_HandlesInputEvents_WithoutInjectingWhenNoInject()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();
        Assert.NotNull(await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig));

        await service.SendAsync(ProtocolConstants.FrameType.InputMouseMove, InputEventCodec.EncodeMouseMove(500, 500));
        await service.SendAsync(ProtocolConstants.FrameType.InputKey, InputEventCodec.EncodeKey(0x1C, false, true));
        await service.SendAsync(ProtocolConstants.FrameType.InputMouseButton, InputEventCodec.EncodeMouseButton(0, true));
        await service.SendAsync(ProtocolConstants.FrameType.InputWheel, InputEventCodec.EncodeWheel(120));

        // 运行时必须把事件交给注入器；--no-inject 下注入器是空操作，
        // 但计数会增长（证明事件确实被处理，而不是被静默丢弃）。
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && runtime.GetStats().InputEventsInjected < 4)
        {
            await Task.Delay(50);
        }

        var stats = runtime.GetStats();
        Assert.True(stats.InputEventsInjected >= 4, $"应处理至少 4 个输入事件，实际 {stats.InputEventsInjected}");
        Assert.Equal(0, stats.InjectionDenied);

        cts.Cancel();
        await run;
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_EmitsSessionStats()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();

        var payload = await service.WaitForFrameAsync(ProtocolConstants.FrameType.SessionControl, timeoutMs: 20000);
        Assert.NotNull(payload);
        Assert.Equal(SessionStatsPayload.Size, payload!.Length);
        var stats = SessionStatsPayload.Decode(payload);
        // 帧率/码率是实测值（可能为 0，取决于采样窗口），但结构必须能解出来。
        Assert.True(stats.FpsX10 <= 15 * 10 + 5, $"fps 上报异常：{stats.FpsX10 / 10.0}");

        cts.Cancel();
        await run;
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_Rotation90_ReportsSwappedDimensions()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName, rotation: 90));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();

        var payload = await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig);
        Assert.NotNull(payload);
        var config = DesktopConfigPayload.Decode(payload!);
        Assert.Equal(240, config.Width);   // 320x240 旋转 90° → 240x320
        Assert.Equal(320, config.Height);
        Assert.Equal(90, config.RotationDegrees);

        cts.Cancel();
        await run;
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_StopsCleanly_OnCancellation()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource();
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();
        Assert.NotNull(await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig));

        cts.Cancel();
        // 取消后必须**正常返回**（不是抛异常、也不是挂住）。
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(runtime.GetStats().FramesSent >= 1);
    }

    /// <summary>
    /// 管道晚于代理启动时才出现：代理必须自行重连（退避重试），而不是直接退出。
    /// 这对应真实场景——Service 重启或代理先于 Service 就绪。
    /// </summary>
    [SkippableFact(Timeout = 60000)]
    public async Task Resident_Reconnects_WhenPipeAppearsLate()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);

        // 先让代理经历几次连接失败。
        await Task.Delay(700);

        await using var service = FakeServicePipe.Create(pipeName);
        await service.WaitForAgentAsync(timeoutMs: 15000);

        var config = await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig, timeoutMs: 15000);
        Assert.NotNull(config);

        cts.Cancel();
        await run;
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_AppliesFlowFeedback_ByDegradingQuality()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();
        Assert.NotNull(await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig));

        Assert.Equal(0, runtime.GetStats().QualityLevel);

        // 连发多轮"大量丢帧"反馈：自适应控制器应据此降档。
        for (var i = 1; i <= 12; i++)
        {
            await service.SendAsync(ProtocolConstants.FrameType.MediaFlow,
                new MediaFlowPayload(DroppedFrames: (uint)(i * 100), ClientQueueDepth: 100, DroppedInput: 0).Encode());
            await Task.Delay(60);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && runtime.GetStats().QualityLevel == 0)
        {
            await Task.Delay(50);
        }

        var stats = runtime.GetStats();
        Assert.True(stats.QualityLevel > 0,
            $"持续丢帧反馈后应降档，实际档位 {stats.QualityLevel}（{stats.QualityLabel}）");

        cts.Cancel();
        await run;
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_InputDropFeedback_DegradesQuality()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();
        Assert.NotNull(await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig));

        // 只有"输入被丢弃"（DroppedInput 增长）而画面没有丢：DESIGN 要求优先保输入响应，
        // 因此这同样必须触发降档。
        for (var i = 1; i <= 12; i++)
        {
            await service.SendAsync(ProtocolConstants.FrameType.MediaFlow,
                new MediaFlowPayload(DroppedFrames: 0, ClientQueueDepth: 0, DroppedInput: (uint)i).Encode());
            await Task.Delay(60);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && runtime.GetStats().QualityLevel == 0)
        {
            await Task.Delay(50);
        }

        Assert.True(runtime.GetStats().QualityLevel > 0, "输入被丢弃时应降档（牺牲画质保输入）");

        cts.Cancel();
        await run;
    }

    [SkippableFact(Timeout = 60000)]
    public async Task Resident_IgnoresUnknownInboundFrames()
    {
        RequireEncoder();

        var pipeName = NewPipeName();
        await using var service = FakeServicePipe.Create(pipeName);
        await using var runtime = new AgentRuntime(TestOptions(pipeName));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = runtime.RunAsync(cts.Token);
        await service.WaitForAgentAsync();
        Assert.NotNull(await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopConfig));

        // 未知/不该来的帧（例如客户端误发的画面帧）不能让代理崩掉或断链。
        await service.SendAsync(ProtocolConstants.FrameType.DesktopVideo, new byte[] { 1, 2, 3 });
        await service.SendAsync(ProtocolConstants.FrameType.MediaFlow, new byte[] { 0xFF });

        // 链路仍然健康：还能继续收到画面。
        var payload = await service.WaitForFrameAsync(ProtocolConstants.FrameType.DesktopVideo, timeoutMs: 15000);
        Assert.NotNull(payload);

        cts.Cancel();
        await run;
    }

    // ── CLI ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cli_Run_And_MediaPipe_Parse()
    {
        var options = Program.AgentOptions.Parse(new[]
        {
            "--run", "--no-inject", "--media-pipe", "DeskLink.AgentMedia.a", "--fps", "24", "--bitrate", "4000000",
        });

        Assert.True(options.Run);
        Assert.True(options.NoInject);
        Assert.Equal("DeskLink.AgentMedia.a", options.MediaPipeName);
        Assert.Equal(24, options.Fps);
        Assert.Equal(4_000_000, options.BitrateBps);
    }

    [Fact]
    public void Cli_Serve_IsAliasForRun()
        => Assert.True(Program.AgentOptions.Parse(new[] { "--serve", "--no-inject" }).Run);

    [Fact]
    public void Cli_Resident_Defaults()
    {
        var options = Program.AgentOptions.Parse(Array.Empty<string>());
        Assert.False(options.Run);
        Assert.Equal(30, options.Fps);
        Assert.Equal(8_000_000, options.BitrateBps);
        Assert.Equal("DeskLink.AgentMedia.default", options.MediaPipeName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("500")]
    public void Cli_RejectsOutOfRangeFps(string fps)
        => Assert.Throws<ArgumentException>(() =>
            Program.AgentOptions.Parse(new[] { "--fps", fps }));

    [Fact]
    public void Cli_RejectsTooLowBitrate()
        => Assert.Throws<ArgumentException>(() =>
            Program.AgentOptions.Parse(new[] { "--bitrate", "1000" }));
}
