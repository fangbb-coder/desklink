// 三个"UI 撒谎"缺陷的回归测试。
//
// 共同点：UI 曾**无条件**告诉用户"已连接/已保存"，而底层根本没发生对应的事。
// 这类缺陷单测比截图评审可靠得多——因为它们钉死的是"不允许出现的状态"。

using System.IO;
using DeskLink.Client.Security;
using DeskLink.Client.Services;
using DeskLink.Client.ViewModels;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// 缺陷①：局域网直连的 UI 是"假接线"——从不拨号。
/// </summary>
public class LanDialWiringTests
{
    private static DevicesViewModel NewDevices(FakeServiceApi api)
    {
        var store = new PeerFingerprintStore(
            Path.Combine(Path.GetTempPath(), $"desklink-fp-{Guid.NewGuid():N}.json"));
        return new DevicesViewModel(api, store);
    }

    private static DeviceItem NewDevice() => new()
    {
        PeerPubB64 = Convert.ToBase64String(new byte[32]),
        Label = "alpha",
    };

    [Fact]
    public async Task ConnectLan_Dials_The_Peer_And_Passes_Endpoint()
    {
        var api = new FakeServiceApi();
        var vm = NewDevices(api);
        vm.SelectedDevice = NewDevice();
        vm.PathMode = ConnectPathMode.Lan;
        vm.LanEndpointText = "192.168.1.20:47200";
        api.StatusResult.DirectEnabled = true;
        await vm.RefreshAsync();

        var approved = 0;
        vm.LanConnectApproved += (_, _) => approved++;

        var outcome = await vm.ConnectSelectedAsync();

        Assert.Equal(ConnectOutcome.Approved, outcome);
        // 关键：必须真的发出了 direct_dial，且参数是用户填的地址。
        var call = Assert.Single(api.DialCalls);
        Assert.Equal("192.168.1.20", call.Host);
        Assert.Equal(47200, call.Port);
        Assert.Equal(1, approved);
    }

    [Fact]
    public async Task 控制端未放行入站端口时仍能拨号()
    {
        // 回归用例（2026-09-29 真机踩到）：ConnectLanAsync 曾经拿 DirectEnabled 当闸门，
        // 而 DirectEnabled 取自 StatusResult.DirectEnabled = FirewallHelper.QueryEnabled(port)
        // ——那是"**本机防火墙有没有放行该端口**"，不是"能不能直连"。
        // 控制端只负责拨出（DirectDialer 无条件注册，不需要入站规则），
        // 它的防火墙通常是关的 → DirectEnabled 恒 false → 点"连接"直接被拒，
        // 用户只看到"没反应"。这条用例锁死"闸门必须移除"这个决定。
        var api = new FakeServiceApi();
        var vm = NewDevices(api);
        vm.SelectedDevice = NewDevice();
        vm.PathMode = ConnectPathMode.Lan;
        vm.LanEndpointText = "192.168.1.20:47200";

        // 模拟控制端典型状态：本机没放行入站端口
        api.StatusResult.DirectEnabled = false;
        await vm.RefreshAsync();
        Assert.False(vm.DirectEnabled);

        api.NextDialResult = new DirectDialResultDto { Ok = true, Transport = "tcp-tls" };
        var approved = 0;
        vm.LanConnectApproved += (_, _) => approved++;

        var outcome = await vm.ConnectSelectedAsync();

        Assert.Equal(ConnectOutcome.Approved, outcome);
        Assert.Single(api.DialCalls);   // 真的拨出去了
        Assert.Equal(1, approved);
    }

