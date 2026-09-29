using System.Collections.ObjectModel;
using DeskLink.Client.Services;
using DeskLink.Protocol.Pipe;

namespace DeskLink.Client.ViewModels;

/// <summary>中继地址校验结果。</summary>
public readonly record struct RelayUrlValidation(bool IsValid, string Error)
{
    public static RelayUrlValidation Ok => new(true, "");
    public static RelayUrlValidation Fail(string error) => new(false, error);
}

/// <summary>
/// 设置页视图模型：中继地址、直连端口/开关、文件授权根、桌面代理开关，
/// 以及**中继证书指纹的首次连接确认（TOFU）**。
///
/// 为什么中继证书也要 TOFU：中继链路虽是 TLS，但自签/自建证书默认不被系统信任；
/// 客户端必须记住首次看到的证书指纹，之后要求一致，否则无法发现中继被替换。
/// 与对端设备指纹（<see cref="Security.PeerFingerprintStore"/>）是**两回事**：
/// 前者信任的是"中继服务器"，后者信任的是"对端设备"。
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private static readonly string[] AllowedSchemes = { "https", "quic", "tls" };

    private readonly IServiceApi _api;

    private string _relayUrl = "";
    private int _directPort = 47200;
    private bool _directEnabled;
    private bool _agentRunning;
    private string _statusMessage = "";
    private string _relayCertificateFingerprint = "";
    private bool _relayCertificateConfirmed;
    private bool _busy;
    private bool _pendingRestart;
    private string _activeRelayUrl = "";

    public SettingsViewModel(IServiceApi api)
    {
        _api = api;

        LoadCommand = new RelayCommand(() => _ = LoadAsync());
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        StartAgentCommand = new RelayCommand(() => _ = StartAgentAsync(inject: true, noInject: false));
        StopAgentCommand = new RelayCommand(() => _ = StopAgentAsync());
    }

    public RelayCommand LoadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand StartAgentCommand { get; }
    public RelayCommand StopAgentCommand { get; }

    /// <summary>展示中继证书指纹并等待用户确认的回调。</summary>
    public Func<string, Task<bool>>? RelayCertConfirmer { get; set; }

    public ObservableCollection<string> FileScopeRoots { get; } = new();

    public string RelayUrl
    {
        get => _relayUrl;
        set
        {
            if (SetProperty(ref _relayUrl, value))
            {
                OnPropertyChanged(nameof(RelayUrlValidation));
                OnPropertyChanged(nameof(IsRelayUrlValid));
                OnPropertyChanged(nameof(HasPendingRelayChange));
            }
        }
    }

    /// <summary>当前中继地址的校验结果（供 UI 即时提示）。</summary>
    public RelayUrlValidation RelayUrlValidation => ValidateRelayUrl(_relayUrl);

    public bool IsRelayUrlValid => RelayUrlValidation.IsValid;

    /// <summary>
    /// 进程**真正在用**的中继地址（Service 启动时快照）。
    ///
    /// 当它与 <see cref="RelayUrl"/> 不一致时，说明用户改了配置但还没重启服务——
    /// UI 必须把这件事显示出来，否则用户以为已经在连新中继。
    /// </summary>
    public string ActiveRelayUrl
    {
        get => _activeRelayUrl;
        private set
        {
            if (SetProperty(ref _activeRelayUrl, value))
            {
                OnPropertyChanged(nameof(HasPendingRelayChange));
            }
        }
    }

    /// <summary>已保存但尚未生效（需重启服务）。</summary>
    public bool PendingRestart
    {
        get => _pendingRestart;
        private set => SetProperty(ref _pendingRestart, value);
    }

    /// <summary>输入框里的地址与实际在用地址不一致（配置已存、连接未换）。</summary>
    public bool HasPendingRelayChange =>
        !string.IsNullOrWhiteSpace(ActiveRelayUrl)
        && !string.Equals(ActiveRelayUrl.Trim(), (RelayUrl ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    public int DirectPort
    {
        get => _directPort;
        set
        {
            if (SetProperty(ref _directPort, value))
            {
                OnPropertyChanged(nameof(IsDirectPortValid));
            }
        }
    }

    public bool IsDirectPortValid => _directPort is >= DirectEndpoint.MinPort and <= DirectEndpoint.MaxPort;

    public bool DirectEnabled
    {
        get => _directEnabled;
        set => SetProperty(ref _directEnabled, value);
    }

    public bool AgentRunning
    {
        get => _agentRunning;
        private set => SetProperty(ref _agentRunning, value);
    }

    /// <summary>中继证书指纹（SHA-256 分组十六进制）。</summary>
    public string RelayCertificateFingerprint
    {
        get => _relayCertificateFingerprint;
        private set => SetProperty(ref _relayCertificateFingerprint, value);
    }

    /// <summary>中继证书是否已确认（首次连接必须确认后才信任）。</summary>
    public bool RelayCertificateConfirmed
    {
        get => _relayCertificateConfirmed;
        private set => SetProperty(ref _relayCertificateConfirmed, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    /// <summary>
    /// 校验中继地址：必须带 <c>https://</c>/<c>quic://</c>/<c>tls://</c> 之一且含主机名。
    /// scheme 决定传输（见 README：quic 强制 QUIC，tls 强制 TCP/TLS，https 优先 QUIC 可回落）。
    /// </summary>
    public static RelayUrlValidation ValidateRelayUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return RelayUrlValidation.Fail("中继地址不能为空");
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            return RelayUrlValidation.Fail("中继地址格式非法，应为 scheme://host[:port]");
        }

        if (!AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return RelayUrlValidation.Fail($"不支持的 scheme '{uri.Scheme}'，应为 https/quic/tls");
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return RelayUrlValidation.Fail("中继地址缺少主机名");
        }

        return RelayUrlValidation.Ok;
    }

    /// <summary>拉取配置与状态。</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var cfg = await _api.GetConfigAsync(ct);
            RelayUrl = cfg.RelayUrl ?? "";
            ActiveRelayUrl = cfg.ActiveRelayUrl ?? "";
            DirectPort = cfg.DirectPort;
            DirectEnabled = cfg.DirectEnabled;

            var scope = await _api.GetFileScopeAsync(ct);
            FileScopeRoots.Clear();
            foreach (var r in scope.Roots) FileScopeRoots.Add(r);

            var status = await _api.GetStatusAsync(ct);
            AgentRunning = status.E2EState is "handshaking" or "sigma" or "established";
            PendingRestart = HasPendingRelayChange;
            StatusMessage = PendingRestart
                ? $"已加载配置。注意：当前进程仍在使用 {ActiveRelayUrl}，输入框里的地址需重启服务后才生效。"
                : "已加载配置";
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法加载配置";
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"加载配置失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>保存中继地址与直连端口。地址非法时拒绝保存。</summary>
    public async Task<bool> SaveAsync(CancellationToken ct = default)
    {
        var validation = ValidateRelayUrl(RelayUrl);
        if (!validation.IsValid)
        {
            StatusMessage = validation.Error;
            return false;
        }
        if (!IsDirectPortValid)
        {
            StatusMessage = $"直连端口必须在 {DirectEndpoint.MinPort}..{DirectEndpoint.MaxPort} 之间";
            return false;
        }

        Busy = true;
        try
        {
            var result = await _api.SetConfigAsync(RelayUrl.Trim(), DirectPort, ct);

            // 诚实性（缺陷③）：中继地址改了并不会重建正在跑的 RelayClient——
            // 它在 Service 启动时就按当时的 options.RelayUrl 构造好了。
            // 旧实现这里无条件弹"已保存"，用户会以为已经在连新中继了。
            StatusMessage = BuildSaveMessage(result);
            PendingRestart = result.RequiresRestart;
            return result.Ok;
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法保存";
            return false;
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 组装保存后的提示文案：把"立即生效"与"需重启才生效"分开讲。
    /// 服务端也带了一份 <c>RestartHint</c>，优先用服务端的（本机化更准）。
    /// </summary>
    private static string BuildSaveMessage(SetConfigResult result)
    {
        if (!result.RequiresRestart)
        {
            return result.FirewallRepaired ? "已保存；防火墙规则已同步" : "已保存";
        }

        var hint = result.RestartHint ?? "中继地址已保存，但需重启 DeskLinkService 后才会生效。";
        return result.FirewallRepaired
            ? $"{hint}（防火墙规则已立即同步）"
            : hint;
    }

    /// <summary>
    /// 首次看到中继证书时要求用户确认（TOFU）。已确认过则直接放行。
    /// 返回 false 表示用户拒绝，调用方必须**中止**与该中继的会话。
    /// </summary>
    public async Task<bool> ConfirmRelayCertificateAsync(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            StatusMessage = "中继证书指纹为空";
            return false;
        }

        if (RelayCertificateConfirmed && RelayCertificateFingerprint == fingerprint)
        {
            return true;
        }

        RelayCertificateFingerprint = fingerprint;
        var confirmed = RelayCertConfirmer is not null && await RelayCertConfirmer(fingerprint);
        RelayCertificateConfirmed = confirmed;
        StatusMessage = confirmed ? "已确认中继证书" : "未确认中继证书，已中止连接";
        return confirmed;
    }

    /// <summary>启动桌面代理（被控端）。<paramref name="noInject"/> 用于自控自联调，防输入环路。</summary>
    public async Task<bool> StartAgentAsync(bool inject, bool noInject, string? pipeOverride = null, CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var result = await _api.StartAgentAsync(inject, noInject, pipeOverride, ct);
            AgentRunning = true;
            StatusMessage = result.StubMode ? $"桌面代理已启动（桩模式 pid={result.Pid}）" : $"桌面代理已启动 pid={result.Pid}";
            return true;
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法启动桌面代理";
            return false;
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"启动桌面代理失败：{ex.Message}";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task<bool> StopAgentAsync(CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            await _api.StopAgentAsync(ct);
            AgentRunning = false;
            StatusMessage = "桌面代理已停止";
            return true;
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法停止桌面代理";
            return false;
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"停止桌面代理失败：{ex.Message}";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
