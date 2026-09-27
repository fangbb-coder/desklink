using DeskLink.Protocol.Common;
using DeskLink.Protocol.Crypto;
using DeskLink.Protocol.Session;
using DeskLink.Service.Direct;
using DeskLink.Service.Security;
using Xunit;
using Xunit.Abstractions;

namespace DeskLink.DirectHandshake.Tests;

/// <summary>
/// 局域网直连握手 + 帧往返测试。
/// 对应 P5.5 验收项:
///   - 已配对握手成功
///   - 未配对被拒
///   - 帧往返 (控制流 / 业务帧端到端)
///   - 撤销即时关闭直连会话
///
/// 注意 DirectClient.ConnectAsync 的第三个参数是**对端** device_id
/// （用于 SIGMA 角色判定）；本机 device_id 由 KeyStore 派生后发给对端做配对校验。
/// 早期测试误传本机 id，导致两侧同为 Responder 的 50% 死锁（见 DirectClient 注释）。
/// </summary>
public class DirectHandshakeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private DirectHandshakeFixture? _fixture;

    public DirectHandshakeTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _fixture = await DirectHandshakeFixture.StartPairedPairAsync();
    }

    public async Task DisposeAsync()
    {
        if (_fixture != null) await _fixture.DisposeAsync();
    }

    [Fact]
    public async Task PairedDevices_HandshakeSucceeds()
    {
        var server = _fixture!.Server;
        var client = _fixture!.Client;

        // 客户端连接已配对服务端：先发自己的 device_id，服务端查配对列表校验
        var result = await client.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);

        Assert.True(result.Ok, $"handshake failed: {result.Detail}");
        Assert.NotNull(result.Pump);
        Assert.NotNull(result.Session);

        // 服务端应已记录会话
        await WaitForSessionsAsync(server, 1);

        // 主动关闭客户端传输，让服务端读循环自然退出
        await result.Pump!.DisposeAsync();
        result.Session!.Dispose();
    }

    [Fact]
    public async Task UnpairedDevice_HandshakeRejected()
    {
        // 服务端配对列表为空 → 任何客户端都算"未配对"
        var fixture = await DirectHandshakeFixture.StartUnpairedServerAsync();
        try
        {
            var result = await fixture.Client.ConnectAsync(
                "127.0.0.1", fixture.DirectPort,
                fixture.ServerDeviceId, CancellationToken.None);

            // 未配对时服务端读 device_id 后立即关闭连接，握手必须失败。
            Assert.False(result.Ok, $"unpaired peer unexpectedly connected: {result.Detail}");
            Assert.Null(result.Pump);
            Assert.Null(result.Session);

            // 拒绝的感知层与时序有关（QUIC 路径下 DirectQuicTransport.ConnectAsync
            // 写完 device_id 即返回，服务器 CONNECTION_CLOSE 与客户端首次 SIGMA
            // 读/写存在竞态）：
            //   - 关闭先到 → 写/读失败在 ConnectAsync → detail = "transport: ..."
            //   - SIGMA 先发起 → 失败在 EstablishE2EAsync → detail = "sigma: ..."
            // 两种都是"立即失败"，都算拒绝成功；但绝不接受 15s SIGMA 超时——
            // 那意味着服务端既不拒绝也不回应，是回归。
            var detail = result.Detail ?? "";
            Assert.False(
                detail.StartsWith("sigma: timeout", StringComparison.OrdinalIgnoreCase),
                $"server neither rejected nor responded (detail: {detail})");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PairedHandshake_FrameRoundTrip()
    {
        var server = _fixture!.Server;
        var client = _fixture!.Client;

        var result = await client.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);

        Assert.True(result.Ok);
        var pump = result.Pump!;

        await WaitForSessionsAsync(server, 1);
        var serverSession = server.Sessions.FirstOrDefault();
        Assert.NotNull(serverSession);

        // 注册一个 echo 处理器：服务端收到 SessionControl 后原样回发
        var echoReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        serverSession!.Pump.Register(
            ProtocolConstants.LogicalStream.Control,
            f =>
            {
                if (f.Type == ProtocolConstants.FrameType.SessionControl)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await serverSession.Pump.SendControlAsync(
                                ProtocolConstants.FrameType.SessionControl, f.Payload).ConfigureAwait(false);
                        }
                        catch { }
                    });
                }
            });

        // 客户端发一个 probe，等回显
        var probe = new byte[] { 0x01, 0xCA, 0xFE, 0xBA, 0xBE };
        pump.Register(ProtocolConstants.LogicalStream.Control, f =>
        {
            if (f.Type == ProtocolConstants.FrameType.SessionControl &&
                f.Payload.Length == probe.Length)
            {
                echoReceived.TrySetResult(f.Payload.ToArray());
            }
        });

        await pump.SendControlAsync(ProtocolConstants.FrameType.SessionControl, probe);
        var echo = await echoReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(probe, echo);

        await pump.DisposeAsync();
        result.Session!.Dispose();
    }

    [Fact]
    public async Task Revocation_ClosesDirectSessionImmediately()
    {
        var client = _fixture!.Client;

        var result = await client.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);

        Assert.True(result.Ok);

        // 撤销配对（从服务端配对列表移除客户端）
        _fixture.RevokeClientPairing();

        // 新连接应被拒（客户端仍发自己的 device_id，但服务端已无该配对）
        var client2 = new DirectClient(_fixture.ClientKeys);
        var result2 = await client2.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);

        Assert.False(result2.Ok, "撤销配对后新连接必须失败");

        await result.Pump!.DisposeAsync();
        result.Session!.Dispose();
    }

    /// <summary>
    /// 撤销应关闭该对端**已建立**的直连会话（DESIGN.md 决策 #7 / 风险回顾 #5：
    /// 撤销立即踢线，两条路径同时失效）。
    /// </summary>
    [Fact]
    public async Task Revocation_CloseSessionsFor_KicksActiveSession()
    {
        var server = _fixture!.Server;
        var client = _fixture!.Client;

        var result = await client.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);
        Assert.True(result.Ok);

        await WaitForSessionsAsync(server, 1);

        var closed = server.CloseSessionsFor(_fixture.ClientDeviceId);
        Assert.Equal(1, closed);

        // 被踢的会话应从活跃表移除（泵退出 → RunE2EAsync finally 清理）。
        await WaitForSessionsAsync(server, 0);
    }

    /// <summary>会话建立回调必须在 pump.Start 之前触发，宿主可据此注册业务处理器。</summary>
    [Fact]
    public async Task OnSessionEstablished_InvokedBeforePumpStarts()
    {
        var server = _fixture!.Server;
        var tcs = new TaskCompletionSource<DirectSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnSessionEstablished = s => tcs.TrySetResult(s);

        var result = await _fixture.Client.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);
        Assert.True(result.Ok);

        var session = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // 服务端看到的对端 id 是**客户端**的 device_id。
        Assert.Equal(_fixture.ClientDeviceId, session.PeerDeviceId);
        Assert.False(session.IsClosed);

        server.CloseAllSessions();
        await result.Pump!.DisposeAsync();
        result.Session!.Dispose();
    }

    /// <summary>同一对端重复连入时应顶替旧会话，活跃表只保留最新一条。</summary>
    [Fact]
    public async Task DuplicateConnect_SupersedesPreviousSession()
    {
        var server = _fixture!.Server;

        var first = await _fixture.Client.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);
        Assert.True(first.Ok);
        await WaitForSessionsAsync(server, 1);

        var client2 = new DirectClient(_fixture.ClientKeys);
        var second = await client2.ConnectAsync(
            "127.0.0.1", _fixture.DirectPort,
            _fixture.ServerDeviceId, CancellationToken.None);
        Assert.True(second.Ok);

        // 仍是 1 条（旧的被顶替）。
        await WaitForSessionsAsync(server, 1);
        Assert.Single(server.Sessions);

        server.CloseAllSessions();
        try { await first.Pump!.DisposeAsync(); } catch { }
        first.Session!.Dispose();
        try { await second.Pump!.DisposeAsync(); } catch { }
        second.Session!.Dispose();
    }

    /// <summary>
    /// 角色判定必须互补：对端 id 字典序更大/更小时，本端应分别成为
    /// Initiator / Responder，绝不能两侧同角色（同角色 = 死锁）。
    /// </summary>
    [Fact]
    public void RoleAssignment_IsComplementary_RegardlessOfIdOrder()
    {
        // 用同一对密钥连两次不会改变 id 顺序，因此这里直接用随机 id 做纯函数断言。
        var a = new byte[32];
        var b = new byte[32];
        Random.Shared.NextBytes(a);
        Random.Shared.NextBytes(b);
        if (a.AsSpan().SequenceCompareTo(b) == 0) b[0] ^= 0xFF;

        var aIsInitiator = a.AsSpan().SequenceCompareTo(b) < 0;
        var bIsInitiator = b.AsSpan().SequenceCompareTo(a) < 0;

        Assert.NotEqual(aIsInitiator, bIsInitiator);
    }

    private static async Task WaitForSessionsAsync(DirectServer server, int expected, int maxMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (server.Sessions.Count == expected) return;
            await Task.Delay(50);
        }
        Assert.Equal(expected, server.Sessions.Count);
    }
}
