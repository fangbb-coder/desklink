using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Diagnostics;
using System.Windows.Threading;
using DeskLink.Client;
using DeskLink.Client.Services;
using DeskLink.Client.Security;
using DeskLink.Client.ViewModels;
using DeskLink.Client.Views;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// WPF 冒烟：证明 App / MainWindow 是**真实可构造且能渲染**的 WPF 类型（而不是占位），
/// 且渲染过程不需要在线的 Service。
///
/// WPF 有线程亲和性：窗口必须在 STA 线程上构造并 Show，因此这里显式起一条 STA 线程。
///
/// ## 为什么必须真的 Show()（2026-09-29 血的教训）
///
/// 之前的版本只 `new MainWindow(vm, ...)` 构造后即弃，**不 Show、不度量**。
/// WPF 的数据绑定是**惰性**的：视觉树没被度量/渲染时，`<Run.Text>` 上的绑定根本不会解析，
/// 于是"绑到只读属性导致启动即崩"这类 bug 可以一路绿灯溜到真机。
///
/// 真实事故：`SettingsView.xaml` 用 `<Binding Path="ActiveRelayUrl"/>` 绑一个
/// **private setter** 的属性；`Run.Text` 的默认绑定模式是 TwoWay，WPF 启动时抛
/// `InvalidOperationException`，整个 DeskLink.Client.exe **每次启动即崩溃**，
/// 而 120 个单测全绿、B0 链路演练也全过。
///
/// 另一个坑：原测试在捕获到 `InvalidOperationException` 时走 `Skip.IfNot(...)`，
/// 等于把**恰好是这类 bug 的异常类型**当成"环境不支持"静默跳过。已删除该豁免。
/// </summary>
public class WpfSmokeTests
{
    [Fact]
    public void App_Type_Is_A_Real_Wpf_Application()
    {
        Assert.True(typeof(Application).IsAssignableFrom(typeof(App)));
        // 必须有公开无参构造，WPF 才能实例化它。
        Assert.NotNull(typeof(App).GetConstructor(Type.EmptyTypes));
    }

    /// <summary>
    /// 在 STA 线程上真正 <c>Show()</c> 窗口并跑完 Loaded+Render，让所有绑定解析一遍。
    /// 窗口摆到屏幕外（Left/Top = -32000）避免干扰真机桌面，但仍参与度量与渲染。
    /// </summary>
    private static (Exception? Failure, string? BindingErrors) RenderOnStaThread(
        Func<Window> factory, int timeoutSeconds = 30)
    {
        Exception? failure = null;
        var bindingErrors = new StringBuilder();

        var thread = new Thread(() =>
        {
            // 捕获 WPF 数据绑定错误：它们默认只进 trace、不抛异常，
            // 但"绑到不存在的属性"这类正是我们要拦的。
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Clear();
            PresentationTraceSources.DataBindingSource.Listeners.Add(
                new BindingErrorListener(bindingErrors));

            Window? window = null;
            try
            {
                window = factory();
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Left = -32000;
                window.Top = -32000;
                window.Show();

                // 逼 WPF 把 Loaded / Render / DataBind 队列全部跑完。
                window.Dispatcher.Invoke(
                    () => { }, DispatcherPriority.ContextIdle);
                window.Dispatcher.Invoke(
                    () => { }, DispatcherPriority.Loaded);
                window.UpdateLayout();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Clear();
                try { window?.Close(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { /* 无头宿主可能关不掉 */ }
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(timeoutSeconds)), "STA 线程渲染窗口超时");
        return (failure, bindingErrors.ToString());
    }

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _sink;
        public BindingErrorListener(StringBuilder sink) => _sink = sink;

        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _sink.AppendLine(message);
        }
    }

    [SkippableFact]
    public void MainWindow_Renders_Without_Live_Service()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF 仅能在 Windows 上构造");

        var (failure, bindingErrors) = RenderOnStaThread(() =>
        {
            var vm = new MainViewModel(
                new ServiceApi(ClientOptions.DefaultClientPipeName()),
                new PeerFingerprintStore(
                    Path.Combine(Path.GetTempPath(), $"desklink-wpf-{Guid.NewGuid():N}.json")));
            return new MainWindow(vm, ClientOptions.Parse(new[] { "--instance", "smoke" }));
        });

        // 注意：这里**不豁免** InvalidOperationException / XamlException。
        // 无头宿主确实可能加载不了 WPF 资源，但那种失败会稳定复现并给出明确消息，
        // 与"某个 View 的绑定写错"无法区分——所以宁可让它红，也别静默跳过。
        Assert.True(failure is null,
            $"MainWindow 渲染失败：{failure?.GetType().Name}: {failure?.Message}");
        Assert.True(string.IsNullOrWhiteSpace(bindingErrors),
            $"MainWindow 存在数据绑定错误：{bindingErrors}");
    }

