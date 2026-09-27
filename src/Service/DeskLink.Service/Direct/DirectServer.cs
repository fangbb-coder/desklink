// P5.5 局域网直连服务端（被控端）。
//
// 职责：
//   - 监听 TCP 47200（TLS）+ UDP 47200（QUIC）。
//   - 新连接先收一个 32 字节 device_id（明文），查本地配对列表；
//     未配对 → 立刻关闭（不弹指纹确认，不依赖 relay 挑战）。
//   - 已配对 → 走 SIGMA 握手 → 建立 EncryptedSession → 交 SessionPump。
//   - 支持撤销：PairingStore 移除配对后，现有会话由上层（ServiceCore）通过
//     CancellationToken 触发关闭；新连接在配对检查时即被拒。
//
// 与 relay 路径的区别：
//   - 无 relay 中间人：Hello 后不经过 relay 的 Dial/接线，直接 SIGMA。
//   - 无 registry 查询：配对关系来自本地 pairings.json。
//   - TLS 证书：生产环境用 installer 部署的统一自签证书；测试用内存证书。
//
// 线程模型：
//   - StartAsync 开启监听并阻塞在 accept 循环（类似 relayd）。
//   - 每个连接一个 Task（不限制并发数——已配对设备数通常极少）。
//   - StopAsync 取消 CTS → accept 循环退出 → 现有连接由 ct 传播关闭。

using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Session;
using DeskLink.Service.Configuration;
using DeskLink.Service.FileTransfer;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;

namespace DeskLink.Service.Direct;

/// <summary>直连会话上下文（供外部查询/管理）。</summary>
public sealed class DirectSession : IDisposable
{
    public byte[] PeerDeviceId { get; }
    public SessionPump Pump { get; }
    public DateTime EstablishedAt { get; }

    /// <summary>本会话的文件传输引擎（P6）；由宿主在 OnSessionEstablished 里装配。</summary>
    public FileTransferEngine? FileEngine { get; set; }

    /// <summary>会话是否已被显式关闭（撤销/顶替/停止）。</summary>
    public bool IsClosed => Volatile.Read(ref _closedFlag) != 0;

    private int _closedFlag;
    private Task? _closeTask;
    private readonly object _closeGate = new();

