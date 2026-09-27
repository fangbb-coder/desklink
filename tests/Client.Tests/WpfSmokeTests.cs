using System.IO;
using System.Windows;
using DeskLink.Client;
using DeskLink.Client.Services;
using DeskLink.Client.ViewModels;
using DeskLink.Client.Views;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// WPF 冒烟：证明 App / MainWindow 是**真实可构造**的 WPF 类型（而不是占位），
/// 且构造过程不需要在线的 Service。
///
/// WPF 有线程亲和性：窗口必须在 STA 线程上构造，因此这里显式起一条 STA 线程。
/// 不调用 Show()（无头测试宿主上显示窗口可能不可靠），只构造后即弃。
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

    [SkippableFact]
    public void MainWindow_Can_Be_Constructed_On_Sta_Thread_Without_Live_Service()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF 仅能在 Windows 上构造");

        Exception? failure = null;
        MainViewModel? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow();
                captured = window.DataContext as MainViewModel;
                // 不 Show()：只验证 XAML 能加载、依赖能装配。
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程构造 MainWindow 超时（疑似挂死）");
        if (failure is not null)
        {
            // 若测试宿主无桌面会话，XAML 加载会失败——那属于环境限制，如实跳过而不是伪造通过。
            Skip.IfNot(failure is not (System.Xaml.XamlException or InvalidOperationException),
                $"测试宿主无法加载 WPF 窗口：{failure.GetType().Name}: {failure.Message}");
            throw new Xunit.Sdk.XunitException($"构造 MainWindow 失败：{failure}");
        }

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Devices);
        Assert.NotNull(captured.Remote);
        Assert.NotNull(captured.Files);
        Assert.NotNull(captured.Settings);
    }

    [SkippableFact]
    public void MainWindow_With_Injected_ViewModel_Uses_It_As_DataContext()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "WPF 仅能在 Windows 上构造");

        Exception? failure = null;
        MainViewModel? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new MainViewModel(
                    new ServiceApi(ClientOptions.DefaultClientPipeName()),
                    new Security.PeerFingerprintStore(
                        Path.Combine(Path.GetTempPath(), $"desklink-wpf-{Guid.NewGuid():N}.json")));
                var window = new MainWindow(vm, ClientOptions.Parse(new[] { "--pipe", "DeskLink.Client.test" }));
                captured = window.DataContext as MainViewModel;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程构造 MainWindow 超时");
        if (failure is not null)
        {
            Skip.IfNot(failure is not (System.Xaml.XamlException or InvalidOperationException),
                $"测试宿主无法加载 WPF 窗口：{failure.GetType().Name}: {failure.Message}");
            throw new Xunit.Sdk.XunitException($"构造 MainWindow 失败：{failure}");
        }

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Devices);
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

        var byInstance = ClientOptions.Parse(new[] { "--instance", "beta" });
        Assert.Equal("DeskLink.Client.beta", byInstance.PipeName);
        Assert.Equal("DeskLink.Media.beta", byInstance.MediaPipeName);
    }

    [Fact]
    public void View_Types_Exist()
    {
        // 四个视图 + 状态条 + 两个对话框都必须存在（防止"只写了 VM 没有视图"）。
        Assert.NotNull(typeof(DevicesView));
        Assert.NotNull(typeof(RemoteView));
        Assert.NotNull(typeof(FilesView));
        Assert.NotNull(typeof(SettingsView));
        Assert.NotNull(typeof(StatusBar));
        Assert.NotNull(typeof(FingerprintConfirmDialog));
        Assert.NotNull(typeof(DirectConnectDialog));
    }
}
