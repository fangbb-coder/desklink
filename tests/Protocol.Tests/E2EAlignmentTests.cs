// 端到端对齐单测：C# 与 Go 必须产出同一字节序列 / 同一数值。
//
// 锚点文件：
//   Go:       src/vps/internal/proto/frames_test.go (TestHexFrame_E2EAlignment 等)
//   C#:       本文件
//   协议说明:  .team/kickoff-build/protocol-engineer/p1-protocol-library-notes.md
//
// 单测组织：
//   - FrameCodecAlignment : 帧 / mux TCP 外层字节对齐
//   - X25519Alignment     : X25519 ECDH 共享密钥 bit-equal(关键单点)
//   - HkdfAeadAlignment   : HKDF 派生 / AES-GCM 序号 nonce 自洽(单端确定向量)
//   - HandshakeAlignment  : SIGMA 简化式握手产出稳定的 initHello/respHello 字节
//                           ——Go 端 frames_test.go 不做握手(留给 C# 自测),
//                             此处 anchor 是给未来 Go 端 handshake 实现对照
//
// 不变量策略：所有 anchor 都是"用固定输入 → 同一输出"，任意一次失败都意味着
// 协议实现产生了不一致的字节或值，C#-Go 互通立刻会断。
namespace DeskLink.Protocol.Tests;

using System.Buffers.Binary;
using System.Security.Cryptography;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Crypto;
using DeskLink.Protocol.Frames;
using DeskLink.Protocol.Handshake;
using DeskLink.Protocol.Mux;
using DeskLink.Protocol.Relay;
using Xunit;

