// DeskLink.Panel —— 主视图模型：主控端 / 被控端两套流程的全部按钮逻辑
//
// 设计取向：把"用户下一步该做什么"直接写在界面上，而不是让用户自己拼命令行。
//   被控端 = 放开防火墙 + 开直连 + 注入代理 + 起服务 → 把公钥和 IP:端口给主控端
//   主控端 = 起服务 + 双向配对 → 打开控制界面 → 设备页选直连填 IP
//
// 界面是**两个角色选项卡**（「我是主控端」在左、「我是被控端」在右），
// 本机服务/高级设置/日志是两个角色共用的，放在选项卡外面。
// 切选项卡只是换视图，不会顺手改 EnableDirect/InjectAgent——那属于「一键准备」的动作。
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
    private readonly string? _settingsPath;

    /// <param name="settingsPath">
    /// 落盘路径。生产传 null（用 %APPDATA%\DeskLink\panel.json）；
    /// 测试必须传一个临时文件——<see cref="StartServiceAsync"/>、两个「一键准备」和
    /// 防火墙开关都会在内部无参调 <see cref="SaveSettings"/>，不隔离的话
    /// 跑一次单测就把开发者自己的面板配置冲掉了。
    /// </param>
    public MainViewModel(IServiceHost host, PanelSettings settings, string? settingsPath = null)
    {
        _host = host;
        _settings = settings;
        _settingsPath = settingsPath;

        _host.Log += OnHostLog;
        _host.ServiceExited += OnServiceExited;

        // 全部命令都走 RunCommand：统一做异常兜底。
        // 以前是 XAML 绑 Click 时由 MainWindow.RunGuarded 兜，绑 Command 之后那条路就没了，
        // 不下沉的话异常会变成没人观察的 Task 异常，面板表面上"点了没反应"。
        StartServiceCommand = new RelayCommand(() => _ = RunCommand(StartServiceAsync), () => CanStart);
        StopServiceCommand = new RelayCommand(() => _ = RunCommand(StopServiceAsync), () => CanStop);
        RefreshIdentityCommand = new RelayCommand(() => _ = RunCommand(RefreshIdentityAsync), () => !IsBusy);
        PairCommand = new RelayCommand(() => _ = RunCommand(PairAsync), () => !IsBusy && HasPeerPub);
        OpenFirewallCommand = new RelayCommand(() => _ = RunCommand(ToggleFirewallAsync), () => !IsBusy);
        PrepareControlledCommand = new RelayCommand(() => _ = RunCommand(PrepareControlledAsync), () => !IsBusy);
        PrepareControllerCommand = new RelayCommand(() => _ = RunCommand(PrepareControllerAsync), () => !IsBusy);
        LaunchClientCommand = new RelayCommand(LaunchClient, () => _host.ClientExePath is not null);
        SaveSettingsCommand = new RelayCommand(() => SaveSettings());
    }

    /// <summary>命令执行期间抛出、且方法内部没接住的异常。View 订阅它来弹窗。</summary>
    public event Action<Exception>? CommandFailed;

    /// <summary>
    /// 提权重启已发起 → 本进程该退位了。View 注入"关掉自己"的实现。
    /// 走 <see cref="RunCommand"/> 的收尾而不是在事件里直接关：那样会在命令
    /// 还停在 await 上时就关窗，命令的 finally（IsBusy 复位等）会落到已释放的对象上。
    /// </summary>
    public Action? ElevationRetire { get; set; }

    private bool _elevationPending;

    private async Task RunCommand(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 顺手也播进横幅：弹窗可能被用户忽略掉，横幅是留在界面上的那份。
            Banner(BannerLevel.Error, $"操作失败：{ex.Message}");
            OnHostLog(ex.ToString());
            CommandFailed?.Invoke(ex);
        }
        finally
        {
            if (_elevationPending)
            {
                _elevationPending = false;
                ElevationRetire?.Invoke();
            }
        }
    }

    private void RequestElevation()
    {
        _elevationPending = true;
        ElevationRequested?.Invoke();
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
    public bool QuicAvailable
    {
        get => _quicAvailable;
        private set { if (SetField(ref _quicAvailable, value)) OnPropertyChanged(nameof(QuicText)); }
    }

    /// <summary>
    /// QUIC 的真实状态。
    ///
    /// 这一行以前是**写死**的"QUIC 可用：…"，于是本机根本不支持 QUIC 时界面也在说可用——
    /// 正是本项目反复在修的"UI 撒谎"。现在两个分支都把话说满：
    /// 不可用时明说不可用，不要让用户以为是防火墙问题。
    /// </summary>
    public string QuicText => QuicAvailable
        ? "QUIC：本机支持。能否真的走 QUIC 还取决于两端系统与防火墙的 UDP 规则。"
        : "QUIC：本机不支持，只能走 TCP-TLS。直连端口的 TCP 规则必须放行。";

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

    private int _monitorIndex;
    /// <summary>
    /// 捕获哪块显示器（0 = 主显示器）。**只有被控端需要**：主控端不产生画面。
    ///
    /// 负数在这里就夹回 0，而不是留给 <see cref="SaveSettings"/>：
    /// 夹在保存阶段的话，输入框里会一直显示 -5，旁边那句说明却写"第 1 块屏幕"，
    /// 同一屏上两处自相矛盾，而且 -5 是能成功转成 int 的、连红框都不会有——
    /// 典型的静默撒谎。夹在 setter 里，输入框立刻回显 0，两边永远一致。
    /// </summary>
    public int MonitorIndex
    {
        get => _monitorIndex;
        set
        {
            var clamped = value < 0 ? 0 : value;
            if (SetField(ref _monitorIndex, clamped)) OnPropertyChanged(nameof(MonitorIndexText));
        }
    }

    public string MonitorIndexText => MonitorIndex > 0
        ? $"第 {MonitorIndex + 1} 块屏幕（索引 {MonitorIndex}）"
        : "第 1 块屏幕（主显示器）";

    private string? _relayUrl;
    public string? RelayUrl { get => _relayUrl; set => SetField(ref _relayUrl, value); }

    private bool _isElevated;
    public bool IsElevated { get => _isElevated; private set => SetField(ref _isElevated, value); }

    private bool _isController;
    public bool IsController { get => _isController; private set => SetField(ref _isController, value); }

    /// <summary>
    /// 角色选项卡的选中索引：<b>0 = 我是主控端，1 = 我是被控端</b>。
    ///
    /// 刻意**不**与 <see cref="PanelRole"/> 的数值对齐：PanelRole.Controlled = 0、Controller = 1，
    /// 而 panel.json 是按**数字**落盘的（JsonOptions 没配 JsonStringEnumConverter），
    /// 将来若把枚举顺序反过来，老用户已保存的角色会被静默读成另一个角色。
    /// </summary>
    private int _selectedRoleIndex = 1;

    public int SelectedRoleIndex
    {
        get => _selectedRoleIndex;
        set
        {
            // TabControl 在内容还没建好时可能推 -1 / 2 过来，夹回合法范围，
            // 否则界面上会出现"两个选项卡都没选中"的空白面板。
            var index = Math.Clamp(value, 0, 1);
            if (index == _selectedRoleIndex) return;
            _selectedRoleIndex = index;
            OnPropertyChanged(nameof(SelectedRoleIndex));
            ApplyRole(index == 0 ? PanelRole.Controller : PanelRole.Controlled);
        }
    }

    /// <summary>当前角色（由选项卡索引派生，供落盘用）。</summary>
    public PanelRole Role => IsController ? PanelRole.Controller : PanelRole.Controlled;

    /// <summary>
    /// 角色落地的**唯一**入口。切选项卡和「一键准备」都走这里——
    /// 两处各改一半会漏掉 _settings.Role，下次启动就跳回另一个角色。
    /// </summary>
    private void ApplyRole(PanelRole role)
    {
        IsController = role == PanelRole.Controller;
        _settings.Role = role;

        var index = IsController ? 0 : 1;
        if (index != _selectedRoleIndex)
        {
            _selectedRoleIndex = index;
            OnPropertyChanged(nameof(SelectedRoleIndex));
        }
    }

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

        // 不要用引导语覆盖上面的失败：读不到公钥 / 查不到防火墙时，
        // 那条错误才是用户该先看到的东西。BannerLevel 仍是 Info 说明前面没出过问题。
        //
        // 引导语必须跟着**真实状态**说话。RefreshStatusAsync 刚刚把服务状态查出来了：
        // 服务没运行时写"就绪"是撒谎，而且原句"被控端就绪。在本页点『一键准备被控端』"
        // 本身也自相矛盾——既说好了又让用户去启动。实测截图里服务明明显示"未运行"，
        // 横幅却是一片绿的"被控端就绪"。
        if (BannerLevel == BannerLevel.Info)
        {
            Banner(BannerLevel.Info, ServiceRunning
                ? _isController
                    ? "本机服务已在运行。在本页粘贴被控端的公钥完成配对，然后点『打开控制界面』。"
                    : "本机服务已在运行。把本页的【公钥】和【本机地址】发给主控端即可。"
                : _isController
                    ? "本机服务未运行。点『一键准备主控端』一步做完，或先单独点『启动服务』。"
                    : "本机服务未运行。点『一键准备被控端』一步做完，或先单独点『启动服务』。");
        }
    }

    public void LoadFromSettings()
    {
        DataDir = _settings.DataDir;
        DirectPort = _settings.DirectPort;
        EnableDirect = _settings.EnableDirect;
        InjectAgent = _settings.InjectAgent;
        FileScopeText = string.Join(";", _settings.FileScopeRoots);
        MonitorIndex = _settings.MonitorIndex > 0 ? _settings.MonitorIndex : 0;
        RelayUrl = _settings.RelayUrl;
        ApplyRole(_settings.Role);
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
        _settings.MonitorIndex = MonitorIndex > 0 ? MonitorIndex : 0;
        _settings.RelayUrl = string.IsNullOrWhiteSpace(RelayUrl) ? null : RelayUrl.Trim();
        _settings.Role = Role;

        // path 为 null 时回落到构造时注入的路径：生产是 %APPDATA%，测试是各自的临时文件。
        // 以前这里直接 _settings.Save(null)，而 StartServiceAsync / 两个「一键准备」 /
        // 防火墙开关都会无参调本方法 —— 等于跑一次单测就改掉开发者自己的面板配置。
        var ok = _settings.Save(path ?? _settingsPath);
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

    public async Task PairAsync() => await TryPairAsync().ConfigureAwait(true);

    /// <summary>
    /// 真去配对，返回是否成功。横幅由本方法负责给出，调用方不要重复播报。
    /// 拆出返回值是为了让「一键准备」能把"到底配没配上"并进它自己的收尾提示里。
    /// </summary>
    private async Task<bool> TryPairAsync()
    {
        var peer = (PeerPub ?? "").Trim();
        if (string.IsNullOrWhiteSpace(peer)) { Banner(BannerLevel.Warn, "请先粘贴对方的公钥。"); return false; }

        if (!Begin("正在配对…")) return false;
        try
        {
            var r = await _host.PairAsync(peer).ConfigureAwait(true);
            if (r.Ok)
            {
                Banner(BannerLevel.Ok, "配对成功。**记得让对方也配回来**——直连必须两边都配，否则会被对端在握手前断开。");
                await RefreshStatusAsync().ConfigureAwait(true);
                return true;
            }

            Banner(BannerLevel.Error, $"配对失败（退出码 {r.ExitCode}）：{FirstLine(r.Combined)}");
            return false;
        }
        finally { End(); }
    }

    public async Task RefreshFirewallAsync()
    {
        if (!Begin("正在查询防火墙…")) return;
        try
        {
            await QueryFirewallAsync().ConfigureAwait(true);
            if (Firewall is null) Banner(BannerLevel.Warn, "读不到防火墙状态（Service 不可用？）。");
        }
        finally { End(); }
    }

    /// <summary>
    /// 只查状态，不碰 IsBusy、不播报横幅。
    /// 切换防火墙后已经在 busy 了，这时候再调 RefreshFirewallAsync 会被它自己的
    /// Begin() 挡在门外——那样 Firewall 永远不刷新，开关就成了"只进不退"的一次性动作。
    /// </summary>
    private async Task QueryFirewallAsync()
        => Firewall = await _host.GetFirewallStatusAsync().ConfigureAwait(true);

    /// <summary>切开放行/关闭。需要管理员时会触发提权重启。</summary>
    public async Task ToggleFirewallAsync()
    {
        // 读不到状态时按"没开"处理，也就是这次点下去是放行。
        // 读不到状态本身是横幅要说的事，但不该让用户按了没反应。
        var wantEnable = Firewall?.FullyOpen != true;

        if (!_host.IsAdministrator)
        {
            SaveSettings();
            Banner(BannerLevel.Warn, "放行入站端口需要管理员权限，正在以管理员身份重新打开面板…");
            if (_host.TryRestartElevated()) RequestElevation();
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
            // 刷新失败也不能盖掉上面那条结果：切换成没成，和读没读到状态，是两回事。
            try { await QueryFirewallAsync().ConfigureAwait(true); }
            catch (Exception ex) { OnHostLog("刷新防火墙状态失败：" + ex.Message); }
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

    /// <summary>
    /// 三态配对结果：null=没填公钥，false=填了但没配上，true=配对成功。
    /// 二态不够用——"没填"和"填错了"要告诉用户的话完全不同，
    /// 混成一句"还没配对"会让人反复检查自己早就填对的输入框。
    /// 横幅由 <see cref="TryPairAsync"/> 播，这里只回报事实。
    /// </summary>
    private async Task<bool?> TryPairTriStateAsync()
        => string.IsNullOrWhiteSpace(PeerPub) ? null : await TryPairAsync().ConfigureAwait(true);

    /// <summary>被控端一键准备：开直连 + 注入代理 + 放行防火墙 + 起服务。</summary>
    public async Task PrepareControlledAsync()
    {
        ApplyRole(PanelRole.Controlled);
        _settings.ApplyRoleDefaults(PanelRole.Controlled);
        EnableDirect = _settings.EnableDirect;
        InjectAgent = _settings.InjectAgent;
        SaveSettings();

        // 共享目录的默认值必须一路带到最后的收尾提示里。
        // 以前是在这里单独播一条 Info，可后面 ToggleFirewallAsync / StartServiceAsync
        // 各自都会 SaveSettings() 播一次"设置已保存"，末尾的"已就绪"再盖一次——
        // 这条信息必定被覆盖三次，用户从头到尾看不到自己被授权了哪个目录。
        string? autoShare = null;
        if (string.IsNullOrWhiteSpace(FileScopeText))
        {
            autoShare = Path.Combine(DataDir, "Share");
            FileScopeText = autoShare;
        }

        // 先配对、再提权。
        // 顺序有讲究：PeerPub 存在内存里、不落盘，提权重启会把这个框清空；
        // 而配对走的是 --pair-peer-pub 一次性子命令，本来就不需要管理员权限。
        var paired = await TryPairTriStateAsync().ConfigureAwait(true);

        if (!_host.IsAdministrator)
        {
            SaveSettings();
            Banner(BannerLevel.Warn, "放行入站端口需要管理员权限，正在以管理员身份重新打开面板…");
            if (_host.TryRestartElevated()) { RequestElevation(); return; }
            Banner(BannerLevel.Error, "提权被拒绝。可先点『启动服务』跳过这一步，但主控端可能连不上。");
        }

        await ToggleFirewallAsync().ConfigureAwait(true);
        if (!_host.IsAdministrator) return;   // 提权失败/被拒：已经提示过了，别再起服务造成半配置状态

        await StartServiceAsync().ConfigureAwait(true);

        // 配没配上是此刻唯一还没定的事，必须留在最显眼的位置说清楚，
        // 而且不能被上面 StartServiceAsync 的"服务已启动"盖掉。
        var pending = paired switch
        {
            true => "",
            false => "【配对没成功】上面写的是真实失败原因（公钥可能贴错、或对方服务没起），修好后再点一次本页的『配对』。\n",
            _ => "【还没配对】把主控端的公钥粘进本页的『对方的公钥』输入框，点『配对』。\n",
        };
        Banner(BannerLevel.Ok, pending
            + "被控端已就绪。把本页的【公钥】和【本机地址】发给主控端即可。"
            + (autoShare is null ? "" : $"\n共享目录已设为 {autoShare}，远程只能读写这个目录。"));
    }

    /// <summary>主控端一键准备：关掉用不上的开关 + 起服务（+ 有公钥就顺手配对）。</summary>
    public async Task PrepareControllerAsync()
    {
        ApplyRole(PanelRole.Controller);
        _settings.ApplyRoleDefaults(PanelRole.Controller);
        EnableDirect = _settings.EnableDirect;
        InjectAgent = _settings.InjectAgent;
        SaveSettings();

        // 必须接住结果：下面 StartServiceAsync 的"服务已启动"和末尾的"已就绪"
        // 都会盖掉 TryPairAsync 播的横幅，唯一的去处就是这里把它并进收尾提示。
        var paired = await TryPairTriStateAsync().ConfigureAwait(true);

        await StartServiceAsync().ConfigureAwait(true);

        // "还没配好"才是主控端用户此刻唯一未完成的事，必须压过绿色的"已就绪"。
        var pending = paired switch
        {
            true => "",
            false => "【配对没成功】上面写的是真实失败原因（公钥可能贴错、或被控端服务没起），改好后再点一次本页的『配对』。\n",
            _ => "【还没配对】把被控端的公钥填进本页的『被控端公钥』输入框，再点一次本按钮。\n",
        };
        Banner(BannerLevel.Ok, pending + "主控端已就绪。点本页的『打开控制界面』，在设备页选『局域网直连』并填被控端 IP:端口。");
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
