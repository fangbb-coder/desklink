// 中继传输抽象。
//
// 用途：DESIGN.md 中继路径传输——
//   QUIC 优先（Win11+），回落 TCP/TLS（Win10 无 QUIC 加密 API / UDP 被封）。
//
// 接口约定：
//   - ConnectAsync 仅做传输握手（QUIC handshake / TLS handshake）；
//     E2E 加密握手（SIGMA）在上层 RelayClient.RunOnce 完成后才进业务流。
//   - SendAsync / ReceiveAsync 走已建立连接上的应用层字节流；
//     业务帧的 mux 头（TCP 模式）在 RelayClient 层加，不在本接口管。
//   - CloseAsync 幂等。
//   - 实现线程模型：连接归调用方 Task 所有，不跨线程共享 Send/Receive 同一方向。
using System.Net;

namespace DeskLink.Service.Relay;

public enum RelayTransportKind
{
    Quic,
    TcpTls,
}

public interface IRelayTransport : IAsyncDisposable
{
    RelayTransportKind Kind { get; }
    EndPoint? LocalEndPoint { get; }
    EndPoint? RemoteEndPoint { get; }

    ValueTask ConnectAsync(Uri relay, CancellationToken ct);
    ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>
    /// 中断正在阻塞的 <see cref="ReceiveAsync"/>（**不释放对象**），让读循环尽快退出。
    ///
    /// 为什么需要它：`SslStream.Dispose()` 与并发的 `ReadAsync` 不是线程安全的，
    /// 并发时会在 native 层崩溃（表现为测试宿主进程"无堆栈崩溃"）。因此收尾必须分两步：
    ///   1) AbortAsync()  —— 关掉底层 socket / abort QUIC 流，让 pending read 立刻返回；
    ///   2) 等读循环真正退出后，再 CloseAsync()/DisposeAsync() 释放对象。
    /// 默认实现退化为 <see cref="CloseAsync"/>（对无此问题的实现足够）。
    /// </summary>
    ValueTask AbortAsync() => CloseAsync();

    ValueTask CloseAsync();
}
