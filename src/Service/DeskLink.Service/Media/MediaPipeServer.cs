// Service 侧媒体通道转发泵（P8/P9 收尾）。
//
// 位置：Service 是**本地交换机**。它同时开两条命名管道：
//   - `DeskLink.Media.{instance}`      ← WPF 客户端连进来（收画面、发输入）
//   - `DeskLink.AgentMedia.{instance}` ← 桌面代理连进来（发画面、收输入）
//
// 为什么由 Service 中转而不是让客户端直连代理：
//   1. 会话生命周期归 Service（中继/直连都是它建立的）。客户端换路径（中继↔局域网）
//      或重连时，代理不该跟着重启；把两者解耦在 Service 里最省事。
//   2. 代理跑在**被控端**、客户端跑在**控制端**，跨机时二者根本不在一台机器上；
//      只有 Service（被控端）和代理同机，能走本地命名管道。
//   3. 背压/丢帧策略只需要在一个地方实现。
//
// 转发方向与丢帧策略（**这是本文件最关键的决策**）：
//   - 代理 → 客户端（画面/配置/统计）：队列满时**丢最旧的**。
//     画面是"只要最新的"的语义，丢旧帧比卡住代理的采集循环好得多；
//     卡住代理会导致整个桌面卡顿，而丢几帧只是画面略跳。
//   - 客户端 → 代理（输入事件）：队列满时**优先丢最旧的鼠标移动**，
//     其次才丢新来的。鼠标移动可以丢（下一个位置就覆盖了），
//     但**按键按下/抬起绝不能随意丢**——丢掉一个"抬起"会造成远端按键卡住。
//
// 顶替语义：同类连接**后来者顶替**（关掉旧连接）。客户端重启或代理重启后
// 不需要等旧连接超时，否则会长时间"连不上"。
using System.IO.Pipes;
using System.Security.AccessControl;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;
using DeskLink.Service.Security;

namespace DeskLink.Service.Media;

/// <summary>转发泵参数。</summary>
public sealed record MediaRouterOptions
{
    /// <summary>发往客户端的队列容量（帧）。默认 120 ≈ 30fps 下 4 秒缓冲。</summary>
    public int ClientQueueCapacity { get; init; } = 120;

    /// <summary>发往代理的队列容量（输入事件）。输入很小，给大一点以减少丢弃。</summary>
    public int AgentQueueCapacity { get; init; } = 2048;

    /// <summary>向代理回报背压状态的间隔。</summary>
    public TimeSpan FlowFeedbackInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>管道允许的并发实例数（顶替旧连接需要 &gt; 1）。</summary>
    public int MaxServerInstances { get; init; } = 4;
}

/// <summary>当前媒体通道状态（供 <c>get_status</c> 上报）。</summary>
public readonly record struct MediaChannelStatus(
    bool ClientConnected,
    bool AgentConnected,
    long FramesToClient,
    long FramesToAgent,
    long DroppedToClient,
    long DroppedToAgent,
    int ClientQueueDepth)
{
    public static MediaChannelStatus Empty => new(false, false, 0, 0, 0, 0, 0);
}

/// <summary>Service 侧媒体通道转发泵。</summary>
public sealed class MediaPipeServer : IAsyncDisposable
{
    private readonly string _clientPipeName;
    private readonly string _agentPipeName;
    private readonly MediaRouterOptions _options;
    private readonly Action<string>? _log;

    private CancellationTokenSource? _cts;
    private Task? _clientAcceptLoop;
    private Task? _agentAcceptLoop;
    private Task? _flowLoop;

    private MediaPeer? _client;
    private MediaPeer? _agent;

    private long _framesToClient;
    private long _framesToAgent;
    private long _droppedToClient;
    private long _droppedToAgent;

    public MediaPipeServer(
        string clientPipeName,
        string agentPipeName,
        MediaRouterOptions? options = null,
        Action<string>? log = null)
    {
        _clientPipeName = clientPipeName ?? throw new ArgumentNullException(nameof(clientPipeName));
        _agentPipeName = agentPipeName ?? throw new ArgumentNullException(nameof(agentPipeName));
        _options = options ?? new MediaRouterOptions();
        _log = log;
    }

    public string ClientPipeName => _clientPipeName;

    public string AgentPipeName => _agentPipeName;

    /// <summary>启动两条监听循环（不阻塞）。</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_cts is not null) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _clientAcceptLoop = Task.Run(() => AcceptLoopAsync(isClient: true, token), token);
        _agentAcceptLoop = Task.Run(() => AcceptLoopAsync(isClient: false, token), token);
        _flowLoop = Task.Run(() => FlowFeedbackLoopAsync(token), token);

