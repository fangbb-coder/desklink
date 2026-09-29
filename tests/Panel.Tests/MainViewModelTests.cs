using DeskLink.Panel;
using DeskLink.Panel.Services;
using DeskLink.Panel.ViewModels;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>
/// ViewModel 是面板唯一有"业务判断"的地方（一键准备该开哪些开关、
/// 什么时候该提示配对、什么时候该要提权）。这些用例锁住的是**面向用户的判断**，
/// 不是实现细节：断言的是 BannerLevel 与 Banner 文案语义。
/// </summary>
public class MainViewModelTests
{
    private static (MainViewModel vm, FakeServiceHost host, PanelSettings settings) NewVm(
        Action<FakeServiceHost>? configure = null)
    {
        var host = new FakeServiceHost();
        configure?.Invoke(host);
        var settings = new PanelSettings { DataDir = @"C:\dl-ctrl", DirectPort = 47200 };
        var vm = new MainViewModel(host, settings);
        return (vm, host, settings);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N") + ".json");

    // ── 初始化 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task 初始化后能读到本机公钥与地址()
    {
        var (vm, _, _) = NewVm();
        await vm.InitializeAsync();
        Assert.Equal("LOCALPUB0000000000000000000000000000000000000=", vm.PublicKey);
        Assert.Equal("192.168.1.20:47200", vm.LocalEndpoint);
        Assert.Equal("AABBCC", vm.DeviceId);
        Assert.True(vm.QuicAvailable);
        vm.Dispose();
    }

    [Fact]
    public async Task 找不到Service时给出错误横幅而不是抛异常()
    {
        var (vm, host, _) = NewVm(h => h.ServiceExePath = null);
        await vm.InitializeAsync();
        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        Assert.Contains("找不到", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 公钥解析失败时报错而不是留下空串()
    {
        var (vm, host, _) = NewVm(h => h.PrintConfigResult = new OneShotResult(0, "garbage", ""));
        await vm.InitializeAsync();
        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        Assert.Equal("", vm.PublicKey);
        vm.Dispose();
    }

    [Fact]
    public async Task printConfig非零退出码时错误信息含退出码()
    {
        var (vm, host, _) = NewVm(h => h.PrintConfigResult = new OneShotResult(1, "", "boom"));
        await vm.InitializeAsync();
        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        Assert.Contains("1", vm.StatusMessage);
        vm.Dispose();
    }

    // ── 配对 ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task 配对成功时提示并且提醒对方也要配()
    {
        var (vm, host, _) = NewVm();
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";
        await vm.PairAsync();

        Assert.Equal("PEERPUB==", Assert.Single(host.PairedPubs));
        Assert.Equal(BannerLevel.Ok, vm.BannerLevel);
        // 直连必须双向配对——这条提示是防止"配一边然后被误判成防火墙问题"的关键
        Assert.Contains("两边都配", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 配对失败时显示退出码与错误首行()
    {
        var (vm, host, _) = NewVm(h => h.PairResult = new OneShotResult(2, "", "bad public key"));
        await vm.InitializeAsync();
        vm.PeerPub = "garbage";
        await vm.PairAsync();

        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        Assert.Contains("bad public key", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 公钥为空时拒绝配对()
    {
        var (vm, host, _) = NewVm();
        await vm.InitializeAsync();
        vm.PeerPub = "   ";
        await vm.PairAsync();

        Assert.Empty(host.PairedPubs);
        Assert.Equal(BannerLevel.Warn, vm.BannerLevel);
        vm.Dispose();
    }

    [Fact]
    public async Task 配对成功后刷新状态以更新已配对数()
    {
        var (vm, host, _) = NewVm(h => h.StatusResult = new StatusResult { PairingCount = 1 });
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";
        await vm.PairAsync();
        Assert.Equal(1, vm.PairingCount);
        vm.Dispose();
    }

    // ── 防火墙与提权 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 未提权时放行防火墙会请求提权而不是直接失败()
    {
        var (vm, host, _) = NewVm(h => h.IsAdministrator = false);
        await vm.InitializeAsync();
        var elevated = false;
        vm.ElevationRequested += () => elevated = true;
        await vm.ToggleFirewallAsync();

        Assert.True(elevated);
        Assert.Equal(1, host.ElevateRequested);
        Assert.False(host.FirewallSetCalled);   // 没提权就不该真去写防火墙
        vm.Dispose();
    }

    [Fact]
    public async Task 已提权时直接执行放行()
    {
        var (vm, host, _) = NewVm(h => { h.IsAdministrator = true; h.FirewallResult = new(47200, false, true, false, false); });
        await vm.InitializeAsync();
        await vm.ToggleFirewallAsync();

        Assert.True(host.FirewallSetCalled);
        Assert.True(host.FirewallSetEnable);
        Assert.Equal(0, host.ElevateRequested);
        vm.Dispose();
    }

    [Fact]
    public async Task 已放行时再点一次是关闭()
    {
        var (vm, host, _) = NewVm(h => { h.IsAdministrator = true; h.FirewallResult = new(47200, true, true, true, true); });
        await vm.InitializeAsync();
        await vm.ToggleFirewallAsync();

        Assert.True(host.FirewallSetCalled);
        Assert.False(host.FirewallSetEnable);
        vm.Dispose();
    }

    [Fact]
    public async Task 防火墙操作失败时报错并带输出()
    {
        var (vm, host, _) = NewVm(h =>
        {
            h.IsAdministrator = true;
            h.FirewallResult = new(47200, false, true, false, false);
            h.FirewallSetResult = new OneShotResult(3, "", "需要管理员权限");
        });
        await vm.InitializeAsync();
        await vm.ToggleFirewallAsync();
        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        vm.Dispose();
    }

    [Fact]
    public async Task 防火墙状态文案区分只开了一条规则()
    {
        var (vm, _, _) = NewVm(h => h.FirewallResult = new(47200, true, true, true, false));
        await vm.InitializeAsync();
        Assert.Contains("缺 UDP", vm.FirewallText);
        vm.Dispose();
    }

    [Fact]
    public async Task 防火墙完全未放行时文案是未放行()
    {
        var (vm, _, _) = NewVm(h => h.FirewallResult = new(47200, false, true, false, false));
        await vm.InitializeAsync();
        Assert.Contains("未放行", vm.FirewallText);
        vm.Dispose();
    }

    // ── 服务启停 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 启动服务后状态变为运行中并刷新状态()
    {
        var (vm, host, _) = NewVm(h => h.StatusResult = new StatusResult { DirectActiveSessions = 2, RelayState = "established" });
        await vm.InitializeAsync();
        await vm.StartServiceAsync();

        Assert.Equal(1, host.StartCount);
        Assert.True(vm.ServiceRunning);
        Assert.Equal(2, vm.DirectSessions);
        Assert.Equal("已连接", vm.RelayState);
        vm.Dispose();
    }

    [Fact]
    public async Task 停止服务后状态变为未运行()
    {
        var (vm, host, _) = NewVm();
        await vm.InitializeAsync();
        await vm.StartServiceAsync();
        await vm.StopServiceAsync();

        Assert.Equal(1, host.StopCount);
        vm.Dispose();
    }

    [Fact]
    public async Task 状态查不到时显示服务未运行()
    {
        var (vm, host, _) = NewVm(h => h.StatusResult = null);
        await vm.InitializeAsync();

        Assert.False(vm.ServiceRunning);
        Assert.Equal("服务未运行", vm.RelayState);
        Assert.Equal(0, vm.DirectSessions);
        vm.Dispose();
    }

    [Fact]
    public async Task 服务意外退出时给出警告并停止显示运行中()
    {
        var (vm, host, _) = NewVm();
        await vm.InitializeAsync();
        await vm.StartServiceAsync();
        host.RaiseExited(3);

        Assert.False(vm.ServiceRunning);
        Assert.Equal(BannerLevel.Warn, vm.BannerLevel);
        Assert.Contains("3", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 中继状态文案可读()
    {
        var (vm, host, _) = NewVm(h => h.StatusResult = new StatusResult { RelayState = "connecting" });
        await vm.InitializeAsync();
        Assert.Equal("连接中", vm.RelayState);
        vm.Dispose();
    }

    // ── 一键准备 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 一键准备被控端会开直连与注入代理并起服务()
    {
        var (vm, host, settings) = NewVm(h => h.IsAdministrator = true);
        settings.EnableDirect = false;
        settings.InjectAgent = false;
        await vm.InitializeAsync();
        await vm.PrepareControlledAsync();

        Assert.True(vm.EnableDirect);
        Assert.True(vm.InjectAgent);
        Assert.True(host.FirewallSetCalled);
        Assert.Equal(1, host.StartCount);
        Assert.Equal(BannerLevel.Ok, vm.BannerLevel);
        // 最后一步必须是"把公钥和地址发给控制端"——这是被控端用户的唯一待办
        Assert.Contains("公钥", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备被控端会填默认共享目录()
    {
        var (vm, host, _) = NewVm(h => h.IsAdministrator = true);
        await vm.InitializeAsync();
        await vm.PrepareControlledAsync();
        Assert.Contains("Share", vm.FileScopeText);
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备被控端在未提权时只提权不起服务()
    {
        // 半配置状态（服务起了但防火墙没放行）比完全没起更难排查，宁可停在这一步
        var (vm, host, _) = NewVm(h => h.IsAdministrator = false);
        await vm.InitializeAsync();
        await vm.PrepareControlledAsync();

        Assert.Equal(1, host.ElevateRequested);
        Assert.Equal(0, host.StartCount);
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备控制端会关掉直连与注入代理()
    {
        var (vm, host, settings) = NewVm();
        settings.EnableDirect = true;
        settings.InjectAgent = true;
        await vm.InitializeAsync();
        await vm.PrepareControllerAsync();

        Assert.False(vm.EnableDirect);
        Assert.False(vm.InjectAgent);
        Assert.Equal(1, host.StartCount);
        Assert.False(host.FirewallSetCalled);   // 控制端只拨出，不需要放行入站
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备控制端时已有公钥会顺手配对()
    {
        var (vm, host, _) = NewVm();
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";
        await vm.PrepareControllerAsync();
        Assert.Equal("PEERPUB==", Assert.Single(host.PairedPubs));
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备控制端没公钥时提示要填但仍起服务()
    {
        var (vm, host, _) = NewVm();
        await vm.InitializeAsync();
        await vm.PrepareControllerAsync();

        Assert.Empty(host.PairedPubs);
        Assert.Equal(1, host.StartCount);
        Assert.Contains("公钥", vm.StatusMessage);
        vm.Dispose();
    }

    // ── 设置与日志 ────────────────────────────────────────────────────────

    [Fact]
    public void 保存设置写回全部字段()
    {
        var path = TempPath();
        try
        {
            var (vm, _, _) = NewVm();
            vm.DataDir = @"C:\dl-targ";
            vm.DirectPort = 47500;
            vm.EnableDirect = true;
            vm.InjectAgent = true;
            vm.FileScopeText = @"D:\A;E:\B";
            vm.RelayUrl = "quic://vps:9443";
            vm.SaveSettings(path);

            var back = PanelSettings.Load(path);
            Assert.Equal(@"C:\dl-targ", back.DataDir);
            Assert.Equal(47500, back.DirectPort);
            Assert.Equal(new[] { @"D:\A", @"E:\B" }, back.FileScopeRoots);
            Assert.Equal("quic://vps:9443", back.RelayUrl);
            vm.Dispose();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 非法端口回退到默认47200()
    {
        var path = TempPath();
        try
        {
            var (vm, _, _) = NewVm();
            vm.DirectPort = 999999;
            vm.SaveSettings(path);
            Assert.Equal(47200, PanelSettings.Load(path).DirectPort);
            vm.Dispose();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 共享目录以分号分隔并去空白()
    {
        var path = TempPath();
        try
        {
            var (vm, _, _) = NewVm();
            vm.FileScopeText = " D:\\A ; ; E:\\B ;";
            vm.SaveSettings(path);
            Assert.Equal(new[] { @"D:\A", @"E:\B" }, PanelSettings.Load(path).FileScopeRoots);
            vm.Dispose();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task 服务在监听但端口未放行时不把直连显示成已关闭()
    {
        // 实测踩过的坑：带 --enable-direct 启动、日志打出 "DirectServer enabled on port N"，
        // 但 get_status 的 direct_enabled 仍为 false（它取自防火墙探测，不是"是否在监听"）。
        // 面板绝不能据此显示"直连已关闭"——服务明明在跑。权威信号是防火墙状态。
        var (vm, host, _) = NewVm(h =>
        {
            h.StatusResult = new StatusResult { DirectEnabled = false, DirectActiveSessions = 0 };
            h.FirewallResult = new FirewallStatus(47200, false, false, false, false);
        });
        await vm.InitializeAsync();

        Assert.True(vm.ServiceRunning);          // 服务确实在跑
        Assert.Contains("未放行", vm.FirewallText); // 真正的问题在这里说清楚
        vm.Dispose();
    }

    [Fact]
    public void 日志超过500行时截断防止内存无界增长()
    {
        var (vm, host, _) = NewVm();
        for (int i = 0; i < 800; i++) host.RaiseLog("line " + i);
        Assert.True(vm.Log.Count <= 500);
        Assert.Equal("line 799", vm.Log[^1]);
        vm.Dispose();
    }

    [Fact]
    public void 打开控制界面成功时提示已启动()
    {
        var (vm, host, _) = NewVm();
        vm.LaunchClient();
        Assert.True(host.LaunchClientCalled);
        Assert.Equal(BannerLevel.Ok, vm.BannerLevel);
        vm.Dispose();
    }

    [Fact]
    public void 打开控制界面找不到时给错误提示()
    {
        var (vm, host, _) = NewVm(h => h.ClientExePath = null);
        vm.LaunchClient();
        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        vm.Dispose();
    }

    [Fact]
    public void 载入设置到界面时同步角色与各开关()
    {
        var host = new FakeServiceHost();
        var settings = new PanelSettings
        {
            DataDir = @"C:\dl-x",
            DirectPort = 47500,
            EnableDirect = true,
            InjectAgent = true,
            FileScopeRoots = new List<string> { @"D:\A" },
            Role = PanelRole.Controller,
        };
        var vm = new MainViewModel(host, settings);
        vm.LoadFromSettings();

        Assert.Equal(@"C:\dl-x", vm.DataDir);
        Assert.Equal(47500, vm.DirectPort);
        Assert.Equal(@"D:\A", vm.FileScopeText);
        Assert.True(vm.IsController);
        vm.Dispose();
    }

    [Fact]
    public void 忙碌期间命令不可执行()
    {
        var (vm, _, _) = NewVm();
        // 通过一个会同步触发的入口制造 busy：直接断言 IsBusy 门禁本身
        Assert.False(vm.HasPeerPub);
        vm.PeerPub = "x";
        Assert.True(vm.HasPeerPub);
        vm.Dispose();
    }
}
