using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskLink.DesktopAgent.Pipe;

/// <summary>
/// Service 的 agent 命名管道客户端。
///
/// 帧格式：[u32 长度][utf-8 JSON]，**长度为大端（big-endian）**。
///
/// 字节序说明：PipeContract.cs 声明 BE，PipeServer.cs 早期误写成 little-endian。
/// 现已统一为 BE（与本仓库"多字节整数一律 big-endian"的约定一致，见 README）。
/// 两端任一侧改回 LE 都会让本客户端与 Service 静默连不上——测试用固定字节锚点锁住。
///
/// 其它约定（与 PipeServer 对齐）：
///   - 帧长度硬上限 64 KB（RPC 元数据，绝不允许编码流进管道）。
///   - 单连接请求/响应串行化：管道是单工请求-响应语义，并发调用必须用信号量排队。
/// </summary>
public sealed class PipeChannel : IDisposable
{
    public const int MaxFrameBytes = 64 * 1024;

    private readonly NamedPipeClientStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private PipeChannel(NamedPipeClientStream stream, string pipeName)
    {
        _stream = stream;
        PipeName = pipeName;
    }

    public string PipeName { get; }

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>连接到 Service 的 agent 管道。超时抛 TimeoutException。</summary>
    public static async Task<PipeChannel> ConnectAsync(string pipeName, int timeoutMs, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipeName 不能为空", nameof(pipeName));

        var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await stream.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
            return new PipeChannel(stream, pipeName);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>发送一个 JSON-RPC 请求并等待响应。</summary>
    public async Task<PipeResponse> CallAsync(string method, object? parameters = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var request = new PipeRequest
        {
            Method = method,
            Id = Guid.NewGuid().ToString("N"),
            Params = parameters is null
                ? null
                : JsonSerializer.SerializeToElement(parameters, JsonOptions),
        };

        var body = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteFrameAsync(_stream, body, ct).ConfigureAwait(false);
            var responseBody = await ReadFrameAsync(_stream, ct).ConfigureAwait(false);
            if (responseBody is null)
            {
                throw new IOException("管道在读取响应前被对端关闭");
            }

            var response = JsonSerializer.Deserialize<PipeResponse>(responseBody, JsonOptions);
            return response ?? throw new IOException("响应 JSON 解析为空");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PingResult?> PingAsync(CancellationToken ct = default)
    {
        var response = await CallAsync("ping", null, ct).ConfigureAwait(false);
        if (response.IsError || response.Result is null) return null;
        return response.Result.Value.Deserialize<PingResult>(JsonOptions);
    }

    public async Task<DeviceInfoResult?> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        var response = await CallAsync("get_device_info", null, ct).ConfigureAwait(false);
        if (response.IsError || response.Result is null) return null;
        return response.Result.Value.Deserialize<DeviceInfoResult>(JsonOptions);
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
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(4));
        return frame;
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> body, CancellationToken ct = default)
    {
        var frame = EncodeFrame(body.Span);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>读取一帧；对端正常关闭时返回 null。</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        int read = await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        if (read == 0) return null;
        if (read < 4) throw new IOException("读取帧头时对端提前关闭");

        uint len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
        if (len == 0 || len > MaxFrameBytes)
        {
            throw new IOException($"非法帧长度 {len}");
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
        _gate.Dispose();
    }
}
