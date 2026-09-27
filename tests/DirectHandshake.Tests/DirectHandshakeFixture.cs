using DeskLink.Protocol.Common;
using DeskLink.Protocol.Session;
using DeskLink.Service.Configuration;
using DeskLink.Service.Direct;
using DeskLink.Service.Security;
using DeskLink.Service.Session;

namespace DeskLink.DirectHandshake.Tests;

/// <summary>
/// 双 --data-dir 实例启动：服务端（被控端）+ 客户端（控制端）。
/// 各自拥有独立的 KeyStore / PairingStore，并预先完成配对（交换公钥）。
/// </summary>
public sealed class DirectHandshakeFixture : IAsyncDisposable
{
    /// <summary>
    /// 测试时是否禁用 QUIC 监听。
    ///
    /// 环境变量 <c>DESKLINK_TEST_DISABLE_QUIC=1</c> 可强制走纯 TCP/TLS 路径，
    /// 用于定位 QUIC 监听器在收尾时引发的 native 崩溃（MsQuic 已知在
    /// DisposeAsync 与 AcceptConnectionAsync 并发时不稳）。默认不禁用。
    /// </summary>
    private static bool DisableQuicForTests =>
        Environment.GetEnvironmentVariable("DESKLINK_TEST_DISABLE_QUIC") == "1";

    public int DirectPort { get; }

    public string ServerDataDir { get; }
    public string ClientDataDir { get; }

    /// <summary>服务端 KeyStore（含服务端 Ed25519 密钥对）。</summary>
    public KeyStore ServerKeys { get; }

    /// <summary>客户端 KeyStore（含客户端 Ed25519 密钥对）。</summary>
    public KeyStore ClientKeys { get; }

    /// <summary>服务端配对列表（已包含客户端公钥）。</summary>
    public PairingStore ServerPairings { get; }

    /// <summary>客户端配对列表（已包含服务端公钥）。</summary>
    public PairingStore ClientPairings { get; }

    /// <summary>服务端 device_id（= BLAKE3(pub)）。</summary>
    public byte[] ServerDeviceId { get; }

    /// <summary>客户端 device_id（= BLAKE3(pub)）。</summary>
    public byte[] ClientDeviceId { get; }

    /// <summary>直连服务端实例。</summary>
    public DirectServer Server { get; }

    /// <summary>直连客户端实例（已配置为连接服务端）。</summary>
    public DirectClient Client { get; }

    private DirectHandshakeFixture(string serverDir, string clientDir)
    {
        // 随机端口避免测试并行/串行时的 TIME_WAIT 冲突
        DirectPort = 47000 + Random.Shared.Next(1000);
        ServerDataDir = serverDir;
        ClientDataDir = clientDir;

        ServerKeys = new KeyStore(serverDir);
        ClientKeys = new KeyStore(clientDir);
        ServerPairings = new PairingStore(serverDir);
        ClientPairings = new PairingStore(clientDir);

        // 预生成密钥对
        var serverPub = ServerKeys.LoadOrCreate().KeyPair.Ed25519Public;
        var clientPub = ClientKeys.LoadOrCreate().KeyPair.Ed25519Public;

        ServerDeviceId = E2ESessionHost.ComputeDeviceId(serverPub);
        ClientDeviceId = E2ESessionHost.ComputeDeviceId(clientPub);

        // 互相加入配对列表
        ServerPairings.Add(clientPub, "client");
        ClientPairings.Add(serverPub, "server");

        var serverOpts = new ServiceOptions { DataDir = serverDir };
        Server = new DirectServer(ServerPairings, ServerKeys, serverOpts, DirectPort,
            log: null, enableQuic: !DisableQuicForTests);

        Client = new DirectClient(ClientKeys);
    }

    /// <summary>启动一对已配对实例。</summary>
    public static async Task<DirectHandshakeFixture> StartPairedPairAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"desklink-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var server = Path.Combine(tempRoot, "server");
        var client = Path.Combine(tempRoot, "client");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(client);

        var fixture = new DirectHandshakeFixture(server, client);
        await fixture.Server.StartAsync(CancellationToken.None);
        // 等监听就绪
        await WaitForPortAsync(fixture.DirectPort);
        return fixture;
    }

    /// <summary>启动一个"未配对"服务端（用于测试拒绝场景）。</summary>
    public static async Task<DirectHandshakeFixture> StartUnpairedServerAsync()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"desklink-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var server = Path.Combine(tempRoot, "server");
        var client = Path.Combine(tempRoot, "client");
        Directory.CreateDirectory(server);
        Directory.CreateDirectory(client);

        var fixture = new DirectHandshakeFixture(server, client);
        // 清掉服务端配对列表 → 客户端变成"未配对"
        foreach (var p in fixture.ServerPairings.List().ToList())
        {
            fixture.ServerPairings.Remove(Convert.FromBase64String(p.PeerPubB64));
        }
        await fixture.Server.StartAsync(CancellationToken.None);
        await WaitForPortAsync(fixture.DirectPort);
        return fixture;
    }

    /// <summary>从服务端配对列表移除客户端（模拟撤销）。</summary>
    public void RevokeClientPairing()
    {
        ServerPairings.Remove(ClientKeys.LoadOrCreate().KeyPair.Ed25519Public);
    }

    public async ValueTask DisposeAsync()
    {
        await Server.StopAsync();
        try { Directory.Delete(Path.GetDirectoryName(ServerDataDir)!, true); } catch { }
    }

    private static async Task WaitForPortAsync(int port, int maxMs = 3000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                await c.ConnectAsync("127.0.0.1", port);
                return;
            }
            catch { await Task.Delay(50); }
        }
    }
}
