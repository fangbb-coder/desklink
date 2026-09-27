// SIGMA 简化式握手协调器。
// 用法：
//   initiator = new HandshakeSession.Initiator(...);
//   var initBytes = initiator.BuildInitiatorHello();  // 写出
//   // 收到 responderHelloBytes 后：
//   var sessionInfo = initiator.HandleResponderHello(responderHelloBytes);
//   var finish = initiator.BuildInitiatorFinish();     // 写出
//   // 收到 responderFinish 即可使用 sessionInfo.SendKey/RecvKey
//
//   responder = new HandshakeSession.Responder(...);
//   var sessionInfo = responder.HandleInitiatorHello(initHelloBytes);  // 输出 respHelloBytes
//   // 收到 initiatorFinishBytes 后：
//   var finish = responder.HandleInitiatorFinish(initiatorFinishBytes);
//
// 任何一步失败都抛 HandshakeException。
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Crypto;

namespace DeskLink.Protocol.Handshake;

public sealed class HandshakeSession
{
    public sealed class Initiator
    {
        private readonly HandshakeKeys.DeviceKeyPair _self;
        private readonly byte[] _selfEphemeralPrivSeed;
        private readonly byte[] _selfEphemeralPub;
        private readonly byte[] _nonceI;
        private readonly byte[] _deviceIdHint;
        private readonly ProtocolConstants.PathKind _path;
        private readonly ushort _protocolVersion;
        private byte[]? _responderLongTermPub;
        private byte[]? _responderEphemeralPub;
        private byte[]? _sharedSecret;
        private byte[]? _transcriptHash;
        public (byte[] SendKey, byte[] RecvKey)? SessionKeys { get; private set; }

        /// <summary>
        /// 对端（Responder）经签名验证过的长期 Ed25519 公钥（副本；未握手完成为 null）。
        /// 调用方必须用它派生 device_id 并与期望对端比对（身份绑定，见 E2ESessionHost）。
        /// </summary>
        public byte[]? ResponderLongTermPub => _responderLongTermPub?.ToArray();

        private byte[]? _initHelloBytes;

        public Initiator(
            HandshakeKeys.DeviceKeyPair self,
            byte[] deviceIdHint,
            ProtocolConstants.PathKind path,
            ushort protocolVersion = ProtocolConstants.ProtocolVersion)
        {
            ValidateHint(deviceIdHint);
            _self = self;
            _deviceIdHint = deviceIdHint;
            _path = path;
            _protocolVersion = protocolVersion;

            // 生成 ephemeral X25519
            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var ephSeed = new byte[32];
            rng.GetBytes(ephSeed);
            _selfEphemeralPrivSeed = ephSeed;
            var xPriv = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(ephSeed, 0);
            _selfEphemeralPub = xPriv.GeneratePublicKey().GetEncoded();

            var nonce = new byte[HandshakeMessages.NonceLen];
            rng.GetBytes(nonce);
            _nonceI = nonce;
        }

        public byte[] BuildInitiatorHello()
        {
            var msg = new HandshakeMessages.InitiatorHello(
                _path,
                _protocolVersion,
                _selfEphemeralPub,
                _self.Ed25519Public,
                _deviceIdHint,
                _nonceI);
            var bytes = HandshakeMessages.EncodeInitiatorHello(msg);
            _initHelloBytes = bytes;
            return bytes;
        }

        public byte[] HandleResponderHello(byte[] responderHelloBytes)
        {
            var resp = HandshakeMessages.DecodeResponderHello(responderHelloBytes);
            _responderEphemeralPub = resp.ResponderEphemeralPub;
            _responderLongTermPub = resp.ResponderLongTermPub;

            // 使用 ephemeral X25519 派生共享密钥（非 long-term）
            var ephKeyPair = HandshakeKeys.FromSeeds(new byte[32], _selfEphemeralPrivSeed);
            var shared = ephKeyPair.DeriveSharedSecret(resp.ResponderEphemeralPub);
            _sharedSecret = shared;

            var initHelloBytes = _initHelloBytes ?? throw new HandshakeException("initiator hello not built yet");
            // TranscriptHash 已剥掉 respHello 的 sig 字段，无需 initiator 端重组 respHello。
            var transcript = HandshakeMessages.TranscriptHash(initHelloBytes, responderHelloBytes);
            _transcriptHash = transcript;

            var ok = HandshakeKeys.DeviceKeyPair.Verify(resp.ResponderLongTermPub, transcript, resp.SigmaSignature);
            if (!ok)
            {
                throw new HandshakeException("responder sigma signature invalid");
            }

            var (send, recv, _) = HandshakeMessages.DeriveSessionKeys(HandshakeRole.Initiator, shared, transcript);
            SessionKeys = (send, recv);

            var sig = _self.Sign(transcript);
            var fin = new HandshakeMessages.InitiatorFinish(sig);
            return HandshakeMessages.EncodeInitiatorFinish(fin);
        }
    }