    [Fact]
    public async Task 地址格式非法时给出明确提示而不是静默返回()
    {
        var api = new FakeServiceApi();
        var vm = NewDevices(api);
        vm.SelectedDevice = NewDevice();
        vm.PathMode = ConnectPathMode.Lan;
        vm.LanEndpointText = "这不是地址";

        var outcome = await vm.ConnectSelectedAsync();

        Assert.Equal(ConnectOutcome.InvalidLanEndpoint, outcome);
        Assert.Empty(api.DialCalls);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage));
    }

    [Fact]
    public async Task ConnectLan_DialFailure_Does_Not_Navigate_To_Remote()
    {
        var api = new FakeServiceApi();
        var vm = NewDevices(api);
        vm.SelectedDevice = NewDevice();
        vm.PathMode = ConnectPathMode.Lan;
        vm.LanEndpointText = "192.168.1.20:47200";
        api.StatusResult.DirectEnabled = true;
        await vm.RefreshAsync();

        api.NextDialResult = new DirectDialResultDto
        {
            Ok = false,
            Detail = "该设备不在本机配对列表中，请先完成配对",
        };

        var approved = 0;
        vm.LanConnectApproved += (_, _) => approved++;

        var outcome = await vm.ConnectSelectedAsync();

        // 拨号失败 → 停在设备页、显示原因，绝不跳远程页。
        Assert.Equal(ConnectOutcome.DialFailed, outcome);
        Assert.Equal(0, approved);
        Assert.Contains("配对", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConnectLan_Invalid_Endpoint_Dials_Nothing()
    {
        var api = new FakeServiceApi();
        var vm = NewDevices(api);
        vm.SelectedDevice = NewDevice();
        vm.PathMode = ConnectPathMode.Lan;
        api.StatusResult.DirectEnabled = true;
        await vm.RefreshAsync();

        vm.LanEndpointText = "192.168.1.20"; // 缺端口

        var outcome = await vm.ConnectSelectedAsync();

        Assert.Equal(ConnectOutcome.InvalidLanEndpoint, outcome);
        Assert.Empty(api.DialCalls);
    }

    [Fact]
    public async Task ConnectLan_When_Direct_Not_Opened_Still_Dials_And_Surfaces_Failure()
    {
        // 这条用例原本断言"DirectEnabled=false 就不拨号"（ConnectOutcome.LanNotEnabled）——
        // 那正是 2026-09-29 真机踩到的 bug：DirectEnabled 的真实含义是
        // "本机防火墙有没有放行该端口"，而控制端只拨出、不需要入站规则，
        // 于是控制端被自己的闸门整个堵死，点"连接"毫无反应。
        //
        // 现在语义是：闸门移除，**拨号照发**，失败由拨号结果如实回报。
        var api = new FakeServiceApi();
        var vm = NewDevices(api);
        vm.SelectedDevice = NewDevice();
        vm.PathMode = ConnectPathMode.Lan;
        vm.LanEndpointText = "192.168.1.20:47200";
        api.StatusResult.DirectEnabled = false;
        await vm.RefreshAsync();

        api.NextDialResult = new DirectDialResultDto { Ok = false, Detail = "对端无响应" };

        var outcome = await vm.ConnectSelectedAsync();

        Assert.Equal(ConnectOutcome.DialFailed, outcome);
        // 关键区别：不再是"什么都不做"，而是真去拨、把失败原因显示出来
        Assert.Single(api.DialCalls);
        Assert.Contains("对端无响应", vm.StatusMessage);
    }
}

/// <summary>
/// 缺陷②：点"连接"无条件显示"已连接"——本机媒体管道连上≠远端会话存在。
/// </summary>
public class SessionExistenceGateTests
{
    private static MainViewModel NewMain(FakeServiceApi api)
    {
        var store = new PeerFingerprintStore(
            Path.Combine(Path.GetTempPath(), $"desklink-fp-{Guid.NewGuid():N}.json"));
        return new MainViewModel(api, store);
    }

    [Fact]
    public async Task WaitForRemoteSession_Relay_Succeeds_Only_When_E2E_Established()
    {
        var api = new FakeServiceApi();
        api.StatusResult = new StatusResult { E2EState = "established" };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: false, TimeSpan.FromSeconds(2));

        Assert.True(ready);
    }

    [Fact]
    public async Task WaitForRemoteSession_Relay_Fails_While_Offline()
    {
        var api = new FakeServiceApi();
        api.StatusResult = new StatusResult { E2EState = "idle" };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: false, TimeSpan.FromMilliseconds(300));

        // 离线设备不能被判成"已连接"——这正是黑屏的根源。
        Assert.False(ready);
        Assert.True(api.GetStatusCallCount > 1, "应当轮询多次，而不是只查一次就下结论");
    }

    [Theory]
    [InlineData("handshaking")]
    [InlineData("sigma")]
    [InlineData("closed")]
    [InlineData("error:transport")]
    public async Task WaitForRemoteSession_NonEstablished_States_All_Rejected(string state)
    {
        var api = new FakeServiceApi();
        api.StatusResult = new StatusResult { E2EState = state };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: false, TimeSpan.FromMilliseconds(200));

        Assert.False(ready, $"状态 '{state}' 不应被当作已建立");
    }

    [Fact]
    public async Task WaitForRemoteSession_Direct_Fails_Without_Active_Session()
    {
        var api = new FakeServiceApi();
        // 注意：即使中继 E2E 是 established，直连路径也必须看 DirectActiveSessions。
        // 用错判据会让"直连失败"被中继状态掩盖。
        api.StatusResult = new StatusResult
        {
            E2EState = "established",
            DirectActiveSessions = 0,
        };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: true, TimeSpan.FromMilliseconds(300));

        Assert.False(ready);
    }

    [Fact]
    public async Task WaitForRemoteSession_Direct_Succeeds_With_Active_Session()
    {
        var api = new FakeServiceApi();
        api.StatusResult = new StatusResult { DirectActiveSessions = 1, E2EState = "idle" };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: true, TimeSpan.FromSeconds(2));

        Assert.True(ready);
    }

    [Fact]
    public async Task WaitForRemoteSession_Polls_Until_Session_Appears()
    {
        var api = new FakeServiceApi();
        var calls = 0;
        // 模拟真实时序：前几次还没建立，第 3 次才 established。
        api.StatusProvider = () => new StatusResult
        {
            E2EState = ++calls >= 3 ? "established" : "sigma",
        };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: false, TimeSpan.FromSeconds(5));

        Assert.True(ready, "会话最终建立后应返回 true");
        Assert.True(calls >= 3);
    }

    [Fact]
    public async Task WaitForRemoteSession_Service_Unavailable_Fails_Fast()
    {
        var api = new FakeServiceApi { Unavailable = true };
        var vm = NewMain(api);

        var ready = await vm.WaitForRemoteSessionAsync(
            requireDirect: false, TimeSpan.FromSeconds(5));

        Assert.False(ready);
        // Service 不可用时不该空转到超时。
        Assert.Equal(1, api.GetStatusCallCount);
    }
}

