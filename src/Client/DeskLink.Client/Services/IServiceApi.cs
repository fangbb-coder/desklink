using DeskLink.Protocol.Pipe;

namespace DeskLink.Client.Services;

/// <summary>
/// 面向 ViewModel 的 Service 能力门面。
///
/// 为什么要有这层（而不是让 VM 直接用 <see cref="PipeRpcClient"/>）：
///   - VM 只依赖"能力"（typed 方法），不依赖命名管道/JSON/字节序这些传输细节；
///   - 单测里可以塞一个假的 <see cref="IServiceApi"/>，把 VM 的**业务决策**
///     （TOFU 门禁、冲突策略、状态机）与真实管道解耦，测得快且稳定。
/// DTO 一律复用 <c>DeskLink.Protocol.Pipe</c> 的定义，绝不在这里另起一套。
/// </summary>
public interface IServiceApi
{
    Task<PingResult> PingAsync(CancellationToken ct = default);

    Task<DeviceInfoResult> GetDeviceInfoAsync(CancellationToken ct = default);

    Task<StatusResult> GetStatusAsync(CancellationToken ct = default);

    Task<ListPairingsResult> ListPairingsAsync(CancellationToken ct = default);

    Task PairAsync(string peerPubB64, string label, CancellationToken ct = default);

    Task UnpairAsync(string peerPubB64, CancellationToken ct = default);

    Task<GetConfigResult> GetConfigAsync(CancellationToken ct = default);

    Task<SetConfigResult> SetConfigAsync(string? relayUrl, int? directPort, CancellationToken ct = default);

    Task<StartAgentResult> StartAgentAsync(bool inject, bool noInject, string? pipeOverride, CancellationToken ct = default);

    Task StopAgentAsync(CancellationToken ct = default);

    /// <summary>被控端"随时断开"：关闭本机全部 E2E 会话（DESIGN 使用流程第 5 条）。</summary>
    Task<EndSessionResult> EndSessionAsync(CancellationToken ct = default);

    /// <summary>
    /// 控制端主动拨号到局域网对端（P5.5 出站方向）。
    ///
    /// 这是"局域网直连"路径**唯一**真正把连接建立起来的调用：没有它，
    /// 客户端只是校验了 IP:端口然后假装连上了。
    /// </summary>
    Task<DirectDialResultDto> DialDirectAsync(string peerPubB64, string host, int port, CancellationToken ct = default);

    Task<FileScopeResult> GetFileScopeAsync(CancellationToken ct = default);

    Task<FileListResultDto> ListRemoteFilesAsync(string path, CancellationToken ct = default);

    Task<FileTransferResultDto> UploadFileAsync(string local, string remote, string policy, CancellationToken ct = default);

