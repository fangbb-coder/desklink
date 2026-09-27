// 文件流业务帧编解码（P6）。
//
// 说明：文件流是 **C# 端到端** 的业务帧（relay 只见密文，Go 侧从不解析），
// 因此这些负载格式不需要与 Go 对齐；但**多字节整数一律 big-endian**，
// 与 README「协议字节序」的仓库级约定保持一致。
//
// 帧目录（ProtocolConstants.FrameType）：
//   0x30 FileListRequest   [u16 pathLen][utf8 path]
//   0x31 FileListResponse  [u8 ok][u16 errLen][utf8 err][u32 count][entry...]
//                          entry = [u8 kind][u64 size][u64 mtimeUnixMs][u16 nameLen][utf8 name]
//   0x32 FileOpen          [u8 direction][u32 transferId][u32 chunkSize][u64 totalSize]
//                          [32B digest][u16 pathLen][utf8 path]
//   0x33 FileChunk         [u32 transferId][u64 chunkIndex][data...]
//   0x34 FileAck           见 FileAckFrame（位图，与 Go 侧对齐）
//   0x35 FilePause         FileStatus 负载
//   0x36 FileResume        FileStatus 负载
//   0x37 FileCancel        FileStatus 负载
//   0x38 FileComplete      [u32 transferId][32B digest]
//   回复统一用 FileStatus：[u32 transferId][u8 status][u16 msgLen][utf8 msg]
using System.Buffers.Binary;
using System.Text;

namespace DeskLink.Service.FileTransfer;

/// <summary>传输方向（FileOpen 的 direction 字段）。</summary>
public static class FileTransferDirection
{
    /// <summary>我（FileOpen 发送方）把文件推给你。</summary>
    public const byte Push = 0x01;

    /// <summary>我请你把文件发给我（拉取）。</summary>
    public const byte Pull = 0x02;
}

/// <summary>传输状态码（FileStatus.status）。</summary>
public static class FileTransferStatus
{
    public const byte Ok = 0x00;
    public const byte DigestMismatch = 0x01;
    public const byte IoError = 0x02;
    public const byte DiskFull = 0x03;
    public const byte ScopeDenied = 0x04;
    public const byte Skipped = 0x05;
    public const byte Paused = 0x06;
    public const byte Resumed = 0x07;
    public const byte Cancelled = 0x08;
    public const byte NotFound = 0x09;
    public const byte ProtocolError = 0x0A;

    public static string Describe(byte status) => status switch
    {
        Ok => "ok",
        DigestMismatch => "digest mismatch",
        IoError => "io error",
        DiskFull => "disk full",
        ScopeDenied => "scope denied",
        Skipped => "skipped",
        Paused => "paused",
        Resumed => "resumed",
        Cancelled => "cancelled",
        NotFound => "not found",
        ProtocolError => "protocol error",
        _ => $"unknown(0x{status:x2})",
    };
}

/// <summary>远端目录项。</summary>
public readonly record struct FileEntry(string Name, bool IsDirectory, long Size, long ModifiedUnixMs);

/// <summary>FileOpen 负载。</summary>
public readonly record struct FileOpenFrame(
    byte Direction,
    uint TransferId,
    int ChunkSize,
    long TotalSize,
    byte[] Digest,
    string RelativePath,
    byte Policy = (byte)FileConflictPolicy.Overwrite);

/// <summary>FileStatus 负载。</summary>
public readonly record struct FileStatusFrame(uint TransferId, byte Status, string Message);

/// <summary>文件流帧编解码。</summary>
public static class FileTransferFrames
{
    public const int DigestSize = 32;

    // —— FileOpen ——

    public static byte[] EncodeOpen(FileOpenFrame f)
    {
        if (f.Digest.Length != DigestSize)
        {
            throw new ArgumentException($"digest 必须是 {DigestSize} 字节", nameof(f));
        }
        var path = Encoding.UTF8.GetBytes(f.RelativePath ?? "");
        if (path.Length > ushort.MaxValue)
        {
            throw new ArgumentException("路径过长", nameof(f));
        }

        var buf = new byte[1 + 1 + 4 + 4 + 8 + DigestSize + 2 + path.Length];
        var w = new PayloadWriter(buf);
        w.U8(f.Direction);
        w.U8(f.Policy);
        w.U32(f.TransferId);
        w.U32((uint)f.ChunkSize);
        w.U64((ulong)f.TotalSize);
        w.Bytes(f.Digest);
        w.U16((ushort)path.Length);
        w.Bytes(path);
        return buf;
    }

