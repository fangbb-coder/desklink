using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskLink.Panel.Services;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>
/// 用真实的命名管道做一次端到端往返，锁住帧协议 [u32 BE 长度][utf-8 JSON]。
///
/// 这里不复用 Service 的 PipeServer（那会把整个 Service 程序集拖进测试），
/// 而是自己起一个最小服务端：既能验证面板发出的字节完全符合约定，
/// 又不会因为服务端实现变了就把"客户端发对了"这件事一起改掉。
/// </summary>
public class PipeRpcTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static async Task<byte[]> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var hdr = new byte[4];
        await ReadExactAsync(s, hdr, ct);
        uint len = BinaryPrimitives.ReadUInt32BigEndian(hdr);
        var body = new byte[len];
        await ReadExactAsync(s, body, ct);
        return body;
    }

    private static async Task WriteFrameAsync(Stream s, byte[] body, CancellationToken ct)
    {
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame.AsSpan(4));
        await s.WriteAsync(frame, ct);
        await s.FlushAsync(ct);
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
    }

    [Fact]
    public async Task 请求帧以大端u32长度开头且内容是合法PipeRequest()
    {
        string pipe = "dl-panel-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        byte[]? captured = null;

        var server = Task.Run(async () =>
        {
            using var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await s.WaitForConnectionAsync(cts.Token);
            captured = await ReadFrameAsync(s, cts.Token);
            // 回一个最小的成功响应
            var resp = JsonSerializer.SerializeToUtf8Bytes(
                new PipeResponse { Id = "x", Result = JsonSerializer.SerializeToElement(new { ok = true }, JsonOptions) },
                JsonOptions);
            await WriteFrameAsync(s, resp, cts.Token);
        }, cts.Token);

        var got = await PipeRpc.CallAsync(pipe, "get_status");
        await server;

        Assert.NotNull(captured);
        // ReadFrameAsync 已按大端长度切出 payload，故这里校验的是 JSON 起始字节 '{'（0x7B）。
        // 帧格式本身的正确性由"服务端能按 u32 BE 长度完整读出请求"这件事本身保证。
        Assert.Equal((byte)'{', captured![0]);
        var req = JsonSerializer.Deserialize<PipeRequest>(captured, JsonOptions);
        Assert.NotNull(req);
        Assert.Equal("get_status", req!.Method);
        Assert.NotEmpty(req.Id);
        Assert.True(got.Result is not null);
    }

    [Fact]
    public async Task TryGetStatus_在服务返回状态时解出DTO()
    {
        string pipe = "dl-panel-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string? capturedPayload = null;

        var server = Task.Run(async () =>
        {
            using var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await s.WaitForConnectionAsync(cts.Token);
            await ReadFrameAsync(s, cts.Token);
            var result = JsonSerializer.SerializeToElement(new
            {
                // 注意：DeskLink 的 RPC DTO 字段名是 snake_case（见 PipeContract 的 JsonPropertyName），
                // 只有信封（method/params/result/error/id）是 camelCase。写错不会编译报错，只会静默拿到默认值。
                relay_state = "established",
                direct_enabled = true,
                pairing_count = 2,
                instance_id = "dl-ctrl",
                e2e_state = "idle",
                direct_active_sessions = 1,
            });
            var resp = JsonSerializer.SerializeToUtf8Bytes(new PipeResponse { Id = "x", Result = result }, JsonOptions);
            capturedPayload = Encoding.UTF8.GetString(resp);
            await WriteFrameAsync(s, resp, cts.Token);
        }, cts.Token);

        var st = await PipeRpc.TryGetStatusAsync(pipe);
        await server;

        Assert.NotNull(st);
        Assert.NotNull(capturedPayload);
        Assert.Contains("established", capturedPayload);
        Assert.Equal("established", st!.RelayState);
        Assert.True(st.DirectEnabled);
        Assert.Equal(2, st.PairingCount);
        Assert.Equal(1, st.DirectActiveSessions);
    }

    [Fact]
    public async Task TryGetStatus_管道不存在时返回null而不是抛异常()
    {
        var st = await PipeRpc.TryGetStatusAsync("dl-panel-no-such-pipe-" + Guid.NewGuid().ToString("N"), timeoutMs: 300);
        Assert.Null(st);
    }

    [Fact]
    public async Task 服务返回协议级错误时TryGetStatus返回null()
    {
        string pipe = "dl-panel-test-" + Guid.NewGuid().ToString("N");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var server = Task.Run(async () =>
        {
            using var s = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await s.WaitForConnectionAsync(cts.Token);
            await ReadFrameAsync(s, cts.Token);
            var resp = JsonSerializer.SerializeToUtf8Bytes(
                new PipeResponse { Id = "x", Error = new PipeError { Code = 404, Message = "unknown method" } },
                JsonOptions);
            await WriteFrameAsync(s, resp, cts.Token);
        }, cts.Token);

        var st = await PipeRpc.TryGetStatusAsync(pipe);
        await server;

        Assert.Null(st);
    }

    [Fact]
    public async Task 管道名为空时抛ArgumentException而不是静默返回()
    {
        // 空管道名是调用方的编程错误（不是运行时"服务没起来"），必须是 ArgumentException，
        // 否则上层会把它和 PipeUnavailableException 一起吞成"服务未运行"，掩盖真正的 bug。
        await Assert.ThrowsAsync<ArgumentException>(
            () => PipeRpc.CallAsync("", "get_status"));
    }
}
