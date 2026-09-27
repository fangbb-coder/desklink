// 中继客户端：管理到 relayd 的出站连接。
//
// 设计：
//   - 启动后开启一次循环：探测 QUIC → 不可用则回落 TCP/TLS。
//   - 每次连接：探测 → 传输握手 → 上层握手（P5 接入 SIGMA）。
//   - 任何步骤失败：退避 → 重新探测。
//   - 每次重连都是"完整重握手"（DESIGN.md 决策）。
//
// P4 范围：
//   - 传输层就绪（QUIC + TCP/TLS）。
//   - 退避循环就绪。
//   - 传输握手后向 OnReady 回调汇报，由 ServiceHost/上层决定做什么。
//   - 业务握手（SIGMA + 设备挑战 + 接线）留到 P5。

using System.Net;

namespace DeskLink.Service.Relay;

public sealed class RelayClient : IAsyncDisposable
{
    private readonly Uri _relayUrl;
    private readonly BackoffPolicy _backoff;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private RelayState _state = RelayState.Disconnected;

    public enum RelayState
    {
        Disconnected,
        Connecting,
        Connected,
        BackingOff,
        Stopped,
    }

    public sealed class RelayStateUpdate
    {
        public RelayState State { get; init; }
        public RelayTransportKind? Transport { get; init; }
        public string? Detail { get; init; }
        public int Attempt { get; init; }
    }

    public RelayState State => _state;
    public Uri RelayUrl => _relayUrl;

    /// <summary>
    /// 状态上报回调。允许在 Start 之前/之后赋值；多次赋值取最后一个。
    /// 单独暴露此属性是为了让 ServiceCore（DI 注入后构造）的回调不必通过
    /// RelayClient 构造器参数反向耦合。
    /// </summary>
    public Action<RelayStateUpdate>? OnState { get; set; }

    /// <summary>
    /// 传输就绪回调（P4/P5）：传输握手完成后调用，由上层完成
    /// 控制握手 → SIGMA → 加密会话收发（见 RelaySessionRunner）。
    ///
    /// 返回值语义：**本次传输期间是否成功建立过会话**。
    /// 用于决定是否重置退避计数——只在真正建立过会话时重置，否则
    /// "连接成功但握手一直失败"（例如未配对、对端始终离线）会每次都把退避
    /// 打回第一档，变成约 1 次/秒的无限重连风暴。
    ///
    /// 与 OnState 同理，做成可赋值属性而非构造器参数，方便 DI 在装配后注入，
    /// 避免 RelayClient 反向依赖业务层类型。
    /// 回调返回即视为上层主动结束本次会话，RelayClient 会退避后重连。
    /// </summary>
    public Func<IRelayTransport, CancellationToken, Task<bool>>? OnReady { get; set; }

    /// <summary>
    /// 中继证书信任策略（P5 TOFU）。
    ///
    /// 生产必须注入 RelayTrustPolicy（首次记录指纹、之后必须一致）；
    /// 只有测试或用户显式关闭 TOFU 时才用 RelayTrustPolicy.AcceptAny。
    /// 默认值是 AcceptAny 仅为兼容早期测试调用点——装配处（Program.cs）总是显式注入。
    /// </summary>
    public IRelayTrustPolicy Trust { get; set; } = RelayTrustPolicy.AcceptAny;