    public sealed class Responder
    {
        private readonly HandshakeKeys.DeviceKeyPair _self;
        private readonly byte[] _selfEphemeralPrivSeed;
        private readonly byte[] _selfEphemeralPub;
        private readonly byte[] _nonceR;
        private readonly byte[] _deviceIdHint;
        private byte[]? _initiatorEphemeralPub;
        private byte[]? _initiatorLongTermPub;
        private byte[]? _sharedSecret;
        private byte[]? _transcriptHash;
        public (byte[] SendKey, byte[] RecvKey)? SessionKeys { get; private set; }

        /// <summary>
        /// 对端（Initiator）的长期 Ed25519 公钥（副本；收到 InitiatorHello 后非 null）。
        /// 注意：此公钥**尚未经过签名验证**（InitiatorFinish 里的 sigma 签名才完成验证），
        /// 调用方应在握手完成后用它派生 device_id 并与期望对端比对（身份绑定）。
        /// </summary>
        public byte[]? InitiatorLongTermPub => _initiatorLongTermPub?.ToArray();

        public Responder(HandshakeKeys.DeviceKeyPair self, byte[] deviceIdHint)
        {
            ValidateHint(deviceIdHint);
            _self = self;
            _deviceIdHint = deviceIdHint;

            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            var ephSeed = new byte[32];
            rng.GetBytes(ephSeed);
            _selfEphemeralPrivSeed = ephSeed;
            var xPriv = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(ephSeed, 0);
            _selfEphemeralPub = xPriv.GeneratePublicKey().GetEncoded();

            var nonce = new byte[HandshakeMessages.NonceLen];
            rng.GetBytes(nonce);
            _nonceR = nonce;
        }

        public byte[] HandleInitiatorHello(byte[] initHelloBytes)
        {
            var init = HandshakeMessages.DecodeInitiatorHello(initHelloBytes);
            _initiatorEphemeralPub = init.InitiatorEphemeralPub;
            _initiatorLongTermPub = init.InitiatorLongTermPub;

            // 使用 ephemeral X25519 派生共享密钥（非 long-term）
            var ephKeyPair = HandshakeKeys.FromSeeds(new byte[32], _selfEphemeralPrivSeed);
            var shared = ephKeyPair.DeriveSharedSecret(init.InitiatorEphemeralPub);
            _sharedSecret = shared;

            // 用不含 sig 的 respHello 字段计算 transcript（sig 占位但 TranscriptHash 会剥掉）
            var respWithoutSig = new byte[HandshakeMessages.X25519PubLen + HandshakeMessages.Ed25519PubLen
                + HandshakeMessages.DeviceIdHintLen + HandshakeMessages.NonceLen];
            Array.Copy(_selfEphemeralPub, 0, respWithoutSig, 0, HandshakeMessages.X25519PubLen);
            Array.Copy(_self.Ed25519Public, 0, respWithoutSig, HandshakeMessages.X25519PubLen, HandshakeMessages.Ed25519PubLen);
            Array.Copy(_deviceIdHint, 0, respWithoutSig, HandshakeMessages.X25519PubLen + HandshakeMessages.Ed25519PubLen, HandshakeMessages.DeviceIdHintLen);
            Array.Copy(_nonceR, 0, respWithoutSig, HandshakeMessages.X25519PubLen + HandshakeMessages.Ed25519PubLen + HandshakeMessages.DeviceIdHintLen, HandshakeMessages.NonceLen);

            var transcript = HandshakeMessages.TranscriptHash(initHelloBytes, respWithoutSig);
            _transcriptHash = transcript;

            var sig = _self.Sign(transcript);

            var resp = new HandshakeMessages.ResponderHello(
                _selfEphemeralPub, _self.Ed25519Public, _deviceIdHint, _nonceR, sig);
            var respHelloBytes = HandshakeMessages.EncodeResponderHello(resp);

            var (send, recv, _) = HandshakeMessages.DeriveSessionKeys(HandshakeRole.Responder, shared, transcript);
            SessionKeys = (send, recv);

            return respHelloBytes;
        }

        public byte[] HandleInitiatorFinish(byte[] initiatorFinishBytes)
        {
            var fin = HandshakeMessages.DecodeInitiatorFinish(initiatorFinishBytes);
            if (_transcriptHash == null || _initiatorLongTermPub == null)
            {
                throw new HandshakeException("responder state not initialized");
            }
            var ok = HandshakeKeys.DeviceKeyPair.Verify(_initiatorLongTermPub, _transcriptHash, fin.SigmaSignature);
            if (!ok)
            {
                throw new HandshakeException("initiator sigma signature invalid");
            }
            var rf = new HandshakeMessages.ResponderFinish(true);
            return HandshakeMessages.EncodeResponderFinish(rf);
        }
    }

    private static void ValidateHint(byte[] hint)
    {
        if (hint == null || hint.Length != HandshakeMessages.DeviceIdHintLen)
        {
            throw new ArgumentException($"deviceIdHint must be {HandshakeMessages.DeviceIdHintLen} bytes");
        }
    }
}

public sealed class HandshakeException : Exception
{
    public HandshakeException(string msg) : base(msg) { }
}
