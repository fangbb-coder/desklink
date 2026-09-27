using DeskLink.Protocol.Crypto;

namespace Protocol.Tests;

public class HkdfAndAeadTests
{
    [Fact]
    public void Hkdf_Derive_Deterministic()
    {
        var ikm = new byte[32];
        for (int i = 0; i < 32; i++) ikm[i] = (byte)i;
        var salt = new byte[16];
        for (int i = 0; i < 16; i++) salt[i] = (byte)(0xA0 + i);
        var info = System.Text.Encoding.UTF8.GetBytes("DeskLink/test/info");

        var a = HkdfSha256.Derive(ikm, salt, info, 64);
        var b = HkdfSha256.Derive(ikm, salt, info, 64);
        Assert.Equal(a, b);

        // 不同 info 应当得到不同输出
        var info2 = System.Text.Encoding.UTF8.GetBytes("DeskLink/test/info2");
        var c = HkdfSha256.Derive(ikm, salt, info2, 64);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Aead_RoundTrip_SequentialFrames()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        using var enc = new AeadSession(key);
        using var dec = new AeadSession(key);

        for (int i = 0; i < 5; i++)
        {
            var plain = System.Text.Encoding.UTF8.GetBytes("frame " + i + " payload");
            var aad = System.Text.Encoding.UTF8.GetBytes("aad-" + i);
            var sealed_ = enc.Seal(plain, aad);
            var back = dec.Open(sealed_, aad);
            Assert.Equal(plain, back);
        }
    }

    [Fact]
    public void Aead_ReorderedCounter_Fails()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);

        using var enc = new AeadSession(key);
        using var dec = new AeadSession(key);

        // s1 counter=0, s2 counter=1
        var s1 = enc.Seal(new byte[] { 1 }, new byte[] { 0 });
        var s2 = enc.Seal(new byte[] { 2 }, new byte[] { 0 });
        // 解密顺序故意倒置：先解 s2（counter=1 但 dec counter=0）→ 应失败
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => dec.Open(s2, new byte[] { 0 }));
    }

    [Fact]
    public void Aead_DifferentKey_Fails()
    {
        var key1 = new byte[32];
        var key2 = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key1);
        System.Security.Cryptography.RandomNumberGenerator.Fill(key2);

        using var enc = new AeadSession(key1);
        using var dec = new AeadSession(key2);
        var sealedFrame = enc.Seal(new byte[] { 1, 2, 3 }, new byte[] { 0 });
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => dec.Open(sealedFrame, new byte[] { 0 }));
    }
}
