// 命名管道 RPC 命令契约（共享契约：Service 实现，Client / DesktopAgent 消费）。
//
// 位置说明：本文件位于 DeskLink.Protocol 而不是 DeskLink.Service —— 契约是两端共用的
// "协议"，放在 Protocol 里可以让 WPF 客户端只引用 Protocol，而不必依赖整个服务程序集
// （否则客户端会连带拉入 Windows 服务宿主 / 注册表 / DPAPI 等与 UI 无关的依赖，
//   也让 DTO 有被复制一份从而漂移的风险）。
//
// 设计约束（DESIGN.md / 私有工作约定）：
//   - 命名管道只走 RPC 元数据，绝不过编码流。
//     含义：本文件定义的所有请求/响应加起来都是结构化的小对象（KB 量级），
//     H.264 码流 / NV12 / 文件数据一律走中继或局域网直连路径，不进管道。
//   - 帧格式：[u32 length BE][utf-8 JSON]。
//   - 调用方：DeskLink.exe（WPF 客户端）与 DeskLink.DesktopAgent（桌面代理）。
//   - 服务端由 PipeServer 监听两套管道；本契约共享给两个端点。
//
// 协议采用 JSON-RPC 2.0 简化版：
//   request  = { "method": string, "params": object?, "id": string }
//   response = { "result": object?, "error": { "code": int, "message": string }?, "id": string }
//   错误码：
//     -32601 method not found
//     -32602 invalid params
//     -32603 internal error
//     -32010 permission denied（调用方进程未授权）

using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink.Protocol.Pipe;

