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

    [JsonPropertyName("relay_url")]
    public string? RelayUrl { get; set; }

    [JsonPropertyName("direct_port")]
    public int DirectPort { get; set; }

    [JsonPropertyName("direct_enabled")]
    public bool DirectEnabled { get; set; }
}

public sealed class SetConfigParams
{
    [JsonPropertyName("relay_url")]
    public string? RelayUrl { get; set; }

    [JsonPropertyName("direct_port")]
    public int? DirectPort { get; set; }
}

public sealed class SetConfigResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("firewall_repaired")]
    public bool FirewallRepaired { get; set; }
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
