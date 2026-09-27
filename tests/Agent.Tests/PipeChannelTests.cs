using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskLink.DesktopAgent.Pipe;

namespace DeskLink.Agent.Tests;

/// <summary>
/// 命名管道帧格式与 JSON-RPC 往返。
///
/// 这些用例是 P7 与 Service 之间唯一的进程间契约，必须真正收发字节而不是"编译过就算"。
/// 服务端在测试进程里用 NamedPipeServerStream 现场起一个，客户端走真实的
/// PipeChannel.ConnectAsync，覆盖"连接→发请求→收响应"的完整路径。
///
/// 特别地，第一个用例把**字节序**钉死为大端（与 PipeContract.cs 声明、
/// 以及本仓库"多字节整数一律 big-endian"的约定一致）。
/// 若任一端改回 little-endian，这个用例会立刻变红——这正是我们想要的效果
/// （宁可炸在测试里，也不要"能编译但连不上"）。
/// </summary>
public class PipeChannelTests
{
    private static string NewPipeName() => "DeskLink.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void EncodeFrame_IsBigEndian()
    {
        // 长度 3 的帧：头应是 00 00 00 03（大端），随后才是 body。
        var frame = PipeChannel.EncodeFrame(new byte[] { 0x61, 0x62, 0x63 });

        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x03, 0x61, 0x62, 0x63 }, frame);
    }

    [Fact]
    public void EncodeFrame_RejectsBodyOverMaxFrame()
    {
        var tooBig = new byte[PipeChannel.MaxFrameBytes + 1];
        Assert.Throws<ArgumentOutOfRangeException>(() => PipeChannel.EncodeFrame(tooBig));
    }

    [Fact]
    public async Task WriteThenReadFrame_RoundTrips()
    {
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"世界\"}");
        using var ms = new MemoryStream();

        await PipeChannel.WriteFrameAsync(ms, body);
        ms.Position = 0;
        var read = await PipeChannel.ReadFrameAsync(ms);

        Assert.NotNull(read);
        Assert.Equal(body, read);
    }

    [Fact]
    public async Task ReadFrame_ReturnsNull_WhenPeerClosedWithoutWriting()
    {
        using var ms = new MemoryStream();
        var read = await PipeChannel.ReadFrameAsync(ms);
        Assert.Null(read);
    }

    [Fact]
    public async Task ReadFrame_RejectsOversizedLength()
    {
        // 伪造一个超过 64KB 上限的长度头，必须被拒绝而不是尝试分配 4GB。
        uint bogus = PipeChannel.MaxFrameBytes + 1;
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, bogus);
        using var ms = new MemoryStream(header);

        await Assert.ThrowsAsync<IOException>(() => PipeChannel.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task CallAsync_PingRoundTrip_AgainstNamedPipeServer()
    {
        string pipeName = NewPipeName();
        var requestSeen = new TaskCompletionSource<PipeRequest>();

        // —— 服务端：接收一帧请求，解析出 method，回一个 ping 结果 ——
        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            var body = await PipeChannel.ReadFrameAsync(server);
            Assert.NotNull(body);
            var request = JsonSerializer.Deserialize<PipeRequest>(body, PipeChannel.JsonOptions);
            Assert.NotNull(request);
            requestSeen.SetResult(request);

            var response = new PipeResponse
            {
                Id = request.Id,
                Result = JsonSerializer.SerializeToElement(
                    new PingResult { Pong = true, Version = "test-1.0" }, PipeChannel.JsonOptions),
            };
            var outBody = JsonSerializer.SerializeToUtf8Bytes(response, PipeChannel.JsonOptions);
            await PipeChannel.WriteFrameAsync(server, outBody);
            await server.FlushAsync();
        });

        using var channel = await PipeChannel.ConnectAsync(pipeName, timeoutMs: 5000);
        var pong = await channel.PingAsync();

        Assert.NotNull(pong);
        Assert.True(pong!.Pong);
        Assert.Equal("test-1.0", pong.Version);

        var seen = await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ping", seen.Method);
        // id 必须非空，否则 Service 无法把响应与请求配对。
        Assert.False(string.IsNullOrWhiteSpace(seen.Id));

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CallAsync_ErrorResponse_IsSurfacedNotThrown()
    {
        string pipeName = NewPipeName();

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            var body = await PipeChannel.ReadFrameAsync(server);
            Assert.NotNull(body);
            var request = JsonSerializer.Deserialize<PipeRequest>(body, PipeChannel.JsonOptions)!;

            var response = new PipeResponse
            {
                Id = request.Id,
                Error = new PipeError { Code = PipeErrorCode.PermissionDenied, Message = "拒绝" },
            };
            await PipeChannel.WriteFrameAsync(server, JsonSerializer.SerializeToUtf8Bytes(response, PipeChannel.JsonOptions));
            await server.FlushAsync();
        });

        using var channel = await PipeChannel.ConnectAsync(pipeName, timeoutMs: 5000);

        // 协议层错误应作为结构化响应返回，而不是抛异常——调用方要靠它区分
        // "方法不存在"和"管道断了"（后者才需要重连）。
        var response = await channel.CallAsync("get_device_info");

        Assert.True(response.IsError);
        Assert.NotNull(response.Error);
        Assert.Equal(PipeErrorCode.PermissionDenied, response.Error!.Code);
        Assert.Equal("拒绝", response.Error!.Message);
        Assert.False(response.Result.HasValue);

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task GetDeviceInfoAsync_DeserializesSnakeCaseFields()
    {
        string pipeName = NewPipeName();

        var serverTask = Task.Run(async () =>
        {
            await using var server = new NamedPipeServerStream(
                pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await server.WaitForConnectionAsync();

            var body = await PipeChannel.ReadFrameAsync(server);
            Assert.NotNull(body);
            var request = JsonSerializer.Deserialize<PipeRequest>(body, PipeChannel.JsonOptions)!;

            // 直接用 JSON 文本构造，验证 DTO 上的 [JsonPropertyName] 真的对得上 snake_case。
            // 注意：这里**不能**用 JsonSerializer 序列化 DeviceInfoResult 来造响应，
            // 否则就是"用同一组 attribute 自己证明自己"，证明不了与 Service 的一致性。
            string json =
                "{\"id\":" + JsonSerializer.Serialize(request.Id) + ",\"result\":{" +
                "\"ed25519_pub_b64\":\"ED\"," +
                "\"x25519_pub_b64\":\"X\"," +
                "\"device_id_hint_b64\":\"HINT\"," +
                "\"created_utc\":\"2026-01-02T03:04:05Z\"}}";
            await PipeChannel.WriteFrameAsync(server, Encoding.UTF8.GetBytes(json));
            await server.FlushAsync();
        });

        using var channel = await PipeChannel.ConnectAsync(pipeName, timeoutMs: 5000);
        var info = await channel.GetDeviceInfoAsync();

        Assert.NotNull(info);
        Assert.Equal("ED", info!.Ed25519PubB64);
        Assert.Equal("X", info.X25519PubB64);
        Assert.Equal("HINT", info.DeviceIdHintB64);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), info.CreatedUtc.ToUniversalTime());

        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
