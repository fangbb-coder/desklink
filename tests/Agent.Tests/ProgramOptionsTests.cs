using DeskLink.DesktopAgent;

namespace DeskLink.Agent.Tests;

/// <summary>
/// 命令行解析与**输入环路守卫**。
///
/// 守卫是 P7 的安全红线（DESIGN 风险回顾 #4）：自控自场景下若允许真实注入，
/// 用户的操作会通过网络回到自己机器形成回环，等于输入被无限放大。
/// 所以这条规则必须有单测钉住，而不是只靠手工跑一次看退出码。
/// </summary>
public class ProgramOptionsTests
{
    [Fact]
    public void InjectionGuard_RejectsInjectWithoutNoInject()
    {
        var options = Program.AgentOptions.Parse(new[] { "--inject" });

        Assert.True(options.Inject);
        Assert.False(options.NoInject);

        Assert.True(Program.IsInjectionGuardViolation(options, out string message));
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public void InjectionGuard_AllowsInjectWhenNoInjectAlsoGiven()
    {
        // 安全优先：两个都给时以 --no-inject 为准，属于合法组合。
        var options = Program.AgentOptions.Parse(new[] { "--inject", "--no-inject" });

        Assert.True(options.Inject);
        Assert.True(options.NoInject);
        Assert.False(Program.IsInjectionGuardViolation(options, out _));
    }

    [Fact]
    public void InjectionGuard_AllowsPlainNoInjectAndPlainStatusReporting()
    {
        Assert.False(Program.IsInjectionGuardViolation(
            Program.AgentOptions.Parse(new[] { "--no-inject" }), out _));
        Assert.False(Program.IsInjectionGuardViolation(
            Program.AgentOptions.Parse(Array.Empty<string>()), out _));
    }

    [Fact]
    public void Parse_Defaults_AreStable()
    {
        var options = Program.AgentOptions.Parse(Array.Empty<string>());

        Assert.Equal(Program.DefaultPipeName, options.PipeName);
        Assert.Equal(320, options.PatternWidth);
        Assert.Equal(240, options.PatternHeight);
        Assert.Equal(0, options.MonitorIndex);
        Assert.False(options.TestPattern);
        Assert.True(options.PreferHardware);
        Assert.False(options.ShowHelp);
    }

    [Theory]
    [InlineData("--pipe", "custom.pipe", "custom.pipe")]
    [InlineData("--pipe=custom.pipe", null, "custom.pipe")]
    public void Parse_PipeName_SupportsBothForms(string first, string? second, string expected)
    {
        var args = second is null ? new[] { first } : new[] { first, second };
        Assert.Equal(expected, Program.AgentOptions.Parse(args).PipeName);
    }

    [Theory]
    [InlineData("--pattern-size 640x480", 640, 480)]
    [InlineData("--pattern-size=1920X1080", 1920, 1080)]
    public void Parse_PatternSize_SupportsBothSeparatorCases(string commandLine, int width, int height)
    {
        var options = Program.AgentOptions.Parse(commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(width, options.PatternWidth);
        Assert.Equal(height, options.PatternHeight);
    }

    [Fact]
    public void Parse_NoHardwareEncoder_DisablesPreferHardware()
    {
        Assert.False(Program.AgentOptions.Parse(new[] { "--no-hardware-encoder" }).PreferHardware);
    }

    [Fact]
    public void Parse_HelpAndListMonitors_AreRecognised()
    {
        Assert.True(Program.AgentOptions.Parse(new[] { "--help" }).ShowHelp);
        Assert.True(Program.AgentOptions.Parse(new[] { "-h" }).ShowHelp);
        Assert.True(Program.AgentOptions.Parse(new[] { "--list-monitors" }).ListMonitors);
    }

    // 注意：特性实参必须是编译期常量，不能写 new[]{...}，因此这里用空格分隔的字符串，
    // 在测试体内再切分。字符串里的空白仅作分隔用途。
    [Theory]
    [InlineData("--bogus")]
    [InlineData("--pattern-size 640")]
    [InlineData("--pattern-size 0x0")]
    [InlineData("--pattern-size -4x10")]
    [InlineData("--monitor -1")]
    [InlineData("--pipe")]
    public void Parse_InvalidArguments_ThrowArgumentException(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Throws<ArgumentException>(() => Program.AgentOptions.Parse(args));
    }
}
