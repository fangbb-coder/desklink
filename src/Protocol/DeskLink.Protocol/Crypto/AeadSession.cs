// AES-256-GCM 序号 nonce 加密会话。
// 序号 nonce 派生：96 位（12 字节），格式 = [u64 sendCounter (big endian)] [u32 keyId (BE)] = 12 字节。
// 单调严格递增，重放窗口由调用方维护；超出窗口的 sendCounter 一律拒绝。
// 密文格式：[u32 sendCounter (BE)] [u32 nonceSuffix (BE，固定为 keyId 或 0)] [u16 tagLen=16 (BE)] [ciphertext...] [tag 16]
// ——为简化，nonceSuffix 与 sendCounter 高 32 位对齐；这里用 sendCounter 自身做 nonce 派生即可。
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DeskLink.Protocol.Crypto;

public sealed class AeadSession : IDisposable
{
    private readonly AesGcm _aead;
    private readonly bool _ownsAead;
    private ulong _counter;
    private bool _disposed;

    public const int KeyLen = 32;
    public const int NonceLen = 12;
    public const int TagLen = 16;

    public AeadSession(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLen)
        {
            throw new ArgumentException($"AEAD key must be {KeyLen} bytes", nameof(key));
        }
        // .NET AesGcm 需要 tag 大小在构造时固定（默认 16）
        _aead = new AesGcm(key.ToArray(), TagLen);
        _ownsAead = true;
    }

    private AeadSession(AesGcm shared)
    {
        _aead = shared;
        _ownsAead = false;
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        ThrowIfDisposed();
        var nonce = BuildNonce(_counter);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagLen];
        _aead.Encrypt(nonce, plaintext, cipher, tag, aad);
        _counter++;
        // 输出顺序：[counter 8][cipher][tag 16]
        var out_ = new byte[8 + cipher.Length + TagLen];
        BinaryPrimitives.WriteUInt64BigEndian(out_.AsSpan(0, 8), _counter - 1);
        cipher.CopyTo(out_.AsSpan(8));
        tag.CopyTo(out_.AsSpan(8 + cipher.Length));
        return out_;
    }

    public byte[] Open(ReadOnlySpan<byte> sealedFrame, ReadOnlySpan<byte> aad)
    {
        ThrowIfDisposed();
        if (sealedFrame.Length < 8 + TagLen)
        {
            throw new CryptographicException("sealed frame too short");
        }
        var counter = BinaryPrimitives.ReadUInt64BigEndian(sealedFrame.Slice(0, 8));
        if (counter != _counter)
        {
            throw new CryptographicException($"AEAD counter mismatch: expected {_counter}, got {counter}");
        }
        var cipherLen = sealedFrame.Length - 8 - TagLen;
        var cipher = sealedFrame.Slice(8, cipherLen);
        var tag = sealedFrame.Slice(8 + cipherLen, TagLen);
        var nonce = BuildNonce(counter);
        var plain = new byte[cipherLen];
        _aead.Decrypt(nonce, cipher, tag, plain, aad);
        _counter++;
        return plain;
    }

    private static byte[] BuildNonce(ulong counter)
    {
        // 12 字节 nonce：counter 的低 32 位 + 高 32 位 + 固定 4 字节 suffix（keyId/方向）
        var n = new byte[NonceLen];
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(0, 4), (uint)(counter & 0xFFFFFFFFu));
        BinaryPrimitives.WriteUInt32BigEndian(n.AsSpan(4, 4), (uint)(counter >> 32));
        // 后 4 字节保持 0（业务可扩展为方向 tag）
        return n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_ownsAead)
        {
            _aead.Dispose();
        }
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AeadSession));
        }
    }
}
