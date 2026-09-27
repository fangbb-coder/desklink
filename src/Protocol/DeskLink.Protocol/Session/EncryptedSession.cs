// E2E 加密会话：把 SIGMA 握手派生的会话密钥包成一条"加解密 + 分帧"的通道。
//
// 职责边界（DESIGN.md）：
//   - 本类只管"会话建立之后"的收发：发送时 Seal(内层帧)，接收时 Open 后交给上层。
//   - 它**不**负责切帧：外层 [u32 len][u8 sid] 由 MuxFrame 负责，本类只按调用方
//     的指示决定要不要加（见 SealFrame 的 tcpMux 参数）。
//
// 帧管道模型：
//   发送：inner frame bytes → AeadSession.Seal → [u64 counter][cipher][tag16]
//         再包外层 [u32 len][u8 sid]（调用方传 tcpMux: true 时）。
//   接收：反向；Open 校验 counter 严格递增（AeadSession 内部保证）。
//
// 关于 tcpMux 的命名：它**不代表"只有 TCP 才加外层"**。TCP 与 QUIC 的流都只保证
// 有序字节、不保留消息边界，因此两条路径都需要显式长度前缀才能切帧
// （SessionPump 对两种传输都传 true）。参数名沿用历史叫法，语义是"加 mux 外层"。
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Crypto;
using DeskLink.Protocol.Frames;
using DeskLink.Protocol.Handshake;
using DeskLink.Protocol.Mux;

namespace DeskLink.Protocol.Session;

/// <summary>
/// E2E 会话通道。一个实例对应一条已接线的传输（relay 或 LAN direct）。
///
/// 线程模型：Send / Receive 可各由一个 Task 调用（单写单读）；内部不额外加锁。
/// Dispose 必须在两个方向都停止后调用。
/// </summary>
public sealed class EncryptedSession : IDisposable
{
    private readonly AeadSession _send;
    private readonly AeadSession _recv;
    private bool _disposed;

    /// <summary>AAD 为固定常量，绑定路径类型，防止跨路径重放。</summary>
    private static readonly byte[] Aad = "desklink/session/v1"u8.ToArray();

    public EncryptedSession(byte[] sendKey, byte[] recvKey)
    {
        if (sendKey is null || sendKey.Length != AeadSession.KeyLen)
            throw new ArgumentException($"sendKey must be {AeadSession.KeyLen} bytes", nameof(sendKey));
        if (recvKey is null || recvKey.Length != AeadSession.KeyLen)
            throw new ArgumentException($"recvKey must be {AeadSession.KeyLen} bytes", nameof(recvKey));
        _send = new AeadSession(sendKey);
        _recv = new AeadSession(recvKey);
    }

    /// <summary>从握手会话密钥构造（Initiator/Responder 已各自拿到 send/recv）。</summary>
    public static EncryptedSession FromKeys((byte[] SendKey, byte[] RecvKey) keys)
        => new(keys.SendKey, keys.RecvKey);

    /// <summary>
    /// 加密一个内层帧，返回"可直接写入传输"的字节。
    ///
    /// tcpMux = true（**当前两种传输都如此**）时加上 [u32 len][u8 sid] 外层：
    /// TCP 与 QUIC 流都只保证有序字节、不保留消息边界，必须靠显式长度前缀切帧。
    /// </summary>
    public byte[] SealFrame(ProtocolConstants.FrameType type, ReadOnlySpan<byte> payload, byte sid, bool tcpMux)
    {
        ThrowIfDisposed();
        var inner = FrameCodec.Encode(new Frame(type, payload.ToArray()));
        var sealed_ = _send.Seal(inner, Aad);
        if (!tcpMux)
        {
            return sealed_;
        }
        // 注意：mux 外层包的是**密文**，不是明文内层帧。因此用 EncodeTcpRaw。
        return MuxFrame.EncodeTcpRaw(sid, sealed_);
    }

    /// <summary>
    /// 解密一个内层帧。
    /// 调用方负责先从流里切出一帧 mux 外层（[u32 len][u8 sid]），把其中的密文传进来；
    /// 切帧由 SessionPump.TryExtract 完成（两种传输一致）。
    /// 返回解码后的 (type, payload)。
    /// </summary>
    public (ProtocolConstants.FrameType Type, byte[] Payload) OpenFrame(ReadOnlySpan<byte> sealedFrame)
    {
        ThrowIfDisposed();
        var inner = _recv.Open(sealedFrame, Aad);
        var n = FrameCodec.TryDecode(inner, out var frame, out var consumed);
        if (n == 0)
        {
            throw new InvalidDataException("decrypted inner frame could not be decoded");
        }
        if (consumed != inner.Length)
        {
            throw new InvalidDataException($"inner frame trailing bytes: {inner.Length - consumed}");
        }
        return (frame.Type, frame.Payload.ToArray());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _send.Dispose();
        _recv.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EncryptedSession));
    }
}
