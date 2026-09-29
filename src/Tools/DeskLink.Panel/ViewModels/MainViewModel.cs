// DeskLink.Panel —— 主视图模型：被控端 / 控制端两套流程的全部按钮逻辑
//
// 设计取向：把"用户下一步该做什么"直接写在界面上，而不是让用户自己拼命令行。
//   被控端 = 放开防火墙 + 开直连 + 注入代理 + 起服务 → 把公钥和 IP:端口给对方
//   控制端 = 起服务 + 配对 → 打开控制界面 → 设备页选直连填 IP
//
// 关键约束：本类**不碰任何进程与管道**，全部经 IServiceHost 抽象，
// 因此所有按钮逻辑都能在 tests\Panel.Tests 里用 FakeServiceHost 脱离进程验证。
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DeskLink.Panel.Services;
using DeskLink.Protocol.Pipe;

namespace DeskLink.Panel.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IServiceHost _host;
    private readonly PanelSettings _settings;
    private bool _disposed;

    public MainViewModel(IServiceHost host, PanelSettings settings)
    {
        _host = host;
        _settings = settings;

        _host.Log += OnHostLog;
        _host.ServiceExited += OnServiceExited;

        StartServiceCommand = new RelayCommand(() => _ = StartServiceAsync(), () => CanStart);
        StopServiceCommand = new RelayCommand(() => _ = StopServiceAsync(), () => CanStop);
        RefreshIdentityCommand = new RelayCommand(() => _ = RefreshIdentityAsync(), () => !IsBusy);
        PairCommand = new RelayCommand(() => _ = PairAsync(), () => !IsBusy && HasPeerPub);
        OpenFirewallCommand = new RelayCommand(() => _ = ToggleFirewallAsync(), () => !IsBusy);
        PrepareControlledCommand = new RelayCommand(() => _ = PrepareControlledAsync(), () => !IsBusy);
        PrepareControllerCommand = new RelayCommand(() => _ = PrepareControllerAsync(), () => !IsBusy);
        LaunchClientCommand = new RelayCommand(LaunchClient, () => _host.ClientExePath is not null);
        SaveSettingsCommand = new RelayCommand(() => SaveSettings());
    }

    // ── 对外状态 ────────────────────────────────────────────────────────────

    private string _statusMessage = "正在初始化…";
    private BannerLevel _bannerLevel = BannerLevel.Info;
    public string StatusMessage { get => _statusMessage; private set => SetField(ref _statusMessage, value); }
    public BannerLevel BannerLevel { get => _bannerLevel; private set => SetField(ref _bannerLevel, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (SetField(ref _isBusy, value)) RefreshCommands(); } }

    private string _busyMessage = "";
    public string BusyMessage { get => _busyMessage; private set => SetField(ref _busyMessage, value); }

    // ── 身份与地址 ──────────────────────────────────────────────────────────

    private string _publicKey = "";
    public string PublicKey { get => _publicKey; private set => SetField(ref _publicKey, value); }

    private string _deviceId = "";
    public string DeviceId { get => _deviceId; private set => SetField(ref _deviceId, value); }

    private string _localEndpoint = "";
    public string LocalEndpoint { get => _localEndpoint; private set => SetField(ref _localEndpoint, value); }

    private bool _quicAvailable;
    public bool QuicAvailable { get => _quicAvailable; private set => SetField(ref _quicAvailable, value); }

    // ── 运行状态 ────────────────────────────────────────────────────────────

    private bool _serviceRunning;
    public bool ServiceRunning { get => _serviceRunning; private set { if (SetField(ref _serviceRunning, value)) RefreshCommands(); } }

    private string _relayState = "—";
    public string RelayState { get => _relayState; private set => SetField(ref _relayState, value); }

    private string _e2eState = "—";
    public string E2EState { get => _e2eState; private set => SetField(ref _e2eState, value); }

    private int _directSessions;
    public int DirectSessions { get => _directSessions; private set => SetField(ref _directSessions, value); }

    private int _pairingCount;
    public int PairingCount { get => _pairingCount; private set => SetField(ref _pairingCount, value); }

    // 刻意**不**把 StatusResult.DirectEnabled 暴露成一个"直连已开启/已关闭"的指示灯。
    // 实测该字段取自 FirewallHelper.QueryEnabled(port)，是"防火墙有没有放行这个端口"，
    // 不是"DirectServer 有没有在监听"：带 --enable-direct 启动、日志已打出
    // "DirectServer enabled on port N" 时它仍可能为 false。
    // 把它显示成"直连已关"就是本项目刚修掉的那类"UI 撒谎"——详见 PipeContract.StatusResult.DirectEnabled。
    // 面板的"直连是否开启"由 EnableDirect 复选框表达（那是用户实际启动时传进去的参数），
    // "能不能被连上"由 FirewallText 表达（那才是真正的决定因素）。

    // ── 防火墙 ──────────────────────────────────────────────────────────────

    private FirewallStatus? _firewall;
    public FirewallStatus? Firewall { get => _firewall; private set { if (SetField(ref _firewall, value)) OnPropertyChanged(nameof(FirewallText)); } }

    public string FirewallText => Firewall switch
    {
        null => "未知",
        { FullyOpen: true } => $"已放行 {Firewall.DirectPort}（TCP+UDP）",
        { TcpRulePresent: true, UdpRulePresent: false } => $"仅 TCP 已放行 {Firewall.DirectPort}（缺 UDP，QUIC 会失败）",
        { UdpRulePresent: true, TcpRulePresent: false } => $"仅 UDP 已放行 {Firewall.DirectPort}（缺 TCP，TCP-TLS 会失败）",
        _ => $"未放行 {Firewall.DirectPort}"
    };

    // ── 表单 ────────────────────────────────────────────────────────────────

    private string _peerPub = "";
    public string PeerPub
    {
        get => _peerPub;
        set { if (SetField(ref _peerPub, value)) { OnPropertyChanged(nameof(HasPeerPub)); RefreshCommands(); } }
    }

    public bool HasPeerPub => !string.IsNullOrWhiteSpace(PeerPub) && !IsBusy;

    private string _dataDir = "";
    public string DataDir { get => _dataDir; set => SetField(ref _dataDir, value); }

    private int _directPort = 47200;
    public int DirectPort { get => _directPort; set => SetField(ref _directPort, value); }

    private bool _enableDirect;
    public bool EnableDirect { get => _enableDirect; set => SetField(ref _enableDirect, value); }

    private bool _injectAgent;
    public bool InjectAgent { get => _injectAgent; set => SetField(ref _injectAgent, value); }

    private string _fileScopeText = "";
    public string FileScopeText { get => _fileScopeText; set => SetField(ref _fileScopeText, value); }

    private string? _relayUrl;
    public string? RelayUrl { get => _relayUrl; set => SetField(ref _relayUrl, value); }

    private bool _isElevated;
    public bool IsElevated { get => _isElevated; private set => SetField(ref _isElevated, value); }

    private bool _isController;
    public bool IsController { get => _isController; private set => SetField(ref _isController, value); }

    public ObservableCollection<string> Log { get; } = new();

    public bool CanStart => !IsBusy && !ServiceRunning;
    public bool CanStop => !IsBusy && ServiceRunning;

    // ── 命令 ────────────────────────────────────────────────────────────────

    public RelayCommand StartServiceCommand { get; }
    public RelayCommand StopServiceCommand { get; }
    public RelayCommand RefreshIdentityCommand { get; }
    public RelayCommand PairCommand { get; }
    public RelayCommand OpenFirewallCommand { get; }
    public RelayCommand PrepareControlledCommand { get; }
    public RelayCommand PrepareControllerCommand { get; }
    public RelayCommand LaunchClientCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    /// <summary>需要提权时触发（窗口负责以管理员身份重启本程序并退出）。</summary>
    public event Action? ElevationRequested;

    // ── 生命周期 ────────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        LoadFromSettings();
        IsElevated = _host.IsAdministrator;
        LocalEndpoint = _host.LocalEndpoint;

        if (_host.ServiceExePath is null)
        {
            Banner(BannerLevel.Error, "找不到 DeskLink.Service.exe。请把面板和 Service 放在同一目录，或在下方手动指定路径。");
            return;
        }

        await RefreshIdentityAsync().ConfigureAwait(true);
        await RefreshStatusAsync().ConfigureAwait(true);
        await RefreshFirewallAsync().ConfigureAwait(true);

        // 不要用"就绪"覆盖上面的失败：读不到公钥 / 查不到防火墙时，
        // 那条错误才是用户该先看到的东西。BannerLevel 仍是 Info 说明前面没出过问题。
        if (BannerLevel == BannerLevel.Info)
        {
            Banner(BannerLevel.Info, _isController
                ? "控制端就绪。粘贴对方公钥完成配对，然后点『打开控制界面』。"
                : "被控端就绪。点『一键准备被控端』，然后把上面的公钥和地址发给控制端。");
        }
    }

    public void LoadFromSettings()
    {
        DataDir = _settings.DataDir;
        DirectPort = _settings.DirectPort;
        EnableDirect = _settings.EnableDirect;
        InjectAgent = _settings.InjectAgent;
        FileScopeText = string.Join(";", _settings.FileScopeRoots);
        RelayUrl = _settings.RelayUrl;
        _isController = _settings.Role == PanelRole.Controller;
        OnPropertyChanged(nameof(IsController));
    }

    /// <summary>
    /// 把界面上的表单写回设置对象并落盘。返回是否落盘成功。
    /// <paramref name="path"/> 仅供测试指定隔离路径；生产路径为 %APPDATA%\DeskLink\panel.json。
    /// </summary>
    public bool SaveSettings(string? path = null)
    {
        _settings.DataDir = string.IsNullOrWhiteSpace(DataDir) ? PanelSettings.DefaultDataDir() : DataDir.Trim();
        _settings.DirectPort = DirectPort is > 0 and < 65536 ? DirectPort : 47200;
        _settings.EnableDirect = EnableDirect;
        _settings.InjectAgent = InjectAgent;
        _settings.FileScopeRoots = (FileScopeText ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _settings.RelayUrl = string.IsNullOrWhiteSpace(RelayUrl) ? null : RelayUrl.Trim();
        _settings.Role = _isController ? PanelRole.Controller : PanelRole.Controlled;

        var ok = _settings.Save(path);
        if (ok) Banner(BannerLevel.Ok, "设置已保存。");
        else Banner(BannerLevel.Warn, "设置保存失败（可能 %APPDATA% 不可写），本次运行仍按界面上的值执行。");
        return ok;
    }

    // ── 具体动作 ────────────────────────────────────────────────────────────

    public async Task RefreshIdentityAsync()
    {
        if (!Begin("正在读取本机公钥…")) return;
        try
        {
            var r = await _host.PrintConfigAsync().ConfigureAwait(true);
            if (!r.Ok)
            {
                Banner(BannerLevel.Error, $"读取本机公钥失败（退出码 {r.ExitCode}）：{FirstLine(r.Combined)}");
                return;
            }

            var id = ServiceOutputParser.ParseIdentity(r.StdOut);
            if (!id.IsValid)
            {
                Banner(BannerLevel.Error, "没能从 --print-config 输出里解析出 ed25519 公钥，配对会失败。");
                return;
            }

            PublicKey = id.Ed25519PubB64;
            DeviceId = id.DeviceIdHex;
            QuicAvailable = id.QuicAvailable;
        }
        finally { End(); }
    }

    public async Task PairAsync()
    {
        var peer = (PeerPub ?? "").Trim();
        if (string.IsNullOrWhiteSpace(peer)) { Banner(BannerLevel.Warn, "请先粘贴对方的公钥。"); return; }

        if (!Begin("正在配对…")) return;
        try
        {
            var r = await _host.PairAsync(peer).ConfigureAwait(true);
            if (r.Ok)
            {
                Banner(BannerLevel.Ok, "配对成功。**记得让对方也配回来**——直连必须两边都配，否则会被对端在握手前断开。");
                await RefreshStatusAsync().ConfigureAwait(true);
            }
            else
            {
                Banner(BannerLevel.Error, $"配对失败（退出码 {r.ExitCode}）：{FirstLine(r.Combined)}");
            }
        }
        finally { End(); }
    }

    public async Task RefreshFirewallAsync()
    {
        if (!Begin("正在查询防火墙…")) return;
        try
        {
            Firewall = await _host.GetFirewallStatusAsync().ConfigureAwait(true);
            if (Firewall is null)
                Banner(BannerLevel.Warn, "读不到防火墙状态（Service 不可用？）。");
        }
        finally { End(); }
    }

    /// <summary>切开放行/关闭。需要管理员时会触发提权重启。</summary>
    public async Task ToggleFirewallAsync()
    {
        var wantEnable = Firewall?.FullyOpen != true;

        if (!_host.IsAdministrator)
        {
            SaveSettings();
            Banner(BannerLevel.Warn, "放行入站端口需要管理员权限，正在以管理员身份重新打开面板…");
            if (_host.TryRestartElevated()) ElevationRequested?.Invoke();
            else Banner(BannerLevel.Error, "提权被拒绝。请右键面板图标 →『以管理员身份运行』后再点一次。");
            return;
        }

        if (!Begin(wantEnable ? "正在放行入站端口…" : "正在关闭入站端口…")) return;
        try
        {
            var r = await _host.SetFirewallAsync(wantEnable).ConfigureAwait(true);
            Banner(r.Ok ? BannerLevel.Ok : BannerLevel.Error,
                r.Ok
                    ? (wantEnable ? $"已放行 {_settings.DirectPort} 入站。" : $"已关闭 {_settings.DirectPort} 入站。")
                    : $"操作失败（退出码 {r.ExitCode}）：{FirstLine(r.Combined)}");
            await RefreshFirewallAsync().ConfigureAwait(true);
        }
        finally { End(); }
    }

    public async Task StartServiceAsync()
    {
        SaveSettings();
        if (!Begin("正在启动本机服务…")) return;
        try
        {
            await _host.StartServiceAsync().ConfigureAwait(true);
            ServiceRunning = true;
            Banner(BannerLevel.Ok, "服务已启动。");
            // 管道起来要一点时间，立刻查常常查不到，给它一拍再查。
            await Task.Delay(800).ConfigureAwait(true);
            await RefreshStatusAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ServiceRunning = false;
            Banner(BannerLevel.Error, $"启动服务失败：{ex.Message}");
        }
        finally { End(); }
    }

    public async Task StopServiceAsync()
    {
        if (!Begin("正在停止本机服务…")) return;
        try
        {
            await _host.StopServiceAsync().ConfigureAwait(true);
            ServiceRunning = false;
            Banner(BannerLevel.Ok, "服务已停止。");
        }
        finally { End(); }
    }

    public async Task RefreshStatusAsync()
    {
        var st = await _host.GetStatusAsync().ConfigureAwait(true);
        if (st is null)
        {
            ServiceRunning = false;
            RelayState = "服务未运行";
            E2EState = "—";
            DirectSessions = 0;
            return;
        }

        ServiceRunning = true;
        RelayState = DescribeRelay(st.RelayState);
        E2EState = st.E2EState;
        DirectSessions = st.DirectActiveSessions;
        PairingCount = st.PairingCount;
    }

    /// <summary>被控端一键准备：开直连 + 注入代理 + 放行防火墙 + 起服务。</summary>
    public async Task PrepareControlledAsync()
    {
        _isController = false;
        OnPropertyChanged(nameof(IsController));
        _settings.ApplyRoleDefaults(PanelRole.Controlled);
        EnableDirect = _settings.EnableDirect;
        InjectAgent = _settings.InjectAgent;
        SaveSettings();

        if (string.IsNullOrWhiteSpace(FileScopeText))
        {
            var share = Path.Combine(DataDir, "Share");
            FileScopeText = share;
            Banner(BannerLevel.Info, $"共享目录默认为 {share}，可在下方改。远程只能读写这个目录。");
        }

        if (!_host.IsAdministrator)
        {
            SaveSettings();
            Banner(BannerLevel.Warn, "放行入站端口需要管理员权限，正在以管理员身份重新打开面板…");
            if (_host.TryRestartElevated()) { ElevationRequested?.Invoke(); return; }
            Banner(BannerLevel.Error, "提权被拒绝。可先点『启动服务』跳过这一步，但控制端可能连不上。");
        }

        await ToggleFirewallAsync().ConfigureAwait(true);
        if (!_host.IsAdministrator) return;   // 提权失败/被拒：已经提示过了，别再起服务造成半配置状态

        await StartServiceAsync().ConfigureAwait(true);
        Banner(BannerLevel.Ok, "被控端已就绪。把上面的【公钥】和【本机地址】发给控制端即可。");
    }

    /// <summary>控制端一键准备：关掉用不上的开关 + 起服务（+ 有公钥就顺手配对）。</summary>
    public async Task PrepareControllerAsync()
    {
        _isController = true;
        OnPropertyChanged(nameof(IsController));
        _settings.ApplyRoleDefaults(PanelRole.Controller);
        EnableDirect = _settings.EnableDirect;
        InjectAgent = _settings.InjectAgent;
        SaveSettings();

        if (string.IsNullOrWhiteSpace(PeerPub))
        {
            Banner(BannerLevel.Warn, "还没填对方公钥。拿到被控端的公钥后填进去再点一次『一键准备控制端』即可完成配对。");
        }
        else
        {
            await PairAsync().ConfigureAwait(true);
        }

        await StartServiceAsync().ConfigureAwait(true);
        // 别把上面那条"还没填公钥"覆盖掉——它才是控制端用户此刻唯一未完成的事。
        var pending = string.IsNullOrWhiteSpace(PeerPub)
            ? "【还没配对】把被控端的公钥填进上面的输入框再点一次本按钮。\n"
            : "";
        Banner(BannerLevel.Ok, pending + "控制端已就绪。点『打开控制界面』，在设备页选『局域网直连』并填被控端 IP:端口。");
    }

    public void LaunchClient()
    {
        if (_host.LaunchClient()) Banner(BannerLevel.Ok, "控制界面已启动。");
        else Banner(BannerLevel.Error, "找不到 DeskLink.Client.exe（控制端界面）。");
    }

    // ── 内部 ────────────────────────────────────────────────────────────────

    private bool Begin(string message)
    {
        if (IsBusy) return false;
        IsBusy = true;
        BusyMessage = message;
        return true;
    }

    private void End() { IsBusy = false; BusyMessage = ""; }

    private void Banner(BannerLevel level, string message)
    {
        BannerLevel = level;
        StatusMessage = message;
    }

    private void RefreshCommands()
    {
        // ICommand.CanExecuteChanged 只能 += / -=，主动刷新要回到具体命令类型。
        StartServiceCommand.RaiseCanExecuteChanged();
        StopServiceCommand.RaiseCanExecuteChanged();
        PairCommand.RaiseCanExecuteChanged();
        RefreshIdentityCommand.RaiseCanExecuteChanged();
        OpenFirewallCommand.RaiseCanExecuteChanged();
        PrepareControlledCommand.RaiseCanExecuteChanged();
        PrepareControllerCommand.RaiseCanExecuteChanged();
    }

    private void OnHostLog(string line)
    {
        // 只保留最近 500 行，否则长时间运行会把内存吃满。
        Log.Add(line);
        while (Log.Count > 500) Log.RemoveAt(0);
    }

    private void OnServiceExited(int code)
    {
        ServiceRunning = false;
        Banner(BannerLevel.Warn, $"服务已退出（退出码 {code}）。看下方日志定位原因。");
    }

    private static string DescribeRelay(string state) => state switch
    {
        "established" or "connected" => "已连接",
        "connecting" => "连接中",
        "disconnected" => "未连接",
        _ => state
    };

    private static string FirstLine(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "（无输出）";
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Log -= OnHostLog;
        _host.ServiceExited -= OnServiceExited;
    }
}

/// <summary>最简 ICommand。面板的命令全是同步触发异步方法，不需要 async 命令那一套。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _execute();
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
