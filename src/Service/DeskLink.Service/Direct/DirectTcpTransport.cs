// P5.5 直连 TCP/TLS 传输适配（复用 IRelayTransport 接口）。
//
// 与 Relay 的 TcpTlsTransport 的区别：
//   - 不做证书 TOFU（直连场景下证书是 installer 统一部署的自签证书，
//     或测试用的内存证书；TOFU 对直连意义不大，因为 IP 本身就是信任锚）。
//   - 连接目标是指定 IP:port，而非 relay URL。
//   - ConnectAsync 的签名改为 (host, port, deviceId) 以匹配直连语义：
//     连接后先发送 32 字节 device_id，再走 TLS。

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using DeskLink.Service.Relay;

namespace DeskLink.Service.Direct;

public sealed class DirectTcpTransport : IRelayTransport
{
    private TcpClient? _tcp;
    private SslStream? _ssl;
    private bool _closed;

    public RelayTransportKind Kind => RelayTransportKind.TcpTls;
    public EndPoint? LocalEndPoint => _tcp?.Client?.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _tcp?.Client?.RemoteEndPoint;

    public async ValueTask ConnectAsync(string host, int port, byte[] deviceId, CancellationToken ct)
    {
        if (deviceId.Length != 32)
            throw new ArgumentException("deviceId must be 32 bytes", nameof(deviceId));

        _tcp = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        await _tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        var raw = _tcp.GetStream();

        // 1) 先发自己的 device_id（32 字节，无长度前缀）
        await raw.WriteAsync(deviceId.AsMemory(), cts.Token).ConfigureAwait(false);
        await raw.FlushAsync(cts.Token).ConfigureAwait(false);

        // 2) TLS 客户端握手（接受任何证书——直连信任来自配对检查，而非 CA 链）
        // 注意：SslStream 构造器与 SslClientAuthenticationOptions 不能同时设回调；
        // .NET 9 下 AuthenticateAsClientAsync 的 options 参数已包含全部设置。
        _ssl = new SslStream(raw, leaveInnerStreamOpen: false);
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        }, cts.Token).ConfigureAwait(false);
    }

    public ValueTask ConnectAsync(Uri relay, CancellationToken ct)
        => throw new InvalidOperationException("DirectTcpTransport uses ConnectAsync(host, port, deviceId, ct)");

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_ssl == null) throw new InvalidOperationException("not connected");
        return new ValueTask(_ssl.WriteAsync(data, ct).AsTask());
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_ssl == null) throw new InvalidOperationException("not connected");
        return _ssl.ReadAsync(buffer, ct);
    }

    /// <summary>中断 pending 读：只关底层 socket，不碰 SslStream（见 IRelayTransport.AbortAsync）。</summary>
    public ValueTask AbortAsync()
    {
        try { _tcp?.Close(); } catch { }
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        if (_closed) return ValueTask.CompletedTask;
        _closed = true;
        // 先关底层 TCP，再关 SslStream：避免 SslStream 在 ReadAsync 进行中时被 dispose
        // 导致未处理的 native 崩溃（MsQuic / schannel 已知行为）。
        try { _tcp?.Close(); } catch { }
        try { _ssl?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
