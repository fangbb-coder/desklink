using System.IO;
using DeskLink.Client.Security;
using DeskLink.Client.Services;
using DeskLink.Client.ViewModels;
using DeskLink.Protocol.Media;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary>
/// 可编程的假 <see cref="IServiceApi"/>：记录调用参数，供 VM 断言"业务决策是否真的透传"。
/// </summary>
internal sealed class FakeServiceApi : IServiceApi
{
    public bool Unavailable { get; set; }

    public List<string> UploadPolicies { get; } = new();
    public List<string> DownloadPolicies { get; } = new();
    public List<(string Local, string Remote)> Uploads { get; } = new();
    public List<(string Remote, string Local)> Downloads { get; } = new();
    public List<(string? RelayUrl, int? Port)> SetConfigCalls { get; } = new();
    public List<string> PairedKeys { get; } = new();
    public List<string> UnpairedKeys { get; } = new();
    public List<string> ListedPaths { get; } = new();

    public ListPairingsResult PairingsResult { get; set; } = new();
    public StatusResult StatusResult { get; set; } = new();
    public GetConfigResult ConfigResult { get; set; } = new();
    public FileScopeResult ScopeResult { get; set; } = new();
    public FileListResultDto ListResult { get; set; } = new() { Ok = true };
    public FileTransferResultDto TransferResult { get; set; } = new() { Ok = true, Bytes = 7, Target = "x" };

    /// <summary>可编程的 set_config 返回值（用于验证"需重启"提示）。</summary>
    public SetConfigResult NextSetConfigResult { get; set; } = new() { Ok = true };

    /// <summary>
    /// 可编程的状态提供器（每次调用返回一个新状态）。
    /// 用于模拟"会话过一会儿才建立"的真实时序，验证轮询逻辑。
    /// </summary>
    public Func<StatusResult>? StatusProvider { get; set; }

    /// <summary>GetStatusAsync 被调用的次数（验证轮询确实在跑）。</summary>
    public int GetStatusCallCount { get; private set; }

    private void FailIfUnavailable()
    {
        if (Unavailable) throw new PipeUnavailableException("DeskLink.Client.default", "服务未运行（假）");
    }

