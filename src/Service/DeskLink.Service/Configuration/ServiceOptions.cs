// DeskLink.Service 配置项。
//
// 来源优先级（高 → 低）：
//   1. 命令行参数（开发自控自、调试、装机后修复入口）
//   2. 注册表 HKLM\SOFTWARE\DeskLink\Service（装机写入；install.ps1 在 P10 接入）
//   3. 内置默认值
//
// 注意：
//   - 设计规格要求 命名管道只走 RPC 元数据，绝不过编码流；本类只描述元数据。
//   - 开发自控自走 --console + --data-dir 双实例模式（DESIGN.md P4 验收）。
//   - --no-inject 由 AgentLauncher 在调用进程处强制，不作为本进程启动参数；
//     这样设计避免把"用户明知要自注入"的开关埋进全局配置。
using System.Net;

namespace DeskLink.Service.Configuration;

public sealed class ServiceOptions
{
    /// <summary>
    /// 数据目录。存放：keystore.json（DPAPI 包裹）、pairings.json、状态文件。
    /// 默认：<c>%ProgramData%\DeskLink</c>；console 模式或 --data-dir 时覆盖。
    /// </summary>
    public string DataDir { get; set; } = DefaultDataDir();

    /// <summary>
    /// 命名管道名前缀。完整管道名 = <c>{PipeNamePrefix}.Client.{InstanceId}</c> /
    /// <c>{PipeNamePrefix}.Agent.{InstanceId}</c>。双实例通过 InstanceId 区分。
    /// </summary>
    public string PipeNamePrefix { get; set; } = "DeskLink";

    /// <summary>
    /// 中继入口（DESIGN.md 中继路径）。默认空，由用户在 Settings 写入。
    ///
    /// scheme 决定传输（与 relayd 双监听对齐）：
    ///   quic://  强制 QUIC（不可用时报错，不静默回落）
    ///   https:// QUIC 优先，不可用回落 TCP/TLS
    ///   tls://   强制 TCP/TLS
    /// 端口号必须显式写出（不允许依赖默认端口）。
    /// </summary>
    public Uri? RelayUrl { get; set; }

    /// <summary>
    /// 关闭中继 TLS 的 TOFU 指纹校验（接受任何证书）。
    ///
    /// **默认 false（开启 TOFU）**。仅在以下场景显式打开：
    ///   - 本机联调：relayd 用 --insecure-tls 自签证书且每次重启都换证书；
    ///   - 首次接入一台尚未确认指纹的 relay 且需要临时连通。
    /// 生产环境不应开启——那等于放弃中继链路的防中间人能力。
    /// </summary>
    public bool InsecureRelayTls { get; set; }

    /// <summary>
    /// 局域网直连监听端口（DESIGN.md P5.5 / DESIGN 偏差项 #10）。
    /// 被控端 DirectServer 在该端口上以服务端身份监听入站（QUIC+TCP/TLS）。
    /// 默认 47200，可在 Settings 中改；端口变更时由 FirewallHelper 同步规则（P5.6）。
    /// </summary>
    public int DirectPort { get; set; } = 47200;

    /// <summary>
    /// 是否启动局域网直连监听（被控端角色）。
    ///
    /// 默认 false：直连需要放行入站端口（防火墙规则），仅在用户于安装/Settings 明确
    /// 同意后才应开启（DESIGN.md 部署章节）。运行时以 `--enable-direct` 或注册表
    /// HKLM\SOFTWARE\DeskLink\Firewall\DirectEnabled 为准（见 ServiceHost）。
    /// </summary>
    public bool EnableDirect { get; set; }

    /// <summary>
    /// 文件传输授权目录（scope）。**默认为空 = 一律拒绝**。
    ///
    /// DESIGN.md 文件安全："被控端仅在用户选择的文件或目录范围内提供上传与下载，
    /// 不提供默认全盘浏览。" 因此绝不能给一个默认根目录——那等于把整块盘暴露出去。
    /// 由 `--file-scope &lt;dir&gt;`（可重复）或 Settings 显式添加。
    /// </summary>
    public List<string> FileScopeRoots { get; set; } = new();

    // —— 桌面代理的采集参数（P9/P8 收尾）——
    //
    // 这些值由 Service 通过命令行传给代理（代理不读 Service 的配置，避免两处配置漂移）。

    /// <summary>要捕获的显示器序号（0 = 第一块）。</summary>
    public int CaptureMonitorIndex { get; set; }

    /// <summary>显示器旋转角度（0/90/180/270）。</summary>
    public int CaptureRotation { get; set; }

    /// <summary>目标帧率上限。</summary>
    public int CaptureFps { get; set; } = 30;

    /// <summary>目标码率上限（自适应码率在此基础上降档）。</summary>
    public int CaptureBitrateBps { get; set; } = 8_000_000;

    /// <summary>
    /// 重连退避上限（毫秒）。指数退避从 1s 起步，单调递增到本值。
    /// </summary>
    public int BackoffMaxMs { get; set; } = 30_000;

    /// <summary>
    /// 重连退避起始（毫秒）。
    /// </summary>
    public int BackoffBaseMs { get; set; } = 1_000;

    /// <summary>
    /// console 模式（开发自控自 / 联调）。Windows 服务模式不传。
    /// </summary>
    public bool ConsoleMode { get; set; }