public sealed class PipeRequest
{
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("params")]
    public JsonElement? Params { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public sealed class PipeResponse
{
    [JsonPropertyName("result")]
    public JsonElement? Result { get; set; }

    [JsonPropertyName("error")]
    public PipeError? Error { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public sealed class PipeError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

public static class PipeErrorCode
{
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int PermissionDenied = -32010;
    public const int NotReady = -32020;
}

// —— 参数与返回类型（统一命名，方便调用方对齐）——

public sealed class DeviceInfoResult
{
    [JsonPropertyName("ed25519_pub_b64")]
    public string Ed25519PubB64 { get; set; } = "";

    [JsonPropertyName("x25519_pub_b64")]
    public string X25519PubB64 { get; set; } = "";

    [JsonPropertyName("device_id_hint_b64")]
    public string DeviceIdHintB64 { get; set; } = "";

    [JsonPropertyName("created_utc")]
    public DateTime CreatedUtc { get; set; }
}

public sealed class SignChallengeParams
{
    [JsonPropertyName("challenge_b64")]
    public string ChallengeB64 { get; set; } = "";
}

public sealed class SignChallengeResult
{
    [JsonPropertyName("signature_b64")]
    public string SignatureB64 { get; set; } = "";
}

public sealed class StatusResult
{
    [JsonPropertyName("relay_state")]
    public string RelayState { get; set; } = "disconnected";

    /// <summary>
    /// **语义警告（2026-09-29 补）**：这个名字极具误导性——它**不是**"DirectServer 正在监听"。
    /// 它取自 <c>FirewallHelper.QueryEnabled(port)</c>，含义是"该端口的防火墙/注册表放行状态"。
    ///
    /// 实测：带 <c>--enable-direct</c> 启动、日志明确打出
    /// <c>DirectServer enabled on port 47211</c>，本字段仍可能是 <c>false</c>（因为防火墙没放行）。
    /// 消费方（UI）**不要**把它渲染成"直连已开启/已关闭"——那会在 DirectServer 明明在监听时
    /// 显示"已关闭"，正是本项目刚修掉的那类"UI 撒谎"缺陷。
    ///
    /// 判断"直连是否真在监听"请看 Service 日志的 <c>DirectServer enabled on port N</c>，
    /// 或看直连会话数 <see cref="DirectActiveSessions"/>。
    /// </summary>
    [JsonPropertyName("direct_enabled")]
    public bool DirectEnabled { get; set; }

    [JsonPropertyName("pairing_count")]
    public int PairingCount { get; set; }
    [JsonPropertyName("instance_id")]
    public string InstanceId { get; set; } = "";

    // —— 媒体通道状态（P8/P9 收尾）——
    // 客户端用它判断"远程页能不能拿到真实画面"：两个都为 true 才可能有画面。
    [JsonPropertyName("media_client_connected")]
    public bool MediaClientConnected { get; set; }

    [JsonPropertyName("media_agent_connected")]
    public bool MediaAgentConnected { get; set; }

    [JsonPropertyName("media_frames_to_client")]
    public long MediaFramesToClient { get; set; }

    [JsonPropertyName("media_dropped_to_client")]
    public long MediaDroppedToClient { get; set; }

    [JsonPropertyName("media_client_queue_depth")]
    public int MediaClientQueueDepth { get; set; }

    [JsonPropertyName("console_mode")]
    public bool ConsoleMode { get; set; }

    /// <summary>E2E 会话阶段：idle / handshaking / sigma / established / closed / error:*。</summary>
    [JsonPropertyName("e2e_state")]
    public string E2EState { get; set; } = "idle";

    /// <summary>当前 E2E 对端 device_id（hex）；未建立时为 null。</summary>
    [JsonPropertyName("e2e_peer_device_id")]
    public string? E2EPeerDeviceId { get; set; }

    /// <summary>是否已完成一次控制流往返（SessionControl probe → ack），P5 验收标志。</summary>
    [JsonPropertyName("e2e_control_round_trip_ok")]
    public bool E2EControlRoundTripOk { get; set; }

    /// <summary>最近一次 Ping 往返时延（毫秒）；无样本为 -1。</summary>
    [JsonPropertyName("e2e_rtt_ms")]
    public long E2ERttMs { get; set; } = -1;

    /// <summary>局域网直连当前活跃会话数（被控端状态条判定依据之一）。</summary>
    [JsonPropertyName("direct_active_sessions")]
    public int DirectActiveSessions { get; set; }
}

/// <summary>
/// <c>end_session</c> 结果：被控端"随时断开"（DESIGN 使用流程第 5 条）。
/// 关闭本机全部 E2E 会话（中继 + 直连），返回关掉的直连会话数。
/// </summary>
public sealed class EndSessionResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>被关闭的直连会话数（中继会话关闭是异步的，不计数）。</summary>
    [JsonPropertyName("closed")]
    public int Closed { get; set; }
}

public sealed class PairingInfo
{
    [JsonPropertyName("peer_pub_b64")]
    public string PeerPubB64 { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("paired_utc")]
    public DateTime PairedUtc { get; set; }
}

public sealed class ListPairingsResult
{
    [JsonPropertyName("pairings")]
    public List<PairingInfo> Pairings { get; set; } = new();
}

public sealed class PairParams
{
    [JsonPropertyName("peer_pub_b64")]
    public string PeerPubB64 { get; set; } = "";

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";
}

public sealed class UnpairParams
{
    [JsonPropertyName("peer_pub_b64")]
    public string PeerPubB64 { get; set; } = "";
}

public sealed class GetConfigResult
{
    [JsonPropertyName("data_dir")]
    public string DataDir { get; set; } = "";

    /// <summary>已保存（可能尚未生效）的中继地址；见 <see cref="ActiveRelayUrl"/>。</summary>
    [JsonPropertyName("relay_url")]
    public string? RelayUrl { get; set; }

    /// <summary>
    /// **当前进程真正在用**的中继地址（启动时快照）。
    ///
    /// 为什么需要两个字段：RelayClient 在 <c>Program.BuildHost</c> 阶段按
    /// <c>options.RelayUrl</c> 一次性构造，之后改 <c>_options.RelayUrl</c> 不会
    /// 重建连接。若 UI 只回显 <see cref="RelayUrl"/>，用户改完地址会看到自己填的
    /// 值，以为已经生效——实际上服务还在用旧地址连中继。分开暴露两个值，
    /// UI 才能如实提示"已保存但需重启"。
    /// </summary>
    [JsonPropertyName("active_relay_url")]
    public string? ActiveRelayUrl { get; set; }

    [JsonPropertyName("direct_port")]
    public int DirectPort { get; set; }

    [JsonPropertyName("direct_enabled")]
    public bool DirectEnabled { get; set; }

    /// <summary>本机文件传输授权根目录（会持久化在 data-dir\service.json 里）。</summary>
    [JsonPropertyName("file_scope_roots")]
    public List<string> FileScopeRoots { get; set; } = new();

    /// <summary>当前捕获的显示器索引（0 = 主显示器）。</summary>
    [JsonPropertyName("capture_monitor_index")]
    public int CaptureMonitorIndex { get; set; }
}

public sealed class SetConfigParams
{
    [JsonPropertyName("relay_url")]
    public string? RelayUrl { get; set; }

    [JsonPropertyName("direct_port")]
    public int? DirectPort { get; set; }

    /// <summary>新的文件授权根目录（null = 不改；空数组 = 明确清空）。</summary>
    [JsonPropertyName("file_scope_roots")]
    public List<string>? FileScopeRoots { get; set; }

    /// <summary>新的捕获显示器索引（null = 不改）。</summary>
    [JsonPropertyName("capture_monitor_index")]
    public int? MonitorIndex { get; set; }
}

public sealed class SetConfigResult
{
    /// <summary>
    /// 本次调用**成功执行**（参数合法、没有抛异常）。
    ///
    /// 契约诚实性：这里表达的是"操作做成了"，**不是**"值变了"。
    /// 保存一个和当前一模一样的值也是成功的；把它回报成 false，
    /// 客户端会拿"同值保存"当失败弹错误，用户越点越困惑。
    /// 值有没有变请读 <see cref="Changed"/>。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>与调用前的值相比，本次是否有字段真的发生了变化（false = 存了个相同值）。</summary>
    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    [JsonPropertyName("firewall_repaired")]
    public bool FirewallRepaired { get; set; }

    /// <summary>
    /// 本次保存里有**必须重启服务才生效**的项。
    ///
    /// 契约诚实性：UI 必须据此提示用户，不能弹"已保存"就当生效了。
    /// direct_port 的改动是**立即生效**的（FirewallHelper.ApplyPortChange 真实写规则），
    /// 所以它不置这个标志。
    /// </summary>
    [JsonPropertyName("requires_restart")]
    public bool RequiresRestart { get; set; }

    /// <summary>
    /// 面向用户的说明文案（已本地化）；**没有任何需要用户处理的事**时才为 null。
    ///
    /// 两种非 null 的情况：需要重启才生效，或**没能落盘**（下次启动会退回旧值）。
    /// 后者与"要不要重启"无关——值没变的保存同样可能是写盘失败，
    /// 那种情况照样必须说出来，否则用户会把"没记住"当成"记住了"。
    /// </summary>
    [JsonPropertyName("restart_hint")]
    public string? RestartHint { get; set; }

    /// <summary>
    /// 授权目录/显示器索引是否**成功落盘**到 data-dir\service.json。
    /// false = 目录不可写：本次运行仍按新值执行，但下次启动会退回旧值。
    /// UI 必须把这个区别说出来，否则用户会以为"以后都记住了"。
    /// </summary>
    [JsonPropertyName("persisted")]
    public bool Persisted { get; set; } = true;
}

/// <summary><c>direct_dial</c> 参数：以控制端身份主动拨向被控端的 <c>host:port</c>。</summary>
public sealed class DirectDialParams
{
    /// <summary>对端 Ed25519 公钥（base64）——必须已在本地配对列表中。</summary>
    [JsonPropertyName("peer_pub_b64")]
    public string PeerPubB64 { get; set; } = "";

    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; }
}

/// <summary><c>direct_dial</c> 结果。</summary>
public sealed class DirectDialResultDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>失败原因（已本地化，可直接显示给用户）。</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    /// <summary>实际使用的传输：quic / tcp-tls。</summary>
    [JsonPropertyName("transport")]
    public string? Transport { get; set; }
}

