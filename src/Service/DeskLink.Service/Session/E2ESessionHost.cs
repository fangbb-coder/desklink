// E2E 会话宿主（P5 核心）。
//
// 职责：把一条已接线的中继传输，从"控制握手完成"推进到"E2E 加密会话可用"，
// 然后在加密通道上跑控制流往返（Ping/Pong + SessionControl）。
//
// 完整时序（本类负责 3~6）：
//   1. RelayClient 建立传输（QUIC / TCP-TLS）。
//   2. RelayControlChannel：Hello（设备挑战签名）→ HelloAck。
//   3. RelayControlChannel：Dial(peer) → DialResult(ok)。
//   4. SIGMA 握手（HandshakeSession）交换 Initiator/Responder 消息，
//      派生 session keys。**复用同一条传输**：握手消息也是加密前的明文帧，
//      长度固定，不涉及 mux 外层（用 sid=0 的固定长度消息即可）。
//   5. 构造 EncryptedSession，进入加密通道。
//   6. 控制流往返：Ping(0x04)/Pong(0x05) 与 SessionControl(0x24) 帧。
//      文件层（P6 前）只做 ack 位图帧的收发骨架。
//
// 角色约定（谁是 Initiator）：
//   device_id 字典序小的一方为 Initiator，大的一方为 Responder。
//   两侧独立计算，无需协商——与 registry 的 device_a <= device_b 排序规则同源。
using System.Security.Cryptography;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Handshake;
using DeskLink.Protocol.Hash;
using DeskLink.Protocol.Mux;
using DeskLink.Protocol.Relay;
using DeskLink.Protocol.Session;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
// 别名避免与 Blake3.Managed 包根命名空间 "Blake3" 冲突。
using ProtoBlake3 = DeskLink.Protocol.Hash.Blake3;

namespace DeskLink.Service.Session;

/// <summary>E2E 会话建立结果。</summary>
public sealed record E2ESessionResult(
    bool Ok,
    string Detail,
    EncryptedSession? Session,
    HandshakeRole Role);

/// <summary>
/// E2E 会话宿主：控制握手 + SIGMA + 加密通道。
/// </summary>
public sealed class E2ESessionHost
{
    private readonly DeviceKeys _selfKeys;
    private readonly byte[] _selfDeviceId;
    private readonly Action<string>? _log;

    /// <summary>SIGMA 握手消息长度常量（与 Go / 协议层一致）。</summary>
    private static readonly int InitiatorHelloLen =
        1 + 2 + RelayControl.DeviceIdSize * 2 + RelayControl.DeviceIdHintSize + RelayControl.NonceSize;

    public E2ESessionHost(DeviceKeys selfKeys, Action<string>? log = null)
    {
        _selfKeys = selfKeys ?? throw new ArgumentNullException(nameof(selfKeys));
        _selfDeviceId = ComputeDeviceId(selfKeys.KeyPair.Ed25519Public);
        _log = log;
    }

    /// <summary>本机 device_id。</summary>
    public byte[] SelfDeviceId => _selfDeviceId;

