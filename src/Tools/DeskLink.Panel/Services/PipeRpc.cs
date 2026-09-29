// DeskLink.Panel —— Service 命名管道 JSON-RPC 客户端
//
// 帧格式 [u32 BE 长度][utf-8 JSON]，与 PipeServer 及 tests\verify-lan.ps1 一致。
// 这里刻意自带一份最小实现而**不引用 DeskLink.Client**：
//   - 面板与客户端是两个独立的可执行物，共享一份 UI 基础设施会让"客户端升级"变成"面板也要重编"；
//   - 面板只需要 get_status 一个方法，拖进整个 Client 程序集不划算；
//   - DTO 复用 DeskLink.Protocol 的 StatusResult，杜绝契约漂移。
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskLink.Protocol.Pipe;

namespace DeskLink.Panel.Services;

/// <summary>管道不可用（服务未启动 / 实例名写错 / 连接超时）。上层翻译成"服务未运行"文案。</summary>
public sealed class PipeUnavailableException : Exception
{
    public PipeUnavailableException(string pipeName, string message, Exception? inner = null)
        : base(message, inner) => PipeName = pipeName;

    public string PipeName { get; }
}

/// <summary>
/// 一次性 RPC 调用（开连接 → 发一帧 → 收一帧 → 断）。
/// 面板的状态轮询是低频短连接，不需要 Client 里那种长连接 + 信号量串行化。
/// </summary>
public static class PipeRpc
{
    /// <summary>RPC 帧长硬上限，与 PipeServer 一致。</summary>
    public const int MaxFrameBytes = 64 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 调用一次 RPC。返回 <see cref="PipeResponse"/>——**协议级错误不抛**，
    /// 由调用方按 Error/Result 分支，这样"服务回了错误"和"服务没起来"是两件可区分的事。
    /// </summary>
    public static async Task<PipeResponse> CallAsync(
        string pipeName,
        string method,
        object? parameters = null,
        int timeoutMs = 3000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("pipeName 不能为空", nameof(pipeName));

        using var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PipeUnavailableException(
                pipeName,
                $"无法连接 Service 管道 {pipeName}（{timeoutMs}ms 内未连上）：{ex.GetType().Name}", ex);
        }

        var request = new PipeRequest
        {
            Method = method,
            Id = Guid.NewGuid().ToString("N"),
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, JsonOptions),
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(4));

        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        uint len = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (len == 0 || len > MaxFrameBytes)
            throw new InvalidDataException($"响应帧长度非法：{len} 字节");

        var payload = new byte[len];
        await ReadExactAsync(stream, payload, ct).ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<PipeResponse>(payload, JsonOptions);
        if (response is null)
            throw new InvalidDataException("响应不是合法的 PipeResponse JSON");
        return response;
    }

    /// <summary>取当前状态。服务不可用时返回 null（调用方据此显示"服务未运行"）。</summary>
    public static async Task<StatusResult?> TryGetStatusAsync(
        string pipeName, int timeoutMs = 2000, CancellationToken ct = default)
    {
        try
        {
            var resp = await CallAsync(pipeName, "get_status", null, timeoutMs, ct).ConfigureAwait(false);
            if (resp.Error is not null || resp.Result is null) return null;
            return resp.Result.Value.Deserialize<StatusResult>(JsonOptions);
        }
        catch (Exception ex) when (ex is PipeUnavailableException or InvalidDataException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[read..], ct).ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException($"管道在读完 {read}/{buffer.Length} 字节前关闭");
            read += n;
        }
    }
}
