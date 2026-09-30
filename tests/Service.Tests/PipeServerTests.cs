using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskLink.Service.Pipe;
using Xunit;

using DeskLink.Protocol.Pipe;

namespace DeskLink.Service.Tests;

/// <summary>
/// 测试用的 IServiceCore 桩实现。返回确定性数据以便断言。
/// </summary>
internal sealed class StubServiceCore : IServiceCore
{
    public bool StartAgentCalled;
    public bool StopAgentCalled;
    public bool LastInject;
    public bool LastNoInject;

    public DeviceInfoResult GetDeviceInfo() => new()
    {
        Ed25519PubB64 = "AAAA",
        X25519PubB64 = "BBBB",
        DeviceIdHintB64 = "CCCC",
        CreatedUtc = DateTime.UnixEpoch,
    };

    public SignChallengeResult SignChallenge(ReadOnlySpan<byte> challenge)
    {
        // 把挑战复制为签名（仅用于测试）
        return new SignChallengeResult { SignatureB64 = Convert.ToBase64String(challenge.ToArray()) };
    }

    public StatusResult GetStatus() => new()
    {
        RelayState = "connected",
        DirectEnabled = true,
        PairingCount = 2,
        InstanceId = "stub",
        ConsoleMode = true,
    };

    public ListPairingsResult ListPairings() => new()
    {
        Pairings = new List<PairingInfo>
        {
            new() { PeerPubB64 = "AAAA", Label = "alpha", PairedUtc = DateTime.UnixEpoch },
            new() { PeerPubB64 = "BBBB", Label = "bravo", PairedUtc = DateTime.UnixEpoch },
        },
    };

    public void Pair(ReadOnlySpan<byte> peerPub, string label) { }
    public void Unpair(ReadOnlySpan<byte> peerPub) { }

    public GetConfigResult GetConfig() => new()
    {
        DataDir = "C:\\stub",
        RelayUrl = "https://stub",
        ActiveRelayUrl = "https://stub",
        DirectPort = 47200,
        DirectEnabled = true,
        FileScopeRoots = new List<string> { "C:\\stub\\share" },
        CaptureMonitorIndex = 1,
    };

    // set_config 桩：记录完整参数，便于断言新加的 file_scope / monitor 是否真的透下去了。
    public string? LastSetRelayUrl;
    public int? LastSetDirectPort;
    public IReadOnlyList<string>? LastSetFileScopeRoots;
    public int? LastSetMonitorIndex;
    public bool LastSetOk = true;
    public bool LastSetChanged = true;
    // Persisted 必须能独立于 Ok 编排：一次保存可以"执行成功但没落盘"，
    // 而这正是客户端必须说出来的那条路径。共用一个开关的话，
    // 落盘失败的报文永远构造不出来，测试就成了恒过的假绿。
    public bool LastSetPersisted = true;

    public SetConfigResult SetConfig(
        string? relayUrl,
        int? directPort,
        IReadOnlyList<string>? fileScopeRoots = null,
        int? monitorIndex = null)
    {
        LastSetRelayUrl = relayUrl;
        LastSetDirectPort = directPort;
        LastSetFileScopeRoots = fileScopeRoots;
        LastSetMonitorIndex = monitorIndex;
        return new SetConfigResult
        {
            Ok = LastSetOk,
            Changed = LastSetChanged,
            FirewallRepaired = false,
            Persisted = LastSetPersisted,
        };
    }

    // file_progress 桩：默认报"没有在途传输"，测试需要时自己塞。
    public List<FileProgressEntryDto> ProgressEntries { get; } = new();

    public FileProgressResult GetFileProgress() => new() { Transfers = ProgressEntries };

    // direct_dial 桩：记录参数并返回可断言的结果。
    public bool DialDirectCalled;
    public string? LastDialPeerPubB64;
    public string? LastDialHost;
    public int LastDialPort;
    public DirectDialResultDto NextDialResult = new() { Ok = true, Transport = "quic" };

