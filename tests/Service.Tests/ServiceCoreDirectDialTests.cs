// 缺陷修复的回归测试：direct_dial RPC（缺陷①）与 SetConfig 的"需重启"语义（缺陷③）。
//
// 这两个都属于**契约诚实性**问题，单元测试比真机验证更重要：
//   - direct_dial：WPF 客户端"局域网直连"此前从不拨号，只校验地址就显示已连接；
//   - requires_restart：中继地址改了不会重建 RelayClient，UI 却弹"已保存"。

using DeskLink.Protocol.Pipe;
using DeskLink.Service;
using DeskLink.Service.Configuration;
using DeskLink.Service.Direct;
using DeskLink.Service.Process;
using DeskLink.Service.Relay;
using DeskLink.Service.Security;
using DeskLink.Service.Session;
using Xunit;

namespace DeskLink.Service.Tests;

public class ServiceCoreDirectDialTests : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), $"desklink-core-{Guid.NewGuid():N}");

    public ServiceCoreDirectDialTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ServiceCore NewCore(ServiceOptions options, DirectDialer? dialer = null)
    {
        var keys = new KeyStore(_tmp);
        var pairings = new PairingStore(_tmp);
        var agent = new AgentLauncher(Path.Combine(_tmp, "no-such-agent.exe"));
        // isElevated=false → 防火墙相关调用不会真的动系统状态。
        var firewall = new FirewallHelper(log: null, isElevated: () => false);
        var runner = new RelaySessionRunner(keys, pairings, options, log: null);

        return new ServiceCore(
            keys, pairings, agent, firewall, options,
            relay: null, runner, direct: null, dialer,
            media: null, log: null);
    }

    // ───────────────────────── 缺陷③：SetConfig 诚实性 ─────────────────────────

    [Fact]
    public void SetConfig_Changing_RelayUrl_Reports_RequiresRestart()
    {
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            RelayUrl = new Uri("https://old-relay.example:8443"),
        };
        var core = NewCore(options);

        var result = core.SetConfig("https://new-relay.example:8443", null);

        Assert.True(result.Ok);
        // 关键：RelayClient 在启动时就构造好了，不重建 → 必须告诉用户。
        Assert.True(result.RequiresRestart);
        Assert.False(string.IsNullOrWhiteSpace(result.RestartHint));
    }

    [Fact]
    public void SetConfig_Same_RelayUrl_Is_Not_A_Change()
    {
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            RelayUrl = new Uri("https://same.example:8443"),
        };
        var core = NewCore(options);

        // 同一个地址重复保存：不应报"有变更"，更不该提示要重启。
        var result = core.SetConfig("https://same.example:8443", null);

        Assert.False(result.Ok);
        Assert.False(result.RequiresRestart);
        Assert.Null(result.RestartHint);
    }

    [Fact]
    public void SetConfig_Changing_DirectPort_Is_Immediate_No_Restart()
    {
        var options = new ServiceOptions { DataDir = _tmp, DirectPort = 47200 };
        var core = NewCore(options);

        var result = core.SetConfig(null, 47300);

        Assert.True(result.Ok);
        // 端口改动走 FirewallHelper.ApplyPortChange，是立即生效的。
        Assert.False(result.RequiresRestart);
        Assert.Equal(47300, options.DirectPort);
    }

    [Fact]
    public void GetConfig_Reports_ActiveRelayUrl_Separately_From_Configured()
    {
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            RelayUrl = new Uri("https://old-relay.example:8443"),
        };
        var core = NewCore(options);

        core.SetConfig("https://new-relay.example:8443", null);

        var cfg = core.GetConfig();
        // 已保存的值 = 用户填的（Uri.ToString() 会规范化，补一个尾斜杠）
        Assert.Equal(new Uri("https://new-relay.example:8443").ToString(), cfg.RelayUrl);
        // 实际在用的值 = 启动快照（旧地址）——UI 据此提示"需重启"。
        Assert.Equal(new Uri("https://old-relay.example:8443").ToString(), cfg.ActiveRelayUrl);
        Assert.NotEqual(cfg.RelayUrl, cfg.ActiveRelayUrl);
    }

    [Fact]
    public void GetConfig_ActiveRelayUrl_Matches_Configured_When_Unchanged()
    {
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            RelayUrl = new Uri("https://relay.example:8443"),
        };
        var core = NewCore(options);

        var cfg = core.GetConfig();
        Assert.Equal(cfg.RelayUrl, cfg.ActiveRelayUrl);
    }

    [Fact]
    public void SetConfig_Invalid_RelayUrl_Throws()
    {
        var options = new ServiceOptions { DataDir = _tmp };
        var core = NewCore(options);

        Assert.Throws<ArgumentException>(() => core.SetConfig("not a url", null));
    }

    // ───────────────────────── 缺陷①：direct_dial ─────────────────────────

    [Fact]
    public async Task DialDirectAsync_Without_Dialer_Fails_Explicitly()
    {
        var core = NewCore(new ServiceOptions { DataDir = _tmp }, dialer: null);

        var r = await core.DialDirectAsync(
            Convert.ToBase64String(new byte[32]), "127.0.0.1", 47200);

        Assert.False(r.Ok);
        // 不能静默成功，也不能抛异常——UI 要能显示原因。
        Assert.False(string.IsNullOrWhiteSpace(r.Detail));
    }

    [Fact]
    public async Task DialDirectAsync_Rejects_Malformed_PeerPub()
    {
        var dialer = new DirectDialer(
            new KeyStore(_tmp), new PairingStore(_tmp),
            new ServiceOptions { DataDir = _tmp });
        await using (dialer)
        {
            var core = NewCore(new ServiceOptions { DataDir = _tmp }, dialer);

            var notBase64 = await core.DialDirectAsync("!!!not-base64!!!", "127.0.0.1", 47200);
            Assert.False(notBase64.Ok);
            Assert.Contains("base64", notBase64.Detail ?? "", StringComparison.Ordinal);

            var wrongLen = await core.DialDirectAsync(
                Convert.ToBase64String(new byte[16]), "127.0.0.1", 47200);
            Assert.False(wrongLen.Ok);
            Assert.Contains("32", wrongLen.Detail ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task DialDirectAsync_Unpaired_Peer_Is_Rejected_Locally()
    {
        var dialer = new DirectDialer(
            new KeyStore(_tmp), new PairingStore(_tmp),
            new ServiceOptions { DataDir = _tmp });
        await using (dialer)
        {
            var core = NewCore(new ServiceOptions { DataDir = _tmp }, dialer);

            // 配对列表为空 → 必须在本地就拒绝，不打网络。
            var r = await core.DialDirectAsync(
                Convert.ToBase64String(new byte[32]), "127.0.0.1", 47200);

            Assert.False(r.Ok);
            Assert.Contains("配对", r.Detail ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GetStatus_DirectActiveSessions_Counts_Outbound_Sessions()
    {
        var options = new ServiceOptions { DataDir = _tmp };
        var dialer = new DirectDialer(
            new KeyStore(_tmp), new PairingStore(_tmp), options);
        var core = NewCore(options, dialer);

        // 没有会话时必须是 0——客户端的会话存在性校验依赖这个值。
        Assert.Equal(0, core.GetStatus().DirectActiveSessions);
    }
}
