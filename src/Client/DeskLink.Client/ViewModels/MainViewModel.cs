using DeskLink.Client.Security;
using DeskLink.Client.Services;
using DeskLink.Protocol.Media;

namespace DeskLink.Client.ViewModels;

/// <summary>
/// 主窗口视图模型：聚合四个页面 VM，并承载**被控端低干扰状态条**的状态。
///
/// 状态条语义（DESIGN「使用流程」第 5 条）：被控端以持续但低干扰的方式表示"正在被控制"，
/// 因此状态条文案要克制、不能是弹窗，同时把 fps/kbps/rtt/degraded 一并展示，
/// 让用户一眼知道"有人正在控制我，链路是否吃紧"。
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IServiceApi _api;
    private int _selectedTabIndex;
    private string _statusText = "就绪";
    private bool _isControlled;
    private string _controlledBy = "";

    public MainViewModel(IServiceApi api, PeerFingerprintStore store)
    {
        _api = api;
        Devices = new DevicesViewModel(api, store);
        Remote = new RemoteViewModel();
        Files = new FilesViewModel(api);
        Settings = new SettingsViewModel(api);

        OpenFilesCommand = new RelayCommand(() => SelectedTabIndex = 2);
        ToggleFullscreenCommand = new RelayCommand(() => FullscreenToggleRequested?.Invoke());
        DisconnectCommand = new RelayCommand(() => DisconnectRequested?.Invoke());
        EndControlCommand = new RelayCommand(() => _ = EndControlledAsync());
    }

    /// <summary>会话内"全屏"动作（由 MainWindow 切换 WindowState/全屏布局）。</summary>
    public event Action? FullscreenToggleRequested;

    /// <summary>会话内"断开"动作（由 MainWindow 关闭媒体通道并回到设备页）。</summary>
    public event Action? DisconnectRequested;

    /// <summary>会话内只允许这三个操作（DESIGN「使用流程」第 4 条）。</summary>
    public RelayCommand OpenFilesCommand { get; }
    public RelayCommand ToggleFullscreenCommand { get; }
    public RelayCommand DisconnectCommand { get; }

    /// <summary>被控端"随时断开"（DESIGN 使用流程第 5 条）：关闭本机全部 E2E 会话。</summary>
    public RelayCommand EndControlCommand { get; }

    public DevicesViewModel Devices { get; }
    public RemoteViewModel Remote { get; }
    public FilesViewModel Files { get; }
    public SettingsViewModel Settings { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    /// <summary>本机是否正被远控（被控端状态条可见性）。</summary>
    public bool IsControlled
    {
        get => _isControlled;
        set => SetProperty(ref _isControlled, value);
    }

    /// <summary>被控时展示的控制端标识（尽量不含敏感信息）。</summary>
    public string ControlledBy
    {
        get => _controlledBy;
        set => SetProperty(ref _controlledBy, value);
    }

    /// <summary>状态条一行文案：控制状态 + 统计。</summary>
    public string StatusBarText
    {
        get
        {
            if (!IsControlled)
            {
                return "DeskLink 就绪 · 未被控制";
            }

            var rtt = Remote.RttMs >= 0 ? $"{Remote.RttMs}ms" : "—";
            var degraded = Remote.Degraded ? " · 已降级" : "";
            var codec = Remote.SoftwareCodec ? " · 软件编码" : "";
            var who = string.IsNullOrEmpty(ControlledBy) ? "" : $"（{ControlledBy}）";
            return $"正在被控制{who} · {Remote.Fps:0.#}fps · {Remote.Kbps:0}kbps · {rtt}{degraded}{codec}";
        }
    }

    /// <summary>会话状态条文案（含"正在恢复画面"）。</summary>
    public string SessionStatusText => Remote.IsRecovering ? "正在恢复画面…" : Remote.StateText;

    /// <summary>把媒体通道收到的统计喂进状态条。</summary>
    public void OnStats(SessionStatsPayload stats)
    {
        Remote.UpdateStats(stats);
        OnPropertyChanged(nameof(StatusBarText));
    }

    /// <summary>把编码器配置喂进会话状态（软件编码时提示用户）。</summary>
    public void OnDesktopConfig(DesktopConfigPayload config)
    {
        Remote.ApplyDesktopConfig(config);
        OnPropertyChanged(nameof(StatusBarText));
    }

    /// <summary>刷新状态条文案（Remote 的属性变化时调用）。</summary>
    public void RefreshStatusBar() => OnPropertyChanged(nameof(StatusBarText));

    /// <summary>
    /// 轮询等待**远端会话真正建立**后才允许 UI 进入远程页。
    ///
    /// 为什么必须有这一步（缺陷②）：媒体通道是**本机** Service 与本机客户端之间的
    /// 管道，它连得上只说明本机两进程通了，**完全不能证明**对端在线。
    /// 旧实现据此无条件 <c>MarkEstablished()</c>，于是选中一台离线设备点"连接"，
    /// 界面立刻显示"已连接（…）"然后永远黑屏——用户被 UI 骗了。
    ///
    /// 这里改为以 Service 侧的真实会话状态为准：
    ///   - 直连路径：<c>DirectActiveSessions &gt; 0</c>（含入站与出站会话）
    ///   - 中继路径：<c>E2EState == "established"</c>
    /// 超时返回 false，由调用方如实报错并留在设备页。
    /// </summary>
    public async Task<bool> WaitForRemoteSessionAsync(
        bool requireDirect,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var s = await _api.GetStatusAsync(ct).ConfigureAwait(false);
                var ready = requireDirect
                    ? s.DirectActiveSessions > 0
                    : s.E2EState == "established";
                if (ready) return true;
            }
            catch (Exception ex) when (ex is PipeUnavailableException or PipeRpcException)
            {
                // Service 不可用时直接失败：轮询下去也不会有结果。
                return false;
            }

            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 轮询 Service 状态并刷新 IsControlled（被控端状态条可见性）。
    /// 由 MainWindow 的定时器周期调用（UI 线程，await 后回 UI 上下文）。
    ///
    /// 判定启发式：本机既是"已建立的中继 E2E 会话 + 媒体代理已连"（被控端跑
    /// 桌面代理产出画面）或存在直连活跃会话，即认为"正在被控制"。协议层无法
    /// 可靠区分主/被控（SIGMA Role 是字典序角色，不代表控制方向），启发式
    /// 是已知且可接受的近似：控制端点击"断开"（DisconnectCommand）即可离开。
    /// </summary>
    public async Task RefreshControlledStateAsync()
    {
        try
        {
            var s = await _api.GetStatusAsync();
            var controlled =
                (s.E2EState == "established" && s.MediaAgentConnected) ||
                s.DirectActiveSessions > 0;
            IsControlled = controlled;
            if (!controlled)
            {
                ControlledBy = "";
            }
            else if (string.IsNullOrEmpty(ControlledBy) && !string.IsNullOrEmpty(s.E2EPeerDeviceId))
            {
                // 只展示对端 device_id 前缀（不含敏感信息）。
                ControlledBy = $"设备 {s.E2EPeerDeviceId[..Math.Min(8, s.E2EPeerDeviceId.Length)]}…";
            }
        }
        catch (Exception ex) when (ex is PipeUnavailableException or PipeRpcException or OperationCanceledException)
        {
            // Service 未安装/未启动/正重启：保持当前显示不变（避免闪灭），
            // 状态条本身低干扰，下一轮轮询自然恢复。
        }
    }

    /// <summary>被控端主动断开：关闭本机全部 E2E 会话（中继 + 直连）。</summary>
    private async Task EndControlledAsync()
    {
        try
        {
            var r = await _api.EndSessionAsync();
            IsControlled = false;
            StatusText = r.Closed > 0
                ? $"已断开（关闭 {r.Closed} 个直连会话）"
                : "已断开";
        }
        catch (Exception ex) when (ex is PipeUnavailableException or PipeRpcException or OperationCanceledException)
        {
            StatusText = $"断开失败：{ex.Message}";
        }
    }
}
