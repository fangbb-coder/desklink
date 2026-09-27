// 设备长期密钥对：Ed25519 签名 + X25519 DH。
//
// 实现策略（win-service-engineer 接手 Service 时的临时修复，仅为让 P4 Service 编译通过）：
//   - Ed25519 签名 / 验签：BouncyCastle 2.7.0 Ed25519Signer（与 Go ed25519 / RFC 8032 互通）
//   - X25519 DH：BouncyCastle 2.7.0 的 X25519Agreement / X25519PublicKeyParameters
//     选 BC 不用 NSec 是为了减少第三方依赖（NSec 26.x API 不稳定）；BC 2.7.0 的 X25519
//     在 ephemeral-vs-ephemeral 双向 ECDH 一致性通过（P1 笔记确认）。
//   - 私钥种子：32 字节随机；X25519Agreement 内部按 RFC 7748 clamping 处理
//
// 注意：X25519 选型（NSec vs BC）由 protocol-engineer 在 P1 评审中确认；本实现满足
// P4 Service 验收（密钥生成 / DPAPI 跨进程解密）。
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace DeskLink.Protocol.Handshake;

public static class HandshakeKeys
{
    public const int SeedLen = 32;
    public const int Ed25519PubLen = 32;
    public const int X25519PubLen = 32;
    public const int Ed25519SigLen = 64;

    public sealed class DeviceKeyPair
    {
        public byte[] Ed25519Seed { get; }
        public byte[] Ed25519Public { get; }
        public byte[] X25519PrivateSeed { get; }
        public byte[] X25519Public { get; }

        public DeviceKeyPair(byte[] ed25519Seed, byte[] x25519PrivateSeed, byte[] ed25519Pub, byte[] x25519Pub)
        {
            Ed25519Seed = ed25519Seed;
            X25519PrivateSeed = x25519PrivateSeed;
            Ed25519Public = ed25519Pub;
            X25519Public = x25519Pub;
        }

        public byte[] Sign(ReadOnlySpan<byte> msg)
        {
            var priv = new Ed25519PrivateKeyParameters(Ed25519Seed, 0);
            var signer = new Ed25519Signer();
            signer.Init(true, priv);
            signer.BlockUpdate(msg.ToArray(), 0, msg.Length);
            return signer.GenerateSignature();
        }

        public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> msg, ReadOnlySpan<byte> sig)
        {
            var pub = new Ed25519PublicKeyParameters(publicKey.ToArray(), 0);
            var verifier = new Ed25519Signer();
            verifier.Init(false, pub);
            verifier.BlockUpdate(msg.ToArray(), 0, msg.Length);
            return verifier.VerifySignature(sig.ToArray());
        }

        public byte[] DeriveSharedSecret(ReadOnlySpan<byte> peerX25519Public)
        {
            if (peerX25519Public.Length != X25519PubLen)
            {
                throw new ArgumentException($"peer X25519 public must be {X25519PubLen} bytes", nameof(peerX25519Public));
            }
            var priv = new X25519PrivateKeyParameters(X25519PrivateSeed, 0);
            var pub = new X25519PublicKeyParameters(peerX25519Public.ToArray(), 0);
            var agreement = new X25519Agreement();
            agreement.Init(priv);
            var shared = new byte[32];
            agreement.CalculateAgreement(pub, shared, 0);
            return shared;
        }
    }

    public static DeviceKeyPair Generate()
    {
        // Ed25519
        var edGen = new Ed25519KeyPairGenerator();
        edGen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var edKp = edGen.GenerateKeyPair();
        var edPriv = (Ed25519PrivateKeyParameters)edKp.Private;
        var edPub = (Ed25519PublicKeyParameters)edKp.Public;
        var edSeed = edPriv.GetEncoded();
        var edPubBytes = edPub.GetEncoded();

        // X25519
        var xGen = new X25519KeyPairGenerator();
        xGen.Init(new X25519KeyGenerationParameters(new SecureRandom()));
        var xKp = xGen.GenerateKeyPair();
        var xPriv = (X25519PrivateKeyParameters)xKp.Private;
        var xPub = (X25519PublicKeyParameters)xKp.Public;
        var xPrivSeed = xPriv.GetEncoded();
        var xPubBytes = xPub.GetEncoded();

        return new DeviceKeyPair(edSeed, xPrivSeed, edPubBytes, xPubBytes);
    }

    public static DeviceKeyPair FromSeeds(byte[] ed25519Seed, byte[] x25519PrivateSeed)
    {
        if (ed25519Seed.Length != SeedLen)
        {
            throw new ArgumentException("ed25519 seed must be 32 bytes", nameof(ed25519Seed));
        }
        if (x25519PrivateSeed.Length != SeedLen)
        {
            throw new ArgumentException("x25519 seed must be 32 bytes", nameof(x25519PrivateSeed));
        }

        var edPubParams = new Ed25519PrivateKeyParameters(ed25519Seed, 0).GeneratePublicKey();
        var edPubBytes = edPubParams.GetEncoded();

        var xPubParams = new X25519PrivateKeyParameters(x25519PrivateSeed, 0).GeneratePublicKey();
        var xPubBytes = xPubParams.GetEncoded();

        return new DeviceKeyPair(
            (byte[])ed25519Seed.Clone(),
            (byte[])x25519PrivateSeed.Clone(),
            edPubBytes,
            xPubBytes);
    }
}
