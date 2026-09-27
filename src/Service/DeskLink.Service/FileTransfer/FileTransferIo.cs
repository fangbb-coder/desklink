// 文件传输的磁盘 IO 抽象（P6）。
//
// 为什么抽一层：DESIGN 要求覆盖"磁盘空间不足"用例，而真实把磁盘写满在单测里
// 不可行（也不该）。把"写 .part / 改名前校验 / 列目录"抽成接口后，单测可以注入
// 一个"写到第 N 字节就抛磁盘满"的实现，从而验证：
//   - 引擎把 IOException 归类为 DiskFull 并回传 FileStatus；
//   - 已写内容保留在 .part（可恢复），**绝不**产生半截的目标文件。
//
// 生产实现 RealFileTransferIo 用 FileStream(RandomAccess) + File.Move(overwrite)。
using System.Security.Cryptography;
using DeskLink.Protocol.Hash;
using DeskLink.Service.FileTransfer;

// 包根命名空间就叫 "Blake3"（Blake3.Managed），直接用 `Blake3.Xxx` 会解析到那个
// 命名空间而不是我们的静态类（CS0234）。与 E2ESessionHost 用同样的别名处理。
using ProtoBlake3 = DeskLink.Protocol.Hash.Blake3;

namespace DeskLink.Service.FileTransfer;

/// <summary>可随机写的 `.part` 写入器。</summary>
public interface IPartFileWriter : IDisposable
{
    long Length { get; }

    /// <summary>在指定偏移写入（支持断点续传的乱序块）。</summary>
    void WriteAt(long offset, ReadOnlySpan<byte> data);

    void Flush();
}

/// <summary>磁盘满（模拟或真实）。</summary>
public sealed class DiskFullException : IOException
{
    public DiskFullException(string message) : base(message) { }
}

/// <summary>文件传输用到的磁盘操作集合。</summary>
public interface IFileTransferIo
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    long GetFileLength(string path);
    Stream OpenRead(string path);
    IPartFileWriter CreatePart(string partPath);
    void MoveReplace(string source, string destination);
    void Delete(string path);
    void EnsureDirectory(string path);
    IReadOnlyList<FileEntry> List(string directory);
    byte[] HashFile(string path);
}

/// <summary>真实文件系统实现。</summary>
public sealed class RealFileTransferIo : IFileTransferIo
{
    public static RealFileTransferIo Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public long GetFileLength(string path) => new FileInfo(path).Length;

    public Stream OpenRead(string path)
        => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    public IPartFileWriter CreatePart(string partPath)
    {
        EnsureDirectory(Path.GetDirectoryName(partPath) ?? ".");
        var fs = new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            64 * 1024, FileOptions.RandomAccess);
        return new RealPartFileWriter(fs);
    }

    public void MoveReplace(string source, string destination)
    {
        EnsureDirectory(Path.GetDirectoryName(destination) ?? ".");
        // File.Move(overwrite: true) 在 Windows 上走 MoveFileEx(MOVEFILE_REPLACE_EXISTING)，
        // 同卷内是原子的：校验通过后要么是旧文件、要么是完整新文件，不会出现半截文件。
        File.Move(source, destination, overwrite: true);
    }

    public void Delete(string path)
    {
        try { File.Delete(path); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }

    public void EnsureDirectory(string path)
    {
        if (!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path);
    }

    public IReadOnlyList<FileEntry> List(string directory)
    {
        var list = new List<FileEntry>();
        var di = new DirectoryInfo(directory);
        foreach (var d in di.EnumerateDirectories())
        {
            list.Add(new FileEntry(d.Name, true, 0, new DateTimeOffset(d.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        foreach (var f in di.EnumerateFiles())
        {
            list.Add(new FileEntry(f.Name, false, f.Length, new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeMilliseconds()));
        }
        // 稳定排序（目录在前，再按名字），便于断言与 UI 展示。
        list.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    public byte[] HashFile(string path) => ProtoBlake3.HashFile(path);

    private sealed class RealPartFileWriter : IPartFileWriter
    {
        private readonly FileStream _fs;
        private bool _disposed;

        public RealPartFileWriter(FileStream fs) => _fs = fs;

        public long Length => _fs.Length;

        public void WriteAt(long offset, ReadOnlySpan<byte> data)
        {
            try
            {
                _fs.Seek(offset, SeekOrigin.Begin);
                _fs.Write(data);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new DiskFullException($"写入 .part 失败（磁盘空间不足）：{ex.Message}");
            }
        }

        public void Flush() => _fs.Flush(flushToDisk: false);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _fs.Flush(flushToDisk: true); } catch { /* best-effort */ }
            _fs.Dispose();
        }

        /// <summary>
        /// 识别"磁盘空间不足"。Windows 上 HResult 0x80070070 = ERROR_DISK_FULL，
        /// 0x80070027 = ERROR_HANDLE_DISK_FULL；Linux 上 mono/dotnet 用 ENOSPC(28)。
        /// </summary>
        internal static bool IsDiskFull(IOException ex)
        {
            const int ErrorDiskFull = unchecked((int)0x80070070);
            const int ErrorHandleDiskFull = unchecked((int)0x80070027);
            const int Enospc = 28;
            return ex.HResult is ErrorDiskFull or ErrorHandleDiskFull or Enospc;
        }
    }
}
