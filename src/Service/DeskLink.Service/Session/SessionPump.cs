// 加密会话收发泵（P5）。
//
// 职责：在 EncryptedSession 上跑“控制流往返”——
//   - 主动心跳：定时发 Ping 帧，收到 Pong 记录 RTT。
//   - 被动应答：收到 Ping 回 Pong。
//   - 业务帧分发：收到的帧按逻辑流（desktop/control/file）交给注册的处理器。
//
// P5 验收只要求“控制流往返 + 文件层仅认 ack 位图”：
//   - 控制流用 Ping/Pong + SessionControl(0x24) 验证往返；
//   - 文件层在 P5 只做 FileAck(0x34) 位图帧的收发骨架，真实文件逻辑在 P6。
//
// 统一分帧（两种传输一致，避免 QUIC 路径需要“一整帧一次到达”的隐含假设）：
//   线上字节 = [u32 len BE][u8 sid][密封帧]，len = 1 + 密封帧长度。
//   - TCP/TLS 路径：这正好等于 MuxFrame 外层（[u32 len][u8 sid][inner]）。
//   - QUIC 路径：QUIC 原生是字节流（不做消息边界），所以同样显式加外层，
//     保证接收端可以按长度前缀精确剥帧。
//
// 线程模型：ReadLoopAsync 是唯一读循环；SendFrameAsync 供业务线程调用
// （_sendLock 串行化，配合 EncryptedSession 内部的 AEAD 计数器保证顺序）。
using System.Buffers.Binary;
using System.Collections.Concurrent;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Frames;
using DeskLink.Protocol.Mux;
using DeskLink.Protocol.Session;
using DeskLink.Service.Relay;

namespace DeskLink.Service.Session;

/// <summary>收到的业务帧（已解密）。</summary>
public sealed record InboundFrame(ProtocolConstants.FrameType Type, byte[] Payload, byte Sid);

/// <summary>
/// 加密会话收发泵：负责剥帧、解密、分发，以及心跳往返。
/// </summary>
public sealed class SessionPump : IAsyncDisposable
{
    private readonly IRelayTransport _transport;
    private readonly EncryptedSession _session;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    /// <summary>按逻辑流注册的入站处理器。</summary>
    private readonly Dictionary<ProtocolConstants.LogicalStream, Action<InboundFrame>> _handlers = new();

    /// <summary>读/心跳循环共享的取消源。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>外部取消令牌的注册句柄（Start 传入可取消令牌时非 null）。</summary>
    private CancellationTokenRegistration _externalReg;

    /// <summary>
    /// 接收缓冲区（累积半帧）。仅由读循环访问，无需加锁。
    ///
    /// 用 byte[] + 有效长度替代 List&lt;byte&gt;：
    ///   - List&lt;byte&gt;.AddRange 只接受数组/IEnumerable，于是每次 Recv 都得先
    ///     tmp.AsSpan(0,n).ToArray()——每个读多一次最多 64KB 的分配；
    ///   - RemoveRange(0,total) 会搬移剩余元素，GetRange(...).ToArray() 再分配一次。
    ///   视频/文件流（P6/P7）会把这个路径打成热点，所以改成裸缓冲 + 手动消费。
    /// </summary>
    private byte[] _rx = new byte[16 * 1024];

    /// <summary>_rx 中的有效字节数（[0, _rxLen) 是待解析数据）。</summary>
    private int _rxLen;

    private Task? _readLoop;
    private Task? _pingLoop;

    /// <summary>DisposeAsync 幂等标志（0=未释放，1=已释放）。</summary>
    private int _disposedFlag;

    /// <summary>最近一次 Ping 的往返时延（毫秒）；无样本时为 -1。</summary>
    public long LastRttMs { get; private set; } = -1;

    /// <summary>
    /// 读循环结束（对端关闭 / 传输错误 / 被取消）时完成的任务。
    /// 上层可 await 它来感知会话结束。Start() 之前返回一个未启动的占位任务。
    /// </summary>
    public Task Completion => _readLoop ?? Task.CompletedTask;

    /// <summary>控制流默认使用的 sid（对应 StreamId.ControlBase）。</summary>
    private const byte ControlSid = StreamId.ControlBase;

