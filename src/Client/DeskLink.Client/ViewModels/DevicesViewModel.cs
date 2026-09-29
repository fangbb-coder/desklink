using System.Collections.ObjectModel;
using System.Security.Cryptography;
using DeskLink.Client.Security;
using DeskLink.Client.Services;

namespace DeskLink.Client.ViewModels;

/// <summary>连接路径。用户在设备页显式选择，**不自动切换**（DESIGN「路径切换」）。</summary>
public enum ConnectPathMode
{
    Relay,
    Lan,
}

/// <summary>发起连接的结果，供 UI/测试分支。</summary>
public enum ConnectOutcome
{
    Approved,
    NoDeviceSelected,
    FingerprintNotConfirmed,
    InvalidLanEndpoint,
    LanNotEnabled,

    /// <summary>拨号请求已发出但失败（网络不通 / 未配对 / 握手失败），UI 停留在设备页。</summary>
    DialFailed,
}

/// <summary>设备列表项。</summary>
public sealed class DeviceItem : ObservableObject
{
    private bool _online;
    private string _label = "";

    public required string PeerPubB64 { get; init; }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    /// <summary>
    /// 在线状态。注意：当前 RPC 契约（get_status）只暴露**单个**活跃对端
    /// （e2e_peer_device_id），没有逐设备在线表；因此这里只能标"当前会话对端"，
    /// 其余设备默认为未知/离线，待 Service 侧补充逐设备在线信息后再细化。
    /// </summary>
    public bool Online
    {
        get => _online;
        set => SetProperty(ref _online, value);
    }

    public DateTime PairedUtc { get; init; }

    /// <summary>对端公钥指纹（分组十六进制），供列表/确认对话框显示。</summary>
    public string Fingerprint
    {
        get
        {
            try
            {
                return PeerFingerprintStore.ComputeFingerprint(Convert.FromBase64String(PeerPubB64));
            }
            catch (FormatException)
            {
                return PeerFingerprintStore.ComputeFingerprint(System.Text.Encoding.UTF8.GetBytes(PeerPubB64));
            }
        }
    }
}

/// <summary>
/// 设备页视图模型：配对、设备列表、路径选择、撤销，以及**中继路径的 TOFU 指纹门禁**。
///
/// 关键安全决策：中继连接前必须先确认对端指纹（见 <see cref="PeerFingerprintStore"/>
/// 的 TOFU 语义）。本 VM 把这道门禁放在**唯一**的发起入口
/// <see cref="ConnectSelectedAsync"/> 里，避免有别的代码路径绕过它。
/// 局域网直连不复用该门禁（已配对即信任）。
/// </summary>
public sealed class DevicesViewModel : ObservableObject
{
    private readonly IServiceApi _api;
    private readonly PeerFingerprintStore _store;

    private DeviceItem? _selectedDevice;
    private ConnectPathMode _pathMode = ConnectPathMode.Relay;
    private string _pairingCode = "";
    private string _pairingLabel = "";
    private string _lanEndpointText = "";
    private string _statusMessage = "";
    private bool _directEnabled;
    private bool _busy;

