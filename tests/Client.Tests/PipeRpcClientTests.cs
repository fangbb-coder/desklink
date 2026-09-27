using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskLink.Client.Services;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// PipeRpcClient 的帧编解码与端到端往返（用真实 NamedPipeServerStream 起服务端）。
/// </summary>
public class PipeRpcClientTests
{
    [Fact]
    public void EncodeFrame_Writes_BigEndian_Length_Header()
    {
        var body = Encoding.UTF8.GetBytes("hello");

        var frame = PipeRpcClient.EncodeFrame(body);

        // 精确字节锚点：长度 5 必须是 00 00 00 05（大端），不是 05 00 00 00。
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x05 }, frame.AsSpan(0, 4).ToArray());
        Assert.Equal(body, frame.AsSpan(4).ToArray());
        Assert.Equal(9, frame.Length);
    }

    [Fact]
    public void EncodeFrame_MultiByte_Length_Is_BigEndian()
    {
        // 258 = 0x0102：LE 会写成 02 01 00 00，BE 是 00 00 01 02。用它区分端序。
        var body = new byte[258];
        var frame = PipeRpcClient.EncodeFrame(body);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x01, 0x02 }, frame.AsSpan(0, 4).ToArray());
    }

    [Fact]
    public void EncodeFrame_Rejects_Body_Over_MaxFrameBytes()
    {
        var body = new byte[PipeRpcClient.MaxFrameBytes + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => PipeRpcClient.EncodeFrame(body));
    }

    [Fact]
    public async Task ReadFrameAsync_Rejects_Oversized_Declared_Length()
    {
        // 帧头声明 100MB（远超 64KB 上限）→ 必须拒绝，而不是尝试分配 100MB。
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 100u * 1024 * 1024);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<IOException>(() => PipeRpcClient.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrameAsync_Rejects_Zero_Length()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0);
        await using var stream = new MemoryStream(header);

        await Assert.ThrowsAsync<IOException>(() => PipeRpcClient.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task CallAsync_RoundTrips_Against_Real_PipeServer()
    {
        var pipeName = NewPipeName();
        var server = RunSingleExchangeAsync(pipeName, req => new PipeResponse
        {
            Id = req.Id,
            Result = JsonSerializer.SerializeToElement(new { pong = true, version = "test" }, PipeRpcClient.JsonOptions),
        });

        await using var client = await PipeRpcClient.ConnectAsync(pipeName, 5000);
        var ping = await client.CallAsync<PingResult>("ping");

        Assert.True(ping.Pong);
        Assert.Equal("test", ping.Version);
        await server;
    }

    [Fact]
    public async Task CallRawAsync_Surfaces_Error_As_Structured_Error_Not_Throw()
    {
        var pipeName = NewPipeName();
        var server = RunSingleExchangeAsync(pipeName, req => new PipeResponse
        {
            Id = req.Id,
            Error = new PipeError { Code = PipeErrorCode.MethodNotFound, Message = "unknown method: nope" },
        });

        await using var client = await PipeRpcClient.ConnectAsync(pipeName, 5000);
        var response = await client.CallRawAsync("nope");

        Assert.Null(response.Result);
        Assert.NotNull(response.Error);
        Assert.Equal(PipeErrorCode.MethodNotFound, response.Error!.Code);
        Assert.Contains("unknown method", response.Error.Message);
        await server;
    }

    [Fact]
    public async Task CallAsync_Throws_Structured_PipeRpcException_On_Error_Response()
    {
        var pipeName = NewPipeName();
        var server = RunSingleExchangeAsync(pipeName, req => new PipeResponse
        {
            Id = req.Id,
            Error = new PipeError { Code = PipeErrorCode.InvalidParams, Message = "bad params" },
        });

        await using var client = await PipeRpcClient.ConnectAsync(pipeName, 5000);
        var ex = await Assert.ThrowsAsync<PipeRpcException>(() => client.CallAsync<PingResult>("ping"));

        Assert.Equal(PipeErrorCode.InvalidParams, ex.Code);
        Assert.Equal("bad params", ex.Error.Message);
        await server;
    }

    [Fact]
    public async Task CallRawAsync_Correlates_Request_And_Response_Id()
    {
        var pipeName = NewPipeName();
        string? seenId = null;
        var server = RunSingleExchangeAsync(pipeName, req =>
        {
            seenId = req.Id;
            return new PipeResponse
            {
                Id = req.Id,
                Result = JsonSerializer.SerializeToElement(new { pong = true, version = "v" }, PipeRpcClient.JsonOptions),
            };
        });

        await using var client = await PipeRpcClient.ConnectAsync(pipeName, 5000);
        var response = await client.CallRawAsync("ping");

        Assert.False(string.IsNullOrEmpty(seenId));
        Assert.Equal(seenId, response.Id);
        await server;
    }

    [Fact]
    public async Task CallRawAsync_Serializes_Concurrent_Calls_Over_One_Connection()
    {
        // 管道是单工请求-响应：并发调用必须被信号量串行化，否则响应会串台。
        var pipeName = NewPipeName();
        var serverTask = Task.Run(async () =>
        {
            await using var server = CreateServer(pipeName);
            await server.WaitForConnectionAsync();
            for (int i = 0; i < 5; i++)
            {
                var body = await PipeRpcClient.ReadFrameAsync(server);
                var req = JsonSerializer.Deserialize<PipeRequest>(body!, PipeRpcClient.JsonOptions)!;
                var resp = new PipeResponse
                {
                    Id = req.Id,
                    Result = JsonSerializer.SerializeToElement(new { pong = true, version = req.Id }, PipeRpcClient.JsonOptions),
                };
                await PipeRpcClient.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(resp, PipeRpcClient.JsonOptions));
            }
        });

        await using var client = await PipeRpcClient.ConnectAsync(pipeName, 5000);
        var tasks = Enumerable.Range(0, 5).Select(_ => client.CallAsync<PingResult>("ping")).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(5, results.Length);
        Assert.All(results, r => Assert.True(r.Pong));
        await serverTask;
    }

    [Fact]
    public async Task ConnectAsync_When_Server_Not_Running_Throws_PipeUnavailable()
    {
        // 用一个几乎不可能存在的管道名，短超时。
        var pipeName = $"DeskLink.Test.Missing.{Guid.NewGuid():N}";

        var ex = await Assert.ThrowsAsync<PipeUnavailableException>(
            () => PipeRpcClient.ConnectAsync(pipeName, timeoutMs: 200));

        Assert.Equal(pipeName, ex.PipeName);
        Assert.Contains(pipeName, ex.Message);
    }

    // —— 辅助 ——

    private static string NewPipeName() => $"DeskLink.Test.Rpc.{Guid.NewGuid():N}";

    private static NamedPipeServerStream CreateServer(string pipeName)
        => new(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>起一个只处理一次请求的服务器，返回可 await 的 Task。</summary>
    private static async Task RunSingleExchangeAsync(string pipeName, Func<PipeRequest, PipeResponse> respond)
    {
        await using var server = CreateServer(pipeName);
        await server.WaitForConnectionAsync();

        var body = await PipeRpcClient.ReadFrameAsync(server);
        Assert.NotNull(body);
        var req = JsonSerializer.Deserialize<PipeRequest>(body!, PipeRpcClient.JsonOptions)!;

        var resp = respond(req);
        await PipeRpcClient.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(resp, PipeRpcClient.JsonOptions));
    }
}