    public Task<DirectDialResultDto> DialDirectAsync(
        string peerPubB64, string host, int port, CancellationToken ct = default)
    {
        DialDirectCalled = true;
        LastDialPeerPubB64 = peerPubB64;
        LastDialHost = host;
        LastDialPort = port;
        return Task.FromResult(NextDialResult);
    }

    public StartAgentResult StartAgent(bool inject, bool noInject, string? pipeOverride, string? mediaPipe = null)
    {
        StartAgentCalled = true;
        LastInject = inject;
        LastNoInject = noInject;
        return new StartAgentResult { Pid = 0, StubMode = true };
    }

    public void StopAgent()
    {
        StopAgentCalled = true;
    }

    public EndSessionResult EndSession() => new() { Ok = true, Closed = 0 };


    // —— P6 文件传输（桩）——

    public FileScopeResult GetFileScope() => new() { Roots = new List<string> { @"C://stub-scope" } };

    public Task<FileListResultDto> ListRemoteFilesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(new FileListResultDto
        {
            Ok = true,
            Entries = new List<FileListEntryDto>
            {
                new() { Name = "a.txt", IsDirectory = false, Size = 11, ModifiedUnixMs = 1000 },
                new() { Name = "dir", IsDirectory = true },
            },
        });

    public Task<FileTransferResultDto> UploadFileAsync(string local, string remote, string policy, CancellationToken ct = default)
        => Task.FromResult(new FileTransferResultDto { Ok = true, Bytes = 42, Target = remote });

    public Task<FileTransferResultDto> DownloadFileAsync(string remote, string local, string policy, CancellationToken ct = default)
        => Task.FromResult(new FileTransferResultDto { Ok = false, Error = "stub-download" });
}

public class PipeServerTests : IAsyncDisposable
{
    private readonly PipeServer _server;
    private readonly StubServiceCore _core;
    private readonly string _clientPipe;
    private readonly string _agentPipe;

    public PipeServerTests()
    {
        _clientPipe = $"DeskLink.Test.Client.{Guid.NewGuid():N}";
        _agentPipe = $"DeskLink.Test.Agent.{Guid.NewGuid():N}";
        _core = new StubServiceCore();
        _server = new PipeServer(_clientPipe, _agentPipe, _core);
        _server.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
    }

    [Fact]
    public async Task Ping_Roundtrip()
    {
        var resp = await SendAsync(_clientPipe, "ping", null);
        Assert.True(resp.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());
        Assert.Equal("P4", resp.RootElement.GetProperty("result").GetProperty("version").GetString());
    }

    [Fact]
    public async Task GetDeviceInfo_Roundtrip()
    {
        var resp = await SendAsync(_clientPipe, "get_device_info", null);
        Assert.Equal("AAAA", resp.RootElement.GetProperty("result").GetProperty("ed25519_pub_b64").GetString());
    }

    [Fact]
    public async Task GetStatus_Roundtrip()
    {
        var resp = await SendAsync(_clientPipe, "get_status", null);
        var result = resp.RootElement.GetProperty("result");
        Assert.Equal("connected", result.GetProperty("relay_state").GetString());
        Assert.True(result.GetProperty("direct_enabled").GetBoolean());
    }

