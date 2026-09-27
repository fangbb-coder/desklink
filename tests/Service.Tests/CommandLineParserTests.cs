using DeskLink.Service.Configuration;
using Xunit;

namespace DeskLink.Service.Tests;

public class CommandLineParserTests
{
    [Fact]
    public void No_Args_Returns_Default_Options()
    {
        var r = CommandLineParser.Parse(Array.Empty<string>());
        Assert.True(r.Ok);
        Assert.NotNull(r.Options);
        Assert.Equal("default", r.Options!.InstanceId);
        Assert.False(r.Options.ConsoleMode);
        Assert.False(r.Options.InjectAgent);
        Assert.Equal(47200, r.Options.DirectPort);
    }

    [Fact]
    public void Console_Mode_Flag()
    {
        var r = CommandLineParser.Parse(new[] { "--console" });
        Assert.True(r.Ok);
        Assert.True(r.Options!.ConsoleMode);
    }

    [Fact]
    public void Data_Dir_Overrides_Default_And_Derives_Instance()
    {
        var r = CommandLineParser.Parse(new[] { "--data-dir", @"C:\Temp\desklink-A" });
        Assert.True(r.Ok);
        Assert.Equal(@"C:\Temp\desklink-A", r.Options!.DataDir);
        Assert.Equal("desklink-a", r.Options.InstanceId);
    }

    [Fact]
    public void Relay_Url_Accepts_Absolute_Uri()
    {
        var r = CommandLineParser.Parse(new[] { "--relay-url", "https://relay.example.com:443" });
        Assert.True(r.Ok);
        Assert.NotNull(r.Options!.RelayUrl);
        Assert.Equal("relay.example.com", r.Options.RelayUrl!.Host);
    }

    [Fact]
    public void Direct_Port_Range_Validated()
    {
        var r1 = CommandLineParser.Parse(new[] { "--direct-port", "0" });
        Assert.False(r1.Ok);
        Assert.Equal(2, r1.ExitCode);

        var r2 = CommandLineParser.Parse(new[] { "--direct-port", "70000" });
        Assert.False(r2.Ok);

        var r3 = CommandLineParser.Parse(new[] { "--direct-port", "50000" });
        Assert.True(r3.Ok);
        Assert.Equal(50000, r3.Options!.DirectPort);
    }

    [Fact]
    public void Help_Returns_Zero_Exit_And_No_Options_Apply()
    {
        var r = CommandLineParser.Parse(new[] { "--help" });
        Assert.True(r.HelpRequested);
        Assert.Equal(0, r.ExitCode);
    }

    [Fact]
    public void Unknown_Arg_Returns_Error()
    {
        var r = CommandLineParser.Parse(new[] { "--bogus" });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void Missing_Value_Returns_Error()
    {
        var r = CommandLineParser.Parse(new[] { "--data-dir" });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void Pipe_Prefix_Override()
    {
        var r = CommandLineParser.Parse(new[] { "--pipe-prefix", "DeskLinkTest" });
        Assert.True(r.Ok);
        Assert.Equal("DeskLinkTest", r.Options!.PipeNamePrefix);
    }

    [Fact]
    public void Enable_Direct_Flag_Defaults_Off_And_Turns_On()
    {
        var off = CommandLineParser.Parse(Array.Empty<string>());
        Assert.True(off.Ok);
        Assert.False(off.Options!.EnableDirect);

        var on = CommandLineParser.Parse(new[] { "--enable-direct" });
        Assert.True(on.Ok);
        Assert.True(on.Options!.EnableDirect);
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    [InlineData("enable", true)]
    [InlineData("disable", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("ON", true)]
    public void Firewall_Set_ParsesPortAndValue(string value, bool expected)
    {
        var r = CommandLineParser.Parse(new[] { "--firewall-set", "47200", value });
        Assert.True(r.Ok, r.Error);
        Assert.NotNull(r.Options!.FirewallSet);
        Assert.Equal(47200, r.Options.FirewallSet!.Value.Port);
        Assert.Equal(expected, r.Options.FirewallSet!.Value.Enable);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("maybe")]
    [InlineData("")]
    public void Firewall_Set_RejectsUnknownValue(string value)
    {
        var r = CommandLineParser.Parse(new[] { "--firewall-set", "47200", value });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public void Firewall_Set_RejectsInvalidPort(string port)
    {
        var r = CommandLineParser.Parse(new[] { "--firewall-set", port, "on" });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void Firewall_Set_RequiresBothArguments()
    {
        var r = CommandLineParser.Parse(new[] { "--firewall-set", "47200" });
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
    }

    [Fact]
    public void Firewall_Repair_ParsesPort()
    {
        var r = CommandLineParser.Parse(new[] { "--firewall-repair", "47200" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(47200, r.Options!.FirewallRepair);

        var bad = CommandLineParser.Parse(new[] { "--firewall-repair", "70000" });
        Assert.False(bad.Ok);

        var missing = CommandLineParser.Parse(new[] { "--firewall-repair" });
        Assert.False(missing.Ok);
    }

    [Fact]
    public void FileScope_ParsesRepeatableAndNormalisesToAbsolute()
    {
        var r = CommandLineParser.Parse(new[]
        {
            "--file-scope", @"C://data//a",
            "--file-scope", @"C://data//b",
            "--file-scope", @"C://data//a",   // 重复应被去掉
        });
        Assert.True(r.Ok, r.Error);
        Assert.Equal(2, r.Options!.FileScopeRoots.Count);
        Assert.All(r.Options.FileScopeRoots, d => Assert.True(Path.IsPathRooted(d)));
    }

    [Fact]
    public void FileScope_DefaultsToEmpty_WhichMeansDenyAll()
    {
        var r = CommandLineParser.Parse(Array.Empty<string>());
        Assert.True(r.Ok);
        Assert.Empty(r.Options!.FileScopeRoots);
    }

    [Fact]
    public void Enable_Direct_Appears_In_Help()
    {
        Assert.Contains("--enable-direct", CommandLineParser.HelpText);
    }
}
