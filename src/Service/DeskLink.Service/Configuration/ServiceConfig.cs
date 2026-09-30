// Service 侧的**持久化**配置。
//
// ## 为什么需要它
//
// 修复"`--file-scope` 不持久化"：这个参数（以及捕获显示器索引）原先只存在于进程的命令行里，
// 于是有两个真实问题：
//   1) 直接跑 `DeskLink.Service.exe --file-scope D:\share` 的人，下次不带这个参数重启，
//      授权目录就静默变成"一律拒绝"——文件功能无声失效，用户只会看到"传输被拒绝"。
//   2) WPF 客户端只能**读** file_scope（`get_config` 之外没有写路径），想改只能改命令行。
//
// ## 落盘位置与优先级
//
// 落盘到 `<data-dir>\service.json`（与 keystore / pairings 同目录）。
// 位置依赖 `--data-dir`，所以 CommandLineParser **两遍扫描**：先廉价取出 --data-dir，
// 读出本文件，再做完整解析。命令行参数**永远优先**于落盘值。
//
// ## 为什么不去覆盖命令行里已有的能力
//
// 中继地址 / 直连端口不在这里：它们有各自的"立即生效 vs 需重启"语义，
// 由 ServiceCore.SetConfig 就地处理，混进本文件会让"哪些是持久化配置"变得说不清。
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink.Service.Configuration;

/// <summary>需要跨重启保留的配置（当前：文件授权目录 + 捕获显示器索引）。</summary>
public sealed class ServiceConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>文件传输授权根目录（空列表 = 一律拒绝，DESIGN 不提供默认全盘浏览）。</summary>
    public List<string> FileScopeRoots { get; set; } = new();

    /// <summary>要捕获的显示器索引（0 = 主显示器；对应 Agent 的 --monitor）。</summary>
    public int CaptureMonitorIndex { get; set; }

    public static string PathFor(string dataDir) =>
        System.IO.Path.Combine(dataDir, "service.json");

    /// <summary>
    /// 读取持久化配置。文件不存在 / 损坏 / 不可读时返回 null（调用方按"没有默认值"处理），
    /// 绝不让一个坏掉的 service.json 阻止 Service 启动。
    /// </summary>
    public static ServiceConfig? TryLoad(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var cfg = JsonSerializer.Deserialize<ServiceConfig>(json, Options);
            if (cfg is null) return null;

            // 归一化：路径取绝对路径并去重，与命令行分支保持同一套规则。
            cfg.FileScopeRoots = cfg.FileScopeRoots
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => Path.GetFullPath(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (cfg.CaptureMonitorIndex < 0) cfg.CaptureMonitorIndex = 0;
            return cfg;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>写回。失败返回 false（调用方据此提示"本次运行仍按内存里的值执行"）。</summary>
    public bool Save(string dataDir)
    {
        try
        {
            var path = PathFor(dataDir);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