    public Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(new PingResult { Pong = true, Version = "fake" });
    }

    public Task<DeviceInfoResult> GetDeviceInfoAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(new DeviceInfoResult());
    }

    public Task<StatusResult> GetStatusAsync(CancellationToken ct = default)
    {
        // 先计数再判定可用性：调用方"尝试过几次"是独立于成败的信号
        // （例如验证 Service 不可用时没有空转到超时）。
        GetStatusCallCount++;
        FailIfUnavailable();
        return Task.FromResult(StatusProvider is not null ? StatusProvider() : StatusResult);
    }

    public Task<ListPairingsResult> ListPairingsAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(PairingsResult);
    }

    public Task PairAsync(string peerPubB64, string label, CancellationToken ct = default)
    {
        FailIfUnavailable();
        PairedKeys.Add(peerPubB64);
        return Task.CompletedTask;
    }

    public Task UnpairAsync(string peerPubB64, CancellationToken ct = default)
    {
        FailIfUnavailable();
        UnpairedKeys.Add(peerPubB64);
        return Task.CompletedTask;
    }

    public Task<GetConfigResult> GetConfigAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(ConfigResult);
    }

    public Task<SetConfigResult> SetConfigAsync(string? relayUrl, int? directPort, CancellationToken ct = default)
    {
        FailIfUnavailable();
        SetConfigCalls.Add((relayUrl, directPort));
        return Task.FromResult(NextSetConfigResult);
    }

    /// <summary>file_progress 桩。测试可塞进度条目验证进度条不再撒谎。</summary>
    public List<FileProgressEntryDto> FileProgressEntries { get; } = new();

    public bool FileProgressThrows { get; set; }

    /// <summary>GetFileProgressAsync 被调用的次数（验证轮询确实在跑 / 传输结束后确实停了）。</summary>
    public int GetFileProgressCallCount { get; private set; }

    public Task<FileProgressResult> GetFileProgressAsync(CancellationToken ct = default)
    {
        GetFileProgressCallCount++;
        FailIfUnavailable();
        if (FileProgressThrows) throw new InvalidOperationException("管道断了");
        return Task.FromResult(new FileProgressResult { Transfers = FileProgressEntries });
    }

    public Task<StartAgentResult> StartAgentAsync(bool inject, bool noInject, string? pipeOverride, CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(new StartAgentResult { Pid = 1 });
    }

    public Task StopAgentAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.CompletedTask;
    }

    public Task<EndSessionResult> EndSessionAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(new EndSessionResult { Ok = true, Closed = 0 });
    }

    // ── direct_dial（缺陷①）──
    public List<(string Pub, string Host, int Port)> DialCalls { get; } = new();
    public DirectDialResultDto NextDialResult { get; set; } = new() { Ok = true, Transport = "quic" };

    public Task<DirectDialResultDto> DialDirectAsync(
        string peerPubB64, string host, int port, CancellationToken ct = default)
    {
        FailIfUnavailable();
        DialCalls.Add((peerPubB64, host, port));
        return Task.FromResult(NextDialResult);
    }

    public Task<FileScopeResult> GetFileScopeAsync(CancellationToken ct = default)
    {
        FailIfUnavailable();
        return Task.FromResult(ScopeResult);
    }

    public Task<FileListResultDto> ListRemoteFilesAsync(string path, CancellationToken ct = default)
    {
        FailIfUnavailable();
        ListedPaths.Add(path);
        return Task.FromResult(ListResult);
    }

    public Task<FileTransferResultDto> UploadFileAsync(string local, string remote, string policy, CancellationToken ct = default)
    {
        FailIfUnavailable();
        UploadPolicies.Add(policy);
        Uploads.Add((local, remote));
        return Gate(TransferResult);
    }

    /// <summary>
    /// 非空时，传输调用会挂起直到 <see cref="ReleaseTransfer"/> 被调用。
    /// 用来模拟"传输还在进行中"这段窗口，好让与它并行的进度轮询真的跑起来。
    /// </summary>
    public TaskCompletionSource? TransferGate { get; set; }

    public void ReleaseTransfer() => TransferGate?.TrySetResult();

    private async Task<FileTransferResultDto> Gate(FileTransferResultDto result)
    {
        if (TransferGate is not null)
        {
            try { await TransferGate.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { /* 超时也要往下走，别把测试挂死在这里 */ }
        }
        return result;
    }

    public Task<FileTransferResultDto> DownloadFileAsync(string remote, string local, string policy, CancellationToken ct = default)
    {
        FailIfUnavailable();
        DownloadPolicies.Add(policy);
        Downloads.Add((remote, local));
        return Gate(TransferResult);
    }
}

/// <summary>View 模型的行为测试（全部对着假 IServiceApi，不碰真实管道）。</summary>
public class ViewModelTests
{
    private static string Pub(byte seed) => Convert.ToBase64String(new[] { seed, seed, seed, seed });

    private static string TempStorePath()
        => Path.Combine(Path.GetTempPath(), $"desklink-vm-{Guid.NewGuid():N}.json");

    // —— DevicesViewModel ——

    [Fact]
    public async Task Devices_Refuses_Relay_Connect_Until_Fingerprint_Confirmed()
    {
        var api = new FakeServiceApi();
        var store = new PeerFingerprintStore(TempStorePath());
        var vm = new DevicesViewModel(api, store)
        {
            SelectedDevice = new DeviceItem { PeerPubB64 = Pub(1), Label = "peer" },
            PathMode = ConnectPathMode.Relay,
        };

        bool approved = false;
        vm.RelayConnectApproved += _ => approved = true;

        bool confirmerCalled = false;
        vm.FingerprintConfirmer = (_, _) => { confirmerCalled = true; return Task.FromResult(false); };

        var outcome = await vm.ConnectSelectedAsync();

        Assert.Equal(ConnectOutcome.FingerprintNotConfirmed, outcome);
        Assert.True(confirmerCalled);
        Assert.False(approved);
        Assert.True(store.NeedsConfirmation(Pub(1))); // 拒绝后绝不落盘信任
    }

    [Fact]
    public async Task Devices_Approves_Relay_After_Confirmation_And_Then_AutoApproves()
    {
        var api = new FakeServiceApi();
        var store = new PeerFingerprintStore(TempStorePath());
        var vm = new DevicesViewModel(api, store)
        {
            SelectedDevice = new DeviceItem { PeerPubB64 = Pub(1), Label = "peer" },
            PathMode = ConnectPathMode.Relay,
        };

        int approvedCount = 0;
        vm.RelayConnectApproved += _ => approvedCount++;

        vm.FingerprintConfirmer = (_, _) => Task.FromResult(true);
        Assert.Equal(ConnectOutcome.Approved, await vm.ConnectSelectedAsync());
        Assert.False(store.NeedsConfirmation(Pub(1))); // 确认后持久化

        // 第二次同公钥：不该再弹指纹。
        vm.FingerprintConfirmer = (_, _) => throw new InvalidOperationException("不应再次询问");
        Assert.Equal(ConnectOutcome.Approved, await vm.ConnectSelectedAsync());
        Assert.Equal(2, approvedCount);
    }