    /// <summary>
    /// Ping/Pong payload 约定（P5 起带时间戳，用于**真实 RTT**）：
    ///   [u64 seq BE][u64 sendUnixMicros BE]  —— 共 16 字节
    ///
    /// 语义：
    ///   - 发送方填自己的 seq 与发送时刻（unix 微秒）；接收方把整个 payload **原样回显**。
    ///   - 只有"发送方自己"会解读回显值，因此不依赖两端时钟同步，也不需要协商。
    ///   - 空 payload 仍然合法（兼容不带时间戳的对端）：只表示"往返可达"，不更新 RTT。
    ///
    /// 精度说明：Windows 上 `DateTimeOffset.UtcNow` 的粒度约 1ms，因此亚毫秒精度
    /// 不保证；RTT 本身以整毫秒对外暴露，够用。
    /// </summary>
    private const int PingPayloadLen = 16;

    /// <summary>Ping 序号（单调递增，用于把 Pong 与在途 Ping 配对）。</summary>
    private ulong _pingSeq;

    /// <summary>在途 Ping：seq → 等待该 Pong 的完成源。</summary>
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<long>> _pendingPings = new();

    public SessionPump(IRelayTransport transport, EncryptedSession session, Action<string>? log = null)
    {
        _transport = transport;
        _session = session;
        _log = log;
    }

    /// <summary>注册某逻辑流的入站处理器（重复注册会覆盖）。</summary>
    public void Register(ProtocolConstants.LogicalStream stream, Action<InboundFrame> handler)
    {
        _handlers[stream] = handler;
    }

    /// <summary>
    /// 启动读循环与心跳循环。
    ///
    /// external 非默认值时与之联动：外部取消（如上层停止服务 / 测试收尾）
    /// 会一并取消本泵，从而让读循环退出、Completion 完成。
    /// 否则泵只能靠"传输对端关闭"来结束，上层会一直等下去。
    /// </summary>
    public void Start(CancellationToken external = default)
    {
        if (external.CanBeCanceled)
        {
            _externalReg = external.Register(static state => ((SessionPump)state!)._cts.Cancel(), this);
        }
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _pingLoop = Task.Run(() => PingLoopAsync(_cts.Token));
    }

