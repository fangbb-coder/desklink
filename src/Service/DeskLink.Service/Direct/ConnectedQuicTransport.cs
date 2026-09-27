// P5.5 服务端侧：QUIC 连接/流已就绪后，包成 IRelayTransport。

using System.Net;
using System.Net.Quic;
using DeskLink.Service.Relay;

namespace DeskLink.Service.Direct;

public sealed class ConnectedQuicTransport : IRelayTransport
{
    private readonly QuicConnection _conn;
    private readonly QuicStream _stream;
    private bool _closed;

    public RelayTransportKind Kind => RelayTransportKind.Quic;
    public EndPoint? LocalEndPoint => _conn.LocalEndPoint;
    public EndPoint? RemoteEndPoint => _conn.RemoteEndPoint;

    public ConnectedQuicTransport(QuicConnection conn, QuicStream stream)
    {
        _conn = conn ?? throw new ArgumentNullException(nameof(conn));
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    public ValueTask ConnectAsync(Uri relay, CancellationToken ct)
        => throw new InvalidOperationException("ConnectedQuicTransport is already connected");

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        => _stream.ReadAsync(buffer, ct);

    /// <summary>中断 pending 读：abort QUIC 流（不释放对象）。</summary>
    public ValueTask AbortAsync()
    {
        try { _stream.Abort(QuicAbortDirection.Both, 0x0A); } catch { }
        return ValueTask.CompletedTask;
    }

    public async ValueTask CloseAsync()
    {
        if (_closed) return;
        _closed = true;
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await _conn.CloseAsync(0x0B).ConfigureAwait(false); } catch { }
        try { await _conn.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