    /// <summary>
    /// 启用后由 Service 启动 DeskLink.DesktopAgent。
    /// P4 仅保留入口（DESIGN.md 风险回顾 #4），真实注入路径在 P7 接入。
    /// </summary>
    public bool InjectAgent { get; set; }

    /// <summary>
    /// 打印解析后的配置（含密钥文件路径）后退出 0；用于装机后首次启动指引。
    /// </summary>
    public bool PrintConfig { get; set; }

    /// <summary>
    /// 打印防火墙/直连状态 JSON 后退出 0。
    ///
    /// 用途：给冒烟脚本与运维提供"不用起服务、不用命名管道客户端"就能读状态的入口
    /// （HKLM\SOFTWARE\DeskLink\Firewall 的 DirectEnabled / DirectPort）。
    /// 只读，不需要管理员权限。
    /// </summary>
    public bool PrintFirewallStatus { get; set; }

    /// <summary>
    /// 实例标识（由 --data-dir 派生）。用于多实例隔离管道名与数据目录。
    /// </summary>
    public string InstanceId { get; set; } = "default";

    /// <summary>
    /// 运维/联调入口：把给定 Ed25519 公钥（hex 或 base64）加入本机配对列表后退出 0。
    /// 用于脚本化冒烟（避免为了造配对关系去实现一个管道客户端）与现场修复。
    /// </summary>
    public string? PairPeerPub { get; set; }

    /// <summary>
    /// 运维/联调入口：作为控制端对 <c>host:port</c> 发起一次局域网直连探测，
    /// 打印 JSON 结果后退出（成功 0 / 失败 1）。用于 P5.5 端到端冒烟与现场排障。
    /// </summary>
    public string? DirectProbe { get; set; }

    /// <summary>
    /// 安装/运维入口：放行或回收入站防火墙规则后退出（成功 0 / 失败 1）。
    ///
    /// 为什么由 Service CLI 承担而不是让 install.ps1 直接调 netsh：
    /// 防火墙逻辑（规则命名、幂等、端口变更、注册表状态）已经在 FirewallHelper 里
    /// 被 13 个单测覆盖；脚本再实现一遍就会产生第二份真相，且容易与 C# 侧不一致。
    /// 脚本只负责"以管理员身份调用本入口"。
    /// 形如 <c>--firewall-set 47200 on|off</c>。
    /// </summary>
    public (int Port, bool Enable)? FirewallSet { get; set; }

    /// <summary>
    /// 安装/运维入口：把防火墙规则重新对齐到注册表记录的启用状态（修复入口）后退出。
    /// 形如 <c>--firewall-repair 47200</c>。
    /// </summary>
    public int? FirewallRepair { get; set; }

    /// <summary>
    /// 期望连接的对端 device_id（hex 或 base64）。
    ///
    /// 用途：中继路径下 relay 只负责"按 device_id 接线"，谁跟谁配对由上层决定。
    /// 显式给出对端可让双实例联调（P4/P5 验收）无需依赖 registry 的配对码流程：
    ///   DeskLink.Service --console --data-dir A --relay-url tcp://127.0.0.1:9443 --peer &lt;B 的 device_id&gt;
    ///
    /// 为 null 时回退到 pairings.json：若恰好只有一个已配对设备，则用它。
    /// </summary>
    public string? PeerDeviceId { get; set; }

    /// <summary>
    /// 客户端管道名（DeskLink.exe 接入）。
    /// </summary>
    public string ClientPipeName => $"{PipeNamePrefix}.Client.{InstanceId}";

    /// <summary>
    /// Agent 管道名（DeskLink.DesktopAgent 接入）。
    /// </summary>
    public string AgentPipeName => $"{PipeNamePrefix}.Agent.{InstanceId}";

    /// <summary>
    /// 媒体通道（客户端侧）：WPF 客户端连这条，收画面、发输入。
    /// 命名与客户端 <c>ClientOptions.DefaultMediaPipeName</c> 保持一致。
    /// </summary>
    public string ClientMediaPipeName => $"{PipeNamePrefix}.Media.{InstanceId}";

    /// <summary>媒体通道（代理侧）：桌面代理连这条，发画面、收输入。</summary>
    public string AgentMediaPipeName => $"{PipeNamePrefix}.AgentMedia.{InstanceId}";

    public string KeyStorePath => Path.Combine(DataDir, "keystore.json");

    public string PairingsPath => Path.Combine(DataDir, "pairings.json");

    public string LogPath => Path.Combine(DataDir, "service.log");

    public static string DefaultDataDir()
    {
        if (OperatingSystem.IsWindows())
        {
            var pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(pd, "DeskLink");
        }
        // 非 Windows 仅用于跨平台测试
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".desklink");
    }

    public override string ToString()
    {
        var relay = RelayUrl?.ToString() ?? "(unset)";
        return $"ServiceOptions {{ DataDir={DataDir}, InstanceId={InstanceId}, " +
               $"Console={ConsoleMode}, InjectAgent={InjectAgent}, " +
               $"RelayUrl={relay}, DirectPort={DirectPort}, EnableDirect={EnableDirect}, " +
               $"FileScopeRoots=[{string.Join(";", FileScopeRoots)}], " +
               $"PeerDeviceId={PeerDeviceId ?? "(auto)"}, " +
               $"PipeName={PipeNamePrefix}.{{Client|Agent|Media|AgentMedia}}.{InstanceId} }}";
    }
}
