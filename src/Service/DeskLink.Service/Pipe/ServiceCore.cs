// IServiceCore 的真实实现：装配 KeyStore / PairingStore / AgentLauncher / FirewallHelper / RelayClient。
//
// 生命周期：
//   - 由 ServiceHost 构造时单例注入
//   - 所有命令都是同步可执行（不阻塞 I/O）；握手/连接状态由 RelayClient 异步上报
//
// 状态：
//   - relay_state：缓存 RelayClient 上次报告
//   - agent_pid：缓存 AgentLauncher 上次启动结果
using DeskLink.Protocol.Handshake;
using DeskLink.Service.Configuration;
using DeskLink.Service.Direct;
using DeskLink.Service.FileTransfer;
using DeskLink.Service.Pipe;
using DeskLink.Service.Process;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;

using DeskLink.Protocol.Pipe;

namespace DeskLink.Service;

public sealed class ServiceCore : IServiceCore
{
    private readonly KeyStore _keyStore;
    private readonly PairingStore _pairings;
    private readonly AgentLauncher _agent;
    private readonly FirewallHelper _firewall;
    private readonly ServiceOptions _options;
    private readonly RelayClient? _relay;
    private readonly RelaySessionRunner? _runner;
    private readonly DirectServer? _direct;
    private readonly DirectDialer? _dialer;
    private readonly Media.MediaPipeServer? _media;
    private readonly Action<string>? _log;
    private volatile string _relayState = "disconnected";

    /// <summary>
    /// 启动时快照的"进程真正在用的"中继地址。
    ///
    /// RelayClient 在 Program.BuildHost 阶段按 options.RelayUrl 一次性构造，
    /// 之后 <see cref="SetConfig"/> 改 <c>_options.RelayUrl</c> 不会重建连接。
    /// 保留这个快照是为了让 GetConfig/GetStatus 能如实回答"现在连的是哪个中继"，
    /// 而不是在用户改过配置后谎报新值。
    /// </summary>
    private readonly string? _activeRelayUrl;

    public ServiceCore(
        KeyStore keyStore,
        PairingStore pairings,
        AgentLauncher agent,
        FirewallHelper firewall,
        ServiceOptions options,
        RelayClient? relay,
        RelaySessionRunner? runner,
        DirectServer? direct,
        DirectDialer? dialer,
        Media.MediaPipeServer? media,
        Action<string>? log)
    {
        _keyStore = keyStore;
        _pairings = pairings;
        _agent = agent;
        _firewall = firewall;
        _options = options;
        _relay = relay;
        _runner = runner;
        _direct = direct;
        _dialer = dialer;
        _media = media;
        _log = log;
        _activeRelayUrl = options.RelayUrl?.ToString();

        if (_relay != null)
        {
            _relayClientSub = update =>
            {
                _relayState = update.State.ToString().ToLowerInvariant();
                _log?.Invoke($"RelayClient state={_relayState} detail={update.Detail}");
            };
            _relay.OnState = _relayClientSub;
        }
    }

    private readonly Action<RelayClient.RelayStateUpdate>? _relayClientSub;

    public DeviceInfoResult GetDeviceInfo()
    {
        var keys = _keyStore.LoadOrCreate();
        return new DeviceInfoResult
        {
            Ed25519PubB64 = Convert.ToBase64String(keys.KeyPair.Ed25519Public),
            X25519PubB64 = Convert.ToBase64String(keys.KeyPair.X25519Public),
            DeviceIdHintB64 = Convert.ToBase64String(keys.DeviceIdHint),
            CreatedUtc = keys.CreatedUtc,
        };
    }

    public SignChallengeResult SignChallenge(ReadOnlySpan<byte> challenge)
    {
        var keys = _keyStore.LoadOrCreate();
        var sig = keys.KeyPair.Sign(challenge);
        return new SignChallengeResult
        {
            SignatureB64 = Convert.ToBase64String(sig),
        };
    }

