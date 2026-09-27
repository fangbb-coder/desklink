using System.IO.Pipes;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;
using DeskLink.Service.Media;
using Xunit;
using Xunit.Abstractions;

namespace DeskLink.Service.Tests;

/// <summary>
/// P8/P9 收尾：Service 侧媒体通道转发泵。
///
/// 分两层验证：
///   1. <see cref="MediaFrameQueue"/> 的丢弃策略 —— 这是"保输入响应"的实现核心，
///      用管道端到端很难稳定制造背压（命名管道有 64KB 内核缓冲，小帧要灌满才溢出），
///      所以直接对队列做确定性断言。
///   2. <see cref="MediaPipeServer"/> 的真实路由 —— 用真命名管道、真字节收发，
///      断言画面下行、输入上行、顶替、无对端丢弃、背压反馈。
/// </summary>
public class MediaChannelTests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public MediaChannelTests(ITestOutputHelper output) => _out = output;

    public void Dispose() { }

    private static string NewPipeName(string tag) => $"DeskLink.Test.{tag}.{Guid.NewGuid():N}";

    // ────────────────────────────────────────────────────────────────────────
    // 1. 丢弃策略（确定性）
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Queue_DropOldest_DiscardsOldestFrame()
    {
        var q = new MediaFrameQueue(3);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(q.Enqueue(new byte[] { (byte)i }, ProtocolConstants.FrameType.DesktopVideo, MediaDropPolicy.DropOldest));
        }

        Assert.Equal(3, q.Depth);
        Assert.Equal(2, q.Dropped);

        // 留下的应是最后 3 帧（2,3,4）—— 画面只要最新的。
        Assert.True(q.TryDequeue(out var first, out _));
        Assert.Equal(new byte[] { 2 }, first);
    }

    [Fact]
    public void Queue_PreferInput_DropsOldestMouseMove_NotNewFrame()
    {
        var q = new MediaFrameQueue(3);
        q.Enqueue(new byte[] { 1 }, ProtocolConstants.FrameType.InputMouseMove, MediaDropPolicy.PreferInput);
        q.Enqueue(new byte[] { 2 }, ProtocolConstants.FrameType.InputMouseMove, MediaDropPolicy.PreferInput);
        q.Enqueue(new byte[] { 3 }, ProtocolConstants.FrameType.InputMouseMove, MediaDropPolicy.PreferInput);

        // 队列已满，新来的是按键：必须入队成功（丢掉最旧的鼠标移动）。
        var accepted = q.Enqueue(new byte[] { 9 }, ProtocolConstants.FrameType.InputKey, MediaDropPolicy.PreferInput);

        Assert.True(accepted);
        Assert.Equal(1, q.Dropped);
        Assert.Equal(3, q.Depth);

        // 最早的两个移动被丢了一个，按键一定还在队列里。
        var types = new List<ProtocolConstants.FrameType>();
        while (q.TryDequeue(out _, out var t)) types.Add(t);
        Assert.Contains(ProtocolConstants.FrameType.InputKey, types);
        Assert.Equal(2, types.Count(t => t == ProtocolConstants.FrameType.InputMouseMove));
    }

    /// <summary>
    /// 队列里全是按键（没有可丢的移动）时，宁可丢**新来的**也不丢已有的按键。
    /// 丢掉一个"抬起"会让远端按键永久卡住，比丢一个按下严重得多。
    /// </summary>
    [Fact]
    public void Queue_PreferInput_WhenNoMouseMove_DropsNewFrameAndKeepsKeys()
    {
        var q = new MediaFrameQueue(2);
        Assert.True(q.Enqueue(new byte[] { 1 }, ProtocolConstants.FrameType.InputKey, MediaDropPolicy.PreferInput));
        Assert.True(q.Enqueue(new byte[] { 2 }, ProtocolConstants.FrameType.InputKey, MediaDropPolicy.PreferInput));

        var accepted = q.Enqueue(new byte[] { 3 }, ProtocolConstants.FrameType.InputKey, MediaDropPolicy.PreferInput);

        Assert.False(accepted);
        Assert.Equal(1, q.Dropped);
        Assert.Equal(2, q.Depth);

        Assert.True(q.TryDequeue(out var first, out _));
        Assert.Equal(new byte[] { 1 }, first); // 旧按键仍在，未被顶掉
    }

    [Fact]
    public void Queue_WithinCapacity_NeverDrops()
    {
        var q = new MediaFrameQueue(4);
        for (var i = 0; i < 4; i++)
        {
            Assert.True(q.Enqueue(new byte[] { (byte)i }, ProtocolConstants.FrameType.DesktopVideo, MediaDropPolicy.DropOldest));
        }
        Assert.Equal(0, q.Dropped);
        Assert.Equal(4, q.Depth);
    }

    [Fact]
    public void Queue_TryDequeueOnEmpty_ReturnsFalse()
    {
        var q = new MediaFrameQueue(2);
        Assert.False(q.TryDequeue(out _, out _));
    }

    [Fact]
    public void Queue_Clear_EmptiesQueue()
    {
        var q = new MediaFrameQueue(4);
        q.Enqueue(new byte[] { 1 }, ProtocolConstants.FrameType.DesktopVideo, MediaDropPolicy.DropOldest);
        q.Clear();
        Assert.Equal(0, q.Depth);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2. 负载编解码
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MediaFlowPayload_RoundTrips()
    {
        var original = new MediaFlowPayload(DroppedFrames: 123, ClientQueueDepth: 7, DroppedInput: 45);
        var decoded = MediaFlowPayload.Decode(original.Encode());
        Assert.Equal(original, decoded);
        Assert.Equal(12, MediaFlowPayload.Size);
    }

    [Fact]
    public void MediaFlowPayload_RejectsShortPayload()
        => Assert.Throws<InvalidDataException>(() => MediaFlowPayload.Decode(new byte[MediaFlowPayload.Size - 1]));

    // ────────────────────────────────────────────────────────────────────────
    // 3. 真实管道路由
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>测试用的原始媒体对端（真命名管道 + 真字节收发）。</summary>
    private sealed class TestPeer : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _stream;
        private readonly MediaFrameReader _reader = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly byte[] _buffer = new byte[64 * 1024];

        private TestPeer(NamedPipeClientStream stream) => _stream = stream;

        public static async Task<TestPeer> ConnectAsync(string pipeName, int timeoutMs = 5000)
        {
            var s = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await s.ConnectAsync(timeoutMs);
            return new TestPeer(s);
        }

        public async Task SendAsync(ProtocolConstants.FrameType type, ReadOnlyMemory<byte> payload)
        {
            var frame = MediaChannelFraming.Encode(type, payload.Span);
            await _writeGate.WaitAsync();
            try
            {
                await _stream.WriteAsync(frame);
                await _stream.FlushAsync();
            }
            finally { _writeGate.Release(); }
        }

        /// <summary>读一帧；超时返回 null。</summary>
        public async Task<(ProtocolConstants.FrameType Type, byte[] Payload)?> ReadFrameAsync(int timeoutMs = 3000)
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            while (!cts.IsCancellationRequested)
            {
                if (_reader.TryRead(out var type, out var payload)) return (type, payload);
                int n = await _stream.ReadAsync(_buffer, cts.Token);
                if (n == 0) return null;
                _reader.Append(_buffer.AsSpan(0, n));
            }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            _writeGate.Dispose();
        }
    }

    private async Task<MediaPipeServer> StartRouterAsync(MediaRouterOptions? options = null)
    {
        var server = new MediaPipeServer(
            NewPipeName("media-client"),
            NewPipeName("media-agent"),
            options ?? new MediaRouterOptions { FlowFeedbackInterval = TimeSpan.FromMilliseconds(150) },
            msg => _out.WriteLine("[router] " + msg));
        await server.StartAsync();
        return server;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"等待条件超时（{timeoutMs}ms）");
    }

    [Fact]
    public async Task Router_ForwardsDesktopConfig_FromAgentToClient()
    {
        await using var router = await StartRouterAsync();
        await using var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);

        await WaitUntilAsync(() => router.GetStatus().ClientConnected && router.GetStatus().AgentConnected);

        var config = new DesktopConfigPayload(1920, 1080, DesktopConfigPayload.CodecH264,
            DesktopConfigPayload.BackendSoftware, 30, 8_000_000, 0, 90);
        await agent.SendAsync(ProtocolConstants.FrameType.DesktopConfig, config.Encode());

        var received = await client.ReadFrameAsync();
        Assert.NotNull(received);
        Assert.Equal(ProtocolConstants.FrameType.DesktopConfig, received!.Value.Type);
        Assert.Equal(config, DesktopConfigPayload.Decode(received.Value.Payload));
    }

    [Fact]
    public async Task Router_ForwardsDesktopVideo_FromAgentToClient()
    {
        await using var router = await StartRouterAsync();
        await using var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected && router.GetStatus().AgentConnected);

        var chunk = new DesktopVideoChunk(7, 123456, IsKeyFrame: true, IsLastFragment: true, Data: new byte[] { 1, 2, 3, 4 });
        await agent.SendAsync(ProtocolConstants.FrameType.DesktopVideo, chunk.Encode());

        var received = await client.ReadFrameAsync();
        Assert.NotNull(received);
        Assert.Equal(ProtocolConstants.FrameType.DesktopVideo, received!.Value.Type);

        // 逐字段比较而不是整体 Assert.Equal：DesktopVideoChunk 是 record struct，
        // 但 Data 是 byte[]，record 的合成相等对数组是**引用比较**，
        // 整体比较会在内容一致时也失败（误导性的红灯）。
        var decoded = DesktopVideoChunk.Decode(received.Value.Payload);
        Assert.Equal(chunk.FrameSeq, decoded.FrameSeq);
        Assert.Equal(chunk.PtsMicros, decoded.PtsMicros);
        Assert.Equal(chunk.IsKeyFrame, decoded.IsKeyFrame);
        Assert.Equal(chunk.IsLastFragment, decoded.IsLastFragment);
        Assert.Equal(chunk.Data, decoded.Data);
    }

    [Fact]
    public async Task Router_ForwardsInput_FromClientToAgent()
    {
        await using var router = await StartRouterAsync();
        await using var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected && router.GetStatus().AgentConnected);

        await client.SendAsync(ProtocolConstants.FrameType.InputMouseMove, InputEventCodec.EncodeMouseMove(250, 750));
        await client.SendAsync(ProtocolConstants.FrameType.InputKey, InputEventCodec.EncodeKey(0x1C, extended: false, down: true));

        var first = await agent.ReadFrameAsync();
        Assert.NotNull(first);
        Assert.Equal(ProtocolConstants.FrameType.InputMouseMove, first!.Value.Type);
        Assert.Equal((250, 750), InputEventCodec.DecodeMouseMove(first.Value.Payload));

        var second = await agent.ReadFrameAsync();
        Assert.NotNull(second);
        Assert.Equal(ProtocolConstants.FrameType.InputKey, second!.Value.Type);
        Assert.Equal((0x1C, false, true), InputEventCodec.DecodeKey(second.Value.Payload));
    }

    [Fact]
    public async Task Router_IgnoresWrongDirectionFrames()
    {
        await using var router = await StartRouterAsync();
        await using var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected && router.GetStatus().AgentConnected);

        // 客户端发"画面"、代理发"输入"：都属于方向错误，必须被丢弃而不是转发。
        await client.SendAsync(ProtocolConstants.FrameType.DesktopVideo,
            new DesktopVideoChunk(1, 0, false, true, new byte[] { 9 }).Encode());
        await agent.SendAsync(ProtocolConstants.FrameType.InputKey, InputEventCodec.EncodeKey(1, false, true));

        // 再发一条正确方向的，确认链路仍然健康（错误帧只是被忽略，没有断链）。
        var config = new DesktopConfigPayload(640, 480, 1, 1, 30, 1_000_000, 0, 0);
        await agent.SendAsync(ProtocolConstants.FrameType.DesktopConfig, config.Encode());

        var received = await client.ReadFrameAsync();
        Assert.NotNull(received);
        Assert.Equal(ProtocolConstants.FrameType.DesktopConfig, received!.Value.Type);
    }

    [Fact]
    public async Task Router_LaterClient_ReplacesEarlier()
    {
        await using var router = await StartRouterAsync();
        await using var first = await TestPeer.ConnectAsync(router.ClientPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected);

        await using var second = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().AgentConnected);

        // 画面只应到达**最新**的客户端。
        var config = new DesktopConfigPayload(800, 600, 1, 0, 30, 2_000_000, 0, 0);
        await agent.SendAsync(ProtocolConstants.FrameType.DesktopConfig, config.Encode());

        var got = await second.ReadFrameAsync();
        Assert.NotNull(got);
        Assert.Equal(ProtocolConstants.FrameType.DesktopConfig, got!.Value.Type);
    }

    [Fact]
    public async Task Router_WithoutClient_DropsVideoInsteadOfBlocking()
    {
        await using var router = await StartRouterAsync();
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().AgentConnected);

        // 没有客户端：代理连发多帧必须**立即返回**（不阻塞采集循环），并被计为丢弃。
        for (var i = 0; i < 20; i++)
        {
            await agent.SendAsync(ProtocolConstants.FrameType.DesktopVideo,
                new DesktopVideoChunk((uint)i, 0, false, true, new byte[64]).Encode());
        }

        await WaitUntilAsync(() => router.GetStatus().DroppedToClient >= 20);
        Assert.False(router.GetStatus().ClientConnected);
    }

    [Fact]
    public async Task Router_SendsFlowFeedback_ToAgent()
    {
        await using var router = await StartRouterAsync();
        await using var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected && router.GetStatus().AgentConnected);

        // 反馈是周期性的（150ms），因此这里等一小会儿。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        MediaFlowPayload? flow = null;
        while (DateTime.UtcNow < deadline && flow is null)
        {
            var frame = await agent.ReadFrameAsync(timeoutMs: 1000);
            if (frame is null) continue;
            if (frame.Value.Type == ProtocolConstants.FrameType.MediaFlow)
            {
                flow = MediaFlowPayload.Decode(frame.Value.Payload);
            }
        }

        Assert.NotNull(flow);
    }

    [Fact]
    public async Task Router_Status_ReflectsCounters()
    {
        await using var router = await StartRouterAsync();
        Assert.False(router.GetStatus().ClientConnected);

        await using var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await using var agent = await TestPeer.ConnectAsync(router.AgentPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected && router.GetStatus().AgentConnected);

        await agent.SendAsync(ProtocolConstants.FrameType.DesktopConfig,
            new DesktopConfigPayload(640, 480, 1, 0, 30, 1_000_000, 0, 0).Encode());
        await WaitUntilAsync(() => router.GetStatus().FramesToClient >= 1);

        var status = router.GetStatus();
        Assert.True(status.ClientConnected);
        Assert.True(status.AgentConnected);
        Assert.True(status.FramesToClient >= 1);
    }

    [Fact]
    public async Task Router_Disconnect_IsReflectedInStatus()
    {
        await using var router = await StartRouterAsync();
        var client = await TestPeer.ConnectAsync(router.ClientPipeName);
        await WaitUntilAsync(() => router.GetStatus().ClientConnected);

        await client.DisposeAsync();
        await WaitUntilAsync(() => !router.GetStatus().ClientConnected);
    }
}
