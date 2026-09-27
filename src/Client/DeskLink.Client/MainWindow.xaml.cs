using System.Windows;
using System.Windows.Threading;
using DeskLink.Client.Input;
using DeskLink.Client.Media;
using DeskLink.Client.Security;
using DeskLink.Client.Services;
using DeskLink.Client.ViewModels;
using DeskLink.Client.Views;

namespace DeskLink.Client;

/// <summary>
/// 主窗口。代码后置只做**装配与转发**（打开对话框、切换全屏、连接媒体通道），
/// 不含业务决策——所有决策在 ViewModel。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ClientOptions _options;

    private MediaChannelClient? _media;
    private RemoteSessionHost? _sessionHost;
    private DispatcherTimer? _demoTimer;
    private NullFrameSource? _demoSource;
    private DispatcherTimer? _controlledPollTimer;
    private bool _isFullscreen;
    private RawInputHook? _rawInput;

    /// <summary>
    /// 无参构造：给测试/设计器用（不连接 Service、不起 Application）。
    /// ServiceApi 是惰性的，构造阶段不会碰管道。
    /// </summary>
    public MainWindow()
        : this(new MainViewModel(new ServiceApi(ClientOptions.DefaultClientPipeName()), new PeerFingerprintStore()),
               ClientOptions.Parse(null))
    {
    }

    public MainWindow(MainViewModel vm, ClientOptions options)
    {
        _vm = vm;
        _options = options;

        InitializeComponent();
        DataContext = vm;

        // 中继连接前弹指纹确认对话框（TOFU）。
        vm.Devices.FingerprintConfirmer = (device, fingerprint) =>
        {
            var dlg = new FingerprintConfirmDialog(device.Label, fingerprint) { Owner = this };
            return Task.FromResult(dlg.ShowDialog() == true);
        };

        vm.Devices.RelayConnectApproved += OnRelayConnectApproved;
        vm.Devices.LanConnectApproved += OnLanConnectApproved;

        vm.FullscreenToggleRequested += ToggleFullscreen;
        vm.DisconnectRequested += OnDisconnectRequested;

        vm.Remote.PropertyChanged += (_, _) => vm.RefreshStatusBar();

        Loaded += async (_, _) => await _vm.Devices.RefreshAsync();

        // 被控端状态条轮询（DESIGN 使用流程第 5 条）：周期性查询 Service 的
        // E2E/直连会话状态，点亮"正在被控制"并让"断开"按钮可用。
        // 3s 一次的 get_status 是管道上的小请求，开销可忽略。
        _controlledPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controlledPollTimer.Tick += (_, _) => _ = PollControlledStateAsync();
        _controlledPollTimer.Start();
    }

    private async Task PollControlledStateAsync()
    {
        try
        {
            await _vm.RefreshControlledStateAsync();
        }
        catch
        {
            // VM 内部已兜底；这里再兜一层，避免定时器回调抛异常进 Dispatcher。
        }
    }

    private async void OnRelayConnectApproved(DeviceItem device)
    {
        _vm.Remote.PeerLabel = device.Label;
        _vm.Remote.BeginConnect();
        _vm.SelectedTabIndex = 1;
        await OpenMediaAsync($"中继 → {device.Label}");
    }

    private async void OnLanConnectApproved(DeviceItem device, DirectEndpoint endpoint)
    {
        _vm.Remote.PeerLabel = device.Label;
        _vm.Remote.BeginConnect();
        _vm.SelectedTabIndex = 1;
        await OpenMediaAsync($"局域网直连 {endpoint}");
    }

    /// <summary>
    /// 打开媒体通道。Service 侧媒体管道尚在并行实现，连接失败时**如实报错**并退回
    /// 演示帧源（带显式标注），而不是假装已连上。
    /// </summary>
    private async Task OpenMediaAsync(string target)
    {
        await CloseMediaAsync();

        try
        {
            _media = await MediaChannelClient.ConnectAsync(_options.MediaPipeName);
            _sessionHost = new RemoteSessionHost(_media);

            _sessionHost.FrameReady += frame => Dispatcher.InvokeAsync(() =>
            {
                _vm.Remote.DemoMode = false;
                _vm.Remote.UpdateFrame(frame.Width, frame.Height, frame.Pixels);
            });

            _sessionHost.ConfigReceived += cfg => Dispatcher.InvokeAsync(() => _vm.OnDesktopConfig(cfg));
            _sessionHost.StatsReceived += stats => Dispatcher.InvokeAsync(() => _vm.OnStats(stats));
            _sessionHost.Faulted += ex => Dispatcher.InvokeAsync(() => _vm.Remote.MarkError($"媒体通道错误：{ex.Message}"));

            _vm.Remote.MarkEstablished();
            _vm.StatusText = $"已连接（{target}）";
        }
        catch (PipeUnavailableException ex)
        {
            // 诚实降级：媒体管道不可用（Service 侧未实现或未启动），显示演示画面并标注。
            _vm.Remote.MarkError($"媒体通道不可用：{ex.Message}");
            StartDemoFrames();
            _vm.StatusText = "媒体通道不可用，已进入演示画面";
        }
    }

    private void StartDemoFrames()
    {
        _demoSource = new NullFrameSource();
        _vm.Remote.DemoMode = true;

        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _demoTimer.Tick += (_, _) =>
        {
            var frame = _demoSource!.Next();
            _vm.Remote.UpdateFrame(frame.Width, frame.Height, frame.Pixels);
        };
        _demoTimer.Start();
    }

    private async Task CloseMediaAsync()
    {
        // 断开会话/重连前先解绑输入转发（RawInputHook 的契约：退出全屏或断开时 Detach）。
        // 置空是为了重连后用新的 _media 重建，避免发到已关闭的旧通道。
        _rawInput?.Dispose();
        _rawInput = null;

        _demoTimer?.Stop();
        _demoTimer = null;
        _demoSource?.Dispose();
        _demoSource = null;
        _vm.Remote.DemoMode = false;

        _sessionHost?.Dispose();
        _sessionHost = null;

        if (_media is not null)
        {
            await _media.DisposeAsync();
            _media = null;
        }
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;
            // 全屏 = 独占远控：挂接 Raw Input 把键盘/鼠标转发到远端。
            // 只有媒体通道已连接才挂；演示模式下全屏只是本地放大画面。
            // 非全屏不挂——窗口里还有地址栏/按钮等本地 UI，Raw Input 会把
            // 本地打字也送到远端（双写），交互语义错误。
            TryAttachRawInput();
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            _isFullscreen = false;
            _rawInput?.Detach();
        }
    }

    private void TryAttachRawInput()
    {
        if (_media is null) return;
        try
        {
            _rawInput ??= new RawInputHook(_media);
            _rawInput.Attach(this);
        }
        catch (InvalidOperationException)
        {
            // Raw Input 注册失败不影响看画面，只是输入送不到远端。
        }
    }

    private async void OnDisconnectRequested()
    {
        await CloseMediaAsync();
        _vm.Remote.MarkDisconnected();
        _vm.SelectedTabIndex = 0;
        _vm.StatusText = "已断开";
    }

    protected override async void OnClosed(EventArgs e)
    {
        _controlledPollTimer?.Stop();
        _controlledPollTimer = null;
        await CloseMediaAsync();
        base.OnClosed(e);
    }
}