    public static FileOpenFrame DecodeOpen(ReadOnlySpan<byte> p)
    {
        var r = new PayloadReader(p);
        var direction = r.U8();
        if (direction != FileTransferDirection.Push && direction != FileTransferDirection.Pull)
        {
            throw new InvalidDataException($"非法传输方向 0x{direction:x2}");
        }
        var policy = r.U8();
        var transferId = r.U32();
        var chunkSizeRaw = r.U32();
        var totalSize = r.U64();
        var digest = r.Bytes(DigestSize).ToArray();
        var pathLen = r.U16();
        var path = r.Utf8(pathLen);
        r.EnsureConsumed();

        if (chunkSizeRaw == 0 || chunkSizeRaw > int.MaxValue)
        {
            throw new InvalidDataException($"非法块大小 {chunkSizeRaw}");
        }
        if (totalSize > long.MaxValue)
        {
            throw new InvalidDataException($"非法文件大小 {totalSize}");
        }
        ChunkLayout.ValidateChunkSize((int)chunkSizeRaw);
        return new FileOpenFrame(direction, transferId, (int)chunkSizeRaw, (long)totalSize, digest, path, policy);
    }

    // —— FileChunk ——

    public static byte[] EncodeChunk(uint transferId, long chunkIndex, ReadOnlySpan<byte> data)
    {
        if (data.Length > ChunkLayout.MaxChunkDataSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data), $"块数据 {data.Length} 超过上限 {ChunkLayout.MaxChunkDataSize}");
        }
        var buf = new byte[ChunkLayout.ChunkHeaderSize + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), transferId);
        BinaryPrimitives.WriteUInt64BigEndian(buf.AsSpan(4, 8), (ulong)chunkIndex);
        data.CopyTo(buf.AsSpan(ChunkLayout.ChunkHeaderSize));
        return buf;
    }

    public static (uint TransferId, long ChunkIndex, byte[] Data) DecodeChunk(ReadOnlySpan<byte> p)
    {
        if (p.Length < ChunkLayout.ChunkHeaderSize)
        {
            throw new InvalidDataException($"FileChunk 负载过短：{p.Length}");
        }
        var transferId = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(0, 4));
        var indexRaw = BinaryPrimitives.ReadUInt64BigEndian(p.Slice(4, 8));
        if (indexRaw > long.MaxValue)
        {
            throw new InvalidDataException($"非法块序号 {indexRaw}");
        }
        var data = p.Slice(ChunkLayout.ChunkHeaderSize).ToArray();
        return (transferId, (long)indexRaw, data);
    }

    // —— FileComplete ——

    public static byte[] EncodeComplete(uint transferId, ReadOnlySpan<byte> digest)
    {
        if (digest.Length != DigestSize)
        {
            throw new ArgumentException($"digest 必须是 {DigestSize} 字节", nameof(digest));
        }
        var buf = new byte[4 + DigestSize];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), transferId);
        digest.CopyTo(buf.AsSpan(4));
        return buf;
    }

    public static (uint TransferId, byte[] Digest) DecodeComplete(ReadOnlySpan<byte> p)
    {
        if (p.Length != 4 + DigestSize)
        {
            throw new InvalidDataException($"FileComplete 负载长度应为 {4 + DigestSize}，实际 {p.Length}");
        }
        var transferId = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(0, 4));
        return (transferId, p.Slice(4, DigestSize).ToArray());
    }

    // —— FileStatus ——

    public static byte[] EncodeStatus(uint transferId, byte status, string? message = null)
    {
        var msg = Encoding.UTF8.GetBytes(message ?? "");
        if (msg.Length > ushort.MaxValue)
        {
            msg = msg.AsSpan(0, ushort.MaxValue).ToArray();
        }
        var buf = new byte[4 + 1 + 2 + msg.Length];
        var w = new PayloadWriter(buf);
        w.U32(transferId);
        w.U8(status);
        w.U16((ushort)msg.Length);
        w.Bytes(msg);
        return buf;
    }

    public static FileStatusFrame DecodeStatus(ReadOnlySpan<byte> p)
    {
        var r = new PayloadReader(p);
        var transferId = r.U32();
        var status = r.U8();
        var len = r.U16();
        var msg = r.Utf8(len);
        r.EnsureConsumed();
        return new FileStatusFrame(transferId, status, msg);
    }

    // —— FileListRequest / FileListResponse ——

    public static byte[] EncodeListRequest(string? relativePath)
    {
        var path = Encoding.UTF8.GetBytes(relativePath ?? "");
        if (path.Length > ushort.MaxValue) throw new ArgumentException("路径过长", nameof(relativePath));
        var buf = new byte[2 + path.Length];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0, 2), (ushort)path.Length);
        path.CopyTo(buf.AsSpan(2));
        return buf;
    }

    public static string DecodeListRequest(ReadOnlySpan<byte> p)
    {
        var r = new PayloadReader(p);
        var len = r.U16();
        var path = r.Utf8(len);
        r.EnsureConsumed();
        return path;
    }

    public static byte[] EncodeListResponse(IReadOnlyList<FileEntry> entries, string? error)
    {
        var errBytes = Encoding.UTF8.GetBytes(error ?? "");
        // 注意：这里**不能**用 BinaryWriter —— 它默认写 little-endian，
        // 而本仓库约定多字节整数一律 big-endian（解码端也按 BE 读）。
        // 早期版本正是踩了这个坑：编码 LE、解码 BE，导致列表项全部错位。
        var size = 1 + 2 + errBytes.Length + 4;
        foreach (var e in entries)
        {
            size += 1 + 8 + 8 + 2 + Encoding.UTF8.GetByteCount(e.Name);
        }
        var buf = new byte[size];
        var w = new PayloadWriter(buf);
        w.U8((byte)(error is null ? 1 : 0));
        w.U16((ushort)errBytes.Length);
        w.Bytes(errBytes);
        w.U32((uint)entries.Count);
        foreach (var e in entries)
        {
            var name = Encoding.UTF8.GetBytes(e.Name);
            w.U8((byte)(e.IsDirectory ? 1 : 0));
            w.U64((ulong)e.Size);
            w.U64((ulong)e.ModifiedUnixMs);
            w.U16((ushort)name.Length);
            w.Bytes(name);
        }
        return buf;
    }

    public static (string? Error, IReadOnlyList<FileEntry> Entries) DecodeListResponse(ReadOnlySpan<byte> p)
    {
        var r = new PayloadReader(p);
        var ok = r.U8() != 0;
        var errLen = r.U16();
        var err = r.Utf8(errLen);
        var count = r.U32();
        if (count > 1_000_000)
        {
            throw new InvalidDataException($"目录项数量异常：{count}");
        }
        var list = new List<FileEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var kind = r.U8();
            var size = r.U64();
            var mtime = r.U64();
            var nameLen = r.U16();
            var name = r.Utf8(nameLen);
            list.Add(new FileEntry(name, kind == 1, (long)size, (long)mtime));
        }
        r.EnsureConsumed();
        return (ok ? null : err, list);
    }

    // —— FileAck（会话层包装）——

    /// <summary>
    /// 会话层的 FileAck 负载 = <c>[u32 transferId][FileAckFrame 负载]</c>。
    ///
    /// 为什么要在外面套一层 transferId：<see cref="Protocol.Frames.FileAckFrame"/> 是
    /// 与 Go 侧对齐的**位图原语**（不含会话概念），而一条会话上可能同时存在多个传输，
    /// 接收端必须能说明"这个 ack 属于哪条传输"。位图原语保持不变，
    /// 跨语言黄金向量继续有效。
    /// </summary>
    public static byte[] EncodeAck(uint transferId, ulong baseChunk, ReadOnlySpan<bool> bits)
    {
        var ack = new Protocol.Frames.FileAckFrame(
            baseChunk, (uint)bits.Length, Protocol.Frames.FileAckFrame.PackBitmap(bits));
        return WrapAck(transferId, ack);
    }

    /// <summary>直接给位图字节（避免 bool[] 中转，供 tracker.BuildBitmap 的输出直用）。</summary>
    public static byte[] EncodeAckBitmap(uint transferId, ulong baseChunk, int bitCount, ReadOnlySpan<byte> bitmap)
    {
        var ack = new Protocol.Frames.FileAckFrame(baseChunk, (uint)bitCount, bitmap.ToArray());
        return WrapAck(transferId, ack);
    }

    private static byte[] WrapAck(uint transferId, Protocol.Frames.FileAckFrame ack)
    {
        var inner = ack.Encode();
        var buf = new byte[4 + inner.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(0, 4), transferId);
        inner.CopyTo(buf.AsSpan(4));
        return buf;
    }

    /// <summary>会话层 FileAck 解码。</summary>
    public static (uint TransferId, Protocol.Frames.FileAckFrame Ack) DecodeAck(ReadOnlySpan<byte> p)
    {
        if (p.Length < 4 + Protocol.Frames.FileAckFrame.HeaderSize)
        {
            throw new InvalidDataException($"FileAck 负载过短：{p.Length}");
        }
        var transferId = BinaryPrimitives.ReadUInt32BigEndian(p.Slice(0, 4));
        var ack = Protocol.Frames.FileAckFrame.Decode(p.Slice(4));
        return (transferId, ack);
    }

    /// <summary>单个 ack 帧最多能覆盖的位数（受内层帧 u16 负载上限约束）。</summary>
    public static int MaxAckBits =>
        (Protocol.Frames.FrameCodec.MaxFramePayloadWireSize - 4 - Protocol.Frames.FileAckFrame.HeaderSize) * 8;

    // —— 小工具 ——

    private ref struct PayloadWriter
    {
        private readonly Span<byte> _buf;
        private int _pos;

        public PayloadWriter(Span<byte> buf) { _buf = buf; _pos = 0; }

        public void U8(byte v) { _buf[_pos++] = v; }
        public void U16(ushort v) { BinaryPrimitives.WriteUInt16BigEndian(_buf.Slice(_pos, 2), v); _pos += 2; }
        public void U32(uint v) { BinaryPrimitives.WriteUInt32BigEndian(_buf.Slice(_pos, 4), v); _pos += 4; }
        public void U64(ulong v) { BinaryPrimitives.WriteUInt64BigEndian(_buf.Slice(_pos, 8), v); _pos += 8; }
        public void Bytes(ReadOnlySpan<byte> v) { v.CopyTo(_buf.Slice(_pos)); _pos += v.Length; }
    }

    private ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> _buf;
        private int _pos;

        public PayloadReader(ReadOnlySpan<byte> buf) { _buf = buf; _pos = 0; }

        private void Need(int n)
        {
            if (_pos + n > _buf.Length)
            {
                throw new InvalidDataException($"负载不完整：需要 {n} 字节，剩余 {_buf.Length - _pos}");
            }
        }

        public byte U8() { Need(1); return _buf[_pos++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16BigEndian(_buf.Slice(_pos, 2)); _pos += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32BigEndian(_buf.Slice(_pos, 4)); _pos += 4; return v; }
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64BigEndian(_buf.Slice(_pos, 8)); _pos += 8; return v; }
        public ReadOnlySpan<byte> Bytes(int n) { Need(n); var v = _buf.Slice(_pos, n); _pos += n; return v; }

        public string Utf8(int n)
        {
            Need(n);
            var v = _buf.Slice(_pos, n);
            _pos += n;
            return Encoding.UTF8.GetString(v);
        }

        public void EnsureConsumed()
        {
            if (_pos != _buf.Length)
            {
                throw new InvalidDataException($"负载有多余字节：已读 {_pos}，总长 {_buf.Length}");
            }
        }
    }
}