    Task<FileTransferResultDto> DownloadFileAsync(string remote, string local, string policy, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IServiceApi"/> 的默认实现：把 typed 方法翻译成命名管道 RPC。
///
/// 连接策略：**惰性 + 复用**。首次调用时才连（避免 UI 启动即因服务未装而报错），
/// 连接断开后下次调用自动重连一次（Service 重启是常见运维场景）。
/// </summary>
public sealed class ServiceApi : IServiceApi, IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private PipeRpcClient? _client;
    private bool _disposed;

    public ServiceApi(string pipeName, int connectTimeoutMs = 3000)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("pipeName 不能为空", nameof(pipeName));
        _pipeName = pipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public string PipeName => _pipeName;

    /// <summary>拿到（必要时建立）可用连接。</summary>
    private async Task<PipeRpcClient> EnsureAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var existing = _client;
        if (existing is not null && existing.IsConnected) return existing;

        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 双检：等锁期间别的调用可能已经连上。
            if (_client is not null && _client.IsConnected) return _client;
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
            _client = await PipeRpcClient.ConnectAsync(_pipeName, _connectTimeoutMs, ct).ConfigureAwait(false);
            return _client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>调用 + 断线重连重试一次。</summary>
    private async Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken ct)
    {
        var client = await EnsureAsync(ct).ConfigureAwait(false);
        try
        {
            return await client.CallAsync<T>(method, parameters, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 调用方主动取消（如文件传输的"暂停"）。管道是串行请求-响应：
            // 取消时响应可能已在路上/稍后到达，若继续复用这条连接，下一个调用
            // 会读到**上一个请求的旧响应**（id 校验失败或更糟的错位）。
            // 因此连接必须作废，下次调用重建。
            await InvalidateAsync(client).ConfigureAwait(false);
            throw;
        }
        catch (PipeUnavailableException) when (!ct.IsCancellationRequested)
        {
            // 连接在调用中途断掉：丢弃旧连接、重连一次再试。RPC 是幂等读/短操作，
            // 重试一次比让 UI 直接报错体验好；若再失败则如实上抛。
            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_client, client))
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    _client = null;
                }
            }
            finally
            {
                _connectGate.Release();
            }

            var retry = await EnsureAsync(ct).ConfigureAwait(false);
            return await retry.CallAsync<T>(method, parameters, ct).ConfigureAwait(false);
        }
    }

    /// <summary>把指定连接从缓存里摘除并释放（不持锁做 IO）。</summary>
    private async Task InvalidateAsync(PipeRpcClient client)
    {
        await _connectGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
            }
        }
        finally
        {
            _connectGate.Release();
        }

        try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
    }

    private static async Task CallOkAsync(PipeRpcClient c, string method, object? parameters, CancellationToken ct)
    {
        // 服务端对这类方法回 {"ok":true}；我们只关心是否成功（失败会抛结构化错误）。
        await c.CallAsync<OkResult>(method, parameters, ct).ConfigureAwait(false);
    }

    public Task<PingResult> PingAsync(CancellationToken ct = default)
        => InvokeAsync<PingResult>("ping", null, ct);

    public Task<DeviceInfoResult> GetDeviceInfoAsync(CancellationToken ct = default)
        => InvokeAsync<DeviceInfoResult>("get_device_info", null, ct);

    public Task<StatusResult> GetStatusAsync(CancellationToken ct = default)
        => InvokeAsync<StatusResult>("get_status", null, ct);

    public Task<ListPairingsResult> ListPairingsAsync(CancellationToken ct = default)
        => InvokeAsync<ListPairingsResult>("list_pairings", null, ct);

    public async Task PairAsync(string peerPubB64, string label, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct).ConfigureAwait(false);
        await CallOkAsync(client, "pair", new PairParams { PeerPubB64 = peerPubB64, Label = label }, ct).ConfigureAwait(false);
    }

    public async Task UnpairAsync(string peerPubB64, CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct).ConfigureAwait(false);
        await CallOkAsync(client, "unpair", new UnpairParams { PeerPubB64 = peerPubB64 }, ct).ConfigureAwait(false);
    }

    public Task<GetConfigResult> GetConfigAsync(CancellationToken ct = default)
        => InvokeAsync<GetConfigResult>("get_config", null, ct);

    public Task<SetConfigResult> SetConfigAsync(string? relayUrl, int? directPort, CancellationToken ct = default)
        => InvokeAsync<SetConfigResult>("set_config", new SetConfigParams { RelayUrl = relayUrl, DirectPort = directPort }, ct);

    public Task<StartAgentResult> StartAgentAsync(bool inject, bool noInject, string? pipeOverride, CancellationToken ct = default)
        => InvokeAsync<StartAgentResult>("start_agent",
            new StartAgentParams { Inject = inject, NoInject = noInject, PipeOverride = pipeOverride }, ct);

    public async Task StopAgentAsync(CancellationToken ct = default)
    {
        var client = await EnsureAsync(ct).ConfigureAwait(false);
        await CallOkAsync(client, "stop_agent", null, ct).ConfigureAwait(false);
    }

    public Task<EndSessionResult> EndSessionAsync(CancellationToken ct = default)
        => InvokeAsync<EndSessionResult>("end_session", null, ct);

    public Task<DirectDialResultDto> DialDirectAsync(string peerPubB64, string host, int port, CancellationToken ct = default)
        => InvokeAsync<DirectDialResultDto>("direct_dial",
            new DirectDialParams { PeerPubB64 = peerPubB64, Host = host, Port = port }, ct);

    public Task<FileScopeResult> GetFileScopeAsync(CancellationToken ct = default)
        => InvokeAsync<FileScopeResult>("file_scope", null, ct);

    public Task<FileListResultDto> ListRemoteFilesAsync(string path, CancellationToken ct = default)
        => InvokeAsync<FileListResultDto>("file_list", new FilePathParams { Path = path }, ct);

    public Task<FileTransferResultDto> UploadFileAsync(string local, string remote, string policy, CancellationToken ct = default)
        => InvokeAsync<FileTransferResultDto>("file_upload",
            new FileTransferParams { Local = local, Remote = remote, Policy = policy }, ct);

    public Task<FileTransferResultDto> DownloadFileAsync(string remote, string local, string policy, CancellationToken ct = default)
        => InvokeAsync<FileTransferResultDto>("file_download",
            new FileTransferParams { Local = local, Remote = remote, Policy = policy }, ct);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
        _connectGate.Dispose();
    }

    /// <summary>服务端 <c>{"ok":true}</c> 响应的反序列化目标。</summary>
    private sealed class OkResult
    {
        public bool Ok { get; set; }
    }
}
