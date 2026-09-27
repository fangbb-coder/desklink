// P5 FileAck 位图帧编解码测试。
//
// 覆盖：
//   1) 往返编解码（baseChunk / bitCount / bitmap 完全一致）。
//   2) IsAcked 边界（baseChunk 之前、覆盖范围之外、最后一个字节的部分位）。
//   3) PackBitmap / UnpackBitmap 对称性。
//   4) 校验失败：payload 过短、bitmap 长度与 bitCount 不匹配。

using DeskLink.Protocol.Frames;
using Xunit;

namespace Protocol.Tests;

public class FileAckFrameTests
{
    [Fact]
    public void RoundTrip_EncodeDecode()
    {
        var frame = new FileAckFrame(
            BaseChunk: 42,
            BitCount: 16,
            Bitmap: new byte[] { 0b10101010, 0b01010101 });

        var encoded = frame.Encode();
        var decoded = FileAckFrame.Decode(encoded);

        Assert.Equal(42uL, decoded.BaseChunk);
        Assert.Equal(16u, decoded.BitCount);
        Assert.Equal(new byte[] { 0b10101010, 0b01010101 }, decoded.Bitmap);
    }

    [Fact]
    public void RoundTrip_LargeBaseChunk()
    {
        var frame = new FileAckFrame(
            BaseChunk: ulong.MaxValue - 100,
            BitCount: 8,
            Bitmap: new byte[] { 0xFF });

        var decoded = FileAckFrame.Decode(frame.Encode());
        Assert.Equal(ulong.MaxValue - 100, decoded.BaseChunk);
    }

    [Fact]
    public void IsAcked_BeforeBaseChunk_IsFalse()
    {
        var frame = new FileAckFrame(100, 8, new byte[] { 0xFF });
        Assert.False(frame.IsAcked(99));
    }

    [Fact]
    public void IsAcked_AfterRange_IsFalse()
    {
        var frame = new FileAckFrame(100, 8, new byte[] { 0xFF });
        Assert.False(frame.IsAcked(108)); // 100 + 8 = 108，越界
    }

    [Fact]
    public void IsAcked_MsbFirst()
    {
        // Bitmap = 0b10000000：只有 bit 0（chunk 100）被 ack
        var frame = new FileAckFrame(100, 8, new byte[] { 0b10000000 });
        Assert.True(frame.IsAcked(100));
        Assert.False(frame.IsAcked(101));
    }

    [Fact]
    public void IsAcked_PartialLastByte()
    {
        // bitCount = 3，bitmap = 1 byte：只认前 3 位
        var frame = new FileAckFrame(0, 3, new byte[] { 0b10100000 });
        Assert.True(frame.IsAcked(0));
        Assert.False(frame.IsAcked(1));
        Assert.True(frame.IsAcked(2));
    }

    [Theory]
    [InlineData(new[] { true, false, true, false }, new byte[] { 0b10100000 })]
    [InlineData(new[] { true, true, true, true, true, true, true, true }, new byte[] { 0xFF })]
    [InlineData(new bool[0], new byte[0])]
    public void PackBitmap_MatchesExpected(bool[] bits, byte[] expected)
    {
        var packed = FileAckFrame.PackBitmap(bits);
        Assert.Equal(expected, packed);
    }

    [Theory]
    [InlineData(new byte[] { 0b10100000 }, 4, new[] { true, false, true, false })]
    [InlineData(new byte[] { 0xFF }, 8, new[] { true, true, true, true, true, true, true, true })]
    public void UnpackBitmap_MatchesExpected(byte[] bitmap, int bitCount, bool[] expected)
    {
        var unpacked = FileAckFrame.UnpackBitmap(bitmap, bitCount);
        Assert.Equal(expected, unpacked);
    }

    [Fact]
    public void PackUnpack_RoundTrip()
    {
        var rng = new Random(42);
        var bits = new bool[1000];
        for (var i = 0; i < bits.Length; i++) bits[i] = rng.Next(2) == 1;

        var packed = FileAckFrame.PackBitmap(bits);
        var unpacked = FileAckFrame.UnpackBitmap(packed, bits.Length);
        Assert.Equal(bits, unpacked);
    }

    [Fact]
    public void Decode_PayloadTooShort_Throws()
    {
        Assert.Throws<InvalidDataException>(() => FileAckFrame.Decode(new byte[10]));
    }

    [Fact]
    public void Decode_BitmapLengthMismatch_Throws()
    {
        // bitCount = 9 需要 2 字节，但只给 1 字节
        var bad = new byte[13]; // 12 header + 1 byte
        bad[8] = 0; bad[9] = 0; bad[10] = 0; bad[11] = 9; // bitCount = 9
        Assert.Throws<InvalidDataException>(() => FileAckFrame.Decode(bad));
    }
}
