using DeskLink.Protocol.Common;
using DeskLink.Protocol.Session;
using DeskLink.Service.Direct;
using DeskLink.Service.Session;
using Xunit;

namespace DeskLink.DirectHandshake.Tests;

/// <summary>
/// P5.5 **出站**拨号（DirectDialer）测试——WPF 客户端"局域网直连"背后的真正实现。
///
/// 为什么单独覆盖：这条路径此前**完全不存在**。WPF 客户端校验完 IP:端口就
/// 显示"已连接"，而没有任何 RPC 让 Service 去拨号（唯一能建直连会话的是
/// 一次性 CLI <c>--direct-probe</c>）。补上这条链路时最该证明的不是"参数能传"，
/// 而是**两端真的握手成功、服务端真的看到了会话**。
/// </summary>
public class DirectDialerTests : IAsyncLifetime
{
    private DirectHandshakeFixture? _fixture;

    public async Task InitializeAsync()
    {
        _fixture = await DirectHandshakeFixture.StartPairedPairAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fixture != null) await _fixture.DisposeAsync();
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, int maxMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (predicate()) return true;
            await Task.Delay(50);
        }
        return predicate();
    }

    [Fact]
    public async Task DialAsync_Establishes_Real_Session_And_Server_Sees_Peer()
    {
        var f = _fixture!;

        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);

        Assert.True(outcome.Ok, $"拨号应成功，实际失败：{outcome.Detail}");
        Assert.False(string.IsNullOrEmpty(outcome.Transport));

        // 关键断言：服务端真的建立了会话，且对端是"客户端"这个设备。
        // 只断言 outcome.Ok 是不够的——那只能证明本地没抛异常。
        var saw = await WaitUntilAsync(() =>
            f.Server.Sessions.Any(s => s.PeerDeviceId.AsSpan().SequenceEqual(f.ClientDeviceId)));

        Assert.True(saw, "DirectServer 未观察到出站拨号建立的会话");
    }

    [Fact]
    public async Task DialAsync_Populates_Outbound_Session_Table()
    {
        var f = _fixture!;
        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);
        Assert.True(outcome.Ok, outcome.Detail);

        // 出站会话必须进入活跃表，否则 ServiceCore.GetStatus 的
        // DirectActiveSessions 永远是 0，客户端的会话存在性校验会误判。
        var visible = await WaitUntilAsync(() => f.Dialer.Sessions.Count == 1);
        Assert.True(visible, "出站会话未进入 DirectDialer.Sessions");
        Assert.True(f.Dialer.Sessions[0].PeerDeviceId.AsSpan().SequenceEqual(f.ServerDeviceId));
    }

    [Fact]
    public async Task OnSessionEstablished_Fires_Before_Pump_Starts()
    {
        var f = _fixture!;
        var attached = false;
        DirectSession? captured = null;
        f.Dialer.OnSessionEstablished = s =>
        {
            // 处理器必须在 pump.Start 之前挂上，否则对端立即发来的首帧会丢。
            attached = true;
            captured = s;
        };

        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);

        Assert.True(outcome.Ok, outcome.Detail);
        Assert.True(attached, "OnSessionEstablished 未被调用");
        Assert.NotNull(captured);
    }

    [Fact]
    public async Task Second_Dial_Supersedes_Previous_Outbound_Session()
    {
        var f = _fixture!;
        var first = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);
        Assert.True(first.Ok, first.Detail);
        await WaitUntilAsync(() => f.Dialer.Sessions.Count == 1);

        var second = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);
        Assert.True(second.Ok, second.Detail);

        // 顶替：同一对端重复拨号只保留最新一条，旧连接不泄漏。
        var settled = await WaitUntilAsync(() => f.Dialer.Sessions.Count == 1);
        Assert.True(settled, "重复拨号后应只剩一条出站会话");
    }

    [Fact]
    public async Task CloseAllSessions_Terminates_Outbound_Session()
    {
        var f = _fixture!;
        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);
        Assert.True(outcome.Ok, outcome.Detail);
        await WaitUntilAsync(() => f.Dialer.Sessions.Count == 1);

        var closed = f.Dialer.CloseAllSessions();
        Assert.Equal(1, closed);
        Assert.Empty(f.Dialer.Sessions);
    }

    [Fact]
    public async Task CloseSessionsFor_Revokes_Only_Targeted_Peer()
    {
        var f = _fixture!;
        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);
        Assert.True(outcome.Ok, outcome.Detail);
        await WaitUntilAsync(() => f.Dialer.Sessions.Count == 1);

        // 撤销一个**未在会话中**的 device_id：必须是 0，不能误伤现有会话。
        var stranger = new byte[32];
        Random.Shared.NextBytes(stranger);
        Assert.Equal(0, f.Dialer.CloseSessionsFor(stranger));

        var closed = f.Dialer.CloseSessionsFor(f.ServerDeviceId);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task DialAsync_Unpaired_Peer_Fails_Fast_With_Clear_Reason()
    {
        var f = _fixture!;

        // 把客户端的配对列表清空 → 控制端"没配过这台设备"。
        // 期望：本地就拒绝并给出可读原因，而不是浪费一次网络往返让对端拒绝。
        foreach (var p in f.ClientPairings.List().ToList())
        {
            f.ClientPairings.Remove(Convert.FromBase64String(p.PeerPubB64));
        }

        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, f.ServerDeviceId);

        Assert.False(outcome.Ok);
        Assert.Contains("配对", outcome.Detail ?? "", StringComparison.Ordinal);
        Assert.Empty(f.Dialer.Sessions);
        Assert.Empty(f.Server.Sessions);
    }

    [Theory]
    [InlineData("", 47200)]
    [InlineData("   ", 47200)]
    public async Task DialAsync_Blank_Host_Is_Rejected_Without_Network(string host, int port)
    {
        var f = _fixture!;
        var outcome = await f.Dialer.DialAsync(host, port, f.ServerDeviceId);

        Assert.False(outcome.Ok);
        Assert.Contains("地址为空", outcome.Detail ?? "", StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public async Task DialAsync_Out_Of_Range_Port_Is_Rejected(int port)
    {
        var f = _fixture!;
        var outcome = await f.Dialer.DialAsync("127.0.0.1", port, f.ServerDeviceId);

        Assert.False(outcome.Ok);
        Assert.Contains("端口越界", outcome.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task DialAsync_Wrong_Length_DeviceId_Is_Rejected()
    {
        var f = _fixture!;
        var outcome = await f.Dialer.DialAsync("127.0.0.1", f.DirectPort, new byte[16]);

        Assert.False(outcome.Ok);
        Assert.Contains("device_id", outcome.Detail ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task DialAsync_Nothing_Listening_Fails_Without_Hanging()
    {
        var f = _fixture!;
        // 监听一个确定没人监的端口：必须**快速失败**（返回 Ok=false），
        // 而不是挂住让 UI 转圈。直连没有 RelayClient 的退避循环兜底。
        var deadPort = 47000 + Random.Shared.Next(1000, 2000);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var outcome = await f.Dialer.DialAsync("127.0.0.1", deadPort, f.ServerDeviceId);

        sw.Stop();
        Assert.False(outcome.Ok);
        Assert.NotNull(outcome.Detail);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20),
            $"拨号失败耗时过长（{sw.Elapsed}），说明缺少超时保护");
        Assert.Empty(f.Dialer.Sessions);
    }
}
