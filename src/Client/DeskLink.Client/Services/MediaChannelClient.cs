using System.IO;
using System.IO.Pipes;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Media;

namespace DeskLink.Client.Services;

/// <summary>
/// 鼠标移动合并器（输入节流）。
///
/// 为什么要节流：WPF 的 MouseMove 在高刷屏/快速拖动下可达每秒上千次，
/// 而远端注入一次移动的成本远高于"看着顺滑"所需。DESIGN 关心的是**输入响应**，
/// 不是把每一个中间位置都送出去——合并掉中间位置、只保留最新位置，
/// 能显著降低管道带宽与远端注入压力，同时用户感知的指针轨迹几乎不变。
/// 这里取"同一时刻最多一个在途位置 + 最小发送间隔"的简单策略。
/// </summary>
public sealed class MouseMoveCoalescer
{
    private readonly int _minIntervalMs;
    private long _lastSentMs = long.MinValue;
    private (int X, int Y)? _pending;

    /// <param name="minIntervalMs">两次发送之间的最小间隔（毫秒）。默认 5ms ≈ 200 次/秒。</param>
    public MouseMoveCoalescer(int minIntervalMs = 5)
    {
        if (minIntervalMs < 0) throw new ArgumentOutOfRangeException(nameof(minIntervalMs));
        _minIntervalMs = minIntervalMs;
    }

    public int MinIntervalMs => _minIntervalMs;

    /// <summary>已合并（丢弃）的中间位置数量，仅用于诊断。</summary>
    public long CoalescedCount { get; private set; }

    /// <summary>
    /// 提交一个新位置。<paramref name="toSend"/> 非空表示"此刻应立即发送该位置"；
    /// 为空表示被节流、已暂存为最新值，等 <see cref="TryFlush"/> 送出。
    /// </summary>
    public bool Submit(int x, int y, long nowMs, out (int X, int Y) toSend)
    {
        toSend = default;
        if (_pending is not null) CoalescedCount++;
        _pending = (x, y);

        if (nowMs - _lastSentMs >= _minIntervalMs || _lastSentMs == long.MinValue)
        {
            _lastSentMs = nowMs;
            toSend = _pending.Value;
            _pending = null;
            return true;
        }
        return false;
    }

    /// <summary>定时冲刷：节流窗口已过且存在暂存值时取出最新位置。</summary>
    public bool TryFlush(long nowMs, out (int X, int Y) value)
    {
        value = default;
        if (_pending is null) return false;
        if (_lastSentMs != long.MinValue && nowMs - _lastSentMs < _minIntervalMs) return false;

        _lastSentMs = nowMs;
        value = _pending.Value;
        _pending = null;
        return true;
    }

    /// <summary>是否存在尚未送出的暂存位置。</summary>
    public bool HasPending => _pending is not null;
}

/// <summary>媒体通道上重组出的一个完整 H.264 访问单元。</summary>
public sealed record VideoAccessUnit(uint FrameSeq, ulong PtsMicros, bool IsKeyFrame, byte[] Data);

/// <summary>
/// 把分片的 DesktopVideoChunk 重组成完整访问单元。
///
/// 为什么必须重组：媒体帧负载上限受 u16 约束（65535），1080p 关键帧远超此值，
/// 发送端按访问单元分片、只有最后一片 IsLastFragment=true。解码器需要完整 AU，
/// 逐片送进去只会解不出东西。
/// </summary>
public sealed class VideoFragmentAssembler
{
    private readonly MemoryStream _buffer = new();
    private uint _seq;
    private ulong _pts;
    private bool _key;
    private bool _hasData;

    /// <summary>喂入一片；收齐一个访问单元时返回它，否则返回 null。</summary>
    public VideoAccessUnit? Add(DesktopVideoChunk chunk)
    {
        // 序号变了但上一帧没收尾：说明丢片（管道不保证可靠），丢弃残帧避免拼接出坏数据。
        if (_hasData && chunk.FrameSeq != _seq)
        {
            Reset();
        }

        if (!_hasData)
        {
            _seq = chunk.FrameSeq;
            _pts = chunk.PtsMicros;
            _key = chunk.IsKeyFrame;
            _hasData = true;
        }
        else if (chunk.IsKeyFrame)
        {
            _key = true;
        }

        _buffer.Write(chunk.Data, 0, chunk.Data.Length);

        if (!chunk.IsLastFragment) return null;

        var data = _buffer.ToArray();
        var unit = new VideoAccessUnit(_seq, _pts, _key, data);
        Reset();
        return unit;
    }

    public void Reset()
    {
        _buffer.SetLength(0);
        _hasData = false;
    }
}

