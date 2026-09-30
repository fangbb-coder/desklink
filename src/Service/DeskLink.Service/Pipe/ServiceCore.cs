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
            FileScopeRoots = _options.FileScopeRoots.ToList(),
            CaptureMonitorIndex = _options.CaptureMonitorIndex,
        };
    }

    public SetConfigResult SetConfig(
        string? relayUrl,
        int? directPort,
        IReadOnlyList<string>? fileScopeRoots = null,
        int? monitorIndex = null)
    {
        var oldPort = _options.DirectPort;
        var oldRelay = _options.RelayUrl?.ToString();
        var changed = false;

        // ── 全部入参先校验，再动任何状态 ──
        //
        // 顺序有讲究：校验一旦放在副作用之后，一次非法调用就会留下半套配置。
        // 例如 fileScopeRoots 合法 + monitorIndex = -1：授权目录已经改进 _options 了，
        // 下一个参数才抛异常，而异常在管道层被吞成 "internal error" ——
        // 用户看到的是一句没头没尾的报错，内存里的授权目录却已经变了，还没落盘。
        if (relayUrl != null && !Uri.TryCreate(relayUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException("relayUrl invalid");
        }
        if (monitorIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(monitorIndex), "monitorIndex must be >= 0");
        }

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
            var u = new Uri(relayUrl, UriKind.Absolute);   // 上面已校验可解析
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

        // 授权目录与捕获显示器索引：**立即生效 + 落盘**。
        //
        // 落盘是修复"`--file-scope` 不持久化"的那一半：原先它只活在命令行里，
        // 换个方式启动服务（不带该参数）授权就静默清空，文件功能无声失效。
        // 另一半是 CommandLineParser 的两遍扫描——下次启动把这里写的值读回来。
        var persistedOk = true;
        if (fileScopeRoots is not null)
        {
            var normalized = fileScopeRoots
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => Path.GetFullPath(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!normalized.SequenceEqual(_options.FileScopeRoots, StringComparer.OrdinalIgnoreCase))
            {
                _options.FileScopeRoots = normalized;
                changed = true;
                requiresRestart = true;   // 已在跑的会话持有旧 FileTransferScope
            }
        }
        if (monitorIndex is not null && monitorIndex.Value != _options.CaptureMonitorIndex)
        {
            _options.CaptureMonitorIndex = monitorIndex.Value;
            changed = true;
            requiresRestart = true;   // Agent 已经带着旧索引起来了
        }

        if (fileScopeRoots is not null || monitorIndex is not null)
        {
            var store = new ServiceConfig
            {
                FileScopeRoots = _options.FileScopeRoots.ToList(),
                CaptureMonitorIndex = _options.CaptureMonitorIndex,
            };
            persistedOk = store.Save(_options.DataDir);
            if (!persistedOk)
            {
                _log?.Invoke("ServiceCore: service.json 写入失败，授权目录/显示器索引本次运行仍按内存值执行");
            }
        }

        return new SetConfigResult
        {
            Ok = true,          // 走到这里就说明参数合法、没失败。值有没有变看 Changed。
            Changed = changed,  // 存了和当前一样的值也是成功，别让客户端把它当失败。
            FirewallRepaired = firewallRepaired,
            RequiresRestart = requiresRestart,
            Persisted = persistedOk,
            RestartHint = BuildRestartHint(requiresRestart, persistedOk, fileScopeRoots is not null || monitorIndex is not null),
        };
    }

    private static string? BuildRestartHint(bool requiresRestart, bool persisted, bool touchedPersistentFields)
    {
        var parts = new List<string>();

        // 落盘失败和"要不要重启"是两件独立的事。原先这里 !requiresRestart 就直接 return null，
        // 于是"值没变 + 写盘失败"会走成一句"已保存"——用户以为以后都记住了，其实下次启动就退回旧值。
        if (!persisted)
        {
            parts.Add("⚠ **未能落盘**到 data-dir\\service.json（目录不可写？）——" +
                      "本次运行已按新值执行，但**下次启动会退回旧值**。");
        }

        if (requiresRestart)
        {
            if (touchedPersistentFields)
            {
                parts.Add("文件授权目录 / 捕获显示器索引需要**重启 DeskLinkService 后才会对已建立的会话生效**。");
            }
            parts.Add("中继地址同样需重启后才会生效（当前进程仍在使用旧地址）。");
        }

        return parts.Count == 0 ? null : string.Join("\n", parts);
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

    /// <summary>
    /// 本机在途传输的真实分块进度（供客户端在等长调用返回期间轮询）。
    ///
    /// 引擎里本来就有 <c>Snapshot()</c>，此前只是没人问它——所以界面上的进度条
    /// 只能画 0% → 100%。这里原样透出：没有会话时返回空列表而不是错误，
    /// 因为"现在没有进度"是常态，不是故障。
    /// </summary>
    public FileProgressResult GetFileProgress()
    {
        var result = new FileProgressResult();
        var engine = ActiveFileEngine();
        if (engine is null) return result;

        foreach (var p in engine.Snapshot())
        {
            result.Transfers.Add(new FileProgressEntryDto
            {
                TransferId = p.TransferId,
                Direction = p.Direction == TransferDirection.Sending ? "sending" : "receiving",
                Path = p.Path,
                TotalBytes = p.TotalBytes,
                TransferredBytes = p.TransferredBytes,
                Percent = p.Percent,
                State = p.State,
            });
        }
        return result;
    }

    private static FileConflictPolicy ParsePolicy(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "rename" => FileConflictPolicy.Rename,
        "skip" => FileConflictPolicy.Skip,
        _ => FileConflictPolicy.Overwrite,
    };
}
