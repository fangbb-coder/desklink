using DeskLink.Panel;
using Xunit;

namespace DeskLink.Panel.Tests;

/// <summary>
/// 参数装配是面板与 Service 之间唯一的"真接口"，拼错一个 flag 不会编译报错、
/// 只会在用户点按钮时静默失效，因此这些用例的价值全在"锁住每个 flag 的位置与取值"。
/// </summary>
public class ServiceCliTests
{
    private static PanelSettings Basic() => new()
    {
        DataDir = @"C:\dl-targ",
        DirectPort = 47200,
        EnableDirect = true,
        InjectAgent = true,
        FileScopeRoots = { @"D:\Share" },
    };

    [Fact]
    public void RunArgs_始终带console与dataDir()
    {
        var args = ServiceCli.BuildRunArgs(Basic());
        Assert.Equal("--console", args[0]);
        Assert.Contains("--data-dir", args);
        Assert.Contains(@"C:\dl-targ", args);
    }

    [Fact]
    public void RunArgs_被控端带enableDirect与injectAgent()
    {
        var args = ServiceCli.BuildRunArgs(Basic());
        Assert.Contains("--enable-direct", args);
        Assert.Contains("--inject-agent", args);
    }

    [Fact]
    public void RunArgs_控制端不带enableDirect与injectAgent()
    {
        var s = Basic();
        s.EnableDirect = false;
        s.InjectAgent = false;
        var args = ServiceCli.BuildRunArgs(s);
        Assert.DoesNotContain("--enable-direct", args);
        Assert.DoesNotContain("--inject-agent", args);
    }

    [Fact]
    public void RunArgs_端口紧跟在directPort之后()
    {
        var s = Basic();
        s.DirectPort = 47500;
        var args = ServiceCli.BuildRunArgs(s);
        var i = args.ToList().IndexOf("--direct-port");
        Assert.True(i >= 0);
        Assert.Equal("47500", args[i + 1]);
    }

    [Fact]
    public void RunArgs_每个fileScopeRoots各出一个flag()
    {
        var s = Basic();
        s.FileScopeRoots = new List<string> { @"D:\A", @"E:\B" };
        var args = ServiceCli.BuildRunArgs(s).ToList();
        Assert.Equal(2, args.Count(a => a == "--file-scope"));
        Assert.Equal(@"D:\A", args[args.IndexOf("--file-scope") + 1]);
    }

    [Fact]
    public void RunArgs_空relayUrl不产出relayUrlFlag()
    {
        var s = Basic();
        s.RelayUrl = null;
        Assert.DoesNotContain("--relay-url", ServiceCli.BuildRunArgs(s));
    }

    // —— 缺陷②「多显示器没有 UI 入口」——
    // 以前面板压根拼不出 --monitor，多屏用户只能上命令行。现在：0 不传（= Service 默认的主显示器），
    // >0 显式传，且索引必须紧跟在 flag 后面。

    [Fact]
    public void RunArgs_默认不产出monitorFlag()
    {
        var s = Basic();
        s.MonitorIndex = 0;
        Assert.DoesNotContain("--monitor", ServiceCli.BuildRunArgs(s));
    }

    [Fact]
    public void RunArgs_负数索引不产出monitorFlag()
    {
        var s = Basic();
        s.MonitorIndex = -1;
        Assert.DoesNotContain("--monitor", ServiceCli.BuildRunArgs(s));
    }

    [Fact]
    public void RunArgs_非零索引成对出现()
    {
        var s = Basic();
        s.MonitorIndex = 2;
        var args = ServiceCli.BuildRunArgs(s).ToList();
        var i = args.IndexOf("--monitor");
        Assert.True(i >= 0, "非零显示器索引必须透传给 Service");
        Assert.Equal("2", args[i + 1]);
    }

