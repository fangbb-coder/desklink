// SessionPump → 文件引擎的发送适配（P6）。
//
// 文件引擎只依赖一个"把负载发到 File 逻辑流"的极小接口，这里把它落到
// SessionPump.SendFrameAsync(type, payload, StreamId.FileBase)。
// 这样引擎完全不知道 SessionPump / 传输 / 加密的存在，单测可以换成内存 sink。
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Mux;
using DeskLink.Service.FileTransfer;

namespace DeskLink.Service.Session;

/// <summary>把文件流帧发到会话的 File 逻辑流。</summary>
public sealed class SessionPumpFileSink : IFileFrameSink
{
    private readonly SessionPump _pump;

    public SessionPumpFileSink(SessionPump pump)
        => _pump = pump ?? throw new ArgumentNullException(nameof(pump));

    public Task SendAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken ct)
        => _pump.SendFrameAsync(type, payload, StreamId.FileBase, ct);
}