/// <summary>
/// 媒体通道客户端（WPF 客户端 ↔ Service 的**第二条**命名管道）。
///
/// 为什么和 RPC 管道分开（见 MediaChannelContract.cs 顶部）：
///   RPC 管道按设计只走元数据、帧上限 64KB；远端画面是几 Mbps 的**流**，
///   绝不能塞进 RPC。因此另开 <c>DeskLink.Media.{instance}</c>，格式是
///   <c>[u32 len BE][u8 frameType][payload]</c>，不做 JSON、不做请求响应配对。
///
/// 方向：入站 = 桌面码流/统计（Service → 客户端）；出站 = 输入事件（客户端 → Service）。
/// </summary>
public sealed class MediaChannelClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly MediaFrameReader _reader = new();
    private readonly VideoFragmentAssembler _assembler = new();
    private readonly MouseMoveCoalescer _coalescer;
    private Task? _readLoop;
    private bool _disposed;

    private MediaChannelClient(NamedPipeClientStream stream, string pipeName, int mouseMinIntervalMs)
    {
        _stream = stream;
        PipeName = pipeName;
        _coalescer = new MouseMoveCoalescer(mouseMinIntervalMs);
    }

    public string PipeName { get; }

    /// <summary>收到编码器/显示器配置（据此重配解码器与画面尺寸）。</summary>
    public event Action<DesktopConfigPayload>? DesktopConfigReceived;

    /// <summary>收到一个完整 H.264 访问单元。</summary>
    public event Action<VideoAccessUnit>? AccessUnitReceived;

    /// <summary>收到会话统计（供状态条显示 fps/kbps/rtt/degraded）。</summary>
    public event Action<SessionStatsPayload>? StatsReceived;

    /// <summary>读循环因帧头非法等原因终止。</summary>
    public event Action<Exception>? Faulted;

    public bool IsConnected => !_disposed && _stream.IsConnected;

    /// <summary>打开媒体管道并启动读循环。连不上抛 <see cref="PipeUnavailableException"/>。</summary>
    public static async Task<MediaChannelClient> ConnectAsync(
        string pipeName, int timeoutMs = 3000, int mouseMinIntervalMs = 5, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipeName 不能为空", nameof(pipeName));

        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            var client = new MediaChannelClient(stream, pipeName, mouseMinIntervalMs);
            client.StartReadLoop();
            return client;
        }
        catch (OperationCanceledException)
        {
            // 调用方取消也要释放已创建的管道句柄（旧代码只处理非取消异常，取消时泄漏）。
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new PipeUnavailableException(pipeName,
                $"无法连接媒体管道 {pipeName}（{timeoutMs}ms 内未连上）：{ex.GetType().Name}", ex);
        }
    }

    private void StartReadLoop()
        => _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) break; // 对端关闭

                _reader.Append(buffer.AsSpan(0, n));
                while (_reader.TryRead(out var type, out var payload))
                {
                    Dispatch(type, payload);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private void Dispatch(ProtocolConstants.FrameType type, byte[] payload)
    {
        try
        {
            switch (type)
            {
                case ProtocolConstants.FrameType.DesktopConfig:
                    DesktopConfigReceived?.Invoke(DesktopConfigPayload.Decode(payload));
                    break;
                case ProtocolConstants.FrameType.DesktopVideo:
                {
                    var unit = _assembler.Add(DesktopVideoChunk.Decode(payload));
                    if (unit is not null) AccessUnitReceived?.Invoke(unit);
                    break;
                }
                case ProtocolConstants.FrameType.SessionControl:
                    // SessionControl 有多种子类型；会话统计是定长 13 字节那种。
                    if (payload.Length == SessionStatsPayload.Size)
                    {
                        StatsReceived?.Invoke(SessionStatsPayload.Decode(payload));
                    }
                    break;
            }
        }
        catch (InvalidDataException ex)
        {
            // 单帧负载损坏：只丢这一帧并报错，不断链（长度前缀仍可信，下一帧能对齐）。
            Faulted?.Invoke(ex);
        }
    }

    // —— 出站：输入事件 ——

    public void SendMouseMove(int xPermille, int yPermille, long nowMs = 0)
    {
        if (nowMs == 0) nowMs = Environment.TickCount64;
        if (_coalescer.Submit(xPermille, yPermille, nowMs, out var toSend))
        {
            _ = SendAsync(ProtocolConstants.FrameType.InputMouseMove, InputEventCodec.EncodeMouseMove(toSend.X, toSend.Y));
        }
    }

    /// <summary>把暂存的最后一次鼠标移动补发出去（由 UI 的定时器/空闲回调调用）。</summary>
    public void FlushPendingMouseMove(long nowMs = 0)
    {
        if (nowMs == 0) nowMs = Environment.TickCount64;
        if (_coalescer.TryFlush(nowMs, out var v))
        {
            _ = SendAsync(ProtocolConstants.FrameType.InputMouseMove, InputEventCodec.EncodeMouseMove(v.X, v.Y));
        }
    }

    public void SendMouseButton(byte button, bool down)
        => _ = SendAsync(ProtocolConstants.FrameType.InputMouseButton, InputEventCodec.EncodeMouseButton(button, down));

    public void SendWheel(int delta)
        => _ = SendAsync(ProtocolConstants.FrameType.InputWheel, InputEventCodec.EncodeWheel(delta));

    /// <summary>
    /// 发送按键。用 scancode 而非虚拟键码：远端注入走 SendInput 的
    /// KEYEVENTF_SCANCODE，scancode 与物理布局无关（VK 会受本机键盘布局影响）。
    /// </summary>
    public void SendKey(ushort scancode, bool extended, bool down)
        => _ = SendAsync(ProtocolConstants.FrameType.InputKey, InputEventCodec.EncodeKey(scancode, extended, down));

    /// <summary>发送原始媒体帧（主要给测试与扩展用）。</summary>
    public Task SendAsync(ProtocolConstants.FrameType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = MediaChannelFraming.Encode(type, payload.Span);

        return SendSerializedAsync(frame, ct);
    }

    private async Task SendSerializedAsync(byte[] frame, CancellationToken ct)
    {
        // 写方向也要串行化：命名管道是字节流，两个并发写会互相穿插把帧撕开。
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Faulted?.Invoke(ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        try
        {
            if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
        }
        catch { /* 释放路径 */ }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
        _writeGate.Dispose();
    }
}
