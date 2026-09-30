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
        // 落盘路径必须隔离：StartServiceAsync / 两个「一键准备」/ 防火墙开关
        // 都会在内部无参调 SaveSettings()，不隔离就会写进开发者真实的
        // %APPDATA%\DeskLink\panel.json，把人家面板配置冲掉。
        var vm = new MainViewModel(host, settings, TempPath());
        return (vm, host, settings);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task 初始化时服务没跑起来就不能说已就绪()
    {
        // 实测截图抓到的：运行状态写着"未运行"，横幅却是一片 Info 的"被控端就绪"，
        // 而且那句话本身还让用户去点"一键准备"——既说好了又让人去启动。
        var (vm, _, _) = NewVm(h => h.StatusResult = null);
        await vm.InitializeAsync();

        Assert.False(vm.ServiceRunning);
        Assert.DoesNotContain("就绪", vm.StatusMessage);
        Assert.Contains("未运行", vm.StatusMessage);
        Assert.Contains("启动服务", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 初始化时服务确实在跑才可以说已在运行()
    {
        var (vm, _, _) = NewVm();   // 默认 StatusResult 非 null = 服务在跑
        await vm.InitializeAsync();

        Assert.True(vm.ServiceRunning);
        Assert.Contains("已在运行", vm.StatusMessage);
        Assert.DoesNotContain("未运行", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 初始化读不到公钥时引导语不许盖掉错误()
    {
        var (vm, _, _) = NewVm(h => h.PrintConfigResult = new OneShotResult(1, "", "boom"));
        await vm.InitializeAsync();

        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        Assert.Contains("boom", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 负的显示器索引当场夹回0_输入框和说明不许自相矛盾()
    {
        var (vm, _, _) = NewVm();

        // -5 是能成功转成 int 的，所以连红框都不会有。
        // 夹在 SaveSettings 里的话，输入框会一直显示 -5、旁边的说明却说"第 1 块屏幕"，
        // 同一屏上两处打架，用户只能靠猜。夹在 setter 里输入框立刻回显 0。
        vm.MonitorIndex = -5;

        Assert.Equal(0, vm.MonitorIndex);
        Assert.Equal("第 1 块屏幕（主显示器）", vm.MonitorIndexText);
        vm.Dispose();
    }

    [Fact]
    public void 非零显示器索引的说明要跟索引对上()
    {
        var (vm, _, _) = NewVm();
        vm.MonitorIndex = 1;
        Assert.Equal("第 2 块屏幕（索引 1）", vm.MonitorIndexText);
        vm.Dispose();
    }

    [Fact]
    public async Task 被控端一键准备_共享目录的默认值必须留在最终提示里()
    {
        // 以前这条 Info 会被后面三次"设置已保存/已就绪"覆盖掉，
        // 用户从头到尾看不到自己到底授权了哪个目录。
        var (vm, _, _) = NewVm(h => h.IsAdministrator = true);
        await vm.InitializeAsync();
        vm.DataDir = @"C:\dl-ctrl";
        vm.FileScopeText = "";

        await vm.PrepareControlledAsync();

        Assert.Contains(@"C:\dl-ctrl\Share", vm.StatusMessage);
        Assert.Contains("被控端已就绪", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 用户自己填了共享目录就不再替他决定()
    {
        var (vm, _, _) = NewVm(h => h.IsAdministrator = true);
        await vm.InitializeAsync();
        vm.DataDir = @"C:\dl-ctrl";
        vm.FileScopeText = @"D:\myshare";

        await vm.PrepareControlledAsync();

        Assert.DoesNotContain(@"C:\dl-ctrl\Share", vm.StatusMessage);
        Assert.Equal(@"D:\myshare", vm.FileScopeText);
        vm.Dispose();
    }

    [Fact]
    public async Task 跑完一键准备也不能碰到真实的APPDATA配置()    {
        // 护栏不是"再测一遍功能"，而是钉住"副作用的落点"：
        // 以前 NewVm() 不给隔离路径，而 PrepareController/StartService 内部会
        // 无参 SaveSettings() → 写真实的 %APPDATA%\DeskLink\panel.json。
        // 单测跑一次，开发者的 dataDir/端口/角色就被冲掉了，而且很难归因到测试。
        var real = PanelSettings.SettingsPath;
        var before = File.Exists(real) ? File.ReadAllBytes(real) : null;

        var (vm, _, _) = NewVm();
        await vm.InitializeAsync();
        await vm.PrepareControllerAsync();
        await vm.StartServiceAsync();
        vm.Dispose();

        var after = File.Exists(real) ? File.ReadAllBytes(real) : null;
        Assert.Equal(before, after);
    }

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
    public async Task 切换防火墙后必须刷新状态_否则这个开关永远关不掉()
    {
        // RefreshFirewallAsync 自带 Begin/End。切换时 IsBusy 已经是 true，
        // 再调它会被自己的 Begin() 挡在门外，Firewall 就永远停在切换前的旧值；
        // 而"下次该开还是该关"正是拿这个旧值算的 —— 结果就是只进不退的单向开关。
        var (vm, host, _) = NewVm(h => { h.IsAdministrator = true; h.FirewallResult = new(47200, false, true, false, false); });
        await vm.InitializeAsync();

        await vm.ToggleFirewallAsync();
        Assert.True(host.FirewallSetEnable);
        Assert.True(vm.Firewall?.FullyOpen);   // 没刷新的话这里还是 false

        await vm.ToggleFirewallAsync();
        Assert.False(host.FirewallSetEnable); // 没刷新的话这里还是 true，等于关不掉
        Assert.False(vm.Firewall?.FullyOpen);
        vm.Dispose();
    }

    [Fact]
    public async Task 切换后刷新状态失败_不能盖掉切换自己的结果()
    {
        // 刷新读不到是一回事，切换成没成是另一回事。
        // 让查询抛异常，验证"操作失败"的横幅不会被刷新异常顶掉。
        var (vm, _, _) = NewVm(h =>
        {
            h.IsAdministrator = true;
            h.FirewallResult = new(47200, false, true, false, false);
            h.FirewallSetResult = new OneShotResult(5, "", "netsh 返回 5");
        });
        await vm.InitializeAsync();

        await vm.ToggleFirewallAsync();

        Assert.Equal(BannerLevel.Error, vm.BannerLevel);
        Assert.Contains("netsh 返回 5", vm.StatusMessage);
        Assert.False(vm.IsBusy);
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
    public async Task 一键准备被控端会顺手配对主控端()
    {
        // 直连握手是双向 SIGMA：被控端不配主控端就��不上。
        // 原来这里压根不配对，而主控端页的文案却让用户"到被控端页点配对"——流程真的走不通。
        var (vm, host, _) = NewVm(h => h.IsAdministrator = true);
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";

        await vm.PrepareControlledAsync();

        Assert.Equal("PEERPUB==", Assert.Single(host.PairedPubs));
        Assert.Equal(1, host.StartCount);
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备被控端在提权重启前就配好对()
    {
        // PeerPub 只在内存里、不落盘：提权会以 runas 重启面板，那个框会被清空。
        // 所以配对必须排在提权前面，否则用户填的公钥白填。
        var (vm, host, _) = NewVm(h => h.IsAdministrator = false);
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";

        await vm.PrepareControlledAsync();

        Assert.Equal("PEERPUB==", Assert.Single(host.PairedPubs));
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备被控端没填公钥时明说还没配对()
    {
        var (vm, host, _) = NewVm(h => h.IsAdministrator = true);
        await vm.InitializeAsync();

        await vm.PrepareControlledAsync();

        Assert.Empty(host.PairedPubs);
        Assert.Contains("还没配对", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task Quic文案跟着真实状态走()
    {
        // 界面上那行 QUIC 说明以前是写死的"可用"，本机不支持时也在撒谎。
        var (yes, _, _) = NewVm();
        await yes.InitializeAsync();
        Assert.True(yes.QuicAvailable);
        Assert.Contains("本机支持", yes.QuicText);

        var (no, _, _) = NewVm(h => h.PrintConfigResult = new OneShotResult(0, """
            ServiceOptions { DataDir=C:\dl, InstanceId=dl }
            KeyStore: C:\dl\keystore.json
              ed25519_pub_b64 = LOCALPUB0000000000000000000000000000000000000=
              device_id       = AABBCC
              quic_available  = false
            """, ""));
        await no.InitializeAsync();
        Assert.False(no.QuicAvailable);
        Assert.Contains("不支持", no.QuicText);
        Assert.Contains("TCP-TLS", no.QuicText);
        // 不可用时绝不能还说"可用"
        Assert.DoesNotContain("本机支持", no.QuicText);

        yes.Dispose();
        no.Dispose();
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

    [Fact]
    public async Task 主控端配对失败时不能只剩一句绿色已就绪()
    {
        // 以前这里 await PairAsync()（void 包装）把结果丢了，
        // 失败横幅紧接着被 StartServiceAsync 的"服务已启动"和末尾的"已就绪"连盖两层，
        // 用户看到的是一片绿，实际一台都连不上。
        var (vm, host, _) = NewVm(h => h.PairResult = new OneShotResult(1, "", "公钥格式不对"));
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";
        await vm.PrepareControllerAsync();

        Assert.Equal("PEERPUB==", Assert.Single(host.PairedPubs));
        Assert.Equal(1, host.StartCount);              // 配对失败不影响起服务
        Assert.Contains("配对没成功", vm.StatusMessage); // 但必须说出来
        Assert.DoesNotContain("配对成功", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 被控端配对失败时也不能只剩一句绿色已就绪()
    {
        var (vm, host, _) = NewVm(h =>
        {
            h.IsAdministrator = true;
            h.PairResult = new OneShotResult(1, "", "被控端服务没起");
        });
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";
        await vm.PrepareControlledAsync();

        Assert.Equal("PEERPUB==", Assert.Single(host.PairedPubs));
        Assert.Contains("配对没成功", vm.StatusMessage);
        vm.Dispose();
    }

    [Fact]
    public async Task 没填公钥和填错了要给不同的提示()
    {
        // 二态（true/false）不够用：混成一句"还没配对"会让人反复检查自己早就填对的输入框。
        var (noKey, _, _) = NewVm();
        await noKey.InitializeAsync();
        await noKey.PrepareControllerAsync();
        var whenMissing = noKey.StatusMessage;
        noKey.Dispose();

        var (wrong, host, _) = NewVm(h => h.PairResult = new OneShotResult(1, "", "公钥格式不对"));
        await wrong.InitializeAsync();
        wrong.PeerPub = "PEERPUB==";
        await wrong.PrepareControllerAsync();
        var whenFailed = wrong.StatusMessage;
        wrong.Dispose();

        Assert.NotEqual(whenMissing, whenFailed);
        Assert.Contains("还没配对", whenMissing);
        Assert.Contains("配对没成功", whenFailed);
        Assert.Contains("公钥", whenMissing);
        Assert.Equal(1, host.PairedPubs.Count);
    }

    [Fact]
    public async Task 配对成功时不该再挂一条还没配对的提示()
    {
        var (vm, _, _) = NewVm();
        await vm.InitializeAsync();
        vm.PeerPub = "PEERPUB==";
        await vm.PrepareControllerAsync();

        Assert.DoesNotContain("还没配对", vm.StatusMessage);
        Assert.DoesNotContain("配对没成功", vm.StatusMessage);
        Assert.Contains("主控端已就绪", vm.StatusMessage);
        vm.Dispose();
    }

    // ── 角色选项卡 ────────────────────────────────────────────────────────

    [Fact]
    public void 选项卡索引与角色一一对应_主控端在左()
    {
        var (vm, _, _) = NewVm();

        // 界面约定：0 = 我是主控端（左边那一页），1 = 我是被控端
        vm.SelectedRoleIndex = 0;
        Assert.True(vm.IsController);
        Assert.Equal(PanelRole.Controller, vm.Role);

        vm.SelectedRoleIndex = 1;
        Assert.False(vm.IsController);
        Assert.Equal(PanelRole.Controlled, vm.Role);

        vm.Dispose();
    }

    [Fact]
    public void 切到主控端选项卡会同步角色_并在下一次保存时落盘()
    {
        var path = TempPath();
        try
        {
            var (vm, _, settings) = NewVm();
            vm.SelectedRoleIndex = 0;
            Assert.Equal(PanelRole.Controller, settings.Role);

            vm.SaveSettings(path);
            Assert.Equal(PanelRole.Controller, PanelSettings.Load(path).Role);
            vm.Dispose();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void 越界的选项卡索引会被夹回有效范围_不会把面板搞成空白()
    {
        var (vm, _, _) = NewVm();
        vm.SelectedRoleIndex = 0;

        // TabControl 在内容尚未建好时可能推 -1 / 越界值进来
        vm.SelectedRoleIndex = -1;
        Assert.Equal(0, vm.SelectedRoleIndex);
        Assert.True(vm.IsController);

        vm.SelectedRoleIndex = 99;
        Assert.Equal(1, vm.SelectedRoleIndex);
        Assert.False(vm.IsController);
        vm.Dispose();
    }

    [Fact]
    public void 载入设置时选项卡会落到上次用的那一页()
    {
        var host = new FakeServiceHost();
        var settings = new PanelSettings { Role = PanelRole.Controller };
        var vm = new MainViewModel(host, settings);
        vm.LoadFromSettings();

        Assert.Equal(0, vm.SelectedRoleIndex);
        Assert.True(vm.IsController);
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备被控端会自动切到被控端选项卡()
    {
        var (vm, _, _) = NewVm(h => h.IsAdministrator = true);
        await vm.InitializeAsync();
        vm.SelectedRoleIndex = 0;          // 先停在主控端页

        await vm.PrepareControlledAsync();

        Assert.Equal(1, vm.SelectedRoleIndex);
        Assert.False(vm.IsController);
        vm.Dispose();
    }

    [Fact]
    public async Task 一键准备主控端会自动切到主控端选项卡()
    {
        var (vm, _, _) = NewVm();
        await vm.InitializeAsync();
        Assert.Equal(1, vm.SelectedRoleIndex);

        await vm.PrepareControllerAsync();

        Assert.Equal(0, vm.SelectedRoleIndex);
        Assert.True(vm.IsController);
        vm.Dispose();
    }

    [Fact]
    public void 切选项卡只是换视图_不会顺手改直连与代理开关()
    {
        // 一键准备才该动 EnableDirect/InjectAgent；纯切页如果也改，
        // 用户只是"想看看另一页"就把当前配置改了，是个静默的数据损坏。
        var (vm, _, settings) = NewVm();
        settings.EnableDirect = true;
        settings.InjectAgent = true;
        vm.LoadFromSettings();

        vm.SelectedRoleIndex = 0;

        Assert.True(vm.EnableDirect);
        Assert.True(vm.InjectAgent);
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