/// <summary>
/// 缺陷③：Settings 改中继地址后弹"已保存"，但运行时不生效且无提示。
/// </summary>
public class SettingsRestartHintTests
{
    private static SettingsViewModel NewSettings(FakeServiceApi api) => new(api);

    [Fact]
    public async Task SaveAsync_When_Restart_Required_Says_So_Explicitly()
    {
        var api = new FakeServiceApi();
        api.NextSetConfigResult = new SetConfigResult
        {
            Ok = true,
            RequiresRestart = true,
            RestartHint = "中继地址已保存，但**需重启 DeskLinkService 后才会生效**（当前进程仍在使用旧地址）。",
        };
        var vm = NewSettings(api);
        vm.RelayUrl = "https://new-relay.example:8443";
        vm.DirectPort = 47200;

        var ok = await vm.SaveAsync();

        Assert.True(ok);
        Assert.True(vm.PendingRestart);
        // 关键：文案必须包含"重启"，否则用户仍会被误导。
        Assert.Contains("重启", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_Without_Restart_Keeps_Plain_Message()
    {
        var api = new FakeServiceApi();
        api.NextSetConfigResult = new SetConfigResult { Ok = true, RequiresRestart = false };
        var vm = NewSettings(api);
        vm.RelayUrl = "https://relay.example:8443";
        vm.DirectPort = 47200;

        var ok = await vm.SaveAsync();

        Assert.True(ok);
        Assert.False(vm.PendingRestart);
        Assert.Equal("已保存", vm.StatusMessage);
    }

    [Fact]
    public async Task LoadAsync_Highlights_Active_Url_Differing_From_Configured()
    {
        var api = new FakeServiceApi();
        api.ConfigResult = new GetConfigResult
        {
            RelayUrl = "https://new-relay.example:8443/",
            ActiveRelayUrl = "https://old-relay.example:8443/",
            DirectPort = 47200,
        };
        var vm = NewSettings(api);

        await vm.LoadAsync();

        Assert.Equal("https://old-relay.example:8443/", vm.ActiveRelayUrl);
        Assert.True(vm.HasPendingRelayChange);
        Assert.True(vm.PendingRestart);
        // 提示里要同时出现"旧地址"和"重启"，用户才知道该做什么。
        Assert.Contains("old-relay.example", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("重启", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_No_Pending_Change_When_Urls_Match()
    {
        var api = new FakeServiceApi();
        api.ConfigResult = new GetConfigResult
        {
            RelayUrl = "https://relay.example:8443/",
            ActiveRelayUrl = "https://relay.example:8443/",
            DirectPort = 47200,
        };
        var vm = NewSettings(api);

        await vm.LoadAsync();

        Assert.False(vm.HasPendingRelayChange);
        Assert.False(vm.PendingRestart);
    }
}
