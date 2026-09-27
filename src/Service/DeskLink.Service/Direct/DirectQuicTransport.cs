// P5.5 直连 QUIC 传输适配。
//
// QUIC 路径同样先发送 32 字节 device_id，再走 TLS 握手（QUIC 内置）。
// 目前仅在有 .NET 9+ QUIC API 的平台上可用。

using System.Net;
using System.Net.Quic;
using System.Net.Security;
using DeskLink.Service.Relay;

namespace DeskLink.Service.Direct;

public sealed class DirectQuicTransport : IRelayTransport
{
    private QuicConnection? _conn;
    private QuicStream? _stream;
    private bool _closed;

    public RelayTransportKind Kind => RelayTransportKind.Quic;
    public EndPoint? LocalEndPoint => _conn?.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _conn?.RemoteEndPoint;

    public async ValueTask ConnectAsync(string host, int port, byte[] deviceId, CancellationToken ct)
    {
        if (deviceId.Length != 32)
            throw new ArgumentException("deviceId must be 32 bytes", nameof(deviceId));

        var opts = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new DnsEndPoint(host, port),
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol("desklink-direct-v1"),
                },
            },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        _conn = await QuicConnection.ConnectAsync(opts, cts.Token).ConfigureAwait(false);
        _stream = await _conn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token).ConfigureAwait(false);

        // 发送 device_id
        await _stream.WriteAsync(deviceId.AsMemory(), cts.Token).ConfigureAwait(false);
        await _stream.FlushAsync(cts.Token).ConfigureAwait(false);
    }

    public ValueTask ConnectAsync(Uri relay, CancellationToken ct)
        => throw new InvalidOperationException("DirectQuicTransport uses ConnectAsync(host, port, deviceId, ct)");

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (_stream == null) throw new InvalidOperationException("not connected");
        return _stream.ReadAsync(buffer, ct);
    }

    /// <summary>中断 pending 读：abort QUIC 流（不释放对象）。</summary>
    public ValueTask AbortAsync()
    {
        try { _stream?.Abort(QuicAbortDirection.Both, 0x0A); } catch { }
        return ValueTask.CompletedTask;
    }

    public async ValueTask CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try { if (_stream != null) await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        if (_conn != null)
        {
            try { await _conn.CloseAsync(0x0B).ConfigureAwait(false); } catch { }
            try { await _conn.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