    [Fact]
    public async Task Devices_Lan_Mode_Does_Not_Require_Fingerprint()
    {
        var api = new FakeServiceApi { StatusResult = new StatusResult { DirectEnabled = true } };
        var store = new PeerFingerprintStore(TempStorePath());
        var vm = new DevicesViewModel(api, store)
        {
            SelectedDevice = new DeviceItem { PeerPubB64 = Pub(2), Label = "peer" },
            PathMode = ConnectPathMode.Lan,
            LanEndpointText = "192.168.1.20:47200",
        };
        await vm.RefreshAsync();
        Assert.True(vm.DirectEnabled);

        bool lanFired = false;
        vm.LanConnectApproved += (_, ep) =>
        {
            lanFired = true;
            Assert.Equal("192.168.1.20", ep.Host);
            Assert.Equal(47200, ep.Port);
        };
        vm.FingerprintConfirmer = (_, _) => throw new InvalidOperationException("局域网直连不应弹指纹");

        Assert.Equal(ConnectOutcome.Approved, await vm.ConnectSelectedAsync());
        Assert.True(lanFired);
    }

    [Fact]
    public async Task Devices_Lan_Mode_Rejects_Invalid_Endpoint()
    {
        var api = new FakeServiceApi { StatusResult = new StatusResult { DirectEnabled = true } };
        var vm = new DevicesViewModel(api, new PeerFingerprintStore(TempStorePath()))
        {
            SelectedDevice = new DeviceItem { PeerPubB64 = Pub(3), Label = "peer" },
            PathMode = ConnectPathMode.Lan,
            LanEndpointText = "not-an-endpoint",
        };
        await vm.RefreshAsync();

        Assert.Equal(ConnectOutcome.InvalidLanEndpoint, await vm.ConnectSelectedAsync());
        Assert.Contains("端口", vm.StatusMessage);
    }

    [Fact]
    public async Task Devices_Connect_Without_Selection_Returns_NoDeviceSelected()
    {
        var vm = new DevicesViewModel(new FakeServiceApi(), new PeerFingerprintStore(TempStorePath()));
        Assert.Equal(ConnectOutcome.NoDeviceSelected, await vm.ConnectSelectedAsync());
    }

    [Fact]
    public async Task Devices_Refresh_Reports_Service_Unavailable()
    {
        var api = new FakeServiceApi { Unavailable = true };
        var vm = new DevicesViewModel(api, new PeerFingerprintStore(TempStorePath()));

        await vm.RefreshAsync();

        Assert.Contains("Service 未运行", vm.StatusMessage);
    }

    [Fact]
    public async Task Devices_Unpair_Forwards_Key_And_Removes_Row()
    {
        var api = new FakeServiceApi();
        var vm = new DevicesViewModel(api, new PeerFingerprintStore(TempStorePath()));
        var device = new DeviceItem { PeerPubB64 = Pub(4), Label = "peer" };
        vm.Devices.Add(device);

        Assert.True(await vm.UnpairAsync(device));
        Assert.Equal(Pub(4), api.UnpairedKeys.Single());
        Assert.Empty(vm.Devices);
    }

    // —— FilesViewModel ——

    [Fact]
    public async Task Files_Passes_Selected_ConflictPolicy_To_Upload()
    {
        var api = new FakeServiceApi();
        var vm = new FilesViewModel(api) { ConflictPolicy = FileConflictPolicy.Rename };

        await vm.UploadAsync("local/a.bin", "remote/a.bin");

        Assert.Equal("rename", api.UploadPolicies.Single());
        Assert.Equal(("local/a.bin", "remote/a.bin"), api.Uploads.Single());
        Assert.Equal(TransferState.Completed, vm.Transfers.Single().State);
        Assert.Equal(100, vm.Transfers.Single().Progress);
    }