    public StatusResult GetStatus()
    {
        return new StatusResult
        {
            RelayState = _relayState,
            DirectEnabled = _firewall.QueryEnabled(_options.DirectPort),
            MediaClientConnected = _media?.GetStatus().ClientConnected ?? false,
            MediaAgentConnected = _media?.GetStatus().AgentConnected ?? false,
            MediaFramesToClient = _media?.GetStatus().FramesToClient ?? 0,
            MediaDroppedToClient = _media?.GetStatus().DroppedToClient ?? 0,
            MediaClientQueueDepth = _media?.GetStatus().ClientQueueDepth ?? 0,
            PairingCount = _pairings.Count,
            InstanceId = _options.InstanceId,
            ConsoleMode = _options.ConsoleMode,
            // E2E 会话状态（P5）：由 RelaySessionRunner 维护。
            E2EState = _runner?.State ?? "idle",
            E2EPeerDeviceId = _runner?.PeerDeviceId is { } id ? Convert.ToHexString(id) : null,
            E2EControlRoundTripOk = _runner?.ControlRoundTripOk ?? false,
            E2ERttMs = _runner?.LastRttMs ?? -1,
            // 直连活跃会话 = 被控端入站会话（DirectServer）+ 控制端出站会话（DirectDialer）。
            // 客户端据此判断"远程页该等中继会话还是等直连会话"。
            DirectActiveSessions = AllDirectSessions().Count,
        };
    }

    /// <summary>本机全部直连会话：入站（被控端角色）+ 出站（控制端角色）。</summary>
    private List<DirectSession> AllDirectSessions()
    {
        var all = new List<DirectSession>();
        if (_direct is not null) all.AddRange(_direct.Sessions);
        if (_dialer is not null) all.AddRange(_dialer.Sessions);
        return all;
    }

    public ListPairingsResult ListPairings()
    {
        var list = _pairings.List();
        return new ListPairingsResult
        {
            Pairings = list.Select(e => new PairingInfo
            {
                PeerPubB64 = e.PeerPubB64,
                Label = e.Label,
                PairedUtc = e.PairedUtc,
            }).ToList(),
        };
    }

    public void Pair(ReadOnlySpan<byte> peerPub, string label)
    {
        _pairings.Add(peerPub, label);
        _log?.Invoke($"ServiceCore: paired peer ({peerPub.Length} bytes) label='{label}'");
    }

    public void Unpair(ReadOnlySpan<byte> peerPub)
    {
        var removed = _pairings.Remove(peerPub);
        _log?.Invoke($"ServiceCore: unpair peer ({peerPub.Length} bytes) removed={removed}");

        // 撤销路径：DESIGN.md 决策 #7 + 风险回顾 #5 —— 撤销立即踢线 + 清除配对关系，
        // 该设备再次接入必须重新输入新配对码。
        //
        //   1) 局域网直连：被控端本地就能判定——按对端 device_id 关闭活跃会话。
        //      （直连握手用已配对公钥，配对关系被移除后新连接在握手前即被拒。）
        //   2) 中继：本端关闭到 relay 的传输，RelayClient 退避后重连；重连时
        //      relay 查 registry 已无配对 → 拒绝接线。这保证了"撤销使相关会话失效"。
        if (removed)
        {
            var peerDeviceId = E2ESessionHost.ComputeDeviceId(peerPub);
            // 入站（被控端角色）与出站（控制端角色）都要踢——
            // 否则本机主动拨出去的那条会话会继续活着，撤销形同虚设。
            var closedIn = _direct?.CloseSessionsFor(peerDeviceId) ?? 0;
            var closedOut = _dialer?.CloseSessionsFor(peerDeviceId) ?? 0;
            _log?.Invoke($"ServiceCore: revoked peer device_id={Convert.ToHexString(peerDeviceId)[..16]}..., " +
                         $"closed {closedIn} inbound + {closedOut} outbound direct session(s)");
            CloseRelaySession();
        }
    }

