// DeskLink.Panel —— 面板自身的持久化设置
//
// 为什么单独存一份，而不是直接改 Service 的配置：
//   Service 的权威配置在 data-dir 下的 keystore/pairings + 启动参数里，面板不持有它；
//   面板只存"下次打开时我上次选了什么"（数据目录、端口、开关、共享目录、角色）。
//   这样面板与 Service 保持单向：面板读状态靠 RPC，改状态靠改启动参数，不侵入 Service 的配置面。
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink.Panel;

/// <summary>本机在这套远控里的角色。决定界面重点与「一键准备」的具体动作。</summary>
public enum PanelRole
{
    /// <summary>被控端：屏幕和键鼠给别人看/控。需要开放入站 + 注入桌面代理。</summary>
    Controlled,

    /// <summary>控制端：我去看/控别人。需要能主动拨出，不需要入站规则。</summary>
    Controller,
}

/// <summary>面板设置。落盘到 %APPDATA%\DeskLink\panel.json（按用户，不污染 ProgramData）。</summary>
public sealed class PanelSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Service 的数据目录。实例名 = 该路径末段（小写），与 Service 自己的推导规则一致。</summary>
    public string DataDir { get; set; } = DefaultDataDir();

    public int DirectPort { get; set; } = 47200;

    /// <summary>是否 <c>--enable-direct</c>：被控端必须开，控制端不需要（它只拨出）。</summary>
    public bool EnableDirect { get; set; }

    /// <summary>是否 <c>--inject-agent</c>：接管本机桌面（截图 + 键鼠注入）。仅被控端需要。</summary>
    public bool InjectAgent { get; set; }

    /// <summary><c>--file-scope</c>：仅这些目录可被远端读取/写入。空列表 = 全盘可读（危险）。</summary>
    public List<string> FileScopeRoots { get; set; } = new();

    /// <summary>
    /// <c>--monitor</c>：捕获哪块显示器。0 = 主显示器（默认值，也是 Service 的默认行为）。
    /// 多屏用户把它设成 1 / 2… 才有意义。**只对被控端生效**——主控端不产生画面。
    /// 面板以前完全没有这个入口，用户只能去命令行里加，是"多显示器没法用"的根因。
    /// </summary>
    public int MonitorIndex { get; set; }

    public string? RelayUrl { get; set; }

    public bool InsecureRelayTls { get; set; }

    public PanelRole Role { get; set; } = PanelRole.Controlled;

    /// <summary>手动指定 Service 可执行文件位置；为空时按 <see cref="ServiceCli.ResolveServiceExe"/> 探测。</summary>
    public string? ServiceExePath { get; set; }

    /// <summary>手动指定 WPF 客户端位置；为空时按同目录规则探测。</summary>
    public string? ClientExePath { get; set; }

    /// <summary>与 <see cref="CommandLineParser.DeriveInstanceId"/> 保持一致的实例名推导（末段小写）。</summary>
    public string InstanceId
    {
        get
        {
            var trimmed = DataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) name = "default";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            return name.ToLowerInvariant();
        }
    }

    /// <summary>Service 的客户端 RPC 管道名。</summary>
    public string ClientPipeName => $"DeskLink.Client.{InstanceId}";

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskLink", "panel.json");

    public static string DefaultDataDir()
    {
        var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(pd, "DeskLink");
    }

    public PanelSettings Clone() => new()
    {
        DataDir = DataDir,
        DirectPort = DirectPort,
        EnableDirect = EnableDirect,
        InjectAgent = InjectAgent,
        FileScopeRoots = new List<string>(FileScopeRoots),
        MonitorIndex = MonitorIndex,
        RelayUrl = RelayUrl,
        InsecureRelayTls = InsecureRelayTls,
        Role = Role,
        ServiceExePath = ServiceExePath,
        ClientExePath = ClientExePath,
    };

    /// <summary>读取设置；文件不存在或损坏时返回默认值（面板必须永远能打开）。</summary>
    public static PanelSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            if (!File.Exists(path)) return new PanelSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PanelSettings>(json, JsonOptions) ?? new PanelSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 面板不该因为配置文件坏了就打不开——退回默认值，用户重新设一次即可。
            return new PanelSettings();
        }
    }

    /// <summary>写回设置。失败不抛：设置存不上不该阻断用户在界面上的其它操作。</summary>
    public bool Save(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>按角色套用推荐开关（"一键准备"用）。返回是否有改动。</summary>
    public bool ApplyRoleDefaults(PanelRole role)
    {
        Role = role;
        // 被控端：开直连入站 + 注入代理；控制端：两者都不需要（只拨出、不控本机桌面）。
        bool direct = role == PanelRole.Controlled;
        bool inject = role == PanelRole.Controlled;
        bool changed = EnableDirect != direct || InjectAgent != inject;
        EnableDirect = direct;
        InjectAgent = inject;
        return changed;
    }
}