    public RelayClient(
        Uri relayUrl,
        BackoffPolicy? backoff = null,
        Func<IRelayTransport, CancellationToken, Task<bool>>? onReady = null,
        IRelayTrustPolicy? trust = null,
        Action<string>? log = null)
    {
        _relayUrl = relayUrl ?? throw new ArgumentNullException(nameof(relayUrl));
        _backoff = backoff ?? new BackoffPolicy();
        OnReady = onReady;
        if (trust != null) Trust = trust;
        _log = log;
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_loop != null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }
        SetState(RelayState.Stopped);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IRelayTransport? transport = null;
            try
            {
                SetState(RelayState.Connecting);
                transport = OpenTransport(_relayUrl);
                await transport.ConnectAsync(_relayUrl, ct).ConfigureAwait(false);

                SetState(RelayState.Connected, transport.Kind, "transport ready");

                if (OnReady != null)
                {
                    // 业务握手 + 接线 + 收发循环；返回时说明上层主动结束。
                    // 返回 true 才重置退避：连接成功但会话一直建不起来时，
                    // 退避必须继续增长（否则变成 1 次/秒的重连风暴）。
                    var established = await OnReady(transport, ct).ConfigureAwait(false);
                    if (established)
                    {
                        _backoff.Reset();
                    }
                }
                else
                {
                    // 无上层回调：等待连接被对端关闭 / 取消
                    var buf = new byte[1];
                    while (!ct.IsCancellationRequested)
                    {
                        var n = await transport.ReceiveAsync(buf, ct).ConfigureAwait(false);
                        if (n == 0) break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                SetState(RelayState.Stopped);
                if (transport != null) await transport.CloseAsync().ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                SetState(RelayState.BackingOff, null, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (transport != null)
                {
                    await transport.CloseAsync().ConfigureAwait(false);
                }
            }

            if (ct.IsCancellationRequested) break;

            var delay = _backoff.NextDelayMs();
            SetState(RelayState.BackingOff, null, $"retry in {delay} ms (attempt {_backoff.Attempt})");
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        SetState(RelayState.Stopped);
    }

    /// <summary>
    /// 按 relayUrl 的 scheme 选择传输实现。
    ///
    /// scheme 契约（与 relayd 的双监听对齐）：
    ///   quic://    —— **强制 QUIC**。QUIC 不可用时直接抛错，不静默回落。
    ///   https://   —— 优先 QUIC，不可用则回落 TCP/TLS（"443 同端口双协议"的默认语义）。
    ///   tls://     —— **强制 TCP/TLS**（显式声明，便于排查/联调）。
    ///
    /// 为什么 quic:// 不回落：若在这里静默换成 TcpTlsTransport，它会因为 scheme 不是
    /// tls:// 而抛 "expects tls:// or https://"，最终只看到一句与根因无关的报错，
    /// 然后进入退避重连循环。显式 scheme 应当得到显式结论。
    /// </summary>
    private IRelayTransport OpenTransport(Uri relay)
    {
        return SelectTransportKind(relay, QuicTransport.IsAvailable()) switch
        {
            RelayTransportKind.Quic => new QuicTransport(Trust, _log),
            _ => new TcpTlsTransport(Trust, _log),
        };
    }

    /// <summary>
    /// 纯函数形式的"scheme + 环境 → 传输类型"决策（便于单测覆盖整张决策表）。
    ///
    /// 抛 <see cref="NotSupportedException"/> 表示该 scheme 在当前环境不可用，
    /// 且调用方**不应**静默降级。
    /// </summary>
    public static RelayTransportKind SelectTransportKind(Uri relay, bool quicAvailable)
    {
        ArgumentNullException.ThrowIfNull(relay);

        switch (relay.Scheme)
        {
            case "quic":
                if (!quicAvailable)
                {
                    throw new NotSupportedException(
                        "relayUrl uses quic:// but QUIC is unavailable on this machine " +
                        "(needs Windows 11 build 20000+ / Linux / macOS). " +
                        "Use tls:// to force TCP/TLS, or https:// to allow fallback.");
                }
                return RelayTransportKind.Quic;

            case "https":
                // 未显式指定协议：QUIC 优先，不可用回落 TCP/TLS。
                return quicAvailable ? RelayTransportKind.Quic : RelayTransportKind.TcpTls;

            case "tls":
                return RelayTransportKind.TcpTls;

            default:
                throw new NotSupportedException(
                    $"unsupported relayUrl scheme '{relay.Scheme}': use quic://, tls:// or https://");
        }
    }

    private void SetState(RelayState state, RelayTransportKind? transport = null, string? detail = null)
    {
        _state = state;
        OnState?.Invoke(new RelayStateUpdate
        {
            State = state,
            Transport = transport,
            Detail = detail,
            Attempt = _backoff.Attempt,
        });
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
