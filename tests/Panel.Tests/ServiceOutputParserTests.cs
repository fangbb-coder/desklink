using DeskLink.Panel.Services;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>
/// 解析层是"按钮点了没反应"类 bug 的头号来源：Service 的输出格式只存在于
/// 它的实现里，面板只能靠解析去接。这些用例把每种输出形态都钉住。
/// </summary>
public class ServiceOutputParserTests
{
    private const string SamplePrintConfig = """
        ServiceOptions { DataDir=C:\dl-targ, InstanceId=dl-targ, Console=False, ... }
        KeyStore: C:\dl-targ\keystore.json
          ed25519_pub_b64 = E4r9ZgkSsMYtSRqOt6unPvT57Wc6cEcEsAmpDFGeA6E=
          x25519_pub_b64  = AbCdEf0123456789
          device_id_hint  = Zm9vYmFy
          device_id       = 63C15CDE6980F6FA35A4EFC019FCA6F6C74AE3DEBDAB98C85267FE9973A8518F
          quic_available  = true
        """;

    [Fact]
    public void 解析printConfig输出()
    {
        var id = ServiceOutputParser.ParseIdentity(SamplePrintConfig);
        Assert.True(id.IsValid);
        Assert.Equal("E4r9ZgkSsMYtSRqOt6unPvT57Wc6cEcEsAmpDFGeA6E=", id.Ed25519PubB64);
        Assert.Equal("63C15CDE6980F6FA35A4EFC019FCA6F6C74AE3DEBDAB98C85267FE9973A8518F", id.DeviceIdHex);
        Assert.True(id.QuicAvailable);
    }

    [Fact]
    public void 空输出解析为无效身份而不是抛异常()
    {
        // 关键行为：拿不到公钥时必须 IsValid=false，让界面提示，而不是拿空串去配对
        var id = ServiceOutputParser.ParseIdentity(null);
        Assert.False(id.IsValid);
        Assert.Equal("", id.Ed25519PubB64);
    }

    [Fact]
    public void 缺公钥字段时判定为无效()
    {
        var id = ServiceOutputParser.ParseIdentity("ServiceOptions { DataDir=x }\n  device_id = ABC\n");
        Assert.False(id.IsValid);
        Assert.Equal("ABC", id.DeviceIdHex);
    }

    [Fact]
    public void KeyStore行用冒号分隔也能解析()
    {
        var id = ServiceOutputParser.ParseIdentity(SamplePrintConfig);
        Assert.Equal(@"C:\dl-targ\keystore.json", id.KeyStorePath);
    }

    [Fact]
    public void 短公钥取全部做缩写()
    {
        var id = ServiceOutputParser.ParseIdentity("  ed25519_pub_b64 = ABCD\n");
        Assert.Equal("ABCD", id.ShortPub);
    }

    [Fact]
    public void 长公钥缩写取前8位()
    {
        var id = ServiceOutputParser.ParseIdentity("  ed25519_pub_b64 = 0123456789ABCDEF\n");
        Assert.Equal("01234567", id.ShortPub);
    }

    [Fact]
    public void 解析防火墙状态JSON()
    {
        const string json = """
            {"direct_port":47200,"direct_enabled":true,"elevated":true,
             "netsh_rules_implemented":true,"rule_tcp_present":true,"rule_udp_present":true,
             "rule_tcp_port":47200,"rule_udp_port":47200}
            """;
        var fw = ServiceOutputParser.ParseFirewallStatus(json);
        Assert.NotNull(fw);
        Assert.Equal(47200, fw!.DirectPort);
        Assert.True(fw.DirectEnabled);
        Assert.True(fw.FullyOpen);
    }

    [Fact]
    public void 防火墙JSON前后夹日志行仍能解析()
    {
        var text = "some log line\n{\"direct_port\":1,\"direct_enabled\":false,\"rule_tcp_present\":false,\"rule_udp_present\":false}\ntail";
        var fw = ServiceOutputParser.ParseFirewallStatus(text);
        Assert.NotNull(fw);
        Assert.False(fw!.FullyOpen);
    }

    [Fact]
    public void 缺TCP规则时FullyOpen为假()
    {
        var fw = ServiceOutputParser.ParseFirewallStatus(
            """{"direct_port":47200,"direct_enabled":true,"rule_tcp_present":true,"rule_udp_present":false}""");
        Assert.NotNull(fw);
        Assert.False(fw!.FullyOpen);
    }

    [Fact]
    public void 非JSON输入返回null而不是当成已放行()
    {
        // 解析失败必须返回 null；绝不能默认 false 之后被上层当成"已放行"或"没放行"而误报
        Assert.Null(ServiceOutputParser.ParseFirewallStatus("error: 需要管理员权限"));
        Assert.Null(ServiceOutputParser.ParseFirewallStatus(""));
        Assert.Null(ServiceOutputParser.ParseFirewallStatus(null));
    }
}
