// DeskLink.Panel —— 面板依赖的"本机服务"抽象
//
// 抽出接口的唯一目的是**可测**：MainViewModel 里所有的按钮逻辑都能在无进程、无 UI 的
// 情况下跑（tests\Panel.Tests 用 FakeServiceHost 驱动）。真实现 LocalServiceHost
// 才真的去起子进程、走命名管道。
using System.IO;
using System.Windows.Threading;
using DeskLink.Protocol.Pipe;
using DeskLink.Panel.Services;

namespace DeskLink.Panel;

/// <summary>面板需要本机 Service 提供的全部能力。</summary>
public interface IServiceHost
{
    bool IsServiceRunning { get; }
    bool IsAdministrator { get; }
    IReadOnlyList<string> LocalIPv4s { get; }
    string LocalEndpoint { get; }
    string? ServiceExePath { get; }
    string? ClientExePath { get; }

    event Action<string>? Log;
    event Action<int>? ServiceExited;

    Task<OneShotResult> PrintConfigAsync(CancellationToken ct = default);
    Task<OneShotResult> PairAsync(string peerPubB64, CancellationToken ct = default);
    Task<FirewallStatus?> GetFirewallStatusAsync(CancellationToken ct = default);
    Task<OneShotResult> SetFirewallAsync(bool enable, CancellationToken ct = default);

    Task StartServiceAsync(CancellationToken ct = default);
    Task StopServiceAsync();
    Task<StatusResult?> GetStatusAsync(CancellationToken ct = default);

    bool LaunchClient();
    bool TryRestartElevated();
}

/// <summary>真实实现：子进程 + 命名管道 + 本机网卡。</summary>
public sealed class LocalServiceHost : IServiceHost, IAsyncDisposable
{
    private readonly PanelSettings _settings;
    private readonly ServiceProcess _process;
    private readonly string _baseDir;

    public LocalServiceHost(PanelSettings settings, Dispatcher dispatcher, string? baseDir = null)
    {
        _settings = settings;
        _process = new ServiceProcess(dispatcher);
        _baseDir = baseDir ?? AppContext.BaseDirectory;
        _process.LogReceived += line => Log?.Invoke(line);
        _process.Exited += code => ServiceExited?.Invoke(code);
    }

    public event Action<string>? Log;
    public event Action<int>? ServiceExited;

    public bool IsServiceRunning => _process.IsRunning;
    public bool IsAdministrator => Elevation.IsAdministrator();
    public IReadOnlyList<string> LocalIPv4s => NetworkInfo.EnumerateIPv4();
    public string LocalEndpoint => NetworkInfo.DescribeEndpoint(_settings.DirectPort);
    public string? ServiceExePath => ServiceCli.ResolveServiceExe(_settings, _baseDir);
    public string? ClientExePath => ServiceCli.ResolveClientExe(_settings, _baseDir);

    public async Task<OneShotResult> PrintConfigAsync(CancellationToken ct = default)
    {
        var exe = RequireServiceExe();
        return await ServiceCli.RunOneShotAsync(exe, ServiceCli.BuildPrintConfigArgs(_settings), ct: ct).ConfigureAwait(false);
    }

    public async Task<OneShotResult> PairAsync(string peerPubB64, CancellationToken ct = default)
    {
        var exe = RequireServiceExe();
        return await ServiceCli.RunOneShotAsync(exe, ServiceCli.BuildPairArgs(_settings, peerPubB64), ct: ct).ConfigureAwait(false);
    }

    public async Task<FirewallStatus?> GetFirewallStatusAsync(CancellationToken ct = default)
    {
        var exe = RequireServiceExe();
        var r = await ServiceCli.RunOneShotAsync(exe, ServiceCli.BuildFirewallStatusArgs(_settings.DirectPort), ct: ct).ConfigureAwait(false);
        if (!r.Ok) return null;
        return ServiceOutputParser.ParseFirewallStatus(r.StdOut);
    }

    public async Task<OneShotResult> SetFirewallAsync(bool enable, CancellationToken ct = default)
    {
        var exe = RequireServiceExe();
        return await ServiceCli.RunOneShotAsync(exe, ServiceCli.BuildFirewallSetArgs(_settings.DirectPort, enable), ct: ct).ConfigureAwait(false);
    }

    public async Task StartServiceAsync(CancellationToken ct = default)
    {
        if (_process.IsRunning) return;
        var exe = RequireServiceExe();
        await _process.StartAsync(exe, ServiceCli.BuildRunArgs(_settings), ct).ConfigureAwait(false);
    }

    public Task StopServiceAsync() => _process.StopAsync();

    public Task<StatusResult?> GetStatusAsync(CancellationToken ct = default) =>
        PipeRpc.TryGetStatusAsync(_settings.ClientPipeName, ct: ct);

    public bool LaunchClient()
    {
        var exe = ClientExePath;
        if (exe is null) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? _baseDir,
            };
            // 必须告诉客户端用哪个实例：客户端默认连 `DeskLink.Client.default`，
            // 而面板是用 `--data-dir <用户选的>` 起的服务，实例名 = 该路径末段（小写）。
            // 不传的话客户端会去连一个根本不存在的管道，表现为"打开了但全是连接失败"。
            psi.ArgumentList.Add("--instance");
            psi.ArgumentList.Add(_settings.InstanceId);
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public bool TryRestartElevated() =>
        Elevation.TryRestartElevated(
            Environment.ProcessPath ?? throw new InvalidOperationException("无法确定当前程序路径"),
            new[] { Elevation.ElevatedMarker });

    private string RequireServiceExe() =>
        ServiceExePath ?? throw new FileNotFoundException(
            "找不到 DeskLink.Service.exe。请确认面板与 Service 在同一发布目录，" +
            "或在「高级」里手动指定 Service 可执行文件路径。");

    public async ValueTask DisposeAsync() => await _process.DisposeAsync().ConfigureAwait(false);
}