    [Fact]
    public async Task GetConfig_带出文件授权目录与显示器索引()
    {
        var resp = await SendAsync(_clientPipe, "get_config", null);
        var result = resp.RootElement.GetProperty("result");
        Assert.Equal(
            new[] { "C:\\stub\\share" },
            result.GetProperty("file_scope_roots").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(1, result.GetProperty("capture_monitor_index").GetInt32());
    }

    [Fact]
    public async Task SetConfig_把文件授权目录与显示器索引透传给Core()
    {
        var resp = await SendAsync(_clientPipe, "set_config", new
        {
            file_scope_roots = new[] { @"D:\share", @"E:\more" },
            capture_monitor_index = 2,
        });
        Assert.True(resp.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.Equal(new[] { @"D:\share", @"E:\more" }, _core.LastSetFileScopeRoots);
        Assert.Equal(2, _core.LastSetMonitorIndex);
    }

    [Fact]
    public async Task SetConfig_落盘成功时如实回报persisted为真()
    {
        _core.LastSetPersisted = true;
        var ok = await SendAsync(_clientPipe, "set_config", new { capture_monitor_index = 1 });
        Assert.True(ok.RootElement.GetProperty("result").GetProperty("persisted").GetBoolean());
    }

    [Fact]
    public async Task SetConfig_落盘失败必须回报false_不能让客户端以为以后记住了()
    {
        // 目录不可写时：本次运行按新值跑，但下次启动会退回旧值。
        // 管道层若把 false 吞成 true，客户端就会弹一句"已保存"——
        // 用户以为记住了，其实配置已经蒸发。
        _core.LastSetPersisted = false;
        var ok = await SendAsync(_clientPipe, "set_config", new { capture_monitor_index = 1 });
        Assert.False(ok.RootElement.GetProperty("result").GetProperty("persisted").GetBoolean());
    }

    [Fact]
    public async Task SetConfig_ok表达的是执行成功_不是值有没有变()
    {
        // 存一个和当前一模一样的值也是成功的。以前 ok 跟着 changed 走，
        // 客户端会把"同值保存"当失败弹红条，用户越点越困惑。
        _core.LastSetOk = true;
        _core.LastSetChanged = false;
        var ok = await SendAsync(_clientPipe, "set_config", new { capture_monitor_index = 1 });
        var result = ok.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("changed").GetBoolean());
    }

    [Fact]
    public async Task FileProgress_无在途传输时返回空列表而不是null()
    {
        var resp = await SendAsync(_clientPipe, "file_progress", null);
        var transfers = resp.RootElement.GetProperty("result").GetProperty("transfers");
        Assert.Equal(0, transfers.GetArrayLength());
    }

    [Fact]
    public async Task FileProgress_把在途传输的字节数如实报回去()
    {
        _core.ProgressEntries.Add(new FileProgressEntryDto
        {
            TransferId = 1,
            Direction = "sending",
            Path = @"share\big.bin",
            TotalBytes = 2048,
            TransferredBytes = 512,
            Percent = 25,
            State = "active",
        });

        var resp = await SendAsync(_clientPipe, "file_progress", null);
        var t = resp.RootElement.GetProperty("result").GetProperty("transfers")[0];
        Assert.Equal("sending", t.GetProperty("direction").GetString());
        Assert.Equal(@"share\big.bin", t.GetProperty("path").GetString());
        Assert.Equal(2048, t.GetProperty("total_bytes").GetInt64());
        Assert.Equal(512, t.GetProperty("transferred_bytes").GetInt64());
        Assert.Equal(25, t.GetProperty("percent").GetDouble());
    }

    [Fact]
    public async Task SignChallenge_Base64_Roundtrip()
    {
        var challenge = new byte[] { 1, 2, 3, 4, 5 };
        var resp = await SendAsync(_clientPipe, "sign_challenge", new
        {
            challenge_b64 = Convert.ToBase64String(challenge),
        });
        var sig = resp.RootElement.GetProperty("result").GetProperty("signature_b64").GetString();
        Assert.Equal(Convert.ToBase64String(challenge), sig);
    }

    [Fact]
    public async Task Unknown_Method_Returns_MethodNotFound()
    {
        var resp = await SendAsync(_clientPipe, "nope", null);
        Assert.True(resp.RootElement.TryGetProperty("error", out var err));
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Invalid_Json_Returns_InvalidParams()
    {
        var resp = await SendAsyncRaw(_clientPipe, "{not json}");
        Assert.True(resp.RootElement.TryGetProperty("error", out var err));
        Assert.Equal(-32602, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task StartAgent_Forward_Params_To_Core()
    {
        var resp = await SendAsync(_agentPipe, "start_agent", new
        {
            inject = false,
            no_inject = true,
            pipe_override = "DeskLink.Agent.stuba",
        });
        Assert.True(resp.RootElement.GetProperty("result").GetProperty("stub_mode").GetBoolean());
        Assert.True(_core.StartAgentCalled);
        Assert.False(_core.LastInject);
        Assert.True(_core.LastNoInject);
    }

    [Fact]
    public async Task StopAgent_Forwards()
    {
        var resp = await SendAsync(_clientPipe, "stop_agent", null);
        Assert.True(resp.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.True(_core.StopAgentCalled);
    }

    // ───────────────── 缺陷①：direct_dial 派发 ─────────────────
    //
    // 这条 RPC 是 WPF 客户端"局域网直连"背后**唯一**真正拨号的东西。
    // 此前客户端只校验 IP:端口就显示已连接，等于整条路径没有出口。

    [Fact]
    public async Task DirectDial_Forwards_Peer_Host_And_Port_To_Core()
    {
        var pub = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var resp = await SendAsync(_clientPipe, "direct_dial", new
        {
            peer_pub_b64 = pub,
            host = "192.168.1.20",
            port = 47200,
        });

        Assert.True(_core.DialDirectCalled);
        Assert.Equal(pub, _core.LastDialPeerPubB64);
        Assert.Equal("192.168.1.20", _core.LastDialHost);
        Assert.Equal(47200, _core.LastDialPort);

        var result = resp.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("quic", result.GetProperty("transport").GetString());
    }

    [Fact]
    public async Task DirectDial_Failure_Detail_Is_Propagated()
    {
        _core.NextDialResult = new DirectDialResultDto
        {
            Ok = false,
            Detail = "该设备不在本机配对列表中，请先完成配对",
        };

        var resp = await SendAsync(_clientPipe, "direct_dial", new
        {
            peer_pub_b64 = Convert.ToBase64String(new byte[32]),
            host = "127.0.0.1",
            port = 47200,
        });

        var result = resp.RootElement.GetProperty("result");
        // 失败必须带可读原因，UI 才能告诉用户到底为什么没连上。
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Contains("配对", result.GetProperty("detail").GetString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectDial_Without_PeerPub_Is_InvalidParams()
    {
        var resp = await SendAsync(_clientPipe, "direct_dial", new
        {
            host = "127.0.0.1",
            port = 47200,
        });

        Assert.False(_core.DialDirectCalled);
        Assert.False(resp.RootElement.TryGetProperty("result", out _));
        var err = resp.RootElement.GetProperty("error");
        // PipeError.Code 在契约里是数字枚举（PipeErrorCode.InvalidParams）。
        Assert.Equal((int)DeskLink.Protocol.Pipe.PipeErrorCode.InvalidParams, err.GetProperty("code").GetInt32());
        Assert.Contains("peer_pub_b64", err.GetProperty("message").GetString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_Frame_Disconnects()
    {
        // 帧头声明超大长度 → 服务端关闭连接，不响应
        await using var client = new NamedPipeClientStream(".", _clientPipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);

        // 4 字节长度 = 100 MB（> 64KB 上限）。
        //
        // 注意：必须按**大端**构造。早期本用例手写 `00 00 A0 00` 是按小端写的
        // （LE 下 = 0x00A00000 ≈ 10MB，确实超限）；改成大端后该串变成 0x0000A000 = 40KB，
        // 反而成了**合法**长度 —— 服务端会一直等 40KB 数据，客户端一直等响应，
        // 双方互等导致测试宿主挂死后被判为"进程崩溃"。
        // 这正是"字节序不一致"最典型的坑：测试自己写错了字节，却表现为运行时卡死。
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, 100u * 1024 * 1024);
        await client.WriteAsync(header);
        await client.FlushAsync();

        // 读不应有响应（服务端应已断开）
        var buf = new byte[4];
        try
        {
            using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var n = await client.ReadAsync(buf, readCts.Token);
            Assert.Equal(0, n);
        }
        catch (IOException)
        {
            // 服务端主动断开 → 抛 IOException 也是合法
        }
        catch (OperationCanceledException)
        {
            // 兜底：若服务端既没断开也没响应，测试必须**失败**而不是无限挂起。
            Assert.Fail("服务端未在 5s 内关闭超大帧连接（应立刻断开）");
        }
    }

    // —— P6 文件传输 RPC ——

    [Fact]
    public async Task FileScope_ReturnsAuthorisedRoots()
    {
        var resp = await SendAsync(_clientPipe, "file_scope", null);
        var roots = resp.RootElement.GetProperty("result").GetProperty("roots");
        Assert.Equal(1, roots.GetArrayLength());
        Assert.Equal(@"C://stub-scope", roots[0].GetString());
    }

    [Fact]
    public async Task FileList_ReturnsEntries()
    {
        var resp = await SendAsync(_clientPipe, "file_list", new { path = "" });
        var result = resp.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        var entries = result.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Equal("a.txt", entries[0].GetProperty("name").GetString());
        Assert.Equal(11, entries[0].GetProperty("size").GetInt64());
        Assert.True(entries[1].GetProperty("is_dir").GetBoolean());
    }

    [Fact]
    public async Task FileUpload_ReturnsTransferResult()
    {
        var resp = await SendAsync(_clientPipe, "file_upload", new
        {
            local = "a.bin",
            remote = "b.bin",
            policy = "rename",
        });
        var result = resp.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(42, result.GetProperty("bytes").GetInt64());
        Assert.Equal("b.bin", result.GetProperty("target").GetString());
    }

    [Fact]
    public async Task FileDownload_SurfacesFailureAsStructuredResult()
    {
        // 传输失败是"业务结果"而不是协议错误：必须回 result.ok=false，
        // 让调用方能区分"传输失败"与"方法不存在/管道断了"。
        var resp = await SendAsync(_clientPipe, "file_download", new
        {
            remote = "a.bin",
            local = "b.bin",
            policy = "overwrite",
        });
        Assert.False(resp.RootElement.TryGetProperty("error", out _));
        var result = resp.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("stub-download", result.GetProperty("error").GetString());
    }

    [Fact]
    public async Task FileUpload_MissingParams_ReturnsInvalidParams()
    {
        var resp = await SendAsyncRaw(_clientPipe,
            "{\"method\":\"file_upload\",\"id\":\"1\"}");
        Assert.True(resp.RootElement.TryGetProperty("error", out var err));
        Assert.Equal(-32602, err.GetProperty("code").GetInt32());
    }

    // —— 辅助 ——
    private static async Task<JsonDocument> SendAsync(string pipeName, string method, object? @params)
    {
        var req = new
        {
            method,
            @params,
            id = Guid.NewGuid().ToString("N"),
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(req, JsonOpts);
        return await SendAsyncRaw(pipeName, body);
    }

    private static async Task<JsonDocument> SendAsyncRaw(string pipeName, string rawJson)
    {
        return await SendAsyncRaw(pipeName, Encoding.UTF8.GetBytes(rawJson));
    }

    private static async Task<JsonDocument> SendAsyncRaw(string pipeName, byte[] body)
    {
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);

        var header = new byte[4];
        // 长度前缀是 big-endian（与 PipeContract.cs 声明及 README「协议字节序」一致）。
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
        await client.WriteAsync(header);
        await client.WriteAsync(body);
        await client.FlushAsync();

        // 读响应
        var lenBuf = new byte[4];
        await ReadExactAsync(client, lenBuf, 4);
        var len = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
        Assert.True(len > 0 && len < 64 * 1024);

        var payload = new byte[len];
        await ReadExactAsync(client, payload, len);
        return JsonDocument.Parse(payload);
    }

    private static async Task ReadExactAsync(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(total, count - total));
            if (n == 0) throw new IOException("pipe closed");
            total += n;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