        _log?.Invoke($"MediaPipeServer: 监听 client={_clientPipeName} agent={_agentPipeName}");
        return Task.CompletedTask;
    }

    /// <summary>当前状态快照。</summary>
    public MediaChannelStatus GetStatus()
    {
        var client = Volatile.Read(ref _client);
        return new MediaChannelStatus(
            ClientConnected: client is { IsConnected: true },
            AgentConnected: Volatile.Read(ref _agent) is { IsConnected: true },
            FramesToClient: Interlocked.Read(ref _framesToClient),
            FramesToAgent: Interlocked.Read(ref _framesToAgent),
            DroppedToClient: Interlocked.Read(ref _droppedToClient),
            DroppedToAgent: Interlocked.Read(ref _droppedToAgent),
            ClientQueueDepth: client?.QueueDepth ?? 0);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 监听与顶替
    // ────────────────────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(bool isClient, CancellationToken ct)
    {
        var pipeName = isClient ? _clientPipeName : _agentPipeName;
        var role = isClient ? "client" : "agent";

        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = CreateServerStream(pipeName);
                await stream.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stream?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                _log?.Invoke($"MediaPipeServer[{role}]: accept 失败 {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
                continue;
            }

            var capacity = isClient ? _options.ClientQueueCapacity : _options.AgentQueueCapacity;
            var peer = new MediaPeer(role, stream, capacity);

            // 顶替：后来者优先。先把旧连接摘掉再关，避免旧连接的收尾逻辑
            // 把新连接从槽位上清掉（见 ServePeerAsync 的引用相等判断）。
            var previous = isClient
                ? Interlocked.Exchange(ref _client, peer)
                : Interlocked.Exchange(ref _agent, peer);
            if (previous is not null)
            {
                _log?.Invoke($"MediaPipeServer[{role}]: 新连接顶替旧连接");
                _ = DisposeQuietlyAsync(previous);
            }

            _log?.Invoke($"MediaPipeServer[{role}]: 已连接（容量 {capacity} 帧）");
            _ = Task.Run(() => ServePeerAsync(peer, isClient, ct), ct);
        }
    }

    private async Task ServePeerAsync(MediaPeer peer, bool isClient, CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var writer = peer.WriterLoopAsync(linked.Token);
            var reader = peer.ReaderLoopAsync((type, payload) => RouteAsync(isClient, type, payload), linked.Token);

            // 任一方向结束（对端关闭 / 出错）即认为该连接结束。
            await Task.WhenAny(writer, reader).ConfigureAwait(false);
            linked.Cancel();
            try { await Task.WhenAll(writer, reader).ConfigureAwait(false); } catch { /* 收尾 */ }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log?.Invoke($"MediaPipeServer[{peer.Role}]: 会话结束 {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // 只有"自己仍是当前槽位"时才清空：否则会把已经顶替上来的新连接清掉。
            if (isClient)
            {
                if (ReferenceEquals(Volatile.Read(ref _client), peer)) Volatile.Write(ref _client, null);
            }
            else
            {
                if (ReferenceEquals(Volatile.Read(ref _agent), peer)) Volatile.Write(ref _agent, null);
            }
            await DisposeQuietlyAsync(peer).ConfigureAwait(false);
            _log?.Invoke($"MediaPipeServer[{peer.Role}]: 已断开");
        }
    }

    private static async Task DisposeQuietlyAsync(MediaPeer peer)
    {
        try { await peer.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 路由
    // ────────────────────────────────────────────────────────────────────────

    private Task RouteAsync(bool fromClient, ProtocolConstants.FrameType type, byte[] payload)
    {
        if (fromClient)
        {
            // 客户端 → 代理：输入事件。
            if (!IsClientToAgent(type))
            {
                _log?.Invoke($"MediaPipeServer: 忽略客户端发来的非输入帧 0x{(byte)type:x2}");
                return Task.CompletedTask;
            }

            var agent = Volatile.Read(ref _agent);
            if (agent is null)
            {
                // 没有代理在线：丢掉而不是缓存。缓存输入毫无意义（等代理上线时
                // 用户早就松手了），还会把队列占满。
                Interlocked.Increment(ref _droppedToAgent);
                return Task.CompletedTask;
            }

            agent.Enqueue(MediaChannelFraming.Encode(type, payload), type, MediaDropPolicy.PreferInput);
            Interlocked.Increment(ref _framesToAgent);
            return Task.CompletedTask;
        }

        // 代理 → 客户端：画面/配置/统计。
        if (IsClientToAgent(type))
        {
            _log?.Invoke($"MediaPipeServer: 忽略代理发来的输入帧 0x{(byte)type:x2}");
            return Task.CompletedTask;
        }

        var client = Volatile.Read(ref _client);
        if (client is null)
        {
            // 没有客户端在线：丢掉。画面是"只要最新"的语义，缓存只会让客户端
            // 一上线就看到一段过期画面。
            Interlocked.Increment(ref _droppedToClient);
            return Task.CompletedTask;
        }

        client.Enqueue(MediaChannelFraming.Encode(type, payload), type, MediaDropPolicy.DropOldest);
        Interlocked.Increment(ref _framesToClient);
        return Task.CompletedTask;
    }

    /// <summary>该帧类型是否属于"客户端 → 代理"方向。</summary>
    public static bool IsClientToAgent(ProtocolConstants.FrameType type) => type switch
    {
        ProtocolConstants.FrameType.InputMouseMove => true,
        ProtocolConstants.FrameType.InputMouseButton => true,
        ProtocolConstants.FrameType.InputWheel => true,
        ProtocolConstants.FrameType.InputKey => true,
        _ => false,
    };

    // ────────────────────────────────────────────────────────────────────────
    // 背压反馈（让代理的自适应码率能看到"发不动了"）
    // ────────────────────────────────────────────────────────────────────────

    private async Task FlowFeedbackLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_options.FlowFeedbackInterval, ct).ConfigureAwait(false);

                var agent = Volatile.Read(ref _agent);
                if (agent is null) continue;

                var status = GetStatus();
                var payload = new MediaFlowPayload(
                    DroppedFrames: (uint)Math.Min(uint.MaxValue, status.DroppedToClient),
                    ClientQueueDepth: (uint)Math.Max(0, status.ClientQueueDepth),
                    DroppedInput: (uint)Math.Min(uint.MaxValue, status.DroppedToAgent)).Encode();

                agent.Enqueue(
                    MediaChannelFraming.Encode(ProtocolConstants.FrameType.MediaFlow, payload),
                    ProtocolConstants.FrameType.MediaFlow,
                    // 反馈帧本身不占多少带宽，满了就丢新的（旧反馈已经过时）。
                    MediaDropPolicy.PreferInput);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log?.Invoke($"MediaPipeServer: 反馈循环结束 {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 管道与队列
    // ────────────────────────────────────────────────────────────────────────

    private NamedPipeServerStream CreateServerStream(string pipeName)
    {
        // ACL 与 RPC 管道同源：必须授予控制台登录用户，否则服务以 LocalSystem 运行时
        // 非提权客户端连不上（详见 PipeAcl）。
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            _options.MaxServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 64 * 1024,
            outBufferSize: 64 * 1024,
            pipeSecurity: PipeAcl.Build(_log));
    }

    /// <summary>一条已建立的媒体连接：带界的出站队列 + 写循环 + 读循环。</summary>
    private sealed class MediaPeer : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _stream;
        private readonly MediaFrameQueue _queue;
        private readonly SemaphoreSlim _signal = new(0);
        private readonly MediaFrameReader _reader = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private bool _disposed;

        public MediaPeer(string role, NamedPipeServerStream stream, int capacity)
        {
            Role = role;
            _stream = stream;
            _queue = new MediaFrameQueue(capacity);
        }

        public string Role { get; }

        public bool IsConnected => !_disposed && _stream.IsConnected;

        public long Dropped => _queue.Dropped;

        public int QueueDepth => _queue.Depth;

        public void Enqueue(byte[] frame, ProtocolConstants.FrameType type, MediaDropPolicy policy)
        {
            if (_disposed) return;

            if (!_queue.Enqueue(frame, type, policy)) return;

            try { _signal.Release(); } catch (ObjectDisposedException) { /* 收尾竞态 */ }
        }

        public async Task WriterLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);

                if (!_queue.TryDequeue(out var frame, out _)) continue;

                await _writeGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }

        public async Task ReaderLoopAsync(
            Func<ProtocolConstants.FrameType, byte[], Task> onFrame, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) return; // 对端关闭

                _reader.Append(buffer.AsSpan(0, n));
                while (_reader.TryRead(out var type, out var payload))
                {
                    await onFrame(type, payload).ConfigureAwait(false);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;

            try { _signal.Release(); } catch { /* 唤醒写循环退出 */ }
            _queue.Clear();
            try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
            _signal.Dispose();
            _writeGate.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        _cts = null;

        try { cts.Cancel(); } catch { }

        var loops = new[] { _clientAcceptLoop, _agentAcceptLoop, _flowLoop }
            .Where(t => t is not null).ToArray();
        if (loops.Length > 0)
        {
            try { await Task.WhenAll(loops!).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { /* 收尾 */ }
        }

        foreach (var peer in new[] { Interlocked.Exchange(ref _client, null), Interlocked.Exchange(ref _agent, null) })
        {
            if (peer is not null) await DisposeQuietlyAsync(peer).ConfigureAwait(false);
        }

        cts.Dispose();
    }
}
