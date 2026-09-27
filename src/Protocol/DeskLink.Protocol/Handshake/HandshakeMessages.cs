// X25519 + Ed25519 SIGMA 简化式握手消息结构。
// 协议：
//   1. Initiator → Responder: InitiatorHello
//      [u8  pathKind][u16  protoVersion]
//      [32  initiatorEphemeralPubX25519]
//      [32  initiatorLongTermEd25519Pub]
//      [16  deviceIdHint]            // 预留字段，DESIGN.md P1 协议层必须存在
//      [32  nonceI]                  // 抗重放
//   2. Responder → Initiator: ResponderHello
//      [32  responderEphemeralPubX25519]
//      [32  responderLongTermEd25519Pub]
//      [16  deviceIdHint]
//      [32  nonceR]
//      [64  sigmaSignature]          // Ed25519 over transcript (initHello || respHello 序列化)
//
//   transcript = SHA-256(initHelloBytes || respHelloBytes)（DESIGN.md 未指定时也可直接用
//   双方临时/长期公钥 + 双向 nonce 做 Ed25519 签名前缀；这里用 SHA-256 摘要做 transcript）。
//
//   3. Initiator → Responder: InitiatorFinish
//      [64  sigmaSignature]
//   4. Responder → Initiator: ResponderFinish
//      [u8  confirmOk]
//
// 双方各自:
//   sharedSecret = X25519(ephemeralPrivSelf, ephemeralPubPeer)
//   prk          = HKDF-SHA256(ikm=sharedSecret, salt=transcriptHash, info="DeskLink/session")
//   sendKey      = HKDF-SHA256(ikm=prk,        salt=transcriptHash, info="DeskLink/sendKey",  L=32)
//   recvKey      = HKDF-SHA256(ikm=prk,        salt=transcriptHash, info="DeskLink/recvKey",  L=32)
//
// 角色约定：发起方使用 sendKey 发送，recvKey 接收；接收方反之（双方看到的 send/recv 名称相反）。
//
// deviceIdHint 长度 16 字节。规范用途：稳定且短的对端设备标识（registry 私钥/ID 派生均可），
// 用于帮助人类用户在中继路径首连时确认"确实是这一台"。该字段在 P1 协议层预留，UI 在 P5 强制确认。
using System.Buffers.Binary;
using System.Security.Cryptography;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Crypto;
using DeskLink.Protocol.Hash;

namespace DeskLink.Protocol.Handshake;

public static class HandshakeMessages
{
    public const int DeviceIdHintLen = 16;
    public const int NonceLen = 32;
    public const int X25519PubLen = 32;
    public const int Ed25519PubLen = 32;
    public const int Ed25519SigLen = 64;

    public readonly record struct InitiatorHello(
        ProtocolConstants.PathKind PathKind,
        ushort ProtocolVersion,
        byte[] InitiatorEphemeralPub,
        byte[] InitiatorLongTermPub,
        byte[] DeviceIdHint,
        byte[] Nonce);

    public readonly record struct ResponderHello(
        byte[] ResponderEphemeralPub,
        byte[] ResponderLongTermPub,
        byte[] DeviceIdHint,
        byte[] Nonce,
        byte[] SigmaSignature);

    public readonly record struct InitiatorFinish(byte[] SigmaSignature);
    public readonly record struct ResponderFinish(bool ConfirmOk);

    public static byte[] EncodeInitiatorHello(in InitiatorHello msg)
    {
        ValidatePub(msg.InitiatorEphemeralPub, nameof(msg.InitiatorEphemeralPub));
        ValidatePub(msg.InitiatorLongTermPub, nameof(msg.InitiatorLongTermPub));
        ValidateFixed(msg.DeviceIdHint, DeviceIdHintLen, nameof(msg.DeviceIdHint));
        ValidateFixed(msg.Nonce, NonceLen, nameof(msg.Nonce));

        using var ms = new MemoryStream();
        ms.WriteByte((byte)msg.PathKind);
        Span<byte> ver = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(ver, msg.ProtocolVersion);
        ms.Write(ver);
        ms.Write(msg.InitiatorEphemeralPub);
        ms.Write(msg.InitiatorLongTermPub);
        ms.Write(msg.DeviceIdHint);
        ms.Write(msg.Nonce);
        return ms.ToArray();
    }

