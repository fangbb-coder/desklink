// 中继控制通道：在已建立的传输（QUIC 流 / TLS 连接）上完成 relay 的控制握手。
//
// 流程（与 relayd 的 serveSession 状态机对应）：
//   1. 客户端 → relay：RelayHello（携带 device_id + 挑战签名）
//   2. relay → 客户端：RelayHelloAck（ok / 原因）
//   3. 双方都 Hello 后，任一端 → relay：RelayDial(peer_device_id)
//   4. relay → 客户端：RelayDialResult(ok)，中间可能夹 PeerEvent(ready)
//   5. 此后该字节流进入纯密文泵送：本类不再参与，交给 E2E 会话。
//
// 本类只处理"控制阶段"；它保证控制阶段的字节不会被漏读到业务流里
// （精确读帧头 + 按长度读 payload）。
using System.Buffers.Binary;
using DeskLink.Protocol.Relay;
using DeskLink.Service.Relay;

namespace DeskLink.Service.Session;

/// <summary>控制握手结果。</summary>
public sealed record RelayHandshakeResult(
    bool Ok,
    string Reason,
    RelayTransportKind Transport);

/// <summary>
/// 中继控制握手执行器。
/// </summary>
public sealed class RelayControlChannel
{
    private readonly IRelayTransport _transport;

    public RelayControlChannel(IRelayTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <summary>发送 Hello 并等待 HelloAck。</summary>
    public async Task<RelayHandshakeResult> HelloAsync(
        ushort protoVersion,
        ReadOnlyMemory<byte> deviceId,
        ReadOnlyMemory<byte> deviceIdHint,
        uint challengeId,
        long challengeUnix,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> signature,
        CancellationToken ct)
    {
        var payload = RelayHelloCodec.Encode(
            protoVersion, deviceId.Span, deviceIdHint.Span, challengeId, challengeUnix, nonce.Span, signature.Span);
        await SendAsync(RelayControlKind.Hello, payload, ct).ConfigureAwait(false);

        var frame = await ReadControlFrameAsync(ct).ConfigureAwait(false);
        if (frame.Kind != RelayControlKind.HelloAck)
        {
            return new RelayHandshakeResult(false, RelayReason.BadFrame, _transport.Kind);
        }
        var (ok, reason) = RelayStatusCodec.Decode(frame.Payload);
        return new RelayHandshakeResult(ok, reason, _transport.Kind);
    }

    /// <summary>
    /// 发送 Dial 并等待 DialResult。
    /// 中间的 PeerEvent（对端上下线/ready）会被跳过并上报给回调，不属于失败。
    /// </summary>
    public async Task<RelayHandshakeResult> DialAsync(
        ReadOnlyMemory<byte> peerDeviceId,
        Action<RelayPeerEventKind, byte[]>? onPeerEvent,
        CancellationToken ct)
    {
        await SendAsync(RelayControlKind.Dial, peerDeviceId.ToArray(), ct).ConfigureAwait(false);
        // 最多跳 16 个 PeerEvent，避免对端反复上下线导致无限循环。
        for (var i = 0; i < 16; i++)
        {
            var frame = await ReadControlFrameAsync(ct).ConfigureAwait(false);
            switch (frame.Kind)
            {
                case RelayControlKind.DialResult:
                {
                    var (ok, reason) = RelayStatusCodec.Decode(frame.Payload);
                    return new RelayHandshakeResult(ok, reason, _transport.Kind);
                }
                case RelayControlKind.PeerEvent:
                {
                    if (frame.Payload.Length == 1 + RelayControl.DeviceIdSize)
                    {
                        var kind = (RelayPeerEventKind)frame.Payload[0];
                        var id = frame.Payload.AsSpan(1).ToArray();
                        onPeerEvent?.Invoke(kind, id);
                    }
                    break;
                }
                case RelayControlKind.Bye:
                    return new RelayHandshakeResult(false, RelayReason.Internal, _transport.Kind);
                default:
                    // 忽略 Ping/Pong 等无关帧
                    break;
            }
        }
        return new RelayHandshakeResult(false, RelayReason.Internal, _transport.Kind);
    }

    /// <summary>发送 Bye（优雅断开）。</summary>
    public async Task SendByeAsync(CancellationToken ct)
    {
        try
        {
            await SendAsync(RelayControlKind.Bye, RelayStatusCodec.Encode(false, RelayReason.Ok), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // 尽力而为
        }
    }

    private Task SendAsync(RelayControlKind kind, byte[] payload, CancellationToken ct)
    {
        var frame = RelayControl.Encode(kind, payload);
        return _transport.SendAsync(frame, ct).AsTask();
    }

    /// <summary>
    /// 精确读一帧控制帧。
    /// 关键：先读满 3 字节帧头，再按声明的长度读 payload——
    /// 不能"往大缓冲里读一次再试探解码"，否则会把下一帧/业务密文的头部吞进来，
    /// 导致后续业务流错位（relay 侧数据面零容错）。
    /// </summary>
    private async Task<RelayFrame> ReadControlFrameAsync(CancellationToken ct)
    {
        var header = await ReadExactAsync(RelayControl.HeaderSize, ct).ConfigureAwait(false);
        var plen = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1, 2));
        if (plen > RelayControl.MaxPayload)
        {
            throw new InvalidDataException($"relay control payload too large: {plen}");
        }
        byte[] payload = Array.Empty<byte>();
        if (plen > 0)
        {
            payload = await ReadExactAsync(plen, ct).ConfigureAwait(false);
        }
        return new RelayFrame((RelayControlKind)header[0], payload);
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await _transport.ReceiveAsync(buf.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("relay transport closed during control handshake");
            }
            got += n;
        }
        return buf;
    }
}
