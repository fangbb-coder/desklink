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
    private readonly MainViewModel _vm;
    private readonly LocalServiceHost _host;
    private readonly DispatcherTimer _statusTimer;
    private bool _elevationRestarting;

    public MainWindow()
    {
        InitializeComponent();

        var settings = PanelSettings.Load();
        _host = new LocalServiceHost(settings, Dispatcher);
        _vm = new MainViewModel(_host, settings);
        DataContext = _vm;

        // 日志集合 → 文本框。刻意不在 XAML 里绑 ObservableCollection 到 TextBox.Text：
        // 那样会走 ToString() 得到类型名。这里自己追加，顺带做行数上限与自动滚动。
        _vm.Log.CollectionChanged += OnLogChanged;

        _vm.ElevationRequested += () => _elevationRestarting = true;

        // 服务没运行时照常轮询：用户可能正在等自己点「启动服务」那一刻起来。
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _statusTimer.Tick += async (_, _) =>
        {
            if (_vm.IsBusy) return;   // 别和正在跑的一次性命令抢 UI 线程
            try { await _vm.RefreshStatusAsync(); }
            catch (Exception ex) { /* 轮询失败不该打断界面；下一次再试 */ }
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

    private void OnPrepareControlledClick(object sender, RoutedEventArgs e) => _ = RunGuarded(_vm.PrepareControlledAsync);

    private void OnPrepareControllerClick(object sender, RoutedEventArgs e) => _ = RunGuarded(_vm.PrepareControllerAsync);

    private async Task RunGuarded(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DeskLink 控制面板", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // 提权重启已发起：关掉自己，把位置让给管理员权限的新实例。
            if (_elevationRestarting) Close();
        }
    }

    private void OnCopyPublicKeyClick(object sender, RoutedEventArgs e) => Copy(_vm.PublicKey, "公钥");

    private void OnCopyEndpointClick(object sender, RoutedEventArgs e) => Copy(_vm.LocalEndpoint, "本机地址");

    private static void Copy(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            MessageBox.Show($"复制失败，请手动选中复制。\n\n{ex.Message}", "DeskLink 控制面板",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        _statusTimer.Stop();
        _vm.Log.CollectionChanged -= OnLogChanged;

        if (_host.IsServiceRunning)
        {
            var answer = MessageBox.Show(this,
                "本机服务正在运行。\n\n关闭面板会一并停止服务，被控端将无法再被连接。\n\n确定关闭吗？",
                "DeskLink 控制面板", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) { e.Cancel = true; return; }
        }

        try { await _host.DisposeAsync(); }
        catch (Exception ex) { /* 关闭阶段不再打扰用户 */ }

        _vm.Dispose();
        base.OnClosing(e);
    }
}
