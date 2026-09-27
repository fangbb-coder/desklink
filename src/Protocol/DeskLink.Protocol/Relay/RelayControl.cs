// 中继控制帧编解码（与 Go src/vps/internal/proto/relay.go 严格字节对齐）。
//
// 这是 relay 明文层：跑在 QUIC / TCP-TLS 之上、E2E（SIGMA）之下。
// relay 需要知道"连接背后的 device_id"才能查接线表；这些信息必须在 E2E 建立
// 之前以明文交换，因此独立一套帧格式。
//
// 帧格式（大端）：
//   [ u8  kind ]
//   [ u16 payloadLen BE ]
//   [ payloadLen B payload ]
//
// 业务密文与控制帧在同一条字节流上先后出现：建立阶段只有控制帧；
// 一旦 DialResult(ok) 返回，之后的字节全部是 E2E 密文。
using System.Buffers.Binary;

namespace DeskLink.Protocol.Relay;

/// <summary>中继控制帧类型（与 Go proto.RelayKind 一一对应）。</summary>
public enum RelayControlKind : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    Dial = 0x03,
    DialResult = 0x04,
    PeerEvent = 0x05,
    Bye = 0x06,
    Ping = 0x07,
    Pong = 0x08,
}

/// <summary>对端上下线事件类型（与 Go proto.RelayPeerEventKind 一致）。</summary>
public enum RelayPeerEventKind : byte
{
    Online = 0x01,
    Offline = 0x02,
    Ready = 0x03,
}

/// <summary>
/// 中继失败原因（与 Go proto.Reason* 常量字符串一致）。
/// 用字符串而非枚举，保持与 Go 侧 JSON/日志可读性一致。
/// </summary>
public static class RelayReason
{
    public const string Ok = "ok";
    public const string AuthFailed = "auth_failed";
    public const string ProtoMismatch = "proto_mismatch";
    public const string NotPaired = "not_paired";
    public const string PeerOffline = "peer_offline";
    public const string Revoked = "revoked";
    public const string RateLimited = "rate_limited";
    public const string Internal = "internal";
    public const string BadFrame = "bad_frame";
    public const string AlreadyWired = "already_wired";
}

/// <summary>控制帧常量。</summary>
public static class RelayControl
{
    /// <summary>控制帧头大小：1B kind + 2B len。</summary>
    public const int HeaderSize = 3;

    /// <summary>控制帧 payload 上限（与 Go proto.RelayCtlMaxPayload 一致）。</summary>
    public const int MaxPayload = 4 * 1024;

    /// <summary>device_id 长度。</summary>
    public const int DeviceIdSize = 32;

    /// <summary>device_id_hint 长度。</summary>
    public const int DeviceIdHintSize = 16;

    /// <summary>Ed25519 签名长度。</summary>
    public const int SignatureSize = 64;

    /// <summary>挑战 nonce 长度。</summary>
    public const int NonceSize = 32;

    /// <summary>挑战签名域分隔前缀（与 Go proto.ChallengeTranscriptDomain 一致）。</summary>
    public const string ChallengeTranscriptDomain = "desklink/device-challenge/v1";

    /// <summary>ALPN（与 relayd / Go 侧一致）。</summary>
    public const string Alpn = "desklink-relay-v1";

    /// <summary>编码控制帧。</summary>
    public static byte[] Encode(RelayControlKind kind, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
        {
            throw new ArgumentException($"relay control payload {payload.Length} exceeds {MaxPayload}", nameof(payload));
        }
        var buf = new byte[HeaderSize + payload.Length];
        buf[0] = (byte)kind;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1, 2), (ushort)payload.Length);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>
    /// 尝试从缓冲区解出一帧控制帧。
    /// 语义与 Go proto.TryDecodeRelayCtl 一致：
    /// 字节不足返回 false；成功返回 true 并给出 frame / consumed；格式错抛异常。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> src, out RelayFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        if (src.Length < HeaderSize)
        {
            return false;
        }
        var kind = (RelayControlKind)src[0];
        var plen = BinaryPrimitives.ReadUInt16BigEndian(src.Slice(1, 2));
        if (plen > MaxPayload)
        {
            throw new InvalidDataException($"relay control payload too large: {plen}");
        }
        if (src.Length < HeaderSize + plen)
        {
            return false;
        }
        var payload = src.Slice(HeaderSize, plen).ToArray();
        frame = new RelayFrame(kind, payload);
        consumed = HeaderSize + plen;
        return true;
    }
}

/// <summary>解码后的控制帧。</summary>
public sealed record RelayFrame(RelayControlKind Kind, byte[] Payload);

/// <summary>
/// Hello payload 编解码。
/// 布局：[u16 protoVersion][32B deviceId][16B hint][u32 challengeId]
///       [i64 challengeUnix][32B nonce][u16 sigLen][sigLen B signature]
/// </summary>
public static class RelayHelloCodec
{
    public const int PayloadSize = 2 + RelayControl.DeviceIdSize + RelayControl.DeviceIdHintSize
        + 4 + 8 + RelayControl.NonceSize + 2 + RelayControl.SignatureSize;

