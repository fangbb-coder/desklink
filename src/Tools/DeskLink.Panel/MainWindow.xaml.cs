using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DeskLink.Panel.Services;
using DeskLink.Panel.ViewModels;

namespace DeskLink.Panel;

public partial class MainWindow : Window
{
    private readonly IServiceHost _host;
    private readonly MainViewModel _vm;
    private readonly DispatcherTimer? _statusTimer;
    private bool _elevationRestarting;

    /// <summary>生产入口：真实 <see cref="LocalServiceHost"/> + 已落盘设置 + 2 秒状态轮询。</summary>
    public MainWindow() : this(null, null, startServicePolling: true)
    {
    }

    /// <summary>
    /// 可注入宿主的构造。测试用它塞 <c>FakeServiceHost</c>，把「真起 Service 子进程」这一步
    /// 从 WPF 渲染冒烟里彻底摘掉——否则冒烟测试会在 CI/本机拉起真实服务进程。
    /// </summary>
    /// <param name="host">null = 用真实 <see cref="LocalServiceHost"/>。</param>
    /// <param name="settings">null = 从 %APPDATA%\DeskLink\panel.json 载入。</param>
    /// <param name="startServicePolling">false = 不起 2 秒轮询定时器、不在 Loaded 时自检。</param>
    public MainWindow(IServiceHost? host, PanelSettings? settings, bool startServicePolling)
    {
        InitializeComponent();

        settings ??= host is null ? PanelSettings.Load() : new PanelSettings();
        _host = host ?? new LocalServiceHost(settings, Dispatcher);
        _vm = new MainViewModel(_host, settings);
        DataContext = _vm;

        // 日志集合 → 文本框。刻意不在 XAML 里绑 ObservableCollection 到 TextBox.Text：
        // 那样会走 ToString() 得到类型名。这里自己追加，顺带做行数上限与自动滚动。
        _vm.Log.CollectionChanged += OnLogChanged;

        // 提权已发起：等当前命令跑完就退位，把界面让给管理员权限的新实例。
        // 走命令统一收尾而不是绑在某个按钮上——触发提权的路径有好几条
        // （放行入站端口、一键准备被控端…），漏掉任何一条都会留下两个并存的面板实例，
        // 用户对着两个"已就绪"横幅不知道哪个是真的。
        _vm.ElevationRequested += () => _elevationRestarting = true;
        _vm.ElevationRetire = RetireIfElevating;
        _vm.CommandFailed += ex => MessageBox.Show(
            this, ex.Message, "DeskLink 控制面板", MessageBoxButton.OK, MessageBoxImage.Error);

        if (!startServicePolling) return;

        // 服务没运行时照常轮询：用户可能正在等自己点「启动服务」那一刻起来。
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _statusTimer.Tick += async (_, _) =>
        {
            if (_vm.IsBusy) return;   // 别和正在跑的一次性命令抢 UI 线程
            try { await _vm.RefreshStatusAsync(); }
            catch (Exception) { /* 轮询失败不该打断界面；下一次再试 */ }
        };
        _statusTimer.Start();

        Loaded += async (_, _) => await _vm.InitializeAsync();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (var item in e.NewItems)
        {
            LogBox.AppendText(item?.ToString() ?? "");
            LogBox.AppendText(Environment.NewLine);
        }
        // 只保留尾部，避免长时间运行把文本框撑到几十 MB。
        if (LogBox.LineCount > 500)
        {
            var cut = LogBox.GetCharacterIndexFromLineIndex(200);
            LogBox.Select(0, cut);
            LogBox.SelectedText = "";
        }
        LogBox.ScrollToEnd();
    }

    /// <summary>
    /// 包一层异常兜底 + 提权后自动退位。
    ///
    /// 触发方式刻意改成 <c>Command</c>（不再是 <c>Click</c>）：两个「一键准备」Command 的
    /// <c>CanExecute</c> 是 <c>!IsBusy</c>，绑 Click 等于把这个重入保护绕开——
    /// 第二次点会重跑一半流程，最后照样弹一句"已就绪"，而服务根本没起来。
    /// 异常兜底同步下沉到 <see cref="ViewModels.MainViewModel.RunCommand"/>，这里不再重复兜。
    /// </summary>
    private void RetireIfElevating()
    {
        if (_elevationRestarting) Close();
    }

    private void OnCopyPublicKeyClick(object sender, RoutedEventArgs e) => Copy(_vm.PublicKey, "公钥");

    private void OnCopyEndpointClick(object sender, RoutedEventArgs e) => Copy(_vm.LocalEndpoint, "本机地址");

    /// <summary>
    /// 写系统剪贴板。
    ///
    /// 剪贴板是**全局独占**资源：任何别的进程（剪贴板管理器、远程桌面、输入法、
    /// 甚至刚退出的程序）都可能短时间持有它，此时 <see cref="Clipboard.SetText(string)"/>
    /// 直接抛 <c>CLIPBRD_E_CANT_OPEN (0x800401D0)</c>。这是 WPF 复制按钮的经典故障，
    /// 正确做法是**带退避重试**，而不是把错误甩给用户让他手动选中复制。
    /// </summary>
    private static void Copy(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        const int attempts = 6;
        for (int i = 0; ; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (Exception ex) when (
                ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                && i < attempts - 1)
            {
                // 退避 80ms → 总计约 400ms，足够覆盖绝大多数短暂占用
                Thread.Sleep(80);
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                MessageBox.Show(
                    $"复制{what}失败（剪贴板被其他程序占用）。\n\n请手动选中上面的文本复制。\n\n{ex.Message}",
                    "DeskLink 控制面板", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        // 顺序有讲究：先问，确认要关了才拆。
        // 原来 Stop 定时器 / 摘事件排在确认之前，于是用户点「否」之后面板就成了僵尸——
        // 状态永远不刷新、日志不再滚动，而且没有任何地方会把它们装回去。
        if (_host.IsServiceRunning)
        {
            var answer = MessageBox.Show(this,
                "本机服务正在运行。\n\n关闭面板会一并停止服务，被控端将无法再被连接。\n\n确定关闭吗？",
                "DeskLink 控制面板", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) { e.Cancel = true; return; }
        }

        _statusTimer?.Stop();
        _vm.Log.CollectionChanged -= OnLogChanged;

        // 只有真实的 LocalServiceHost 才持有子进程句柄需要释放；
        // 测试注入的替身不实现 IAsyncDisposable，这里自然跳过。
        try
        {
            if (_host is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(true);
        }
        catch (Exception) { /* 关闭阶段不再打扰用户 */ }

        _vm.Dispose();
        base.OnClosing(e);
    }
}
