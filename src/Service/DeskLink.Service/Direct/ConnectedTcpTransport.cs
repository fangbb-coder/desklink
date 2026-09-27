// P5.5 服务端侧：TLS 握手完成后，把已连接的 SslStream 包成 IRelayTransport。
// 与 DirectTcpTransport 的区别：后者负责 Connect（发 device_id + TLS 客户端握手）；
// 本类只负责包装已就绪的 SslStream，不重复做握手。

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using DeskLink.Service.Relay;

namespace DeskLink.Service.Direct;

public sealed class ConnectedTcpTransport : IRelayTransport
{
    private readonly SslStream _ssl;
    private readonly NetworkStream _inner;
    private bool _closed;

    public RelayTransportKind Kind => RelayTransportKind.TcpTls;
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    public ConnectedTcpTransport(SslStream ssl, NetworkStream inner)
    {
        _ssl = ssl ?? throw new ArgumentNullException(nameof(ssl));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public ValueTask ConnectAsync(Uri relay, CancellationToken ct)
        => throw new InvalidOperationException("ConnectedTcpTransport is already connected");

    public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        => new ValueTask(_ssl.WriteAsync(data, ct).AsTask());

    public ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        => _ssl.ReadAsync(buffer, ct);

    /// <summary>
    /// 中断 pending 读：只关底层 NetworkStream（socket），不碰 SslStream。
    /// pending 的 <c>SslStream.ReadAsync</c> 会立刻返回，读循环随即退出；
    /// 之后再由 CloseAsync 安全释放 SslStream。顺序反过来会在 native 层崩溃。
    /// </summary>
    public ValueTask AbortAsync()
    {
        try { _inner.Close(); } catch { }
        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync()
    {
        if (_closed) return ValueTask.CompletedTask;
        _closed = true;
        // 先关底层 NetworkStream：这会立刻让 SslStream.ReadAsync 返回 0 或异常，
        // 从而安全地结束读循环；然后再 dispose SslStream。
        // 顺序反过来（先 dispose SslStream）在 ReadAsync 进行中时会触发 native 崩溃。
        try { _inner.Close(); } catch { }
        try { _ssl.Dispose(); } catch { }
        try { _inner.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync();
}