public sealed class StartAgentParams
{
    [JsonPropertyName("inject")]
    public bool Inject { get; set; }

    [JsonPropertyName("no_inject")]
    public bool NoInject { get; set; }

    [JsonPropertyName("pipe_override")]
    public string? PipeOverride { get; set; }

    /// <summary>媒体通道名覆盖；不传则用 Service 实例约定的 <c>{prefix}.AgentMedia.{instance}</c>。</summary>
    [JsonPropertyName("media_pipe")]
    public string? MediaPipe { get; set; }
}

public sealed class StartAgentResult
{
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("stub_mode")]
    public bool StubMode { get; set; }
}

public sealed class PingResult
{
    [JsonPropertyName("pong")]
    public bool Pong { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

// —— 文件传输（P6）——

public sealed class FileScopeResult
{
    /// <summary>本机授权的文件传输根目录（为空表示一律拒绝）。</summary>
    [JsonPropertyName("roots")]
    public List<string> Roots { get; set; } = new();
}

public sealed class FilePathParams
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";
}

public sealed class FileListEntryDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("is_dir")]
    public bool IsDirectory { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("mtime_unix_ms")]
    public long ModifiedUnixMs { get; set; }
}

public sealed class FileListResultDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("entries")]
    public List<FileListEntryDto> Entries { get; set; } = new();
}

public sealed class FileTransferParams
{
    /// <summary>本端路径（相对授权根）。</summary>
    [JsonPropertyName("local")]
    public string Local { get; set; } = "";

    /// <summary>对端路径（相对对端授权根）。</summary>
    [JsonPropertyName("remote")]
    public string Remote { get; set; } = "";

    /// <summary>冲突策略：overwrite / rename / skip。</summary>
    [JsonPropertyName("policy")]
    public string Policy { get; set; } = "overwrite";
}

public sealed class FileTransferResultDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// 一条在途传输的实时进度。
///
/// 为什么需要：<c>file_upload</c> / <c>file_download</c> 是"一次调用、做完才返回"的长调用，
/// 在此之前客户端只能画出 0% → 100% 的假进度条。Service 侧引擎本来就有分块级进度
/// （<c>FileTransferEngine.Snapshot()</c>），这里只是把它**暴露出去**，
/// 客户端在等长调用返回期间轮询本接口即可拿到真实分块进度。
/// </summary>
public sealed class FileProgressEntryDto
{
    /// <summary>传输 id。客户端用它与本端发起的调用对应（对端发起的传输也会出现在这里）。</summary>
    [JsonPropertyName("transfer_id")]
    public uint TransferId { get; set; }

    /// <summary>sending / receiving（从**本机**视角）。</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "";

    /// <summary>对端看到的相对路径（发送方向=要传过去的名字；接收方向=对方发来的名字）。</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("total_bytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("transferred_bytes")]
    public long TransferredBytes { get; set; }

    /// <summary>0~100，已在服务端算好（total=0 视为 100）。</summary>
    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    /// <summary>sending / receiving / paused / cancelled。</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";
}

public sealed class FileProgressResult
{
    /// <summary>本机全部在途传输；空数组表示当前没有会话或没有在途传输。</summary>
    [JsonPropertyName("transfers")]
    public List<FileProgressEntryDto> Transfers { get; set; } = new();
}
