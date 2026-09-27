// 中继会话驱动器（P4/P5 的收口）。
//
// 职责：把 RelayClient 交来的"一条已就绪传输"推进为一个可用的加密会话，
// 并在其上跑通控制流往返（P5 验收："双实例加密帧往返验证"）。
//
// 完整时序（本类负责 2~6）：
//   1. RelayClient：QUIC / TCP-TLS 传输握手完成 → 调 OnTransportReadyAsync。
//   2. 解析对端 device_id（--peer 显式给出；否则回退 pairings.json 唯一条目）。
//   3. RelayControlChannel：Hello（设备挑战签名）→ HelloAck。
//   4. RelayControlChannel：Dial(peer) → DialResult(ok)。
//      此刻 relay 已把两端接线，并开始纯字节泵送。
//   5. SIGMA 握手 → 派生会话密钥 → EncryptedSession。
//   6. SessionPump：控制流往返（Ping/Pong 由泵自动应答；
//      SessionControl(probe) → 对端回 SessionControl(probe-ack) 由本类处理）。
//
// 角色约定：device_id 字典序小者为 SIGMA Initiator（两侧独立计算，无需协商），
// 与 relay 的接线、registry 的 device_a <= device_b 排序同源。
//
// 失败处理：任一步失败都直接返回，交由 RelayClient 退避重连；
// 每次重连都是"完整重握手"（DESIGN.md 决策），因此本类不保存跨连接的会话状态。
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Frames;
using DeskLink.Protocol.Handshake;
using DeskLink.Protocol.Session;
using DeskLink.Service.Configuration;
using DeskLink.Service.FileTransfer;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;

namespace DeskLink.Service.Session;

/// <summary>
/// 中继加密会话驱动器：控制握手 + SIGMA + 加密收发 + 控制流往返。
/// </summary>
public sealed class RelaySessionRunner : IAsyncDisposable
{
    private readonly KeyStore _keyStore;
    private readonly PairingStore _pairings;
    private readonly ServiceOptions _options;
    private readonly Action<string>? _log;
    private readonly object _gate = new();

    private SessionPump? _pump;
    private EncryptedSession? _session;

    /// <summary>本次会话的控制流探针（每次建立会话新建一个，避免跨重连残留状态）。</summary>
    private SessionControlProbe? _probe;

    /// <summary>
    /// 控制握手（Hello + 最多 15 次 Dial 重试）的总预算。
    ///
    /// 为什么必须有超时：传输层 ReceiveAsync 只受 CancellationToken 约束，
    /// relay 若"接受连接但一直不回应"，客户端会永久卡在读上——而 RelayClient 的
    /// 重连循环要等 OnReady 返回才会推进，于是整个实例就此僵住。
    /// 20s 足够覆盖 Hello(RTT) + 15×400ms 重试 + 余量。
    /// </summary>
    private static readonly TimeSpan ControlHandshakeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>SIGMA 握手总预算（4 条定长消息，正常在毫秒级完成）。</summary>
    private static readonly TimeSpan SigmaTimeout = TimeSpan.FromSeconds(15);

    /// <summary>当前阶段（供状态上报）：idle / no_peer / handshaking / sigma / established / closed / error:*。</summary>
    public string State { get; private set; } = "idle";

    /// <summary>本次会话的对端 device_id（未解析时为 null）。</summary>
    public byte[]? PeerDeviceId { get; private set; }

    /// <summary>是否已完成一次控制流往返（SessionControl probe → ack）。</summary>
    public bool ControlRoundTripOk => _probe?.RoundTripOk ?? false;

    /// <summary>本次会话的 SIGMA 角色（device_id 字典序小者为 Initiator）；未建立时为 null。</summary>
    public HandshakeRole? Role { get; private set; }

    /// <summary>最近一次 Ping 的往返时延（毫秒）；无样本为 -1。</summary>
    public long LastRttMs => _pump?.LastRttMs ?? -1;

    /// <summary>
    /// 本次会话的文件传输引擎（P6）。
    ///
    /// 每次建立会话新建一个（与 SessionPump 同生命周期）；会话结束后置空。
    /// 上层（ServiceCore/管道 RPC）通过它发起上传/下载/列目录。
    /// </summary>
    public FileTransferEngine? FileEngine { get; private set; }

