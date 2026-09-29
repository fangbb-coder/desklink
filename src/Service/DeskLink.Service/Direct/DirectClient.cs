// P5.5 局域网直连客户端（控制端）。
//
// 职责：
//   - 给定目标 IP + port，选择 QUIC（优先）或 TCP/TLS 连接。
//   - 发送本地 device_id（32 字节）。
//   - 走 SIGMA 握手 → 建立 EncryptedSession → 返回 SessionPump。
//   - 由调用方（如 ServiceCore）在 pump 上注册业务流处理器。
//
// 与 RelayClient 的区别：
//   - 单连接、无重连循环——直连失败即抛异常，由调用方决定是否重试。
//   - 无退避逻辑——直连是用户主动行为，不适合自动重连。
//   - 目标是对端 IP，而非 relay URL。

using DeskLink.Protocol.Crypto;
using DeskLink.Protocol.Session;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;

namespace DeskLink.Service.Direct;

/// <summary>直连客户端连接结果。</summary>
public sealed class DirectConnectResult
{
    public bool Ok { get; init; }
    public string? Detail { get; init; }
    public SessionPump? Pump { get; init; }
    public EncryptedSession? Session { get; init; }

    /// <summary>实际使用的传输："quic" 或 "tcp-tls"。失败时为 null。</summary>
    public string? Transport { get; init; }
}

/// <summary>局域网直连客户端。</summary>
public sealed class DirectClient
{
    private readonly KeyStore _keys;
    private readonly Action<string>? _log;

    public DirectClient(KeyStore keys, Action<string>? log = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _log = log;
    }

    /// <summary>
    /// 连接到指定对端。返回的 pump 已经 Start，调用方只需注册处理器即可。
    /// </summary>
    /// <param name="peerDeviceId">
    /// **对端**（被控端）的 device_id —— 用于 SIGMA 角色判定与会话绑定。
    /// 注意与"本机 device_id"区分：后者由 <see cref="KeyStore"/> 派生，作为身份
    /// 发给对端做配对校验（见 DirectServer.IsPaired）。
    ///
    /// 早期实现把这两个值混为一谈（把本机 id 同时当作对端 id），导致当对端 id
    /// 字典序更大时两侧都判定自己为 Responder → 双方都在等 InitiatorHello →
    /// 永久死锁（实测约 50% 的直连握手挂死）。
    /// </param>
    /// <param name="startPump">
    /// 是否在返回前启动收发泵。默认 true（CLI 探测等"拿到就能用"的调用方）。
    /// <see cref="DirectDialer"/> 传 false：它必须先把控制流探针与文件传输引擎
    /// 挂到 pump 上再 Start，否则对端立即发来的首帧会落在没有处理器的窗口里被丢弃。
    /// </param>
    public async Task<DirectConnectResult> ConnectAsync(
        string host,
        int port,
        byte[] peerDeviceId,
        CancellationToken ct,
        bool startPump = true)
    {
        if (peerDeviceId is null || peerDeviceId.Length != 32)
        {
            return new DirectConnectResult { Ok = false, Detail = "peerDeviceId must be 32 bytes" };
        }

        var keys = _keys.LoadOrCreate();
        var selfDeviceId = E2ESessionHost.ComputeDeviceId(keys.KeyPair.Ed25519Public);

        IRelayTransport transport;
        try
        {
            // 发给对端的是**本机** device_id（对端据此查配对列表）。
            transport = await TryConnectAsync(host, port, selfDeviceId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DirectConnectResult { Ok = false, Detail = $"transport: {ex.Message}" };
        }

        var transportName = transport is DirectQuicTransport ? "quic" : "tcp-tls";
        var sessionHost = new E2ESessionHost(keys, _log);
        E2ESessionResult e2e;
        using (var sigmaCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            // SIGMA 必须有超时：否则对端"接受连接但不回应"会让调用方永久挂起
            // （直连没有 RelayClient 那样的退避重连循环来兜底）。
            sigmaCts.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                e2e = await sessionHost.EstablishE2EAsync(transport, peerDeviceId, sigmaCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
                return new DirectConnectResult { Ok = false, Detail = "sigma: timeout" };
            }
            catch (Exception ex)
            {
                try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
                return new DirectConnectResult { Ok = false, Detail = $"sigma: {ex.GetType().Name}: {ex.Message}" };
            }
        }
        if (!e2e.Ok || e2e.Session is null)
        {
            try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
            return new DirectConnectResult { Ok = false, Detail = $"sigma: {e2e.Detail}" };
        }

        var pump = new SessionPump(transport, e2e.Session, _log);
        if (startPump) pump.Start(ct);
        return new DirectConnectResult
        {
            Ok = true,
            Pump = pump,
            Session = e2e.Session,
            Transport = transportName,
        };
    }

    private async Task<IRelayTransport> TryConnectAsync(string host, int port, byte[] deviceId, CancellationToken ct)
    {
        // QUIC 优先
        if (QuicTransport.IsAvailable())
        {
            try
            {
                var quic = new DirectQuicTransport();
                await quic.ConnectAsync(host, port, deviceId, ct).ConfigureAwait(false);
                _log?.Invoke($"DirectClient: QUIC connected to {host}:{port}");
                return quic;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"DirectClient: QUIC failed ({ex.GetType().Name}), falling back to TCP");
            }
        }

        var tcp = new DirectTcpTransport();
        await tcp.ConnectAsync(host, port, deviceId, ct).ConfigureAwait(false);
        _log?.Invoke($"DirectClient: TCP/TLS connected to {host}:{port}");
        return tcp;
    }
}
