using System.Buffers.Binary;
using DeskLink.Protocol.Common;
using DeskLink.Protocol.Mux;
using DeskLink.Service.FileTransfer;
using DeskLink.Service.Session;
using Xunit;
using Xunit.Abstractions;

namespace DeskLink.Service.Tests;

/// <summary>
/// P6 文件流测试：scope 授权、分块/ack 断点续传、暂停/取消、BLAKE3 校验、
/// `.part` + 原子改名、覆盖/重命名/跳过冲突策略、磁盘满。
///
/// 端到端部分用"两个引擎 + 内存 sink 对接"跑真实传输（真实文件系统、真实 BLAKE3），
/// 只有磁盘满通过注入 IFileTransferIo 模拟——把磁盘写满在单测里不可行。
/// </summary>
public class FileTransferTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _root;

    public FileTransferTests(ITestOutputHelper output)
    {
        _out = output;
        _root = Path.Combine(Path.GetTempPath(), $"desklink-ft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string NewDir(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    // ────────────────────────────────────────────────────────────────────────
    // scope 授权
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scope_ResolvesRelativePathWithinRoot()
    {
        var root = NewDir("s1");
        var scope = new FileTransferScope(new[] { root });

        var resolved = scope.Resolve("sub/file.txt");
        Assert.Equal(Path.Combine(root, "sub", "file.txt"), resolved);
        Assert.True(FileTransferScope.IsWithinRoot(root, resolved));
    }

    [Fact]
    public void Scope_RootItselfIsResolvable()
    {
        var root = NewDir("s2");
        var scope = new FileTransferScope(new[] { root });
        Assert.Equal(root, scope.Resolve(""));
        Assert.Equal(root, scope.Resolve("."));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\?\C:\secret")]
    [InlineData(@"..\..\Windows\file")]
    [InlineData("../outside.txt")]
    [InlineData(@"sub\..\..\escape")]
    [InlineData("file.txt:stream")]
    [InlineData("")]
    public void Scope_RejectsOutOfScopePaths(string relative)
    {
        var root = NewDir("s3");
        var scope = new FileTransferScope(new[] { root });

        if (relative == "")
        {
            // 空串是合法的（= 根），单独断言，避免误判。
            Assert.Equal(root, scope.Resolve(relative));
            return;
        }
        Assert.Throws<FileScopeException>(() => scope.Resolve(relative));
    }

    /// <summary>
    /// 经典前缀绕过：root=`C:\data` 时 `C:\database` 必须被拒。
    /// 单纯 StartsWith(root) 会误判为"在范围内"。
    /// </summary>
    [Fact]
    public void Scope_RejectsSiblingWithSharedPrefix()
    {
        var root = NewDir("data");
        var sibling = NewDir("database");
        var scope = new FileTransferScope(new[] { root });

        Assert.False(FileTransferScope.IsWithinRoot(root, Path.Combine(sibling, "x.txt")));
        Assert.False(FileTransferScope.IsWithinRoot(root, sibling));
        Assert.True(FileTransferScope.IsWithinRoot(root, root));
        Assert.True(FileTransferScope.IsWithinRoot(root, Path.Combine(root, "x.txt")));
    }

    [Fact]
    public void Scope_EmptyRejectsEverything()
    {
        Assert.True(FileTransferScope.Empty.IsEmpty);
        Assert.Throws<FileScopeException>(() => FileTransferScope.Empty.Resolve("a.txt"));
    }

    [Fact]
    public void Scope_ToRelativeRoundTrips()
    {
        var root = NewDir("s4");
        var scope = new FileTransferScope(new[] { root });
        var abs = Path.Combine(root, "a", "b.txt");
        Assert.Equal("a/b.txt", scope.ToRelative(abs));
        Assert.Null(scope.ToRelative(Path.Combine(NewDir("other"), "b.txt")));
        Assert.Equal("", scope.ToRelative(root));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 分块
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 1000, 0)]
    [InlineData(999, 1000, 1)]
    [InlineData(1000, 1000, 1)]
    [InlineData(1001, 1000, 2)]
    [InlineData(2500, 1000, 3)]
    public void ChunkLayout_ComputesChunkCount(long total, int chunk, long expected)
    {
        var layout = ChunkLayout.Create(total, chunk);
        Assert.Equal(expected, layout.ChunkCount);
    }

    [Fact]
    public void ChunkLayout_LastChunkIsShorter()
    {
        var layout = ChunkLayout.Create(2500, 1000);
        Assert.Equal(1000, layout.LengthOf(0));
        Assert.Equal(1000, layout.LengthOf(1));
        Assert.Equal(500, layout.LengthOf(2));
        Assert.Equal(0, layout.LengthOf(3));
        Assert.Equal(2000, layout.OffsetOf(2));
    }

    [Fact]
    public void ChunkLayout_RejectsIllegalChunkSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkLayout.Create(10, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkLayout.Create(10, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkLayout.Create(10, ChunkLayout.MaxChunkDataSize + 1));
        // 上限本身合法
        _ = ChunkLayout.Create(10, ChunkLayout.MaxChunkDataSize);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 冲突策略
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Conflict_OverwriteUsesTarget()
    {
        var target = Path.Combine(Path.GetTempPath(), "x", "a.txt");
        var r = FileConflictResolver.Resolve(target, FileConflictPolicy.Overwrite, _ => true);
        Assert.False(r.Skipped);
        Assert.Equal(target, r.TargetPath);
    }

    [Fact]
    public void Conflict_SkipReturnsSkippedWhenExists()
    {
        var target = Path.Combine(Path.GetTempPath(), "x", "a.txt");
        var exists = FileConflictResolver.Resolve(target, FileConflictPolicy.Skip, _ => true);
        Assert.True(exists.Skipped);

        var missing = FileConflictResolver.Resolve(target, FileConflictPolicy.Skip, _ => false);
        Assert.False(missing.Skipped);
        Assert.Equal(target, missing.TargetPath);
    }

    [Fact]
    public void Conflict_RenameAppendsCounter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "x");
        var target = Path.Combine(dir, "a.txt");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            target,
            Path.Combine(dir, "a (1).txt"),
        };
        var r = FileConflictResolver.Resolve(target, FileConflictPolicy.Rename, taken.Contains);
        Assert.False(r.Skipped);
        Assert.Equal(Path.Combine(dir, "a (2).txt"), r.TargetPath);
    }

    [Fact]
    public void Conflict_RenameWhenFreeKeepsName()
    {
        var target = Path.Combine(Path.GetTempPath(), "x", "a.txt");
        var r = FileConflictResolver.Resolve(target, FileConflictPolicy.Rename, _ => false);
        Assert.Equal(target, r.TargetPath);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 帧编解码
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Frames_OpenRoundTrip()
    {
        var digest = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var original = new FileOpenFrame(FileTransferDirection.Push, 0x01020304, 4096, 123456, digest,
            "a/b c/文件.bin", (byte)FileConflictPolicy.Rename);

        var decoded = FileTransferFrames.DecodeOpen(FileTransferFrames.EncodeOpen(original));

        Assert.Equal(original.Direction, decoded.Direction);
        Assert.Equal(original.TransferId, decoded.TransferId);
        Assert.Equal(original.ChunkSize, decoded.ChunkSize);
        Assert.Equal(original.TotalSize, decoded.TotalSize);
        Assert.Equal(original.Digest, decoded.Digest);
        Assert.Equal(original.RelativePath, decoded.RelativePath);
        Assert.Equal(original.Policy, decoded.Policy);
    }

    [Fact]
    public void Frames_ChunkRoundTrip()
    {
        var data = new byte[ChunkLayout.MaxChunkDataSize];
        Random.Shared.NextBytes(data);
        var payload = FileTransferFrames.EncodeChunk(7, 42, data);
        var (id, index, got) = FileTransferFrames.DecodeChunk(payload);
        Assert.Equal(7u, id);
        Assert.Equal(42L, index);
        Assert.Equal(data, got);
    }

    [Fact]
    public void Frames_ChunkRejectsOversize()
    {
        var tooBig = new byte[ChunkLayout.MaxChunkDataSize + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => FileTransferFrames.EncodeChunk(1, 0, tooBig));
    }

    [Fact]
    public void Frames_CompleteAndStatusRoundTrip()
    {
        var digest = new byte[32];
        Random.Shared.NextBytes(digest);
        var (id, d) = FileTransferFrames.DecodeComplete(FileTransferFrames.EncodeComplete(9, digest));
        Assert.Equal(9u, id);
        Assert.Equal(digest, d);

        var st = FileTransferFrames.DecodeStatus(
            FileTransferFrames.EncodeStatus(9, FileTransferStatus.DiskFull, "磁盘满"));
        Assert.Equal(9u, st.TransferId);
        Assert.Equal(FileTransferStatus.DiskFull, st.Status);
        Assert.Equal("磁盘满", st.Message);
    }

    [Fact]
    public void Frames_ListRoundTrip()
    {
        var entries = new List<FileEntry>
        {
            new("dir", true, 0, 111),
            new("a.txt", false, 1234, 222),
        };
        var (err, got) = FileTransferFrames.DecodeListResponse(
            FileTransferFrames.EncodeListResponse(entries, null));
        Assert.Null(err);
        Assert.Equal(entries, got);

        var (err2, got2) = FileTransferFrames.DecodeListResponse(
            FileTransferFrames.EncodeListResponse(Array.Empty<FileEntry>(), "拒绝"));
        Assert.Equal("拒绝", err2);
        Assert.Empty(got2);

        var req = FileTransferFrames.DecodeListRequest(FileTransferFrames.EncodeListRequest("sub/dir"));
        Assert.Equal("sub/dir", req);
    }

    [Fact]
    public void Frames_AckWrapRoundTrip()
    {
        var bits = new[] { true, false, true, true, false };
        var (id, ack) = FileTransferFrames.DecodeAck(FileTransferFrames.EncodeAck(3, 10, bits));
        Assert.Equal(3u, id);
        Assert.Equal(10UL, ack.BaseChunk);
        Assert.Equal(5u, ack.BitCount);
        Assert.Equal(bits, DeskLink.Protocol.Frames.FileAckFrame.UnpackBitmap(ack.Bitmap, 5));
    }

    [Fact]
    public void Frames_DecodeOpenRejectsMalformed()
    {
        Assert.Throws<InvalidDataException>(() => FileTransferFrames.DecodeOpen(new byte[3]));

        // 方向非法
        var bad = FileTransferFrames.EncodeOpen(new FileOpenFrame(
            FileTransferDirection.Push, 1, 1024, 0, new byte[32], "x"));
        bad[0] = 0x7F;
        Assert.Throws<InvalidDataException>(() => FileTransferFrames.DecodeOpen(bad));
    }

    // ────────────────────────────────────────────────────────────────────────
    // 端到端（两个引擎 + 内存 sink）
    // ────────────────────────────────────────────────────────────────────────

    private sealed class LoopbackSink : IFileFrameSink
    {
        public FileTransferEngine? Peer;
        public long PayloadBytes;

        public Task SendAsync(ProtocolConstants.FrameType type, byte[] payload, CancellationToken ct)
        {
            Interlocked.Add(ref PayloadBytes, payload.Length);
            Peer?.OnFrame(new InboundFrame(type, payload, StreamId.FileBase));
            return Task.CompletedTask;
        }
    }

    /// <summary>把两个引擎用内存 sink 对接（真实文件系统、真实 BLAKE3）。</summary>
    private (FileTransferEngine A, FileTransferEngine B, LoopbackSink SinkA, LoopbackSink SinkB)
        CreatePair(FileTransferScope scopeA, FileTransferScope scopeB, IFileTransferIo? ioA = null, IFileTransferIo? ioB = null)
    {
        var engineA = new FileTransferEngine(scopeA, ioA, msg => _out.WriteLine($"[A] {msg}"));
        var engineB = new FileTransferEngine(scopeB, ioB, msg => _out.WriteLine($"[B] {msg}"));
        var sinkA = new LoopbackSink();
        var sinkB = new LoopbackSink();
        sinkA.Peer = engineB;
        sinkB.Peer = engineA;
        engineA.Attach(sinkA);
        engineB.Attach(sinkB);
        return (engineA, engineB, sinkA, sinkB);
    }

    private static void WriteFile(string path, int size, int seed = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        var rng = new Random(seed);
        rng.NextBytes(data);
        File.WriteAllBytes(path, data);
    }

    [Fact]
    public async Task E2E_Upload_MultiChunk_TransfersAndVerifies()
    {
        var srcRoot = NewDir("up-src");
        var dstRoot = NewDir("up-dst");
        // 2500 字节、块 1000 → 3 块（含一个短尾块），覆盖分块边界。
        WriteFile(Path.Combine(srcRoot, "payload.bin"), 2500, seed: 7);

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync("payload.bin", "payload.bin", FileConflictPolicy.Overwrite);

            Assert.True(outcome.Ok, outcome.Error);
            Assert.Equal(2500, outcome.BytesTransferred);

            var written = Path.Combine(dstRoot, "payload.bin");
            Assert.True(File.Exists(written));
            Assert.Equal(File.ReadAllBytes(Path.Combine(srcRoot, "payload.bin")), File.ReadAllBytes(written));
            // .part 必须已被原子改名消耗掉
            Assert.False(File.Exists(written + ".part"));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_EmptyFile_Transfers()
    {
        var srcRoot = NewDir("empty-src");
        var dstRoot = NewDir("empty-dst");
        File.WriteAllBytes(Path.Combine(srcRoot, "empty.bin"), Array.Empty<byte>());

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync("empty.bin", "empty.bin", FileConflictPolicy.Overwrite);
            Assert.True(outcome.Ok, outcome.Error);
            Assert.True(File.Exists(Path.Combine(dstRoot, "empty.bin")));
            Assert.Empty(File.ReadAllBytes(Path.Combine(dstRoot, "empty.bin")));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_Download_PullWorks()
    {
        var ourRoot = NewDir("dl-ours");
        var peerRoot = NewDir("dl-peer");
        WriteFile(Path.Combine(peerRoot, "remote.bin"), 3000, seed: 11);

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { ourRoot }),
                                      new FileTransferScope(new[] { peerRoot }));
        try
        {
            // A 拉取 B 的文件；B 的 FileOpen(Pull) 会带回真实元数据。
            var outcome = await a.DownloadAsync("remote.bin", "got.bin", FileConflictPolicy.Overwrite);

            Assert.True(outcome.Ok, outcome.Error);
            Assert.Equal(Path.Combine(ourRoot, "got.bin"), outcome.TargetPath);
            Assert.Equal(File.ReadAllBytes(Path.Combine(peerRoot, "remote.bin")),
                         File.ReadAllBytes(Path.Combine(ourRoot, "got.bin")));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_ConflictOverwrite_ReplacesTarget()
    {
        var srcRoot = NewDir("co-src");
        var dstRoot = NewDir("co-dst");
        WriteFile(Path.Combine(srcRoot, "f.bin"), 500, seed: 1);
        WriteFile(Path.Combine(dstRoot, "f.bin"), 999, seed: 2); // 已存在，内容不同

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync("f.bin", "f.bin", FileConflictPolicy.Overwrite);
            Assert.True(outcome.Ok, outcome.Error);
            Assert.Equal(File.ReadAllBytes(Path.Combine(srcRoot, "f.bin")),
                         File.ReadAllBytes(Path.Combine(dstRoot, "f.bin")));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_ConflictRename_KeepsExisting()
    {
        var srcRoot = NewDir("cr-src");
        var dstRoot = NewDir("cr-dst");
        WriteFile(Path.Combine(srcRoot, "f.bin"), 400, seed: 3);
        WriteFile(Path.Combine(dstRoot, "f.bin"), 777, seed: 4);
        var originalTarget = File.ReadAllBytes(Path.Combine(dstRoot, "f.bin"));

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync("f.bin", "f.bin", FileConflictPolicy.Rename);
            Assert.True(outcome.Ok, outcome.Error);

            // 原文件未被改动
            Assert.Equal(originalTarget, File.ReadAllBytes(Path.Combine(dstRoot, "f.bin")));
            // 新文件落在 f (1).bin
            var renamed = Path.Combine(dstRoot, "f (1).bin");
            Assert.True(File.Exists(renamed), "重命名策略应产生 f (1).bin");
            Assert.Equal(File.ReadAllBytes(Path.Combine(srcRoot, "f.bin")), File.ReadAllBytes(renamed));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_ConflictSkip_LeavesTargetUntouched()
    {
        var srcRoot = NewDir("cs-src");
        var dstRoot = NewDir("cs-dst");
        WriteFile(Path.Combine(srcRoot, "f.bin"), 400, seed: 5);
        WriteFile(Path.Combine(dstRoot, "f.bin"), 777, seed: 6);
        var originalTarget = File.ReadAllBytes(Path.Combine(dstRoot, "f.bin"));

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync("f.bin", "f.bin", FileConflictPolicy.Skip);

            // 跳过 = 成功但没有传输任何字节，目标保持原样。
            Assert.True(outcome.Ok, outcome.Error);
            Assert.Equal(0, outcome.BytesTransferred);
            Assert.Equal(originalTarget, File.ReadAllBytes(Path.Combine(dstRoot, "f.bin")));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_Resume_OnlyMissingChunksAreSent()
    {
        var srcRoot = NewDir("rs-src");
        var dstRoot = NewDir("rs-dst");
        const int size = 200_000;
        WriteFile(Path.Combine(srcRoot, "big.bin"), size, seed: 9);

        // 预置一个"已传了 4 块"的 .part（内容必须与源一致，模拟上次中断的连续前缀）。
        const int chunk = ChunkLayout.DefaultChunkSize;
        var target = Path.Combine(dstRoot, "big.bin");
        var part = target + ".part";
        Directory.CreateDirectory(dstRoot);
        var full = File.ReadAllBytes(Path.Combine(srcRoot, "big.bin"));
        File.WriteAllBytes(part, full.AsSpan(0, chunk * 4).ToArray());

        var (a, b, _, sinkB) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                          new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync("big.bin", "big.bin", FileConflictPolicy.Overwrite);
            Assert.True(outcome.Ok, outcome.Error);

            // 断点续传的核心断言：实际传输的块数据显著少于整文件。
            // 整文件 200000 字节；已确认 4 块（4*48KB=196608），只剩 3392 字节要传。
            Assert.True(sinkB.PayloadBytes < size / 2,
                $"应只传缺失块，实际传输 {sinkB.PayloadBytes} 字节（整文件 {size}）");

            Assert.Equal(full, File.ReadAllBytes(target));
            Assert.False(File.Exists(part));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_DigestMismatch_FailsAndLeavesNoTarget()
    {
        var srcRoot = NewDir("dm-src");
        var dstRoot = NewDir("dm-dst");
        WriteFile(Path.Combine(srcRoot, "f.bin"), 3000, seed: 13);

        // 篡改接收端磁盘上的 BLAKE3（模拟传输中被破坏）。
        var ioB = new CorruptingHashIo();
        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }), ioB: ioB);
        try
        {
            var outcome = await a.UploadAsync("f.bin", "f.bin", FileConflictPolicy.Overwrite);

            Assert.False(outcome.Ok);
            Assert.Contains("BLAKE3", outcome.Error!, StringComparison.Ordinal);

            // 目标文件绝不能出现（校验不通过就不改名）；.part 也应被清掉。
            var target = Path.Combine(dstRoot, "f.bin");
            Assert.False(File.Exists(target), "校验失败时不得产生目标文件");
            Assert.False(File.Exists(target + ".part"), "校验失败时应清理 .part");
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_DiskFull_FailsAndKeepsPartForResume()
    {
        var srcRoot = NewDir("df-src");
        var dstRoot = NewDir("df-dst");
        WriteFile(Path.Combine(srcRoot, "f.bin"), 100_000, seed: 17);

        // 接收端写到 20000 字节后就"磁盘满"。
        var ioB = new DiskFullIo(afterBytes: 20_000);
        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }), ioB: ioB);
        try
        {
            var outcome = await a.UploadAsync("f.bin", "f.bin", FileConflictPolicy.Overwrite);

            Assert.False(outcome.Ok);
            Assert.Contains("disk full", outcome.Error!, StringComparison.OrdinalIgnoreCase);

            var target = Path.Combine(dstRoot, "f.bin");
            Assert.False(File.Exists(target), "磁盘满时不得产生目标文件");
            // .part 必须保留（DESIGN：保留可恢复状态）
            Assert.True(File.Exists(target + ".part"), "磁盘满时应保留 .part 以便恢复");
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_ScopeDenied_OnReceiver()
    {
        var srcRoot = NewDir("sd-src");
        var dstRoot = NewDir("sd-dst");
        WriteFile(Path.Combine(srcRoot, "f.bin"), 500, seed: 19);

        // 接收端 scope 为空 → 必须拒绝（DESIGN：不提供默认全盘浏览）。
        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      FileTransferScope.Empty);
        try
        {
            var outcome = await a.UploadAsync("f.bin", "f.bin", FileConflictPolicy.Overwrite);
            Assert.False(outcome.Ok);
            Assert.Contains("scope denied", outcome.Error!, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(dstRoot));
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_UploadOutOfScopeSource_IsRejectedLocally()
    {
        var srcRoot = NewDir("os-src");
        var dstRoot = NewDir("os-dst");
        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var outcome = await a.UploadAsync(@"..\..\windows\win.ini", "x", FileConflictPolicy.Overwrite);
            Assert.False(outcome.Ok);
            Assert.Contains("不在授权范围内", outcome.Error!);
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_ListRemoteDirectory()
    {
        var ours = NewDir("ls-ours");
        var peer = NewDir("ls-peer");
        Directory.CreateDirectory(Path.Combine(peer, "sub"));
        File.WriteAllText(Path.Combine(peer, "a.txt"), "hello");
        WriteFile(Path.Combine(peer, "b.bin"), 100);

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { ours }),
                                      new FileTransferScope(new[] { peer }));
        try
        {
            var result = await a.ListAsync("");
            Assert.True(result.Ok, result.Error);
            Assert.Contains(result.Entries, e => e.Name == "a.txt" && !e.IsDirectory && e.Size == 5);
            Assert.Contains(result.Entries, e => e.Name == "b.bin" && !e.IsDirectory && e.Size == 100);
            Assert.Contains(result.Entries, e => e.Name == "sub" && e.IsDirectory);

            // 越界目录列举必须被拒
            var denied = await a.ListAsync(@"..\..");
            Assert.False(denied.Ok);
            Assert.NotNull(denied.Error);
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    [Fact]
    public async Task E2E_Cancel_SenderStopsAndReceiverDropsPart()
    {
        var srcRoot = NewDir("cx-src");
        var dstRoot = NewDir("cx-dst");
        // 32MB 不是随便挑的：这个用例要取消一条**正在进行**的传输，
        // 而传输得慢到"进行中"是个能被采样到的状态。原来的 5MB 在回环上常常一两拍就传完，
        // 于是取消打在一条已经结束的传输上 —— 传输确实成功了，用例随机红，且和被测代码无关。
        WriteFile(Path.Combine(srcRoot, "big.bin"), 32_000_000, seed: 23);

        var (a, b, _, _) = CreatePair(new FileTransferScope(new[] { srcRoot }),
                                      new FileTransferScope(new[] { dstRoot }));
        try
        {
            var upload = a.UploadAsync("big.bin", "big.bin", FileConflictPolicy.Overwrite);

            // 等 Snapshot 里出现一条**状态为 sending 的**发送传输再取消。
            // 只等 ActiveTransfers > 0 不够：那是"建过传输"，不保证此刻还活着。
            // FileTransferProgress 是值类型，不能用 FirstOrDefault + null 判断，只能手工扫。
            var sw = System.Diagnostics.Stopwatch.StartNew();
            uint liveId = 0;
            while (sw.ElapsedMilliseconds < 15_000)
            {
                foreach (var p in a.Snapshot())
                {
                    if (p.Direction == TransferDirection.Sending && p.State == "sending")
                    {
                        liveId = p.TransferId;
                        break;
                    }
                }
                if (liveId != 0) break;
                await Task.Delay(10);
            }
            Assert.True(liveId != 0, "应观察到一条进行中的发送传输");

            a.Cancel(liveId);

            var outcome = await upload.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.False(outcome.Ok);
            Assert.False(File.Exists(Path.Combine(dstRoot, "big.bin")), "取消后不得产生目标文件");
        }
        finally
        {
            await a.DisposeAsync();
            await b.DisposeAsync();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 注入的 IO 替身
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>把读到的文件内容哈希篡改一位（模拟传输后数据被破坏）。</summary>
    private sealed class CorruptingHashIo : IFileTransferIo
    {
        private readonly RealFileTransferIo _inner = RealFileTransferIo.Instance;

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public long GetFileLength(string path) => _inner.GetFileLength(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public IPartFileWriter CreatePart(string partPath) => _inner.CreatePart(partPath);
        public void MoveReplace(string source, string destination) => _inner.MoveReplace(source, destination);
        public void Delete(string path) => _inner.Delete(path);
        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);
        public IReadOnlyList<FileEntry> List(string directory) => _inner.List(directory);

        public byte[] HashFile(string path)
        {
            var h = _inner.HashFile(path);
            h[0] ^= 0xFF;
            return h;
        }
    }

    /// <summary>写到指定字节数之后抛"磁盘满"。</summary>
    private sealed class DiskFullIo : IFileTransferIo
    {
        private readonly RealFileTransferIo _inner = RealFileTransferIo.Instance;
        private readonly long _limit;

        public DiskFullIo(long afterBytes) => _limit = afterBytes;

        public bool FileExists(string path) => _inner.FileExists(path);
        public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
        public long GetFileLength(string path) => _inner.GetFileLength(path);
        public Stream OpenRead(string path) => _inner.OpenRead(path);
        public void MoveReplace(string source, string destination) => _inner.MoveReplace(source, destination);
        public void Delete(string path) => _inner.Delete(path);
        public void EnsureDirectory(string path) => _inner.EnsureDirectory(path);
        public IReadOnlyList<FileEntry> List(string directory) => _inner.List(directory);
        public byte[] HashFile(string path) => _inner.HashFile(path);

        public IPartFileWriter CreatePart(string partPath)
            => new LimitedWriter(_inner.CreatePart(partPath), _limit);

        private sealed class LimitedWriter : IPartFileWriter
        {
            private readonly IPartFileWriter _inner;
            private readonly long _limit;
            private long _written;

            public LimitedWriter(IPartFileWriter inner, long limit)
            {
                _inner = inner;
                _limit = limit;
            }

            public long Length => _inner.Length;

            public void WriteAt(long offset, ReadOnlySpan<byte> data)
            {
                if (_written + data.Length > _limit)
                {
                    throw new DiskFullException($"模拟磁盘满（上限 {_limit} 字节）");
                }
                _written += data.Length;
                _inner.WriteAt(offset, data);
            }

            public void Flush() => _inner.Flush();
            public void Dispose() => _inner.Dispose();
        }
    }
}