    /// <summary>阶段变化通知（日志/管道状态查询用）。</summary>
    public event Action<string>? OnStateChanged;

    public RelaySessionRunner(
        KeyStore keyStore,
        PairingStore pairings,
        ServiceOptions options,
        Action<string>? log = null)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        _pairings = pairings ?? throw new ArgumentNullException(nameof(pairings));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log;
    }

    /// <summary>
    /// 传输就绪回调：完成控制握手 → SIGMA → 加密收发，直到会话结束才返回。
    ///
    /// 返回 true 表示**本次传输期间成功建立过会话**（供 RelayClient 决定是否重置退避）。
    /// </summary>
    public async Task<bool> OnTransportReadyAsync(IRelayTransport transport, CancellationToken ct)
    {
        var established = false;
        try
        {
            // 1) 解析对端 device_id
            var peer = ResolvePeerDeviceId();
            if (peer is null)
            {
                // 没有可用对端：保持连接但什么也不做，等待上层配置变更后重连。
                SetState("no_peer");
                _log?.Invoke("RelaySession: no peer configured (--peer or a single pairing required)");
                await WaitForCancelAsync(ct).ConfigureAwait(false);
                return false;
            }
            PeerDeviceId = peer;
            Role = null;
            _log?.Invoke($"RelaySession: peer={Convert.ToHexString(peer)[..16]} transport={transport.Kind}");

            // 2) 控制握手（Hello + Dial）
            //
            // 必须带超时：传输层的 ReceiveAsync 只受 ct 约束，若 relay 接受连接后
            // 一直不回应，这里会永久阻塞，RelayClient 的重连循环也永远不会触发。
            SetState("handshaking");
            var keys = _keyStore.LoadOrCreate();
            var host = new E2ESessionHost(keys, _log);

            RelayHandshakeResult hs;
            using (var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                phaseCts.CancelAfter(ControlHandshakeTimeout);
                try
                {
                    hs = await host.ControlHandshakeAsync(transport, peer, phaseCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    SetState("handshake_timeout");
                    _log?.Invoke($"RelaySession: control handshake timed out after {ControlHandshakeTimeout.TotalSeconds:0}s");
                    return false;
                }
            }
            if (!hs.Ok)
            {
                SetState($"handshake_failed:{hs.Reason}");
                _log?.Invoke($"RelaySession: control handshake failed reason={hs.Reason}");
                return false;
            }

            // 3) SIGMA → 会话密钥
            SetState("sigma");
            E2ESessionResult e2e;
            using (var phaseCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                phaseCts.CancelAfter(SigmaTimeout);
                try
                {
                    e2e = await host.EstablishE2EAsync(transport, peer, phaseCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    SetState("sigma_timeout");
                    _log?.Invoke($"RelaySession: sigma timed out after {SigmaTimeout.TotalSeconds:0}s");
                    return false;
                }
            }
            if (!e2e.Ok || e2e.Session is null)
            {
                SetState($"sigma_failed:{e2e.Detail}");
                _log?.Invoke($"RelaySession: sigma failed detail={e2e.Detail}");
                return false;
            }

            // 4) 加密收发泵
            Role = e2e.Role;
            var pump = new SessionPump(transport, e2e.Session, _log);
            // 控制流处理器：SessionControl 探针应答/确认（与直连路径共用同一实现）。
            var probe = new SessionControlProbe(_log);
            probe.OnRoundTripOk += () => SetState("established");
            probe.Attach(pump);
            // 文件流处理器（P6）：完整文件传输引擎（scope/分块/ack 续传/BLAKE3/.part 原子改名）。
            // 引擎的入站处理只入队、不阻塞读循环；发送在各自任务里跑。
            var fileEngine = new FileTransferEngine(
                new FileTransferScope(_options.FileScopeRoots), log: _log);
            fileEngine.Attach(new SessionPumpFileSink(pump), ct);
            pump.Register(ProtocolConstants.LogicalStream.File, fileEngine.OnFrame);
            lock (_gate)
            {
                _pump = pump;
                _session = e2e.Session;
                _probe = probe;
                FileEngine = fileEngine;
            }
            pump.Start(ct);
            SetState("established");
            // 会话确实建立起来了：允许 RelayClient 重置退避。
            established = true;
            _log?.Invoke($"RelaySession: e2e established role={e2e.Role}");

            // 5) 控制流往返探针：主动发一条 SessionControl(probe)，等对端回 ack。
            await probe.SendProbeAsync(ct).ConfigureAwait(false);

            // 5.5) 主动测一次 RTT（P5 起为真实值：Ping 带时间戳，Pong 原样回显）。
            //      心跳循环 15s 才发第一次 Ping，这里先测一次让状态查询立刻有数。
            var rttMs = await pump.MeasureRttAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            if (rttMs.HasValue)
            {
                _log?.Invoke($"RelaySession: ping rtt={rttMs.Value}ms");
            }
            else
            {
                _log?.Invoke("RelaySession: ping rtt probe did not complete (session may be closing)");
            }

            // 6) 保持会话，直到传输断开 / 被取消。
            await pump.Completion.ConfigureAwait(false);
            SetState("closed");
            _log?.Invoke("RelaySession: session ended");
        }
        catch (OperationCanceledException)
        {
            SetState("stopped");
        }
        catch (Exception ex)
        {
            SetState($"error:{ex.GetType().Name}");
            _log?.Invoke($"RelaySession: aborted {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            await TeardownAsync().ConfigureAwait(false);
        }
        return established;
    }

    /// <summary>
    /// 解析对端 device_id：
    ///   1. --peer 显式给出（hex 64 / base64 32B）；
    ///   2. 否则回退 pairings.json —— 恰好一个已配对设备时由其实 Ed25519 公钥推导。
    /// 解析失败返回 null（调用方保持连接但不动）。
    /// </summary>
    private byte[]? ResolvePeerDeviceId()
    {
        if (!string.IsNullOrWhiteSpace(_options.PeerDeviceId))
        {
            var parsed = ParseDeviceId(_options.PeerDeviceId!);
            if (parsed is null)
            {
                _log?.Invoke($"RelaySession: --peer is not a valid device_id: {_options.PeerDeviceId}");
            }
            return parsed;
        }

        var list = _pairings.List();
        if (list.Count == 1)
        {
            try
            {
                var pub = Convert.FromBase64String(list[0].PeerPubB64);
                if (pub.Length == 32)
                {
                    // device_id = BLAKE3("desklink/device/v1" || ed25519_pub)
                    return E2ESessionHost.ComputeDeviceId(pub);
                }
            }
            catch (FormatException)
            {
                // 配对条目损坏：忽略，回退为"无对端"
            }
        }
        else if (list.Count > 1)
        {
            _log?.Invoke($"RelaySession: {list.Count} pairings present; specify --peer to disambiguate");
        }
        return null;
    }

    /// <summary>
    /// 解析 device_id 文本：支持 64 字符 hex（可带 0x 前缀）或 32 字节 base64。
    /// </summary>
    public static byte[]? ParseDeviceId(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }
        // hex（64 字符 = 32 字节）
        if (t.Length == 64 && IsHex(t))
        {
            try { return Convert.FromHexString(t); }
            catch (FormatException) { return null; }
        }
        // base64 → 32 字节
        try
        {
            var b = Convert.FromBase64String(t);
            if (b.Length == 32) return b;
        }
        catch (FormatException)
        {
            // 落到"解析失败"
        }
        return null;
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>等待取消（用于"无对端"时的挂起）。</summary>
    private static async Task WaitForCancelAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
    }

    /// <summary>释放本会话持有的泵与会话对象（幂等）。</summary>
    private async Task TeardownAsync()
    {
        SessionPump? pump;
        EncryptedSession? session;
        FileTransferEngine? fileEngine;
        lock (_gate)
        {
            pump = _pump;
            session = _session;
            fileEngine = FileEngine;
            _pump = null;
            _session = null;
            _probe = null;
            FileEngine = null;
        }
        if (fileEngine != null)
        {
            try { await fileEngine.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        }
        if (pump != null)
        {
            try { await pump.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        }
        session?.Dispose();
    }

    private void SetState(string state)
    {
        State = state;
        OnStateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await TeardownAsync().ConfigureAwait(false);
    }
}