    [Fact]
    public async Task Files_Passes_Selected_ConflictPolicy_To_Download()
    {
        var api = new FakeServiceApi();
        var vm = new FilesViewModel(api) { ConflictPolicy = FileConflictPolicy.Skip };

        await vm.DownloadAsync("remote/b.bin", "local/b.bin");

        Assert.Equal("skip", api.DownloadPolicies.Single());
    }

    [Fact]
    public void Files_ConflictPolicy_Maps_To_Wire_Values()
    {
        Assert.Equal("overwrite", FileConflictPolicy.Overwrite.ToWire());
        Assert.Equal("rename", FileConflictPolicy.Rename.ToWire());
        Assert.Equal("skip", FileConflictPolicy.Skip.ToWire());
    }

    [Fact]
    public async Task Files_Empty_Scope_Is_Reported_As_All_Rejected()
    {
        var api = new FakeServiceApi { ScopeResult = new FileScopeResult { Roots = new List<string>() } };
        var vm = new FilesViewModel(api);

        await vm.LoadScopeAsync();

        Assert.Empty(vm.LocalRoots);
        Assert.Contains("一律被拒绝", vm.StatusMessage);
    }

    [Fact]
    public async Task Files_Failed_Transfer_Marks_Item_Failed()
    {
        var api = new FakeServiceApi { TransferResult = new FileTransferResultDto { Ok = false, Error = "disk full" } };
        var vm = new FilesViewModel(api);

        await vm.UploadAsync("a", "b");

        var item = vm.Transfers.Single();
        Assert.Equal(TransferState.Failed, item.State);
        Assert.Equal("disk full", item.Message);
    }

    [Fact]
    public void Files_Pause_Resume_Toggles_State()
    {
        var vm = new FilesViewModel(new FakeServiceApi());
        var item = new TransferItem { Name = "x", IsUpload = true, LocalPath = "a", RemotePath = "b" };
        vm.Transfers.Add(item);

        vm.MarkPaused(item);
        Assert.Equal(TransferState.Paused, item.State);
        Assert.True(item.IsPaused);

        vm.MarkResumed(item);
        Assert.Equal(TransferState.Running, item.State);
    }

    // —— RemoteViewModel ——

    [Fact]
    public void Remote_StateMachine_Rejects_Illegal_Transition()
    {
        var vm = new RemoteViewModel();
        Assert.Equal(SessionState.Idle, vm.State);

        // Idle 不能直接跳到 Established。
        Assert.False(vm.MarkEstablished());
        Assert.Equal(SessionState.Idle, vm.State);
    }

    [Fact]
    public void Remote_StateMachine_Full_Happy_Path_With_Recovery()
    {
        var vm = new RemoteViewModel();

        Assert.True(vm.BeginConnect());
        Assert.Equal(SessionState.Connecting, vm.State);

        Assert.True(vm.MarkEstablished());
        Assert.True(vm.IsEstablished);

        // ACCESS_LOST：显示"正在恢复画面"，不丢控制权。
        Assert.True(vm.MarkAccessLost());
        Assert.True(vm.IsRecovering);
        Assert.Equal("正在恢复画面…", vm.StateText);

        Assert.True(vm.MarkRecovered());
        Assert.True(vm.IsEstablished);

        Assert.True(vm.MarkDisconnected());
        Assert.Equal(SessionState.Idle, vm.State);
    }

    [Fact]
    public void Remote_Waiting_For_Local_Login_Only_From_Connecting()
    {
        var vm = new RemoteViewModel();
        vm.BeginConnect();

        Assert.True(vm.MarkWaitingForLocalLogin());
        Assert.Equal("等待本地登录", vm.StateText);

        // 锁屏态可以直接恢复到已连接。
        Assert.True(vm.MarkEstablished());
    }

    [Fact]
    public void Remote_Error_Then_Retry()
    {
        var vm = new RemoteViewModel();
        vm.BeginConnect();
        Assert.True(vm.MarkError("relay unreachable"));
        Assert.Equal("relay unreachable", vm.ErrorMessage);

        // 重试：Error → Connecting，且错误信息被清掉。
        Assert.True(vm.BeginConnect());
        Assert.Equal("", vm.ErrorMessage);
    }

