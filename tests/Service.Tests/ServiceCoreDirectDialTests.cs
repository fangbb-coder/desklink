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

        // 这里以前断言的是 Assert.False(result.Ok) —— 那是把缺陷当规范写下来了：
        // ok 混着"值有没有变"，客户端于是把"同值保存"当失败弹红条。
        // 现在分工明确：ok = 执行成功，changed = 值变了没有。
        Assert.True(result.Ok);
        Assert.False(result.Changed);
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

    // ───────────────────── 参数校验必须排在副作用之前 ─────────────────────

    [Fact]
    public void SetConfig_非法monitorIndex不能留下半套配置()
    {
        // 校验排在副作用之后时会发生什么：file_scope_roots 先写进 _options，
        // 下一个参数才抛异常；异常在管道层被吞成 "internal error"，
        // 于是用户看到一句没头没尾的报错，内存里的授权目录却已经变了、还没落盘。
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            FileScopeRoots = new List<string> { @"C:\old\share" },
        };
        var core = NewCore(options);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            core.SetConfig(null, null, new[] { @"D:\new\share" }, -1));

        Assert.Equal(new[] { @"C:\old\share" }, options.FileScopeRoots);
        // 内存没变，就不该留下一个"下次启动会生效"的落盘文件
        Assert.False(File.Exists(Path.Combine(_tmp, "service.json")));
    }

    [Fact]
    public void SetConfig_非法relayUrl同样不能留下半套配置()
    {
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            FileScopeRoots = new List<string> { @"C:\old\share" },
        };
        var core = NewCore(options);

        Assert.Throws<ArgumentException>(() =>
            core.SetConfig("not a url", null, new[] { @"D:\new\share" }, 2));

        Assert.Equal(new[] { @"C:\old\share" }, options.FileScopeRoots);
        Assert.False(File.Exists(Path.Combine(_tmp, "service.json")));
    }

    // ───────────────────── ok 表达"成功"而不是"变了" ─────────────────────

    [Fact]
    public void SetConfig_存了相同的值也是成功()
    {
        // 以前 Ok 跟着 changed 走：同值保存回报 false，客户端会弹红条当失败，
        // 用户点一次被气一次，却什么也没改坏。
        var options = new ServiceOptions
        {
            DataDir = _tmp,
            FileScopeRoots = new List<string> { @"C:\same\share" },
            CaptureMonitorIndex = 2,
        };
        var core = NewCore(options);

        var result = core.SetConfig(null, null, new[] { @"C:\same\share" }, 2);

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.False(result.RequiresRestart);
    }

    [Fact]
    public void SetConfig_值变了要如实回报changed()
    {
        var options = new ServiceOptions { DataDir = _tmp, CaptureMonitorIndex = 0 };
        var core = NewCore(options);

        var result = core.SetConfig(null, null, new[] { @"D:\share" }, 3);

        Assert.True(result.Ok);
        Assert.True(result.Changed);
    }

    // ───────────────────── 落盘失败必须说出来 ─────────────────────

    [Fact]
    public void SetConfig_值没变但落盘失败_仍然必须给出提示()
    {
        // 这条路径以前完全没法说出口：BuildRestartHint 见 !requiresRestart 就 return null，
        // 于是"同值保存 + 写盘失败"被渲染成一句"已保存"——用户以为记住了，实际没记住。
        //
        // 造一个真的写不进去的 DataDir：在 data-dir 的位置上先放一个**同名文件**，
        // Save 里的 Directory.CreateDirectory 就会抛，落到 catch 返回 false。
        // （只用一个不存在的目录是没用的——Save 会自己把它建出来。）
        var blocker = Path.Combine(_tmp, "blocker");
        File.WriteAllText(blocker, "这是文件，不是目录");
        var broken = new ServiceOptions
        {
            DataDir = Path.Combine(blocker, "sub"),
            CaptureMonitorIndex = 1,
        };
        var brokenCore = NewCore(broken);

        // 只传 monitorIndex 且与当前相同 → changed=false、requiresRestart=false
        var result = brokenCore.SetConfig(null, null, monitorIndex: 1);

        Assert.True(result.Ok);
        Assert.False(result.Changed);
        Assert.False(result.RequiresRestart);
        Assert.False(result.Persisted);
        Assert.False(string.IsNullOrWhiteSpace(result.RestartHint));
        Assert.Contains("落盘", result.RestartHint);
    }

    [Fact]
    public void SetConfig_正常落盘时persisted为真且无多余提示()
    {
        var options = new ServiceOptions { DataDir = _tmp, CaptureMonitorIndex = 0 };
        var core = NewCore(options);

        var result = core.SetConfig(null, null, new[] { @"D:\share" }, 0);

        Assert.True(result.Persisted);
        // 有变化 → 需要重启 → 有提示是合理的
        Assert.Contains("重启", result.RestartHint);
        Assert.True(File.Exists(Path.Combine(_tmp, "service.json")));
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