public class E2EAlignmentTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // 帧 / mux TCP 字节对齐
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FrameCodec_Hello_DeskLink_Matches_Go_Anchor()
    {
        // 与 src/vps/internal/proto/frames_test.go:TestHexFrame_E2EAlignment 完全一致。
        // FrameHello(0x01) + payload="DeskLink" (8 bytes) →
        //   01 00 08 44 65 73 6B 4C 69 6E 6B
        var payload = System.Text.Encoding.UTF8.GetBytes("DeskLink");
        var frame = new Frame(ProtocolConstants.FrameType.Hello, payload);
        var encoded = FrameCodec.Encode(frame);
        Assert.Equal(
            "0100084465736b4c696e6b",
            Convert.ToHexString(encoded).ToLowerInvariant());
    }

    [Fact]
    public void FrameCodec_Ping_EmptyPayload_Matches_Go_RoundTrip()
    {
        // Go: TestEncodeDecodeFrame_RoundTrip 风格固定输入。
        // FramePing(0x04) + 空 payload → 04 00 00
        var frame = new Frame(ProtocolConstants.FrameType.Ping, ReadOnlyMemory<byte>.Empty);
        var encoded = FrameCodec.Encode(frame);
        Assert.Equal("040000", Convert.ToHexString(encoded).ToLowerInvariant());

        // 往返：再解码回来必须一致。
        Assert.Equal(1, FrameCodec.TryDecode(encoded, out var dec, out var consumed));
        Assert.Equal(ProtocolConstants.FrameType.Ping, dec.Type);
        Assert.Equal(0, dec.Payload.Length);
        Assert.Equal(3, consumed);
    }

    [Fact]
    public void MuxTcp_InputKey_CAFE_Sid33_Matches_Go_RoundTrip()
    {
        // 与 src/vps/internal/proto/frames_test.go:TestEncodeDecodeMuxTcp_RoundTrip 一致。
        // inner = EncodeFrame(InputKey=0x22, payload=0xCA 0xFE) = 22 00 02 CA FE (5B)
        // outer len = 1 + 5 = 6（u32 big-endian）→ 00 00 00 06 22 22 00 02 CA FE
        // 注意 sid=33=0x21 与 InputKey=0x22 的十六进制易混，勿看串。
        var payload = new byte[] { 0xCA, 0xFE };
        var inner = FrameCodec.Encode(new Frame(ProtocolConstants.FrameType.InputKey, payload));
        var mux = MuxFrame.EncodeTcp(33, new Frame(ProtocolConstants.FrameType.InputKey, payload));
        Assert.Equal("0000000621220002cafe", Convert.ToHexString(mux).ToLowerInvariant());
        Assert.Equal(inner.Length + 1, mux.Length - 4);

        // 往返
        Assert.Equal(1, MuxFrame.TryDecodeTcp(mux, out var gotSid, out var gotFrame, out var gotConsumed));
        Assert.Equal(33, gotSid);
        Assert.Equal(ProtocolConstants.FrameType.InputKey, gotFrame.Type);
        Assert.Equal(new byte[] { 0xCA, 0xFE }, gotFrame.Payload.ToArray());
        Assert.Equal(mux.Length, gotConsumed);
    }

    [Fact]
    public void FrameCodec_Oversize_Rejects_At_Boundary()
    {
        // Go: TestEncodeFrame_Oversize: payload = MaxFramePayloadSize+1 → 报错。
        var payload = new byte[ProtocolConstants.MaxFramePayloadSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameCodec.Encode(new Frame(ProtocolConstants.FrameType.FileChunk, payload)));
    }

    [Fact]
    public void StreamId_FromLogical_Allocations_Match_Go_Cases()
    {
        // 与 src/vps/internal/proto/frames_test.go:TestStreamIdFromLogical 完全对齐。
        Assert.Equal(16, StreamId.FromLogical(ProtocolConstants.LogicalStream.Desktop, 0));
        Assert.Equal(32, StreamId.FromLogical(ProtocolConstants.LogicalStream.Control, 0));
        Assert.Equal(48, StreamId.FromLogical(ProtocolConstants.LogicalStream.File, 0));
        Assert.Equal(80, StreamId.FromLogical(ProtocolConstants.LogicalStream.File, 32));

        // sub=250 → 48 + 250 = 298 > 255,应抛
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamId.FromLogical(ProtocolConstants.LogicalStream.File, 250));

        // 未知 LogicalStream 应抛
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StreamId.FromLogical((ProtocolConstants.LogicalStream)0x40, 0));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // X25519 ECDH 共享密钥 bit-equal(互通关键单点)
    //
    // 协议说明 p1-protocol-library-notes.md §5 已 flag:
    //   "X25519 私钥 BouncyCastle X25519PrivateKeyParameters 在 .NET 9 上对
    //    RFC 7748 测试向量偏差几个 bit,但 ephemeral-vs-ephemeral 双向一致性
    //    通过 —— 已确认是 BC 对 RFC 7748 测试向量本身的解释差异,不影响协议互通。"
    //
    // 我们的端到端策略：
    //   1) 自洽：自己跟自己握手 → 共享密钥一致(双向一致性)
    //   2) RFC 7748:BC vs Go crypto/ecdh 偏差"几个 bit"是否真的在可接受范围
    //      —— 跑 RFC 7748 §5.2 公开测试向量,断言 bit-level equality。
    //      如果失败,在测试名上明确标 [INTEROP-RISK],作为互通风险项,
    //      vps-backend-engineer 在 Go 端 frames_test.go 用同样 vector 跑对照。
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void X25519_Ecdh_Self_Consistency_BitEqual()
    {
        // 自洽:同一密钥对,自己跟自己 derive 两次必须 bit-equal。
        var seed = new byte[32];
        for (int i = 0; i < 32; i++) seed[i] = (byte)i;

        var kp = HandshakeKeys.FromSeeds(new byte[32], seed);
        var s1 = kp.DeriveSharedSecret(kp.X25519Public);
        var s2 = kp.DeriveSharedSecret(kp.X25519Public);
        Assert.Equal(s1, s2);
        Assert.Equal(32, s1.Length);
    }

    [Fact]
    public void X25519_Ecdh_TwoParties_BitEqual()
    {
        // 双向一致性:Alice 算 shared = X25519(aPriv, bPub),
        //            Bob   算 shared = X25519(bPriv, aPub),两者必须 bit-equal。
        // 这是端到端互通的关键不变式 —— 只要两端用相同 X25519 私钥/公钥定义,
        // 共享密钥必须在字节级一致,否则 HKDF 派生出来的 send/recv key 全部错位。

        // 用确定种子构造两侧密钥对
        var aliceSeed = Enumerable.Range(0, 32).Select(i => (byte)(0x10 + i)).ToArray();
        var bobSeed   = Enumerable.Range(0, 32).Select(i => (byte)(0x80 + i)).ToArray();
        var alice = HandshakeKeys.FromSeeds(new byte[32], aliceSeed);
        var bob   = HandshakeKeys.FromSeeds(new byte[32], bobSeed);

        var aliceShared = alice.DeriveSharedSecret(bob.X25519Public);
        var bobShared   = bob.DeriveSharedSecret(alice.X25519Public);

        Assert.Equal(aliceShared, bobShared);
        Assert.Equal(32, aliceShared.Length);
    }

    [Fact]
    public void X25519_Ecdh_Rfc7748_KnownAnswer_INTEROP_RISK()
    {
        // [INTEROP-RISK]
        // RFC 7748 §5.2 公开 KAT(Wikipedia / RFC 原文):
        //   Alice 私钥 (32B):
        //     77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a
        //   Alice 公钥 = X25519 basepoint * priv:
        //     8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a
        //   Bob 私钥 (32B):
        //     5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb
        //   Shared secret = X25519(alicePriv, bobPub) = X25519(bobPriv, alicePub):
        //     4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742
        //
        // 期望:BC 2.4.0 .NET 9 计算 aliceShared 与 RFC KAT bit-equal。
        // 若失败 → 标记 [INTEROP-RISK],vps-backend-engineer 在 Go 端用同样 KAT 跑对照。
        // 协议说明已说明"几个 bit 偏差",所以这个测试本来就可能 FAIL,
        // 我们的目的是把偏差量化和定位。

        var aliceSeed = Convert.FromHexString(
            "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var aliceExpectedPub = Convert.FromHexString(
            "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        var bobSeed = Convert.FromHexString(
            "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var expectedShared = Convert.FromHexString(
            "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        var alice = HandshakeKeys.FromSeeds(new byte[32], aliceSeed);
        var bob   = HandshakeKeys.FromSeeds(new byte[32], bobSeed);

        // (1) Alice 公钥必须与 RFC KAT 一致 —— 这才是端到端能 bit-equal 的前提
        Assert.Equal(aliceExpectedPub, alice.X25519Public);

        // (2) 共享密钥必须 bit-equal
        var aliceShared = alice.DeriveSharedSecret(bob.X25519Public);
        var bobShared   = bob.DeriveSharedSecret(alice.X25519Public);

        // 与 RFC KAT 比对;若失败,记录实际 hex 供 Go 端对照
        var actualHex = Convert.ToHexString(aliceShared).ToLowerInvariant();
        if (!actualHex.Equals(
            "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742",
            StringComparison.OrdinalIgnoreCase))
        {
            // 不让 dotnet test FAIL 掩盖问题:用 Assert.Fail 给出明确锚定
            Assert.Fail(
                "BC 2.4.0 X25519 与 RFC 7748 §5.2 KAT 有 bit-level 偏差。" +
                $"实际: {actualHex}\n" +
                "→ vps-backend-engineer 在 Go 端 crypto/ecdh 跑同一 KAT 对照。" +
                "若 Go 端 bit-equal 而 C# 端不,确认互通需 BouncyCastle 版本升级或换 NSec/cryptography 包。");
        }

        // (3) Alice/Bob 两侧自己算出来必须 bit-equal(自洽)
        Assert.Equal(aliceShared, bobShared);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // HKDF / AES-GCM 单端确定向量(给未来 Go 端 handshake 实现对照)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HkdfSha256_Derive_Matches_Rfc5869_Kat()
    {
        // RFC 5869 Appendix A Test Case 1(确认 HKDF-SHA256 实现稳定)。
        //
        // 更正记录（2026-09-26）：原先 info 用 Encoding.UTF8.GetBytes("f0f1f2f3f4f5f6f7f8f9")
        // 把 hex 字符串当成了 ASCII 文本，派生结果自然对不上 RFC 的 OKM。
        // RFC 5869 A.1 的 info 是 **十六进制字面量**，必须 hex 解码为 10 字节原始数据：
        //   info = 0xf0f1f2f3f4f5f6f7f8f9 (10 bytes)
        // 已用独立 HKDF-SHA256 实现复核：hex 解码后 OKM 与 RFC 期望值 bit-equal。
        var ikm  = Convert.FromHexString(
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
        var salt = Convert.FromHexString(
            "000102030405060708090a0b0c");
        var info = Convert.FromHexString(
            "f0f1f2f3f4f5f6f7f8f9");
        var expectedOkm = Convert.FromHexString(
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

        var okm = HkdfSha256.Derive(ikm, salt, info, 42);
        Assert.Equal(expectedOkm, okm);
    }

    [Fact]
    public void AeadSession_SealOpen_RoundTrip()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        using var sender   = new AeadSession(key);
        using var receiver = new AeadSession(key);

        var plain = System.Text.Encoding.UTF8.GetBytes("hello-desklink");
        var aad   = System.Text.Encoding.UTF8.GetBytes("ctrl-stream-1");

        var sealedBytes = sender.Seal(plain, aad);
        // sealedBytes = [counter 8][cipher N][tag 16]
        Assert.Equal(8 + plain.Length + AeadSession.TagLen, sealedBytes.Length);

        var opened = receiver.Open(sealedBytes, aad);
        Assert.Equal(plain, opened);
    }

    [Fact]
    public void AeadSession_Open_RejectsOutOfOrderCounter()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        using var sender   = new AeadSession(key);
        using var receiver = new AeadSession(key);

        sender.Seal(new byte[] { 1, 2, 3 }, new byte[] { });
        var sealed2 = sender.Seal(new byte[] { 4, 5, 6 }, new byte[] { });

        // 让 receiver 故意跳过 sealed2 直接读 sealed1 → counter mismatch
        // 重置 receiver 让 counter = 0,期望 Open sealed2(counter=1) 抛异常
        using var receiver2 = new AeadSession(key);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(
            () => receiver2.Open(sealed2, new byte[] { }));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SIGMA 简化式握手:产出确定向量(给未来 Go 端 handshake 实现对照)
    //
    // 关键:握手过程用了 SecureRandom,所以每次跑的 nonce / ephemeral 都不同,
    // 不能直接断言 initHello 的 hex;只断言:
    //   - 双方会话密钥 bit-equal
    //   - transcript hash 在两侧一致(可从签名验证路径间接断言)
    //   - 握手消息结构大小与协议说明一致
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Handshake_MessageSizes_Match_Protocol_Spec()
    {
        // 协议说明 §2.2 / HandshakeMessages.cs:
        //   InitiatorHello = 1(path) + 2(version) + 32(ephX) + 32(ltEd) + 16(hint) + 32(nonce) = 115
        //   ResponderHello = 32(ephX) + 32(ltEd) + 16(hint) + 32(nonce) + 64(sig) = 176
        //   InitiatorFinish = 64(sig)
        //   ResponderFinish = 1(confirmOk)

        var i = new HandshakeSession.Initiator(
            HandshakeKeys.Generate(),
            deviceIdHint: new byte[16],
            path: ProtocolConstants.PathKind.Relay);
        var initBytes = i.BuildInitiatorHello();
        Assert.Equal(1 + 2 + 32 + 32 + 16 + 32, initBytes.Length);
    }

    [Fact]
    public void Handshake_RelayPath_SessionKeys_BitEqual_BothRoles()
    {
        // 中继路径:两端用 SIGMA 简化式握手,会话密钥双向必须 bit-equal。
        var alice = HandshakeKeys.Generate();
        var bob   = HandshakeKeys.Generate();
        var aliceHint = new byte[16];
        var bobHint   = new byte[16];
        RandomNumberGenerator.Fill(aliceHint);
        RandomNumberGenerator.Fill(bobHint);

        var initiator = new HandshakeSession.Initiator(alice, aliceHint, ProtocolConstants.PathKind.Relay);
        var responder = new HandshakeSession.Responder(bob, bobHint);

        var initHello = initiator.BuildInitiatorHello();
        var respHello = responder.HandleInitiatorHello(initHello);
        var initFinish = initiator.HandleResponderHello(respHello);
        var respFinish = responder.HandleInitiatorFinish(initFinish);

        Assert.NotNull(initiator.SessionKeys);
        Assert.NotNull(responder.SessionKeys);

        // 发起方 sendKey == 接收方 recvKey;发起方 recvKey == 接收方 sendKey
        Assert.Equal(initiator.SessionKeys!.Value.SendKey, responder.SessionKeys!.Value.RecvKey);
        Assert.Equal(initiator.SessionKeys!.Value.RecvKey, responder.SessionKeys!.Value.SendKey);

        // ResponderFinish confirmOk = true
        var rf = HandshakeMessages.DecodeResponderFinish(respFinish);
        Assert.True(rf.ConfirmOk);
    }

    [Fact]
    public void Handshake_DirectPath_SessionKeys_BitEqual_BothRoles()
    {
        // 直连路径:与中继路径同样的 SIGMA 简化式,但 PathKind=Direct。
        var alice = HandshakeKeys.Generate();
        var bob   = HandshakeKeys.Generate();
        var aliceHint = new byte[16];
        var bobHint   = new byte[16];
        RandomNumberGenerator.Fill(aliceHint);
        RandomNumberGenerator.Fill(bobHint);

        var initiator = new HandshakeSession.Initiator(alice, aliceHint, ProtocolConstants.PathKind.Direct);
        var responder = new HandshakeSession.Responder(bob, bobHint);

        var initHello = initiator.BuildInitiatorHello();
        var respHello = responder.HandleInitiatorHello(initHello);
        var initFinish = initiator.HandleResponderHello(respHello);
        responder.HandleInitiatorFinish(initFinish);

        Assert.Equal(initiator.SessionKeys!.Value.SendKey, responder.SessionKeys!.Value.RecvKey);
        Assert.Equal(initiator.SessionKeys!.Value.RecvKey, responder.SessionKeys!.Value.SendKey);
    }

    [Fact]
    public void Handshake_TamperedResponderHello_Rejected()
    {
        // 篡改 responder 的 ephemeral pub → 共享密钥会错位 → initiator 验签失败 → 抛 HandshakeException
        var alice = HandshakeKeys.Generate();
        var bob   = HandshakeKeys.Generate();

        var aliceHint = new byte[16];
        var bobHint   = new byte[16];
        RandomNumberGenerator.Fill(aliceHint);
        RandomNumberGenerator.Fill(bobHint);

        var initiator = new HandshakeSession.Initiator(alice, aliceHint, ProtocolConstants.PathKind.Relay);
        var responder = new HandshakeSession.Responder(bob, bobHint);

        var initHello = initiator.BuildInitiatorHello();
        var respHello = responder.HandleInitiatorHello(initHello);

        // 篡改 ephemeral pub 字节(前 32 字节)
        respHello[0] ^= 0xFF;
        respHello[1] ^= 0xFF;

        Assert.Throws<HandshakeException>(
            () => initiator.HandleResponderHello(respHello));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // relay 明文控制层对齐（P3/P4/P5 新增）
    //
    // 锚点：src/vps/internal/relay/alignment_test.go 的同名黄金向量。
    // 这一层跑在 QUIC/TLS 之上、SIGMA 之下，是 relay 用来查接线表的明文协议；
    // 字节一旦漂移，C# 客户端就会在 Hello 阶段被 relayd 拒绝（或更糟：静默错位）。
    // ─────────────────────────────────────────────────────────────────────────

    // 两侧共享的固定输入（与 Go 侧 alignInputs 完全一致）。
    private static (byte[] DeviceId, byte[] Hint, byte[] Nonce, byte[] Pub, byte[] Sig) AlignInputs()
    {
        static byte[] Seq(byte start, int n)
        {
            var a = new byte[n];
            for (var i = 0; i < n; i++) a[i] = (byte)(start + i);
            return a;
        }
        return (Seq(0x00, 32), Seq(0x10, 16), Seq(0x20, 32), Seq(0x40, 32), Seq(0x60, 64));
    }

    private const string AlignHelloPayloadHex =
        "0001000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" +
        "101112131415161718191a1b1c1d1e1f" +
        "01020304" + "000000006553f100" +
        "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f" +
        "0040" +
        "606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f" +
        "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f";

    private const string AlignHelloFrameHex = "0100a0" + AlignHelloPayloadHex;

    private const string AlignStatusFrameHex = "0400050100026f6b";

    private const string AlignDialFrameHex =
        "030020000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";

    private const string AlignChallengeTranscriptHex =
        "6465736b6c696e6b2f6465766963652d6368616c6c656e67652f7631" +
        "01020304" + "000000006553f100" +
        "202122232425262728292a2b2c2d2e2f303132333435363738393a3b3c3d3e3f" +
        "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f" +
        "404142434445464748494a4b4c4d4e4f505152535455565758595a5b5c5d5e5f";

    [Fact]
    public void RelayCtl_HelloFrame_Matches_Go_Anchor()
    {
        var (deviceId, hint, nonce, _, sig) = AlignInputs();

        var payload = RelayHelloCodec.Encode(
            protoVersion: 1,
            deviceId: deviceId,
            deviceIdHint: hint,
            challengeId: 0x01020304,
            challengeUnix: 1700000000,
            nonce: nonce,
            signature: sig);
        Assert.Equal(AlignHelloPayloadHex, Convert.ToHexString(payload).ToLowerInvariant());

        var frame = RelayControl.Encode(RelayControlKind.Hello, payload);
        Assert.Equal(AlignHelloFrameHex, Convert.ToHexString(frame).ToLowerInvariant());

        // 往返：解回来必须一致（并验证 decoder 与 encoder 同序）。
        Assert.True(RelayControl.TryDecode(frame, out var decoded, out var consumed));
        Assert.Equal(frame.Length, consumed);
        Assert.Equal(RelayControlKind.Hello, decoded!.Kind);
        var hello = RelayHelloCodec.Decode(decoded.Payload);
        Assert.Equal(1, hello.ProtoVersion);
        Assert.Equal(deviceId, hello.DeviceId);
        Assert.Equal(hint, hello.DeviceIdHint);
        Assert.Equal(0x01020304u, hello.ChallengeId);
        Assert.Equal(1700000000L, hello.ChallengeUnix);
        Assert.Equal(nonce, hello.Nonce);
        Assert.Equal(sig, hello.Signature);
    }

    [Fact]
    public void RelayCtl_StatusFrame_Matches_Go_Anchor()
    {
        var status = RelayStatusCodec.Encode(true, RelayReason.Ok);
        var frame = RelayControl.Encode(RelayControlKind.DialResult, status);
        Assert.Equal(AlignStatusFrameHex, Convert.ToHexString(frame).ToLowerInvariant());

        Assert.True(RelayControl.TryDecode(frame, out var decoded, out _));
        Assert.Equal(RelayControlKind.DialResult, decoded!.Kind);
        var (ok, reason) = RelayStatusCodec.Decode(decoded.Payload);
        Assert.True(ok);
        Assert.Equal("ok", reason);
    }

    [Fact]
    public void RelayCtl_DialFrame_Matches_Go_Anchor()
    {
        var (deviceId, _, _, _, _) = AlignInputs();
        var frame = RelayControl.Encode(RelayControlKind.Dial, deviceId);
        Assert.Equal(AlignDialFrameHex, Convert.ToHexString(frame).ToLowerInvariant());
    }

    [Fact]
    public void DeviceChallenge_Transcript_Matches_Go_Anchor()
    {
        var (deviceId, _, nonce, pub, _) = AlignInputs();

        var transcript = DeviceChallenge.Transcript(
            challengeId: 0x01020304,
            unixSeconds: 1700000000,
            nonce: nonce,
            deviceId: deviceId,
            ed25519Public: pub);

        Assert.Equal(AlignChallengeTranscriptHex, Convert.ToHexString(transcript).ToLowerInvariant());
        Assert.Equal("desklink/device-challenge/v1", RelayControl.ChallengeTranscriptDomain);
    }
}
