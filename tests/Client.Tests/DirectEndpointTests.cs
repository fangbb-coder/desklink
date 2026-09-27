using DeskLink.Client.Services;
using Xunit;

namespace DeskLink.Client.Tests;

/// <summary><c>IP:端口</c> 解析表（局域网直连输入）。</summary>
public class DirectEndpointTests
{
    [Theory]
    [InlineData("192.168.1.20:47200", "192.168.1.20", 47200)]
    [InlineData("10.0.0.1:1", "10.0.0.1", 1)]
    [InlineData("10.0.0.1:65535", "10.0.0.1", 65535)]
    [InlineData("relay.example.com:443", "relay.example.com", 443)]
    [InlineData("localhost:47200", "localhost", 47200)]
    [InlineData(" 192.168.1.20:47200 ", "192.168.1.20", 47200)] // 两端空白被裁剪
    [InlineData("[::1]:47200", "::1", 47200)]
    [InlineData("[fe80::1]:80", "fe80::1", 80)]
    public void Valid_Inputs_Parse(string input, string expectedHost, int expectedPort)
    {
        Assert.True(DirectEndpoint.TryParse(input, out var ep, out var error), error);
        Assert.Equal(expectedHost, ep.Host);
        Assert.Equal(expectedPort, ep.Port);
    }

    [Theory]
    [InlineData("", "空")]                    // 空串
    [InlineData("   ", "空白")]               // 纯空白
    [InlineData("192.168.1.20", "缺端口")]
    [InlineData("192.168.1.20:", "端口为空")]
    [InlineData("192.168.1.20:abc", "端口非数字")]
    [InlineData("192.168.1.20:0", "端口 0")]
    [InlineData("192.168.1.20:65536", "端口越界")]
    [InlineData(":47200", "主机为空")]
    [InlineData("[::1:47200", "IPv6 缺右括号")]
    [InlineData("[::1]47200", "IPv6 缺冒号")]
    public void Invalid_Inputs_Are_Rejected(string input, string why)
    {
        Assert.False(DirectEndpoint.TryParse(input, out _, out var error), $"应拒绝：{why}");
        Assert.False(string.IsNullOrWhiteSpace(error), "拒绝时必须给出可读原因");
    }

    [Fact]
    public void Parse_Throws_With_Reason()
    {
        var ex = Assert.Throws<FormatException>(() => DirectEndpoint.Parse("nope"));
        Assert.Contains("端口", ex.Message);
    }

    [Fact]
    public void ToString_RoundTrips_And_Brackets_Ipv6()
    {
        var ipv4 = DirectEndpoint.Parse("192.168.1.20:47200");
        Assert.Equal("192.168.1.20:47200", ipv4.ToString());

        var ipv6 = DirectEndpoint.Parse("[::1]:47200");
        Assert.Equal("[::1]:47200", ipv6.ToString());
        Assert.True(DirectEndpoint.TryParse(ipv6.ToString(), out var again, out _));
        Assert.Equal(ipv6, again);
    }
}
