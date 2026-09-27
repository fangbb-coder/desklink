using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink.DesktopAgent.Pipe;

/// <summary>
/// 命名管道 JSON-RPC 的请求/响应 DTO。
///
/// 为什么在这里**重新声明**而不是引用 DeskLink.Service 的 PipeContract：
/// 代理与 Service 是两个独立进程，v1 刻意不让 Agent 项目依赖 Service 项目
/// （避免把整个 Service 的依赖图（QUIC/DPAPI/文件引擎…）拖进一个只需要发几 KB JSON 的小进程）。
/// 代价是两份 DTO 需要手工保持一致；字段名以 JSON 名为准，改动时必须两边同步。
/// </summary>
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

    public bool IsError => Error is not null;
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

public sealed class PingResult
{
    [JsonPropertyName("pong")]
    public bool Pong { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
}

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
