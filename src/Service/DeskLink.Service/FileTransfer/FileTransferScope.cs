// 文件传输授权范围（scope）。
//
// DESIGN.md 文件安全：「文件传输必须由会话内显式操作触发。被控端仅在用户选择的
// 文件或目录范围内提供上传与下载，不提供默认全盘浏览。scope 之外的访问请求一律拒绝。」
//
// 本类只做一件事：把"对端给的相对路径"解析成"本机绝对路径"，并在**任何**
// 越界情形下抛 FileScopeException。所有越界判定都必须走这里，禁止在别处
// 用字符串拼接绕过——那正是路径穿越漏洞的来源。
//
// 被拒绝的情形（每条都有对应单测）：
//   1. 绝对路径（`C:\Windows\x`、`/etc/passwd`、`\\server\share`）
//   2. 设备/扩展路径（`\\?\C:\...`、`\\.\PhysicalDrive0`）
//   3. 目录穿越（`..\..\Windows`）——由 GetFullPath 归一化后再判包含关系
//   4. 备用数据流 / 盘符（路径里出现 `:`）
//   5. 归一化后不在任何授权根之下（含"根自身前缀但非路径边界"的经典绕过：
//      root=`C:\data` 时 `C:\database` 必须被拒）
using System.Text;

namespace DeskLink.Service.FileTransfer;

/// <summary>scope 越界。</summary>
public sealed class FileScopeException : Exception
{
    public FileScopeException(string message) : base(message) { }
}

/// <summary>文件传输授权范围（一个或多个根目录）。</summary>
public sealed class FileTransferScope
{
    private readonly List<string> _roots = new();

    public FileTransferScope(IEnumerable<string> roots)
    {
        if (roots is null) throw new ArgumentNullException(nameof(roots));
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            var full = System.IO.Path.GetFullPath(r);
            full = System.IO.Path.TrimEndingDirectorySeparator(full);
            if (!_roots.Contains(full, PathComparer)) _roots.Add(full);
        }
    }

    /// <summary>无授权目录：任何访问都会被拒（DESIGN：不提供默认全盘浏览）。</summary>
    public static FileTransferScope Empty { get; } = new(Array.Empty<string>());

    public IReadOnlyList<string> Roots => _roots;

    public bool IsEmpty => _roots.Count == 0;

    /// <summary>Windows 上路径大小写不敏感；其它平台保持敏感。</summary>
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// 把相对路径解析为绝对路径；越界抛 <see cref="FileScopeException"/>。
    /// 空串 / "." 表示"授权根本身"（仅用于列目录）。
    /// </summary>
    public string Resolve(string? relativePath)
    {
        var rel = Normalize(relativePath);

        if (Path.IsPathRooted(rel))
        {
            throw new FileScopeException($"拒绝绝对路径：{relativePath}");
        }
        // 盘符 / 备用数据流（`file.txt:stream`）/ 协议前缀都会带 `:`。
        if (rel.Contains(':'))
        {
            throw new FileScopeException($"路径中不允许出现 ':'：{relativePath}");
        }
        // `\\?\` / `\\.\` 设备路径。
        if (rel.StartsWith(@"\\", StringComparison.Ordinal) ||
            rel.StartsWith("//", StringComparison.Ordinal))
        {
            throw new FileScopeException($"拒绝设备/UNC 路径：{relativePath}");
        }

        foreach (var root in _roots)
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(root, rel));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new FileScopeException($"非法路径：{relativePath}（{ex.GetType().Name}）");
            }

            if (IsWithinRoot(root, candidate))
            {
                return candidate;
            }
        }

        throw new FileScopeException(
            $"路径不在授权范围内：{relativePath}（授权根：{string.Join(", ", _roots)}）");
    }

    /// <summary>尝试解析，不抛异常。</summary>
    public bool TryResolve(string? relativePath, out string absolute, out string? error)
    {
        try
        {
            absolute = Resolve(relativePath);
            error = null;
            return true;
        }
        catch (FileScopeException ex)
        {
            absolute = "";
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 计算相对路径（用于向对端展示授权范围内的相对路径，绝不泄露授权根以外的信息）。
    /// 不在范围内返回 null。
    /// </summary>
    public string? ToRelative(string absolutePath)
    {
        var full = Path.GetFullPath(absolutePath);
        foreach (var root in _roots)
        {
            if (!IsWithinRoot(root, full)) continue;
            if (full.Equals(root, PathComparison)) return "";
            var rel = full[(root.Length + 1)..];
            return rel.Replace(Path.DirectorySeparatorChar, '/');
        }
        return null;
    }

    /// <summary>
    /// 判断 fullPath 是否位于 root 之下（含 root 自身）。
    ///
    /// 注意必须比 "root + 分隔符" 而不是单纯 StartsWith(root)：
    /// 否则 root=`C:\data` 会把 `C:\database` 误判为在范围内。
    /// </summary>
    public static bool IsWithinRoot(string root, string fullPath)
    {
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var f = Path.GetFullPath(fullPath);
        if (f.Equals(r, PathComparison)) return true;
        var prefix = r + Path.DirectorySeparatorChar;
        return f.StartsWith(prefix, PathComparison);
    }

    private static string Normalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return ".";
        // 统一分隔符：对端可能用 '/'（协议约定用 '/'）。
        var s = relativePath.Replace('/', Path.DirectorySeparatorChar)
                           .Replace('\\', Path.DirectorySeparatorChar)
                           .Trim();
        // 去掉尾部分隔符（但保留根表达 "."）
        while (s.Length > 1 && s.EndsWith(Path.DirectorySeparatorChar))
        {
            s = s[..^1];
        }
        return s.Length == 0 ? "." : s;
    }

    public override string ToString()
    {
        var sb = new StringBuilder("FileTransferScope[");
        sb.Append(string.Join(", ", _roots));
        sb.Append(']');
        return sb.ToString();
    }
}
