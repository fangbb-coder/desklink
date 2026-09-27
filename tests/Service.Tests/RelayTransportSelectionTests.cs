// relayUrl scheme → 传输类型的决策表测试。
//
// 背景：RelayClient 早期实现是"QUIC 不可用就静默换成 TcpTlsTransport"。
// 但 TcpTlsTransport 只接受 tls:// / https://，于是 quic:// 在 Win10 上会以
// "expects tls:// or https://" 失败——报错与根因（QUIC 不可用）完全无关，
// 还会被退避循环吞掉，排查成本很高。
//
// 现在显式 scheme 必须给出显式结论：quic:// 不可用时抛错，不静默降级。
// 本文件锁定整张决策表。
using DeskLink.Service.Relay;
using Xunit;

namespace DeskLink.Service.Tests;

public class RelayTransportSelectionTests
{
    private static Uri U(string s) => new(s);

    [Fact]
    public void Quic_Scheme_WithQuicAvailable_SelectsQuic()
    {
        Assert.Equal(
            RelayTransportKind.Quic,
            RelayClient.SelectTransportKind(U("quic://127.0.0.1:9443"), quicAvailable: true));
    }

    [Fact]
    public void Quic_Scheme_WithoutQuic_ThrowsInsteadOfSilentFallback()
    {
        // 关键断言：必须抛错，且错误信息要能指导用户改 scheme。
        var ex = Assert.Throws<NotSupportedException>(
            () => RelayClient.SelectTransportKind(U("quic://127.0.0.1:9443"), quicAvailable: false));
        Assert.Contains("quic://", ex.Message);
        Assert.Contains("tls://", ex.Message);
    }

    [Fact]
    public void Tls_Scheme_AlwaysSelectsTcpTls()
    {
        Assert.Equal(
            RelayTransportKind.TcpTls,
            RelayClient.SelectTransportKind(U("tls://127.0.0.1:9443"), quicAvailable: true));
        Assert.Equal(
            RelayTransportKind.TcpTls,
            RelayClient.SelectTransportKind(U("tls://127.0.0.1:9443"), quicAvailable: false));
    }

    [Fact]
    public void Https_Scheme_PrefersQuicAndFallsBackToTcpTls()
    {
        // https:// 是"未显式指定协议"的写法：QUIC 优先，不可用回落。
        Assert.Equal(
            RelayTransportKind.Quic,
            RelayClient.SelectTransportKind(U("https://relay.example.com:443"), quicAvailable: true));
        Assert.Equal(
            RelayTransportKind.TcpTls,
            RelayClient.SelectTransportKind(U("https://relay.example.com:443"), quicAvailable: false));
    }

    [Fact]
    public void Unknown_Scheme_Throws()
    {
        var ex = Assert.Throws<NotSupportedException>(
            () => RelayClient.SelectTransportKind(U("http://127.0.0.1:9443"), quicAvailable: true));
        Assert.Contains("http", ex.Message);
    }

    [Fact]
    public void Null_Relay_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RelayClient.SelectTransportKind(null!, quicAvailable: true));
    }
}
