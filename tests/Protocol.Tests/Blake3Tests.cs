// BLAKE3 官方测试向量验证。
// 输入：input_len=0..8, 63, 64, 65, 127, 128, 129, 1023, 1024, 1025 的填充（重复 0..250）。
// key = "whats the Elvish word for friend"（ASCII 31 字节 + 1 字节 0x00 = 32 字节）
// 期望输出：测试向量 JSON 中的 hash / keyed_hash 字段。
using DeskLink.Protocol.Hash;
using Blake3Hash = DeskLink.Protocol.Hash.Blake3;

namespace Protocol.Tests;

public class Blake3Tests
{
    // 官方测试向量的子集（覆盖空、字节边界 63/64/65、chunk 边界 1023/1024/1025 等）
    private static (int InputLen, string Hash, string KeyedHash)[] Vectors =
    {
        (0,
         "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262",
         "92b2b75604ed3c761f9d6f62392c8a9227ad0ea3f09573e783f1498a4ed60d26"),
        (1,
         "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c1510225d0f592e213",
         "6d7878dfff2f485635d39013278ae14f1454b8c0a3a2d34bc1ab38228a80c95b"),
        (63,
         "e9bc37a594daad83be9470df7f7b3798297c3d834ce80ba85d6e207627b7db7b",
         "bb1eb5d4afa793c1ebdd9fb08def6c36d10096986ae0cfe148cd101170ce37ae"),
        (64,
         "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f4c6dc7f2511b98",
         "ba8ced36f327700d213f120b1a207a3b8c04330528586f414d09f2f7d9ccb7e6"),
        (65,
         "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1c30ecb6b263dee",
         "c0a4edefa2d2accb9277c371ac12fcdbb52988a86edc54f0716e1591b4326e72"),
        (1023,
         "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35bba72b28f70bd11",
         "c951ecdf03288d0fcc96ee3413563d8a6d3589547f2c2fb36d9786470f1b9d6e"),
        (1024,
         "42214739f095a406f3fc83deb889744ac00df831c10daa55189b5d121c855af7",
         "75c46f6f3d9eb4f55ecaaee480db732e6c2105546f1e675003687c31719c7ba4"),
        (1025,
         "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c8cb7fb814b8444",
         "357dc55de0c7e382c900fd6e320acc04146be01db6a8ce7210b7189bd664ea69"),
        (8192,
         "aae792484c8efe4f19e2ca7d371d8c467ffb10748d8a5a1ae579948f718a2a63",
         "dc9637c8845a770b4cbf76b8daec0eebf7dc2eac11498517f08d44c8fc00d58a"),
    };

    private static byte[] MakeInput(int len)
    {
        var input = new byte[len];
        for (int i = 0; i < len; i++)
        {
            input[i] = (byte)(i % 251);
        }
        return input;
    }

    private static byte[] MakeKey()
    {
        // 官方 key 文本（31 ASCII 字节）+ 1 字节 0x00 = 32 字节
        var s = System.Text.Encoding.ASCII.GetBytes("whats the Elvish word for friend");
        var key = new byte[32];
        Array.Copy(s, key, s.Length);
        return key;
    }

    [Theory]
    [MemberData(nameof(GetVectorData))]
    public void Hash_Matches_Official(int len, string expectedHex, string expectedKeyedHex)
    {
        var input = MakeInput(len);
        var actual = Convert.ToHexString(Blake3Hash.Hash(input)).ToLowerInvariant();
        Assert.Equal(expectedHex, actual);
    }

    [Theory]
    [MemberData(nameof(GetVectorData))]
    public void KeyedHash_Matches_Official(int len, string _, string expectedKeyedHex)
    {
        var input = MakeInput(len);
        var key = MakeKey();
        var actual = Convert.ToHexString(Blake3Hash.KeyedHash(key, input)).ToLowerInvariant();
        Assert.Equal(expectedKeyedHex, actual);
    }

    public static IEnumerable<object[]> GetVectorData()
    {
        foreach (var (len, hash, keyed) in Vectors)
        {
            yield return new object[] { len, hash, keyed };
        }
    }

    [Fact]
    public void Hash_EmptyInput_ProducesOfficialVector()
    {
        var actual = Convert.ToHexString(Blake3Hash.Hash(ReadOnlySpan<byte>.Empty)).ToLowerInvariant();
        Assert.Equal("af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc9a93cae41f3262", actual);
    }
}