    /// <summary>
    /// 关闭当前中继加密会话（撤销时调用）。
    ///
    /// 实现方式：释放 RelaySessionRunner 持有的泵 → 读循环退出 → OnTransportReadyAsync
    /// 返回 → RelayClient 关闭传输并退避重连；重连时 relay 会因 registry 已无配对而拒绝。
    /// </summary>
    private void CloseRelaySession()
    {
        if (_runner is null) return;
        // 不阻塞管道请求线程：释放是异步的（最多等 3s 收尾）。
        _ = Task.Run(async () =>
        {
            try
            {
                await _runner.DisposeAsync().ConfigureAwait(false);
                _log?.Invoke("ServiceCore: relay session closed after revocation");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"ServiceCore: relay session close failed {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public GetConfigResult GetConfig()
    {
        return new GetConfigResult
        {
            DataDir = _options.DataDir,
            RelayUrl = _options.RelayUrl?.ToString(),
            ActiveRelayUrl = _activeRelayUrl,
            DirectPort = _options.DirectPort,
            DirectEnabled = _firewall.QueryEnabled(_options.DirectPort),
        };
    }

    public SetConfigResult SetConfig(string? relayUrl, int? directPort)
    {
        var oldPort = _options.DirectPort;
        var oldRelay = _options.RelayUrl?.ToString();
        var changed = false;

        // 中继地址改了 → 标注"需重启"。
        //
        // 原因（不要在这里"顺手"重建连接）：RelayClient 持有到 relay 的传输、
        // 信任策略（TOFU pin）与退避循环；在一个进行中的 E2E 会话里换掉它，
        // 会话、文件传输与媒体通道都要跟着重建，失败面远大于收益。DESIGN 也
        // 允许"路径由用户显式选择"。因此诚实做法是：存下来 + 明确告诉用户
        // 什么时候生效，而不是弹"已保存"让人以为已经连上新中继。
        var requiresRestart = false;
        if (relayUrl != null)
        {
            if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var u))
            {
                throw new ArgumentException("relayUrl invalid");
            }
            if (u.ToString() != oldRelay)
            {
                _options.RelayUrl = u;
                changed = true;
                requiresRestart = true;
            }
        }

        // 直连端口是**立即生效**的：FirewallHelper.ApplyPortChange 真的删旧建新。
        var firewallRepaired = false;
        if (directPort.HasValue && directPort.Value != oldPort)
        {
            _options.DirectPort = directPort.Value;
            firewallRepaired = _firewall.ApplyPortChange(oldPort, directPort.Value);
            changed = true;
        }

        return new SetConfigResult
        {
            Ok = changed,
            FirewallRepaired = firewallRepaired,
            RequiresRestart = requiresRestart,
            RestartHint = requiresRestart
                ? "中继地址已保存，但**需重启 DeskLinkService 后才会生效**（当前进程仍在使用旧地址）。"
                : null,
        };
    }

    /// <summary>
    /// 控制端主动拨号到局域网对端（P5.5 出站方向）。
    ///
    /// 对端公钥用 base64 传入：与 pair / unpair 同一表示，便于客户端直接复用
    /// 设备列表里的 <c>PeerPubB64</c>，不必在 UI 侧再做一次 base64↔device_id 换算。
    /// </summary>
    public async Task<DirectDialResultDto> DialDirectAsync(
        string peerPubB64, string host, int port, CancellationToken ct = default)
    {
        if (_dialer is null)
        {
            return new DirectDialResultDto
            {
                Ok = false,
                Detail = "本 Service 未装配出站拨号器，无法发起局域网直连",
            };
        }

        byte[] peerDeviceId;
        try
        {
            var pub = Convert.FromBase64String(peerPubB64);
            if (pub.Length != 32)
            {
                return new DirectDialResultDto { Ok = false, Detail = "对端公钥长度非法（应为 32 字节）" };
            }
            peerDeviceId = E2ESessionHost.ComputeDeviceId(pub);
        }
        catch (FormatException)
        {
            return new DirectDialResultDto { Ok = false, Detail = "对端公钥不是合法 base64" };
        }

        var outcome = await _dialer.DialAsync(host, port, peerDeviceId, ct).ConfigureAwait(false);
        return new DirectDialResultDto
        {
            Ok = outcome.Ok,
            Detail = outcome.Detail,
            Transport = outcome.Transport,
        };
    }

    public StartAgentResult StartAgent(bool inject, bool noInject, string? pipeOverride, string? mediaPipe = null)
    {
        // AgentInjectSafetyException 会被 PipeServer 包成 InternalError；
        // 但按 DESIGN.md 风险回顾 #4 要求"启动 Agent 未传参则退出非零"——本类在
        // 真实服务模式（--console 缺省时）下也由 Program 决定如何处理。
        // P4 范围：把抛错原样传到管道响应方；P8 UI 接到后弹窗或拒绝。
        // 未显式指定时用本实例约定的媒体管道名（与 PipeServer 的命名规则一致）。
        var media = mediaPipe ?? _media?.AgentPipeName;
        var r = _agent.Start(
            inject, noInject, pipeOverride, media,
            monitorIndex: _options.CaptureMonitorIndex,
            rotation: _options.CaptureRotation,
            fps: _options.CaptureFps,
            bitrateBps: _options.CaptureBitrateBps);
        return new StartAgentResult { Pid = r.Pid, StubMode = r.StubMode };
    }

    public void StopAgent()
    {
        _agent.Stop();
    }

    /// <summary>
    /// 被控端"随时断开"（DESIGN 使用流程第 5 条）：
    /// 关闭本机全部 E2E 会话——直连同步关闭（返回关掉的数量），中继走
    /// CloseRelaySession（释放 runner 泵 → RelayClient 关闭传输并退避重连）。
    /// 无论本端当前是控制方还是被控方，调用都成立（控制方调它等于主动收线）。
    /// </summary>
    public EndSessionResult EndSession()
    {
        var closedIn = _direct?.CloseAllSessions() ?? 0;
        var closedOut = _dialer?.CloseAllSessions() ?? 0;
        _log?.Invoke($"ServiceCore: end_session closed {closedIn} inbound + {closedOut} outbound direct session(s)");
        CloseRelaySession();
        return new EndSessionResult { Ok = true, Closed = closedIn + closedOut };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 文件传输（P6）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 取当前可用的文件传输引擎：优先中继会话，其次任一直连会话
    /// （入站与出站都算——控制端主动拨出的会话同样可以传文件）。
    /// 两者共用同一份实现与同一套 scope（DESIGN：两条路径业务协议复用）。
    /// </summary>
    private FileTransferEngine? ActiveFileEngine()
    {
        var relayEngine = _runner?.FileEngine;
        if (relayEngine is not null) return relayEngine;

        foreach (var s in AllDirectSessions())
        {
            if (s.FileEngine is not null) return s.FileEngine;
        }
        return null;
    }

    public FileScopeResult GetFileScope() => new() { Roots = _options.FileScopeRoots.ToList() };

    public async Task<FileListResultDto> ListRemoteFilesAsync(string path, CancellationToken ct = default)
    {
        var engine = ActiveFileEngine();
        if (engine is null)
        {
            return new FileListResultDto { Ok = false, Error = "当前没有已建立的会话（中继或局域网直连）" };
        }
        var result = await engine.ListAsync(path ?? "", ct).ConfigureAwait(false);
        return new FileListResultDto
        {
            Ok = result.Ok,
            Error = result.Error,
            Entries = result.Entries.Select(e => new FileListEntryDto
            {
                Name = e.Name,
                IsDirectory = e.IsDirectory,
                Size = e.Size,
                ModifiedUnixMs = e.ModifiedUnixMs,
            }).ToList(),
        };
    }

    public async Task<FileTransferResultDto> UploadFileAsync(
        string local, string remote, string policy, CancellationToken ct = default)
    {
        var engine = ActiveFileEngine();
        if (engine is null)
        {
            return new FileTransferResultDto { Ok = false, Error = "当前没有已建立的会话（中继或局域网直连）" };
        }
        var outcome = await engine.UploadAsync(local, remote, ParsePolicy(policy), ct).ConfigureAwait(false);
        return ToDto(outcome);
    }

    public async Task<FileTransferResultDto> DownloadFileAsync(
        string remote, string local, string policy, CancellationToken ct = default)
    {
        var engine = ActiveFileEngine();
        if (engine is null)
        {
            return new FileTransferResultDto { Ok = false, Error = "当前没有已建立的会话（中继或局域网直连）" };
        }
        var outcome = await engine.DownloadAsync(remote, local, ParsePolicy(policy), ct).ConfigureAwait(false);
        return ToDto(outcome);
    }

    private static FileTransferResultDto ToDto(FileTransferOutcome o) => new()
    {
        Ok = o.Ok,
        Bytes = o.BytesTransferred,
        Target = o.TargetPath,
        Error = o.Error,
    };

    private static FileConflictPolicy ParsePolicy(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "rename" => FileConflictPolicy.Rename,
        "skip" => FileConflictPolicy.Skip,
        _ => FileConflictPolicy.Overwrite,
    };
}