    /// <summary>发送一个控制流帧。</summary>
    public Task SendControlAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken ct = default)
        => SendFrameAsync(type, payload, ControlSid, ct);

    /// <summary>发送任意逻辑流帧（密封 + 加外层 + 串行写出）。</summary>
    public async Task SendFrameAsync(ProtocolConstants.FrameType type, byte[] payload, byte sid, CancellationToken ct = default)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // SealFrame 在 tcpMux=true 时返回“已含 mux 外层”的字节；
            // 为了让 QUIC 与 TCP 线上格式完全一致，这里统一自己加外层。
            var sealedFrame = _session.SealFrame(type, payload, sid, tcpMux: true);
            await _transport.SendAsync(sealedFrame, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>发起一次控制流往返：发 Ping，Pong 由读循环处理（更新 RTT）。</summary>
    public Task PingAsync(CancellationToken ct = default)
        => SendPingAsync(ct);

    /// <summary>
    /// 主动测一次往返时延：发一个带时间戳的 Ping，等对应的 Pong。
    /// 返回毫秒数；超时/被取消返回 null（会话可能已断）。
    /// </summary>
    public async Task<long?> MeasureRttAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var seq = Interlocked.Increment(ref _pingSeq);
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingPings[seq] = tcs;
        try
        {
            await SendPingCoreAsync(seq, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _log?.Invoke("SessionPump: rtt probe timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _pendingPings.TryRemove(seq, out _);
        }
    }

    /// <summary>发一个带时间戳的 Ping（fire-and-forget，RTT 由读循环记录）。</summary>
    private Task SendPingAsync(CancellationToken ct)
    {
        var seq = Interlocked.Increment(ref _pingSeq);
        return SendPingCoreAsync(seq, ct);
    }

    /// <summary>按给定 seq 发送带时间戳的 Ping。</summary>
    private Task SendPingCoreAsync(ulong seq, CancellationToken ct)
    {
        var payload = new byte[PingPayloadLen];
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0, 8), seq);
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(8, 8), UnixMicrosNow());
        return SendControlAsync(ProtocolConstants.FrameType.Ping, payload, ct);
    }

    /// <summary>当前 unix 微秒（发送时刻标记；仅本端解读回显值，不需要跨机时钟同步）。</summary>
    private static ulong UnixMicrosNow()
        => (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L);

    /// <summary>
    /// 处理收到的 Pong：按回显的时间戳算出真实 RTT。
    /// 空 payload（旧对端）只记"往返可达"，不更新 RTT。
    /// </summary>
    private void HandlePong(byte[] payload)
    {
        if (payload.Length < PingPayloadLen)
        {
            _log?.Invoke("SessionPump: pong received without timestamp (round-trip ok, rtt unknown)");
            return;
        }
        var seq = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(0, 8));
        var sentMicros = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(8, 8));
        var nowMicros = UnixMicrosNow();
        // 时钟回拨/异常时间戳保护：负数一律按 0 处理，避免把坏值写进状态。
        var rttMs = nowMicros >= sentMicros ? (long)((nowMicros - sentMicros) / 1000UL) : 0L;
        LastRttMs = rttMs;

        // 若有在途的主动测量，唤醒它。
        if (_pendingPings.TryRemove(seq, out var tcs))
        {
            tcs.TrySetResult(rttMs);
        }
        _log?.Invoke($"SessionPump: pong received rtt={rttMs}ms (control round-trip ok)");
    }

    /// <summary>
    /// 心跳循环：每 15s 发一次带时间戳的 Ping，Pong 回来后由 HandlePong 更新 LastRttMs。
    /// 当前不做"连续丢包即踢链"（P5 只要求可达性 + RTT 可观测；断链判定留到 P6）。
    /// </summary>
    private async Task PingLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                await SendPingAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log?.Invoke($"SessionPump: ping loop ended {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>唯一读循环：收字节 → 剥帧 → 解密 → 分发。</summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var tmp = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await _transport.ReceiveAsync(tmp, ct).ConfigureAwait(false);
                if (n == 0) break; // 对端关闭
                AppendRx(tmp.AsSpan(0, n));

                // 逐个剥帧，直到缓冲区不足以构成完整帧。
                while (TryExtract(out var sid, out var sealedFrame))
                {
                    Dispatch(sid, sealedFrame, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex)
        {
            _log?.Invoke($"SessionPump: read loop ended {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>把新收到的字节追加到接收缓冲（按需 2 倍扩容）。</summary>
    private void AppendRx(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;
        var need = _rxLen + data.Length;
        if (need > _rx.Length)
        {
            var cap = _rx.Length;
            while (cap < need) cap *= 2;
            Array.Resize(ref _rx, cap);
        }
        data.CopyTo(_rx.AsSpan(_rxLen));
        _rxLen = need;
    }

    /// <summary>
    /// 尝试从缓冲区剥出一个完整“外层帧”：[u32 len BE][u8 sid][密封帧]。
    /// len = 1(sid) + 密封帧长度。成功时消费掉对应字节。
    ///
    /// 返回 false 且已取消会话，表示对端违反了分帧约定（长度非法/超大）——
    /// 此时**不能**"清缓冲后继续"：长度前缀已被消费，后续字节会被当成新帧头，
    /// 流会永久错位且不报错。终止会话是唯一正确的处置。
    /// </summary>
    private bool TryExtract(out byte sid, out byte[] sealedFrame)
    {
        sid = 0;
        sealedFrame = Array.Empty<byte>();

        if (_rxLen < 4) return false; // 长度前缀还没收齐

        // 大端解析长度前缀（与 Go 侧 proto 一致）。
        var len = (uint)((_rx[0] << 24) | (_rx[1] << 16) | (_rx[2] << 8) | _rx[3]);
        if (len < 1)
        {
            // len 至少为 1（sid 字节）。早期实现这里只 return false，于是同一个
            // 非法前缀会被反复解析、缓冲无限增长——实为可被对端触发的内存耗尽。
            _log?.Invoke($"SessionPump: illegal outer len={len} (must be >= 1), aborting session");
            _cts.Cancel();
            return false;
        }

        // 上界保护：防对端/内存被恶意长度撑爆。
        //
        // 合法最大外层 len = sid(1) + AEAD 计数器(8) + 内层帧头(3) + 负载(65535) + AEAD tag(16)
        //                   = MaxFramePayloadWireSize + 28
        // 这里取 MaxFramePayloadSize（256KB，比合法值宽松）作为 DoS 上界；
        // 真正的负载上限由 FrameCodec.Encode 的 u16 校验把关。
        // （早期写法 1+8+MaxFramePayloadSize+16 少了内层帧头的 3 字节，
        //   会误判最大合法帧为"超大帧"，已改为从线上上限推导。）
        var maxOuter = 1 + 8 + Frame.HeaderSize + ProtocolConstants.MaxFramePayloadSize + 16;
        if (len > maxOuter)
        {
            _log?.Invoke($"SessionPump: oversized frame len={len} > {maxOuter}, aborting session");
            _cts.Cancel();
            return false;
        }

        var total = 4 + (int)len;
        if (_rxLen < total) return false; // 帧体还没收齐

        sid = _rx[4];
        sealedFrame = new byte[len - 1];
        Array.Copy(_rx, 5, sealedFrame, 0, sealedFrame.Length);

        // 消费掉 total 字节：把剩余部分搬到前面（通常剩余很少）。
        var rest = _rxLen - total;
        if (rest > 0)
        {
            Array.Copy(_rx, total, _rx, 0, rest);
        }
        _rxLen = rest;
        return true;
    }

    /// <summary>解密并按类型/sid 分发一帧。</summary>
    private void Dispatch(byte sid, byte[] sealedFrame, CancellationToken ct)
    {
        ProtocolConstants.FrameType type;
        byte[] payload;
        try
        {
            (type, payload) = _session.OpenFrame(sealedFrame);
        }
        catch (Exception ex)
        {
            // 解密/序号校验失败属于严重问题：记录后终止会话。
            _log?.Invoke($"SessionPump: open frame failed {ex.GetType().Name}: {ex.Message}");
            _cts.Cancel();
            return;
        }

        switch (type)
        {
            case ProtocolConstants.FrameType.Ping:
                // 收到 Ping 立即回 Pong：**原样回显 payload**（含发送方的时间戳），
                // 这样对端才能算出真实 RTT。fire-and-forget，不阻塞读循环。
                _ = Task.Run(async () =>
                {
                    try { await SendControlAsync(ProtocolConstants.FrameType.Pong, payload, ct).ConfigureAwait(false); }
                    catch { /* 尽力而为 */ }
                }, CancellationToken.None);
                return;
            case ProtocolConstants.FrameType.Pong:
                // 按回显的时间戳算真实 RTT（空 payload 只记可达性）。
                HandlePong(payload);
                return;
        }

        // 按 sid 映射逻辑流并交给注册的处理器。
        var frame = new InboundFrame(type, payload, sid);
        var stream = SidToLogical(sid);
        if (stream.HasValue && _handlers.TryGetValue(stream.Value, out var handler))
        {
            handler(frame);
        }
        else
        {
            _log?.Invoke($"SessionPump: unhandled frame type=0x{(byte)type:x2} sid={sid}");
        }
    }

    /// <summary>把 mux sid 映射到逻辑流。</summary>
    private static ProtocolConstants.LogicalStream? SidToLogical(byte sid)
    {
        if (sid >= StreamId.DesktopBase && sid < StreamId.ControlBase) return ProtocolConstants.LogicalStream.Desktop;
        if (sid >= StreamId.ControlBase && sid < StreamId.FileBase) return ProtocolConstants.LogicalStream.Control;
        if (sid >= StreamId.FileBase && sid <= StreamId.Max) return ProtocolConstants.LogicalStream.File;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等（线程安全）：RelaySessionRunner 的 finally 与调用方可能都会释放同一个泵，
        // 而 IAsyncDisposable 的 `await using` 也会再释放一次。早期实现没有守卫，
        // 第二次进来时会对已释放的 _cts 调 Cancel() → ObjectDisposedException，
        // 把正常的收尾路径变成异常。
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }
        _cts.Cancel();
        _externalReg.Dispose();
        // 唤醒仍在等待 Pong 的主动测量，避免调用方白等到超时。
        foreach (var kv in _pendingPings)
        {
            kv.Value.TrySetCanceled();
        }
        _pendingPings.Clear();

        // 先"中断"再"释放"：只关底层 socket / abort QUIC 流，让 pending 的
        // ReceiveAsync 立刻返回，读循环随即退出。若跳过这一步直接 CloseAsync，
        // 就会在 ReadAsync 仍挂起时 dispose SslStream —— native 层无堆栈崩溃
        // （测试宿主进程"意外退出"，且无任何 .NET 异常可捕获）。
        try { await _transport.AbortAsync().ConfigureAwait(false); } catch { /* best-effort */ }

        foreach (var loop in new[] { _readLoop, _pingLoop })
        {
            if (loop is null) continue;
            try
            {
                // 加 3s 上限：底层 SslStream.ReadAsync 在部分平台/场景下不响应
                // CancellationToken，await loop 会永远挂起（测试收尾时表现为进程崩溃）。
                await loop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch { /* 忽略取消/超时异常 */ }
        }
        // 读循环已退出（或超时放弃）：此时释放传输对象是安全的。
        try { await _transport.CloseAsync().ConfigureAwait(false); } catch { }
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