    [Fact]
    public void RunArgs_多屏设置不被fileScope挤掉()
    {
        var s = Basic();
        s.FileScopeRoots = new List<string> { @"D:\A", @"E:\B" };
        s.MonitorIndex = 1;
        var args = ServiceCli.BuildRunArgs(s).ToList();
        Assert.Equal(2, args.Count(a => a == "--file-scope"));
        Assert.Equal(1, args.Count(a => a == "--monitor"));
    }

    [Fact]
    public void RunArgs_有relayUrl时成对出现()
    {
        var s = Basic();
        s.RelayUrl = "quic://vps.example.com:9443";
        var args = ServiceCli.BuildRunArgs(s).ToList();
        var i = args.IndexOf("--relay-url");
        Assert.True(i >= 0);
        Assert.Equal("quic://vps.example.com:9443", args[i + 1]);
    }

    [Fact]
    public void RunArgs_空白fileScopeRoots被跳过()
    {
        var s = Basic();
        s.FileScopeRoots = new List<string> { "   ", "" };
        Assert.DoesNotContain("--file-scope", ServiceCli.BuildRunArgs(s));
    }

    [Fact]
    public void PrintConfigArgs_带dataDir且不误开console()
    {
        var args = ServiceCli.BuildPrintConfigArgs(Basic());
        Assert.Contains("--print-config", args);
        Assert.Contains(@"C:\dl-targ", args);
        // --print-config 是打印后立刻退出的路径，带 --console 没有意义（也不致命，但不该有）
        Assert.DoesNotContain("--console", args);
    }

    [Fact]
    public void PairArgs_公钥原样透传()
    {
        var args = ServiceCli.BuildPairArgs(Basic(), "E4r9ZgkSsMYtSRqOt6unPvT57Wc6cEcEsAmpDFGeA6E=");
        var i = args.ToList().IndexOf("--pair-peer-pub");
        Assert.True(i >= 0);
        Assert.Equal("E4r9ZgkSsMYtSRqOt6unPvT57Wc6cEcEsAmpDFGeA6E=", args[i + 1]);
    }

    [Theory]
    [InlineData(47200, true, "on")]
    [InlineData(47200, false, "off")]
    [InlineData(47500, true, "on")]
    public void FirewallSetArgs_端口与开关正确(int port, bool enable, string expected)
    {
        var args = ServiceCli.BuildFirewallSetArgs(port, enable);
        Assert.Equal("--firewall-set", args[0]);
        Assert.Equal(port.ToString(), args[1]);
        Assert.Equal(expected, args[2]);
    }

    [Fact]
    public void FirewallStatusArgs_带端口且不写注册表()
    {
        var args = ServiceCli.BuildFirewallStatusArgs(47500);
        Assert.Contains("--firewall-status", args);
        Assert.Contains("47500", args);
        // 只读查询不能带 --firewall-set，否则会变成写操作
        Assert.DoesNotContain("--firewall-set", args);
    }

    [Fact]
    public void ResolveServiceExe_优先用设置里指定的路径()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var exe = Path.Combine(tmp, "DeskLink.Service.exe");
            File.WriteAllText(exe, "");
            var s = new PanelSettings { ServiceExePath = exe };
            Assert.Equal(Path.GetFullPath(exe), ServiceCli.ResolveServiceExe(s, tmp));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ResolveServiceExe_同目录下的exe可被发现()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "DeskLink.Service.exe"), "");
            Assert.NotNull(ServiceCli.ResolveServiceExe(new PanelSettings(), tmp));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }

    [Fact]
    public void ResolveServiceExe_找不到时返回null而不是抛异常()
    {
        // 空目录 + 无设置 → 面板应该显示"找不到 Service"而不是崩掉
        var tmp = Path.Combine(Path.GetTempPath(), "dl-panel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            Assert.Null(ServiceCli.ResolveServiceExe(new PanelSettings(), tmp));
        }
        finally { Directory.Delete(tmp, recursive: true); }
    }
}
