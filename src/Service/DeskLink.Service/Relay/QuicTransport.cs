// QUIC 中继传输（DESIGN.md 中继路径 QUIC 优先）。
//
// .NET 9 自带 System.Net.Quic，底层走 MsQuic；Win11+ 可用，Win10 缺加密 API。
// QuicConnection.OpenOutboundStreamAsync 返回双向流；此处取 1 条单向连接外的双向流
// （DESIGN.md："QUIC=原生流"），业务 mux 由调用方决定（每个内层帧一个流）。
//
// P4 阶段：单流承载字节流（与 TCP/TLS 同样的应用层接口）；P5 可改用多流并发。
//
// 注意：
//   - DefaultStreamErrorCode / DefaultCloseErrorCode 必填（DESIGN.md 风险回顾）。
//   - MaxInboundBidirectionalStreams 显式调大（默认 100 → 这里 1024 容纳多逻辑流并发）。
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace DeskLink.Service.Relay;

public sealed class QuicTransport : IRelayTransport
{
    private QuicConnection? _conn;
    private QuicStream? _stream;
    private readonly IRelayTrustPolicy _trust;
    private readonly Action<string>? _log;
    private string _host = "";
    private int _port;
    private bool _closed;

    public RelayTransportKind Kind => RelayTransportKind.Quic;
    public EndPoint? LocalEndPoint => _conn?.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _conn?.RemoteEndPoint;

    /// <summary>
    /// 构造传输。
    /// </summary>
    /// <param name="trust">
    /// 证书信任策略。**生产必须传 RelayTrustPolicy（TOFU）**；
    /// 只有测试或用户显式关闭 TOFU 时才传 RelayTrustPolicy.AcceptAny。
    /// </param>
    public QuicTransport(IRelayTrustPolicy? trust = null, Action<string>? log = null)
    {
        _trust = trust ?? RelayTrustPolicy.AcceptAny;
        _log = log;
    }

    public static bool IsAvailable()
    {
        // QuicListener / QuicConnection 在没有 MsQuic 时会抛 PlatformNotSupportedException
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20000) // Win11
            || OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS();
    }

    public async ValueTask ConnectAsync(Uri relay, CancellationToken ct)
    {
        if (relay.Scheme != "quic" && relay.Scheme != "https")
        {
            throw new ArgumentException("QuicTransport expects quic:// or https:// scheme", nameof(relay));
        }

        _host = relay.Host;
        _port = relay.Port;

        var sslOpts = new SslClientAuthenticationOptions
        {
            TargetHost = relay.Host,
            // 证书校验交给注入的信任策略（生产 = TOFU 指纹固定）。
            RemoteCertificateValidationCallback = ValidateRemoteCertificate,
            ApplicationProtocols = new List<SslApplicationProtocol>
            {
                new SslApplicationProtocol("desklink-relay-v1"),
            },
        };

        var connOpts = new QuicClientConnectionOptions
        {
            DefaultStreamErrorCode = 0x0A, // 业务定义：10 = application error
            DefaultCloseErrorCode = 0x0B,  // 业务定义：11 = graceful close
            MaxInboundBidirectionalStreams = 1024,
            MaxInboundUnidirectionalStreams = 1024,
            ClientAuthenticationOptions = sslOpts,
            RemoteEndPoint = new IPEndPoint(Dns.GetHostAddresses(relay.Host).First(), relay.Port),
        };

        // QuicConnection 没有公共无参构造函数；出站连接统一走静态 ConnectAsync。
        // 10s 超时用 CancellationTokenSource 施加在 ConnectAsync 上。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        _conn = await QuicConnection.ConnectAsync(connOpts, cts.Token).ConfigureAwait(false);

        _stream = await _conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token).ConfigureAwait(false);
    }

    /// <summary>证书校验：委托给注入的信任策略（生产 = TOFU 指纹固定）。</summary>
    private bool ValidateRemoteCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        var decision = _trust.Evaluate(_host, _port, certificate, sslPolicyErrors);
        if (!decision.Trusted)
        {
            _log?.Invoke($"QuicTransport: 证书被拒 {_host}:{_port} — {decision.Reason}");
        }
        return decision.Trusted;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        return await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    /// <summary>中断 pending 读：abort QUIC 流（不释放对象）。</summary>
    public ValueTask AbortAsync()
    {
        try { _stream?.Abort(QuicAbortDirection.Both, 0x0A); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    public async ValueTask CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try
        {
            if (_stream != null) await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        try
        {
            if (_conn != null) await _conn.CloseAsync(0x0B).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        try
        {
            if (_conn != null) await _conn.DisposeAsync().ConfigureAwait(false);
        }
        catch { /* best-effort */ }
        _stream = null;
        _conn = null;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}