    public DirectSession(byte[] peerDeviceId, SessionPump pump)
    {
        PeerDeviceId = peerDeviceId;
        Pump = pump;
        EstablishedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// 关闭本会话并**等待收尾完成**（幂等，多次调用返回同一个 Task）。
    ///
    /// 关键：绝不能写成 `_ = Pump.DisposeAsync()` 的 fire-and-forget——那样调用方
    /// （StopAsync / 撤销）会在收尾尚未完成时就返回，随后进程退出/目录删除与
    /// 后台的 SslStream 释放并发，表现为 native 层"测试宿主进程意外退出"。
    /// </summary>
    public Task CloseAsync()
    {
        lock (_closeGate)
        {
            return _closeTask ??= CloseCoreAsync();
        }
    }

    private async Task CloseCoreAsync()
    {
        Volatile.Write(ref _closedFlag, 1);
        try { await Pump.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
    }

    /// <summary>请求关闭（不等待）。同步调用点（撤销/顶替）用这个，随后由 CloseAsync 收尾。</summary>
    public void RequestClose() => _ = CloseAsync();

    public void Dispose()
    {
        // 不能无限 Wait：底层 SslStream.ReadAsync 在 cancellation 后可能不返回，
        // 导致 Dispose 永远挂起（测试/进程退出时表现为 host 崩溃）。
        try { CloseAsync().Wait(TimeSpan.FromSeconds(3)); } catch { /* best-effort */ }
    }
}

/// <summary>局域网直连服务端。</summary>
public sealed class DirectServer : IAsyncDisposable
{
    private readonly PairingStore _pairings;
    private readonly KeyStore _keys;
    private readonly ServiceOptions _options;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<DirectSession> _sessions = new();
    private readonly object _sessionsGate = new();

    private TcpListener? _tcpListener;
    private QuicListener? _quicListener;
    private Task? _tcpLoop;
    private Task? _quicLoop;

    public int Port { get; }
    public bool IsRunning => _tcpLoop != null || _quicLoop != null;

    /// <summary>QUIC 监听是否成功建立（false = 仅 TCP/TLS）。</summary>
    public bool QuicListening => _quicLoop != null;

    /// <summary>
    /// 会话建立回调（在 pump.Start 之前调用）。
    ///
    /// 用途：宿主（ServiceHost）在此为直连会话注册业务流处理器（控制流探针、
    /// 文件流等），保证在读到第一帧之前处理器已就位。为 null 时只建立空会话
    /// （仅供握手/往返测试使用）。
    /// </summary>
    public Action<DirectSession>? OnSessionEstablished { get; set; }

    /// <summary>当前活跃会话（只读快照）。</summary>
    public IReadOnlyList<DirectSession> Sessions
    {
        get { lock (_sessionsGate) { return _sessions.ToList(); } }
    }

    public DirectServer(
        PairingStore pairings,
        KeyStore keys,
        ServiceOptions options,
        int port = 47200,
        Action<string>? log = null,
        bool enableQuic = true)
    {
        _pairings = pairings ?? throw new ArgumentNullException(nameof(pairings));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Port = port;
        _log = log;
        _enableQuic = enableQuic;
    }

    private readonly bool _enableQuic;

    public async Task StartAsync(CancellationToken ct)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);

        // TCP/TLS
        _tcpListener = new TcpListener(IPAddress.Any, Port);
        _tcpListener.Start();
        _tcpLoop = Task.Run(() => TcpAcceptLoop(linked.Token), linked.Token);
        _log?.Invoke($"DirectServer: TCP listening on :{Port}");

        // QUIC（如果可用）
        if (_enableQuic && QuicTransport.IsAvailable())
        {
            try
            {
                var cert = await SelfSignedCert.LoadOrCreateAsync(_options.DataDir, "direct");
                var quicOpts = new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Any, Port),
                    ListenBacklog = 128,
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        new SslApplicationProtocol("desklink-direct-v1"),
                    },
                    ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
                    {
                        ServerAuthenticationOptions = new SslServerAuthenticationOptions
                        {
                            ServerCertificate = cert,
                        },
                    }),
                };
                _quicListener = await QuicListener.ListenAsync(quicOpts, linked.Token).ConfigureAwait(false);
                _quicLoop = Task.Run(() => QuicAcceptLoop(linked.Token), linked.Token);
                _log?.Invoke($"DirectServer: QUIC listening on :{Port}");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"DirectServer: QUIC listener failed ({ex.GetType().Name}), falling back to TCP only");
            }
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _tcpListener?.Stop();

        // 先请求关闭所有会话，再逐个等待收尾完成（CloseAsync 幂等，返回同一个 Task）。
        List<DirectSession> snapshot;
        lock (_sessionsGate) { snapshot = _sessions.ToList(); _sessions.Clear(); }
        foreach (var s in snapshot)
        {
            try { s.RequestClose(); } catch { /* best-effort */ }
        }

        // 等 accept 循环退出后再释放监听器（避免"释放中仍 Accept"的 native 竞态）。
        var loops = new[] { _tcpLoop, _quicLoop }.Where(t => t != null).ToArray();
        if (loops.Length > 0)
        {
            try { await Task.WhenAll(loops!).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* 尽力关闭 */ }
        }
        if (_quicListener != null)
        {
            try { await _quicListener.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            _quicListener = null;
        }

        // 必须真正 await 会话收尾：否则 StopAsync 返回后进程/目录就没了，
        // 而后台仍在释放 SslStream → native 崩溃。
        foreach (var s in snapshot)
        {
            try { await s.CloseAsync().ConfigureAwait(false); } catch { /* best-effort */ }
        }
    }

    private async Task TcpAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _ = Task.Run(() => HandleTcpClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log?.Invoke($"DirectServer: TCP accept error {ex.GetType().Name}");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task QuicAcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var conn = await _quicListener!.AcceptConnectionAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleQuicConnectionAsync(conn, ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log?.Invoke($"DirectServer: QUIC accept error {ex.GetType().Name}");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            // 1) 读对端 device_id（32 字节，无长度前缀——直连控制阶段极简）
            var peerId = await ReadExactAsync(stream, 32, ct).ConfigureAwait(false);
            if (peerId == null) return;

            // 2) 配对检查
            if (!IsPaired(peerId))
            {
                _log?.Invoke($"DirectServer: rejecting unpaired peer {Convert.ToHexString(peerId)[..16]}...");
                return;
            }

            // 3) TLS 握手（内存证书）
            // leaveInnerStreamOpen=true：避免 dispose SslStream 时连带 dispose NetworkStream，
            // 这样我们可以在 SslStream ReadAsync 进行中时先关闭底层流，安全地唤醒读循环。
            var cert = await SelfSignedCert.LoadOrCreateAsync(_options.DataDir, "direct");
            var ssl = new SslStream(stream, leaveInnerStreamOpen: true);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = cert,
                    ClientCertificateRequired = false,
                },
                ct).ConfigureAwait(false);

            // 4) SIGMA → SessionPump（用已连接的 ssl 构造一个最小适配传输）
            await RunE2EAsync(peerId, new ConnectedTcpTransport(ssl, stream), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"DirectServer: TCP handler error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleQuicConnectionAsync(QuicConnection conn, CancellationToken ct)
    {
        // 会话一旦交给 RunE2EAsync，连接所有权随之转移（由 ConnectedQuicTransport 释放）；
        // 提前 return 的路径必须自己释放，避免连接泄漏。
        var handedOff = false;
        try
        {
            var stream = await conn.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
            // QUIC 下 device_id 放在流的前 32 字节
            var peerId = await ReadExactQuicAsync(stream, 32, ct).ConfigureAwait(false);
            if (peerId == null) return;

            if (!IsPaired(peerId))
            {
                _log?.Invoke($"DirectServer: QUIC rejecting unpaired peer {Convert.ToHexString(peerId)[..16]}...");
                await conn.CloseAsync(0x01, ct).ConfigureAwait(false);
                return;
            }

            handedOff = true;
            await RunE2EAsync(peerId, new ConnectedQuicTransport(conn, stream), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"DirectServer: QUIC handler error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (!handedOff)
            {
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            }
        }
    }

    private async Task RunE2EAsync(byte[] peerId, IRelayTransport transport, CancellationToken ct)
    {
        var host = new E2ESessionHost(_keys.LoadOrCreate(), _log);
        E2ESessionResult e2e;
        try
        {
            e2e = await host.EstablishE2EAsync(transport, peerId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"DirectServer: SIGMA threw {ex.GetType().Name}: {ex.Message}");
            try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
            return;
        }
        if (!e2e.Ok || e2e.Session is null)
        {
            _log?.Invoke($"DirectServer: SIGMA failed: {e2e.Detail}");
            try { await transport.CloseAsync().ConfigureAwait(false); } catch { }
            return;
        }

        var pump = new SessionPump(transport, e2e.Session, _log);
        var session = new DirectSession(peerId, pump);

        // 顶替同对端的旧会话：同一设备重复连入时保留最新一条，避免旧连接泄漏。
        List<DirectSession> superseded;
        lock (_sessionsGate)
        {
            superseded = _sessions.Where(s => s.PeerDeviceId.AsSpan().SequenceEqual(peerId)).ToList();
            foreach (var s in superseded) _sessions.Remove(s);
            _sessions.Add(session);
        }
        foreach (var s in superseded)
        {
            _log?.Invoke($"DirectServer: superseding previous session for peer={Convert.ToHexString(peerId)[..16]}...");
            try { s.RequestClose(); } catch { /* best-effort */ }
        }

        // 业务处理器必须在 pump.Start 之前注册，否则可能漏掉对端立即发来的首帧。
        OnSessionEstablished?.Invoke(session);
        pump.Start(ct);

        _log?.Invoke($"DirectServer: session established peer={Convert.ToHexString(peerId)[..16]}...");

        try
        {
            await pump.Completion.ConfigureAwait(false);
        }
        finally
        {
            lock (_sessionsGate) _sessions.Remove(session);
            var engine = session.FileEngine;
            session.FileEngine = null;
            if (engine is not null)
            {
                try { await engine.DisposeAsync().ConfigureAwait(false); } catch { }
            }
            try { await pump.DisposeAsync().ConfigureAwait(false); } catch { }
            e2e.Session.Dispose();
        }
    }

    /// <summary>
    /// 关闭与指定对端 device_id 匹配的全部直连会话（撤销/踢线）。
    /// 返回被关闭的会话数。
    /// </summary>
    public int CloseSessionsFor(ReadOnlySpan<byte> peerDeviceId)
    {
        // Span 不能被 lambda 捕获（ref-like），先落到数组。
        var target = peerDeviceId.ToArray();
        List<DirectSession> matches;
        lock (_sessionsGate)
        {
            matches = _sessions.Where(s => s.PeerDeviceId.AsSpan().SequenceEqual(target)).ToList();
            foreach (var s in matches) _sessions.Remove(s);
        }
        foreach (var s in matches)
        {
            _log?.Invoke($"DirectServer: closing session for revoked peer={Convert.ToHexString(target)[..16]}...");
            try { s.RequestClose(); } catch { /* best-effort */ }
        }
        return matches.Count;
    }

    /// <summary>关闭全部直连会话（停机/全局撤销）。返回被关闭的会话数。</summary>
    public int CloseAllSessions()
    {
        List<DirectSession> all;
        lock (_sessionsGate) { all = _sessions.ToList(); _sessions.Clear(); }
        foreach (var s in all) { try { s.RequestClose(); } catch { /* best-effort */ } }
        return all.Count;
    }

    private bool IsPaired(byte[] peerDeviceId)
    {
        // 将 device_id 与配对列表中的 Ed25519 公钥逐一比较：
        // 配对列表存的是 base64 公钥，device_id = BLAKE3("desklink/device/v1" || pub)。
        foreach (var p in _pairings.List())
        {
            try
            {
                var pub = Convert.FromBase64String(p.PeerPubB64);
                if (pub.Length != 32) continue;
                var id = E2ESessionHost.ComputeDeviceId(pub);
                if (id.AsSpan().SequenceEqual(peerDeviceId)) return true;
            }
            catch (FormatException) { continue; }
        }
        return false;
    }

    private static async Task<byte[]?> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    private static async Task<byte[]?> ReadExactQuicAsync(QuicStream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