    [SkippableFact]
    public void MainWindow_Default_Constructor_Renders()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF 仅能在 Windows 上构造");

        var (failure, bindingErrors) = RenderOnStaThread(() => new MainWindow());

        Assert.True(failure is null,
            $"MainWindow（默认构造）渲染失败：{failure?.GetType().Name}: {failure?.Message}");
        Assert.True(string.IsNullOrWhiteSpace(bindingErrors),
            $"MainWindow（默认构造）存在数据绑定错误：{bindingErrors}");
    }

    [SkippableFact]
    public void SettingsView_Binds_ActiveRelayUrl_Without_Crash()
    {
        // 回归用例：ActiveRelayUrl 只有 private setter（它表示"服务当前在用的地址"，
        // 不是用户可编辑的输入），SettingsView.xaml 必须显式 Mode=OneWay。
        // 忘了 Mode=OneWay → Run.Text 默认 TwoWay → 客户端启动即崩。
        //
        // 关键：必须把**真正的 SettingsViewModel** 设成 DataContext。
        // MainWindow.xaml 里是 `<views:SettingsView DataContext="{Binding Settings}" />`，
        // 绑到别的 VM 上根本命中不了这个属性，也就测不出这个 bug（2026-09-29 踩过）。
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF 仅能在 Windows 上构造");

        var (failure, bindingErrors) = RenderOnStaThread(() =>
        {
            var settingsVm = new ViewModels.SettingsViewModel(new FakeServiceApi());
            var window = new Window { Width = 900, Height = 700 };
            window.Content = new System.Windows.Controls.ContentControl
            {
                Content = new SettingsView(),
                DataContext = settingsVm,
            };
            return window;
        });

        Assert.True(failure is null,
            $"SettingsView 渲染失败（多半是绑定模式写错）：{failure?.GetType().Name}: {failure?.Message}");
        Assert.True(string.IsNullOrWhiteSpace(bindingErrors),
            $"SettingsView 存在数据绑定错误：{bindingErrors}");
    }

    [SkippableFact]
    public void MainWindow_Real_Settings_Tab_Renders_With_Its_Own_DataContext()
    {
        // 上面那条是"构造正确上下文"的定向测试；这条走真实装配路径
        // （MainWindow.xaml 自己把 SettingsView 的 DataContext 接到 SettingsViewModel），
        // 保证接线本身没被改坏。
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF 仅能在 Windows 上构造");

        MainViewModel? captured = null;
        var (failure, bindingErrors) = RenderOnStaThread(() =>
        {
            var vm = new MainViewModel(
                new ServiceApi(ClientOptions.DefaultClientPipeName()),
                new PeerFingerprintStore(
                    Path.Combine(Path.GetTempPath(), $"desklink-wpf-{Guid.NewGuid():N}.json")));
            captured = vm;
            return new MainWindow(vm, ClientOptions.Parse(new[] { "--instance", "smoke" }));
        });

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Settings);
        Assert.True(failure is null,
            $"MainWindow 渲染失败：{failure?.GetType().Name}: {failure?.Message}");
        Assert.True(string.IsNullOrWhiteSpace(bindingErrors),
            $"MainWindow 存在数据绑定错误：{bindingErrors}");
    }

    [Fact]
    public void Xaml_Bindings_Target_Properties_That_Can_Accept_TwoWay()
    {
        // 静态护栏：SettingsView.xaml 里凡是把绑定放在 <Run.Text> 上的，
        // Path 指向的 VM 属性必须有 public setter，否则运行时会抛。
        // 这条不需要 STA 线程，任何环境都能跑，属于"第一道网"。
        var xamlPath = LocateClientFile(Path.Combine("Views", "SettingsView.xaml"));
        Assert.NotNull(xamlPath);

        var xaml = File.ReadAllText(xamlPath!);
        var runTextBlock = ExtractRunTextBlocks(xaml);
        foreach (var (path, mode) in runTextBlock)
        {
            if (string.Equals(mode, "OneWay", StringComparison.OrdinalIgnoreCase)) continue;

            var prop = typeof(ViewModels.SettingsViewModel)
                .GetProperty(path)
                ?? throw new Xunit.Sdk.XunitException(
                    $"SettingsView.xaml 的 <Run.Text> 绑定了不存在的属性：{path}");
            var setter = prop.GetSetMethod(nonPublic: false);
            Assert.True(setter is not null,
                $"SettingsView.xaml 把 <Run.Text> 绑到 {path}，但它没有 public setter。" +
                "Run.Text 默认 TwoWay，运行时会在启动时抛 InvalidOperationException。" +
                "请显式写 Mode=OneWay。");
        }
    }

    private static List<(string Path, string? Mode)> ExtractRunTextBlocks(string xaml)
    {
        var result = new List<(string, string?)>();
        int i = 0;
        while (true)
        {
            int start = xaml.IndexOf("<Run.Text", i, StringComparison.Ordinal);
            if (start < 0) break;
            int end = xaml.IndexOf("</Run.Text>", start, StringComparison.Ordinal);
            if (end < 0) break;
            var block = xaml[start..end];
            int p = block.IndexOf("Path=\"", StringComparison.Ordinal);
            if (p >= 0)
            {
                int q = block.IndexOf('"', p + 6);
                var path = block[(p + 6)..q];
                string? mode = null;
                int m = block.IndexOf("Mode=\"", StringComparison.Ordinal);
                if (m >= 0)
                {
                    int m2 = block.IndexOf('"', m + 6);
                    mode = block[(m + 6)..m2];
                }
                result.Add((path, mode));
            }
            i = end + 1;
        }
        return result;
    }

    private static string? LocateClientFile(string relative)
    {
        // 测试 bin 目录 → 找仓库里的 Client 项目文件
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Client", "DeskLink.Client", relative);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    [Fact]
    public void ClientOptions_Parses_Pipe_Overrides()
    {
        var defaults = ClientOptions.Parse(null);
        Assert.Equal("DeskLink.Client.default", defaults.PipeName);
        Assert.Equal("DeskLink.Media.default", defaults.MediaPipeName);

        var custom = ClientOptions.Parse(new[] { "--pipe", "DeskLink.Client.alpha", "--media-pipe", "DeskLink.Media.alpha" });
        Assert.Equal("DeskLink.Client.alpha", custom.PipeName);
        Assert.Equal("DeskLink.Media.alpha", custom.MediaPipeName);

        // --instance 必须同时改写客户端与媒体两条管道名，缺一条媒体通道就永远协商不上。
        var byInstance = ClientOptions.Parse(new[] { "--instance", "DL-CTRL" });
        Assert.Equal("DeskLink.Client.dl-ctrl", byInstance.PipeName);
        Assert.Equal("DeskLink.Media.dl-ctrl", byInstance.MediaPipeName);
    }

    [Fact]
    public void Client_Defaults_To_Pipe_Named_Default_Not_The_DataDir_Instance()
    {
        // 这条是 Panel 的配套契约：面板用 `--data-dir <dir>` 起服务，实例名 = 路径末段（小写），
        // 所以面板启动客户端时**必须**显式传 --instance，否则客户端去连
        // `DeskLink.Client.default` 这个根本不存在的管道，表现为"打开了但全是连接失败"。
        var noArgs = ClientOptions.Parse(null);
        Assert.Equal("DeskLink.Client.default", noArgs.PipeName);

        var withInstance = ClientOptions.Parse(new[] { "--instance", "dl-ctrl" });
        Assert.NotEqual(noArgs.PipeName, withInstance.PipeName);
    }
}
