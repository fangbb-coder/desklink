using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskLink.Protocol.Pipe;

namespace DeskLink.Client.Services;

/// <summary>
/// "Service 没在跑"：连接不上管道。与"方法返回了错误"必须严格区分——
/// 前者是可重试的运维状态（提示用户服务未启动），后者是业务/协议错误。
/// </summary>
public sealed class PipeUnavailableException : IOException
{
    public PipeUnavailableException(string pipeName, string message, Exception? inner = null)
        : base(message, inner)
    {
        PipeName = pipeName;
    }

    public string PipeName { get; }
}

/// <summary>
/// "Service 在跑，但这次调用被拒绝/失败"：携带结构化的 JSON-RPC 错误码。
/// </summary>
public sealed class PipeRpcException : Exception
{
    public PipeRpcException(string method, PipeError error)
        : base($"RPC {method} 失败 [{error.Code}] {error.Message}")
    {
        Method = method;
        Error = error;
    }

    public string Method { get; }
    public PipeError Error { get; }
    public int Code => Error.Code;
}

/// <summary>
/// Service 命名管道 RPC 客户端（DeskLink.exe → DeskLinkService）。
///
/// 帧格式：[u32 length BE][utf-8 JSON]，与 <c>PipeServer</c> 完全一致。
/// 字节序是 big-endian（本仓库"多字节整数一律 big-endian"的全局约定）；
/// 写错端序会表现为"静默连不上"，测试用固定字节锚点锁死。
///
/// 并发模型：管道是**单工请求-响应**语义（一个连接同一时刻只能有一个在途请求），
/// 因此所有调用经一个 <see cref="SemaphoreSlim"/> 串行化。不要试图并发复用。
/// </summary>
public sealed class PipeRpcClient : IAsyncDisposable
{
    /// <summary>RPC 元数据的帧长硬上限（与 PipeServer 一致，绝不允许编码流进管道）。</summary>
    public const int MaxFrameBytes = 64 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NamedPipeClientStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private PipeRpcClient(NamedPipeClientStream stream, string pipeName)
    {
        _stream = stream;
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public bool IsConnected => !_disposed && _stream.IsConnected;

    /// <summary>
    /// 连接 Service 的客户端管道。连接失败（超时/管道不存在）统一抛
    /// <see cref="PipeUnavailableException"/>，让上层能把它翻译成"服务未运行"。
    /// </summary>
    public static async Task<PipeRpcClient> ConnectAsync(string pipeName, int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            throw new ArgumentException("pipeName 不能为空", nameof(pipeName));
        }

        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return new PipeRpcClient(stream, pipeName);
        }
        catch (OperationCanceledException)
        {
            // 调用方取消也要释放已创建的管道句柄（旧代码只处理非取消异常，取消时泄漏）。
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new PipeUnavailableException(
                pipeName,
                $"无法连接 Service 管道 {pipeName}（{timeoutMs}ms 内未连上）：{ex.GetType().Name}",
                ex);
        }
    }

    /// <summary>
    /// 发一个请求并原样拿回 <see cref="PipeResponse"/>（**错误不抛**，以便调用方按需分支）。
    /// </summary>
    public async Task<PipeResponse> CallRawAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new PipeRequest
        {
            Method = method,
            Id = Guid.NewGuid().ToString("N"),
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters, JsonOptions),
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(_stream, body, ct).ConfigureAwait(false);

            var responseBody = await ReadFrameAsync(_stream, ct).ConfigureAwait(false);
            if (responseBody is null)
            {
                throw new PipeUnavailableException(PipeName, "管道在读取响应前被对端关闭（Service 可能已退出）");
            }

            var response = JsonSerializer.Deserialize<PipeResponse>(responseBody, JsonOptions);
            if (response is null)
            {
                throw new PipeUnavailableException(PipeName, "响应 JSON 解析为空");
            }

            // id 关联校验：管道是串行的，id 必须回显同一个，否则说明流已错位。
            if (!string.IsNullOrEmpty(response.Id) && response.Id != request.Id)
            {
                throw new InvalidDataException($"RPC 响应 id 不匹配：期望 {request.Id}，收到 {response.Id}");
            }

            return response;
        }
        catch (IOException ex) when (ex is not PipeUnavailableException)
        {
            throw new PipeUnavailableException(PipeName, $"管道 I/O 失败：{ex.Message}", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 发请求并把 result 反序列化为 <typeparamref name="T"/>。
    /// 方法返回 error 时抛 <see cref="PipeRpcException"/>（结构化错误码）。
    /// </summary>
    public async Task<T> CallAsync<T>(string method, object? parameters = null, CancellationToken ct = default)
    {
        var response = await CallRawAsync(method, parameters, ct).ConfigureAwait(false);
        if (response.Error is not null)
        {
            throw new PipeRpcException(method, response.Error);
        }
        if (response.Result is null)
        {
            throw new PipeRpcException(method, new PipeError { Code = PipeErrorCode.InternalError, Message = "响应缺少 result" });
        }

        var value = response.Result.Value.Deserialize<T>(JsonOptions);
        if (value is null)
        {
            throw new PipeRpcException(method, new PipeError { Code = PipeErrorCode.InternalError, Message = "result 反序列化为空" });
        }
        return value;
    }

    // —— 帧编解码（公开以便测试直接验证字节序）——

    /// <summary>把 JSON 体打包成 [u32 大端长度][body]。</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxFrameBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(body), $"帧长度 {body.Length} 超过上限 {MaxFrameBytes}");
        }

        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var frame = EncodeFrame(body.Span);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读取一帧；对端正常关闭时返回 null。长度越界抛 IOException（防 DoS）。</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        int read = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < 4) throw new IOException("读取帧头时对端提前关闭");

        uint len = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (len == 0 || len > MaxFrameBytes)
        {
            throw new IOException($"非法帧长度 {len}（上限 {MaxFrameBytes}）");
        }

        var body = new byte[len];
        int bodyRead = await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        if (bodyRead != len) throw new IOException("读取帧体时对端提前关闭");
        return body;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Dispose();
        }
    }
}
