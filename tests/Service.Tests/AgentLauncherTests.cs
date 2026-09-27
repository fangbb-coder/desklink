using DeskLink.Service.Process;
using Xunit;

namespace DeskLink.Service.Tests;

public class AgentLauncherTests
{
    private static string NonExistentPath()
        => Path.Combine(Path.GetTempPath(), $"desklink-agent-stub-{Guid.NewGuid():N}.exe");

    [Fact]
    public void Inject_Without_NoInject_Hard_Fails()
    {
        var launcher = new AgentLauncher(NonExistentPath());
        var ex = Assert.Throws<AgentInjectSafetyException>(() =>
            launcher.Start(inject: true, noInject: false, pipeOverride: null));
        Assert.Contains("--no-inject", ex.Message);
    }

    [Fact]
    public void No_Inject_Mode_Runs_In_Stub_When_Exe_Missing()
    {
        var launcher = new AgentLauncher(NonExistentPath());
        // 没有 --no-inject 但也没启用 inject；调用方意图仅"拉起 Agent 不注入"
        // → 不触发强制约束；stub 模式启动成功
        var r = launcher.Start(inject: false, noInject: true, pipeOverride: null);
        Assert.True(r.StubMode);
        Assert.Equal(0, r.Pid);
    }

    [Fact]
    public void Inject_With_NoInject_Allows_Stub_Start()
    {
        var launcher = new AgentLauncher(NonExistentPath());
        var r = launcher.Start(inject: true, noInject: true, pipeOverride: "DeskLink.Agent.test");
        Assert.True(r.StubMode);
    }

    // ────────────────────────────────────────────────────────────────────────
    // 命令行拼装（回归锚点）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// **必须带 --run**。
    ///
    /// 这条曾经缺失：代理默认是"一次性自检 + 状态上报"，不带 --run 就会跑完立刻退出。
    /// 冒烟脚本是手工带 --run 启动代理的，所以掩盖了它；真实路径
    /// （客户端点"开始控制" → start_agent → AgentLauncher）会拿到一个立刻退出的代理，
    /// 表现是"远程页一直没画面"。这里把 --run 钉死，防止再次回归。
    /// </summary>
    [Fact]
    public void BuildArguments_Always_Includes_Run()
    {
        var args = AgentLauncher.BuildArguments(inject: false, noInject: true,
            pipeOverride: null, mediaPipe: null);
        Assert.Contains("--run", args);
    }

    [Fact]
    public void BuildArguments_Includes_MediaPipe_And_NoInject()
    {
        var args = AgentLauncher.BuildArguments(inject: false, noInject: true,
            pipeOverride: "DeskLink.Agent.a", mediaPipe: "DeskLink.AgentMedia.a");

        Assert.Contains("--no-inject", args);
        Assert.Contains("DeskLink.AgentMedia.a", args);
        Assert.Contains("DeskLink.Agent.a", args);
        Assert.Equal("--media-pipe", args[args.IndexOf("DeskLink.AgentMedia.a") - 1]);
    }

    [Fact]
    public void BuildArguments_Includes_CaptureParameters()
    {
        var args = AgentLauncher.BuildArguments(inject: false, noInject: true,
            pipeOverride: null, mediaPipe: "m", monitorIndex: 2, rotation: 90, fps: 24, bitrateBps: 4_000_000);

        Assert.Equal("2", args[args.IndexOf("--monitor") + 1]);
        Assert.Equal("90", args[args.IndexOf("--rotation") + 1]);
        Assert.Equal("24", args[args.IndexOf("--fps") + 1]);
        Assert.Equal("4000000", args[args.IndexOf("--bitrate") + 1]);
    }

    [Fact]
    public void BuildArguments_Omits_Default_CaptureParameters()
    {
        var args = AgentLauncher.BuildArguments(inject: false, noInject: true,
            pipeOverride: null, mediaPipe: null, monitorIndex: 0, rotation: 0);

        // 显示器 0 与旋转 0 是默认值，没必要塞进命令行（减少日志噪声）。
        Assert.DoesNotContain("--monitor", args);
        Assert.DoesNotContain("--rotation", args);
        // 帧率/码率总是显式给出（用户可在 Settings 调，写出来便于排障）。
        Assert.Contains("--fps", args);
        Assert.Contains("--bitrate", args);
    }

    /// <summary>
    /// **每个命令行元素不得含空格**。
    ///
    /// ProcessStartInfo.ArgumentList 的每个元素就是一个 argv 项，不会再按空格切分。
    /// 早期把"开关+取值"拼成一个字符串（`"--fps 30"`），实际传给代理的是一个
    /// 无法识别的参数，代理启动即失败。这条不变式能一次性挡住这类回归。
    /// </summary>
    [Fact]
    public void BuildArguments_Never_Combines_Flag_And_Value()
    {
        var args = AgentLauncher.BuildArguments(inject: true, noInject: true,
            pipeOverride: "DeskLink.Agent.a", mediaPipe: "DeskLink.AgentMedia.a",
            monitorIndex: 1, rotation: 270, fps: 60, bitrateBps: 12_000_000);

        Assert.All(args, a => Assert.DoesNotContain(' ', a));
        // 开关与取值成对出现
        Assert.Equal("DeskLink.Agent.a", args[args.IndexOf("--pipe") + 1]);
        Assert.Equal("DeskLink.AgentMedia.a", args[args.IndexOf("--media-pipe") + 1]);
        Assert.Equal("1", args[args.IndexOf("--monitor") + 1]);
        Assert.Equal("270", args[args.IndexOf("--rotation") + 1]);
        Assert.Equal("60", args[args.IndexOf("--fps") + 1]);
        Assert.Equal("12000000", args[args.IndexOf("--bitrate") + 1]);
    }

    [Fact]
    public void BuildArguments_Inject_Mode_Adds_Inject()
    {
        var args = AgentLauncher.BuildArguments(inject: true, noInject: true,
            pipeOverride: null, mediaPipe: null);
        Assert.Contains("--inject", args);
        Assert.Contains("--no-inject", args);
    }

    [Fact]
    public void Double_Start_Returns_First()
    {
        var launcher = new AgentLauncher(NonExistentPath());
        var r1 = launcher.Start(inject: false, noInject: true, pipeOverride: null);
        var r2 = launcher.Start(inject: false, noInject: true, pipeOverride: null);
        Assert.Equal(r1.Pid, r2.Pid);
        Assert.Equal(r1.StubMode, r2.StubMode);
    }
}
