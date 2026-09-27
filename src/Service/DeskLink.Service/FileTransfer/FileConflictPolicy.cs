// 文件冲突策略（P6，DESIGN.md 文件安全 / 偏差项 #9）。
//
//   覆盖：以新文件替换目标，跳过备份。
//   重命名：自动在文件名后追加 `(n)` 后缀，不覆盖已有文件。
//   跳过：保留已存在目标，仅跳过当前文件。
//
// 策略由用户在文件传输页选择；不同文件可用不同策略（因此策略是每次传输的参数，
// 不是全局配置）。

namespace DeskLink.Service.FileTransfer;

/// <summary>文件冲突处理策略（字节值与协议一致）。</summary>
public enum FileConflictPolicy : byte
{
    /// <summary>覆盖（0x01）。</summary>
    Overwrite = 0x01,

    /// <summary>重命名（0x02）。</summary>
    Rename = 0x02,

    /// <summary>跳过（0x03）。</summary>
    Skip = 0x03,
}

/// <summary>冲突消解结果。</summary>
public readonly record struct ConflictResolution(string? TargetPath, bool Skipped, string? Reason)
{
    public static ConflictResolution Use(string path) => new(path, false, null);
    public static ConflictResolution SkipIt(string reason) => new(null, true, reason);
}

/// <summary>按策略计算最终落盘路径。</summary>
public static class FileConflictResolver
{
    /// <summary>重命名后缀最多尝试多少次（防止病态目录下无限循环）。</summary>
    public const int MaxRenameAttempts = 10_000;

    /// <summary>
    /// 解析最终目标路径。返回 <c>Skipped=true</c> 表示按策略应跳过本次传输。
    /// </summary>
    /// <param name="desiredPath">期望的绝对目标路径。</param>
    /// <param name="policy">用户选择的冲突策略。</param>
    /// <param name="exists">存在性判定（注入以便测试）。</param>
    public static ConflictResolution Resolve(
        string desiredPath,
        FileConflictPolicy policy,
        Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(desiredPath);
        ArgumentNullException.ThrowIfNull(exists);

        switch (policy)
        {
            case FileConflictPolicy.Overwrite:
                // 覆盖：直接用目标路径（存在也照样替换）。
                return ConflictResolution.Use(desiredPath);

            case FileConflictPolicy.Skip:
                return exists(desiredPath)
                    ? ConflictResolution.SkipIt("目标已存在，按策略跳过")
                    : ConflictResolution.Use(desiredPath);

            case FileConflictPolicy.Rename:
                if (!exists(desiredPath)) return ConflictResolution.Use(desiredPath);
                return ConflictResolution.Use(NextRenamePath(desiredPath, exists));

            default:
                throw new ArgumentOutOfRangeException(nameof(policy), $"未知冲突策略：{(byte)policy}");
        }
    }

    /// <summary>
    /// 生成 `name (1).ext`、`name (2).ext` … 直到不冲突。
    /// 目录不存在时按原样返回（上层会先建目录）。
    /// </summary>
    public static string NextRenamePath(string desiredPath, Func<string, bool> exists)
    {
        var dir = Path.GetDirectoryName(desiredPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        for (var n = 1; n <= MaxRenameAttempts; n++)
        {
            var candidate = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException($"重命名冲突无法消解（尝试 {MaxRenameAttempts} 次）：{desiredPath}");
    }
}
