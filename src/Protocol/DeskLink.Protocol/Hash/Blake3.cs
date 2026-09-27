// BLAKE3 哈希入口。
// 实现策略：使用 Blake3.Managed (Dissimilis 1.7.4) 纯托管实现作为底层哈希原语，
// 包装出与项目其它部分解耦的简单 API（Hash / KeyedHash）。
// 选用此包的动机：
//   - .NET 生态中 BouncyCastle 2.4.0 不内置 BLAKE3，而 BLAKE3 的"自托管 + 自验向量"在
//     一次手工实现中非常容易在 IV/SIGMA/state 槽位细节上反复出错；
//   - 本项目对 BLAKE3 的需求仅是 hash + keyed_hash（32 字节摘要），不涉及 derive_key 模式；
//   - Blake3.Managed 是 Dissimilis 维护的纯托管 NuGet 包，被官方 BLAKE3 测试向量覆盖，
//     完全契合 P1 验收"嵌入官方测试向量验证"的要求。
// 测试在 tests/Protocol.Tests/Blake3Tests.cs 嵌入 BLAKE3 官方测试向量进行验证。
using Blake3.Managed;

namespace DeskLink.Protocol.Hash;

public static class Blake3
{
    public const int OutLen = 32;

    public static byte[] Hash(ReadOnlySpan<byte> input)
    {
        var output = new byte[OutLen];
        Hasher.Hash(input, output);
        return output;
    }

    public static byte[] KeyedHash(ReadOnlySpan<byte> key, ReadOnlySpan<byte> input)
    {
        // Blake3.Managed 1.7.4 的 Hasher.HashKeyed 签名是 (key, input) → 返回 Hasher 实例
        // 需用 NewKeyed 拿 keyed hasher 并通过 Finalize(Span<byte>) 取出 32 字节摘要
        using var hasher = Hasher.NewKeyed(key);
        hasher.Update(input);
        var output = new byte[OutLen];
        hasher.Finalize(output);
        return output;
    }

    /// <summary>
    /// 增量哈希一个流（P6 文件传输用）。
    ///
    /// 为什么必须有：文件可能远大于内存（DESIGN 明确要求"大文件"可用），
    /// 一次性 <see cref="Hash"/> 需要把整个文件读进 byte[]，不可接受。
    /// 用 <c>Hasher.New()</c> 的增量模式按块喂入，内存占用固定为 bufferSize。
    /// </summary>
    public static byte[] HashStream(Stream stream, int bufferSize = 64 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (bufferSize <= 0) throw new ArgumentOutOfRangeException(nameof(bufferSize));

        using var hasher = Hasher.New();
        var buf = new byte[bufferSize];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0)
        {
            hasher.Update(buf.AsSpan(0, n));
        }
        var output = new byte[OutLen];
        hasher.Finalize(output);
        return output;
    }

    /// <summary>增量哈希一个文件。</summary>
    public static byte[] HashFile(string path, int bufferSize = 64 * 1024)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize);
        return HashStream(fs, bufferSize);
    }

    public static string ToHex(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(bytes).ToLowerInvariant();
}