    public DevicesViewModel(IServiceApi api, PeerFingerprintStore store)
    {
        _api = api;
        _store = store;

        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        PairCommand = new RelayCommand(() => _ = PairAsync());
        ConnectCommand = new RelayCommand(() => _ = ConnectSelectedAsync());
        UnpairCommand = new RelayCommand<DeviceItem>(d => _ = UnpairAsync(d));
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand PairCommand { get; }
    public RelayCommand ConnectCommand { get; }
    public RelayCommand<DeviceItem> UnpairCommand { get; }

    /// <summary>
    /// 展示指纹并等待用户确认的回调（返回 true = 用户确认）。
    /// 真实运行由 FingerprintConfirmDialog 提供；测试注入假实现。
    /// </summary>
    public Func<DeviceItem, string, Task<bool>>? FingerprintConfirmer { get; set; }

    public ObservableCollection<DeviceItem> Devices { get; } = new();

    /// <summary>中继连接通过门禁后触发（App 据此导航到远程页）。</summary>
    public event Action<DeviceItem>? RelayConnectApproved;

    /// <summary>局域网直连参数校验通过后触发。</summary>
    public event Action<DeviceItem, DirectEndpoint>? LanConnectApproved;

    public DeviceItem? SelectedDevice
    {
        get => _selectedDevice;
        set => SetProperty(ref _selectedDevice, value);
    }

    public ConnectPathMode PathMode
    {
        get => _pathMode;
        set
        {
            if (SetProperty(ref _pathMode, value))
            {
                OnPropertyChanged(nameof(IsLanMode));
                OnPropertyChanged(nameof(IsRelayMode));
            }
        }
    }

    public bool IsRelayMode
    {
        get => _pathMode == ConnectPathMode.Relay;
        set { if (value) PathMode = ConnectPathMode.Relay; }
    }

    public bool IsLanMode
    {
        get => _pathMode == ConnectPathMode.Lan;
        set { if (value) PathMode = ConnectPathMode.Lan; }
    }

    public string PairingCode
    {
        get => _pairingCode;
        set => SetProperty(ref _pairingCode, value);
    }

    public string PairingLabel
    {
        get => _pairingLabel;
        set => SetProperty(ref _pairingLabel, value);
    }

    /// <summary>局域网直连目标文本（<c>IP:端口</c>）。</summary>
    public string LanEndpointText
    {
        get => _lanEndpointText;
        set => SetProperty(ref _lanEndpointText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>被控端是否启用了局域网直连（未启用时入口禁用）。</summary>
    public bool DirectEnabled
    {
        get => _directEnabled;
        private set => SetProperty(ref _directEnabled, value);
    }

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    /// <summary>拉取配对列表与状态。服务未运行时给出可读提示，而不是抛栈。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        Busy = true;
        try
        {
            var pairings = await _api.ListPairingsAsync(ct);
            var status = await _api.GetStatusAsync(ct);

            Devices.Clear();
            foreach (var p in pairings.Pairings)
            {
                Devices.Add(new DeviceItem
                {
                    PeerPubB64 = p.PeerPubB64,
                    Label = string.IsNullOrWhiteSpace(p.Label) ? ShortPub(p.PeerPubB64) : p.Label,
                    PairedUtc = p.PairedUtc,
                    Online = status.E2EPeerDeviceId is not null
                             && status.E2EState == "established"
                             && status.E2EPeerDeviceId == p.PeerPubB64,
                });
            }

            DirectEnabled = status.DirectEnabled;
            StatusMessage = $"共 {Devices.Count} 台已配对设备；中继状态：{status.RelayState}";
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法读取设备列表（请确认 DeskLinkService 已启动）";
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"读取设备列表失败：{ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 配对。注意：契约 <c>pair</c> 的字段是 <c>peer_pub_b64</c>；客户端不掌握 registry，
    /// 配对码 → 公钥的解析在 Service 侧完成，这里把用户输入的凭据原样透传。
    /// </summary>
    public async Task<bool> PairAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(PairingCode))
        {
            StatusMessage = "请输入配对码";
            return false;
        }

        Busy = true;
        try
        {
            await _api.PairAsync(PairingCode.Trim(), PairingLabel?.Trim() ?? "", ct);
            PairingCode = "";
            PairingLabel = "";
            StatusMessage = "配对成功";
            await RefreshAsync(ct);
            return true;
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法配对";
            return false;
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"配对失败：{ex.Message}";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>撤销（取消配对）。撤销后该设备再接入必须重新输入新配对码。</summary>
    public async Task<bool> UnpairAsync(DeviceItem device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        Busy = true;
        try
        {
            await _api.UnpairAsync(device.PeerPubB64, ct);
            Devices.Remove(device);
            StatusMessage = $"已撤销 {device.Label}";
            return true;
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法撤销";
            return false;
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"撤销失败：{ex.Message}";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>
    /// 按当前选择的路径发起连接。**中继路径必须先通过 TOFU 指纹门禁**。
    /// </summary>
    public async Task<ConnectOutcome> ConnectSelectedAsync(CancellationToken ct = default)
    {
        var device = SelectedDevice;
        if (device is null)
        {
            StatusMessage = "请先选择一台设备";
            return ConnectOutcome.NoDeviceSelected;
        }

        if (PathMode == ConnectPathMode.Lan)
        {
            return await ConnectLanAsync(device, ct);
        }

        return await ConnectRelayAsync(device, ct);
    }

    private async Task<ConnectOutcome> ConnectRelayAsync(DeviceItem device, CancellationToken ct)
    {
        if (_store.NeedsConfirmation(device.PeerPubB64))
        {
            var fingerprint = device.Fingerprint;
            var confirmed = FingerprintConfirmer is not null
                            && await FingerprintConfirmer(device, fingerprint);
            if (!confirmed)
            {
                StatusMessage = "未确认对端指纹，已取消中继连接";
                return ConnectOutcome.FingerprintNotConfirmed;
            }

            // 用户显式确认后才持久化；下次同公钥自动放行，公钥变更则再次弹窗。
            _store.Confirm(device.PeerPubB64);
        }

        StatusMessage = $"正在经中继连接到 {device.Label}…";
        RelayConnectApproved?.Invoke(device);
        return ConnectOutcome.Approved;
    }

    /// <summary>
    /// 局域网直连：**先真的拨出去，拨通才通知 UI 跳远程页**。
    ///
    /// 这一步以前根本不存在——旧实现校验完 IP:端口就直接
    /// <see cref="LanConnectApproved"/>，而没有任何 RPC 告诉 Service 去拨号，
    /// 于是 UI 显示"已连接"、画面永远黑屏。现在拨号失败会明确报错并留在设备页。
    /// </summary>
    private async Task<ConnectOutcome> ConnectLanAsync(DeviceItem device, CancellationToken ct)
    {
        if (!DirectEnabled)
        {
            StatusMessage = "本机未启用局域网直连（被控端需 --enable-direct 或在 Settings 放行端口）";
            return ConnectOutcome.LanNotEnabled;
        }

        if (!DirectEndpoint.TryParse(LanEndpointText, out var endpoint, out var error))
        {
            StatusMessage = error;
            return ConnectOutcome.InvalidLanEndpoint;
        }

        // 局域网直连不弹指纹确认：配对已是公钥信任的根来源（DESIGN「设备身份和权限」）。
        Busy = true;
        StatusMessage = $"正在直连 {endpoint}…";
        try
        {
            var result = await _api
                .DialDirectAsync(device.PeerPubB64, endpoint.Host, endpoint.Port, ct)
                .ConfigureAwait(false);

            if (!result.Ok)
            {
                StatusMessage = $"直连失败：{result.Detail ?? "未知原因"}";
                return ConnectOutcome.DialFailed;
            }

            StatusMessage = $"已直连 {endpoint}（传输：{result.Transport ?? "?"}）";
            LanConnectApproved?.Invoke(device, endpoint);
            return ConnectOutcome.Approved;
        }
        catch (PipeUnavailableException)
        {
            StatusMessage = "Service 未运行：无法发起直连";
            return ConnectOutcome.DialFailed;
        }
        catch (PipeRpcException ex)
        {
            StatusMessage = $"直连失败：{ex.Message}";
            return ConnectOutcome.DialFailed;
        }
        finally
        {
            Busy = false;
        }
    }

    private static string ShortPub(string pub)
    {
        try
        {
            var hash = SHA256.HashData(Convert.FromBase64String(pub));
            return "device-" + Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        }
        catch (FormatException)
        {
            return "device-unknown";
        }
    }
}