    [Fact]
    public void Remote_Stats_And_Codec_Backend_Update()
    {
        var vm = new RemoteViewModel();
        vm.BeginConnect();
        vm.MarkEstablished();

        vm.UpdateStats(new SessionStatsPayload(299, 1500, 42, Degraded: true));

        Assert.Equal(29.9, vm.Fps, precision: 3);
        Assert.Equal(1500, vm.Kbps);
        Assert.Equal(42, vm.RttMs);
        Assert.True(vm.Degraded);

        vm.ApplyDesktopConfig(new DesktopConfigPayload(1280, 720, DesktopConfigPayload.CodecH264,
            DesktopConfigPayload.BackendSoftware, 30, 2_000_000, 0, 0));
        Assert.True(vm.SoftwareCodec);
    }

    // —— SettingsViewModel ——

    [Theory]
    [InlineData("https://relay.example.com", true)]
    [InlineData("quic://1.2.3.4:443", true)]
    [InlineData("tls://host.local:8443", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("relay.example.com", false)]   // 缺 scheme
    [InlineData("ftp://x", false)]             // 不支持的 scheme
    [InlineData("https://", false)]            // 缺主机
    [InlineData("not a url", false)]
    public void Settings_Validates_Relay_Url(string url, bool expected)
    {
        Assert.Equal(expected, SettingsViewModel.ValidateRelayUrl(url).IsValid);
    }

    [Fact]
    public void Settings_RelayUrl_Property_Exposes_Validation()
    {
        var vm = new SettingsViewModel(new FakeServiceApi());
        vm.RelayUrl = "nonsense";
        Assert.False(vm.IsRelayUrlValid);
        Assert.False(string.IsNullOrWhiteSpace(vm.RelayUrlValidation.Error));

        vm.RelayUrl = "https://relay.example.com";
        Assert.True(vm.IsRelayUrlValid);
    }

    [Fact]
    public async Task Settings_Refuses_To_Save_Invalid_Relay_Url()
    {
        var api = new FakeServiceApi();
        var vm = new SettingsViewModel(api) { RelayUrl = "bad url" };

        Assert.False(await vm.SaveAsync());
        Assert.Empty(api.SetConfigCalls);
    }

    [Fact]
    public async Task Settings_Saves_Valid_Relay_Url_And_Port()
    {
        var api = new FakeServiceApi();
        var vm = new SettingsViewModel(api) { RelayUrl = "https://relay.example.com", DirectPort = 48000 };

        Assert.True(await vm.SaveAsync());
        var call = api.SetConfigCalls.Single();
        Assert.Equal("https://relay.example.com", call.RelayUrl);
        Assert.Equal(48000, call.Port);
    }

    [Fact]
    public async Task Settings_Relay_Certificate_Requires_First_Connect_Confirmation()
    {
        var vm = new SettingsViewModel(new FakeServiceApi());
        bool asked = false;
        vm.RelayCertConfirmer = _ => { asked = true; return Task.FromResult(false); };

        Assert.False(await vm.ConfirmRelayCertificateAsync("AA BB CC"));
        Assert.True(asked);
        Assert.False(vm.RelayCertificateConfirmed);

        vm.RelayCertConfirmer = _ => Task.FromResult(true);
        Assert.True(await vm.ConfirmRelayCertificateAsync("AA BB CC"));
        Assert.True(vm.RelayCertificateConfirmed);

        // 同一指纹后续直接放行，不再询问。
        vm.RelayCertConfirmer = _ => throw new InvalidOperationException("不应再次询问");
        Assert.True(await vm.ConfirmRelayCertificateAsync("AA BB CC"));
    }

    [Fact]
    public void Settings_Direct_Port_Range_Validated()
    {
        var vm = new SettingsViewModel(new FakeServiceApi());
        vm.DirectPort = 0;
        Assert.False(vm.IsDirectPortValid);
        vm.DirectPort = 65536;
        Assert.False(vm.IsDirectPortValid);
        vm.DirectPort = 47200;
        Assert.True(vm.IsDirectPortValid);
    }

    // —— MainViewModel ——

    [Fact]
    public void Main_StatusBar_Shows_Controlled_State_And_Stats()
    {
        var vm = new MainViewModel(new FakeServiceApi(), new PeerFingerprintStore(TempStorePath()));
        Assert.Contains("未被控制", vm.StatusBarText);

        vm.IsControlled = true;
        vm.ControlledBy = "peer-A";
        vm.OnStats(new SessionStatsPayload(300, 2000, 18, false));

        Assert.Contains("正在被控制", vm.StatusBarText);
        Assert.Contains("peer-A", vm.StatusBarText);
        Assert.Contains("30", vm.StatusBarText); // 300/10 = 30fps
        Assert.Contains("18ms", vm.StatusBarText);
    }
}
