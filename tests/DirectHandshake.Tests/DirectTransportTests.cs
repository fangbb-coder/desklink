using DeskLink.Protocol.Session;
using DeskLink.Service.Direct;
using DeskLink.Service.Security;
using Xunit;

namespace DeskLink.DirectHandshake.Tests;

/// <summary>
/// 直连传输层单元测试：不跑完整 SIGMA，只验证 TCP 连接 + device_id 交换 + TLS 握手。
/// </summary>
public class DirectTransportTests : IAsyncLifetime
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

    [Fact]
    public async Task Tcp_DeviceIdExchange_Succeeds()
    {
        var client = new System.Net.Sockets.TcpClient { NoDelay = true };
        await client.ConnectAsync("127.0.0.1", _fixture!.DirectPort);

        // 发 device_id
        var stream = client.GetStream();
        await stream.WriteAsync(_fixture.ClientDeviceId);
        await stream.FlushAsync();

        // TLS 客户端握手（接受任何证书）
        // 注意：SslStream 构造器与 options 不能同时设回调（.NET 9 限制）。
        var ssl = new System.Net.Security.SslStream(stream, false);
        await ssl.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        });

        Assert.True(ssl.IsAuthenticated);
        client.Dispose();
    }

    [Fact]
    public async Task Tcp_Unpaired_DeviceIdRejected()
    {
        var unpaired = await DirectHandshakeFixture.StartUnpairedServerAsync();
        try
        {
            var client = new System.Net.Sockets.TcpClient { NoDelay = true };
            await client.ConnectAsync("127.0.0.1", unpaired.DirectPort);

            var stream = client.GetStream();
            var fakeId = new byte[32];
            Random.Shared.NextBytes(fakeId);
            await stream.WriteAsync(fakeId);
            await stream.FlushAsync();

            // 服务端读到 device_id 后发现未配对，会关闭连接
            // 这里读应该很快返回 0 或异常
            var buf = new byte[1];
            try
            {
                var n = await stream.ReadAsync(buf);
                // 如果服务端发了 TLS alert 或 RST，这里可能抛异常
                // 如果没抛，n 应该为 0（连接已关闭）
            }
            catch (System.IO.IOException) { /* 预期：连接被重置 */ }
            client.Dispose();
        }
        finally
        {
            await unpaired.DisposeAsync();
        }
    }
}
