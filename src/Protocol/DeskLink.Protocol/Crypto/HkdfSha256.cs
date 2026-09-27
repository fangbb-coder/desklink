// HKDF-SHA256 封装（BouncyCastle）。
// 用途：从 IKM + salt + info 派生会话密钥。
// 协议约定：会话密钥派生用 HKDF-SHA256，输出 64 字节，前 32 字节为 initiator→responder 单向 key，后 32 字节为反向 key。
using Org.BouncyCastle.Crypto.Generators;

namespace DeskLink.Protocol.Crypto;

public static class HkdfSha256
{
    public const int HashLen = 32;

    public static byte[] Derive(ReadOnlySpan<byte> ikm, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, int outLen)
    {
        // BouncyCastle HKDF 需要 byte[]，拷贝一次
        var ikmArr = ikm.ToArray();
        var saltArr = salt.Length == 0 ? new byte[HashLen] : salt.ToArray();
        var infoArr = info.ToArray();

        var kdf = new HkdfBytesGenerator(new Org.BouncyCastle.Crypto.Digests.Sha256Digest());
        kdf.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(ikmArr, saltArr, infoArr));
        var output = new byte[outLen];
        kdf.GenerateBytes(output, 0, outLen);
        return output;
    }
}