    public static byte[] Encode(
        ushort protoVersion,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> deviceIdHint,
        uint challengeId,
        long challengeUnix,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> signature)
    {
        if (deviceId.Length != RelayControl.DeviceIdSize)
            throw new ArgumentException("deviceId must be 32 bytes", nameof(deviceId));
        if (deviceIdHint.Length != RelayControl.DeviceIdHintSize)
            throw new ArgumentException("deviceIdHint must be 16 bytes", nameof(deviceIdHint));
        if (nonce.Length != RelayControl.NonceSize)
            throw new ArgumentException("nonce must be 32 bytes", nameof(nonce));
        if (signature.Length != RelayControl.SignatureSize)
            throw new ArgumentException("signature must be 64 bytes", nameof(signature));

        var buf = new byte[PayloadSize];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(0, 2), protoVersion);
        deviceId.CopyTo(s.Slice(2, RelayControl.DeviceIdSize));
        deviceIdHint.CopyTo(s.Slice(2 + RelayControl.DeviceIdSize, RelayControl.DeviceIdHintSize));
        var off = 2 + RelayControl.DeviceIdSize + RelayControl.DeviceIdHintSize;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off, 4), challengeId);
        off += 4;
        BinaryPrimitives.WriteInt64BigEndian(s.Slice(off, 8), challengeUnix);
        off += 8;
        nonce.CopyTo(s.Slice(off, RelayControl.NonceSize));
        off += RelayControl.NonceSize;
        BinaryPrimitives.WriteUInt16BigEndian(s.Slice(off, 2), (ushort)signature.Length);
        off += 2;
        signature.CopyTo(s.Slice(off, signature.Length));
        return buf;
    }

    public static RelayHello Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < PayloadSize)
        {
            throw new InvalidDataException($"relay hello payload too short: {p.Length} < {PayloadSize}");
        }
        var off = 0;
        var ver = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(off, 2)); off += 2;
        var deviceId = p.Slice(off, RelayControl.DeviceIdSize).ToArray(); off += RelayControl.DeviceIdSize;
        var hint = p.Slice(off, RelayControl.DeviceIdHintSize).ToArray(); off += RelayControl.DeviceIdHintSize;
        var chalId = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(off, 4)); off += 4;
        var chalUnix = BinaryPrimitives.ReadInt64BigEndian(p.Slice(off, 8)); off += 8;
        var nonce = p.Slice(off, RelayControl.NonceSize).ToArray(); off += RelayControl.NonceSize;
        var sigLen = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(off, 2)); off += 2;
        if (sigLen != RelayControl.SignatureSize)
        {
            throw new InvalidDataException($"relay hello sigLen must be 64, got {sigLen}");
        }
        var sig = p.Slice(off, sigLen).ToArray();
        return new RelayHello(ver, deviceId, hint, chalId, chalUnix, nonce, sig);
    }
}

/// <summary>解码后的 Hello。</summary>
public sealed record RelayHello(
    ushort ProtoVersion,
    byte[] DeviceId,
    byte[] DeviceIdHint,
    uint ChallengeId,
    long ChallengeUnix,
    byte[] Nonce,
    byte[] Signature);

/// <summary>
/// HelloAck / DialResult payload 编解码。
/// 布局：[u8 ok][u16 reasonLen][reason UTF-8]
/// </summary>
public static class RelayStatusCodec
{
    public static byte[] Encode(bool ok, string reason)
    {
        var rb = System.Text.Encoding.UTF8.GetBytes(reason);
        if (rb.Length > RelayControl.MaxPayload - 3)
        {
            throw new ArgumentException("reason too long", nameof(reason));
        }
        var buf = new byte[3 + rb.Length];
        buf[0] = ok ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(1, 2), (ushort)rb.Length);
        rb.CopyTo(buf.AsSpan(3));
        return buf;
    }

    public static (bool Ok, string Reason) Decode(ReadOnlySpan<byte> p)
    {
        if (p.Length < 3)
        {
            throw new InvalidDataException($"relay status payload too short: {p.Length}");
        }
        var ok = p[0] != 0;
        var rl = BinaryPrimitives.ReadUInt16BigEndian(p.Slice(1, 2));
        if (p.Length < 3 + rl)
        {
            throw new InvalidDataException("relay status reason truncated");
        }
        return (ok, System.Text.Encoding.UTF8.GetString(p.Slice(3, rl)));
    }
}

/// <summary>
/// 挑战签名 transcript 构造（必须与 Go relay.ChallengeTranscript 严格一致）。
///
/// 布局：domain || u32 challengeId(BE) || i64 unixSeconds(BE) || nonce(32) ||
///       deviceId(32) || ed25519PubKey(32)
/// </summary>
public static class DeviceChallenge
{
    public static byte[] Transcript(
        uint challengeId,
        long unixSeconds,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> deviceId,
        ReadOnlySpan<byte> ed25519Public)
    {
        if (nonce.Length != RelayControl.NonceSize)
            throw new ArgumentException("nonce must be 32 bytes", nameof(nonce));
        if (deviceId.Length != RelayControl.DeviceIdSize)
            throw new ArgumentException("deviceId must be 32 bytes", nameof(deviceId));
        if (ed25519Public.Length != 32)
            throw new ArgumentException("ed25519Public must be 32 bytes", nameof(ed25519Public));

        var domain = System.Text.Encoding.ASCII.GetBytes(RelayControl.ChallengeTranscriptDomain);
        var buf = new byte[domain.Length + 4 + 8 + RelayControl.NonceSize + RelayControl.DeviceIdSize + 32];
        var s = buf.AsSpan();
        domain.CopyTo(s);
        var off = domain.Length;
        BinaryPrimitives.WriteUInt32BigEndian(s.Slice(off, 4), challengeId); off += 4;
        BinaryPrimitives.WriteInt64BigEndian(s.Slice(off, 8), unixSeconds); off += 8;
        nonce.CopyTo(s.Slice(off, RelayControl.NonceSize)); off += RelayControl.NonceSize;
        deviceId.CopyTo(s.Slice(off, RelayControl.DeviceIdSize)); off += RelayControl.DeviceIdSize;
        ed25519Public.CopyTo(s.Slice(off, 32));
        return buf;
    }
}