    public static InitiatorHello DecodeInitiatorHello(ReadOnlySpan<byte> data)
    {
        var minLen = 1 + 2 + X25519PubLen + Ed25519PubLen + DeviceIdHintLen + NonceLen;
        if (data.Length != minLen)
        {
            throw new InvalidDataException($"InitiatorHello size mismatch: {data.Length} != {minLen}");
        }
        var path = (ProtocolConstants.PathKind)data[0];
        var ver = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(1, 2));
        var offset = 3;
        var eph = data.Slice(offset, X25519PubLen).ToArray(); offset += X25519PubLen;
        var lt = data.Slice(offset, Ed25519PubLen).ToArray(); offset += Ed25519PubLen;
        var hint = data.Slice(offset, DeviceIdHintLen).ToArray(); offset += DeviceIdHintLen;
        var nonce = data.Slice(offset, NonceLen).ToArray();
        return new InitiatorHello(path, ver, eph, lt, hint, nonce);
    }

    public static byte[] EncodeResponderHello(in ResponderHello msg)
    {
        ValidatePub(msg.ResponderEphemeralPub, nameof(msg.ResponderEphemeralPub));
        ValidatePub(msg.ResponderLongTermPub, nameof(msg.ResponderLongTermPub));
        ValidateFixed(msg.DeviceIdHint, DeviceIdHintLen, nameof(msg.DeviceIdHint));
        ValidateFixed(msg.Nonce, NonceLen, nameof(msg.Nonce));
        ValidateFixed(msg.SigmaSignature, Ed25519SigLen, nameof(msg.SigmaSignature));

        using var ms = new MemoryStream();
        ms.Write(msg.ResponderEphemeralPub);
        ms.Write(msg.ResponderLongTermPub);
        ms.Write(msg.DeviceIdHint);
        ms.Write(msg.Nonce);
        ms.Write(msg.SigmaSignature);
        return ms.ToArray();
    }

    public static ResponderHello DecodeResponderHello(ReadOnlySpan<byte> data)
    {
        var minLen = X25519PubLen + Ed25519PubLen + DeviceIdHintLen + NonceLen + Ed25519SigLen;
        if (data.Length != minLen)
        {
            throw new InvalidDataException($"ResponderHello size mismatch: {data.Length} != {minLen}");
        }
        var offset = 0;
        var eph = data.Slice(offset, X25519PubLen).ToArray(); offset += X25519PubLen;
        var lt = data.Slice(offset, Ed25519PubLen).ToArray(); offset += Ed25519PubLen;
        var hint = data.Slice(offset, DeviceIdHintLen).ToArray(); offset += DeviceIdHintLen;
        var nonce = data.Slice(offset, NonceLen).ToArray(); offset += NonceLen;
        var sig = data.Slice(offset, Ed25519SigLen).ToArray();
        return new ResponderHello(eph, lt, hint, nonce, sig);
    }

    public static byte[] EncodeInitiatorFinish(in InitiatorFinish msg)
    {
        ValidateFixed(msg.SigmaSignature, Ed25519SigLen, nameof(msg.SigmaSignature));
        return (byte[])msg.SigmaSignature.Clone();
    }

    public static InitiatorFinish DecodeInitiatorFinish(ReadOnlySpan<byte> data)
    {
        if (data.Length != Ed25519SigLen)
        {
            throw new InvalidDataException($"InitiatorFinish size mismatch: {data.Length}");
        }
        return new InitiatorFinish(data.ToArray());
    }

    public static byte[] EncodeResponderFinish(in ResponderFinish msg)
    {
        return new[] { msg.ConfirmOk ? (byte)1 : (byte)0 };
    }

    public static ResponderFinish DecodeResponderFinish(ReadOnlySpan<byte> data)
    {
        if (data.Length != 1)
        {
            throw new InvalidDataException($"ResponderFinish size mismatch: {data.Length}");
        }
        return new ResponderFinish(data[0] != 0);
    }

    // 计算 transcript 哈希：双方握手消息的串联摘要。
    // 关键：transcript 必须固定。ResponderHello 含自身的 sig 字段（最后 64 字节），
    // 因此计算 transcript 时**剥掉 sig 字段**再哈希；否则会出现"用含 sig 的 transcript 签 sig"循环依赖。
    public static byte[] TranscriptHash(ReadOnlySpan<byte> initHello, ReadOnlySpan<byte> respHello)
    {
        // respHello 长度 = X25519PubLen + Ed25519PubLen + DeviceIdHintLen + NonceLen + Ed25519SigLen
        var respNoSigLen = X25519PubLen + Ed25519PubLen + DeviceIdHintLen + NonceLen;
        var respNoSig = respHello.Slice(0, Math.Min(respHello.Length, respNoSigLen));
        var concat = new byte[initHello.Length + respNoSig.Length];
        initHello.CopyTo(concat);
        respNoSig.CopyTo(concat.AsSpan(initHello.Length));
        return SHA256.HashData(concat);
    }

    // 会话密钥派生。两侧各自调用，使用各自看到的"send"/"recv"语义：
    // 发起方调用：localRole=Initiator
    // 接收方调用：localRole=Responder
    // 返回：(sendKey, recvKey, prk)
    public static (byte[] SendKey, byte[] RecvKey, byte[] Prk) DeriveSessionKeys(
        HandshakeRole localRole,
        byte[] sharedSecret,
        byte[] transcriptHash)
    {
        var prk = HkdfSha256.Derive(sharedSecret, transcriptHash, "DeskLink/session"u8, HkdfSha256.HashLen);

        // 顺序：发起方的 send = init→resp；接收方的 send = resp→init。
        // 为统一接口，这里返回"我方 sendKey / 我方 recvKey"。
        string sendInfo, recvInfo;
        if (localRole == HandshakeRole.Initiator)
        {
            sendInfo = "DeskLink/init->resp/key";
            recvInfo = "DeskLink/resp->init/key";
        }
        else
        {
            sendInfo = "DeskLink/resp->init/key";
            recvInfo = "DeskLink/init->resp/key";
        }

        var sendKey = HkdfSha256.Derive(prk, transcriptHash, System.Text.Encoding.UTF8.GetBytes(sendInfo), AeadSession.KeyLen);
        var recvKey = HkdfSha256.Derive(prk, transcriptHash, System.Text.Encoding.UTF8.GetBytes(recvInfo), AeadSession.KeyLen);
        return (sendKey, recvKey, prk);
    }

    private static void ValidatePub(byte[] data, string name)
    {
        ValidateFixed(data, 32, name);
    }

    private static void ValidateFixed(byte[] data, int len, string name)
    {
        if (data == null || data.Length != len)
        {
            throw new ArgumentException($"{name} must be {len} bytes", name);
        }
    }
}

public enum HandshakeRole
{
    Initiator,
    Responder,
}
