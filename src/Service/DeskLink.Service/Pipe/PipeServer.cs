// 命名管道 RPC 服务端。
//
// 监听两套管道：
//   - ClientPipe：DeskLink.exe（WPF）通信
//   - AgentPipe：DeskLink.DesktopAgent 通信
//
// 帧格式：[u32 length BE][utf-8 JSON]。
// 每个连接复用：循环读请求 → 派发 → 写响应，直到对端关闭。
//
// 安全：
//   - 默认 PipeSecurity：当前用户 / SYSTEM / Admins 可连；其他拒绝。
//   - 帧长度硬上限 64 KB（远超任何 RPC 元数据，绝不允许编码流进管道）。
//   - 错误统一包成 PipeError，不向调用方泄露栈。
//
// 生命周期：Start/Stop 由 ServiceHost 控制；每个连接一个 Task，限制并发（默认 16）。
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using DeskLink.Protocol.Pipe;

using DeskLink.Service.Security;

namespace DeskLink.Service.Pipe;

public sealed class PipeServer : IAsyncDisposable
{
    private readonly string _clientPipeName;
    private readonly string _agentPipeName;
    private readonly IServiceCore _core;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _clientLoop;
    private Task? _agentLoop;
    private int _activeConnections;
    private const int MaxConcurrentConnections = 16;
    private const int MaxFrameBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public PipeServer(string clientPipeName, string agentPipeName, IServiceCore core, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(clientPipeName))
        {
            throw new ArgumentException("clientPipeName required", nameof(clientPipeName));
        }
        if (string.IsNullOrWhiteSpace(agentPipeName))
        {
            throw new ArgumentException("agentPipeName required", nameof(agentPipeName));
        }
        _clientPipeName = clientPipeName;
        _agentPipeName = agentPipeName;
        _core = core;
        _log = log;
    }

    public void Start()
    {
        _clientLoop = Task.Run(() => AcceptLoopAsync(_clientPipeName, _cts.Token));
        _agentLoop = Task.Run(() => AcceptLoopAsync(_agentPipeName, _cts.Token));
        _log?.Invoke($"PipeServer started: client={_clientPipeName} agent={_agentPipeName}");
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        try
        {
            if (_clientLoop != null) await _clientLoop;
            if (_agentLoop != null) await _agentLoop;
        }
        catch (OperationCanceledException) { /* expected */ }
        _log?.Invoke("PipeServer stopped");
    }

    private async Task AcceptLoopAsync(string pipeName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServerStream(pipeName);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // 限制并发；超限直接关连接（轻量拒绝）。
                if (Interlocked.Increment(ref _activeConnections) > MaxConcurrentConnections)
                {
                    Interlocked.Decrement(ref _activeConnections);
                    _log?.Invoke($"PipeServer {pipeName}: rejected (max concurrent)");
                    server.Dispose();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServeConnectionAsync(server, pipeName, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log?.Invoke($"PipeServer {pipeName}: connection error {ex.GetType().Name}: {ex.Message}");
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeConnections);
                        server.Dispose();
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                return;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"PipeServer {pipeName}: accept error {ex.GetType().Name}: {ex.Message}");
                server?.Dispose();
                // 退避 100ms 避免紧错误循环
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private NamedPipeServerStream CreateServerStream(string pipeName)
    {
        // ACL 统一由 PipeAcl 构造：必须显式授予**控制台登录用户**，否则服务以 LocalSystem
        // 运行时，同机非提权用户连不上（Administrators 在过滤令牌里是 deny-only）。
        // 详见 PipeAcl 顶部注释。
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: PipeAcl.Build(_log));
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream server, string pipeName, CancellationToken ct)
    {
        using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(server, new UTF8Encoding(false), leaveOpen: true);

        while (server.IsConnected && !ct.IsCancellationRequested)
        {
            // 读 4 字节长度
            var lenBytes = new byte[4];
            int read = 0;
            try
            {
                read = await ReadExactAsync(server, lenBytes, 4, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return; // 对端关闭
            }
            if (read == 0) return;

            var len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lenBytes);
            if (len == 0 || len > MaxFrameBytes)
            {
                _log?.Invoke($"PipeServer {pipeName}: invalid frame len {len}");
                return;
            }

            var payload = new byte[len];
            try
            {
                read = await ReadExactAsync(server, payload, (int)len, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return;
            }
            if (read != (int)len) return;

            var resp = await DispatchAsync(payload, ct).ConfigureAwait(false);
            var respJson = JsonSerializer.SerializeToUtf8Bytes(resp, JsonOpts);

            // 写响应：[u32 len BE][json]
            //
            // 字节序必须与 PipeContract.cs 的声明一致（多字节整数一律 big-endian，
            // 见 README「协议字节序」）。早期实现写的是 little-endian，
            // 与注释/契约不符 —— 那会让按契约实现的下一个客户端（P8 WPF）静默连不上。
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)respJson.Length);

            await server.WriteAsync(header, ct).ConfigureAwait(false);
            await server.WriteAsync(respJson, ct).ConfigureAwait(false);
            await server.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadExactAsync(Stream s, byte[] buf, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (n == 0) return total;
            total += n;
        }
        return total;
    }

    private async Task<PipeResponse> DispatchAsync(byte[] payload, CancellationToken ct)
    {
        PipeRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<PipeRequest>(payload, JsonOpts);
        }
        catch (JsonException ex)
        {
            return Error("", PipeErrorCode.InvalidParams, $"invalid JSON: {ex.Message}");
        }
        if (req == null)
        {
            return Error("", PipeErrorCode.InvalidParams, "empty request");
        }

        try
        {
            switch (req.Method)
            {
                case "ping":
                    return Ok(req.Id, new PingResult { Pong = true, Version = "P4" });
                case "get_device_info":
                    return Ok(req.Id, _core.GetDeviceInfo());
                case "sign_challenge":
                {
                    var p = ParseParams<SignChallengeParams>(req);
                    if (p?.ChallengeB64 == null) return Error(req.Id, PipeErrorCode.InvalidParams, "challenge_b64 required");
                    var challenge = Convert.FromBase64String(p.ChallengeB64);
                    var result = _core.SignChallenge(challenge);
                    return Ok(req.Id, result);
                }
                case "get_status":
                    return Ok(req.Id, _core.GetStatus());
                case "list_pairings":
                    return Ok(req.Id, _core.ListPairings());
                case "pair":
                {
                    var p = ParseParams<PairParams>(req);
                    if (p?.PeerPubB64 == null) return Error(req.Id, PipeErrorCode.InvalidParams, "peer_pub_b64 required");
                    var peer = Convert.FromBase64String(p.PeerPubB64);
                    _core.Pair(peer, p.Label ?? "");
                    return Ok(req.Id, new { ok = true });
                }
                case "unpair":
                {
                    var p = ParseParams<UnpairParams>(req);
                    if (p?.PeerPubB64 == null) return Error(req.Id, PipeErrorCode.InvalidParams, "peer_pub_b64 required");
                    var peer = Convert.FromBase64String(p.PeerPubB64);
                    _core.Unpair(peer);
                    return Ok(req.Id, new { ok = true });
                }
                case "get_config":
                    return Ok(req.Id, _core.GetConfig());
                case "set_config":
                {
                    var p = ParseParams<SetConfigParams>(req);
                    if (p == null) return Error(req.Id, PipeErrorCode.InvalidParams, "params required");
                    var result = _core.SetConfig(p.RelayUrl, p.DirectPort);
                    return Ok(req.Id, result);
                }
                case "start_agent":
                {
                    var p = ParseParams<StartAgentParams>(req);
                    if (p == null) return Error(req.Id, PipeErrorCode.InvalidParams, "params required");
                    var result = _core.StartAgent(p.Inject, p.NoInject, p.PipeOverride, p.MediaPipe);
                    return Ok(req.Id, result);
                }
                case "stop_agent":
                    _core.StopAgent();
                    return Ok(req.Id, new { ok = true });
                case "direct_dial":
                {
                    // 控制端主动拨号（P5.5 出站）。WPF 客户端"局域网直连"按钮
                    // 走的就是这条 RPC——此前客户端只校验 IP:端口，从不拨号。
                    var p = ParseParams<DirectDialParams>(req);
                    if (p == null) return Error(req.Id, PipeErrorCode.InvalidParams, "params required");
                    if (string.IsNullOrWhiteSpace(p.PeerPubB64))
                        return Error(req.Id, PipeErrorCode.InvalidParams, "peer_pub_b64 required");
                    var dial = await _core.DialDirectAsync(p.PeerPubB64, p.Host, p.Port, ct).ConfigureAwait(false);
                    return Ok(req.Id, dial);
                }
                case "end_session":
                    return Ok(req.Id, _core.EndSession());

                // —— 文件传输（P6）——
                case "file_scope":
                    return Ok(req.Id, _core.GetFileScope());
                case "file_list":
                {
                    var p = ParseParams<FilePathParams>(req);
                    var result = await _core.ListRemoteFilesAsync(p?.Path ?? "", ct).ConfigureAwait(false);
                    return Ok(req.Id, result);
                }
                case "file_upload":
                {
                    var p = ParseParams<FileTransferParams>(req);
                    if (p == null) return Error(req.Id, PipeErrorCode.InvalidParams, "params required");
                    var result = await _core.UploadFileAsync(p.Local, p.Remote, p.Policy, ct).ConfigureAwait(false);
                    return Ok(req.Id, result);
                }
                case "file_download":
                {
                    var p = ParseParams<FileTransferParams>(req);
                    if (p == null) return Error(req.Id, PipeErrorCode.InvalidParams, "params required");
                    var result = await _core.DownloadFileAsync(p.Remote, p.Local, p.Policy, ct).ConfigureAwait(false);
                    return Ok(req.Id, result);
                }
                default:
                    return Error(req.Id, PipeErrorCode.MethodNotFound, $"unknown method: {req.Method}");
            }
        }
        catch (FormatException ex)
        {
            return Error(req.Id, PipeErrorCode.InvalidParams, $"base64 decode: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"PipeServer dispatch error: {ex.GetType().Name}: {ex.Message}");
            return Error(req.Id, PipeErrorCode.InternalError, "internal error");
        }
    }

    private static T? ParseParams<T>(PipeRequest req) where T : class
    {
        if (req.Params == null) return null;
        return req.Params.Value.Deserialize<T>(JsonOpts);
    }

    private static PipeResponse Ok(string id, object result)
    {
        var elem = JsonSerializer.SerializeToElement(result, JsonOpts);
        return new PipeResponse { Id = id, Result = elem };
    }

    private static PipeResponse Error(string id, int code, string message)
        => new() { Id = id, Error = new PipeError { Code = code, Message = message } };

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
