// TCP/TLS 中继传输（DESIGN.md QUIC 兜底；Win10 缺 QUIC 加密 API / UDP 被封时回落）。
//
// 协议：System.Net.Security.SslStream 跑标准 TLS 1.3（.NET 9 默认最小 TLS 1.2，握手时协商 1.3）。
//       上层 RelayClient 加 TCP mux 外层头（[u32 len][u8 sid]）。
//
// 证书校验（P5 起）：由构造时注入的 IRelayTrustPolicy 决定，生产用
//   RelayTrustPolicy（TOFU 指纹固定）；测试/显式关闭才传 RelayTrustPolicy.AcceptAny。
//   早期实现是无条件 `=> true`，等于中继链路上没有防中间人能力。
//
// 约束：
//   - 不做应用层重试；连接断开即抛，由 RelayClient 退避重连。
//   - E2E（SIGMA）握手在 TLS 之上，由 RelaySessionRunner 完成。
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace DeskLink.Service.Relay;

public sealed class TcpTlsTransport : IRelayTransport
{
    private readonly IRelayTrustPolicy _trust;
    private readonly Action<string>? _log;

    private TcpClient? _tcp;
    private SslStream? _ssl;
    private NetworkStream? _raw;
    private string _host = "";
    private int _port;
    private bool _closed;

    public RelayTransportKind Kind => RelayTransportKind.TcpTls;
    public EndPoint? LocalEndPoint => _tcp?.Client?.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _tcp?.Client?.RemoteEndPoint;

    /// <summary>
    /// 构造传输。
    /// </summary>
    /// <param name="trust">
    /// 证书信任策略。**生产必须传 RelayTrustPolicy（TOFU）**；
    /// 只有测试或用户显式关闭 TOFU 时才传 RelayTrustPolicy.AcceptAny。
    /// 默认参数仅为兼容既有测试调用点，新代码请显式传入。
    /// </param>
    public TcpTlsTransport(IRelayTrustPolicy? trust = null, Action<string>? log = null)
    {
        _trust = trust ?? RelayTrustPolicy.AcceptAny;
        _log = log;
    }

    public async ValueTask ConnectAsync(Uri relay, CancellationToken ct)
    {
        if (relay.Scheme is not ("tls" or "https"))
        {
            throw new ArgumentException("TcpTlsTransport expects tls:// or https:// scheme", nameof(relay));
        }
        if (relay.IsDefaultPort)
        {
            throw new ArgumentException("TcpTlsTransport requires explicit port (no defaults)", nameof(relay));
        }

        _host = relay.Host;
        _port = relay.Port;

        _tcp = new TcpClient { NoDelay = true };
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(10));

        await _tcp.ConnectAsync(relay.Host, relay.Port, connectCts.Token).ConfigureAwait(false);
        _raw = _tcp.GetStream();

        _ssl = new SslStream(_raw, leaveInnerStreamOpen: false, UserCertificateValidationCallback);
        var sslOpts = new SslClientAuthenticationOptions
        {
            TargetHost = relay.Host,
            RemoteCertificateValidationCallback = UserCertificateValidationCallback,
        };
        await _ssl.AuthenticateAsClientAsync(sslOpts, connectCts.Token).ConfigureAwait(false);
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_ssl == null) throw new InvalidOperationException("not connected");
        await _ssl.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_ssl == null) throw new InvalidOperationException("not connected");
        return await _ssl.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 中断 pending 读：只关底层 socket，不碰 SslStream。
    /// 这样 pending 的 <c>SslStream.ReadAsync</c> 会立刻以 IOException 返回，
    /// 读循环随即退出；之后再由 CloseAsync 安全释放 SslStream。
    /// </summary>
    public ValueTask AbortAsync()
    {
        try { _tcp?.Close(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        if (_closed) return ValueTask.CompletedTask;
        _closed = true;
        try { _tcp?.Close(); } catch { /* best-effort */ }
        try { _ssl?.Dispose(); } catch { /* best-effort */ }
        try { _tcp?.Dispose(); } catch { /* best-effort */ }
        _ssl = null;
        _tcp = null;
        _raw = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }

    private bool UserCertificateValidationCallback(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // 委托给注入的信任策略（生产 = TOFU 指纹固定）。
        // 注意这里不再无条件 return true——那会让中继链路失去防中间人能力。
        var decision = _trust.Evaluate(_host, _port, certificate, sslPolicyErrors);
        if (!decision.Trusted)
        {
            _log?.Invoke($"TcpTlsTransport: 证书被拒 {_host}:{_port} — {decision.Reason}");
        }
        return decision.Trusted;
    }
}
