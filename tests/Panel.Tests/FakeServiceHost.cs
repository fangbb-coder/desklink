using DeskLink.Panel;
using DeskLink.Panel.Services;
using DeskLink.Protocol.Pipe;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>
/// MainViewModel 的替身宿主。不起任何真实进程/管道，因此 ViewModel 的全部按钮逻辑
/// 可以在毫秒级、无副作用地反复验证。
/// </summary>
public sealed class FakeServiceHost : IServiceHost
{
    public bool IsServiceRunning { get; private set; }
    public bool IsAdministrator { get; set; } = true;
    public IReadOnlyList<string> LocalIPv4s { get; set; } = new[] { "192.168.1.20" };
    public string LocalEndpoint { get; set; } = "192.168.1.20:47200";
    public string? ServiceExePath { get; set; } = @"C:\fake\DeskLink.Service.exe";
    public string? ClientExePath { get; set; } = @"C:\fake\DeskLink.Client.exe";

    // 可编排的返回值
    public OneShotResult PrintConfigResult { get; set; } = new(0, """
        ServiceOptions { DataDir=C:\dl, InstanceId=dl }
        KeyStore: C:\dl\keystore.json
          ed25519_pub_b64 = LOCALPUB0000000000000000000000000000000000000=
          device_id       = AABBCC
          quic_available  = true
        """, "");
    public OneShotResult PairResult { get; set; } = new(0, "paired", "");
    public OneShotResult FirewallSetResult { get; set; } = new(0, "ok", "");
    public FirewallStatus? FirewallResult { get; set; } = new(47200, true, true, true, true);
    public StatusResult? StatusResult { get; set; } = new() { RelayState = "disconnected", DirectActiveSessions = 0 };

    // 调用记录
    public List<string> PairedPubs { get; } = new();
    public bool FirewallSetCalled { get; private set; }
    public bool FirewallSetEnable { get; private set; }
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool LaunchClientCalled { get; private set; }
    public int ElevateRequested { get; private set; }

    public event Action<string>? Log;
    public event Action<int>? ServiceExited;

    public Task<OneShotResult> PrintConfigAsync(CancellationToken ct = default) => Task.FromResult(PrintConfigResult);
    public Task<OneShotResult> PairAsync(string peerPubB64, CancellationToken ct = default)
    {
        PairedPubs.Add(peerPubB64);
        return Task.FromResult(PairResult);
    }
    public Task<FirewallStatus?> GetFirewallStatusAsync(CancellationToken ct = default) => Task.FromResult(FirewallResult);
    public Task<OneShotResult> SetFirewallAsync(bool enable, CancellationToken ct = default)
    {
        FirewallSetCalled = true;
        FirewallSetEnable = enable;
        return Task.FromResult(FirewallSetResult);
    }
    public Task StartServiceAsync(CancellationToken ct = default)
    {
        StartCount++;
        IsServiceRunning = true;
        return Task.CompletedTask;
    }
    public Task StopServiceAsync()
    {
        StopCount++;
        IsServiceRunning = false;
        return Task.CompletedTask;
    }
    public Task<StatusResult?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(StatusResult);
    public bool LaunchClient() { LaunchClientCalled = true; return ClientExePath is not null; }
    public bool TryRestartElevated() { ElevateRequested++; return true; }

    public void RaiseLog(string line) => Log?.Invoke(line);
    public void RaiseExited(int code) => ServiceExited?.Invoke(code);
}