    /// <summary>
    /// 执行控制握手（Hello + Dial）。
    /// </summary>
    public async Task<RelayHandshakeResult> ControlHandshakeAsync(
        IRelayTransport transport,
        ReadOnlyMemory<byte> peerDeviceId,
        CancellationToken ct)
    {
        var channel = new RelayControlChannel(transport);

        // 挑战：随机 nonce + 当前时间 + 随机 challengeId
        var nonce = new byte[RelayControl.NonceSize];
        RandomNumberGenerator.Fill(nonce);
        var challengeId = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4), 0);
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 签名 transcript（与 relayd / registryd 三方一致）
        var transcript = DeviceChallenge.Transcript(
            challengeId, unix, nonce, _selfDeviceId, _selfKeys.KeyPair.Ed25519Public);
        var sig = _selfKeys.KeyPair.Sign(transcript);

        _log?.Invoke($"E2ESession: hello (transport={transport.Kind}, challengeId={challengeId})");
        var hello = await channel.HelloAsync(
            ProtocolConstants.ProtocolVersion,
            _selfDeviceId,
            _selfKeys.DeviceIdHint,
            challengeId, unix, nonce, sig, ct).ConfigureAwait(false);
        if (!hello.Ok)
        {
            _log?.Invoke($"E2ESession: hello rejected reason={hello.Reason}");
            return hello;
        }

        _log?.Invoke("E2ESession: hello ok, dialing peer");

        // Dial 重试策略（重要）：
        //   relay 在"对端尚未注册"时回 peer_offline，并且**刻意不断开**连接，
        //   把重试决定权交给客户端（relayd 侧注释："不立即断：让客户端自己决定重试"）。
        //   两端几乎同时启动时，先到者必然撞上 peer_offline；若这里直接失败返回，
        //   RelayClient 会关闭连接 + 退避重连——而"断开"会让 relay 立刻注销本端，
        //   于是双方在"注册 → 被对方注销 → 重连"之间反复，收敛极慢
        //   （实测两个实例互相报 peer_offline 超过 25s 都不收敛）。
        //   因此在**同一条连接**上重试 Dial，直到对端上线或次数耗尽。
        const int maxDialAttempts = 15;
        var dialDelay = TimeSpan.FromMilliseconds(400);

        RelayHandshakeResult dial = new(false, RelayReason.Internal, transport.Kind);
        for (var attempt = 1; attempt <= maxDialAttempts; attempt++)
        {
            dial = await channel.DialAsync(peerDeviceId, (kind, id) =>
            {
                _log?.Invoke($"E2ESession: peer event {kind} device={Convert.ToHexString(id)[..16]}");
            }, ct).ConfigureAwait(false);

            // 成功，或失败原因不是"对端不在线"（未配对/已撤销/协议不符等）→ 立即返回。
            if (dial.Ok || dial.Reason != RelayReason.PeerOffline)
            {
                return dial;
            }
            _log?.Invoke(
                $"E2ESession: peer offline (attempt {attempt}/{maxDialAttempts}), " +
                $"retrying on same connection in {dialDelay.TotalMilliseconds}ms");
            await Task.Delay(dialDelay, ct).ConfigureAwait(false);
        }
        return dial;
    }

    /// <summary>
    /// 在已接线的传输上执行 SIGMA 握手并构造 EncryptedSession。
    ///
    /// 关键：握手消息直接读写同一条传输字节流（不经 mux），因为此刻 relay 已开始
    /// 纯字节泵送——两侧对"先发什么"有确定顺序（字典序小者先发），无需额外定长分帧。
    /// 每个握手消息长度固定（见 HandshakeMessages 常量），因此读侧可精确读满。
    /// </summary>
    public async Task<E2ESessionResult> EstablishE2EAsync(
        IRelayTransport transport,
        ReadOnlyMemory<byte> peerDeviceId,
        CancellationToken ct)
    {
        // 角色：device_id 字典序小者为 Initiator（两侧独立计算，无需协商）。
        var isInitiator = _selfDeviceId.AsSpan().SequenceCompareTo(peerDeviceId.Span) < 0;
        var role = isInitiator ? HandshakeRole.Initiator : HandshakeRole.Responder;
        _log?.Invoke($"E2ESession: sigma role={role}");

        try
        {
            if (isInitiator)
            {
                var init = new HandshakeSession.Initiator(_selfKeys.KeyPair, _selfKeys.DeviceIdHint, ProtocolConstants.PathKind.Relay);
                var hello = init.BuildInitiatorHello();
                await transport.SendAsync(hello, ct).ConfigureAwait(false);

                var respHello = await ReadExactAsync(transport, ResponderHelloLen, ct).ConfigureAwait(false);
                var initFinish = init.HandleResponderHello(respHello);
                await transport.SendAsync(initFinish, ct).ConfigureAwait(false);

                var respFinish = await ReadExactAsync(transport, 1, ct).ConfigureAwait(false);
                var confirm = HandshakeMessages.DecodeResponderFinish(respFinish);
                if (!confirm.ConfirmOk)
                {
                    return new E2ESessionResult(false, "responder finish not ok", null, role);
                }
                if (init.SessionKeys is null)
                {
                    return new E2ESessionResult(false, "initiator keys missing", null, role);
                }
                // 身份绑定：SIGMA 签名验证过的对端公钥必须派生出期望的 device_id，
                // 否则视为冒充（对端自报身份与密码学身份不符）。
                if (!PeerIdentityMatches(init.ResponderLongTermPub, peerDeviceId))
                {
                    return new E2ESessionResult(false, "peer identity mismatch (responder key)", null, role);
                }
                return new E2ESessionResult(true, "e2e established", EncryptedSession.FromKeys(init.SessionKeys.Value), role);
            }
            else
            {
                var responder = new HandshakeSession.Responder(_selfKeys.KeyPair, _selfKeys.DeviceIdHint);
                var initHello = await ReadExactAsync(transport, InitiatorHelloLen, ct).ConfigureAwait(false);
                var respHello = responder.HandleInitiatorHello(initHello);
                await transport.SendAsync(respHello, ct).ConfigureAwait(false);

                var initFinish = await ReadExactAsync(transport, 64, ct).ConfigureAwait(false);
                var respFinish = responder.HandleInitiatorFinish(initFinish);
                await transport.SendAsync(respFinish, ct).ConfigureAwait(false);

                if (responder.SessionKeys is null)
                {
                    return new E2ESessionResult(false, "responder keys missing", null, role);
                }
                // 身份绑定（同上）：InitiatorFinish 的 sigma 签名已验证该公钥，
                // 此处确认它与入站连接自报的 device_id 一致。
                if (!PeerIdentityMatches(responder.InitiatorLongTermPub, peerDeviceId))
                {
                    return new E2ESessionResult(false, "peer identity mismatch (initiator key)", null, role);
                }
                return new E2ESessionResult(true, "e2e established", EncryptedSession.FromKeys(responder.SessionKeys.Value), role);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"E2ESession: sigma failed {ex.GetType().Name}: {ex.Message}");
            return new E2ESessionResult(false, $"{ex.GetType().Name}: {ex.Message}", null, role);
        }
    }

    /// <summary>ResponderHello 长度：32+32+16+32+64。</summary>
    private static int ResponderHelloLen =>
        RelayControl.DeviceIdSize * 2 + RelayControl.DeviceIdHintSize + RelayControl.NonceSize + RelayControl.SignatureSize;

    /// <summary>
    /// 身份绑定校验：用 SIGMA 握手中经签名验证的对端长期公钥派生 device_id，
    /// 与期望的对端 device_id 比对。不匹配说明"自报身份"与"密码学身份"不符，
    /// 必须拒绝会话——否则局域网直连路径上，任何人自报一个已配对设备的
    /// device_id 即可冒充该设备（配对列表按 device_id 索引，而 device_id
    /// 必须由对端真实公钥派生才合法）。
    /// </summary>
    private static bool PeerIdentityMatches(byte[]? verifiedPeerLongTermPub, ReadOnlyMemory<byte> expectedDeviceId)
    {
        if (verifiedPeerLongTermPub is null || verifiedPeerLongTermPub.Length != 32)
        {
            return false;
        }
        var derived = ComputeDeviceId(verifiedPeerLongTermPub);
        return derived.AsSpan().SequenceEqual(expectedDeviceId.Span);
    }

    private static async Task<byte[]> ReadExactAsync(IRelayTransport t, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await t.ReceiveAsync(buf.AsMemory(got, count - got), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("transport closed during sigma");
            got += n;
        }
        return buf;
    }

    /// <summary>device_id = BLAKE3("desklink/device/v1" || ed25519_pub)，与 Go proto.DeviceID 一致。</summary>
    public static byte[] ComputeDeviceId(ReadOnlySpan<byte> ed25519Public)
    {
        if (ed25519Public.Length != 32)
        {
            throw new ArgumentException("ed25519Public must be 32 bytes", nameof(ed25519Public));
        }
        var domain = System.Text.Encoding.ASCII.GetBytes("desklink/device/v1");
        var input = new byte[domain.Length + ed25519Public.Length];
        domain.CopyTo(input, 0);
        ed25519Public.CopyTo(input.AsSpan(domain.Length));
        return ProtoBlake3.Hash(input);
    }
}
